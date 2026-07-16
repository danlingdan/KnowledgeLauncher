using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using KnowledgeLauncher.Core;
using KnowledgeLauncher.Infrastructure;

namespace KnowledgeLauncher.App;

public partial class MainWindow : Window, IDisposable
{
    private static readonly Version RequiredObsidianVersion = new(1, 11, 0);
    private readonly AppPaths _paths = new();
    private readonly ObsidianDetector _obsidianDetector = new();
    private readonly ObsidianLauncher _obsidianLauncher = new();
    private readonly ObsidianVaultRegistry _vaultRegistry = new();
    private readonly VaultUsageDetector _vaultUsageDetector = new();
    private CancellationTokenSource? _cancellation;
    private LauncherSettings? _settings;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        await CheckForUpdateOnStartupAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var log = new FileLauncherLog(_paths);
        _settings = await new SettingsService(_paths, log).LoadAsync();
        var obsidian = _obsidianDetector.Detect();
        var obsidianCompatible = obsidian.IsInstalled
            && obsidian.Version is not null
            && obsidian.Version >= RequiredObsidianVersion;
        ObsidianStatusText.Text = !obsidian.IsInstalled
            ? "未安装"
            : obsidian.Version is null
                ? "已安装，但无法读取版本"
                : obsidianCompatible
                    ? $"已安装，版本 {obsidian.Version}"
                    : $"版本 {obsidian.Version}，需要 {RequiredObsidianVersion} 或更高版本";
        InstallObsidianButton.Content = obsidian.IsInstalled ? "更新 Obsidian" : "安装 Obsidian";
        InstallObsidianButton.Visibility = obsidianCompatible ? Visibility.Collapsed : Visibility.Visible;
        var installed = !string.IsNullOrWhiteSpace(_settings.InstalledVaultPath)
            && Directory.Exists(_settings.InstalledVaultPath);
        VaultStatusText.Text = installed ? $"已安装，版本 {_settings.InstalledVersion}" : "尚未安装";
        LastSuccessText.Text = _settings.LastSuccessfulRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "—";
        InstallButton.IsEnabled = obsidianCompatible && !installed;
        OpenButton.IsEnabled = obsidianCompatible && installed;
        UpdateButton.IsEnabled = obsidianCompatible && installed;
        RepairButton.IsEnabled = obsidianCompatible && installed;
    }

    private async void InstallObsidianButton_Click(object sender, RoutedEventArgs e)
    {
        var installer = new ObsidianInstaller();
        var useWinget = installer.IsWingetAvailable()
            && MessageBox.Show(
                "是否通过 Windows 程序包管理器安装官方 Obsidian？",
                "安装 Obsidian",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

        SetBusy(true);
        try
        {
            var installed = useWinget && await installer.InstallWithWingetAsync();
            if (!installed && !_obsidianDetector.Detect().IsInstalled)
            {
                installer.OpenOfficialDownloadPage();
                MessageBox.Show("已打开 Obsidian 官方下载页。安装完成后返回启动器重新检查。", "安装 Obsidian");
            }
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var trust = MessageBox.Show(
            "知识库将运行清单中锁定的社区插件代码。是否信任发布者并继续？\n\n" +
            "知识库内容由发布者管理，未来更新可能覆盖对上游笔记的直接修改。",
            "社区插件信任确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!trust)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);
        StatusTextBox.Clear();
        var log = new FileLauncherLog(_paths);
        var settingsService = new SettingsService(_paths, log);
        using var httpClient = new HttpClient();
        var releases = new GitHubReleaseClient(httpClient, log);
        var preparer = new PrototypeWorkflow(
            _paths,
            settingsService,
            _obsidianDetector,
            releases,
            new PluginInstaller(releases, log),
            log);
        var workflow = new FirstInstallWorkflow(_paths, settingsService, preparer, log);
        var progress = new Progress<InstallationProgress>(item =>
        {
            StatusTextBox.AppendText($"✓ {item.Message}{Environment.NewLine}");
            StatusTextBox.ScrollToEnd();
        });

        try
        {
            var result = await workflow.InstallAsync(true, progress, cancellation.Token);
            StatusTextBox.AppendText(
                $"{Environment.NewLine}安装完成：{result.Manifest.Name} {result.Manifest.Version}{Environment.NewLine}" +
                $"插件：{string.Join("、", result.InstalledPluginIds)}{Environment.NewLine}");
            await RefreshStatusAsync();
            OpenVault(result.VaultPath, result.Manifest);
        }
        catch (OperationCanceledException)
        {
            StatusTextBox.AppendText($"{Environment.NewLine}安装已取消。");
        }
        catch (LauncherException exception)
        {
            StatusTextBox.AppendText($"{Environment.NewLine}失败 [{exception.ErrorCode}]：{exception.Message}");
            log.Write(LogLevel.Error, exception.ErrorCode, exception.Message, exception);
        }
        catch (Exception exception)
        {
            StatusTextBox.AppendText($"{Environment.NewLine}发生未预期错误，请查看日志。");
            log.Write(LogLevel.Error, "KL-UNEXPECTED", "首次安装发生未预期错误。", exception);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings?.InstalledVaultPath is not { Length: > 0 } vaultPath)
        {
            return;
        }

        try
        {
            var manifest = await ManifestLoader.LoadVaultManifestAsync(
                Path.Combine(vaultPath, "launcher", "manifest.json"),
                vaultPath);
            OpenVault(vaultPath, manifest);
        }
        catch (LauncherException exception)
        {
            MessageBox.Show(exception.Message, $"打开失败 [{exception.ErrorCode}]", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenVault(string vaultPath, VaultManifest manifest)
    {
        var vaultId = _vaultRegistry.FindVaultId(vaultPath);
        if (vaultId is null && _vaultUsageDetector.IsObsidianRunning())
        {
            MessageBox.Show(
                "这是首次打开该知识库。请完全退出 Obsidian 后再次点击“打开知识库”，启动器将自动登记并打开它。",
                "需要退出 Obsidian",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        vaultId ??= _vaultRegistry.EnsureRegistered(vaultPath);
        _obsidianLauncher.Open(vaultId, manifest.Entry);
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        _paths.EnsureCreated();
        Process.Start(new ProcessStartInfo(_paths.Logs) { UseShellExecute = true });
    }

    private async Task CheckForUpdateOnStartupAsync()
    {
        if (_settings?.InstalledVaultPath is not { Length: > 0 } vaultPath || !Directory.Exists(vaultPath))
        {
            return;
        }

        var log = new FileLauncherLog(_paths);
        try
        {
            var settingsService = new SettingsService(_paths, log);
            using var httpClient = new HttpClient();
            var releases = new GitHubReleaseClient(httpClient, log);
            var preparer = new PrototypeWorkflow(
                _paths, settingsService, _obsidianDetector, releases, new PluginInstaller(releases, log), log);
            var updater = new VaultUpdateService(
                _paths, settingsService, releases, preparer, new VaultUsageDetector(), log);
            var check = await updater.CheckAsync();
            if (check.Availability == UpdateAvailability.UpdateAvailable)
            {
                VaultStatusText.Text = $"已安装 {check.InstalledVersion}，可更新到 {check.LatestVersion}";
            }
        }
        catch (Exception exception) when (exception is LauncherException or HttpRequestException or TaskCanceledException)
        {
            log.Write(LogLevel.Warning, "KL-STARTUP-CHECK", "启动时检查更新失败，继续使用本地知识库。", exception);
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUpdateAsync(repair: false);
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
            "修复安装会重新下载并验证受管理内容，同时保留工作区、快捷键和插件个人配置。继续前请关闭 Obsidian。",
            "修复安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunUpdateAsync(repair: true);
    }

    private async Task RunUpdateAsync(bool repair)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);
        var log = new FileLauncherLog(_paths);
        var settingsService = new SettingsService(_paths, log);
        using var httpClient = new HttpClient();
        var releases = new GitHubReleaseClient(httpClient, log);
        var preparer = new PrototypeWorkflow(
            _paths,
            settingsService,
            _obsidianDetector,
            releases,
            new PluginInstaller(releases, log),
            log);
        var updater = new VaultUpdateService(
            _paths,
            settingsService,
            releases,
            preparer,
            new VaultUsageDetector(),
            log);
        var progress = new Progress<string>(message =>
        {
            StatusTextBox.AppendText($"✓ {message}{Environment.NewLine}");
            StatusTextBox.ScrollToEnd();
        });

        try
        {
            if (!repair)
            {
                StatusTextBox.AppendText("检查远程版本…" + Environment.NewLine);
                var check = await updater.CheckAsync(cancellation.Token);
                if (check.Availability != UpdateAvailability.UpdateAvailable)
                {
                    StatusTextBox.AppendText($"当前已是最新版本 {check.LatestVersion}。{Environment.NewLine}");
                    return;
                }

                if (MessageBox.Show(
                    $"发现新版本 {check.LatestVersion}，当前版本 {check.InstalledVersion}。更新前请关闭 Obsidian，是否继续？",
                    "发现知识库更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            var result = await updater.ApplyAsync(repair, progress, cancellation.Token);
            StatusTextBox.AppendText(
                repair
                    ? $"修复完成，版本 {result.InstalledVersion}。{Environment.NewLine}"
                    : $"已从 {result.PreviousVersion} 更新到 {result.InstalledVersion}。{Environment.NewLine}");
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            StatusTextBox.AppendText("操作已取消。" + Environment.NewLine);
        }
        catch (LauncherException exception)
        {
            StatusTextBox.AppendText($"失败 [{exception.ErrorCode}]：{exception.Message}{Environment.NewLine}");
            log.Write(LogLevel.Error, exception.ErrorCode, exception.Message, exception);
        }
        catch (Exception exception)
        {
            StatusTextBox.AppendText("发生未预期错误，请查看日志。" + Environment.NewLine);
            log.Write(LogLevel.Error, "KL-UNEXPECTED", "更新或修复发生未预期错误。", exception);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        OperationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy;
        OpenButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        InstallObsidianButton.IsEnabled = !busy;
        LogsButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        GC.SuppressFinalize(this);
    }
}
