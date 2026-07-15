using System.Diagnostics;
using System.Runtime.Versioning;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class ObsidianInstaller : IObsidianInstaller
{
    private static readonly Uri OfficialDownloadUri = new("https://obsidian.md/download");

    public bool IsWingetAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), "winget.exe"))
            .Any(File.Exists);
    }

    public async Task<bool> InstallWithWingetAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWingetAvailable())
        {
            return false;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "winget.exe",
                Arguments = "install --id Obsidian.Obsidian --exact --source winget --accept-source-agreements --accept-package-agreements",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        try
        {
            if (!process.Start())
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void OpenOfficialDownloadPage()
    {
        Process.Start(new ProcessStartInfo(OfficialDownloadUri.AbsoluteUri) { UseShellExecute = true });
    }
}
