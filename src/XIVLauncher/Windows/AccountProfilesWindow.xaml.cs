using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

using Microsoft.Win32;

using XIVLauncher.Accounts;

namespace XIVLauncher.Windows
{
    /// <summary>
    /// アカウント別プロファイル(独立 Roaming フォルダ + 起動ショートカット)の管理ウィンドウ。
    /// </summary>
    public partial class AccountProfilesWindow : Window
    {
        private readonly AccountProfileManager manager;

        private AccountProfilesViewModel Model => (AccountProfilesViewModel)this.DataContext;

        private static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public AccountProfilesWindow()
        {
            this.InitializeComponent();
            this.manager = new AccountProfileManager();
            this.DataContext = new AccountProfilesViewModel(this.manager.Profiles);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var profile = new AccountProfile { Name = $"Account{this.Model.Profiles.Count}" };
            this.Model.Profiles.Add(profile);
            this.Model.SelectedProfile = profile;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var profile = this.Model.SelectedProfile;
            if (profile == null)
                return;

            if (profile.IsDefault)
            {
                MessageBox.Show("既定プロファイルは削除できません。", "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            this.Model.Profiles.Remove(profile);
            this.Model.SelectedProfile = this.Model.Profiles.FirstOrDefault();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var profile = this.Model.SelectedProfile;
            if (profile == null)
                return;

            var dlg = new OpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(profile.CustomFolder))
            {
                try { dlg.InitialDirectory = profile.CustomFolder; }
                catch { /* 無効パスは無視 */ }
            }

            if (dlg.ShowDialog() == true)
                profile.CustomFolder = dlg.FolderName;
        }

        private void GenerateSelected_Click(object sender, RoutedEventArgs e)
        {
            var profile = this.Model.SelectedProfile;
            if (profile == null)
                return;

            try
            {
                var lnk = AccountProfileManager.GenerateShortcut(profile, DesktopDir);
                MessageBox.Show($"ショートカットを生成しました:\n{lnk}\n\nデータフォルダ:\n{profile.ResolvedRoamingPath}", "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ショートカット生成に失敗しました:\n{ex.Message}", "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var count = this.Model.Profiles.Select(p => AccountProfileManager.GenerateShortcut(p, DesktopDir)).Count();
                MessageBox.Show($"{count} 件のショートカットをデスクトップに生成しました。", "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ショートカット生成に失敗しました:\n{ex.Message}", "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            this.manager.Save();
            this.DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }

    /// <summary>アカウント別プロファイル管理ウィンドウの ViewModel。</summary>
    public class AccountProfilesViewModel : INotifyPropertyChanged
    {
        private AccountProfile? selectedProfile;

        public ObservableCollection<AccountProfile> Profiles { get; }

        public AccountProfile? SelectedProfile
        {
            get => this.selectedProfile;
            set { this.selectedProfile = value; this.OnChanged(); }
        }

        public AccountProfilesViewModel(ObservableCollection<AccountProfile> profiles)
        {
            this.Profiles = profiles;
            this.SelectedProfile = profiles.FirstOrDefault();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? propertyName = null)
            => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
