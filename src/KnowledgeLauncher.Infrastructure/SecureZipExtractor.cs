using System.IO.Compression;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public static class SecureZipExtractor
{
    private const int MaximumEntries = 50_000;
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;

    public static void Extract(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var root = Path.GetFullPath(destinationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new LauncherException("KL4101", "ZIP 文件条目数量超过安全限制。");
        }

        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new LauncherException("KL4102", "ZIP 解压后大小超过安全限制。");
            }

            if (IsSymbolicLink(entry))
            {
                throw new LauncherException("KL4103", $"ZIP 包含不允许的符号链接：{entry.FullName}");
            }

            var targetPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException("KL4104", $"ZIP 条目试图写出目标目录：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: false);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return unixMode == unixSymbolicLink;
    }
}
