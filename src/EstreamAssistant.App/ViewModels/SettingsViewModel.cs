using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Claude;
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
        RememberScannerDigits();
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
    public IReadOnlyList<string> Models { get; } = ["claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5"];
    public IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high"];

    public string DataFolder => _paths.Root;

    [ObservableProperty] private DataSourceKind _dataSource;
    [ObservableProperty] private string _connectionString = "";
    [ObservableProperty] private string _sampleDataPath = "";
    [ObservableProperty] private string _engineFolder = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _effort = "";
    [ObservableProperty] private string _receiptModel = "";
    [ObservableProperty] private string _receiptEffort = "";
    [ObservableProperty] private PricingMode _defaultPricingMode;
    [ObservableProperty] private string _defaultMarkupPct = "";
    [ObservableProperty] private string _priceEndings = "";
    [ObservableProperty] private string _scanMaxInterKeyMs = "";
    [ObservableProperty] private string _scanListWindowSeconds = "";
    [ObservableProperty] private string _protectedSheets = "";
    [ObservableProperty] private string _protectedBatches = "";
    [ObservableProperty] private string _labelDesign = "";
    [ObservableProperty] private string _scanMinDigits = "";
    [ObservableProperty] private string _scanMaxDigits = "";
    [ObservableProperty] private string _longChatTurns = "";
    [ObservableProperty] private bool _debugLogging;
    [ObservableProperty] private string _priceIn = "";
    [ObservableProperty] private string _priceOut = "";
    [ObservableProperty] private string _priceCacheRead = "";
    /// <summary>Spec 6.8: today's and this month's tokens and estimated cost (filled in by the main view model).</summary>
    [ObservableProperty] private string _usageDetail = "";

    public string PricesLabel => $"Prices for {Model.Trim()} (US$ per million tokens)";

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(PricesLabel));
        LoadPrices(value.Trim());
    }

    private void LoadPrices(string model)
    {
        var p = _settings.ModelPrices.GetValueOrDefault(model) ?? UsageTracker.Prices.GetValueOrDefault(model) ?? UsageTracker.Prices["claude-opus-5-5"];
        PriceIn = p.InputPerMTok.ToString("0.00##", CultureInfo.InvariantCulture);
        PriceOut = p.OutputPerMTok.ToString("0.00##", CultureInfo.InvariantCulture);
        PriceCacheRead = p.CacheReadPerMTok.ToString("0.00##", CultureInfo.InvariantCulture);
    }

    /// <summary>Spec 8.3: opens Power Automate Desktop so the owner can run the "Estream" flow by hand.</summary>
    [RelayCommand]
    private void OpenPadFlow()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-powerautomate:") { UseShellExecute = true });
            StatusMessage = "Opening Power Automate Desktop. Run the \"Estream\" flow from its list.";
        }
        catch (Exception ex)
        {
            _log.Error("Opening Power Automate Desktop failed", ex);
            StatusMessage = "Couldn't open Power Automate Desktop. Is it installed?";
        }
    }

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
        ReceiptModel = s.ReceiptModel;
        ReceiptEffort = s.ReceiptEffort;
        DefaultPricingMode = s.DefaultPricingMode;
        DefaultMarkupPct = s.DefaultMarkupPct.ToString(CultureInfo.InvariantCulture);
        PriceEndings = string.Join(", ", s.PriceEndings.Select(e => e.ToString("00", CultureInfo.InvariantCulture)));
        ScanMaxInterKeyMs = s.ScanMaxInterKeyMs.ToString(CultureInfo.InvariantCulture);
        ScanListWindowSeconds = s.ScanListWindowSeconds.ToString(CultureInfo.InvariantCulture);
        ProtectedSheets = string.Join(", ", s.ProtectedSheets);
        ProtectedBatches = string.Join(", ", s.ProtectedBatches);
        LabelDesign = s.LabelDesign;
        ScanMinDigits = s.ScanMinDigits.ToString(CultureInfo.InvariantCulture);
        ScanMaxDigits = s.ScanMaxDigits.ToString(CultureInfo.InvariantCulture);
        LongChatTurns = s.LongChatTurns.ToString(CultureInfo.InvariantCulture);
        DebugLogging = s.DebugLogging;
        LoadPrices(s.Model);
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
        if (!TryParseNumbers(ProtectedBatches, out var protectedBatches))
        { StatusMessage = "Protected price batches must be batch numbers separated by commas."; return; }
        if (string.IsNullOrWhiteSpace(LabelDesign) || LabelDesign.Trim().Length > 60)
        { StatusMessage = "Label design must be an eStream label design name or number, e.g. GROCERY SHELVES."; return; }
        if (!int.TryParse(ScanMinDigits, out var minDigits) || !int.TryParse(ScanMaxDigits, out var maxDigits)
            || minDigits < 4 || maxDigits > 20 || minDigits > maxDigits)
        { StatusMessage = "Barcode digits: min and max between 4 and 20, min not more than max (default 6 and 14)."; return; }
        if (!int.TryParse(LongChatTurns, out var longChat) || longChat is < 0 or > 1000)
        { StatusMessage = "Long chat must be a number of messages, e.g. 60 (0 = never suggest a new chat)."; return; }
        if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(ReceiptModel))
        { StatusMessage = "Choose a chat model and a receipt model."; return; }
        if (!Money(PriceIn, out var pIn) || !Money(PriceOut, out var pOut) || !Money(PriceCacheRead, out var pCache))
        { StatusMessage = "Claude prices must be dollar amounts per million tokens, e.g. 4.00."; return; }

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
        _settings.ReceiptModel = ReceiptModel.Trim();
        _settings.ReceiptEffort = ReceiptEffort;
        _settings.DefaultPricingMode = DefaultPricingMode;
        _settings.DefaultMarkupPct = markup;
        _settings.PriceEndings = endings;
        _settings.ScanMaxInterKeyMs = gap;
        _settings.ScanListWindowSeconds = window;
        _settings.ProtectedSheets = protectedSheets;
        _settings.ProtectedBatches = protectedBatches;
        _settings.LabelDesign = LabelDesign.Trim();
        _settings.ScanMinDigits = minDigits;
        _settings.ScanMaxDigits = maxDigits;
        _settings.LongChatTurns = longChat;
        _settings.DebugLogging = DebugLogging;
        var price = new ModelPrice(pIn, pOut, pCache);
        if (UsageTracker.Prices.GetValueOrDefault(_settings.Model) == price) _settings.ModelPrices.Remove(_settings.Model);
        else _settings.ModelPrices[_settings.Model] = price;
        restartNeeded |= minDigits != _scannerMin || maxDigits != _scannerMax;

        try
        {
            _settings.Save(_paths.SettingsFile);
            _log.Info("Settings saved.");
            StatusMessage = restartNeeded
                ? "Saved. Close and reopen the app to use the new data source, engine or scanner digits."
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

    private int _scannerMin = -1, _scannerMax = -1;

    /// <summary>The scanner settings the window started with (they apply after a restart).</summary>
    public void RememberScannerDigits() => (_scannerMin, _scannerMax) = (_settings.ScanMinDigits, _settings.ScanMaxDigits);

    private static bool Money(string s, out decimal d) =>
        decimal.TryParse(s.Replace("$", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out d) && d is >= 0 and < 1000;

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
