using System.Security.Cryptography;
using KnowledgeLauncher.Core;

namespace KnowledgeLauncher.Infrastructure;

public static class HashVerifier
{
    public static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task VerifySha256Async(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new LauncherException("KL4001", $"文件 {Path.GetFileName(filePath)} 的 SHA-256 清单格式无效。");
        }

        var actual = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException("KL4002", $"文件 {Path.GetFileName(filePath)} 的 SHA-256 校验失败。");
        }
    }
}
