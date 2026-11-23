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
    /// <summary>
    /// Time limit for one item editor run: base + per item. Test 3: while eStream Server was busy one item took
    /// 35-83 s, and the old 60 + 30/item stopped a SAVE half-way (an item's editor was left open in eStream).
    /// </summary>
    public int ScriptTimeoutBaseSeconds { get; set; } = 120;
    public int ScriptTimeoutPerItemSeconds { get; set; } = 90;

    /// <summary>Which defaults the file was written with (0 = before v0.1.8). See <see cref="Migrate"/>.</summary>
    public int SettingsVersion { get; set; }
    public const int CurrentSettingsVersion = 1;
    /// <summary>
    /// How long the registers take to get back-office changes (eStream Server's polling interval; the manual says
    /// about 10 minutes). Only used in messages.
    /// </summary>
    public int RegisterUpdateMinutes { get; set; } = 10;
    /// <summary>S6: the owner's own open stock sheets the app must never touch. Local only (never in the repo).</summary>
    public List<int> ProtectedSheets { get; set; } = [];
    /// <summary>S6: pending price batches that aren't ours. Local only.</summary>
    public List<int> ProtectedBatches { get; set; } = [];
    /// <summary>The eStream label design that "Queue in eStream" adds shelf labels to (name or LabelBatchID).</summary>
    public string LabelDesign { get; set; } = "GROCERY SHELVES";

    // Claude
    /// <summary>Model for ordinary text messages (lookups, questions, pricing). The cheaper one.</summary>
    public string Model { get; set; } = "claude-sonnet-5-5";
    public string Effort { get; set; } = "low";
    /// <summary>Model for messages that carry a photo or PDF (receipts, invoices, count sheets). The better reader.</summary>
    public string ReceiptModel { get; set; } = "claude-opus-5-5";
    /// <summary>Effort for those messages (spec 7.1 suggests medium for receipts).</summary>
    public string ReceiptEffort { get; set; } = "medium";
    /// <summary>Spec 7.4: after this many messages in one chat, suggest a new chat to keep costs down. 0 = never.</summary>
    public int LongChatTurns { get; set; } = 60;
    /// <summary>US$ per million tokens, per model, when they differ from the built-in table (spec 7.1).</summary>
    public Dictionary<string, Claude.ModelPrice> ModelPrices { get; set; } = [];
    /// <summary>Spec 11: also log the text of chat messages (off by default; tool calls are always logged).</summary>
    public bool DebugLogging { get; set; }

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
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()).Migrate();
        }
        catch (JsonException)
        {
            // Bad file: keep a copy for diagnosis and fall back to defaults.
            File.Copy(path, path + ".bad", overwrite: true);
        }
        return new AppSettings { SettingsVersion = CurrentSettingsVersion };
    }

    /// <summary>Moves values the owner never changed from an old default to the new one.</summary>
    internal AppSettings Migrate()
    {
        if (SettingsVersion < 1)
        {
            if (ScriptTimeoutBaseSeconds == 60) ScriptTimeoutBaseSeconds = 120;
            if (ScriptTimeoutPerItemSeconds == 30) ScriptTimeoutPerItemSeconds = 90;
        }
        SettingsVersion = CurrentSettingsVersion;
        return this;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
