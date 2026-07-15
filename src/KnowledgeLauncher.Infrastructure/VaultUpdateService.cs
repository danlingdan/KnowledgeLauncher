using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class VaultUpdateService
{
    private readonly AppPaths _paths;
    private readonly ISettingsService _settingsService;
    private readonly IReleaseClient _releaseClient;
    private readonly PrototypeWorkflow _packagePreparer;
    private readonly IVaultUsageDetector _usageDetector;
    private readonly ILauncherLog _log;

    public VaultUpdateService(
        AppPaths paths,
        ISettingsService settingsService,
        IReleaseClient releaseClient,
        PrototypeWorkflow packagePreparer,
        IVaultUsageDetector usageDetector,
        ILauncherLog log)
    {
        _paths = paths;
        _settingsService = settingsService;
        _releaseClient = releaseClient;
        _packagePreparer = packagePreparer;
        _usageDetector = usageDetector;
        _log = log;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var release = await _releaseClient.GetLatestAsync(
            settings.RepositoryOwner,
            settings.RepositoryName,
            cancellationToken).ConfigureAwait(false);
        var manifestAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new LauncherException("KL7201", "最新 Release 缺少唯一的 manifest.json。");
        var temporaryPath = Path.Combine(_paths.Cache, $"manifest-{Guid.NewGuid():N}.json");
        _paths.EnsureCreated();
        try
        {
            await _releaseClient.DownloadAsync(manifestAsset, temporaryPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var manifest = await ManifestLoader.LoadVaultManifestAsync(temporaryPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.InstalledVersion)
                || string.IsNullOrWhiteSpace(settings.InstalledVaultPath)
                || !Directory.Exists(settings.InstalledVaultPath))
            {
                return new UpdateCheckResult(UpdateAvailability.NotInstalled, settings.InstalledVersion, manifest.Version);
            }

            var installed = VersionRules.Parse(settings.InstalledVersion, "已安装版本");
            var latest = VersionRules.Parse(manifest.Version, "最新版本");
            return new UpdateCheckResult(
                latest > installed ? UpdateAvailability.UpdateAvailable : UpdateAvailability.UpToDate,
                settings.InstalledVersion,
                manifest.Version);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public async Task<VaultUpdateResult> ApplyAsync(
        bool repair,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.InstalledVaultPath is not { Length: > 0 } currentVault
            || settings.InstalledVersion is not { Length: > 0 } currentVersion
            || !Directory.Exists(currentVault))
        {
            throw new LauncherException("KL7202", "没有可更新或修复的已安装知识库。");
        }

        if (_usageDetector.IsObsidianRunning())
        {
            throw new LauncherException("KL7203", "请先关闭 Obsidian，再执行更新或修复安装。");
        }

        progress?.Report(repair ? "下载干净的修复包" : "准备最新知识库版本");
        var prepared = await _packagePreparer.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        var stagingRoot = Directory.GetParent(prepared.PreparedVaultPath)?.FullName
            ?? throw new LauncherException("KL7204", "无法确定更新暂存目录。");
        var backupPath = Path.Combine(_paths.Backup, prepared.Manifest.Id);
        var oldMoved = false;
        var newActivated = false;
        try
        {
            if (!string.Equals(settings.InstalledVaultId, prepared.Manifest.Id, StringComparison.Ordinal))
            {
                throw new LauncherException("KL7205", "下载的知识库 ID 与已安装知识库不一致。");
            }

            var oldVersion = VersionRules.Parse(currentVersion, "已安装版本");
            var newVersion = VersionRules.Parse(prepared.Manifest.Version, "下载版本");
            if (!repair && newVersion <= oldVersion)
            {
                throw new LauncherException("KL7206", "当前知识库已经是最新版本。");
            }

            progress?.Report("保留并合并本地状态");
            var policyPath = Path.Combine(
                prepared.PreparedVaultPath,
                prepared.Manifest.UpdatePolicy.Replace('/', Path.DirectorySeparatorChar));
            var policy = await ManifestLoader.LoadUpdatePolicyAsync(policyPath, cancellationToken).ConfigureAwait(false);
            await UpdatePolicyExecutor.ApplyLocalStateAsync(
                currentVault,
                prepared.PreparedVaultPath,
                policy,
                cancellationToken).ConfigureAwait(false);
            await ManifestLoader.LoadVaultManifestAsync(
                Path.Combine(prepared.PreparedVaultPath, "launcher", "manifest.json"),
                prepared.PreparedVaultPath,
                cancellationToken).ConfigureAwait(false);

            progress?.Report("备份当前可用版本");
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }

            Directory.Move(currentVault, backupPath);
            oldMoved = true;
            progress?.Report(repair ? "启用修复后的知识库" : "启用新版本");
            Directory.Move(prepared.PreparedVaultPath, currentVault);
            newActivated = true;

            var updatedSettings = settings with
            {
                InstalledVersion = prepared.Manifest.Version,
                InstallationState = "Installed",
                LastSuccessfulRun = DateTimeOffset.Now
            };
            await _settingsService.SaveAsync(updatedSettings, cancellationToken).ConfigureAwait(false);
            TryDelete(stagingRoot);
            _log.Write(LogLevel.Information, repair ? "KL-REPAIR-SUCCEEDED" : "KL-UPDATE-SUCCEEDED", $"知识库已安装版本 {prepared.Manifest.Version}。");
            return new VaultUpdateResult(currentVersion, prepared.Manifest.Version, currentVault, backupPath, repair);
        }
        catch
        {
            if (newActivated && Directory.Exists(currentVault))
            {
                Directory.Move(currentVault, prepared.PreparedVaultPath);
            }

            if (oldMoved && Directory.Exists(backupPath) && !Directory.Exists(currentVault))
            {
                Directory.Move(backupPath, currentVault);
            }

            TryDelete(stagingRoot);
            throw;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Cleanup can be retried by the next operation.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup can be retried by the next operation.
        }
    }
}
