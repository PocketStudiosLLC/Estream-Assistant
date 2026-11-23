using System.Globalization;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;
using EstreamAssistant.Core.Search;
using EstreamAssistant.Core.Upc;

namespace EstreamAssistant.Core.Receipts;

/// <summary>One line as Claude read it off the receipt (F5.3). Numbers are null when not printed or not readable.</summary>
public sealed record ReceiptLineInput
{
    /// <summary>Line number on the receipt, from 1. A later line with the same number replaces the earlier one.</summary>
    public required int Line { get; init; }
    public required string Text { get; init; }
    /// <summary>UPC or vendor item code as printed.</summary>
    public string? Code { get; init; }
    /// <summary>Number of cases (or units) bought.</summary>
    public decimal? Qty { get; init; }
    /// <summary>Price of ONE case/pack, as printed (not the line total).</summary>
    public decimal? CasePrice { get; init; }
    /// <summary>Price of one selling unit, when the receipt prints it that way.</summary>
    public decimal? UnitPrice { get; init; }
    public decimal? LineTotal { get; init; }
    /// <summary>False when the line can't be read with certainty. Its numbers are then ignored.</summary>
    public bool Readable { get; init; } = true;
    /// <summary>The eStream item the owner confirmed for this line, if any.</summary>
    public string? Upc { get; init; }
    public string? Note { get; init; }
}

/// <summary>How to work out a proposed price from the new unit cost.</summary>
public sealed record PriceRule(PricingMode Mode, decimal Pct, bool ApplyEndings)
{
    public string Text => $"{Pct.ToString("0.##", CultureInfo.InvariantCulture)}% {(Mode == PricingMode.Markup ? "markup" : "margin")}";
}

/// <summary>Counts for a receipt's stock sheet (Count = on hand + received), the lines they came from, and the lines left out.</summary>
public sealed record ReceivedCounts(IReadOnlyList<Counts.CountRow> Rows, IReadOnlyList<int> Lines, IReadOnlyList<string> Problems);

public enum ReceiptLineStatus
{
    /// <summary>Matched to an item, cost worked out.</summary>
    Ready,
    /// <summary>Matched, but something needs a look (see Warnings).</summary>
    Check,
    /// <summary>Several items could match: the owner must pick (S4).</summary>
    NeedsChoice,
    /// <summary>No eStream item found.</summary>
    NoMatch,
    /// <summary>Claude couldn't read the line: never guessed (F5.3).</summary>
    Unreadable,
    /// <summary>Matched an inactive item: can't be changed.</summary>
    Inactive,
}

public sealed record ReceiptCandidate(long Upc, string Description, int MatchPct);

/// <summary>A receipt line after matching and cost math (one row of the receipt table, F5.7).</summary>
public sealed record ReceiptLine
{
    public required ReceiptLineInput Input { get; init; }
    public ReceiptLineStatus Status { get; init; }
    public Item? Item { get; init; }
    /// <summary>How the item was found: "UPC", "name 92%", "picked by the owner".</summary>
    public string MatchedBy { get; init; } = "";
    public IReadOnlyList<ReceiptCandidate> Candidates { get; init; } = [];
    /// <summary>New cost of one selling unit, excluding deposit and eco fees (F5.5).</summary>
    public decimal? UnitCost { get; init; }
    public string CostMath { get; init; } = "";
    /// <summary>New case cost, offered but never assumed (F5.6).</summary>
    public decimal? NewCaseCost { get; init; }
    public decimal? CostChangePct { get; init; }
    /// <summary>GM% if the current price stays and the cost changes.</summary>
    public decimal? GmAtCurrentPrice { get; init; }
    public PriceRule? Rule { get; init; }
    public decimal? ProposedPrice { get; init; }
    public decimal? ProposedGm { get; init; }
    /// <summary>
    /// Selling units received on this line: qty × packs × units for a line priced per case, qty for a line priced
    /// per unit (or an item sold by the unit). Null when the qty or the case size is missing.
    /// </summary>
    public decimal? ReceivedUnits { get; init; }
    public string ReceivedMath { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Number => Input.Line;
    public bool CanStageCost => Item is { Inactive: false } && NewCaseCost is not null && Status is ReceiptLineStatus.Ready or ReceiptLineStatus.Check;
    public bool CanStagePrice => CanStageCost && ProposedPrice is not null;
}

/// <summary>
/// The open receipt and its table (spec 6.5). Claude reads the lines (show_receipt_lines);
/// the app matches them to items, does all the cost and price math, and flags what needs
/// the owner. Nothing here changes eStream: rows only reach Pending changes when the
/// owner or Claude stages them, and then the usual safety rules, dry run and SAVE apply.
/// </summary>
public sealed class ReceiptSession(ItemLookupService lookup, Func<IReadOnlyCollection<int>> priceEndings)
{
    public const int MaxLines = 120;
    private readonly object _gate = new();
    private readonly SortedDictionary<int, ReceiptLine> _lines = [];
    private readonly List<ReceiptFile> _files = [];
    /// <summary>Receipt lines already imported into a stock sheet (line → sheet number). Imports add up in eStream.</summary>
    private readonly Dictionary<int, int?> _onSheet = [];

