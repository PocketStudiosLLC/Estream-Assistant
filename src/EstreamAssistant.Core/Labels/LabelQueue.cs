using System.Globalization;
using System.Text.Json;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Reports;

namespace EstreamAssistant.Core.Labels;

/// <summary>One item whose shelf label needs reprinting.</summary>
public sealed record LabelEntry(long Upc, string Description, decimal Price, int For, DateTime Added, string Why);

/// <summary>
/// Shelf labels to print. The eStream manual: "Don't forget to make a new shelf label for the item before sending
/// the changes to the POS lanes." Every price saved by the app (and every other item eStream repriced with it in a
/// Mix Match group) is added here. Kept in labels-to-print.json until the owner clears it.
/// </summary>
public sealed class LabelQueue
{
    private readonly string? _file;
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private List<LabelEntry> _entries = [];

    public LabelQueue(string? folder = null, AppLog? log = null)
    {
        _file = folder is null ? null : Path.Combine(folder, "labels-to-print.json");
        _log = log;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<LabelEntry> Entries { get { lock (_gate) return _entries.ToList(); } }
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Adds or replaces the item's entry (the latest price wins).</summary>
    public void Add(IEnumerable<LabelEntry> entries)
    {
        bool any = false;
        lock (_gate)
        {
            foreach (var e in entries)
            {
                _entries.RemoveAll(x => x.Upc == e.Upc);
                _entries.Add(e);
                any = true;
            }
            if (any) Save();
        }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(IEnumerable<long> upcs)
    {
        var set = upcs.ToHashSet();
        lock (_gate)
        {
            if (_entries.RemoveAll(e => set.Contains(e.Upc)) == 0) return;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            Save();
        }
        _log?.Info("Shelf label list cleared.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Report ToReport() => ToReport(Entries);

    /// <summary>The empty list as a report.</summary>
    public static Report Empty => ToReport([]);

    internal static Report ToReport(IReadOnlyList<LabelEntry> entries)
    {
        var rows = entries.OrderBy(e => e.Description, StringComparer.OrdinalIgnoreCase).ToList();
        return new Report
        {
            Kind = ReportKind.Labels,
            Title = "Shelf labels to print",
            Summary = rows.Count == 0
                ? "Nothing to print. Prices saved by the app are added here."
                : $"{rows.Count} label{(rows.Count == 1 ? "" : "s")} · print this list or make the labels in eStream (Labels and Signs, or the Item List's Labels tab), then press Clear",
            Notes = rows.Count == 0 ? [] :
            [
                "The registers get the new prices from eStream Server within a few minutes, so put the new labels out at the same time.",
            ],
            Columns =
            [
                new("UPC", ReportColumnKind.Upc), new("Description"), new("Price", ReportColumnKind.Money),
                new("For", ReportColumnKind.Number), new("Shelf price"), new("Changed"), new("Why"),
            ],
            Rows = rows.Select(e => new object?[]
            {
                e.Upc, e.Description, e.Price, (decimal)e.For,
                e.For > 1 ? $"{e.For} for {e.Price.ToString("C2", CultureInfo.GetCultureInfo("en-CA"))}" : e.Price.ToString("C2", CultureInfo.GetCultureInfo("en-CA")),
                e.Added, e.Why,
            }).ToList(),
        };
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try { _entries = JsonSerializer.Deserialize<List<LabelEntry>>(File.ReadAllText(_file)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { _log?.Error("Couldn't read the shelf label list", ex); }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries));
        }
        catch (IOException ex) { _log?.Error("Couldn't save the shelf label list", ex); }
    }
}
