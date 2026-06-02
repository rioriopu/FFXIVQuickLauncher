using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

using Newtonsoft.Json;

namespace XIVLauncher.Accounts
{
    /// <summary>
    /// 複数アカウント用の起動プロファイル。各プロファイルは独立した RoamingPath を持ち、
    /// XIVLauncher を <c>--roamingPath</c> 付きで起動するショートカットとして展開できる。
    ///
    /// 1アカウント目(<see cref="IsDefault"/>)は通常どおり %APPDATA%\XIVLauncher を使い、
    /// <c>--roamingPath</c> を付けない。2アカウント目以降は
    ///   - 自動: %APPDATA%\XIVLauncher_&lt;名&gt;
    ///   - 指定: <see cref="CustomFolder"/>
    /// を使う。
    /// </summary>
    public class AccountProfile : INotifyPropertyChanged
    {
        private string name = string.Empty;
        private bool useCustomFolder;
        private string? customFolder;

        public string Name
        {
            get => this.name;
            set { this.name = value; this.OnChanged(); this.OnChanged(nameof(this.ResolvedRoamingPath)); }
        }

        /// <summary>true なら <see cref="CustomFolder"/> を使う。false なら自動命名(%APPDATA%\XIVLauncher_&lt;名&gt;)。</summary>
        public bool UseCustomFolder
        {
            get => this.useCustomFolder;
            set { this.useCustomFolder = value; this.OnChanged(); this.OnChanged(nameof(this.ResolvedRoamingPath)); }
        }

        public string? CustomFolder
        {
            get => this.customFolder;
            set { this.customFolder = value; this.OnChanged(); this.OnChanged(nameof(this.ResolvedRoamingPath)); }
        }

        /// <summary>既定プロファイル(1アカウント目)。%APPDATA%\XIVLauncher を使い --roamingPath を付けない。</summary>
        public bool IsDefault { get; set; }

        /// <summary>%APPDATA%\XIVLauncher の既定ベース(--roamingPath 上書きの影響を受けない実体)。</summary>
        [JsonIgnore]
        public static string DefaultRoamingBase =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");

        /// <summary>このプロファイルが使う RoamingPath(UI のプレビュー兼ショートカット生成用)。</summary>
        [JsonIgnore]
        public string ResolvedRoamingPath
        {
            get
            {
                if (this.IsDefault)
                    return DefaultRoamingBase;
                if (this.UseCustomFolder && !string.IsNullOrWhiteSpace(this.CustomFolder))
                    return Environment.ExpandEnvironmentVariables(this.CustomFolder);
                return $"{DefaultRoamingBase}_{SanitizeName(this.Name)}";
            }
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "profile";
            foreach (var c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value.Trim();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? propertyName = null)
            => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
