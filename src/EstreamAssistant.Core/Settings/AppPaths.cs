namespace EstreamAssistant.Core.Settings;

/// <summary>Where the app keeps its own files (%LOCALAPPDATA%\eStreamAssistant).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eStreamAssistant");
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ApiKeyFile => Path.Combine(Root, "apikey.bin");
    public string LogsFolder => Path.Combine(Root, "logs");
    /// <summary>The Bugs list: problems the app ran into while doing a task.</summary>
    public string IssuesFile => Path.Combine(Root, "issues.txt");
    public string UsageFile => Path.Combine(Root, "usage.json");
    /// <summary>Dry-run CSVs (kept out of the engine's Inbox on purpose).</summary>
    public string RunsFolder => Path.Combine(Root, "runs");
    /// <summary>Receipts saved for later (the Pending tab), and the lines of finished ones.</summary>
    public string SavedReceiptsFolder => Path.Combine(Root, "receipts");

    /// <summary>One cache file per data source so mock and real names never mix.</summary>
    public string NameCacheFile(DataSourceKind source) =>
        Path.Combine(Root, $"item-names-{source.ToString().ToLowerInvariant()}.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsFolder);
    }
}
