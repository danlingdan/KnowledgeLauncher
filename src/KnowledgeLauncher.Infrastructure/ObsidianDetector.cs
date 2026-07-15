using System.Runtime.Versioning;
using KnowledgeLauncher.Core;
using Microsoft.Win32;

namespace KnowledgeLauncher.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class ObsidianDetector : IObsidianDetector
{
    public ObsidianInstallation Detect()
    {
        var uriRegistered = IsUriRegistered();
        foreach (var candidate in GetCandidatePaths())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = ReadVersion(candidate);
            return new ObsidianInstallation(
                true,
                candidate,
                version,
                uriRegistered,
                version is null ? "已找到 Obsidian，但无法读取版本。" : null);
        }

        return new ObsidianInstallation(false, null, null, uriRegistered, "未找到 Obsidian 安装。");
    }

    private static HashSet<string> GetCandidatePaths()
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var commandKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\obsidian\shell\open\command")
            ?? Registry.ClassesRoot.OpenSubKey(@"obsidian\shell\open\command"))
        {
            if (commandKey?.GetValue(null) is string command)
            {
                var executable = ExtractExecutablePath(command);
                if (executable is not null)
                {
                    discovered.Add(executable);
                }
            }
        }

        foreach (var registryPath in new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Obsidian",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Obsidian"
        })
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath)
                ?? Registry.LocalMachine.OpenSubKey(registryPath);
            var icon = key?.GetValue("DisplayIcon") as string;
            if (!string.IsNullOrWhiteSpace(icon))
            {
                discovered.Add(icon.Trim('"').Split(',')[0]);
            }

            var location = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(location))
            {
                discovered.Add(Path.Combine(location, "Obsidian.exe"));
            }
        }

        discovered.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Obsidian",
            "Obsidian.exe"));
        discovered.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Obsidian",
            "Obsidian.exe"));
        return discovered;
    }

    private static string? ExtractExecutablePath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? trimmed[..(executableEnd + 4)] : null;
    }

    private static bool IsUriRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\obsidian\shell\open\command")
            ?? Registry.ClassesRoot.OpenSubKey(@"obsidian\shell\open\command");
        return key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command);
    }

    private static Version? ReadVersion(string executablePath)
    {
        var value = System.Diagnostics.FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var numeric = value.Split('-', '+')[0];
        return Version.TryParse(numeric, out var version) ? version : null;
    }
}
