using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XIVLauncher.Common.Dalamud;

namespace XIVLauncher.Common.Tests;

[TestClass]
public class LegacyRuntimeRepairTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("検証用インストーラー。実行しない。");
    private static readonly string Hash = Convert.ToHexString(SHA512.HashData(Installer));

    private static string Metadata(string url = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/6.0.36/runtime.exe", string? hash = null, string desktopVersion = "6.0.36")
        => new JObject
        {
            ["latest-runtime"] = "6.0.36",
            ["releases"] = JArray.FromObject(new[]
            {
                new { runtime = new { version = "6.0.36" }, windowsdesktop = new { version = desktopVersion, files = new[] { new { rid = "win-x64", url, hash = hash ?? Hash } } } },
            }),
        }.ToString(Formatting.None);

    [TestMethod]
    public async Task ResolvesAndVerifiesOfficialX64DesktopInstaller()
    {
        using var fixture = new Fixture();
        var package = await fixture.Service.GetPackage("6.0");
        Assert.AreEqual("6.0.36", package.Version);
        var path = await fixture.Service.Download(package, fixture.Root);
        CollectionAssert.AreEqual(Installer, File.ReadAllBytes(path));
        Assert.AreEqual(1, fixture.DownloadCount);
        await fixture.Service.Download(package, fixture.Root);
        Assert.AreEqual(1, fixture.DownloadCount);
    }

    [TestMethod]
    public void RefusesUntrustedHostInvalidHashAndMismatchedVersion()
    {
        Assert.ThrowsException<InvalidDataException>(() => LegacyRuntimeRepair.ParsePackage("6.0", Metadata(url: "https://example.com/runtime.exe")));
        Assert.ThrowsException<InvalidDataException>(() => LegacyRuntimeRepair.ParsePackage("6.0", Metadata(hash: "bad")));
        Assert.ThrowsException<InvalidDataException>(() => LegacyRuntimeRepair.ParsePackage("6.0", Metadata(desktopVersion: "8.0.0")));
        Assert.ThrowsException<InvalidDataException>(() => LegacyRuntimeRepair.ParsePackage("8.0", Metadata()));
    }

    [TestMethod]
    public async Task CorruptDownloadDoesNotReplaceExistingInstallerOrRuntime()
    {
        using var fixture = new Fixture { Corrupt = true };
        var package = await fixture.Service.GetPackage("6.0");
        var path = Path.Combine(fixture.Root, "windowsdesktop-runtime-6.0.36-win-x64.exe");
        File.WriteAllText(path, "既存のファイル");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Service.Download(package, fixture.Root));
        Assert.AreEqual("既存のファイル", File.ReadAllText(path));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.download").Length);
    }

    [TestMethod]
    public void DoesNotConfuseNewerMajorOrEmptyDirectoryWithInstalledFramework()
    {
        using var fixture = new Fixture();
        var six = Path.Combine(fixture.Root, "shared", "Microsoft.NETCore.App", "6.0.36");
        var ten = Path.Combine(fixture.Root, "shared", "Microsoft.NETCore.App", "10.0.14");
        Directory.CreateDirectory(six);
        Directory.CreateDirectory(ten);
        File.WriteAllText(Path.Combine(ten, "coreclr.dll"), "10");
        Assert.AreEqual(0, LegacyRuntimeRepair.GetInstalledVersions(fixture.Root, "Microsoft.NETCore.App", "6.0").Length);
        File.WriteAllText(Path.Combine(six, "coreclr.dll"), "6");
        CollectionAssert.AreEqual(new[] { "6.0.36" }, LegacyRuntimeRepair.GetInstalledVersions(fixture.Root, "Microsoft.NETCore.App", "6.0"));
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient client;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "estell-legacy-test-" + Guid.NewGuid().ToString("N"));
        public LegacyRuntimeRepair Service { get; }
        public bool Corrupt { get; init; }
        public int DownloadCount { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            client = new HttpClient(this, disposeHandler: false);
            Service = new LegacyRuntimeRepair(client);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("releases.json"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Metadata()) });
            DownloadCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? new byte[] { 0 } : Installer) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(Root).StartsWith("estell-legacy-test-"))
                    throw new InvalidOperationException("検証用の一時フォルダ以外は削除できません。");
                Directory.Delete(Root, recursive: true);
            }
            base.Dispose(disposing);
        }
    }
}
