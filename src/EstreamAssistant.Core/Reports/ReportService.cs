using System.Globalization;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Reports;

public sealed record StockReportResult(Report Report, StockFilter Filter, IReadOnlyList<StockRow> Rows, bool Truncated, decimal TotalUnits, decimal TotalValue);

/// <summary>A stock sheet from eStream plus what the app knows about it.</summary>
public sealed record SheetSummary(StockSheetInfo Info, bool Protected, StockSheetRecord? MadeByApp)
{
    public string Note =>
        Protected ? "Owner's sheet (protected)"
        : MadeByApp is { Posted: true } ? "Made and posted by this app"
        : MadeByApp is not null ? "Made by this app"
        : "";
}

public sealed record SheetListResult(Report Report, IReadOnlyList<SheetSummary> Sheets);

public sealed record SheetResult(Report Report, SheetSummary Sheet, IReadOnlyList<StockSheetLine> Lines, IReadOnlyList<string> Checks, bool Truncated)
{
    public int Over => Lines.Count(l => l.OverShort > 0);
    public int Short => Lines.Count(l => l.OverShort < 0);
    public int Unchanged => Lines.Count(l => l.OverShort == 0);
    public decimal NetUnits => Lines.Sum(l => l.OverShort);
    public decimal NetValue => Math.Round(Lines.Sum(l => l.OverShort * l.UnitCost), 2);
}

/// <summary>
/// Read-only stock reports and stock sheet views (Reports tab and Claude's stock_report / stock_sheets).
/// Nothing here changes eStream. The latest report is kept in <see cref="Current"/> for the Reports tab.
/// </summary>
public sealed partial class ReportService
{
    /// <summary>A difference this big (units, or dollars at cost) is called out in the sheet checks.</summary>
    public const decimal BigUnits = 10;
    public const decimal BigValue = 50;
    /// <summary>Names missing from the cache are looked up one by one, up to this many.</summary>
    public const int MaxNameLookups = 25;

    private readonly ItemLookupService _lookup;
    private readonly Func<IReadOnlyCollection<int>> _protectedSheets;
    private readonly Func<IReadOnlyList<StockSheetRecord>> _appSheets;
    private readonly AppLog? _log;
    private Report? _current;

    public ReportService(ItemLookupService lookup, Func<IReadOnlyCollection<int>> protectedSheets,
        Func<IReadOnlyList<StockSheetRecord>>? appSheets = null, AppLog? log = null)
    {
        _lookup = lookup;
        _protectedSheets = protectedSheets;
        _appSheets = appSheets ?? (() => []);
        _log = log;
    }

    /// <summary>Raised when <see cref="Current"/> changes (may be on a background thread).</summary>
    public event EventHandler? Changed;

    public Report? Current => _current;

    /// <summary>Shows a report made elsewhere (e.g. the shelf label list) on the Reports tab.</summary>
    public void ShowReport(Report r) => Show(r);

