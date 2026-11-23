using System.Globalization;
using System.Text;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;

namespace EstreamAssistant.Core.Reports;

public sealed record OrderResult(Report Report, VendorInfo Vendor, IReadOnlyList<OrderSuggestion> Lines, int Skipped, decimal TotalCost);

public sealed record MarginRow(ItemFacts Item, string Description, decimal UnitPrice, decimal Gm, decimal Target, string Problem);

/// <summary>Phase B: store-wide read-only reports (suggested order, margins, stock value, promotions, sales history).</summary>
public sealed partial class ReportService
{
    /// <summary>Most rows the store-wide lists show.</summary>
    public const int MaxListRows = 5000;

    // --- suggested order ------------------------------------------------------------

    public async Task<Report> VendorListAsync(CancellationToken ct = default)
    {
        var vendors = await _lookup.Repository.GetVendorsAsync(ct).ConfigureAwait(false);
        var report = new Report
        {
            Kind = ReportKind.Other,
            Title = "Vendors",
            Summary = $"{Plural(vendors.Count(v => !v.Inactive), "active vendor")} · pick one for a suggested order",
            Columns = [new("Vendor #", ReportColumnKind.Upc), new("Name"), new("Items", ReportColumnKind.Number), new("Status")],
            Rows = vendors.OrderBy(v => v.Inactive).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Select(v => new object?[] { (long)v.Id, v.Name, (decimal)v.Items, v.Inactive ? "Inactive" : "" }).ToList(),
        };
        Show(report);
        return report;
    }

    /// <summary>Finds a vendor by number or by part of its name. Null = none; several = ask.</summary>
    public async Task<(VendorInfo? Vendor, IReadOnlyList<VendorInfo> Matches)> FindVendorAsync(string text, CancellationToken ct = default)
    {
        var vendors = await _lookup.Repository.GetVendorsAsync(ct).ConfigureAwait(false);
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return (vendors.FirstOrDefault(v => v.Id == id), []);
        var key = VendorKey(text);
        var matches = vendors.Where(v => VendorKey(v.Name).Contains(key, StringComparison.Ordinal)).ToList();
        var exact = matches.Where(v => VendorKey(v.Name) == key).ToList();
        if (exact.Count == 1) return (exact[0], []);
        var active = matches.Where(v => !v.Inactive).ToList();
        if (active.Count == 1) return (active[0], []);
        return (null, matches);
    }

    public async Task<OrderResult> OrderAsync(VendorInfo vendor, OrderSettings settings, CancellationToken ct = default)
    {
        var from = DateTime.Today.AddDays(-(Math.Max(1, settings.HistoryDays) - 1));
        var lines = await _lookup.Repository.GetOrderLinesAsync(vendor.Id, from, ct).ConfigureAwait(false);
        int skipped = lines.Count(l => OrderCalculator.Skip(l) is not null);
        var suggestions = lines.Select(l => OrderCalculator.Suggest(l, settings)).OfType<OrderSuggestion>().ToList();
        var names = await NamesAsync(suggestions.Select(s => s.Line.Upc), ct).ConfigureAwait(false);
        suggestions = suggestions.Select(s => s with { Line = s.Line with { Description = names[s.Line.Upc] } })
            .OrderBy(s => s.Line.VendorCode, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line.Description).ToList();
        decimal total = suggestions.Sum(s => s.Cost);
        bool history = settings.Method == OrderMethod.SalesHistory;

        var cols = new List<ReportColumn>
        {
            new("UPC", ReportColumnKind.Upc), new("Shelf code"), new("Description"), new("On hand", ReportColumnKind.Number),
            new("Min", ReportColumnKind.Number), new("Max", ReportColumnKind.Number),
        };
        if (history) cols.Add(new($"Sold ({settings.HistoryDays} days)", ReportColumnKind.Number));
        cols.AddRange([new("Order units", ReportColumnKind.Number), new("Cases", ReportColumnKind.Number),
            new("Case cost", ReportColumnKind.Money), new("Est. cost", ReportColumnKind.Money), new("Why")]);

        var rows = suggestions.Select(s =>
        {
            var c = new List<object?> { s.Line.Upc, s.Line.VendorCode, s.Line.Description, s.Line.OnHand, s.Line.Min, s.Line.Max };
            if (history) c.Add(s.Line.SoldInPeriod);
            c.AddRange([s.Units, s.Cases, s.Line.CaseCost, s.Cost, s.Why]);
            return c.ToArray();
        }).ToList();

        var notes = new List<string>
        {
            "This is a suggestion only: nothing is ordered. Check it, then make the purchase order in eStream (Vendors > Purchase Orders).",
            "Save the UPC,Count file to import these quantities on eStream's Purchase Order screen (Import CSV).",
        };
        if (skipped > 0) notes.Add($"{Plural(skipped, "item")} left out: inactive, back ordered or marked Do Not Order for this vendor.");
        if (settings.Method != OrderMethod.SalesHistory && lines.Count(l => l.Max <= 0 && OrderCalculator.Skip(l) is null) is var noMax and > 0)
            notes.Add($"{Plural(noMax, "item")} with no Re-Order/Max set can't be ordered this way. Try the sales history method for those.");

        var report = new Report
        {
            Kind = ReportKind.Items,
            Title = $"Suggested order: {vendor.Name}",
            Summary = $"{Plural(suggestions.Count, "item")} · {Money(total)} at cost · {OrderSettings.Label(settings.Method)}"
                      + (history ? $", {settings.HistoryDays} days of sales, {settings.DaysNeeded} days of stock" : ""),
            Notes = notes,
            Columns = cols,
            Rows = rows,
            ImportCsv = UpcCountCsv(suggestions.Select(s => (s.Line.Upc, s.Units))),
            ImportHint = "Purchase Order screen > Import",
        };
        Show(report);
        _log?.Info($"Report: {report.Title}: {report.Summary}");
        return new OrderResult(report, vendor, suggestions, skipped, total);
    }

