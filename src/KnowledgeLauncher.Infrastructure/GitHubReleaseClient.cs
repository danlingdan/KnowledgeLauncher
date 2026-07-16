using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed partial class GitHubReleaseClient : IReleaseClient
{
    private static readonly TimeSpan ReleaseCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, CachedRelease> ReleaseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly ILauncherLog _log;

    public GitHubReleaseClient(HttpClient httpClient, ILauncherLog log)
    {
        _httpClient = httpClient;
        _log = log;
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("KnowledgeLauncher", "0.1"));
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!_httpClient.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    public Task<ReleaseInfo> GetLatestAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default) =>
        GetReleaseAsync(
            owner,
            repository,
            tag: null,
            cancellationToken);

    public Task<ReleaseInfo> GetByTagAsync(
        string owner,
        string repository,
        string tag,
        CancellationToken cancellationToken = default) =>
        GetReleaseAsync(
            owner,
            repository,
            tag,
            cancellationToken);

    public async Task DownloadAsync(
        ReleaseAsset asset,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
            ?? throw new ArgumentException("下载目标必须包含父目录。", nameof(destinationPath));
        Directory.CreateDirectory(parent);
        var partialPath = destinationPath + ".partial";

        try
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long received = 0;
                var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(asset.Name, received, total));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, destinationPath, overwrite: true);
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }
    }

    private async Task<ReleaseInfo> GetReleaseAsync(
        string owner,
        string repository,
        string? tag,
        CancellationToken cancellationToken)
    {
        var relativeUri = tag is null
            ? $"repos/{EscapeSegment(owner)}/{EscapeSegment(repository)}/releases/latest"
            : $"repos/{EscapeSegment(owner)}/{EscapeSegment(repository)}/releases/tags/{EscapeSegment(tag)}";
        var cacheKey = relativeUri;
        if (ReleaseCache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.StoredAt < ReleaseCacheLifetime)
        {
            return cached.Release;
        }

        try
        {
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, relativeUri),
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<ReleaseDto>(
                stream,
                JsonDefaults.Options,
                cancellationToken).ConfigureAwait(false)
                ?? throw new LauncherException("KL3002", "GitHub 返回了空的 Release 信息。");

            var release = new ReleaseInfo(
                dto.TagName,
                dto.Name ?? dto.TagName,
                dto.Draft,
                dto.Prerelease,
                dto.Assets.Select(asset => new ReleaseAsset(
                    asset.Name,
                    new Uri(asset.DownloadUrl, UriKind.Absolute),
                    asset.Size)).ToArray());
            ReleaseCache[cacheKey] = new CachedRelease(release, DateTimeOffset.UtcNow);
            return release;
        }
        catch (LauncherException exception) when (exception.ErrorCode == "KL3005")
        {
            _log.Write(
                LogLevel.Warning,
                "KL-GITHUB-WEB-FALLBACK",
                "GitHub REST API 已限流，改用公开 Release 页面读取版本信息。",
                exception);
            var release = await GetReleaseFromWebAsync(owner, repository, tag, cancellationToken).ConfigureAwait(false);
            ReleaseCache[cacheKey] = new CachedRelease(release, DateTimeOffset.UtcNow);
            return release;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new LauncherException("KL3001", "无法读取 GitHub Release 信息。", exception);
        }
    }

    private async Task<ReleaseInfo> GetReleaseFromWebAsync(
        string owner,
        string repository,
        string? requestedTag,
        CancellationToken cancellationToken)
    {
        var repositoryUrl = $"https://github.com/{EscapeSegment(owner)}/{EscapeSegment(repository)}";
        var tag = requestedTag;
        if (tag is null)
        {
            using var latestResponse = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"{repositoryUrl}/releases/latest"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var finalUri = latestResponse.RequestMessage?.RequestUri;
            tag = GetTagFromReleaseUri(finalUri)
                ?? throw new LauncherException("KL3006", "GitHub 最新 Release 页面没有返回有效的版本标签。");
        }

        using var assetsResponse = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{repositoryUrl}/releases/expanded_assets/{EscapeSegment(tag)}"),
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var html = await assetsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var expectedPrefix = $"/{owner}/{repository}/releases/download/";
        var assets = AssetLinkRegex().Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups["href"].Value))
            .Select(href => Uri.TryCreate(new Uri("https://github.com/"), href, out var uri) ? uri : null)
            .Where(uri => uri is not null
                && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(uri => new ReleaseAsset(
                Uri.UnescapeDataString(uri!.Segments[^1]),
                uri,
                0))
            .DistinctBy(asset => asset.DownloadUrl)
            .ToArray();

        if (assets.Length == 0)
        {
            throw new LauncherException("KL3006", "无法从 GitHub Release 页面读取下载资产。");
        }

        return new ReleaseInfo(tag, tag, false, false, assets);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = requestFactory();
            var response = await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                throw new LauncherException("KL3004", "仓库尚未发布符合要求的 GitHub Release。");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var message = BuildRateLimitMessage(response);
                response.Dispose();
                throw new LauncherException("KL3005", message);
            }

            var retryable = (int)response.StatusCode >= 500;
            if (!retryable || attempt == 3)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new LauncherException("KL3003", $"GitHub 请求失败，HTTP 状态码 {status}。");
            }

            response.Dispose();
            _log.Write(LogLevel.Warning, "KL-GITHUB-RETRY", $"GitHub 请求失败，准备第 {attempt + 1} 次尝试。");
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("重试循环意外结束。");
    }

    private static string BuildRateLimitMessage(HttpResponseMessage response)
    {
        var exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            && string.Equals(remainingValues.FirstOrDefault(), "0", StringComparison.Ordinal);
        if (exhausted
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out var resetSeconds))
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();
            return $"GitHub API 请求额度已用尽，将在 {reset:yyyy-MM-dd HH:mm:ss} 后恢复。";
        }

        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return $"GitHub 暂时限制了请求，请在约 {Math.Ceiling(retryAfter.TotalMinutes)} 分钟后重试。";
        }

        return "GitHub 暂时限制了请求，启动器将尝试通过公开 Release 页面继续。";
    }

    private static string? GetTagFromReleaseUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        const string marker = "/releases/tag/";
        var markerIndex = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var encodedTag = uri.AbsolutePath[(markerIndex + marker.Length)..];
        return string.IsNullOrWhiteSpace(encodedTag) ? null : Uri.UnescapeDataString(encodedTag);
    }

    private static string EscapeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }

    [GeneratedRegex("href\\s*=\\s*[\\\"'](?<href>[^\\\"']+/releases/download/[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex AssetLinkRegex();

    private sealed record CachedRelease(ReleaseInfo Release, DateTimeOffset StoredAt);

    private sealed record ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public required string TagName { get; init; }
        public string? Name { get; init; }
        public bool Draft { get; init; }
        public bool Prerelease { get; init; }
        public IReadOnlyList<AssetDto> Assets { get; init; } = [];
    }

    private sealed record AssetDto
    {
        public required string Name { get; init; }
        [JsonPropertyName("browser_download_url")]
        public required string DownloadUrl { get; init; }
        public long Size { get; init; }
    }
}
