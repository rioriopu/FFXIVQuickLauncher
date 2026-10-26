using System;
using System.Threading.Tasks;

using Serilog;

using Velopack;
using Velopack.Sources;

using XIVLauncher.Common.Dalamud;

namespace XIVLauncher
{
    /// <summary>
    ///     [estell] ランチャー自身の自動更新。
    ///
    ///     公式の自己更新(kamori の Lease API)は使わない。あれを通すと公式ビルドが降ってきて
    ///     estell 改変が巻き戻るため、<see cref="AppUtil.IsDisableUpdates"/> で常時止めてある。
    ///     こちらは自前のフィードだけを見るので、巻き戻る心配がない。
    ///
    ///     フィードは vpk pack の出力(releases.win.json + *.nupkg)をそのまま静的ホストに
    ///     置いたもの。自前 VPS を第一候補、GitHub Releases を代替とする。
    /// </summary>
    internal static class EstellLauncherUpdater
    {
        /// <summary>更新確認の結果。</summary>
        internal sealed class Result
        {
            /// <summary>更新が見つかったか。</summary>
            public bool HasUpdate { get; init; }

            /// <summary>見つかった新しいバージョン。無ければ null。</summary>
            public string? NewVersion { get; init; }

            /// <summary>適用に使う UpdateManager。<see cref="HasUpdate"/> が true のときのみ非 null。</summary>
            public UpdateManager? Manager { get; init; }

            /// <summary>見つかった更新情報。</summary>
            public UpdateInfo? Info { get; init; }
        }

        /// <summary>
        ///     自前フィードを順に当たって更新を探す。
        ///     どのフィードにも繋がらない場合や、そもそも Velopack 管理下でない場合は
        ///     「更新なし」として静かに返す。起動を妨げてはいけない。
        /// </summary>
        internal static async Task<Result> CheckAsync()
        {
            foreach (var feed in DistributionConfig.LauncherFeedUrls)
            {
                try
                {
                    var options = new UpdateOptions
                    {
                        // vpk pack の --channel と一致させる。
                        // ずれていると「更新なし」と誤判定して永遠に更新されない。
                        ExplicitChannel = DistributionConfig.LauncherFeedChannel,
                    };

                    var manager = new UpdateManager(new SimpleWebSource(feed), options);

                    if (!manager.IsInstalled)
                    {
                        // 展開しただけ(ポータブル)の場合はここに来る。更新のしようがない。
                        Log.Information("[ESTELL] Launcher is not Velopack-installed, skipping self-update");
                        return new Result { HasUpdate = false };
                    }

                    var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);

                    if (info == null)
                    {
                        Log.Information("[ESTELL] Launcher is up to date (feed: {Feed})", feed);
                        return new Result { HasUpdate = false };
                    }

                    var version = info.TargetFullRelease.Version.ToString();
                    Log.Information("[ESTELL] Launcher update available: {Version} (feed: {Feed})", version, feed);

                    return new Result
                    {
                        HasUpdate = true,
                        NewVersion = version,
                        Manager = manager,
                        Info = info,
                    };
                }
                catch (Exception ex)
                {
                    // このフィードが落ちているだけかもしれないので次を試す。
                    Log.Warning(ex, "[ESTELL] Launcher update feed unreachable: {Feed}", feed);
                }
            }

            Log.Warning("[ESTELL] No launcher update feed was reachable, continuing without update");
            return new Result { HasUpdate = false };
        }

        /// <summary>
        ///     更新を適用してランチャーを再起動する。
        ///     成功した場合この呼び出しから戻らない(プロセスが差し替わる)。
        /// </summary>
        /// <param name="result"><see cref="CheckAsync"/> の戻り値。</param>
        /// <returns>適用に失敗した場合 false。</returns>
        internal static async Task<bool> ApplyAndRestartAsync(Result result)
        {
            if (result is not { HasUpdate: true, Manager: not null, Info: not null })
                return false;

            try
            {
                await result.Manager.DownloadUpdatesAsync(result.Info).ConfigureAwait(false);
                result.Manager.ApplyUpdatesAndRestart(result.Info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ESTELL] Failed to apply launcher update");
                return false;
            }
        }
    }
}
