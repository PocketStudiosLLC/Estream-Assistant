using System.Text.Json;
using System.Text.Json.Serialization;
using EstreamAssistant.Core.Pricing;

namespace EstreamAssistant.Core.Settings;

public enum DataSourceKind
{
    /// <summary>Built-in sample items, or a sqlcmd export (dev PC).</summary>
    Mock,
    /// <summary>The real eStream database, read-only (store PC).</summary>
    Sql,
}

public enum ExecutorKind
{
    /// <summary>Simulated dry runs (dev PC, or trying the app safely).</summary>
    Mock,
    /// <summary>The real engine: the PowerShell scripts in the engine folder drive eStream (store PC).</summary>
    PowerShell,
}

/// <summary>
/// User settings saved as JSON in %LOCALAPPDATA%\eStreamAssistant\settings.json.
/// The API key is NOT stored here; see <see cref="ISecretStore"/>.
/// </summary>
public sealed class AppSettings
{
    // Data
    public DataSourceKind DataSource { get; set; } = DataSourceKind.Mock;
    public string ConnectionString { get; set; } = Data.SqlItemRepository.DefaultConnectionString;
    /// <summary>Optional sqlcmd export (Plan.txt sec. 12) used by the mock data source.</summary>
    public string? SampleDataPath { get; set; }
    public double NameCacheMaxAgeHours { get; set; } = 12;

    // Engine
    public string EngineFolder { get; set; } = @"C:\eStreamAutomation";
    public ExecutorKind Executor { get; set; } = ExecutorKind.Mock;
    public int ScriptTimeoutBaseSeconds { get; set; } = 60;
    public int ScriptTimeoutPerItemSeconds { get; set; } = 30;
    /// <summary>S6: the owner's own open stock sheets the app must never touch. Local only (never in the repo).</summary>
    public List<int> ProtectedSheets { get; set; } = [];
    /// <summary>S6: pending price batches that aren't ours. Local only.</summary>
    public List<int> ProtectedBatches { get; set; } = [];

    // Claude
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "low";

    // Pricing
    public PricingMode DefaultPricingMode { get; set; } = PricingMode.Markup;
    public decimal DefaultMarkupPct { get; set; } = 30;
    /// <summary>Allowed price endings in cents, e.g. [29, 49, 79, 99]. Empty = no ending rule.</summary>
    public List<int> PriceEndings { get; set; } = [];

    // Scanner
    public int ScanMaxInterKeyMs { get; set; } = 40;
    public int ScanMinDigits { get; set; } = 6;
    public int ScanMaxDigits { get; set; } = 14;
    public int ScanListWindowSeconds { get; set; } = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Bad file: keep a copy for diagnosis and fall back to defaults.
            File.Copy(path, path + ".bad", overwrite: true);
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
