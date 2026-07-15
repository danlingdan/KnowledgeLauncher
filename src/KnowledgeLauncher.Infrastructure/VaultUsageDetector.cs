using System.Diagnostics;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class VaultUsageDetector : IVaultUsageDetector
{
    public bool IsObsidianRunning()
    {
        var processes = Process.GetProcessesByName("Obsidian");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
