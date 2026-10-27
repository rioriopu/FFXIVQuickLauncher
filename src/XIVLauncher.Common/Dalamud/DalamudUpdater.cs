using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud
{
    public class DalamudUpdater
    {
        private readonly DirectoryInfo addonDirectory;
        private readonly DirectoryInfo assetRootDirectory;
        private readonly IUniqueIdCache? cache;
        private readonly HttpClient client;

        public DownloadState State { get; private set; } = DownloadState.Unknown;
        public bool IsStaging { get; private set; } = false;

        public Exception? EnsurementException { get; private set; }

        private FileInfo? runnerInternal;

        public FileInfo Runner
        {
            get
            {
                if (RunnerOverride != null)
                    return RunnerOverride;

                return runnerInternal ?? throw new InvalidOperationException("Runner not prepared yet");
            }
            private set => runnerInternal = value;
        }

        public DirectoryInfo Runtime { get; }

        public FileInfo? RunnerOverride { get; set; }

        public DirectoryInfo? AssetDirectory { get; private set; }

        public IDalamudLoadingOverlay? Overlay { get; set; }

        public string? RolloutBucket { get; }

        public event Action<DalamudVersionInfo?>? ResolvedBranchChanged;

        public DalamudVersionInfo? ResolvedBranch
        {
            get;
            private set
            {
                if (field == value)
                    return;

                field = value;

                try
                {
                    ResolvedBranchChanged?.Invoke(field);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public enum DownloadState
        {
            Unknown,
            Running,
            Done,
            NoIntegrity, // fail with error message
        }

        public DalamudUpdater(HttpClient client, DirectoryInfo addonDirectory, DirectoryInfo runtimeDirectory, DirectoryInfo assetRootDirectory, IUniqueIdCache? cache, string? dalamudRolloutBucket)
        {
            this.addonDirectory = addonDirectory;
            this.assetRootDirectory = assetRootDirectory;
            this.client = client;

            this.Runtime = runtimeDirectory;
            this.AssetDirectory = null;
            this.cache = cache;

            this.RolloutBucket = dalamudRolloutBucket;

            if (this.RolloutBucket == null)
            {
                var rng = new Random();
                this.RolloutBucket = rng.Next(0, 9) >= 7 ? "Canary" : "Control";
            }
        }

        public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress)
        {
            Overlay!.SetStep(progress);
        }

        public void ShowOverlay()
        {
            Overlay!.SetVisible();
        }

        public void CloseOverlay()
        {
            Overlay!.SetInvisible();
        }

        private void ReportOverlayProgress(long? size, long downloaded, double? progress)
        {
            Overlay!.ReportProgress(size, downloaded, progress);
        }

        public void Run(string? betaKind, string? betaKey)
        {
            Log.Information("[DUPDATE] Starting...");
            this.State = DownloadState.Running;

            this.ResolvedBranch = null;

            Task.Run(async () =>
            {
                const int MAX_TRIES = 10;

                var isUpdated = false;

                for (var tries = 0; tries < MAX_TRIES; tries++)
                {
                    try
                    {
                        await UpdateDalamud(betaKind, betaKey).ConfigureAwait(true);
                        isUpdated = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Update failed, try {TryCnt}/{MaxTries}...", tries, MAX_TRIES);
                        this.EnsurementException = ex;
                    }
                }

                this.State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            });
        }

        public bool? ReCheckVersion(DirectoryInfo gamePath)
        {
            if (this.State != DownloadState.Done)
                return null;

            if (this.RunnerOverride != null)
                return true;

            var info = DalamudVersionInfo.Load(new FileInfo(Path.Combine(this.Runner.DirectoryName!,
                "version.json")));

            return Repository.Ffxiv.GetVer(gamePath) == info.SupportedGameVer;
        }

        private static string GetBetaTrackName(string betaKind) =>
            string.IsNullOrEmpty(betaKind) ? "staging" : betaKind;

        /// <summary>
        ///     [estell] マニフェスト取得用の GET を作る。中間キャッシュを一切使わせない。
        ///     配布マニフェストは数百バイトなので、毎回オリジンまで取りに行っても負荷にならない。
        ///     逆にキャッシュされると更新が届かない利用者が出る。
        /// </summary>
        private static HttpRequestMessage NoCacheGet(string url)
        {
            var message = new HttpRequestMessage(HttpMethod.Get, url);

            message.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MustRevalidate = true,
            };
            message.Headers.Pragma.ParseAdd("no-cache");

            return message;
        }

        private async Task<(DalamudVersionInfo release, DalamudVersionInfo? staging)> GetVersionInfo(string? betaKind, string? betaKey)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, DalamudLauncher.REMOTE_BASE + $"release&bucket={this.RolloutBucket}");
            message.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true
            };

            DalamudVersionInfo versionInfoRelease;

            try
            {
                using var primaryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var versionInfoResponse = await this.client.SendAsync(message, primaryTimeout.Token).ConfigureAwait(false);
                versionInfoResponse.EnsureSuccessStatusCode();
                versionInfoRelease = JsonConvert.DeserializeObject<DalamudVersionInfo>(
                    await versionInfoResponse.Content.ReadAsStringAsync(primaryTimeout.Token).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // [estell] kamori が落ちていると、ここで例外→更新処理ごと停止していた。
                // 自前 VPS のミラー(cron で定期同期)へ迂回する。
                var mirror = DistributionConfig.MirrorVersionInfoUrlFor("release");

                if (string.IsNullOrEmpty(mirror))
                    throw;

                Log.Warning(ex, "[DUPDATE] Official release manifest unreachable, falling back to mirror");

                using var mirrorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var mirrorResponse = await this.client.GetAsync(mirror, mirrorTimeout.Token).ConfigureAwait(false);
                mirrorResponse.EnsureSuccessStatusCode();
                versionInfoRelease = JsonConvert.DeserializeObject<DalamudVersionInfo>(
                    await mirrorResponse.Content.ReadAsStringAsync(mirrorTimeout.Token).ConfigureAwait(false));

                Log.Information("[DUPDATE] Using mirrored release manifest ({Version})", versionInfoRelease?.AssemblyVersion);
            }

            DalamudVersionInfo? versionInfoStaging = null;

            if (!string.IsNullOrEmpty(betaKey) && !string.IsNullOrEmpty(betaKind))
            {
                var trackName = GetBetaTrackName(betaKind);

                // 自前トラックの時のみ取得先を自前ホスト(GitHub raw 等)へ振り分ける(公式リリースは常に kamori)。
                var stagingUrl = DistributionConfig.VersionInfoUrlFor(trackName);

                try
                {
                    // [estell] 公式 release 側と同じくキャッシュを禁止する。
                    // これを付けないと GitHub raw の CDN(max-age=300)がエッジごとに
                    // 古いマニフェストを返し、「一部の利用者だけ更新されない」状態になる。
                    // 実際に 2026-09-08 の配布で 15 分以上 estell.30 が返り続けた。
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var request = NoCacheGet(stagingUrl);
                    using var response = await this.client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    // HTTPエラーも通信例外と同じくミラーへ進む。
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    versionInfoStaging = ParseTrackManifest(json, trackName);
                    if (DistributionConfig.IsCustomTrack(trackName) && versionInfoStaging.Key != betaKey)
                        throw new InvalidDataException("配布情報のトラックキーが一致しません。");
                    SaveTrackCache(trackName, json);
                }
                catch (Exception ex)
                {
                    // [estell] 配布元(GitHub raw 等)の障害・タイムアウト時。ここで公式 release へ
                    // フォールバックすると、別トラックの Dalamud を落としに行って失敗し起動できない。
                    // ミラー → ローカルキャッシュ の順で粘る。
                    Log.Warning(ex, "[DUPDATE] Could not fetch track manifest, trying mirror");

                    versionInfoStaging = await TryFetchFromMirror(trackName).ConfigureAwait(false)
                                         ?? LoadTrackCache(trackName);
                }

                if (DistributionConfig.IsCustomTrack(trackName)
                    && (versionInfoStaging is null || versionInfoStaging.Key != betaKey))
                    throw new DalamudIntegrityException("指定したDalamudトラックの配布情報を取得できません。別トラックへは切り替えません。");
            }

            return (versionInfoRelease, versionInfoStaging);
        }

        /// <summary>
        ///     [estell] 自前 VPS のミラーからトラックマニフェストを取る。
        ///     取得できたらキャッシュも更新する。失敗時は null。
        /// </summary>
        private async Task<DalamudVersionInfo?> TryFetchFromMirror(string trackName)
        {
            var mirror = DistributionConfig.MirrorVersionInfoUrlFor(trackName);

            if (string.IsNullOrEmpty(mirror))
                return null;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var request = NoCacheGet(mirror);
                using var response = await this.client.SendAsync(request, timeout.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var info = ParseTrackManifest(json, trackName);

                Log.Information("[DUPDATE] Using mirrored manifest for track {Track} ({Version})", trackName, info.AssemblyVersion);
                SaveTrackCache(trackName, json);
                return info;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DUPDATE] Mirror unreachable for track {Track}", trackName);
                return null;
            }
        }

        /// <summary>[estell] 取得できたトラックマニフェストを保存する。</summary>
        private void SaveTrackCache(string trackName, string json)
        {
            try
            {
                var file = GetTrackCacheFile(trackName);
                file.Directory?.Create();
                File.WriteAllText(file.FullName, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DUPDATE] Could not save track cache");
            }
        }

        /// <summary>
        ///     [estell] 保存済みのトラックマニフェストを読む。配布元が落ちている間の代替。
        ///     対象バージョンが実際にローカルへ導入済みのときだけ採用する
        ///     (未導入のものを返すと結局ダウンロードに行って失敗するため)。
        /// </summary>
        private DalamudVersionInfo? LoadTrackCache(string trackName)
        {
            try
            {
                var file = GetTrackCacheFile(trackName);

                if (!file.Exists)
                {
                    Log.Warning("[DUPDATE] No cached manifest for track {Track}", trackName);
                    return null;
                }

                var info = ParseTrackManifest(File.ReadAllText(file.FullName), trackName);

                var installed = new DirectoryInfo(
                    Path.Combine(this.addonDirectory.FullName, "Hooks", info.AssemblyVersion));

                if (!installed.Exists || !HasExpectedPackage(installed, info))
                {
                    Log.Warning("[DUPDATE] Cached manifest points to {Version}, but it is not installed - ignoring cache",
                                info.AssemblyVersion);
                    return null;
                }

                Log.Information("[DUPDATE] Using cached manifest for track {Track} ({Version}) - distribution host unreachable",
                                trackName, info.AssemblyVersion);
                return info;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DUPDATE] Could not load track cache");
                return null;
            }
        }

        private FileInfo GetTrackCacheFile(string trackName) =>
            new(Path.Combine(this.addonDirectory.FullName, "trackCache", trackName + ".json"));

        private static DalamudVersionInfo ParseTrackManifest(string json, string track)
        {
            var info = JsonConvert.DeserializeObject<DalamudVersionInfo>(json)
                       ?? throw new InvalidDataException("配布情報が空です。");
            if (!string.Equals(info.Track, track, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(info.AssemblyVersion)
                || info.AssemblyVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
                || (!string.IsNullOrEmpty(info.DownloadSha256)
                    && (info.DownloadSha256.Length != 64 || !info.DownloadSha256.All(Uri.IsHexDigit))))
                throw new InvalidDataException("配布情報のトラック・版・取得先が不正です。");
            return info;
        }

        private static bool HasExpectedPackage(DirectoryInfo directory, DalamudVersionInfo expected)
        {
            try
            {
                return IsIntegrity(directory) && DalamudPackage.MatchesVersion(directory, expected);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DUPDATE] Could not verify package version {Version}", expected.AssemblyVersion);
                return false;
            }
        }

        private async Task UpdateDalamud(string? betaKind, string? betaKey)
        {
            var (versionInfoRelease, versionInfoStaging) = await GetVersionInfo(betaKind, betaKey).ConfigureAwait(false);

            var remoteVersionInfo = versionInfoRelease;

            if (versionInfoStaging?.Key != null && versionInfoStaging.Key == betaKey)
            {
                remoteVersionInfo = versionInfoStaging;
                IsStaging = true;
                Log.Information("[DUPDATE] Using staging version {Kind} with key {Key} ({Hash})", betaKind, betaKey, remoteVersionInfo.AssemblyVersion);
            }
            else
            {
                Log.Information("[DUPDATE] Using release version ({Hash})", remoteVersionInfo.AssemblyVersion);
            }

            // Update resolved branch to reflect what the server actually selected
            this.ResolvedBranch = remoteVersionInfo;

            var versionInfoJson = JsonConvert.SerializeObject(remoteVersionInfo);

            var addonPath = new DirectoryInfo(Path.Combine(this.addonDirectory.FullName, "Hooks"));
            var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName, remoteVersionInfo.AssemblyVersion));
            var runtimePaths = new DirectoryInfo[]
            {
                new(Path.Combine(this.Runtime.FullName, "host", "fxr", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.NETCore.App", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", remoteVersionInfo.RuntimeVersion)),
            };

            if (!currentVersionPath.Exists || !HasExpectedPackage(currentVersionPath, remoteVersionInfo))
            {
                Log.Information("[DUPDATE] Not found, redownloading");

                SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

                try
                {
                    await DownloadDalamud(currentVersionPath, remoteVersionInfo).ConfigureAwait(true);

                    // This is a good indicator that we should clear the UID cache
                    cache?.Reset();
                }
                catch (Exception ex)
                {
                    throw new DalamudIntegrityException("Could not download Dalamud", ex);
                }
            }

            if (remoteVersionInfo.RuntimeRequired)
            {
                Log.Information("[DUPDATE] Now starting for .NET Runtime {0}", remoteVersionInfo.RuntimeVersion);

                var versionFile = new FileInfo(Path.Combine(this.Runtime.FullName, "version"));
                var localVersion = GetLocalRuntimeVersion(versionFile);

                var runtimeNeedsUpdate = localVersion != remoteVersionInfo.RuntimeVersion;

                if (!this.Runtime.Exists)
                    Directory.CreateDirectory(this.Runtime.FullName);

                var isRuntimeIntegrity = false;

                // Only check runtime hashes if we don't need to update it
                if (!runtimeNeedsUpdate)
                {
                    try
                    {
                        isRuntimeIntegrity = await CheckRuntimeHashes(Runtime, localVersion).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Could not check runtime integrity.");
                    }
                }

                if (runtimePaths.Any(p => !p.Exists) || runtimeNeedsUpdate || !isRuntimeIntegrity)
                {
                    Log.Information("[DUPDATE] Not found, outdated or no integrity: {LocalVer} - {RemoteVer}", localVersion, remoteVersionInfo.RuntimeVersion);

                    SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

                    try
                    {
                        Log.Verbose("[DUPDATE] Now download runtime...");
                        await DownloadRuntime(this.Runtime, remoteVersionInfo.RuntimeVersion).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new DalamudIntegrityException("Could not ensure runtime", ex);
                    }
                }
            }

            Log.Verbose("[DUPDATE] Now ensure assets...");

            int assetVer;

            try
            {
                this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
                this.ReportOverlayProgress(null, 0, null);
                var assetResult = await AssetManager.EnsureAssets(this.client, this, this.assetRootDirectory).ConfigureAwait(true);
                AssetDirectory = assetResult.AssetDir;
                assetVer = assetResult.Version;
            }
            catch (Exception ex)
            {
                throw new DalamudIntegrityException("Could not ensure assets", ex);
            }

            if (!HasExpectedPackage(currentVersionPath, remoteVersionInfo))
            {
                throw new DalamudIntegrityException("No integrity after ensurement");
            }

            WriteVersionJson(currentVersionPath, versionInfoJson);

            Log.Information("[DUPDATE] All set for {GameVersion} with {DalamudVersion}({RuntimeVersion}, {AssetVersion})", remoteVersionInfo.SupportedGameVer, remoteVersionInfo.AssemblyVersion, remoteVersionInfo.RuntimeVersion, assetVer);

            Runner = new FileInfo(Path.Combine(currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
            ReportOverlayProgress(null, 0, null);
        }

        private static bool CanRead(FileInfo info)
        {
            try
            {
                using var stream = info.OpenRead();
                stream.ReadByte();
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool IsIntegrity(DirectoryInfo addonPath)
        {
            var files = addonPath.GetFiles();

            try
            {
                if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe"))
                    || !CanRead(files.First(x => x.Name == "Dalamud.dll"))
                    || !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
                {
                    Log.Error("[DUPDATE] Can't open files for read");
                    return false;
                }

                var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

                if (!File.Exists(hashesPath))
                {
                    Log.Error("[DUPDATE] No hashes.json");
                    return false;
                }

                return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] No dalamud integrity");
                return false;
            }
        }

        private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
        {
            try
            {
                Log.Verbose("[DUPDATE] Checking integrity of {Directory}", directory.FullName);

                var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson);

                if (hashes == null)
                {
                    throw new Exception("Hashes deserialized to null");
                }

                foreach (var hash in hashes)
                {
                    var file = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                    using var fileStream = File.OpenRead(file);
                    using var md5 = MD5.Create();

                    var hashed = BitConverter.ToString(md5.ComputeHash(fileStream)).ToUpperInvariant().Replace("-", string.Empty);

                    if (hashed != hash.Value)
                    {
                        Log.Error("[DUPDATE] Integrity check failed for {0} ({1} - {2})", file, hash.Value, hashed);
                        return false;
                    }

                    Log.Verbose("[DUPDATE] Integrity check OK for {0} ({1})", file, hashed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Integrity check failed");
                return false;
            }

            return true;
        }

        private static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!addonPath.Exists)
                return;

            foreach (var directory in addonPath.GetDirectories())
            {
                if (directory.Name == "dev" || directory.Name == currentVer || directory.Name.StartsWith(".estell-dalamud-", StringComparison.Ordinal)) continue;

                try
                {
                    directory.Delete(true);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static void WriteVersionJson(DirectoryInfo addonPath, string info)
        {
            File.WriteAllText(Path.Combine(addonPath.FullName, "version.json"), info);
        }

        private async Task DownloadDalamud(DirectoryInfo addonPath, DalamudVersionInfo version)
        {
            var parent = addonPath.Parent ?? throw new InvalidDataException("配置先が不正です。");
            parent.Create();
            using var updateLock = await AcquireUpdateLock(parent).ConfigureAwait(false);
            if (System.IO.Directory.Exists(addonPath.FullName) && HasExpectedPackage(addonPath, version))
                return;

            var staging = parent.CreateSubdirectory(".estell-dalamud-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(staging.FullName, "previous");
            var canCleanup = true;
            try
            {
                var sources = new List<string>();
                if (DistributionConfig.IsCustomTrack(version.Track) && DistributionConfig.HasMirror)
                {
                    // 旧マニフェストのlatest.zipも、DLL内の版を検証してから使う。
                    var file = new Uri(version.DownloadUrl).AbsolutePath.EndsWith("/latest.zip", StringComparison.Ordinal)
                                   ? "latest.zip" : Uri.EscapeDataString(version.AssemblyVersion) + ".zip";
                    var mirror = version.MirrorDownloadUrl ?? DistributionConfig.MirrorBase + "dalamud/" + file;
                    if (!mirror.StartsWith(DistributionConfig.MirrorBase, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("本体ZIPミラーの取得先が不正です。");
                    sources.Add(mirror);
                }
                sources.Add(version.DownloadUrl);

                DirectoryInfo? ready = null;
                Exception? last = null;
                foreach (var source in sources.Distinct())
                {
                    var extracted = staging.CreateSubdirectory("source-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        var archive = Path.Combine(staging.FullName, "package.zip");
                        await this.DownloadFile(source, archive).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(version.DownloadSha256))
                        {
                            using var data = File.OpenRead(archive);
                            if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), version.DownloadSha256, StringComparison.OrdinalIgnoreCase))
                                throw new DalamudIntegrityException("DalamudのZIPハッシュが配布情報と一致しません。");
                        }
                        ZipFile.ExtractToDirectory(archive, extracted.FullName);
                        if (!HasExpectedPackage(extracted, version))
                            throw new DalamudIntegrityException("Dalamudのファイルまたは版が配布情報と一致しません。");
                        ready = extracted;
                        Log.Information("[DUPDATE] Verified package {Version} from {Url}", version.AssemblyVersion, source);
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Log.Warning(ex, "[DUPDATE] Package source failed: {Url}", source);
                    }
                }
                if (ready is null)
                    throw new DalamudIntegrityException("Dalamud本体をミラーと配布元から取得・検証できませんでした。", last);

                // 取得・版検証が完了するまで、既存の配置には触れない。
                var hadOriginal = System.IO.Directory.Exists(addonPath.FullName);
                if (hadOriginal)
                    System.IO.Directory.Move(addonPath.FullName, backup);
                try
                {
                    System.IO.Directory.Move(ready.FullName, addonPath.FullName);
                }
                catch
                {
                    if (hadOriginal)
                    {
                        try { System.IO.Directory.Move(backup, addonPath.FullName); }
                        catch (Exception restoreError)
                        {
                            canCleanup = false;
                            Log.Error(restoreError, "[DUPDATE] Original package retained at {Path}", backup);
                        }
                    }
                    throw;
                }
                CleanUpOld(parent, version.AssemblyVersion);
            }
            finally
            {
                try
                {
                    var prefix = Path.GetFullPath(parent.FullName).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (canCleanup && staging.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && staging.Name.StartsWith(".estell-dalamud-", StringComparison.Ordinal))
                        staging.Delete(true);
                }
                catch (Exception ex) { Log.Warning(ex, "[DUPDATE] Could not remove package staging directory"); }
            }

            try
            {
                var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));

                PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not copy to dev folder.");
            }
        }

        private string GetLocalRuntimeVersion(FileInfo versionFile)
        {
            // This is the version we first shipped. We didn't write out a version file, so we can't check it.
            var localVersion = "5.0.6";

            try
            {
                if (versionFile.Exists)
                    localVersion = File.ReadAllText(versionFile.FullName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not read local runtime version.");
            }

            return localVersion;
        }

        private async Task<bool> CheckRuntimeHashes(DirectoryInfo runtimePath, string version, bool mirrorFirst = true)
        {
            var hashesFile = new FileInfo(Path.Combine(runtimePath.FullName, $"hashes-{version}.json"));
            string? runtimeHashes;

            if (!hashesFile.Exists)
            {
                Log.Verbose("[DUPDATE] Hashes file does not exist, redownloading...");

                try
                {
                    var official = $"https://kamori.goats.dev/Dalamud/Release/Runtime/Hashes/{version}";
                    var sources = mirrorFirst && DistributionConfig.HasMirror ? new[] { DistributionConfig.MirrorRuntimeUrl(version, "hashes.json"), official } : new[] { official };
                    runtimeHashes = await DependencySource.ReadJson(this.client, sources, json =>
                        ValidRuntimeHashes(json) ? json : throw new InvalidDataException("ランタイムのハッシュ一覧が不正です。")).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DUPDATE] Could not download hashes for runtime v{Version}", version);
                    return false;
                }

                File.WriteAllText(hashesFile.FullName, runtimeHashes);
            }
            else
            {
                runtimeHashes = File.ReadAllText(hashesFile.FullName);
            }

            return ValidRuntimeHashes(runtimeHashes) && CheckIntegrity(runtimePath, runtimeHashes);
        }

        private static bool ValidRuntimeHashes(string json)
        {
            try
            {
                var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return hashes is { Count: > 0 } && hashes.All(entry => entry.Value.Length == 32 && entry.Value.All(Uri.IsHexDigit));
            }
            catch
            {
                return false;
            }
        }

        private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
        {
            // 実行中のゲームや補助プロセスが使う旧版は削除しない。
            // 同じフォルダを使う別ランチャーとの更新も、ファイルロックで直列化する。
            runtimePath.Create();
            using var updateLock = await AcquireUpdateLock(runtimePath).ConfigureAwait(false);
            var versionDirectories = new[]
            {
                Path.Combine(runtimePath.FullName, "host", "fxr", version),
                Path.Combine(runtimePath.FullName, "shared", "Microsoft.NETCore.App", version),
                Path.Combine(runtimePath.FullName, "shared", "Microsoft.WindowsDesktop.App", version),
            };
            if (versionDirectories.All(Directory.Exists)
                && await this.CheckRuntimeHashes(runtimePath, version).ConfigureAwait(false))
                return;

            var staging = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "estell-runtime-" + Guid.NewGuid().ToString("N")));
            staging.Create();
            try
            {
                DirectoryInfo? verified = null;
                foreach (var mirrorFirst in DistributionConfig.HasMirror ? new[] { true, false } : new[] { false })
                {
                    var extracted = staging.CreateSubdirectory(mirrorFirst ? "mirror" : "official");
                    try
                    {
                        foreach (var component in new[] { "DotNet", "WindowsDesktop" })
                        {
                            var archive = Path.Combine(extracted.Parent!.FullName, component + ".zip");
                            var url = mirrorFirst
                                ? DistributionConfig.MirrorRuntimeUrl(version, component == "DotNet" ? "dotnet.zip" : "windowsdesktop.zip")
                                : $"https://kamori.goats.dev/Dalamud/Release/Runtime/{component}/{version}";
                            await DependencySource.Download(this.client, url, archive, this.ReportOverlayProgress).ConfigureAwait(false);
                            ZipFile.ExtractToDirectory(archive, extracted.FullName, overwriteFiles: true);
                        }
                        File.WriteAllText(Path.Combine(extracted.FullName, "version"), version);
                        if (!await this.CheckRuntimeHashes(extracted, version, mirrorFirst).ConfigureAwait(false))
                            throw new DalamudIntegrityException("Downloaded runtime did not pass integrity verification");
                        verified = extracted;
                        break;
                    }
                    catch (Exception ex) when (mirrorFirst)
                    {
                        Log.Warning(ex, "[DUPDATE] Runtime mirror failed verification or download; trying official source");
                    }
                }
                // 取得・展開・ハッシュ検証がすべて済むまで既存の構成には触れない。
                await this.InstallRuntime(verified!, runtimePath, version).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (staging.FullName.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                        staging.Delete(true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DUPDATE] Could not remove temporary runtime files");
                }
            }
        }

        private async Task InstallRuntime(DirectoryInfo extracted, DirectoryInfo runtimePath, string version)
        {
            if (!Version.TryParse(version, out var parsedVersion) || parsedVersion.ToString() != version)
                throw new DalamudIntegrityException("Invalid runtime version");

            // 同じボリューム上に入替用と復元用を用意する。他系列のフォルダは操作対象にしない。
            var transaction = runtimePath.CreateSubdirectory(".estell-runtime-update-" + Guid.NewGuid().ToString("N"));
            var canCleanUp = true;
            var replacements = new List<(string Target, string Backup, bool Directory, bool HadOriginal)>();
            try
            {
                var ready = transaction.CreateSubdirectory("ready");
                // 空の準備先にだけ、この上書きを行わない共通関数を使う。
                PlatformHelpers.CopyFilesRecursively(extracted, ready);
                var directories = new[]
                {
                    Path.Combine("host", "fxr", version),
                    Path.Combine("shared", "Microsoft.NETCore.App", version),
                    Path.Combine("shared", "Microsoft.WindowsDesktop.App", version),
                };
                var units = directories.Select(path => (Relative: path, Directory: true))
                    .Concat(ready.GetFiles().OrderBy(file => file.Name == "version" ? 1 : 0)
                        .Select(file => (Relative: file.Name, Directory: false))).ToArray();

                foreach (var unit in units)
                {
                    var source = RuntimeChildPath(ready, unit.Relative);
                    var target = RuntimeChildPath(runtimePath, unit.Relative);
                    var backup = RuntimeChildPath(transaction, Path.Combine("backup", unit.Relative));
                    if (unit.Directory && !System.IO.Directory.Exists(source))
                        throw new DalamudIntegrityException("Downloaded runtime component is missing");

                    // 内容が同じなら実行中の共通ホスト等には触れない。
                    if (unit.Directory ? RuntimeDirectoriesMatch(source, target) : RuntimeFilesMatch(source, target))
                        continue;

                    var hadOriginal = unit.Directory ? System.IO.Directory.Exists(target) : File.Exists(target);
                    if (hadOriginal)
                    {
                        var existingFiles = unit.Directory
                            ? System.IO.Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                            : new[] { target };
                        foreach (var file in existingFiles)
                        {
                            // 使用中の対象版は更新せず、復元して起動前の再試行に任せる。
                            using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                        }
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        if (unit.Directory)
                            System.IO.Directory.Move(target, backup);
                        else
                            File.Move(target, backup);
                    }

                    replacements.Add((target, backup, unit.Directory, hadOriginal));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (unit.Directory)
                        System.IO.Directory.Move(source, target);
                    else
                        File.Move(source, target);
                }

                // 展開先だけでなく、実際の配置先を公式ハッシュで再検査する。
                if (!await this.CheckRuntimeHashes(runtimePath, version).ConfigureAwait(false))
                    throw new DalamudIntegrityException("Installed runtime did not pass integrity verification");
                Log.Information("[DUPDATE] Runtime {Version} installed and verified", version);
            }
            catch
            {
                foreach (var replacement in replacements.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (replacement.Directory)
                        {
                            if (System.IO.Directory.Exists(replacement.Target))
                                System.IO.Directory.Delete(replacement.Target, recursive: true);
                            if (replacement.HadOriginal)
                                System.IO.Directory.Move(replacement.Backup, replacement.Target);
                        }
                        else
                        {
                            File.Delete(replacement.Target);
                            if (replacement.HadOriginal)
                                File.Move(replacement.Backup, replacement.Target);
                        }
                    }
                    catch (Exception ex)
                    {
                        canCleanUp = false;
                        Log.Error(ex, "[DUPDATE] Could not restore runtime; backup retained at {Path}", transaction.FullName);
                    }
                }
                throw;
            }
            finally
            {
                if (canCleanUp)
                {
                    try
                    {
                        // 削除・移動する全パスは、指定された runtime の内部に限定してある。
                        RuntimeChildPath(runtimePath, transaction.Name);
                        transaction.Delete(recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[DUPDATE] Could not remove runtime transaction files at {Path}", transaction.FullName);
                    }
                }
            }
        }

        private static string RuntimeChildPath(DirectoryInfo root, string relative)
        {
            var prefix = root.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root.FullName, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new DalamudIntegrityException("Runtime path is outside the selected directory");
            return path;
        }

        private static bool RuntimeFilesMatch(string source, string target)
        {
            if (!File.Exists(target) || new FileInfo(source).Length != new FileInfo(target).Length)
                return false;
            using var sourceStream = File.OpenRead(source);
            using var targetStream = File.OpenRead(target);
            return SHA256.HashData(sourceStream).SequenceEqual(SHA256.HashData(targetStream));
        }

        private static bool RuntimeDirectoriesMatch(string source, string target)
        {
            if (!System.IO.Directory.Exists(target))
                return false;
            var files = System.IO.Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            return files.Length == System.IO.Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length
                   && files.All(file => RuntimeFilesMatch(file, Path.Combine(target, Path.GetRelativePath(source, file))));
        }

        private static async Task<FileStream> AcquireUpdateLock(DirectoryInfo runtimePath)
        {
            var lockPath = Path.Combine(runtimePath.FullName, "estell-update.lock");
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }

        public async Task DownloadFile(string url, string path)
        {
            await DependencySource.Download(this.client, url, path, this.ReportOverlayProgress).ConfigureAwait(false);
        }

        public Task DownloadDependencyFile(string url, string path) => DependencySource.Download(this.client, url, path, this.ReportOverlayProgress);
    }

    public class DalamudIntegrityException(string msg, Exception? inner = null) : Exception(msg, inner);
}
