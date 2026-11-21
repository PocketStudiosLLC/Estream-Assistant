using System.Globalization;
using System.Text.Json;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Pricing;
using EstreamAssistant.Core.Search;
using EstreamAssistant.Core.Settings;
using EstreamAssistant.Core.Upc;

namespace EstreamAssistant.Core.Claude;

/// <summary>Outcome of one tool call. <see cref="Json"/> goes back to Claude as the tool_result.</summary>
public sealed record ToolOutcome(string Json, bool IsError, UpcLookupResult? ShownItem = null)
{
    public static ToolOutcome Error(string message) =>
        new(JsonSerializer.Serialize(new { error = message }), true);
}

/// <summary>
/// Runs Claude's tool calls against the app (the "ToolRouter" in the spec).
/// Re-validates every input on the app side, whatever the schema said.
/// Independent of the Anthropic SDK so it can be unit-tested without API calls.
/// </summary>
public sealed class ToolExecutor
{
    private readonly ItemLookupService _lookup;
    private readonly AppSettings _settings;
    private readonly PendingChanges? _pending;
    private readonly Counts.StockCounts? _counts;
    private readonly Receipts.ReceiptSession? _receipt;
    private readonly Reports.ReportService? _reports;
    private CodeTables? _codes;

    /// <summary>How many report rows go back to Claude (the full list is on the Reports tab).</summary>
    public const int ReportRowsForClaude = 40;

    public ToolExecutor(ItemLookupService lookup, AppSettings settings, PendingChanges? pending = null, Counts.StockCounts? counts = null,
        Receipts.ReceiptSession? receipt = null, Reports.ReportService? reports = null)
    {
        _reports = reports;
        _lookup = lookup;
        _settings = settings;
        _pending = pending;
        _counts = counts;
        _receipt = receipt;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<ToolOutcome> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct = default)
    {
        try
        {
            return name switch
            {
                ChatTools.LookupItem => await LookupItemAsync(input, ct).ConfigureAwait(false),
                ChatTools.FindItems => await FindItemsAsync(input, ct).ConfigureAwait(false),
                ChatTools.GetCodes => await GetCodesAsync(ct).ConfigureAwait(false),
                ChatTools.CalcPrice => CalcPrice(input),
                ChatTools.StageChanges => await StageChangesAsync(input, ct).ConfigureAwait(false),
                ChatTools.RunDryRun => await RunDryRunAsync(ct).ConfigureAwait(false),
                ChatTools.StockCountPreview => await StockCountPreviewAsync(input, ct).ConfigureAwait(false),
                ChatTools.CreateStockSheet => await CreateStockSheetAsync(input, ct).ConfigureAwait(false),
                ChatTools.ShowReceiptLines => await ShowReceiptLinesAsync(input, ct).ConfigureAwait(false),
                ChatTools.PriceReceiptLines => PriceReceiptLines(input),
                ChatTools.StockReport => await StockReportAsync(input, ct).ConfigureAwait(false),
                ChatTools.StockSheets => await StockSheetsAsync(input, ct).ConfigureAwait(false),
                _ => ToolOutcome.Error($"Unknown tool '{name}'. Only {string.Join(", ", ChatTools.All.Select(t => t.Name))} exist."),
            };
        }
        catch (ToolInputException ex)
        {
            return ToolOutcome.Error(ex.Message);
        }
        catch (StockSheetSetupException ex)
        {
            return ToolOutcome.Error(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolOutcome.Error("The lookup failed inside the app: " + ex.Message);
        }
    }

    private async Task<ToolOutcome> LookupItemAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var upc = GetString(input, "upc", maxLength: 40);
        if (!UpcNormalizer.LooksLikeUpc(upc))
            throw new ToolInputException("upc must be digits only (spaces and dashes are allowed). For names use find_items.");

        var r = await _lookup.LookupUpcAsync(upc, ct).ConfigureAwait(false);
        if (r.Item is null)
            return new(JsonSerializer.Serialize(new
            {
                found = false,
                upc = r.Normalized,
                message = "Not in eStream. New items must be added in eStream by hand.",
            }, Json), false);

        var i = r.Item;
        return new(JsonSerializer.Serialize(new
        {
            found = true,
            upc = i.Upc.ToString(CultureInfo.InvariantCulture),
            description = i.Description,
            price = i.Price,
            @for = i.For,
            case_cost = i.CaseCost,
            packs_per_case = i.PacksPerCase,
            units_per_pack = i.UnitsPerPack,
            unit_cost = i.UnitCost,
            deposit_code = i.Deposit,
            deposit_amount = i.DepositAmount,
            levy_code = i.Levy,
            on_hand = i.OnHand,
            min = i.Min,
            max = i.Max,
            sold = i.Sold,
            gm_pct = i.GmPct,
            inactive = i.Inactive,
            note = r.Note,
        }, Json), false, r);
    }

