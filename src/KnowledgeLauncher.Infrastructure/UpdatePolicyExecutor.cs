using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public static partial class UpdatePolicyExecutor
{
    public static async Task ApplyLocalStateAsync(
        string currentVault,
        string newVault,
        UpdatePolicy policy,
        CancellationToken cancellationToken = default)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(currentVault, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(currentVault, sourcePath).Replace('\\', '/');
            var preserve = MatchesAny(relativePath, policy.Preserve);
            var merge = MatchesAny(relativePath, policy.Merge);
            if (preserve && merge)
            {
                throw new LauncherException("KL7101", $"更新策略冲突：{relativePath} 同时命中 preserve 和 merge。");
            }

            var destinationPath = Path.Combine(newVault, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (preserve)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
            else if (merge)
            {
                await MergeJsonAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static bool Matches(string relativePath, string pattern)
    {
        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        var normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');
        var regex = "^" + GlobTokenRegex().Replace(normalizedPattern, match => match.Value switch
        {
            "**/" => "(?:.*/)?",
            "**" => ".*",
            "*" => "[^/]*",
            "?" => "[^/]",
            _ => Regex.Escape(match.Value)
        }) + "$";
        return Regex.IsMatch(normalizedPath, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool MatchesAny(string relativePath, IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => Matches(relativePath, pattern));

    private static async Task MergeJsonAsync(
        string localPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var local = await ReadNodeAsync(localPath, cancellationToken).ConfigureAwait(false);
            JsonNode? upstream = File.Exists(destinationPath)
                ? await ReadNodeAsync(destinationPath, cancellationToken).ConfigureAwait(false)
                : null;
            var merged = MergeNodes(upstream, local);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(
                destinationPath,
                merged?.ToJsonString(JsonDefaults.Options) ?? "null",
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new LauncherException("KL7102", $"无法安全合并 JSON 文件：{Path.GetFileName(localPath)}", exception);
        }
    }

    private static async Task<JsonNode?> ReadNodeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode? MergeNodes(JsonNode? upstream, JsonNode? local)
    {
        if (upstream is JsonObject upstreamObject && local is JsonObject localObject)
        {
            var result = (JsonObject)upstreamObject.DeepClone();
            foreach (var property in localObject)
            {
                result[property.Key] = result.TryGetPropertyValue(property.Key, out var upstreamValue)
                    ? MergeNodes(upstreamValue, property.Value)
                    : property.Value?.DeepClone();
            }

            return result;
        }

        return local?.DeepClone() ?? upstream?.DeepClone();
    }

    [GeneratedRegex(@"\*\*/|\*\*|\*|\?|[^*?]+", RegexOptions.CultureInvariant)]
    private static partial Regex GlobTokenRegex();
}
