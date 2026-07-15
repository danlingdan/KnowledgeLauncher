using System.IO.Compression;
using System.Text.Json;
using KnowledgeLauncher.Core;
using KnowledgeLauncher.Infrastructure;

namespace KnowledgeLauncher.IntegrationTests;

public sealed class PrototypeWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KnowledgeLauncherIntegration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Workflow_PreparesVerifiedVaultAndInstallsLockedPlugin()
    {
        Directory.CreateDirectory(_root);
        var fixture = await CreateFixtureAsync();
        var paths = new AppPaths(Path.Combine(_root, "appdata"));
        var log = new NullLog();
        var settings = new SettingsService(paths, log);
        var releaseClient = new FixtureReleaseClient(fixture.KnowledgeRelease, fixture.PluginRelease, fixture.AssetSources);
        var workflow = new PrototypeWorkflow(
            paths,
            settings,
            new InstalledObsidianDetector(),
            releaseClient,
            new PluginInstaller(releaseClient, log),
            log);

        var result = await workflow.RunAsync();

        Assert.Equal("computer-knowledge-base", result.Manifest.Id);
        Assert.Contains("test-plugin", result.InstalledPluginIds);
        Assert.True(File.Exists(Path.Combine(result.PreparedVaultPath, "首页.md")));
        Assert.True(File.Exists(Path.Combine(result.PreparedVaultPath, ".obsidian", "plugins", "test-plugin", "main.js")));
        var enabledJson = await File.ReadAllTextAsync(Path.Combine(result.PreparedVaultPath, ".obsidian", "community-plugins.json"));
        Assert.Contains("test-plugin", enabledJson, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Vault));
    }

    [Fact]
    public async Task FirstInstall_ActivatesVaultAndPersistsInstalledState()
    {
        Directory.CreateDirectory(_root);
        var fixture = await CreateFixtureAsync();
        var paths = new AppPaths(Path.Combine(_root, "first-install-appdata"));
        var log = new NullLog();
        var settings = new SettingsService(paths, log);
        var releaseClient = new FixtureReleaseClient(fixture.KnowledgeRelease, fixture.PluginRelease, fixture.AssetSources);
        var preparer = new PrototypeWorkflow(
            paths,
            settings,
            new InstalledObsidianDetector(),
            releaseClient,
            new PluginInstaller(releaseClient, log),
            log);
        var workflow = new FirstInstallWorkflow(paths, settings, preparer, log);

        var result = await workflow.InstallAsync(communityPluginsTrusted: true);
        var saved = await settings.LoadAsync();

        Assert.Equal(Path.Combine(paths.Vault, "computer-knowledge-base"), result.VaultPath);
        Assert.True(File.Exists(Path.Combine(result.VaultPath, "首页.md")));
        Assert.Equal("Installed", saved.InstallationState);
        Assert.Equal("2026.7.16.1", saved.InstalledVersion);
        Assert.Equal(result.VaultPath, saved.InstalledVaultPath);
        Assert.True(saved.CommunityPluginsTrusted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Fact]
    public async Task FirstInstall_RollsBackActivatedDirectoryWhenSettingsSaveFails()
    {
        Directory.CreateDirectory(_root);
        var fixture = await CreateFixtureAsync();
        var paths = new AppPaths(Path.Combine(_root, "rollback-appdata"));
        var log = new NullLog();
        var settings = new FailingSaveSettingsService();
        var releaseClient = new FixtureReleaseClient(fixture.KnowledgeRelease, fixture.PluginRelease, fixture.AssetSources);
        var preparer = new PrototypeWorkflow(
            paths,
            settings,
            new InstalledObsidianDetector(),
            releaseClient,
            new PluginInstaller(releaseClient, log),
            log);
        var workflow = new FirstInstallWorkflow(paths, settings, preparer, log);

        await Assert.ThrowsAsync<IOException>(() => workflow.InstallAsync(communityPluginsTrusted: true));

        Assert.False(Directory.Exists(Path.Combine(paths.Vault, "computer-knowledge-base")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    [Fact]
    public async Task Update_CreatesBackupAndPreservesAndMergesLocalState()
    {
        Directory.CreateDirectory(_root);
        var initialFixture = await CreateFixtureAsync("2026.7.16.1");
        var paths = new AppPaths(Path.Combine(_root, "update-appdata"));
        var log = new NullLog();
        var settings = new SettingsService(paths, log);
        var initialClient = new FixtureReleaseClient(initialFixture.KnowledgeRelease, initialFixture.PluginRelease, initialFixture.AssetSources);
        var initialPreparer = new PrototypeWorkflow(
            paths, settings, new InstalledObsidianDetector(), initialClient, new PluginInstaller(initialClient, log), log);
        var installed = await new FirstInstallWorkflow(paths, settings, initialPreparer, log)
            .InstallAsync(communityPluginsTrusted: true);
        var obsidianDirectory = Path.Combine(installed.VaultPath, ".obsidian");
        await File.WriteAllTextAsync(Path.Combine(obsidianDirectory, "workspace.json"), "{\"localWorkspace\":true}");
        await File.WriteAllTextAsync(Path.Combine(obsidianDirectory, "app.json"), "{\"local\":true,\"theme\":\"local\"}");

        var updateFixture = await CreateFixtureAsync("2026.7.17.1");
        var updateClient = new FixtureReleaseClient(updateFixture.KnowledgeRelease, updateFixture.PluginRelease, updateFixture.AssetSources);
        var updatePreparer = new PrototypeWorkflow(
            paths, settings, new InstalledObsidianDetector(), updateClient, new PluginInstaller(updateClient, log), log);
        var updater = new VaultUpdateService(
            paths, settings, updateClient, updatePreparer, new NotRunningUsageDetector(), log);

        var result = await updater.ApplyAsync(repair: false);
        var saved = await settings.LoadAsync();
        var mergedApp = await File.ReadAllTextAsync(Path.Combine(obsidianDirectory, "app.json"));

        Assert.Equal("2026.7.17.1", result.InstalledVersion);
        Assert.Equal("2026.7.17.1", saved.InstalledVersion);
        Assert.True(Directory.Exists(result.BackupPath));
        Assert.True(File.Exists(Path.Combine(result.BackupPath, "首页.md")));
        Assert.True(File.Exists(Path.Combine(obsidianDirectory, "workspace.json")));
        Assert.Contains("\"upstream\": true", mergedApp, StringComparison.Ordinal);
        Assert.Contains("\"local\": true", mergedApp, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"local\"", mergedApp, StringComparison.Ordinal);
    }

    private async Task<Fixture> CreateFixtureAsync(string version = "2026.7.16.1")
    {
        var source = Path.Combine(_root, "fixture-" + version.Replace('.', '-'));
        var vault = Path.Combine(source, "vault");
        var launcher = Path.Combine(vault, "launcher");
        Directory.CreateDirectory(launcher);
        await File.WriteAllTextAsync(Path.Combine(vault, "首页.md"), "# 测试首页");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidian"));
        await File.WriteAllTextAsync(Path.Combine(vault, ".obsidian", "app.json"), "{\"upstream\":true,\"theme\":\"upstream\"}");

        var pluginAssets = Path.Combine(source, "plugin-assets");
        Directory.CreateDirectory(pluginAssets);
        var mainJs = Path.Combine(pluginAssets, "main.js");
        var pluginManifest = Path.Combine(pluginAssets, "manifest.json");
        await File.WriteAllTextAsync(mainJs, "module.exports = {};");
        await File.WriteAllTextAsync(pluginManifest, "{\"id\":\"test-plugin\",\"name\":\"Test\",\"version\":\"1.0.0\",\"minAppVersion\":\"1.0.0\"}");
        var mainHash = await HashVerifier.ComputeSha256Async(mainJs);
        var manifestHash = await HashVerifier.ComputeSha256Async(pluginManifest);

        var vaultManifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "computer-knowledge-base",
              "name": "计算机知识库",
              "version": "{{version}}",
              "entry": "首页.md",
              "minimumObsidianVersion": "1.0.0",
              "pluginLock": "launcher/plugin-lock.json",
              "updatePolicy": "launcher/update-policy.json"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(launcher, "manifest.json"), vaultManifestJson);
        await File.WriteAllTextAsync(Path.Combine(launcher, "plugin-lock.json"), $$"""
            {
              "schemaVersion": 1,
              "plugins": [{
                "id": "test-plugin",
                "version": "1.0.0",
                "repository": "owner/test-plugin",
                "minimumObsidianVersion": "1.0.0",
                "sha256": {
                  "main.js": "{{mainHash}}",
                  "manifest.json": "{{manifestHash}}"
                }
              }]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(launcher, "update-policy.json"),
            "{\"schemaVersion\":1,\"preserve\":[\".obsidian/workspace.json\"],\"merge\":[\".obsidian/app.json\"]}");

        var zip = Path.Combine(source, $"knowledge-v{version}.zip");
        ZipFile.CreateFromDirectory(vault, zip);
        var externalManifest = Path.Combine(source, "manifest.json");
        await File.WriteAllTextAsync(externalManifest, vaultManifestJson);
        var zipHash = await HashVerifier.ComputeSha256Async(zip);
        var externalManifestHash = await HashVerifier.ComputeSha256Async(externalManifest);
        var checksums = Path.Combine(source, "checksums.json");
        await File.WriteAllTextAsync(checksums, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [Path.GetFileName(zip)] = zipHash,
            ["manifest.json"] = externalManifestHash
        }));

        var knowledgeRelease = new ReleaseInfo("v" + version, "test", false, false,
        [
            Asset(Path.GetFileName(zip)),
            Asset("manifest.json"),
            Asset("checksums.json")
        ]);
        var pluginRelease = new ReleaseInfo("1.0.0", "plugin", false, false,
        [
            Asset("main.js"),
            Asset("manifest.json")
        ]);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFileName(zip)] = zip,
            ["checksums.json"] = checksums,
            ["knowledge:manifest.json"] = externalManifest,
            ["plugin:main.js"] = mainJs,
            ["plugin:manifest.json"] = pluginManifest
        };
        return new Fixture(knowledgeRelease, pluginRelease, sources);

        static ReleaseAsset Asset(string name) => new(name, new Uri($"https://example.invalid/{Uri.EscapeDataString(name)}"), 1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed record Fixture(
        ReleaseInfo KnowledgeRelease,
        ReleaseInfo PluginRelease,
        IReadOnlyDictionary<string, string> AssetSources);

    private sealed class FixtureReleaseClient : IReleaseClient
    {
        private readonly ReleaseInfo _knowledgeRelease;
        private readonly ReleaseInfo _pluginRelease;
        private readonly IReadOnlyDictionary<string, string> _sources;
        private bool _pluginContext;

        public FixtureReleaseClient(
            ReleaseInfo knowledgeRelease,
            ReleaseInfo pluginRelease,
            IReadOnlyDictionary<string, string> sources)
        {
            _knowledgeRelease = knowledgeRelease;
            _pluginRelease = pluginRelease;
            _sources = sources;
        }

        public Task<ReleaseInfo> GetLatestAsync(string owner, string repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(_knowledgeRelease);

        public Task<ReleaseInfo> GetByTagAsync(string owner, string repository, string tag, CancellationToken cancellationToken = default)
        {
            _pluginContext = true;
            return Task.FromResult(_pluginRelease);
        }

        public Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var key = asset.Name;
            if (asset.Name == "manifest.json")
            {
                key = _pluginContext ? "plugin:manifest.json" : "knowledge:manifest.json";
            }
            else if (_pluginContext)
            {
                key = "plugin:" + asset.Name;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(_sources[key], destinationPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    private sealed class InstalledObsidianDetector : IObsidianDetector
    {
        public ObsidianInstallation Detect() => new(true, "Obsidian.exe", new Version(1, 11, 0), true, null);
    }

    private sealed class NotRunningUsageDetector : IVaultUsageDetector
    {
        public bool IsObsidianRunning() => false;
    }

    private sealed class NullLog : ILauncherLog
    {
        public void Write(LogLevel level, string eventId, string message, Exception? exception = null)
        {
        }
    }

    private sealed class FailingSaveSettingsService : ISettingsService
    {
        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LauncherSettings());

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Simulated settings failure."));
    }
}
