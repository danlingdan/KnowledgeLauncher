using System.Diagnostics;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public sealed class ObsidianLauncher : IObsidianLauncher
{
    public Uri BuildOpenUri(string vaultName, string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        var value = $"obsidian://open?vault={Uri.EscapeDataString(vaultName)}&file={Uri.EscapeDataString(entryPath)}";
        return new Uri(value, UriKind.Absolute);
    }

    public Uri BuildOpenPathUri(string absoluteEntryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteEntryPath);
        if (!Path.IsPathFullyQualified(absoluteEntryPath))
        {
            throw new ArgumentException("入口文件必须是绝对路径。", nameof(absoluteEntryPath));
        }

        return new Uri($"obsidian://open?path={Uri.EscapeDataString(Path.GetFullPath(absoluteEntryPath))}", UriKind.Absolute);
    }

    public void Open(string vaultName, string entryPath)
    {
        Start(BuildOpenUri(vaultName, entryPath));
    }

    public void OpenPath(string absoluteEntryPath)
    {
        Start(BuildOpenPathUri(absoluteEntryPath));
    }

    private static void Start(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.OriginalString) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new LauncherException("KL2001", "无法调用 Obsidian，请确认应用已安装且 URI 协议已注册。", exception);
        }
    }
}
