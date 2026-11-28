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
    /// <summary>The picture the line is on, as numbered for Claude ([picture N]); 0 when not known.</summary>
    public int Picture { get; init; }
    /// <summary>Where the line is on that picture: percent of its height from the top (0..100). Top &lt; Bottom when known.</summary>
    public int Top { get; init; }
    public int Bottom { get; init; }
    /// <summary>The owner left this line out (not on any stock sheet, no more questions).</summary>
    public bool Skipped { get; init; }
    /// <summary>The owner checked the line against the receipt: its warnings are answered.</summary>
    public bool Confirmed { get; init; }
    /// <summary>Selling units received, typed by the owner (beats cases × packs × units).</summary>
    public decimal? Units { get; init; }

    public bool HasPosition => Picture > 0 && Top >= 0 && Bottom > Top && Bottom <= 100;
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
    /// <summary>The quantity used: as printed, or worked out when none was printed (see <see cref="QtyAssumed"/>).</summary>
    public decimal? Qty { get; init; }
    /// <summary>
    /// No count was printed: till receipts (Costco, Superstore) print one line per scan and only print a count
    /// ("10 @ 2.59") for several rung up at once, so the line is 1, or line total ÷ price when that's whole.
    /// </summary>
    public bool QtyAssumed { get; init; }
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

    /// <summary>Picture number (as labelled for Claude) → (file, picture of that file), for receipts sent in this run.</summary>
    private readonly Dictionary<int, (int File, int Part)> _pictures = [];

    public event EventHandler? Changed;

    public string Source { get; private set; } = "";
    /// <summary>The store or supplier printed on the receipt ("Superstore", "Costco"), as Claude read it.</summary>
    public string Store { get; private set; } = "";
    /// <summary>The date printed on the receipt, when Claude could read it.</summary>
    public DateOnly? Date { get; private set; }
    /// <summary>The receipt's name: "Superstore · Nov 25, 2026", or Claude's short name for it.</summary>
    public string Title { get { lock (_gate) return Name.Length > 0 ? Name : ReceiptNames.Title(Store, Date, Source); } }
    /// <summary>The owner's own name for the receipt (Pending tab); empty = store and date.</summary>
    public string Name { get; set; } = "";

    /// <summary>Store and date as printed (an empty store or a null date keeps what was there).</summary>
    public void SetInfo(string store, DateOnly? date)
    {
        lock (_gate)
        {
            if (store.Trim().Length > 0) Store = store.Trim();
            if (date is not null) Date = date;
        }
    }
    public bool IsOpen { get { lock (_gate) return _files.Count > 0 || _lines.Count > 0; } }
    public IReadOnlyList<ReceiptFile> Files { get { lock (_gate) return _files.ToList(); } }
    public IReadOnlyList<ReceiptLine> Lines { get { lock (_gate) return _lines.Values.ToList(); } }

    /// <summary>Receipt files the owner attached (more pages of the same receipt add to it).</summary>
    /// <param name="pictures">For each file, the picture numbers it was labelled with for Claude (one per picture sent).</param>
    public void AddFiles(IEnumerable<ReceiptFile> files, IReadOnlyList<IReadOnlyList<int>>? pictures = null)
    {
        lock (_gate)
        {
            var list = files.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (pictures is not null && i < pictures.Count)
                    for (int k = 0; k < pictures[i].Count; k++) _pictures[pictures[i][k]] = (_files.Count, k);
                _files.Add(list[i]);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Which file and which of its pictures a picture number is, or null when it isn't one of this receipt's.</summary>
    public (int File, int Part)? PictureOf(int number)
    {
        lock (_gate) return _pictures.TryGetValue(number, out var p) ? p : null;
    }

    /// <summary>
    /// Opens a receipt saved for later: its lines are matched again (eStream may have changed since: an item added,
    /// a case size fixed) and its stock sheet marks come back.
    /// </summary>
    public async Task RestoreAsync(string source, IReadOnlyList<ReceiptLineInput> inputs, IReadOnlyDictionary<int, int?> onSheet,
        string store = "", DateOnly? date = null, CancellationToken ct = default)
    {
        SetInfo(store, date);
        await SetLinesAsync(source, inputs, ct).ConfigureAwait(false);
        lock (_gate) foreach (var (n, id) in onSheet) _onSheet[n] = id;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The owner answered a question about a line: replaces its input and matches it again.</summary>
    public async Task<ReceiptLine?> UpdateLineAsync(int lineNumber, Func<ReceiptLineInput, ReceiptLineInput> change, CancellationToken ct = default)
    {
        ReceiptLine? old;
        lock (_gate) old = _lines.GetValueOrDefault(lineNumber);
        if (old is null) return null;
        var lines = await SetLinesAsync("", [change(old.Input) with { Line = lineNumber }], ct).ConfigureAwait(false);
        return lines.FirstOrDefault();
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
            _pictures.Clear();
            Source = "";
            Store = "";
            Date = null;
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
                    Reason = $"Receipt{(Title.Length > 0 ? " " + Title : "")}, line {n}"
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
            if (l.Input.Skipped) problems.Add($"Line {l.Number} ({name}) was left out by the owner.");
            else if (onSheet.TryGetValue(l.Number, out var sheet)) problems.Add($"Line {l.Number} ({name}) is already on stock sheet {sheet?.ToString(CultureInfo.InvariantCulture) ?? "(number unknown)"}.");
            else if (l.Status == ReceiptLineStatus.Unreadable) problems.Add($"Line {l.Number} couldn't be read.");
            else if (l.Item is null) problems.Add($"Line {l.Number} ({name}): no item picked.");
            else if (l.Item.Inactive) problems.Add($"Line {l.Number} ({name}) is inactive.");
            else if (l.ReceivedUnits is not > 0) problems.Add($"Line {l.Number} ({name}): no {(l.Qty is > 0 ? "case size in eStream" : "quantity")}.");
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
        string comment;
        lock (_gate) comment = ReceiptNames.SheetComment(Store, Date, Source, Name);
        var notes = new List<string>
        {
            "Receipt: count = on hand + received (negative on hand counts as 0). Post it on the Stock counts tab.",
        };
        // Owner's rule: a negative on hand counts as 0, so posting adds MORE than was received. Fine for a fresh
        // delivery; for an old receipt those goods were probably sold already (test 3: a 4-week-old Costco receipt,
        // HALF & HALF on hand -36 + 10 received -> +46).
        var negative = rc.Rows.Where(r => r.SystemOnHand < 0).ToList();
        if (negative.Count > 0)
            notes.Add($"NEGATIVE on hand, so posting adds more than received: " +
                string.Join("; ", negative.Take(5).Select(r => $"{r.Description} {N(r.SystemOnHand)} → {N(r.Count)} adds {N(r.Count - r.SystemOnHand)}")) +
                (negative.Count > 5 ? $"; and {negative.Count - 5} more" : "") +
                ". If those goods are already sold, cancel and leave them out.");
        notes.AddRange(rc.Problems.Select(p => "Left out: " + p));
        var run = await counts.CreateSheetAsync(rc.Rows, comment, ct, notes).ConfigureAwait(false);
        // A sheet that came out wrong doesn't count: the lines can go on a new one once it's deleted in eStream.
        if (run.Ok || counts.HasSheetFor(rc.Rows)) MarkOnStockSheet(rc.Lines, run.SheetId);
        return (run, rc);
    }

    /// <summary>
    /// The receipt has no more uses, so its pictures can leave the chat: it has lines, and every one of them is on
    /// a stock sheet that has been POSTED (inactive items aside, which can't go on a sheet). A line still waiting on
    /// the owner (an item to pick, a line nobody could read, no match) can't go on a sheet, so it keeps the receipt.
    /// </summary>
    public bool IsDoneWith(IEnumerable<Counts.StockSheetRecord> sheets)
    {
        var posted = sheets.Where(s => s.Posted && s.SheetId is not null).Select(s => s.SheetId!.Value).ToHashSet();
        lock (_gate)
            return _lines.Count > 0 && _lines.Values.All(l => l.Status == ReceiptLineStatus.Inactive || l.Input.Skipped
                || (_onSheet.TryGetValue(l.Number, out var id) && id is int sheet && posted.Contains(sheet)));
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
        // The price is per item as sold (the unit cost), for the item's For quantity ("2 for $5").
        var r = PricingCalculator.Calculate(line.UnitCost!.Value * Math.Max(1, item.For), rule.Mode, rule.Pct,
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
                Warnings = ["Can't read this line. Type it in, or skip it."],
            };

        // F5.4: UPC first (the owner's pick, then the printed code), then the name.
        Item? item = null;
        string matchedBy = "";
        var candidates = new List<ReceiptCandidate>();
        // input.Upc is only set for an item the owner confirmed (here or in the chat).
        if (input.Upc is { } picked && UpcNormalizer.LooksLikeUpc(picked)
            && (await lookup.LookupUpcAsync(picked, ct).ConfigureAwait(false)).Item is { } pickedItem)
        {
            item = pickedItem;
            matchedBy = "picked";
        }
        // The printed code can be a UPC or the store's Shelf Code (test 3: Costco item numbers are shelf codes, and
        // Costco's 1019 HALF & HALF is also the UPC/PLU of PROPANE). One hit = matched; several = the owner picks.
        string? codeProblem = null;
        if (item is null && input.Code?.Trim() is { Length: > 0 } code)
        {
            var hits = new List<(Item Item, string How)>();
            if (UpcNormalizer.LooksLikeUpc(code) && (await lookup.LookupUpcAsync(code, ct).ConfigureAwait(false)).Item is { } byUpc)
                hits.Add((byUpc, "UPC"));
            foreach (var upc in await lookup.Repository.FindByShelfCodeAsync(code, ct).ConfigureAwait(false))
                if (hits.All(h => h.Item.Upc != upc) && await lookup.Repository.GetByBarcodeAsync(upc, ct).ConfigureAwait(false) is { } bySc)
                    hits.Add((bySc, "shelf code"));
            if (hits.Count == 1)
            {
                item = hits[0].Item;
                matchedBy = hits[0].How;
            }
            else if (hits.Count > 1)
            {
                candidates = hits.Select(h => new ReceiptCandidate(h.Item.Upc, $"{h.Item.Description} ({h.How} {code})", 100)).ToList();
                codeProblem = $"Code {code} matches {hits.Count} items. Pick one.";
            }
        }
        if (item is null && codeProblem is null && input.Text.Trim().Length > 0)
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
                Warnings = [codeProblem ?? (candidates.Count > 0
                    ? "Pick the item."
                    : "No match. Scan its barcode, or add it in eStream.")],
            };

        var warnings = new List<string>();
        if (item.Inactive) warnings.Add("INACTIVE in eStream. Can't be changed.");

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
                warnings.Add("No case size in eStream. Unit cost unknown.");
            }
            else
            {
                unitCost = Math.Round(cp / units, 4, MidpointRounding.AwayFromZero);
                caseCost = cp;
                math = $"{Money(cp)} ÷ ({N(item.PacksPerCase)} × {N(item.UnitsPerPack)}) = {Money4(unitCost.Value)}";
                if (units == 1)
                    warnings.Add("eStream has 1 pack × 1 unit. If it comes in a case, check how many are in it.");
            }
        }
        else
        {
            warnings.Add("No price read.");
        }

        var (qtyUsed, qtyAssumed) = QtyOf(input);
        if (!qtyAssumed && input.Qty is { } q && input.LineTotal is { } total && (input.CasePrice ?? input.UnitPrice) is { } each
            && Math.Abs(q * each - total) > 0.02m)
            warnings.Add($"{N(q)} × {Money(each)} = {Money(q * each)}, but the line total is {Money(total)}. Check it.");

        decimal? changePct = null, gmNow = null;
        if (unitCost is { } uc)
        {
            if (item.UnitCost > 0)
            {
                changePct = Math.Round((uc - item.UnitCost) / item.UnitCost * 100m, 1);
                if (Math.Abs(changePct.Value) > 50)
                    warnings.Add($"Cost changes by {changePct:0.#}%. Check the case size.");
            }
            if (item.Price > 0) gmNow = PricingCalculator.GrossMarginPct(item.Price / Math.Max(1, item.For), uc);
        }

        decimal? receivedUnits = null;
        string receivedMath = "";
        if (input.Units is { } counted && counted > 0)
        {
            receivedUnits = counted;
            receivedMath = $"{N(counted)} unit{(counted == 1 ? "" : "s")}, counted by the owner";
        }
        else if (qtyUsed is { } qty && qty > 0)
        {
            if (input.UnitPrice is not null || units == 1) { receivedUnits = qty; receivedMath = $"{N(qty)} unit{(qty == 1 ? "" : "s")}"; }
            else if (units > 1) { receivedUnits = qty * units; receivedMath = $"{N(qty)} × {N(units)} = {N(receivedUnits.Value)} units"; }
            if (receivedUnits is not null && qtyAssumed)
                receivedMath += qty == 1 ? " (no count printed, so 1 scanned)" : $" (no count printed; line total ÷ price = {N(qty)})";
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
            Qty = qtyUsed,
            QtyAssumed = qtyAssumed,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The quantity for a line: as printed; else, for a line with a price but no count (one scan on a till receipt),
    /// line total ÷ price when that's a whole number of at least 1, otherwise 1.
    /// </summary>
    internal static (decimal? Qty, bool Assumed) QtyOf(ReceiptLineInput input)
    {
        if (input.Qty is { } printed) return (printed, false);
        var each = input.CasePrice ?? input.UnitPrice;
        if (each is null && input.LineTotal is null) return (null, false);
        if (each is > 0 && input.LineTotal is { } total && total > 0)
        {
            var n = total / each.Value;
            var whole = Math.Round(n);
            if (whole >= 1 && whole <= 1000 && Math.Abs(whole * each.Value - total) <= 0.02m) return (whole, true);
        }
        return (1, true);
    }

    private static readonly CultureInfo Ca = CultureInfo.GetCultureInfo("en-CA");
    internal static string Money(decimal v) => v.ToString("C2", Ca);
    internal static string Money4(decimal v) => v == Math.Round(v, 2) ? Money(v) : "$" + v.ToString("0.00##", CultureInfo.InvariantCulture);
    private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
