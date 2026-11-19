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
    private CodeTables? _codes;

    public ToolExecutor(ItemLookupService lookup, AppSettings settings, PendingChanges? pending = null, Counts.StockCounts? counts = null)
    {
        _lookup = lookup;
        _settings = settings;
        _pending = pending;
        _counts = counts;
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
                _ => ToolOutcome.Error($"Unknown tool '{name}'. Only {string.Join(", ", ChatTools.All.Select(t => t.Name))} exist."),
            };
        }
        catch (ToolInputException ex)
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
