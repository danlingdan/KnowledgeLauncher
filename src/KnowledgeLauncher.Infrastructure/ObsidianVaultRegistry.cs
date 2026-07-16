using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class ObsidianVaultRegistry
{
    private readonly string _configurationPath;

    public ObsidianVaultRegistry(string? configurationPath = null)
    {
        _configurationPath = configurationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obsidian",
            "obsidian.json");
    }

    public string? FindVaultId(string vaultPath)
    {
        var fullPath = NormalizePath(vaultPath);
        var root = LoadConfiguration(createWhenMissing: false);
        if (root?["vaults"] is not JsonObject vaults)
        {
            return null;
        }

        foreach (var vault in vaults)
        {
            if (vault.Value?["path"]?.GetValue<string>() is { Length: > 0 } registeredPath
                && string.Equals(NormalizePath(registeredPath), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return vault.Key;
            }
        }

        return null;
    }

    public string EnsureRegistered(string vaultPath)
    {
        var fullPath = NormalizePath(vaultPath);
        if (!Directory.Exists(fullPath))
        {
            throw new LauncherException("KL2002", "知识库目录不存在，无法登记到 Obsidian。");
        }

        var root = LoadConfiguration(createWhenMissing: true) ?? new JsonObject();
        var vaults = root["vaults"] as JsonObject;
        if (vaults is null)
        {
            vaults = new JsonObject();
            root["vaults"] = vaults;
        }

        foreach (var vault in vaults)
        {
            if (vault.Value?["path"]?.GetValue<string>() is { Length: > 0 } registeredPath
                && string.Equals(NormalizePath(registeredPath), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return vault.Key;
            }
        }

        var vaultId = CreateVaultId(vaults);
        vaults[vaultId] = new JsonObject
        {
            ["path"] = fullPath,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        SaveConfiguration(root);
        return vaultId;
    }

    private JsonObject? LoadConfiguration(bool createWhenMissing)
    {
        if (!File.Exists(_configurationPath))
        {
            return createWhenMissing ? new JsonObject() : null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(_configurationPath)) as JsonObject
                ?? throw new JsonException("Obsidian 配置根节点不是对象。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new LauncherException("KL2003", "无法读取 Obsidian 的 Vault 配置。", exception);
        }
    }

    private void SaveConfiguration(JsonObject root)
    {
        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new LauncherException("KL2004", "无法确定 Obsidian 配置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _configurationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temporaryPath, _configurationPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("KL2004", "无法登记 Obsidian Vault，请确认 Obsidian 已完全退出。", exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A later registration uses a unique temporary file.
            }
            catch (UnauthorizedAccessException)
            {
                // The original registration error is more useful to the user.
            }
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string CreateVaultId(JsonObject vaults)
    {
        string candidate;
        do
        {
            candidate = Guid.NewGuid().ToString("N")[..16];
        }
        while (vaults.ContainsKey(candidate));

        return candidate;
    }
}
