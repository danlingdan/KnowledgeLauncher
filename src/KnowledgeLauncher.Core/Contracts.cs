namespace KnowledgeLauncher.Core;

public interface ILauncherLog
{
    void Write(LogLevel level, string eventId, string message, Exception? exception = null);
}

public interface ISettingsService
{
    Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
}

public interface IObsidianDetector
{
    ObsidianInstallation Detect();
}

public interface IObsidianLauncher
{
    Uri BuildOpenUri(string vaultName, string entryPath);
    Uri BuildOpenPathUri(string absoluteEntryPath);
    void Open(string vaultName, string entryPath);
    void OpenPath(string absoluteEntryPath);
}

public interface IObsidianInstaller
{
    bool IsWingetAvailable();
    Task<bool> InstallWithWingetAsync(CancellationToken cancellationToken = default);
    void OpenOfficialDownloadPage();
}

public interface IVaultUsageDetector
{
    bool IsObsidianRunning();
}

public interface IReleaseClient
{
    Task<ReleaseInfo> GetLatestAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default);

    Task<ReleaseInfo> GetByTagAsync(
        string owner,
        string repository,
        string tag,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        ReleaseAsset asset,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
