using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using XIVLauncher.Common.Dalamud;

namespace XIVLauncher.Windows;

public partial class RuntimeRepairWindow : Window
{
    private readonly LegacyRuntimeRepair repair = new(App.HttpClient);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Queue<string> trace = new();
    private bool busy;
    private bool installerRunning;

    public RuntimeRepairWindow() => this.InitializeComponent();

    private string SelectedChannel => (string)((ComboBoxItem)this.Channel.SelectedItem).Tag;

    private void Window_Loaded(object sender, RoutedEventArgs e) => this.RefreshStatus();

    private void Channel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.IsLoaded)
            this.RefreshStatus();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => this.RefreshStatus();

    private void RefreshStatus()
    {
        try
        {
            // 32bit 側やランチャー専用の .NET を、Windows x64 の導入済みと誤認しない。
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = registry.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            var root = key?.GetValue("InstallLocation") as string
                       ?? Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            var core = LegacyRuntimeRepair.GetInstalledVersions(root, "Microsoft.NETCore.App", this.SelectedChannel);
            var desktop = LegacyRuntimeRepair.GetInstalledVersions(root, "Microsoft.WindowsDesktop.App", this.SelectedChannel);
            var paired = core.Intersect(desktop).ToArray();
            this.InstalledStatus.Text = $"Windows x64 / .NET {this.SelectedChannel}\n"
                + $".NET Runtime：{(core.Length == 0 ? "不足" : string.Join(", ", core))}\n"
                + $"Desktop Runtime：{(desktop.Length == 0 ? "不足" : string.Join(", ", desktop))}\n"
                + (paired.Length > 0 ? "同じ版の両ランタイムを確認できました。修復から更新・再インストールもできます。" : "不足があります。警告にこの系列が指定されている場合は、修復してください。");
            this.AddTrace($".NET {this.SelectedChannel} の導入状況を確認しました。");
        }
        catch (Exception ex)
        {
            this.InstalledStatus.Text = "導入状況を確認できませんでした。";
            this.ReportError(ex);
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (this.busy)
            return;
        this.SetBusy(true);
        try
        {
            this.AddTrace($".NET {this.SelectedChannel} の公式配布情報を確認しています…");
            var package = await this.repair.GetPackage(this.SelectedChannel, this.cancellation.Token);
            this.AddTrace($"{package.Version} / Windows x64 を取得しています…");
            var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIVLauncher", "runtime-repair");
            var progress = new Progress<double>(value => this.DownloadProgress.Value = Math.Min(100, value));
            var installer = await this.repair.Download(package, destination, progress, this.cancellation.Token);
            this.AddTrace("SHA-512 検証に成功しました。公式インストーラーを開きます。");
            this.installerRunning = true;
            using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
            if (process == null)
                throw new InvalidOperationException("インストーラーを起動できませんでした。");
            await process.WaitForExitAsync();
            this.installerRunning = false;
            this.AddTrace($"インストーラーが終了しました（終了コード：{process.ExitCode}）。導入状況を再確認します。");
            this.RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            this.AddTrace("取得を中止しました。");
        }
        catch (Exception ex)
        {
            this.ReportError(ex);
        }
        finally
        {
            this.installerRunning = false;
            this.SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        this.busy = value;
        this.RepairButton.IsEnabled = this.RefreshButton.IsEnabled = this.Channel.IsEnabled = !value;
        this.DownloadProgress.Value = 0;
    }

    private void AddTrace(string message)
    {
        this.trace.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (this.trace.Count > 20)
            this.trace.Dequeue();
        this.Trace.Text = string.Join(Environment.NewLine, this.trace);
        this.Trace.ScrollToEnd();
        Log.Information("[RuntimeRepair] {Message}", message);
    }

    private void ReportError(Exception ex)
    {
        Log.Error(ex, "[RuntimeRepair] ランタイム修復に失敗しました");
        this.AddTrace("処理に失敗しました：" + ex.Message);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (this.installerRunning)
        {
            e.Cancel = true;
            this.AddTrace("公式インストーラーを閉じてから、この画面を閉じてください。");
            return;
        }
        this.cancellation.Cancel();
    }
}
