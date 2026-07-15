using System.Text.Json;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class PluginInstaller
{
    private static readonly string[] RequiredFiles = ["manifest.json", "main.js"];
    private readonly IReleaseClient _releaseClient;
    private readonly ILauncherLog _log;

    public PluginInstaller(IReleaseClient releaseClient, ILauncherLog log)
    {
        _releaseClient = releaseClient;
        _log = log;
    }

    public async Task InstallAsync(
        PluginLockEntry plugin,
        string vaultRoot,
        Version obsidianVersion,
        CancellationToken cancellationToken = default)
    {
        if (!VersionRules.MeetsMinimum(obsidianVersion, plugin.MinimumObsidianVersion))
        {
            throw new LauncherException("KL4301", $"插件 {plugin.Id} 要求 Obsidian {plugin.MinimumObsidianVersion} 或更高版本。");
        }

        var repositoryParts = plugin.Repository.Split('/');
        var release = await FindReleaseAsync(
            repositoryParts[0],
            repositoryParts[1],
            plugin.Version,
            cancellationToken).ConfigureAwait(false);
        var pluginDirectory = Path.Combine(vaultRoot, ".obsidian", "plugins", plugin.Id);
        Directory.CreateDirectory(pluginDirectory);

        foreach (var fileName in plugin.Sha256.Keys)
        {
            var asset = release.Assets.SingleOrDefault(
                candidate => string.Equals(candidate.Name, fileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new LauncherException("KL4302", $"插件 {plugin.Id} 的 Release 缺少 {fileName}。");
            var destination = Path.Combine(pluginDirectory, fileName);
            await _releaseClient.DownloadAsync(asset, destination, cancellationToken: cancellationToken).ConfigureAwait(false);
            await HashVerifier.VerifySha256Async(
                destination,
                plugin.Sha256[fileName],
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var required in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(pluginDirectory, required)))
            {
                throw new LauncherException("KL4303", $"插件 {plugin.Id} 安装后缺少 {required}。");
            }
        }

        await EnablePluginAsync(vaultRoot, plugin.Id, cancellationToken).ConfigureAwait(false);
        _log.Write(LogLevel.Information, "KL-PLUGIN-INSTALLED", $"已安装插件 {plugin.Id} {plugin.Version}。");
    }

    private async Task<ReleaseInfo> FindReleaseAsync(
        string owner,
        string repository,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _releaseClient.GetByTagAsync(owner, repository, version, cancellationToken).ConfigureAwait(false);
        }
        catch (LauncherException exception) when (exception.ErrorCode == "KL3004" && !version.StartsWith('v'))
        {
            return await _releaseClient.GetByTagAsync(owner, repository, "v" + version, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnablePluginAsync(
        string vaultRoot,
        string pluginId,
        CancellationToken cancellationToken)
    {
        var obsidianDirectory = Path.Combine(vaultRoot, ".obsidian");
        Directory.CreateDirectory(obsidianDirectory);
        var filePath = Path.Combine(obsidianDirectory, "community-plugins.json");
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(filePath))
        {
            await using var source = File.OpenRead(filePath);
            var existing = await JsonSerializer.DeserializeAsync<string[]>(
                source,
                JsonDefaults.Options,
                cancellationToken).ConfigureAwait(false) ?? [];
            enabled.UnionWith(existing);
        }

        enabled.Add(pluginId);
        await using var destination = File.Create(filePath);
        await JsonSerializer.SerializeAsync(
            destination,
            enabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            JsonDefaults.Options,
            cancellationToken).ConfigureAwait(false);
    }
}