    /// <summary>UPC,Count with CRLF: the format eStream's PO, transfer, credit note and stock sheet screens import.</summary>
    public static string UpcCountCsv(IEnumerable<(long Upc, decimal Count)> rows)
    {
        var sb = new StringBuilder("UPC,Count\r\n");
        foreach (var (upc, count) in rows)
            sb.Append(upc.ToString(CultureInfo.InvariantCulture)).Append(',').Append(count.ToString("0.##", CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    // --- margins --------------------------------------------------------------------

    /// <summary>Problems in one item's price/cost, or null when it looks fine.</summary>
    internal static string? MarginProblem(ItemFacts f, decimal target, out decimal unitPrice, out decimal gm)
    {
        unitPrice = f.For > 0 ? Math.Round(f.Price / f.For, 4) : f.Price;
        var cost = f.SellCost; // per item as sold (eStream's StoreGM is per pack: wrong for "1 × N" items)
        gm = PricingCalculator.GrossMarginPct(unitPrice, cost);
        if (f.Price <= 0) return "No price";
        if (cost <= 0) return "No cost";
        if (f.PacksPerCase <= 1 && f.UnitsPerPack <= 1 && f.CaseCost > f.Price * 2) return "Check case size (1 × 1)";
        if (cost > unitPrice) return "Below cost";
        if (cost == unitPrice) return "At cost";
        if (target > 0 && gm < target) return "Below target";
        return null;
    }

    private static int ProblemOrder(string p) => p switch
    {
        "Below cost" => 0, "At cost" => 1, "Check case size (1 × 1)" => 2, "No cost" => 3, "No price" => 4, _ => 5,
    };

    public async Task<(Report Report, IReadOnlyList<MarginRow> Rows)> MarginsAsync(bool includeBelowTarget, CancellationToken ct = default)
    {
        var facts = await _lookup.Repository.GetItemFactsAsync(ct).ConfigureAwait(false);
        var cats = (await _lookup.Repository.GetCategoriesAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Id);
        var found = new List<(ItemFacts F, decimal Price, decimal Gm, decimal Target, string Problem)>();
        foreach (var f in facts)
        {
            var target = cats.TryGetValue(f.CategoryId, out var c) ? c.TargetGp : 0;
            var p = MarginProblem(f, target, out var unitPrice, out var gm);
            if (p is null || (!includeBelowTarget && p == "Below target")) continue;
            found.Add((f, unitPrice, gm, target, p));
        }
        var sorted = found.OrderBy(x => ProblemOrder(x.Problem)).ThenBy(x => x.Gm - x.Target).ThenByDescending(x => x.F.Sold).ToList();
        var shown = sorted.Take(MaxListRows).ToList();
        var names = await NamesAsync(shown.Select(x => x.F.Upc), ct).ConfigureAwait(false);
        var rows = shown.Select(x => new MarginRow(x.F, names[x.F.Upc], x.Price, x.Gm, x.Target, x.Problem)).ToList();

        var counts = sorted.GroupBy(x => x.Problem).OrderBy(g => ProblemOrder(g.Key)).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        var report = new Report
        {
            Kind = ReportKind.Items,
            Title = includeBelowTarget ? "Margin check (with category targets)" : "Margin check",
            Summary = sorted.Count == 0 ? "No problems found." : string.Join(" · ", counts) + (sorted.Count > MaxListRows ? $" · first {MaxListRows} shown" : ""),
            Notes =
            [
                "GM% is worked out like eStream: (price ÷ For − unit cost) ÷ (price ÷ For). eStream shows 0 for items below cost.",
                .. includeBelowTarget ? new[] { "Targets are the category's Target GP% (or its department's when the category has none)." } : [],
            ],
            Columns =
            [
                new("UPC", ReportColumnKind.Upc), new("Description"), new("Price", ReportColumnKind.Money), new("For", ReportColumnKind.Number),
                new("Unit cost", ReportColumnKind.Money), new("GM%", ReportColumnKind.Number), new("Target GM%", ReportColumnKind.Number),
                new("Sold", ReportColumnKind.Number), new("Problem"),
            ],
            Rows = rows.Select(r => new object?[]
            {
                r.Item.Upc, r.Description, r.Item.Price, (decimal)r.Item.For, r.Item.UnitCost, r.Gm, r.Target > 0 ? r.Target : null, r.Item.Sold, r.Problem,
            }).ToList(),
        };
        Show(report);
        _log?.Info($"Report: {report.Title}: {report.Summary}");
        return (report, rows);
    }

    // --- stock value ------------------------------------------------------------------

    public async Task<Report> StockValueAsync(CancellationToken ct = default)
    {
        var facts = await _lookup.Repository.GetItemFactsAsync(ct).ConfigureAwait(false);
        var cats = (await _lookup.Repository.GetCategoriesAsync(ct).ConfigureAwait(false)).ToDictionary(c => c.Id);
        var groups = facts.GroupBy(f => f.CategoryId).Select(g =>
        {
            var on = g.Where(f => f.OnHand > 0).ToList();
            return new
            {
                Name = cats.TryGetValue(g.Key, out var c) ? c.Name : g.Key == 0 ? "(no category)" : $"Category {g.Key}",
                Dept = c?.DeptName ?? "",
                Items = g.Count(),
                Units = on.Sum(f => f.OnHand),
                Cost = Math.Round(on.Sum(f => f.OnHand * f.UnitCost), 2),
                Retail = Math.Round(on.Sum(f => f.OnHand * (f.For > 0 ? f.Price / f.For : f.Price)), 2),
                Negative = g.Count(f => f.OnHand < 0),
            };
        }).OrderByDescending(g => g.Cost).ToList();
        decimal cost = groups.Sum(g => g.Cost), retail = groups.Sum(g => g.Retail);
        var report = new Report
        {
            Kind = ReportKind.Other,
            Title = "Stock value by category",
            Summary = $"{Money(cost)} at cost · {Money(retail)} at retail · {Plural(facts.Count, "active item")}",
            Notes =
            [
                "Only stock on hand above 0 is counted. Negative stock is shown as a count; it usually means sales of stock that was never received.",
                "The numbers are only as good as eStream's on hand and costs.",
            ],
            Columns =
            [
                new("Category"), new("Department"), new("Items", ReportColumnKind.Number), new("Units on hand", ReportColumnKind.Number),
                new("Value at cost", ReportColumnKind.Money), new("Value at retail", ReportColumnKind.Money), new("Items negative", ReportColumnKind.Number),
            ],
            Rows = groups.Select(g => new object?[] { g.Name, g.Dept, (decimal)g.Items, g.Units, g.Cost, g.Retail, (decimal)g.Negative }).ToList(),
        };
        Show(report);
        _log?.Info($"Report: {report.Title}: {report.Summary}");
        return report;
    }

    // --- promotions -----------------------------------------------------------------

    public async Task<Report> MixMatchListAsync(CancellationToken ct = default)
    {
        var groups = await _lookup.Repository.GetMixMatchGroupsAsync(ct).ConfigureAwait(false);
        var used = groups.Where(g => g.Items > 0).OrderByDescending(g => g.Items).ToList();
        var report = new Report
        {
            Kind = ReportKind.Other,
            Title = "Mix match groups",
            Summary = $"{Plural(used.Count, "group")} with active items · {groups.Count - used.Count} empty",
            Notes = ["Every item in a group has the same price: changing one item's price in eStream (or here) changes them all."],
            Columns = [new("Group", ReportColumnKind.Upc), new("Description"), new("Group price", ReportColumnKind.Money), new("Active items", ReportColumnKind.Number)],
            Rows = used.Select(g => new object?[] { (long)g.Id, g.Description, g.Price > 0 ? g.Price : null, (decimal)g.Items }).ToList(),
        };
        Show(report);
        return report;
    }

    public async Task<Report> SpecialsListAsync(CancellationToken ct = default)
    {
        var events = await _lookup.Repository.GetSpecialEventsAsync(ct).ConfigureAwait(false);
        var now = DateTime.Now;
        var report = new Report
        {
            Kind = ReportKind.Other,
            Title = "Special events",
            Summary = $"{events.Count(e => e.Status(now) == "On now")} on now · {events.Count(e => e.Status(now) == "Coming")} coming · {events.Count(e => e.Status(now) == "Ended")} ended in the last week",
            Notes = ["While an event runs, the register charges its special price, not the regular price. The cheapest price wins."],
            Columns = [new("Event", ReportColumnKind.Upc), new("Title"), new("Starts"), new("Ends"), new("Items", ReportColumnKind.Number), new("Status")],
            Rows = events.Select(e => new object?[] { (long)e.Id, e.Title, e.Start, e.End, (decimal)e.Items, e.Status(now) }).ToList(),
        };
        Show(report);
        return report;
    }

    public async Task<Report> BatchListAsync(CancellationToken ct = default)
    {
        var batches = await _lookup.Repository.GetPendingBatchesAsync(ct).ConfigureAwait(false);
        var now = DateTime.Now;
        int auto = batches.Count(b => b.AppliesAutomatically(now));
        var report = new Report
        {
            Kind = ReportKind.Other,
            Title = "Pending price batches",
            Summary = $"{Plural(batches.Count, "batch", "batches")} not applied · {auto} set to apply automatically",
            Notes =
            [
                "eStream applies a pending batch by itself when its Apply On date comes (today or later). Other pending batches only apply if someone applies them.",
                "A batch replaces the price and cost of its items, including changes made since the batch was made.",
            ],
            Columns = [new("Batch", ReportColumnKind.Upc), new("Apply on"), new("Comment"), new("Items", ReportColumnKind.Number), new("Applies by itself")],
            Rows = batches.Select(b => new object?[] { (long)b.Id, b.ApplyOn, b.Comment, (decimal)b.Items, b.AppliesAutomatically(now) ? "Yes" : "" }).ToList(),
        };
        Show(report);
        return report;
    }

    // --- labels flagged in eStream ------------------------------------------------------

    public async Task<Report> EStreamLabelFlagsAsync(CancellationToken ct = default)
    {
        var facts = (await _lookup.Repository.GetItemFactsAsync(ct).ConfigureAwait(false)).Where(f => f.MakeLabel).Take(MaxListRows).ToList();
        var names = await NamesAsync(facts.Select(f => f.Upc), ct).ConfigureAwait(false);
        var report = new Report
        {
            Kind = ReportKind.Items,
            Title = "Items flagged for labels in eStream",
            Summary = $"{Plural(facts.Count, "item")} with Make Label set in eStream",
            Notes = ["eStream sets this when a price changes there. Print them in eStream (Labels and Signs), which clears the flag."],
            Columns = [new("UPC", ReportColumnKind.Upc), new("Description"), new("Price", ReportColumnKind.Money), new("For", ReportColumnKind.Number)],
            Rows = facts.OrderBy(f => names[f.Upc]).Select(f => new object?[] { f.Upc, names[f.Upc], f.Price, (decimal)f.For }).ToList(),
        };
        Show(report);
        return report;
    }

    // --- one item's sales ---------------------------------------------------------------

    public async Task<(Report Report, IReadOnlyList<DailySales> Days)> SalesHistoryAsync(Item item, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 366);
        var list = await _lookup.Repository.GetDailySalesAsync(item.Upc, DateTime.Today.AddDays(-(days - 1)), ct).ConfigureAwait(false);
        decimal qty = list.Sum(d => d.Quantity), amount = list.Sum(d => d.Amount);
        var perDay = Math.Round(qty / days, 2);
        var report = new Report
        {
            Kind = ReportKind.Other,
            Title = $"Sales: {item.Description}",
            Summary = $"{N(qty)} sold in the last {days} days ({N(perDay)} a day) · {Money(amount)}"
                      + (perDay > 0 && item.OnHand > 0 ? $" · on hand {N(item.OnHand)} lasts about {N(Math.Floor(item.OnHand / perDay))} days" : ""),
            Notes = ["From the registers' polled sales (ItemSales). Days with no sales aren't listed."],
            Columns = [new("Day"), new("Units", ReportColumnKind.Number), new("Sales", ReportColumnKind.Money)],
            Rows = list.Where(d => d.Quantity != 0 || d.Amount != 0).Select(d => new object?[] { d.Day.Date, d.Quantity, d.Amount }).ToList(),
        };
        Show(report);
        return (report, list);
    }

    /// <summary>Upper case letters and digits only, so "Frito-Lay" finds "FRITO LAY".</summary>
    private static string VendorKey(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Plural(int n, string word, string plural) => $"{n} {(n == 1 ? word : plural)}";
}
