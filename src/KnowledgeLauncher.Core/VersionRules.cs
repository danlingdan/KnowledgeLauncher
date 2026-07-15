namespace KnowledgeLauncher.Core;

public static class VersionRules
{
    public static Version Parse(string value, string fieldName)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(normalized, out var version))
        {
            throw new LauncherException("KL1001", $"{fieldName} 不是有效版本号：{value}");
        }

        return version;
    }

    public static bool MeetsMinimum(Version actual, string minimum) =>
        actual >= Parse(minimum, "最低版本");
}
