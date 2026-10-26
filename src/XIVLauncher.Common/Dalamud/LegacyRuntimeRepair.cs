using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace XIVLauncher.Common.Dalamud;

/// <summary>利用者の操作で取得する、Windows x64 用の公式 .NET Desktop Runtime。</summary>
public sealed class LegacyRuntimeRepair(HttpClient client)
{
    public sealed record Package(string Version, Uri Url, string Sha512);

    public async Task<Package> GetPackage(string channel, CancellationToken cancellationToken = default)
    {
        if (channel is not ("6.0" or "8.0" or "9.0"))
            throw new ArgumentException("対応していない .NET 系列です。", nameof(channel));

        var json = await client.GetStringAsync($"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json", cancellationToken).ConfigureAwait(false);
        return ParsePackage(channel, json);
    }

    public static Package ParsePackage(string channel, string json)
    {
        var metadata = JObject.Parse(json);
        var latest = (string?)metadata["latest-runtime"];
        if (!Version.TryParse(latest, out var version) || $"{version.Major}.{version.Minor}" != channel)
            throw new InvalidDataException("公式配布情報のバージョンを確認できませんでした。");

        var release = metadata["releases"]?.Children().FirstOrDefault(x => (string?)x["runtime"]?["version"] == latest);
        if ((string?)release?["windowsdesktop"]?["version"] != latest)
            throw new InvalidDataException("Desktop Runtime のバージョンが一致しません。");

        var file = release?["windowsdesktop"]?["files"]?.Children().SingleOrDefault(x =>
            (string?)x["rid"] == "win-x64" && ((string?)x["url"])?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
        var hash = (string?)file?["hash"];
        if (!Uri.TryCreate((string?)file?["url"], UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo)
            || url.Host is not ("builds.dotnet.microsoft.com" or "download.visualstudio.microsoft.com" or "dotnetcli.azureedge.net")
            || hash == null || hash.Length != 128 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("公式インストーラーの取得先または検証情報が不正です。");

        return new Package(latest!, url, hash);
    }

    public static string[] GetInstalledVersions(string root, string framework, string channel)
    {
        var directory = Path.Combine(root, "shared", framework);
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        return Directory.EnumerateDirectories(directory).Select(Path.GetFileName)
            .Where(x => Version.TryParse(x, out var version) && $"{version.Major}.{version.Minor}" == channel
                && File.Exists(Path.Combine(directory, x!, framework == "Microsoft.NETCore.App" ? "coreclr.dll" : "PresentationFramework.dll")))
            .OrderByDescending(x => Version.Parse(x!)).Select(x => x!).ToArray();
    }

    public static async Task<bool> VerifyFile(string path, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> Download(Package package, string destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        var path = Path.Combine(destination, $"windowsdesktop-runtime-{package.Version}-win-x64.exe");
        if (await VerifyFile(path, package.Sha512, cancellationToken).ConfigureAwait(false))
            return path;

        var partial = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    total += count;
                    if (response.Content.Headers.ContentLength is > 0)
                        progress?.Report((double)total / response.Content.Headers.ContentLength.Value * 100);
                }
            }

            if (!await VerifyFile(partial, package.Sha512, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("取得したファイルの検証に失敗しました。インストーラーは起動しません。");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            // 今回作った未検証ファイルだけを削除する。既存のランタイムには触れない。
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }
}
