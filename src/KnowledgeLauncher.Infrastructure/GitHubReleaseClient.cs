using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class GitHubReleaseClient : IReleaseClient
{
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
    }

    public Task<ReleaseInfo> GetLatestAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default) =>
        GetReleaseAsync(
            $"repos/{EscapeSegment(owner)}/{EscapeSegment(repository)}/releases/latest",
            cancellationToken);

    public Task<ReleaseInfo> GetByTagAsync(
        string owner,
        string repository,
        string tag,
        CancellationToken cancellationToken = default) =>
        GetReleaseAsync(
            $"repos/{EscapeSegment(owner)}/{EscapeSegment(repository)}/releases/tags/{EscapeSegment(tag)}",
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

    private async Task<ReleaseInfo> GetReleaseAsync(string relativeUri, CancellationToken cancellationToken)
    {
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

            return new ReleaseInfo(
                dto.TagName,
                dto.Name ?? dto.TagName,
                dto.Draft,
                dto.Prerelease,
                dto.Assets.Select(asset => new ReleaseAsset(
                    asset.Name,
                    new Uri(asset.DownloadUrl, UriKind.Absolute),
                    asset.Size)).ToArray());
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

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
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

    private static string EscapeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }

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
