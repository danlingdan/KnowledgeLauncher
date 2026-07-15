using System.Text.Json;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public static class ManifestLoader
{
    public static async Task<VaultManifest> LoadVaultManifestAsync(
        string filePath,
        string? vaultRoot = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadAsync<VaultManifest>(filePath, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != 1)
        {
            throw new LauncherException("KL4201", "不支持的知识库清单结构版本。");
        }

        RequireValue(manifest.Id, "id");
        RequireValue(manifest.Name, "name");
        VersionRules.Parse(manifest.Version, "知识库版本");
        VersionRules.Parse(manifest.MinimumObsidianVersion, "最低 Obsidian 版本");
        ValidateRelativePath(manifest.Entry, "entry");
        ValidateRelativePath(manifest.PluginLock, "pluginLock");
        ValidateRelativePath(manifest.UpdatePolicy, "updatePolicy");

        if (vaultRoot is not null)
        {
            RequireExistingPath(vaultRoot, manifest.Entry, "入口笔记");
            RequireExistingPath(vaultRoot, manifest.PluginLock, "插件锁定文件");
            RequireExistingPath(vaultRoot, manifest.UpdatePolicy, "更新策略文件");
        }

        return manifest;
    }

    public static async Task<PluginLockFile> LoadPluginLockAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var pluginLock = await LoadAsync<PluginLockFile>(filePath, cancellationToken).ConfigureAwait(false);
        if (pluginLock.SchemaVersion != 1)
        {
            throw new LauncherException("KL4202", "不支持的插件锁定文件结构版本。");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in pluginLock.Plugins)
        {
            RequireValue(plugin.Id, "plugins[].id");
            RequireValue(plugin.Repository, "plugins[].repository");
            VersionRules.Parse(plugin.Version, $"插件 {plugin.Id} 版本");
            VersionRules.Parse(plugin.MinimumObsidianVersion, $"插件 {plugin.Id} 最低 Obsidian 版本");
            if (!ids.Add(plugin.Id))
            {
                throw new LauncherException("KL4203", $"插件锁定文件包含重复插件：{plugin.Id}");
            }

            if (plugin.Repository.Split('/').Length != 2)
            {
                throw new LauncherException("KL4204", $"插件 {plugin.Id} 的 repository 必须是 owner/repo。");
            }

            foreach (var (name, hash) in plugin.Sha256)
            {
                if (name is not ("main.js" or "manifest.json" or "styles.css"))
                {
                    throw new LauncherException("KL4205", $"插件 {plugin.Id} 包含不支持的程序文件：{name}");
                }

                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                {
                    throw new LauncherException("KL4206", $"插件 {plugin.Id} 的 {name} 哈希格式无效。");
                }
            }

            if (!plugin.Sha256.ContainsKey("main.js") || !plugin.Sha256.ContainsKey("manifest.json"))
            {
                throw new LauncherException("KL4207", $"插件 {plugin.Id} 必须锁定 main.js 和 manifest.json。");
            }
        }

        return pluginLock;
    }

    public static async Task<UpdatePolicy> LoadUpdatePolicyAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var policy = await LoadAsync<UpdatePolicy>(filePath, cancellationToken).ConfigureAwait(false);
        if (policy.SchemaVersion != 1)
        {
            throw new LauncherException("KL4208", "不支持的更新策略结构版本。");
        }

        return policy;
    }

    public static async Task<IReadOnlyDictionary<string, string>> LoadChecksumsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("files", out var files))
        {
            root = files;
        }
        else if (root.TryGetProperty("assets", out var assets))
        {
            root = assets;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LauncherException("KL4209", "checksums.json 必须是文件名到 SHA-256 的对象。");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            var hash = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new LauncherException("KL4210", $"checksums.json 中 {property.Name} 的哈希无效。");
            }

            result.Add(property.Name, hash);
        }

        return result;
    }

    private static async Task<T> LoadAsync<T>(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonDefaults.Options,
                cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("JSON 内容为空。");
        }
        catch (JsonException exception)
        {
            throw new LauncherException("KL4211", $"无法解析 {Path.GetFileName(filePath)}。", exception);
        }
    }

    private static void RequireValue(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LauncherException("KL4212", $"清单字段 {field} 不能为空。");
        }
    }

    private static void ValidateRelativePath(string path, string field)
    {
        if (Path.IsPathRooted(path)
            || path.Split('/', '\\').Any(segment => segment is ".." or "." or ""))
        {
            throw new LauncherException("KL4213", $"清单字段 {field} 不是安全的相对路径。");
        }
    }

    private static void RequireExistingPath(string vaultRoot, string relativePath, string description)
    {
        var target = Path.Combine(vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(target))
        {
            throw new LauncherException("KL4214", $"{description}不存在：{relativePath}");
        }
    }
}