    public event EventHandler? Changed;

    public string Source { get; private set; } = "";
    public bool IsOpen { get { lock (_gate) return _files.Count > 0 || _lines.Count > 0; } }
    public IReadOnlyList<ReceiptFile> Files { get { lock (_gate) return _files.ToList(); } }
    public IReadOnlyList<ReceiptLine> Lines { get { lock (_gate) return _lines.Values.ToList(); } }

    /// <summary>Receipt files the owner attached (more pages of the same receipt add to it).</summary>
    public void AddFiles(IEnumerable<ReceiptFile> files)
    {
        lock (_gate) _files.AddRange(files);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the receipt. Returns its files so the caller can file them in Receipts\Done.</summary>
    public IReadOnlyList<ReceiptFile> Finish()
    {
        List<ReceiptFile> files;
        lock (_gate)
        {
            files = _files.ToList();
            _files.Clear();
            _lines.Clear();
            _onSheet.Clear();
            Source = "";
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return files;
    }

    /// <summary>Adds or replaces lines (by line number), matching each to an item.</summary>
    public async Task<IReadOnlyList<ReceiptLine>> SetLinesAsync(string source, IReadOnlyList<ReceiptLineInput> inputs, CancellationToken ct = default)
    {
        var results = new List<ReceiptLine>();
        foreach (var input in inputs)
        {
            PriceRule? keepRule;
            lock (_gate) keepRule = _lines.TryGetValue(input.Line, out var old) ? old.Rule : null;
            var line = await BuildAsync(input, ct).ConfigureAwait(false);
            if (keepRule is not null && line.Item is not null) line = ApplyRule(line, keepRule);
            results.Add(line);
        }
        lock (_gate)
        {
            if (source.Trim().Length > 0) Source = source.Trim();
            foreach (var r in results)
            {
                if (!_lines.ContainsKey(r.Number) && _lines.Count >= MaxLines) continue;
                _lines[r.Number] = r;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return results;
    }

    /// <summary>The owner picked the item for a line (from its candidates or any UPC).</summary>
    public async Task<ReceiptLine?> ChooseAsync(int lineNumber, long upc, CancellationToken ct = default)
    {
        ReceiptLine? old;
        lock (_gate) old = _lines.GetValueOrDefault(lineNumber);
        if (old is null) return null;
        var line = await BuildAsync(old.Input with { Upc = upc.ToString(CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
        if (old.Rule is not null && line.Item is not null) line = ApplyRule(line, old.Rule);
        lock (_gate) _lines[lineNumber] = line;
        Changed?.Invoke(this, EventArgs.Empty);
        return line;
    }

    /// <summary>Works out proposed prices for some lines (or every matched line when <paramref name="lineNumbers"/> is empty).</summary>
    public IReadOnlyList<ReceiptLine> ApplyPriceRule(IReadOnlyCollection<int> lineNumbers, PriceRule rule)
    {
        var changed = new List<ReceiptLine>();
        lock (_gate)
        {
            foreach (var n in lineNumbers.Count == 0 ? _lines.Keys.ToList() : lineNumbers)
            {
                if (!_lines.TryGetValue(n, out var line) || line.Item is null || line.UnitCost is null) continue;
                _lines[n] = ApplyRule(line, rule);
                changed.Add(_lines[n]);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>Change requests for the chosen lines: the new case cost, plus the proposed price if wanted.</summary>
    public IReadOnlyList<ChangeRequest> ToRequests(IEnumerable<int> lineNumbers, bool includePrice)
    {
        var list = new List<ChangeRequest>();
        lock (_gate)
        {
            foreach (var n in lineNumbers)
            {
                if (!_lines.TryGetValue(n, out var l) || !l.CanStageCost) continue;
                list.Add(new ChangeRequest
                {
                    Upc = l.Item!.Upc.ToString(CultureInfo.InvariantCulture),
                    CaseCost = l.NewCaseCost,
                    Price = includePrice ? l.ProposedPrice : null,
                    Reason = $"Receipt{(Source.Length > 0 ? " " + Source : "")}, line {n}"
                        + (includePrice && l.Rule is not null ? $", {l.Rule.Text}" : ""),
                });
            }
        }
        return list;
    }

    public IReadOnlyDictionary<int, int?> LinesOnStockSheets { get { lock (_gate) return new Dictionary<int, int?>(_onSheet); } }

    /// <summary>
    /// Receipt → stock sheet (owner's decision, Phase C): the stock sheet line holds a COUNT, so
    /// Count = on hand right now + received units. When on hand is below 0 it is treated as 0 (the negative is
    /// written off): Count = received units. The sheet is posted "by over/short only", so sales made between
    /// making and posting the sheet are kept. One UPC on several lines: the received units are added first,
    /// on hand only once. Lines already on a stock sheet are left out (imports add up in eStream).
    /// <paramref name="lineNumbers"/> empty = every line.
    /// </summary>
    public async Task<ReceivedCounts> ToReceivedCountsAsync(IReadOnlyCollection<int> lineNumbers, CancellationToken ct = default)
    {
        List<ReceiptLine> lines;
        Dictionary<int, int?> onSheet;
        lock (_gate)
        {
            lines = (lineNumbers.Count == 0 ? _lines.Values : lineNumbers.Where(_lines.ContainsKey).Select(n => _lines[n])).ToList();
            onSheet = new(_onSheet);
        }
        var problems = lineNumbers.Where(n => lines.All(l => l.Number != n)).Order().Select(n => $"Line {n} isn't on the receipt.").ToList();
        var take = new List<ReceiptLine>();
        foreach (var l in lines)
        {
            var name = l.Item?.Description ?? l.Input.Text;
            if (onSheet.TryGetValue(l.Number, out var sheet)) problems.Add($"Line {l.Number} ({name}) is already on stock sheet {sheet?.ToString(CultureInfo.InvariantCulture) ?? "(number unknown)"}.");
            else if (l.Status == ReceiptLineStatus.Unreadable) problems.Add($"Line {l.Number} couldn't be read.");
            else if (l.Item is null) problems.Add($"Line {l.Number} ({name}): no eStream item picked yet.");
            else if (l.Item.Inactive) problems.Add($"Line {l.Number} ({name}) is inactive.");
            else if (l.ReceivedUnits is not > 0) problems.Add($"Line {l.Number} ({name}): no quantity{(l.Input.Qty is > 0 ? " (eStream has no case size for it)" : "")}, so nothing to add.");
            else take.Add(l);
        }

        var rows = new List<Counts.CountRow>();
        var used = new List<int>();
        foreach (var g in take.GroupBy(l => l.Item!.Upc))
        {
            var received = g.Sum(l => l.ReceivedUnits!.Value);
            // Fresh on hand: it may have changed since the receipt was read.
            var item = await lookup.Repository.GetByBarcodeAsync(g.Key, ct).ConfigureAwait(false) ?? g.First().Item!;
            var start = Math.Max(0, item.OnHand);
            var count = start + received;
            if (count > 100_000) { problems.Add($"{item.Description}: count {N(count)} is too big. Check the quantity."); continue; }
            var from = g.Count() == 1 ? $"received {N(received)}" : $"received {N(received)} (lines {string.Join(", ", g.Select(l => l.Number))})";
            var note = item.OnHand < 0
                ? $"on hand {N(item.OnHand)} treated as 0 + {from} = count {N(count)}"
                : $"on hand {N(item.OnHand)} + {from} = count {N(count)}";
            rows.Add(new Counts.CountRow(item.Upc, item.Description, count, item.OnHand, item.UnitCost, DateTime.Now, item.Sold, note));
            used.AddRange(g.Select(l => l.Number));
        }
        return new(rows, used.Order().ToList(), problems);
    }

    /// <summary>
    /// Receipt → stock sheet, stage 1 only (the Stock counts tab posts it, with its own check and typed number).
    /// The owner confirms the counts first (StockCounts' confirmation window). Used by the receipt table's button
    /// and by Claude (receipt_stock_sheet).
    /// </summary>
    public async Task<(Counts.SheetRun Run, ReceivedCounts Counts)> MakeStockSheetAsync(Counts.StockCounts counts,
        IReadOnlyCollection<int> lineNumbers, CancellationToken ct = default)
    {
        var rc = await ToReceivedCountsAsync(lineNumbers, ct).ConfigureAwait(false);
        if (rc.Rows.Count == 0)
            return (new(false, "Nothing to add from this receipt. " + string.Join(" ", rc.Problems.Take(6)), ""), rc);
        string source;
        lock (_gate) source = Source;
        var comment = ("Receipt " + source).Trim();
        var notes = new List<string>
        {
            "Receipt: each count is on hand now + the units received (a negative on hand counts as 0). " +
            "Posting adds only the difference, so sales made in between are kept. Post it on the Stock counts tab.",
        };
        notes.AddRange(rc.Problems.Select(p => "Left out: " + p));
        var run = await counts.CreateSheetAsync(rc.Rows, comment, ct, notes).ConfigureAwait(false);
        if (run.SheetId is not null || counts.HasSheetFor(rc.Rows)) MarkOnStockSheet(rc.Lines, run.SheetId);
        return (run, rc);
    }

    /// <summary>After a stock sheet was made from these lines: they can't go on another sheet.</summary>
    public void MarkOnStockSheet(IEnumerable<int> lineNumbers, int? sheetId)
    {
        lock (_gate) foreach (var n in lineNumbers) _onSheet[n] = sheetId;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ReceiptLine ApplyRule(ReceiptLine line, PriceRule rule)
    {
        var item = line.Item!;
        var unitCost = line.UnitCost!.Value;
        // The price is for the item's For quantity ("2 for $5"), so price that many units.
        var r = PricingCalculator.Calculate(unitCost * Math.Max(1, item.For), rule.Mode, rule.Pct,
            rule.ApplyEndings ? priceEndings() : null);
        return line with { Rule = rule, ProposedPrice = r.Price, ProposedGm = r.GmPct };
    }

    private async Task<ReceiptLine> BuildAsync(ReceiptLineInput input, CancellationToken ct)
    {
        if (!input.Readable)
            return new ReceiptLine
            {
                Input = input,
                Status = ReceiptLineStatus.Unreadable,
                Warnings = ["Couldn't read this line. Check the receipt and type it in, or skip it."],
            };

        // F5.4: UPC first (the owner's pick, then the printed code), then the name.
        Item? item = null;
        string matchedBy = "";
        var candidates = new List<ReceiptCandidate>();
        // input.Upc is only set for an item the owner confirmed (here or in the chat).
        foreach (var (code, confirmed) in new[] { (input.Upc, true), (input.Code, false) })
        {
            if (code is null || !UpcNormalizer.LooksLikeUpc(code)) continue;
            var r = await lookup.LookupUpcAsync(code, ct).ConfigureAwait(false);
            if (r.Item is null) continue;
            item = r.Item;
            matchedBy = confirmed ? "picked" : "UPC";
            break;
        }
        if (item is null && input.Text.Trim().Length > 0)
        {
            var found = await lookup.FindAsync(input.Text, 4, ct).ConfigureAwait(false);
            candidates = found.Matches.Select(m => new ReceiptCandidate(m.Item.Upc, m.Item.Description, m.MatchPercent)).ToList();
            if (found.IsConfident)
            {
                var best = await lookup.LookupUpcAsync(found.Matches[0].Item.Upc.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                item = best.Item;
                matchedBy = $"name {found.Matches[0].MatchPercent}%";
            }
        }

        if (item is null)
            return new ReceiptLine
            {
                Input = input,
                Status = candidates.Count > 0 ? ReceiptLineStatus.NeedsChoice : ReceiptLineStatus.NoMatch,
                Candidates = candidates,
                Warnings = [candidates.Count > 0
                    ? "Not sure which item this is: pick one."
                    : "No eStream item matches. Scan it, or add it in eStream by hand."],
            };

        var warnings = new List<string>();
        if (item.Inactive) warnings.Add("This item is INACTIVE in eStream, so it can't be changed.");

        var units = item.PacksPerCase * item.UnitsPerPack;
        decimal? unitCost = null, caseCost = null;
        string math = "";
        if (input.UnitPrice is { } up)
        {
            unitCost = up;
            caseCost = units > 0 ? PricingCalculator.RoundToCent(up * units) : null;
            math = units > 1 ? $"unit price {Money(up)} × {N(units)} = case {Money(caseCost!.Value)}" : $"unit price {Money(up)}";
        }
        else if (input.CasePrice is { } cp)
        {
            if (units <= 0)
            {
                warnings.Add("eStream has no case size for this item, so the unit cost can't be worked out.");
            }
            else
            {
                unitCost = Math.Round(cp / units, 4, MidpointRounding.AwayFromZero);
                caseCost = cp;
                math = $"{Money(cp)} ÷ ({N(item.PacksPerCase)} × {N(item.UnitsPerPack)}) = {Money4(unitCost.Value)}";
                if (units == 1)
                    warnings.Add("eStream has 1 pack × 1 unit for this item. If it comes by the case, ask how many are in a case before using this cost.");
            }
        }
        else
        {
            warnings.Add("No price was read for this line.");
        }

        if (input.Qty is { } q && input.LineTotal is { } total && (input.CasePrice ?? input.UnitPrice) is { } each
            && Math.Abs(q * each - total) > 0.02m)
            warnings.Add($"Qty × price ({N(q)} × {Money(each)} = {Money(q * each)}) doesn't match the line total {Money(total)}. Check this line.");

        decimal? changePct = null, gmNow = null;
        if (unitCost is { } uc)
        {
            if (item.UnitCost > 0)
            {
                changePct = Math.Round((uc - item.UnitCost) / item.UnitCost * 100m, 1);
                if (Math.Abs(changePct.Value) > 50)
                    warnings.Add($"The cost changes by {changePct:0.#}%. Check the case size and this line.");
            }
            if (item.Price > 0) gmNow = PricingCalculator.GrossMarginPct(item.Price / Math.Max(1, item.For), uc);
        }

        decimal? receivedUnits = null;
        string receivedMath = "";
        if (input.Qty is { } qty && qty > 0)
        {
            if (input.UnitPrice is not null || units == 1) { receivedUnits = qty; receivedMath = $"{N(qty)} unit{(qty == 1 ? "" : "s")}"; }
            else if (units > 1) { receivedUnits = qty * units; receivedMath = $"{N(qty)} × {N(units)} = {N(receivedUnits.Value)} units"; }
        }

        var status = item.Inactive ? ReceiptLineStatus.Inactive
            : warnings.Count > 0 ? ReceiptLineStatus.Check
            : ReceiptLineStatus.Ready;
        return new ReceiptLine
        {
            Input = input,
            Status = status,
            Item = item,
            MatchedBy = matchedBy == "picked" ? "picked by the owner" : matchedBy,
            Candidates = matchedBy.StartsWith("name", StringComparison.Ordinal) ? candidates : [],
            UnitCost = unitCost,
            CostMath = math,
            NewCaseCost = caseCost,
            CostChangePct = changePct,
            GmAtCurrentPrice = gmNow,
            ReceivedUnits = receivedUnits,
            ReceivedMath = receivedMath,
            Warnings = warnings,
        };
    }

    private static readonly CultureInfo Ca = CultureInfo.GetCultureInfo("en-CA");
    internal static string Money(decimal v) => v.ToString("C2", Ca);
    internal static string Money4(decimal v) => v == Math.Round(v, 2) ? Money(v) : "$" + v.ToString("0.00##", CultureInfo.InvariantCulture);
    private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
