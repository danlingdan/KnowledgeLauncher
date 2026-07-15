namespace KnowledgeLauncher.Core;

public sealed class LauncherException : Exception
{
    public LauncherException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public LauncherException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
