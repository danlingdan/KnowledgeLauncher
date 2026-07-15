using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class FirstInstallWorkflow
{
    private readonly AppPaths _paths;
    private readonly ISettingsService _settingsService;
    private readonly PrototypeWorkflow _packagePreparer;
    private readonly ILauncherLog _log;

    public FirstInstallWorkflow(
        AppPaths paths,
        ISettingsService settingsService,
        PrototypeWorkflow packagePreparer,
        ILauncherLog log)
    {
        _paths = paths;
        _settingsService = settingsService;
        _packagePreparer = packagePreparer;
        _log = log;
    }

    public async Task<InstallationResult> InstallAsync(
        bool communityPluginsTrusted,
        IProgress<InstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!communityPluginsTrusted)
        {
            throw new LauncherException("KL6001", "必须确认信任知识库中的社区插件代码后才能安装。");
        }

        _paths.EnsureCreated();
        var currentSettings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(currentSettings.InstalledVaultPath)
            && Directory.Exists(currentSettings.InstalledVaultPath))
        {
            throw new LauncherException("KL6002", "知识库已经安装，可直接打开或在后续阶段执行更新。");
        }

        progress?.Report(new InstallationProgress(InstallationStage.CleaningStaging, "清理上次未完成的安装"));
        CleanupStaging();
        var textProgress = new Progress<string>(message =>
            progress?.Report(new InstallationProgress(MapStage(message), message)));
        var prepared = await _packagePreparer.RunAsync(textProgress, cancellationToken).ConfigureAwait(false);
        var targetPath = Path.Combine(_paths.Vault, prepared.Manifest.Id);
        if (Directory.Exists(targetPath))
        {
            throw new LauncherException("KL6003", "正式知识库目录已存在，但没有有效安装记录。请使用修复安装处理。");
        }

        var stagingRoot = Directory.GetParent(prepared.PreparedVaultPath)?.FullName
            ?? throw new LauncherException("KL6004", "无法确定知识库暂存目录。");
        var activated = false;
        try
        {
            progress?.Report(new InstallationProgress(InstallationStage.ActivatingVault, "启用已验证的知识库"));
            Directory.Move(prepared.PreparedVaultPath, targetPath);
            activated = true;

            progress?.Report(new InstallationProgress(InstallationStage.SavingSettings, "保存安装状态"));
            var installedSettings = currentSettings with
            {
                InstalledVaultId = prepared.Manifest.Id,
                InstalledVersion = prepared.Manifest.Version,
                InstalledVaultPath = targetPath,
                CommunityPluginsTrusted = true,
                InstallationState = "Installed",
                LastSuccessfulRun = DateTimeOffset.Now
            };
            await _settingsService.SaveAsync(installedSettings, cancellationToken).ConfigureAwait(false);
            TryDelete(stagingRoot);
            progress?.Report(new InstallationProgress(InstallationStage.Completed, "知识库安装完成"));
            _log.Write(LogLevel.Information, "KL-FIRST-INSTALL-SUCCEEDED", $"首次安装完成：{prepared.Manifest.Version}。");
            return new InstallationResult(
                prepared.Manifest,
                targetPath,
                prepared.InstalledPluginIds,
                RequiresVaultRegistration: true);
        }
        catch
        {
            if (activated && Directory.Exists(targetPath) && !Directory.Exists(prepared.PreparedVaultPath))
            {
                Directory.Move(targetPath, prepared.PreparedVaultPath);
            }

            TryDelete(stagingRoot);
            throw;
        }
    }

    private void CleanupStaging()
    {
        foreach (var directory in Directory.EnumerateDirectories(_paths.Staging))
        {
            TryDelete(directory);
        }

        foreach (var file in Directory.EnumerateFiles(_paths.Staging))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                _log.Write(LogLevel.Warning, "KL-STAGING-CLEANUP", $"无法删除遗留暂存文件 {Path.GetFileName(file)}。");
            }
            catch (UnauthorizedAccessException)
            {
                _log.Write(LogLevel.Warning, "KL-STAGING-CLEANUP", $"没有权限删除遗留暂存文件 {Path.GetFileName(file)}。");
            }
        }
    }

    private static InstallationStage MapStage(string message) => message switch
    {
        "检查 Obsidian" => InstallationStage.CheckingObsidian,
        "查询最新知识库版本" => InstallationStage.QueryingRelease,
        "下载发布清单" => InstallationStage.DownloadingManifest,
        "下载知识库" => InstallationStage.DownloadingVault,
        "安全解压并验证知识库" => InstallationStage.VerifyingVault,
        _ when message.StartsWith("安装锁定插件", StringComparison.Ordinal) => InstallationStage.InstallingPlugins,
        _ => InstallationStage.VerifyingVault
    };

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
            // A unique staging directory is used on the next run.
        }
        catch (UnauthorizedAccessException)
        {
            // A unique staging directory is used on the next run.
        }
    }
}
