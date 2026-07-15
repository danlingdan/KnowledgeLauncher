using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed record PrototypeResult(
    VaultManifest Manifest,
    string PreparedVaultPath,
    IReadOnlyList<string> InstalledPluginIds,
    ObsidianInstallation Obsidian);

public sealed class PrototypeWorkflow
{
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IObsidianDetector _obsidianDetector;
    private readonly IReleaseClient _releaseClient;
    private readonly PluginInstaller _pluginInstaller;
    private readonly ILauncherLog _log;

    public PrototypeWorkflow(
        AppPaths paths,
        ISettingsService settings,
        IObsidianDetector obsidianDetector,
        IReleaseClient releaseClient,
        PluginInstaller pluginInstaller,
        ILauncherLog log)
    {
        _paths = paths;
        _settings = settings;
        _obsidianDetector = obsidianDetector;
        _releaseClient = releaseClient;
        _pluginInstaller = pluginInstaller;
        _log = log;
    }

    public async Task<PrototypeResult> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report("检查 Obsidian");
        var obsidian = _obsidianDetector.Detect();
        if (!obsidian.IsInstalled)
        {
            throw new LauncherException("KL5001", "未检测到 Obsidian。请先通过官方渠道安装后重试。");
        }

        if (obsidian.Version is null)
        {
            throw new LauncherException("KL5002", "无法读取 Obsidian 版本，不能安全判断兼容性。");
        }

        progress?.Report("查询最新知识库版本");
        var release = await _releaseClient.GetLatestAsync(
            settings.RepositoryOwner,
            settings.RepositoryName,
            cancellationToken).ConfigureAwait(false);
        var assets = SelectAssets(release);
        var stagingRoot = Path.Combine(_paths.Staging, Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(stagingRoot, "package");
        Directory.CreateDirectory(packageRoot);

        try
        {
            progress?.Report("下载发布清单");
            var externalManifestPath = Path.Combine(stagingRoot, "manifest.json");
            var checksumsPath = Path.Combine(stagingRoot, "checksums.json");
            var zipPath = Path.Combine(stagingRoot, assets.Zip.Name);
            await _releaseClient.DownloadAsync(assets.Manifest, externalManifestPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _releaseClient.DownloadAsync(assets.Checksums, checksumsPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var checksums = await ManifestLoader.LoadChecksumsAsync(checksumsPath, cancellationToken).ConfigureAwait(false);

            progress?.Report("下载知识库");
            await _releaseClient.DownloadAsync(assets.Zip, zipPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            await VerifyAssetAsync(assets.Manifest.Name, externalManifestPath, checksums, cancellationToken).ConfigureAwait(false);
            await VerifyAssetAsync(assets.Zip.Name, zipPath, checksums, cancellationToken).ConfigureAwait(false);

            var releaseManifest = await ManifestLoader.LoadVaultManifestAsync(
                externalManifestPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!VersionRules.MeetsMinimum(obsidian.Version, releaseManifest.MinimumObsidianVersion))
            {
                throw new LauncherException("KL5003", $"知识库要求 Obsidian {releaseManifest.MinimumObsidianVersion} 或更高版本。");
            }

            progress?.Report("安全解压并验证知识库");
            SecureZipExtractor.Extract(zipPath, packageRoot);
            var embeddedManifestPath = Path.Combine(packageRoot, "launcher", "manifest.json");
            var embeddedManifest = await ManifestLoader.LoadVaultManifestAsync(
                embeddedManifestPath,
                packageRoot,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(releaseManifest.Id, embeddedManifest.Id, StringComparison.Ordinal)
                || !string.Equals(releaseManifest.Version, embeddedManifest.Version, StringComparison.Ordinal))
            {
                throw new LauncherException("KL5004", "Release 清单与知识库包内清单不一致。");
            }

            var pluginLockPath = ResolveSafePackagePath(packageRoot, embeddedManifest.PluginLock);
            var pluginLock = await ManifestLoader.LoadPluginLockAsync(pluginLockPath, cancellationToken).ConfigureAwait(false);
            var installedPlugins = new List<string>(pluginLock.Plugins.Count);
            foreach (var plugin in pluginLock.Plugins)
            {
                progress?.Report($"安装锁定插件 {plugin.Id} {plugin.Version}");
                await _pluginInstaller.InstallAsync(
                    plugin,
                    packageRoot,
                    obsidian.Version,
                    cancellationToken).ConfigureAwait(false);
                installedPlugins.Add(plugin.Id);
            }

            progress?.Report("技术验证链路完成");
            _log.Write(LogLevel.Information, "KL-PROTOTYPE-SUCCEEDED", $"技术验证完成：{embeddedManifest.Id} {embeddedManifest.Version}。");
            return new PrototypeResult(embeddedManifest, packageRoot, installedPlugins, obsidian);
        }
        catch
        {
            TryDelete(stagingRoot);
            throw;
        }
    }

    private static (ReleaseAsset Zip, ReleaseAsset Manifest, ReleaseAsset Checksums) SelectAssets(ReleaseInfo release)
    {
        var zipCandidates = release.Assets.Where(asset =>
            asset.Name.StartsWith("knowledge-", StringComparison.OrdinalIgnoreCase)
            && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (zipCandidates.Length != 1)
        {
            throw new LauncherException("KL5006", "Release 必须且只能包含一个 knowledge-*.zip 资产。");
        }

        var manifest = FindSingleAsset(release, "manifest.json");
        var checksums = FindSingleAsset(release, "checksums.json");
        return (zipCandidates[0], manifest, checksums);
    }

    private static ReleaseAsset FindSingleAsset(ReleaseInfo release, string name)
    {
        var matches = release.Assets.Where(asset =>
            string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new LauncherException("KL5007", $"Release 必须且只能包含一个 {name} 资产。");
    }

    private static async Task VerifyAssetAsync(
        string assetName,
        string filePath,
        IReadOnlyDictionary<string, string> checksums,
        CancellationToken cancellationToken)
    {
        if (!checksums.TryGetValue(assetName, out var expected))
        {
            throw new LauncherException("KL5008", $"checksums.json 缺少 {assetName}。");
        }

        await HashVerifier.VerifySha256Async(filePath, expected, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveSafePackagePath(string packageRoot, string relativePath)
    {
        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException("KL5009", "清单引用超出知识库目录。");
        }

        return resolved;
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
            // The next run uses a new staging directory and can safely continue.
        }
        catch (UnauthorizedAccessException)
        {
            // The next run uses a new staging directory and can safely continue.
        }
    }
}