    private async Task<ToolOutcome> FindItemsAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var text = GetString(input, "text", maxLength: 100);
        if (text.Trim().Length == 0) throw new ToolInputException("text is empty.");
        var max = GetInt(input, "max");
        if (max is < 1 or > 8) throw new ToolInputException("max must be between 1 and 8.");

        var r = await _lookup.FindAsync(text, max, ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            query = text,
            needs_choice = !r.IsConfident,
            matches = r.Matches.Select(m => new
            {
                upc = m.Item.Upc.ToString(CultureInfo.InvariantCulture),
                description = m.Item.Description,
                match_pct = m.MatchPercent,
                sold = m.Item.Sold,
            }),
            hint = r.Matches.Count == 0
                ? "Nothing matched. Ask the owner for another word or to scan the barcode."
                : r.IsConfident ? "Best match is clear." : "Not sure which item: show the owner a numbered list and let them pick.",
        }, Json), false);
    }

    private async Task<ToolOutcome> GetCodesAsync(CancellationToken ct)
    {
        _codes ??= await _lookup.Repository.GetCodesAsync(ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            deposits = _codes.Deposits.Select(d => new { code = d.Code, description = d.Description, amount = d.Amount }),
            levies = _codes.Levies.Select(l => new { code = l.Code, description = l.Description, amount = l.Amount }),
        }, Json), false);
    }

    private ToolOutcome CalcPrice(IReadOnlyDictionary<string, JsonElement> input)
    {
        var cost = GetDecimal(input, "cost");
        if (cost is < 0 or > 100_000) throw new ToolInputException("cost must be between 0 and 100000.");
        var pct = GetDecimal(input, "pct");
        var modeText = GetString(input, "mode", maxLength: 10);
        var mode = modeText switch
        {
            "markup" => PricingMode.Markup,
            "margin" => PricingMode.Margin,
            _ => throw new ToolInputException("mode must be 'markup' or 'margin'."),
        };
        if (mode == PricingMode.Margin && pct is < 0 or >= 100) throw new ToolInputException("margin pct must be 0 to 99.99.");
        if (mode == PricingMode.Markup && pct is <= -100 or > 1000) throw new ToolInputException("markup pct must be above -100 and at most 1000.");
        bool applyEndings = GetBool(input, "apply_endings");

        var endings = applyEndings ? _settings.PriceEndings : null;
        var r = PricingCalculator.Calculate(cost, mode, pct, endings);
        return new(JsonSerializer.Serialize(new
        {
            cost,
            mode = modeText,
            pct,
            exact_price = Math.Round(r.RawPrice, 4),
            rounded_price = r.RoundedPrice,
            final_price = r.Price,
            gm_pct = r.GmPct,
            endings_applied = applyEndings && _settings.PriceEndings.Count > 0,
            store_endings = _settings.PriceEndings,
        }, Json), false);
    }

    private async Task<ToolOutcome> StageChangesAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (_pending is null) throw new ToolInputException("Pending changes aren't available.");
        var rows = Get(input, "rows");
        if (rows.ValueKind != JsonValueKind.Array) throw new ToolInputException("'rows' must be a list.");
        if (rows.GetArrayLength() is 0 or > PendingChanges.MaxRows) throw new ToolInputException($"Send 1 to {PendingChanges.MaxRows} rows.");

        var requests = new List<ChangeRequest>();
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) throw new ToolInputException("Each row must be an object.");
            var d = r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var upc = GetString(d, "upc", 40);
            if (!UpcNormalizer.LooksLikeUpc(upc)) throw new ToolInputException($"'{upc}' isn't a UPC.");
            requests.Add(new ChangeRequest
            {
                Upc = upc,
                Price = NullableDecimal(d, "price"),
                For = NullableDecimal(d, "for"),
                CaseCost = NullableDecimal(d, "case_cost"),
                Deposit = NullableInt(d, "deposit"),
                Levy = NullableInt(d, "levy"),
                OnHand = NullableDecimal(d, "on_hand"),
                Min = NullableDecimal(d, "min"),
                Max = NullableDecimal(d, "max"),
                Reason = d.TryGetValue("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? Clip(reason.GetString() ?? "", 200) : "",
            });
        }

        var outcomes = await _pending.StageAsync(requests, ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            rows = outcomes.Select(o => new
            {
                upc = o.Upc,
                description = o.Description,
                result = o.Staged ? "staged" : "skipped",
                changes = o.ChangesText,
                message = o.Message,
            }),
            next = "Tell the owner what is in the list (Now -> New). A dry run is needed before the owner can press SAVE.",
        }, Json), false);
    }

    private async Task<ToolOutcome> RunDryRunAsync(CancellationToken ct)
    {
        if (_pending is null) throw new ToolInputException("Pending changes aren't available.");
        var record = await _pending.DryRunAsync(ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            passed = record.Passed,
            summary = record.Summary,
            rows = _pending.Rows.Select(r => new
            {
                upc = r.Upc.ToString(CultureInfo.InvariantCulture),
                description = r.Description,
                status = r.Status.ToString(),
                changes = r.ChangesText,
                message = r.Message,
            }),
            next = record.Passed
                ? "The owner can now check the list and press SAVE. You cannot save."
                : "Explain in plain words what failed and what the owner can do.",
        }, Json), false);
    }

    private async Task<ToolOutcome> StockCountPreviewAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (_counts is null) throw new ToolInputException("Stock counts aren't available.");
        var rows = Get(input, "rows");
        if (rows.ValueKind != JsonValueKind.Array) throw new ToolInputException("'rows' must be a list.");
        if (rows.GetArrayLength() is 0 or > Counts.StockCounts.MaxRows) throw new ToolInputException($"Send 1 to {Counts.StockCounts.MaxRows} rows.");
        var inputs = new List<Counts.CountInput>();
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) throw new ToolInputException("Each row must be an object.");
            var d = r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var upc = GetString(d, "upc", 40);
            if (!UpcNormalizer.LooksLikeUpc(upc)) throw new ToolInputException($"'{upc}' isn't a UPC.");
            var count = GetDecimal(d, "count");
            if (count < 0 || count > 100_000) throw new ToolInputException($"Count {count} for {upc} isn't valid.");
            inputs.Add(new(upc, count));
        }
        var add = GetBool(input, "add_to_list");
        var outcomes = add
            ? await _counts.AddAsync(inputs, addToExisting: false, ct).ConfigureAwait(false)
            : await _counts.PreviewAsync(inputs, ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            rows = outcomes.Select(o => new
            {
                upc = o.Upc,
                description = o.Description,
                ok = o.Added,
                system_on_hand = o.Row?.SystemOnHand,
                counted = o.Row?.Count,
                over_short = o.Row?.OverShort,
                message = o.Message,
            }),
            added_to_list = add,
            list_size = _counts.Rows.Count,
            next = "Nothing changed in eStream. The owner can make a stock sheet from the count list; posting it is their button only.",
        }, Json), false);
    }

    private async Task<ToolOutcome> CreateStockSheetAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (_counts is null) throw new ToolInputException("Stock counts aren't available.");
        var comment = GetString(input, "comment", 200);
        var run = await _counts.CreateSheetAsync(null, Clip(comment, 60), ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            ok = run.Ok,
            sheet = run.SheetId,
            message = run.Message,
            next = run.Ok
                ? "Tell the owner the sheet number. To post it (no undo) they use the Stock counts tab: post check, then type the sheet number. You cannot post."
                : "Explain what happened in plain words.",
        }, Json), false);
    }

    private async Task<ToolOutcome> ShowReceiptLinesAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (_receipt is null) throw new ToolInputException("Receipts aren't available.");
        var source = Clip(GetString(input, "source", 200).Trim(), 60);
        var rows = Get(input, "lines");
        if (rows.ValueKind != JsonValueKind.Array) throw new ToolInputException("'lines' must be a list.");
        if (rows.GetArrayLength() is 0 or > Receipts.ReceiptSession.MaxLines)
            throw new ToolInputException($"Send 1 to {Receipts.ReceiptSession.MaxLines} lines.");
        var inputs = new List<Receipts.ReceiptLineInput>();
        var seen = new HashSet<int>();
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) throw new ToolInputException("Each line must be an object.");
            var d = r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var line = GetInt(d, "line");
            if (line is < 1 or > 999) throw new ToolInputException("line must be 1 to 999.");
            if (!seen.Add(line)) throw new ToolInputException($"Line {line} is in the list twice.");
            var upc = NullableString(d, "upc", 40);
            if (upc is not null && !UpcNormalizer.LooksLikeUpc(upc)) throw new ToolInputException($"upc '{upc}' on line {line} isn't a UPC.");
            decimal? Money(string key)
            {
                var v = NullableDecimal(d, key);
                if (v is < 0 or > 100_000) throw new ToolInputException($"{key} on line {line} isn't a sensible amount.");
                return v;
            }
            var qty = NullableDecimal(d, "qty");
            if (qty is < 0 or > 100_000) throw new ToolInputException($"qty on line {line} isn't valid.");
            inputs.Add(new Receipts.ReceiptLineInput
            {
                Line = line,
                Text = Clip(GetString(d, "text", 300).Trim(), 120),
                Code = NullableString(d, "code", 40)?.Trim(),
                Qty = qty,
                CasePrice = Money("case_price"),
                UnitPrice = Money("unit_price"),
                LineTotal = Money("line_total"),
                Readable = GetBool(d, "readable"),
                Upc = upc,
                Note = NullableString(d, "note", 300) is { } n ? Clip(n, 160) : null,
            });
        }

        var lines = await _receipt.SetLinesAsync(source, inputs, ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            lines = lines.Select(ReceiptLineJson),
            next = "The table is on the owner's screen. Tell them briefly what was matched and what needs them " +
                   "(unreadable lines, items to pick as a numbered list, warnings). Don't stage anything until they say what to do.",
        }, Json), false);
    }

    private ToolOutcome PriceReceiptLines(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (_receipt is null) throw new ToolInputException("Receipts aren't available.");
        if (!_receipt.IsOpen) throw new ToolInputException("No receipt is open. Use show_receipt_lines first.");
        var arr = Get(input, "lines");
        if (arr.ValueKind != JsonValueKind.Array) throw new ToolInputException("'lines' must be a list of line numbers.");
        var numbers = arr.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : throw new ToolInputException("Line numbers must be whole numbers."))
            .ToHashSet();
        var pct = GetDecimal(input, "pct");
        var mode = GetString(input, "mode", 10) switch
        {
            "markup" => PricingMode.Markup,
            "margin" => PricingMode.Margin,
            _ => throw new ToolInputException("mode must be 'markup' or 'margin'."),
        };
        if (mode == PricingMode.Margin && pct is < 0 or >= 100) throw new ToolInputException("margin pct must be 0 to 99.99.");
        if (mode == PricingMode.Markup && pct is <= -100 or > 1000) throw new ToolInputException("markup pct must be above -100 and at most 1000.");
        var rule = new Receipts.PriceRule(mode, pct, GetBool(input, "apply_endings"));
        var priced = _receipt.ApplyPriceRule(numbers, rule);
        var missing = numbers.Except(priced.Select(p => p.Number)).Order().ToList();
        return new(JsonSerializer.Serialize(new
        {
            rule = rule.Text,
            lines = priced.Select(ReceiptLineJson),
            not_priced = missing,
            not_priced_reason = missing.Count > 0 ? "Those lines have no matched item or no cost yet." : null,
            next = "Tell the owner each price as Now -> New. Stage with stage_changes (price, and case_cost from new_case_cost) only when they agree.",
        }, Json), false);
    }

    private async Task<ToolOutcome> StockReportAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (_reports is null) throw new ToolInputException("Reports aren't available.");
        var filter = GetString(input, "filter", 20) switch
        {
            "negative" => StockFilter.Negative,
            "zero" => StockFilter.Zero,
            "below_min" => StockFilter.BelowMin,
            "above_max" => StockFilter.AboveMax,
            _ => throw new ToolInputException("filter must be negative, zero, below_min or above_max."),
        };
        var r = await _reports.StockAsync(filter, GetBool(input, "include_inactive"), ct).ConfigureAwait(false);
        return new(JsonSerializer.Serialize(new
        {
            report = r.Report.Title,
            summary = r.Report.Summary,
            total_items = r.Rows.Count,
            more_than_shown_in_app = r.Truncated,
            total_on_hand = filter is StockFilter.Negative or StockFilter.Zero ? r.TotalUnits : (decimal?)null,
            units_needed_to_reach_min = filter == StockFilter.BelowMin ? r.TotalUnits : (decimal?)null,
            units_over_max = filter == StockFilter.AboveMax ? r.TotalUnits : (decimal?)null,
            value_at_cost = filter == StockFilter.Negative ? r.TotalValue : (decimal?)null,
            rows_here = Math.Min(r.Rows.Count, ReportRowsForClaude),
            items = r.Rows.Take(ReportRowsForClaude).Select(i => new
            {
                upc = i.Upc.ToString(CultureInfo.InvariantCulture),
                description = i.Description,
                on_hand = i.OnHand,
                min = i.Min,
                max = i.Max,
                sold = i.Sold,
                inactive = i.Inactive ? true : (bool?)null,
            }),
            next = r.Rows.Count == 0
                ? "Nothing matched. Tell the owner."
                : "Give the owner the totals and the worst few items. The full list is on the Reports tab, where they can export it " +
                  "to CSV or print it; mention that instead of reading out a long list. Don't stage changes from this unless the owner asks.",
        }, Json), false);
    }

    private async Task<ToolOutcome> StockSheetsAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (_reports is null) throw new ToolInputException("Reports aren't available.");
        var sheet = NullableInt(input, "sheet");
        if (sheet is null)
        {
            var list = await _reports.SheetListAsync(GetBool(input, "include_closed"), ct).ConfigureAwait(false);
            return new(JsonSerializer.Serialize(new
            {
                summary = list.Report.Summary,
                sheets = list.Sheets.Take(ReportRowsForClaude).Select(s => new
                {
                    sheet = s.Info.Id,
                    cut_off = s.Info.CloseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    comment = s.Info.Comment,
                    supervisor = s.Info.Supervisor,
                    status = s.Info.Closed ? "closed" : "open",
                    items = s.Info.Lines,
                    note = s.Note.Length > 0 ? s.Note : null,
                }),
                next = "The list is also on the Reports tab. To see a sheet's items and checks, call stock_sheets with its number.",
            }, Json), false);
        }

        if (sheet is < 1) throw new ToolInputException("sheet must be a sheet number.");
        var r = await _reports.SheetAsync(sheet.Value, ct).ConfigureAwait(false);
        if (r is null)
            return new(JsonSerializer.Serialize(new
            {
                found = false,
                sheet,
                message = "There's no stock sheet with that number in eStream.",
            }, Json), false);

        return new(JsonSerializer.Serialize(new
        {
            found = true,
            sheet = r.Sheet.Info.Id,
            comment = r.Sheet.Info.Comment,
            cut_off = r.Sheet.Info.CloseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            status = r.Sheet.Info.Closed ? "closed" : "open",
            note = r.Sheet.Note.Length > 0 ? r.Sheet.Note : null,
            items = r.Lines.Count,
            over = r.Over,
            @short = r.Short,
            unchanged = r.Unchanged,
            net_units = r.NetUnits,
            net_value_at_cost = r.NetValue,
            checks = r.Checks,
            biggest_differences = r.Lines
                .Where(l => l.OverShort != 0)
                .OrderByDescending(l => Math.Abs(l.OverShort * l.UnitCost)).ThenByDescending(l => Math.Abs(l.OverShort))
                .Take(ReportRowsForClaude)
                .Select(l => new
                {
                    upc = l.Upc.ToString(CultureInfo.InvariantCulture),
                    description = l.Description,
                    system = l.System,
                    counted = l.Counted,
                    over_short = l.OverShort,
                }),
            next = "Tell the owner the totals and the checks in plain words. The full sheet is on the Reports tab. " +
                   "You can't post a sheet: posting (no undo) is only on the Stock counts tab, for sheets this app made.",
        }, Json), false);
    }

    private static object ReceiptLineJson(Receipts.ReceiptLine l) => new
    {
        line = l.Number,
        text = l.Input.Text,
        status = l.Status.ToString(),
        upc = l.Item?.Upc.ToString(CultureInfo.InvariantCulture),
        description = l.Item?.Description,
        matched_by = l.MatchedBy.Length > 0 ? l.MatchedBy : null,
        candidates = l.Candidates.Count > 0 && l.Status == Receipts.ReceiptLineStatus.NeedsChoice
            ? l.Candidates.Select(c => new { upc = c.Upc.ToString(CultureInfo.InvariantCulture), description = c.Description, match_pct = c.MatchPct })
            : null,
        current_price = l.Item?.Price,
        @for = l.Item?.For,
        current_case_cost = l.Item?.CaseCost,
        current_unit_cost = l.Item?.UnitCost,
        new_unit_cost = l.UnitCost,
        new_case_cost = l.NewCaseCost,
        cost_math = l.CostMath.Length > 0 ? l.CostMath : null,
        cost_change_pct = l.CostChangePct,
        gm_pct_at_current_price = l.GmAtCurrentPrice,
        proposed_price = l.ProposedPrice,
        proposed_gm_pct = l.ProposedGm,
        warnings = l.Warnings.Count > 0 ? l.Warnings : null,
    };

    private static string? NullableString(IReadOnlyDictionary<string, JsonElement> d, string key, int maxLength) =>
        d.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null ? GetString(d, key, maxLength) : null;

    private static decimal? NullableDecimal(IReadOnlyDictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null ? GetDecimal(d, key) : null;

    private static int? NullableInt(IReadOnlyDictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind != JsonValueKind.Null ? GetInt(d, key) : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // --- input helpers -------------------------------------------------------

    private static JsonElement Get(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) ? v : throw new ToolInputException($"Missing '{key}'.");

    private static string GetString(IReadOnlyDictionary<string, JsonElement> input, string key, int maxLength)
    {
        var v = Get(input, key);
        if (v.ValueKind != JsonValueKind.String) throw new ToolInputException($"'{key}' must be text.");
        var s = v.GetString() ?? "";
        if (s.Length > maxLength) throw new ToolInputException($"'{key}' is too long.");
        return s;
    }

    private static int GetInt(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        var v = Get(input, key);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw new ToolInputException($"'{key}' must be a whole number.");
    }

    private static decimal GetDecimal(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        var v = Get(input, key);
        return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : throw new ToolInputException($"'{key}' must be a number.");
    }

    private static bool GetBool(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        var v = Get(input, key);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ToolInputException($"'{key}' must be true or false."),
        };
    }

    private sealed class ToolInputException(string message) : Exception(message);
}
