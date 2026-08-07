using System.Collections.Generic;
using System.IO;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Xaml;

namespace XIVLauncher.Settings
{
    public interface ILauncherSettingsV3
    {
        #region Launcher Setting

        DirectoryInfo GamePath { get; set; }
        bool AutologinEnabled { get; set; }
        List<AddonEntry> AddonList { get; set; }
        bool UniqueIdCacheEnabled { get; set; }
        string AdditionalLaunchArgs { get; set; }
        bool InGameAddonEnabled { get; set; }
        DalamudLoadMethod? InGameAddonLoadMethod { get; set; }
        bool OtpServerEnabled { get; set; }
        bool OtpAlwaysOnTopEnabled { get; set; }
        ClientLanguage? Language { get; set; }
        LauncherLanguage? LauncherLanguage { get; set; }
        string CurrentAccountId { get; set; }
        bool? EncryptArguments { get; set; }
        DirectoryInfo PatchPath { get; set; }
        bool? AskBeforePatchInstall { get; set; }
        long SpeedLimitBytes { get; set; }
        decimal DalamudInjectionDelayMs { get; set; }
        bool? KeepPatches { get; set; }
        bool? HasComplainedAboutAdmin { get; set; }
        bool? HasComplainedAboutGShadeDxgi { get; set; }
        bool? HasComplainedAboutNoOtp { get; set; }
        string LastVersion { get; set; }
        bool? HasShownAutoLaunchDisclaimer { get; set; }
        string AcceptLanguage { get; set; }
        DpiAwareness? DpiAwareness { get; set; }
        int? VersionUpgradeLevel { get; set; }
        bool? TreatNonZeroExitCodeAsFailure { get; set; }
        bool? ExitLauncherAfterGameExit { get; set; }
        bool? IsFt { get; set; }
        string DalamudRolloutBucket { get; set; }
        bool? AutoStartSteam { get; set; }
        bool? ForceNorthAmerica { get; set; }

        string? DalamudBetaKind { get; set; }
        string? DalamudBetaKey { get; set; }

        // --- estell 試験機能 ---------------------------------------------------

        /// <summary>
        ///     メンテナンス中(ゲート閉鎖)でもゲームの起動を続行する。
        ///     ログインはできないが、タイトル画面まで到達するので
        ///     パッチ直後に Dalamud が正しく動くかを検証できる。
        /// </summary>
        bool? ExperimentalIgnoreMaintenance { get; set; }

        /// <summary>
        ///     ゲーム起動時に Dalamud を注入するかどうか。
        ///     既定は true。false にすると InGameAddonEnabled の設定に関わらず注入しない。
        ///     素のクライアント挙動と比較したいときに使う。
        /// </summary>
        bool? ExperimentalInjectDalamud { get; set; }

        PreserveWindowPosition.WindowPlacement? MainWindowPlacement { get; set; }

        #endregion
    }
}
