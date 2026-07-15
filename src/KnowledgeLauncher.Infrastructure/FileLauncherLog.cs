using System.Globalization;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class FileLauncherLog : ILauncherLog
{
    private readonly string _logPath;
    private readonly object _sync = new();

    public FileLauncherLog(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        _logPath = Path.Combine(paths.Logs, "launcher.log");
    }

    public void Write(LogLevel level, string eventId, string message, Exception? exception = null)
    {
        var safeMessage = message.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:O} [{level}] {eventId} {safeMessage}");

        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {exception.Message}";
        }

        try
        {
            lock (_sync)
            {
                RotateIfNeeded();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never make the launcher unusable.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never make the launcher unusable.
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_logPath);
        if (!file.Exists || file.Length < 2 * 1024 * 1024)
        {
            return;
        }

        var previous = _logPath + ".1";
        File.Move(_logPath, previous, overwrite: true);
    }
}
