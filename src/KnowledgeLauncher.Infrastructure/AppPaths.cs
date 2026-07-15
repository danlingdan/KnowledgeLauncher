namespace KnowledgeLauncher.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? rootPath = null)
    {
        Root = Path.GetFullPath(rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KnowledgeLauncher"));
    }

    public string Root { get; }
    public string App => Path.Combine(Root, "app");
    public string SettingsFile => Path.Combine(App, "launcher.json");
    public string Vault => Path.Combine(Root, "vault");
    public string Staging => Path.Combine(Root, "staging");
    public string Backup => Path.Combine(Root, "backup");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, App, Vault, Staging, Backup, Cache, Logs })
        {
            Directory.CreateDirectory(path);
        }
    }
}
