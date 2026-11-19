using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Pricing;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly AppLog _log;

    public SettingsViewModel(AppSettings settings, AppPaths paths, ISecretStore secrets, AppLog log)
    {
        _settings = settings;
        _paths = paths;
        _secrets = secrets;
        _log = log;
        LoadFrom(settings);
        RefreshKeyDisplay();
    }

    public event EventHandler? ApiKeyChanged;

    /// <summary>The main view model runs the test (it owns the chat service).</summary>
    public event EventHandler? TestConnectionRequested;

    [ObservableProperty] private string _testResult = "";

    [RelayCommand]
    private void TestConnection()
    {
        TestResult = "Testing…";
        TestConnectionRequested?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<DataSourceKind> DataSources { get; } = Enum.GetValues<DataSourceKind>();
    public IReadOnlyList<ExecutorKind> Executors { get; } = Enum.GetValues<ExecutorKind>();
    [ObservableProperty] private ExecutorKind _executor;
    public IReadOnlyList<PricingMode> PricingModes { get; } = Enum.GetValues<PricingMode>();
    public IReadOnlyList<string> Models { get; } = ["claude-opus-5-5", "claude-sonnet-5-5"];
    public IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high"];

    public string DataFolder => _paths.Root;

    [ObservableProperty] private DataSourceKind _dataSource;
    [ObservableProperty] private string _connectionString = "";
    [ObservableProperty] private string _sampleDataPath = "";
    [ObservableProperty] private string _engineFolder = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _effort = "";
    [ObservableProperty] private PricingMode _defaultPricingMode;
    [ObservableProperty] private string _defaultMarkupPct = "";
    [ObservableProperty] private string _priceEndings = "";
    [ObservableProperty] private string _scanMaxInterKeyMs = "";
    [ObservableProperty] private string _scanListWindowSeconds = "";
    [ObservableProperty] private string _protectedSheets = "";

    [ObservableProperty] private string _apiKeyDisplay = "";
    [ObservableProperty] private string _statusMessage = "";

    public bool HasApiKey => _secrets.HasApiKey;

    private void LoadFrom(AppSettings s)
    {
        DataSource = s.DataSource;
        ConnectionString = s.ConnectionString;
        SampleDataPath = s.SampleDataPath ?? "";
        EngineFolder = s.EngineFolder;
        Executor = s.Executor;
        Model = s.Model;
        Effort = s.Effort;
        DefaultPricingMode = s.DefaultPricingMode;
        DefaultMarkupPct = s.DefaultMarkupPct.ToString(CultureInfo.InvariantCulture);
        PriceEndings = string.Join(", ", s.PriceEndings.Select(e => e.ToString("00", CultureInfo.InvariantCulture)));
        ScanMaxInterKeyMs = s.ScanMaxInterKeyMs.ToString(CultureInfo.InvariantCulture);
        ScanListWindowSeconds = s.ScanListWindowSeconds.ToString(CultureInfo.InvariantCulture);
        ProtectedSheets = string.Join(", ", s.ProtectedSheets);
    }

    private static bool TryParseNumbers(string text, out List<int> numbers)
    {
        numbers = [];
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) return false;
            numbers.Add(n);
        }
        numbers = numbers.Distinct().ToList();
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!decimal.TryParse(DefaultMarkupPct, NumberStyles.Number, CultureInfo.InvariantCulture, out var markup) || markup < 0 || markup >= 1000)
        { StatusMessage = "Default markup must be a number like 30."; return; }
        if (!TryParseEndings(PriceEndings, out var endings))
        { StatusMessage = "Price endings must be cents between 00 and 99, e.g. 29, 49, 79, 99 (or leave it empty)."; return; }
        if (!int.TryParse(ScanMaxInterKeyMs, out var gap) || gap is < 10 or > 200)
        { StatusMessage = "Scanner key gap must be between 10 and 200 ms."; return; }
        if (!int.TryParse(ScanListWindowSeconds, out var window) || window is < 1 or > 600)
        { StatusMessage = "Scan list window must be between 1 and 600 seconds."; return; }
        if (!TryParseNumbers(ProtectedSheets, out var protectedSheets))
        { StatusMessage = "Protected sheets must be sheet numbers separated by commas, e.g. 10001, 10002."; return; }

        bool restartNeeded = _settings.DataSource != DataSource
            || _settings.ConnectionString != ConnectionString.Trim()
            || (_settings.SampleDataPath ?? "") != SampleDataPath.Trim()
            || _settings.Executor != Executor
            || _settings.EngineFolder != EngineFolder.Trim();

        _settings.DataSource = DataSource;
        _settings.ConnectionString = ConnectionString.Trim();
        _settings.SampleDataPath = string.IsNullOrWhiteSpace(SampleDataPath) ? null : SampleDataPath.Trim();
        _settings.EngineFolder = EngineFolder.Trim();
        _settings.Executor = Executor;
        _settings.Model = Model.Trim();
        _settings.Effort = Effort;
        _settings.DefaultPricingMode = DefaultPricingMode;
        _settings.DefaultMarkupPct = markup;
        _settings.PriceEndings = endings;
        _settings.ScanMaxInterKeyMs = gap;
        _settings.ScanListWindowSeconds = window;
        _settings.ProtectedSheets = protectedSheets;

        try
        {
            _settings.Save(_paths.SettingsFile);
            _log.Info("Settings saved.");
            StatusMessage = restartNeeded
                ? "Saved. Close and reopen the app to use the new data source or engine."
                : "Saved.";
        }
        catch (IOException ex)
        {
            _log.Error("Saving settings failed", ex);
            StatusMessage = "Couldn't save settings: " + ex.Message;
        }
    }

    /// <summary>Called from the view with the PasswordBox contents (PasswordBox can't bind).</summary>
    public void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { StatusMessage = "Paste the API key first."; return; }
        if (!key.Trim().StartsWith("sk-ant-", StringComparison.Ordinal))
        { StatusMessage = "That doesn't look like an Anthropic API key (it should start with sk-ant-)."; return; }
        _secrets.SetApiKey(key);
        _log.Info("API key saved (DPAPI).");
        StatusMessage = "API key saved securely for this Windows user.";
        RefreshKeyDisplay();
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        _secrets.ClearApiKey();
        _log.Info("API key cleared.");
        StatusMessage = "API key removed.";
        RefreshKeyDisplay();
    }

    private void RefreshKeyDisplay()
    {
        ApiKeyDisplay = SecretMask.Mask(_secrets.GetApiKey());
        OnPropertyChanged(nameof(HasApiKey));
        ApiKeyChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static bool TryParseEndings(string text, out List<int> endings)
    {
        endings = [];
        foreach (var part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.TrimStart('.');
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var e) || e is < 0 or > 99) return false;
            if (!endings.Contains(e)) endings.Add(e);
        }
        endings.Sort();
        return true;
    }
}
