using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Newtonsoft.Json;

using Serilog;

namespace XIVLauncher.Accounts
{
    /// <summary>
    /// <see cref="AccountProfile"/> の永続化と、アカウント別起動ショートカット(.lnk)の生成を担う。
    /// プロファイル定義は %APPDATA%\XIVLauncher\accountProfiles.json に保存する
    /// (--roamingPath 上書きに依存しないよう、常に既定ベース直下に置く)。
    /// </summary>
    public class AccountProfileManager
    {
        private static string StorePath =>
            Path.Combine(AccountProfile.DefaultRoamingBase, "accountProfiles.json");

        public ObservableCollection<AccountProfile> Profiles { get; private set; } = new();

        public AccountProfileManager()
        {
            this.Load();
            this.EnsureDefault();
        }

        /// <summary>1アカウント目(既定)プロファイルが必ず先頭に存在するようにする。</summary>
        private void EnsureDefault()
        {
            if (this.Profiles.All(p => !p.IsDefault))
            {
                this.Profiles.Insert(0, new AccountProfile
                {
                    Name = "Default",
                    IsDefault = true,
                });
            }
        }

        public void Load()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    var list = JsonConvert.DeserializeObject<List<AccountProfile>>(File.ReadAllText(StorePath));
                    if (list != null)
                        this.Profiles = new ObservableCollection<AccountProfile>(list);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PROFILES] Failed to load account profiles");
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AccountProfile.DefaultRoamingBase);
                File.WriteAllText(StorePath, JsonConvert.SerializeObject(this.Profiles, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PROFILES] Failed to save account profiles");
            }
        }

        /// <summary>現在実行中の XIVLauncher 実行ファイルのパス。</summary>
        private static string GetLauncherExePath() =>
            Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

        /// <summary>
        /// プロファイル用のショートカット(.lnk)を <paramref name="destDir"/> に生成する。
        /// 既定以外は対象フォルダも作成する。生成した .lnk のパスを返す。
        /// </summary>
        public static string GenerateShortcut(AccountProfile profile, string destDir)
        {
            var exePath = GetLauncherExePath();
            var roaming = profile.ResolvedRoamingPath;

            if (!profile.IsDefault)
                Directory.CreateDirectory(roaming);

            Directory.CreateDirectory(destDir);

            var safeName = string.Concat((profile.Name ?? "profile").Split(Path.GetInvalidFileNameChars()));
            var lnkName = profile.IsDefault ? "XIVLauncher.lnk" : $"XIVLauncher ({safeName}).lnk";
            var lnkPath = Path.Combine(destDir, lnkName);

            // 既定は --roamingPath を付けない(通常の %APPDATA%\XIVLauncher を使用)
            var args = profile.IsDefault ? string.Empty : $"--roamingPath=\"{roaming}\"";

            // WScript.Shell 経由で .lnk を作成(追加の参照やパッケージ不要)
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                            ?? throw new InvalidOperationException("WScript.Shell COM が利用できません。");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                var shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath = exePath;
                shortcut.Arguments = args;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.IconLocation = exePath + ",0";
                shortcut.Description = profile.IsDefault ? "XIVLauncher" : $"XIVLauncher - {profile.Name}";
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }

            Log.Information("[PROFILES] Generated shortcut {Path} (roaming={Roaming})", lnkPath, roaming);
            return lnkPath;
        }
    }
}
