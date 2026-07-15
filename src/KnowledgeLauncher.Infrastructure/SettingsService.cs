using System.Text.Json;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class SettingsService : ISettingsService
{
    private readonly AppPaths _paths;
    private readonly ILauncherLog _log;

    public SettingsService(AppPaths paths, ILauncherLog log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!File.Exists(_paths.SettingsFile))
        {
            var defaults = new LauncherSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            await using var stream = File.OpenRead(_paths.SettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<LauncherSettings>(
                stream,
                JsonDefaults.Options,
                cancellationToken).ConfigureAwait(false);

            if (settings is null || settings.SchemaVersion != 1)
            {
                throw new JsonException("不支持的配置结构版本。");
            }

            return settings;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            var corruptPath = _paths.SettingsFile + $".corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(_paths.SettingsFile, corruptPath, overwrite: false);
            _log.Write(LogLevel.Warning, "KL-CONFIG-RECOVERED", "配置损坏，已恢复默认配置。", exception);
            var defaults = new LauncherSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _paths.EnsureCreated();
        var temporaryPath = _paths.SettingsFile + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                settings,
                JsonDefaults.Options,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _paths.SettingsFile, overwrite: true);
    }
}
