using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using Serilog;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud
{
    public class AssetManager
    {
        private const string ASSET_STORE_URL = "https://kamori.goats.dev/Dalamud/Asset/Meta";

        internal class AssetInfo
        {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("assets")]
            public IReadOnlyList<Asset> Assets { get; set; }

            [JsonPropertyName("packageUrl")]
            public string PackageUrl { get; set; }

            [JsonPropertyName("originalPackageUrl")]
            public string? OriginalPackageUrl { get; set; }

            public class Asset
            {
                [JsonPropertyName("url")]
                public string Url { get; set; }

                [JsonPropertyName("fileName")]
                public string FileName { get; set; }

                [JsonPropertyName("hash")]
                public string Hash { get; set; }
            }
        }

        public static async Task<(DirectoryInfo AssetDir, int Version)> EnsureAssets(HttpClient client, DalamudUpdater updater, DirectoryInfo baseDir)
        {
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
            };

            Log.Verbose("[DASSET] Starting asset download");

            var (isRefreshNeeded, info) = await CheckAssetRefreshNeeded(client, baseDir);

            var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, info.Version.ToString()));
            var devDir = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));

            if (!isRefreshNeeded)
                isRefreshNeeded = !CheckAssets(currentDir, info);

            if (isRefreshNeeded)
            {
                var staging = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "estell-assets-" + Guid.NewGuid().ToString("N")));
                staging.Create();
                try
                {
                    var official = info.OriginalPackageUrl ?? info.PackageUrl;
                    var sources = (DistributionConfig.HasMirror ? new[] { DistributionConfig.MirrorAssetPackageUrl(info.Version), official } : new[] { official }).Distinct().ToArray();
                    DirectoryInfo? verified = null;
                    for (var index = 0; index < sources.Length; index++)
                    {
                        var extracted = staging.CreateSubdirectory(index.ToString());
                        try
                        {
                            var archive = Path.Combine(staging.FullName, "package.zip");
                            await updater.DownloadDependencyFile(sources[index], archive).ConfigureAwait(false);
                            ZipFile.ExtractToDirectory(archive, extracted.FullName);
                            if (!CheckAssets(extracted, info))
                                throw new InvalidDataException("取得したアセットが公式ハッシュと一致しません。");
                            verified = extracted;
                            break;
                        }
                        catch (Exception ex) when (index + 1 < sources.Length)
                        {
                            Log.Warning(ex, "[DASSET] Asset mirror failed verification or download; trying official source");
                        }
                    }
                    // 検証済みの取得物がそろってから、既存の対象版を入れ直す。
                    PlatformHelpers.DeleteAndRecreateDirectory(currentDir);
                    PlatformHelpers.CopyFilesRecursively(verified!, currentDir);
                    if (!CheckAssets(currentDir, info))
                        throw new InvalidDataException("配置後のアセット検証に失敗しました。");
                }
                finally
                {
                    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (staging.FullName.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                        staging.Delete(recursive: true);
                }

                try
                {
                    PlatformHelpers.DeleteAndRecreateDirectory(devDir);
                    PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DASSET] Could not copy to dev dir");
                }

            }

            if (isRefreshNeeded)
                SetLocalAssetVer(baseDir, info.Version);

            Log.Verbose("[DASSET] Assets OK at {0}", currentDir.FullName);

            try
            {
                CleanUpOld(baseDir, devDir, currentDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET] Could not clean up old assets");
            }

            return (currentDir, info.Version);
        }

        private static string GetAssetVerPath(DirectoryInfo baseDir)
        {
            return Path.Combine(baseDir.FullName, "asset.ver");
        }

        private static bool CheckAssets(DirectoryInfo directory, AssetInfo info)
        {
            try
            {
                var prefix = directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var entry in info.Assets)
                {
                    var path = Path.GetFullPath(Path.Combine(directory.FullName, entry.FileName));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                        return false;
                    if (string.IsNullOrEmpty(entry.Hash))
                        continue;
                    using var file = File.OpenRead(path);
                    if (!string.Equals(Convert.ToHexString(SHA1.HashData(file)), entry.Hash, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DASSET] Asset verification failed");
                return false;
            }
        }

        /// <summary>
        ///     ミラー、公式の順に一覧を確認する。導入済みより古い一覧は採用しない。
        /// </summary>
        /// <param name="client"><see cref="HttpClient"/> to use for download</param>
        /// <param name="baseDir">Base directory for assets</param>
        /// <returns>Update state</returns>
        private static async Task<(bool isRefreshNeeded, AssetInfo info)> CheckAssetRefreshNeeded(HttpClient client, DirectoryInfo baseDir)
        {
            var localVerFile = GetAssetVerPath(baseDir);
            var localVer = 0;

            try
            {
                if (File.Exists(localVerFile))
                    localVer = int.Parse(File.ReadAllText(localVerFile));
            }
            catch (Exception ex)
            {
                // This means it'll stay on 0, which will redownload all assets - good by me
                Log.Error(ex, "[DASSET] Could not read asset.ver");
            }

            var metadataSources = DistributionConfig.HasMirror ? new[] { DistributionConfig.MirrorAssetMetaUrl, ASSET_STORE_URL } : new[] { ASSET_STORE_URL };
            var remoteVer = await DependencySource.ReadJson(client, metadataSources, json =>
            {
                var info = JsonSerializer.Deserialize<AssetInfo>(json);
                if (info == null || info.Version <= 0 || info.Version < localVer || info.Assets is not { Count: > 0 }
                    || !Uri.TryCreate(info.PackageUrl, UriKind.Absolute, out var url) || url.Scheme != "https"
                    || info.Assets.Any(entry => string.IsNullOrWhiteSpace(entry.FileName)
                        || (!string.IsNullOrEmpty(entry.Hash) && (entry.Hash.Length != 40 || !entry.Hash.All(Uri.IsHexDigit)))))
                    throw new InvalidDataException("アセット一覧が不正、または導入済みより古い版です。");
                if (DistributionConfig.HasMirror && info.PackageUrl.StartsWith(DistributionConfig.MirrorBase, StringComparison.OrdinalIgnoreCase)
                    && (!Uri.TryCreate(info.OriginalPackageUrl, UriKind.Absolute, out var original) || original.Scheme != "https"))
                    throw new InvalidDataException("ミラーのアセット一覧に公式の代替取得先がありません。");
                return info;
            }).ConfigureAwait(false);

            Log.Verbose("[DASSET] Ver check - local:{0} remote:{1}", localVer, remoteVer.Version);

            var needsUpdate = remoteVer.Version > localVer;

            return (needsUpdate, remoteVer);
        }

        private static void SetLocalAssetVer(DirectoryInfo baseDir, int version)
        {
            try
            {
                var localVerFile = GetAssetVerPath(baseDir);
                File.WriteAllText(localVerFile, version.ToString());
            }
            catch (Exception e)
            {
                Log.Error(e, "[DASSET] Could not write local asset version");
            }
        }

        private static void CleanUpOld(DirectoryInfo baseDir, DirectoryInfo devDir, DirectoryInfo currentDir)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!baseDir.Exists)
                return;

            foreach (var toDelete in baseDir.GetDirectories())
            {
                if (toDelete.Name != devDir.Name && toDelete.Name != currentDir.Name)
                {
                    toDelete.Delete(true);
                    Log.Verbose("[DASSET] Cleaned out {Path}", toDelete.FullName);
                }
            }

            Log.Verbose("[DASSET] Finished cleaning");
        }
    }
}
