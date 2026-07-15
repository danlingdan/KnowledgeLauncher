using System.Text.Json.Serialization;

namespace KnowledgeLauncher.Core;

public sealed record LauncherSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string RepositoryOwner { get; init; } = "danlingdan";
    public string RepositoryName { get; init; } = "ComputerKnowledgeBase";
    public string? InstalledVaultId { get; init; }
    public string? InstalledVersion { get; init; }
    public string? InstalledVaultPath { get; init; }
    public bool CommunityPluginsTrusted { get; init; }
    public string InstallationState { get; init; } = "NotInstalled";
    public DateTimeOffset? LastSuccessfulRun { get; init; }
}

public sealed record VaultManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Entry { get; init; }
    public required string MinimumObsidianVersion { get; init; }
    public string PluginLock { get; init; } = "launcher/plugin-lock.json";
    public string UpdatePolicy { get; init; } = "launcher/update-policy.json";
}

public sealed record PluginLockFile
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<PluginLockEntry> Plugins { get; init; } = [];
}

public sealed record PluginLockEntry
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Repository { get; init; }
    public required string MinimumObsidianVersion { get; init; }
    public IReadOnlyDictionary<string, string> Sha256 { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record UpdatePolicy
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<string> Overwrite { get; init; } = [];
    public IReadOnlyList<string> Preserve { get; init; } = [];
    public IReadOnlyList<string> Merge { get; init; } = [];
}

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(
    string TagName,
    string Name,
    bool IsDraft,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record ObsidianInstallation(
    bool IsInstalled,
    string? ExecutablePath,
    Version? Version,
    bool IsUriProtocolRegistered,
    string? Detail);

public sealed record DownloadProgress(string FileName, long BytesReceived, long? TotalBytes)
{
    [JsonIgnore]
    public double? Percentage => TotalBytes is > 0 ? BytesReceived * 100d / TotalBytes : null;
}

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public enum InstallationStage
{
    CheckingObsidian,
    CleaningStaging,
    QueryingRelease,
    DownloadingManifest,
    DownloadingVault,
    VerifyingVault,
    InstallingPlugins,
    ActivatingVault,
    SavingSettings,
    Completed
}

public sealed record InstallationProgress(InstallationStage Stage, string Message, int? Current = null, int? Total = null);

public sealed record InstallationResult(
    VaultManifest Manifest,
    string VaultPath,
    IReadOnlyList<string> InstalledPluginIds,
    bool RequiresVaultRegistration);

public enum UpdateAvailability
{
    NotInstalled,
    UpToDate,
    UpdateAvailable
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string? InstalledVersion,
    string LatestVersion);

public sealed record VaultUpdateResult(
    string PreviousVersion,
    string InstalledVersion,
    string VaultPath,
    string BackupPath,
    bool WasRepair);
