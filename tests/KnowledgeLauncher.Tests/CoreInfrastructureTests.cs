using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using KnowledgeLauncher.Core;
using KnowledgeLauncher.Infrastructure;

namespace KnowledgeLauncher.Tests;

public sealed class CoreInfrastructureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KnowledgeLauncherTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void VersionRules_HandlesVPrefixAndMinimum()
    {
        var actual = VersionRules.Parse("v1.11.2", "version");
        Assert.True(VersionRules.MeetsMinimum(actual, "1.11.0"));
        Assert.Throws<LauncherException>(() => VersionRules.Parse("latest", "version"));
    }

    [Fact]
    public void ObsidianLauncher_EncodesChineseAndSpaces()
    {
        var uri = new ObsidianLauncher().BuildOpenUri("计算机 知识库", "首页 入口.md");
        Assert.Equal("obsidian", uri.Scheme);
        Assert.Contains("%E8%AE%A1%E7%AE%97%E6%9C%BA%20%E7%9F%A5%E8%AF%86%E5%BA%93", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("%E9%A6%96%E9%A1%B5%20%E5%85%A5%E5%8F%A3.md", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ObsidianLauncher_BuildsAbsolutePathUriForFirstRegistration()
    {
        var absolutePath = Path.GetFullPath(Path.Combine(_root, "计算机 知识库", "首页.md"));
        var uri = new ObsidianLauncher().BuildOpenPathUri(absolutePath);

        Assert.Equal("obsidian", uri.Scheme);
        Assert.StartsWith("obsidian://open?path=", uri.OriginalString, StringComparison.Ordinal);
        Assert.Contains("%E8%AE%A1%E7%AE%97%E6%9C%BA%20%E7%9F%A5%E8%AF%86%E5%BA%93", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ObsidianVaultRegistry_RegistersAndFindsVaultByExactPath()
    {
        var vaultPath = Path.Combine(_root, "computer-knowledge-base");
        var configurationPath = Path.Combine(_root, "obsidian", "obsidian.json");
        Directory.CreateDirectory(vaultPath);
        var registry = new ObsidianVaultRegistry(configurationPath);

        var vaultId = registry.EnsureRegistered(vaultPath);

        Assert.Equal(16, vaultId.Length);
        Assert.Equal(vaultId, registry.FindVaultId(vaultPath));
        using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));
        Assert.Equal(
            Path.GetFullPath(vaultPath),
            configuration.RootElement.GetProperty("vaults").GetProperty(vaultId).GetProperty("path").GetString());
    }

    [Fact]
    public void ObsidianVaultRegistry_PreservesExistingConfigurationAndVaultId()
    {
        var vaultPath = Path.Combine(_root, "ComputerKnowledgeBase");
        var configurationPath = Path.Combine(_root, "obsidian.json");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(new
        {
            vaults = new Dictionary<string, object>
            {
                ["existing-vault"] = new { path = vaultPath, ts = 1 }
            },
            cli = true
        }));
        var registry = new ObsidianVaultRegistry(configurationPath);

        var vaultId = registry.EnsureRegistered(vaultPath.ToUpperInvariant());

        Assert.Equal("existing-vault", vaultId);
        using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));
        Assert.True(configuration.RootElement.GetProperty("cli").GetBoolean());
    }

    [Fact]
    public async Task SettingsService_RecoversCorruptConfiguration()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, "{ broken");
        var service = new SettingsService(paths, new NullLog());

        var settings = await service.LoadAsync();

        Assert.Equal("danlingdan", settings.RepositoryOwner);
        Assert.Single(Directory.GetFiles(paths.App, "launcher.json.corrupt-*"));
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task ManifestLoader_RejectsUnsafeRelativePath()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "manifest.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "id": "vault",
              "name": "Vault",
              "version": "1.0.0",
              "entry": "../secret.md",
              "minimumObsidianVersion": "1.0.0"
            }
            """);

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => ManifestLoader.LoadVaultManifestAsync(path));
        Assert.Equal("KL4213", exception.ErrorCode);
    }

    [Fact]
    public void SecureZipExtractor_RejectsZipSlip()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        var exception = Assert.Throws<LauncherException>(
            () => SecureZipExtractor.Extract(archivePath, Path.Combine(_root, "out")));
        Assert.Equal("KL4104", exception.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task HashVerifier_DetectsTampering()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "asset.bin");
        await File.WriteAllTextAsync(path, "original");
        var hash = await HashVerifier.ComputeSha256Async(path);
        await HashVerifier.VerifySha256Async(path, hash);
        await File.WriteAllTextAsync(path, "modified");

        var exception = await Assert.ThrowsAsync<LauncherException>(
            () => HashVerifier.VerifySha256Async(path, hash));
        Assert.Equal("KL4002", exception.ErrorCode);
    }

    [Fact]
    public async Task GitHubReleaseClient_DownloadClosesPartialFileBeforeActivatingIt()
    {
        Directory.CreateDirectory(_root);
        var content = "downloaded content";
        using var httpClient = new HttpClient(new StaticResponseHandler(content));
        var client = new GitHubReleaseClient(httpClient, new NullLog());
        var destination = Path.Combine(_root, "asset.bin");
        var asset = new ReleaseAsset(
            "asset.bin",
            new Uri("https://example.invalid/asset.bin"),
            content.Length);

        await client.DownloadAsync(asset, destination);

        Assert.Equal(content, await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task GitHubReleaseClient_FallsBackToPublicReleasePageWhenApiIsRateLimited()
    {
        var handler = new RateLimitedReleaseHandler();
        using var httpClient = new HttpClient(handler);
        var client = new GitHubReleaseClient(httpClient, new NullLog());

        var release = await client.GetLatestAsync("fallback-owner", "fallback-repository");
        var cachedRelease = await client.GetLatestAsync("fallback-owner", "fallback-repository");

        Assert.Equal("vault-v1.2.3", release.TagName);
        Assert.Same(release, cachedRelease);
        Assert.Equal(1, handler.ApiRequestCount);
        Assert.Equal(
            ["checksums.json", "knowledge-v1.2.3.zip", "manifest.json"],
            release.Assets.Select(asset => asset.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("首页.md", "**/*.md", true)]
    [InlineData("课程/第一章.md", "**/*.md", true)]
    [InlineData("assets/a/b.png", "assets/**", true)]
    [InlineData(".obsidian/plugins/dataview/data.json", ".obsidian/plugins/*/data.json", true)]
    [InlineData(".obsidian/plugins/a/nested/data.json", ".obsidian/plugins/*/data.json", false)]
    public void UpdatePolicyExecutor_MatchesExpectedGlobRules(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, UpdatePolicyExecutor.Matches(path, pattern));
    }

    [Fact]
    public async Task UpdatePolicyExecutor_PreservesFilesAndMergesJsonWithLocalPrecedence()
    {
        var current = Path.Combine(_root, "current");
        var next = Path.Combine(_root, "next");
        Directory.CreateDirectory(Path.Combine(current, ".obsidian"));
        Directory.CreateDirectory(Path.Combine(next, ".obsidian"));
        await File.WriteAllTextAsync(Path.Combine(current, ".obsidian", "workspace.json"), "{\"local\":true}");
        await File.WriteAllTextAsync(Path.Combine(current, ".obsidian", "app.json"), "{\"theme\":\"local\",\"local\":true}");
        await File.WriteAllTextAsync(Path.Combine(next, ".obsidian", "app.json"), "{\"theme\":\"upstream\",\"upstream\":true}");
        var policy = new UpdatePolicy
        {
            Preserve = [".obsidian/workspace.json"],
            Merge = [".obsidian/app.json"]
        };

        await UpdatePolicyExecutor.ApplyLocalStateAsync(current, next, policy);

        var workspace = await File.ReadAllTextAsync(Path.Combine(next, ".obsidian", "workspace.json"));
        var app = await File.ReadAllTextAsync(Path.Combine(next, ".obsidian", "app.json"));
        Assert.Contains("local", workspace, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"local\"", app, StringComparison.Ordinal);
        Assert.Contains("\"upstream\": true", app, StringComparison.Ordinal);
        Assert.Contains("\"local\": true", app, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class NullLog : ILauncherLog
    {
        public void Write(LogLevel level, string eventId, string message, Exception? exception = null)
        {
        }
    }

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
    }

    private sealed class RateLimitedReleaseHandler : HttpMessageHandler
    {
        public int ApiRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                ApiRequestCount++;
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"message\":\"API rate limit exceeded\"}"),
                    RequestMessage = request
                };
                forbidden.Headers.Add("X-RateLimit-Remaining", "0");
                forbidden.Headers.Add(
                    "X-RateLimit-Reset",
                    DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                return Task.FromResult(forbidden);
            }

            if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty),
                    RequestMessage = new HttpRequestMessage(
                        HttpMethod.Get,
                        "https://github.com/fallback-owner/fallback-repository/releases/tag/vault-v1.2.3")
                });
            }

            const string html = """
                <a href="/fallback-owner/fallback-repository/releases/download/vault-v1.2.3/manifest.json">manifest</a>
                <a href="/fallback-owner/fallback-repository/releases/download/vault-v1.2.3/checksums.json">checksums</a>
                <a href="/fallback-owner/fallback-repository/releases/download/vault-v1.2.3/knowledge-v1.2.3.zip">archive</a>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = request
            });
        }
    }
}