    private void Show(Report r)
    {
        _current = r;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Title(StockFilter f) => f switch
    {
        StockFilter.Negative => "Negative stock",
        StockFilter.Zero => "Zero stock",
        StockFilter.BelowMin => "Below minimum",
        StockFilter.AboveMax => "Above re-order max",
        _ => f.ToString(),
    };

    // --- stock reports ----------------------------------------------------------

    public async Task<StockReportResult> StockAsync(StockFilter filter, bool includeInactive, CancellationToken ct = default)
    {
        var raw = await _lookup.Repository.GetStockRowsAsync(filter, includeInactive, ct).ConfigureAwait(false);
        bool truncated = raw.Count > SqlItemRepository.MaxReportRows;
        var names = await NamesAsync(raw.Take(SqlItemRepository.MaxReportRows).Select(r => r.Upc), ct).ConfigureAwait(false);
        var rows = raw.Take(SqlItemRepository.MaxReportRows)
            .Select(r => r with { Description = names[r.Upc] })
            .ToList();
        rows = filter switch
        {
            StockFilter.Negative => rows.OrderBy(r => r.OnHand).ThenBy(r => r.Description).ToList(),
            StockFilter.Zero => rows.OrderByDescending(r => r.Sold).ThenBy(r => r.Description).ToList(),
            StockFilter.BelowMin => rows.OrderByDescending(r => r.Min - r.OnHand).ThenBy(r => r.Description).ToList(),
            StockFilter.AboveMax => rows.OrderByDescending(r => r.OnHand - r.Max).ThenBy(r => r.Description).ToList(),
            _ => rows,
        };

        decimal units = filter switch
        {
            StockFilter.BelowMin => rows.Sum(r => r.Min - r.OnHand),
            StockFilter.AboveMax => rows.Sum(r => r.OnHand - r.Max),
            _ => rows.Sum(r => r.OnHand),
        };
        decimal value = Math.Round(rows.Sum(r => r.OnHand * r.UnitCost), 2);

        var cols = new List<ReportColumn>
        {
            new("UPC", ReportColumnKind.Upc), new("Description"), new("On hand", ReportColumnKind.Number),
            new("Min", ReportColumnKind.Number), new("Max", ReportColumnKind.Number), new("Sold", ReportColumnKind.Number),
            new("Unit cost", ReportColumnKind.Money), new("Price", ReportColumnKind.Money),
        };
        bool showFor = rows.Any(r => r.For > 1);
        if (showFor) cols.Add(new("For", ReportColumnKind.Number));
        string? extra = filter switch
        {
            StockFilter.Negative => "Value at cost",
            StockFilter.BelowMin => "Short of min",
            StockFilter.AboveMax => "Over max",
            _ => null,
        };
        if (extra is not null) cols.Add(new(extra, filter == StockFilter.Negative ? ReportColumnKind.Money : ReportColumnKind.Number));
        if (includeInactive) cols.Add(new("Inactive"));

        var cells = rows.Select(r =>
        {
            var c = new List<object?> { r.Upc, r.Description, r.OnHand, r.Min, r.Max, r.Sold, r.UnitCost, r.Price };
            if (showFor) c.Add(r.For);
            if (filter == StockFilter.Negative) c.Add(Math.Round(r.OnHand * r.UnitCost, 2));
            else if (filter == StockFilter.BelowMin) c.Add(r.Min - r.OnHand);
            else if (filter == StockFilter.AboveMax) c.Add(r.OnHand - r.Max);
            if (includeInactive) c.Add(r.Inactive ? "INACTIVE" : "");
            return c.ToArray();
        }).ToList();

        string n = Plural(rows.Count, "item");
        string summary = filter switch
        {
            StockFilter.Negative => $"{n} · {N(units)} units · {Money(value)} at cost",
            StockFilter.BelowMin => $"{n} · {N(units)} units needed to reach the minimums",
            StockFilter.AboveMax => $"{n} · {N(units)} units over the re-order max",
            _ => n,
        };
        if (truncated) summary += $" · only the first {SqlItemRepository.MaxReportRows} are shown";

        var report = new Report
        {
            Kind = ReportKind.Stock,
            Title = Title(filter) + (includeInactive ? " (including inactive items)" : ""),
            Summary = summary,
            Notes = filter == StockFilter.Zero && rows.Count > 500
                ? ["Many of these may be items the store no longer carries. Sorted by most sold first."]
                : [],
            Columns = cols,
            Rows = cells,
        };
        Show(report);
        _log?.Info($"Report: {report.Title}: {summary}");
        return new(report, filter, rows, truncated, units, value);
    }

    // --- stock sheets -------------------------------------------------------------

    public async Task<SheetListResult> SheetListAsync(bool includeClosed, CancellationToken ct = default)
    {
        var sheets = (await _lookup.Repository.GetStockSheetsAsync(includeClosed, ct).ConfigureAwait(false))
            .Select(Summarize).ToList();
        var report = new Report
        {
            Kind = ReportKind.SheetList,
            Title = includeClosed ? "Stock sheets (open and closed)" : "Open stock sheets",
            Summary = $"{Plural(sheets.Count(s => !s.Info.Closed), "open sheet")}"
                      + (includeClosed ? $" · {sheets.Count(s => s.Info.Closed)} closed" : "")
                      + " · double-click a sheet to see its items",
            Columns =
            [
                new("Sheet", ReportColumnKind.Upc), new("Cut-off date"), new("Comment"), new("Supervisor"),
                new("Status"), new("Items", ReportColumnKind.Number), new("Note"),
            ],
            Rows = sheets.Select(s => new object?[]
            {
                s.Info.Id, s.Info.CloseDate, s.Info.Comment, s.Info.Supervisor, s.Info.Closed ? "Closed" : "Open", s.Info.Lines, s.Note,
            }).ToList(),
        };
        Show(report);
        return new(report, sheets);
    }

    /// <summary>One sheet's items with over/short and the checks to look at before posting. Null if there's no such sheet.</summary>
    public async Task<SheetResult?> SheetAsync(int sheetId, CancellationToken ct = default)
    {
        var info = (await _lookup.Repository.GetStockSheetsAsync(includeClosed: true, ct).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == sheetId);
        if (info is null) return null;
        var sheet = Summarize(info);

        var raw = await _lookup.Repository.GetStockSheetLinesAsync(sheetId, ct).ConfigureAwait(false);
        bool truncated = raw.Count > SqlItemRepository.MaxReportRows;
        var kept = raw.Take(SqlItemRepository.MaxReportRows).ToList();
        int notInCache = kept.Count(l => _lookup.Cache.Get(l.Upc) is null);
        var names = await NamesAsync(kept.Select(l => l.Upc), ct).ConfigureAwait(false);
        var lines = kept.Select(l => l with { Description = names[l.Upc], SheetClosed = info.Closed }).OrderBy(l => l.Description).ToList();

        var checks = SheetChecks(sheet, lines);
        if (lines.Count >= 4 && _lookup.Cache.Count > 0 && notInCache * 2 > lines.Count)
        {
            checks.Insert(0, "Most item numbers on this sheet didn't match a barcode, so the names and costs may be wrong. The sheet query needs checking (see the app log).");
            _log?.Warn($"Stock sheet {sheetId}: {notInCache} of {lines.Count} Adjustments.Invennum values aren't barcodes in the name cache. Check that Invennum holds the barcode.");
        }
        if (truncated) checks.Add($"Only the first {SqlItemRepository.MaxReportRows} items are shown.");

        var result = new SheetResult(null!, sheet, lines, checks, truncated);
        var title = $"Stock sheet {info.Id}" + (info.Comment.Length > 0 ? $": {info.Comment}" : "") + (info.Closed ? " (closed)" : "");
        var report = new Report
        {
            Kind = ReportKind.Sheet,
            SheetId = info.Id,
            Title = title,
            Summary = $"{Plural(lines.Count, "item")} · {result.Over} over · {result.Short} short · {result.Unchanged} unchanged · " +
                      $"net {Signed(result.NetUnits)} units · {Money(result.NetValue)} at cost" +
                      (info.CloseDate is { } d ? $" · cut-off {d:MMM d, yyyy}" : ""),
            Notes = checks,
            Columns =
            [
                new("UPC", ReportColumnKind.Upc), new("Description"), new("System", ReportColumnKind.Number),
                new("Counted", ReportColumnKind.Number), new("Over/short", ReportColumnKind.Number), new("Value at cost", ReportColumnKind.Money),
            ],
            Rows = lines.Select(l => new object?[]
            {
                l.Upc, l.Description, l.System, l.Counted, l.OverShort, Math.Round(l.OverShort * l.UnitCost, 2),
            }).ToList(),
        };
        Show(report);
        _log?.Info($"Report: {title}: {report.Summary}");
        return result with { Report = report };
    }

    private SheetSummary Summarize(StockSheetInfo info) => new(
        info,
        _protectedSheets().Contains(info.Id),
        _appSheets().FirstOrDefault(s => s.SheetId == info.Id && !s.IsMock));

    /// <summary>What the owner should look at before posting a sheet.</summary>
    internal static List<string> SheetChecks(SheetSummary sheet, IReadOnlyList<StockSheetLine> lines)
    {
        var checks = new List<string>();
        if (sheet.Info.Closed)
            checks.Add("This sheet is closed in eStream. Reopening or posting it again can only be done in eStream (Inventory > Period Activity).");
        else if (sheet.Protected)
            checks.Add("This is one of the owner's own sheets (protected): the app only reads it, and it can't be posted from the app.");
        else if (sheet.MadeByApp is null)
            checks.Add("This sheet wasn't made by this app, so it can only be posted in eStream.");

        if (lines.Count == 0)
        {
            checks.Add("The sheet has no items yet.");
            return checks;
        }

        static string Names(IEnumerable<StockSheetLine> ls, Func<StockSheetLine, string> detail)
        {
            var list = ls.ToList();
            var shown = string.Join(", ", list.Take(5).Select(l => $"{l.Description} ({detail(l)})"));
            return list.Count > 5 ? shown + $" and {list.Count - 5} more" : shown;
        }

        var zeroed = lines.Where(l => l.Counted == 0 && l.System > 0).OrderByDescending(l => l.System).ToList();
        if (zeroed.Count > 0)
            checks.Add($"{Plural(zeroed.Count, "item")} counted 0 but the system has stock: {Names(zeroed, l => $"system {N(l.System)}")}. Check they weren't missed.");

        var big = lines.Where(l => Math.Abs(l.OverShort) >= BigUnits || Math.Abs(l.OverShort * l.UnitCost) >= BigValue)
            .OrderByDescending(l => Math.Abs(l.OverShort * l.UnitCost)).ThenByDescending(l => Math.Abs(l.OverShort)).ToList();
        if (big.Count > 0)
            checks.Add($"{Plural(big.Count, "big difference")} ({N(BigUnits)}+ units or {Money(BigValue)}+ at cost): {Names(big, l => Signed(l.OverShort))}.");

        var negative = lines.Where(l => l.System < 0).ToList();
        if (negative.Count > 0)
            checks.Add($"{Plural(negative.Count, "item")} had negative system stock: {Names(negative, l => $"system {N(l.System)}")}. Posting sets {(negative.Count == 1 ? "it" : "them")} to the count.");

        if (sheet.Info.Closed)
        {
            if (lines.Any(l => l.FromPosting))
                checks.Add("Posting set the sheet's system numbers to the counts, so Over/short shows what posting changed on hand by.");
        }
        else if (lines.All(l => l.OverShort == 0))
            checks.Add("Every count equals the system number. New sheets are pre-filled with system on-hand, so this sheet may not have been counted yet.");

        return checks;
    }

    /// <summary>Descriptions from the name cache; a few misses are looked up one by one.</summary>
    private async Task<Dictionary<long, string>> NamesAsync(IEnumerable<long> upcs, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        int lookups = 0;
        foreach (var upc in upcs.Distinct())
        {
            var cached = _lookup.Cache.Get(upc);
            if (cached is not null) { names[upc] = cached.Description; continue; }
            if (lookups++ < MaxNameLookups)
            {
                var item = await _lookup.Repository.GetByBarcodeAsync(upc, ct).ConfigureAwait(false);
                names[upc] = item?.Description ?? "(no item with this barcode)";
                continue;
            }
            names[upc] = "(name not loaded: refresh item names in Settings)";
        }
        return names;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    internal static string N(decimal v) => v.ToString("0.##", Inv);
    internal static string Signed(decimal v) => v > 0 ? "+" + N(v) : N(v);
    internal static string Money(decimal v) => v.ToString("C2", CultureInfo.GetCultureInfo("en-CA"));
    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
}
