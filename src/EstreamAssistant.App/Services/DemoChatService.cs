using System.Globalization;
using System.Text;
using System.Text.Json;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Upc;

namespace EstreamAssistant.App.Services;

/// <summary>
/// Dev/demo only (--demo-chat): a scripted stand-in for Claude so the chat UI
/// (streaming, tool status, numbered choices) can be checked without an API key
/// or any cost. It uses the real read-only tools.
/// </summary>
public sealed class DemoChatService(ToolExecutor tools) : IChatService
{
    public bool IsConfigured => true;
    public void AddContextNote(string note) { }
    public void Reset() { }
    public Task TestConnectionAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<ChatTurnResult> SendAsync(string ownerText, IProgress<ChatEvent>? progress, CancellationToken ct) =>
        SendAsync(ownerText, [], progress, ct);

    private bool _receiptRead;

    public async Task<ChatTurnResult> SendAsync(string ownerText, IReadOnlyList<ReceiptFile> attachments, IProgress<ChatEvent>? progress, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        var text = ownerText.Trim();
        var digits = text.Split('.', ',')[0].Trim();
        string reply;

        if (attachments.Count > 0)
        {
            // A made-up reading of a sample invoice (it doesn't look at the picture).
            progress?.Report(new ChatEvent.ToolStarted(ChatTools.ShowReceiptLines, "Matching the receipt lines to eStream items…"));
            await Task.Delay(600, ct);
            await tools.ExecuteAsync(ChatTools.ShowReceiptLines, Args(new
            {
                source = "Sample invoice (demo)",
                lines = new object[]
                {
                    Line(1, "LAYS ALL DRESSED 220G", "060410079591", 2, 55.20m, null, 110.40m),
                    Line(2, "LAYS CUCUMBER", null, 1, 55.20m, null, 55.20m),
                    Line(3, "COKE 2L 8PK", "67000000409", 3, 21.60m, null, 64.80m),
                    Line(4, "DORITOS NACHO CHS 235G", "28400090360", 1, 54.40m, null, 45.40m),
                    Line(5, "KRAFT DINNER ORIG", null, 2, 14.40m, null, 28.80m),
                    new { line = 6, text = "HEINZ KETCH?? 1L", code = (string?)null, qty = (decimal?)null, case_price = (decimal?)null,
                          unit_price = (decimal?)null, line_total = (decimal?)null, readable = false, upc = (string?)null, note = "Price is smudged." },
                },
            }), ct);
            _receiptRead = true;
            reply = "I read 6 lines from the invoice. 3 are matched and ready, line 4's quantity × price doesn't match its total, " +
                    "line 2 could be more than one item (pick it in the receipt table), and line 6 is unreadable, so check it on the paper.\n\n" +
                    "Tell me the markup you want, e.g. \"30% markup on the chips\".\n\n(Demo reply: no API key used.)";
        }
        else if (_receiptRead && text.Contains("markup", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new ChatEvent.ToolStarted(ChatTools.PriceReceiptLines, "Working out prices for the receipt…"));
            await Task.Delay(400, ct);
            await tools.ExecuteAsync(ChatTools.PriceReceiptLines, Args(new { lines = new[] { 1, 2, 4 }, mode = "markup", pct = 30, apply_endings = true }), ct);
            reply = "With a 30% markup the chips come out as shown in the receipt table. Tick the lines you want and press Add to pending, " +
                    "or tell me and I'll add them.\n\n(Demo reply.)";
        }
        else if (text.Contains("negative", StringComparison.OrdinalIgnoreCase) || text.Contains("below min", StringComparison.OrdinalIgnoreCase))
        {
            var filter = text.Contains("negative", StringComparison.OrdinalIgnoreCase) ? "negative" : "below_min";
            progress?.Report(new ChatEvent.ToolStarted(ChatTools.StockReport, "Making the stock report…"));
            var r = await tools.ExecuteAsync(ChatTools.StockReport, Args(new { filter, include_inactive = false }), ct);
            using var d = JsonDocument.Parse(r.Json);
            var items = d.RootElement.GetProperty("items").EnumerateArray().Take(3)
                .Select(i => $"{i.GetProperty("description").GetString()} ({i.GetProperty("on_hand").GetDecimal():0.##})");
            reply = $"{d.RootElement.GetProperty("summary").GetString()}. The worst are {string.Join(", ", items)}. " +
                    "The full list is on the Reports tab, where you can export it or print it.\n\n(Demo reply: no API key used.)";
        }
        else if (text.Contains("sheet", StringComparison.OrdinalIgnoreCase))
        {
            var number = new string(text.Where(char.IsDigit).ToArray());
            progress?.Report(new ChatEvent.ToolStarted(ChatTools.StockSheets, "Reading stock sheets…"));
            var r = await tools.ExecuteAsync(ChatTools.StockSheets,
                Args(new { sheet = number.Length is > 0 and < 9 ? int.Parse(number, CultureInfo.InvariantCulture) : 0, include_closed = true }), ct);
            using var d = JsonDocument.Parse(r.Json);
            var root = d.RootElement;
            reply = root.TryGetProperty("error", out var err) ? err.GetString() + "\n\n(Demo reply.)"
                : root.TryGetProperty("checks", out var checks)
                    ? $"Sheet {root.GetProperty("sheet").GetInt32()}: {root.GetProperty("items").GetInt32()} items, {root.GetProperty("over").GetInt32()} over, " +
                      $"{root.GetProperty("short").GetInt32()} short.\n" + string.Join("\n", checks.EnumerateArray().Select(c => "- " + c.GetString())) +
                      "\n\nThe whole sheet is on the Reports tab.\n\n(Demo reply: no API key used.)"
                : root.TryGetProperty("summary", out var sum)
                    ? $"{sum.GetString()}. The list is on the Reports tab.\n\n(Demo reply: no API key used.)"
                    : "There's no stock sheet with that number.\n\n(Demo reply.)";
        }
        else if (UpcNormalizer.LooksLikeUpc(digits) || text.Contains("UPC", StringComparison.OrdinalIgnoreCase))
        {
            int at = text.IndexOf("UPC", StringComparison.OrdinalIgnoreCase);
            var upc = at >= 0 ? text[(at + 3)..].Split(',')[0].Trim() : digits;
            progress?.Report(new ChatEvent.ToolStarted(ChatTools.LookupItem, $"Looking up {upc}…"));
            var r = await tools.ExecuteAsync(ChatTools.LookupItem, Args(new { upc }), ct);
            if (r.ShownItem is not null) progress?.Report(new ChatEvent.ItemShown(r.ShownItem));
            using var d = JsonDocument.Parse(r.Json);
            reply = d.RootElement.TryGetProperty("found", out var f) && f.GetBoolean()
                ? $"**{d.RootElement.GetProperty("description").GetString()}** is {d.RootElement.GetProperty("price").GetDecimal().ToString("C", CultureInfo.GetCultureInfo("en-CA"))}. " +
                  $"Unit cost is {d.RootElement.GetProperty("unit_cost").GetDecimal():C}, so the GM is {d.RootElement.GetProperty("gm_pct").GetDecimal():0.#}%.\n\n(Demo reply: no API key used.)"
                : "That barcode isn't in eStream. New items have to be added in eStream by hand.\n\n(Demo reply.)";
        }
        else
        {
            progress?.Report(new ChatEvent.ToolStarted(ChatTools.FindItems, $"Searching for \"{text}\"…"));
            await Task.Delay(400, ct);
            var r = await tools.ExecuteAsync(ChatTools.FindItems, Args(new { text, max = 4 }), ct);
            using var d = JsonDocument.Parse(r.Json);
            var matches = d.RootElement.GetProperty("matches").EnumerateArray().ToList();
            if (matches.Count == 0)
                reply = $"I couldn't find anything like \"{text}\". Try another word, or scan the barcode.\n\n(Demo reply.)";
            else
            {
                var sb = new StringBuilder("I found a few that could match. Which one do you mean?\n");
                for (int i = 0; i < matches.Count; i++)
                    sb.Append($"{i + 1}. {matches[i].GetProperty("description").GetString()}, UPC {matches[i].GetProperty("upc").GetString()}\n");
                sb.Append("\n(Demo reply: no API key used.)");
                reply = sb.ToString();
            }
        }

        // Stream it out in small pieces, like the real thing.
        for (int i = 0; i < reply.Length; i += 6)
        {
            progress?.Report(new ChatEvent.TextDelta(reply.Substring(i, Math.Min(6, reply.Length - i))));
            await Task.Delay(15, ct);
        }
        return new ChatTurnResult(reply, false, false, 0, 0);
    }

    private static object Line(int line, string text, string? code, decimal qty, decimal casePrice, decimal? unitPrice, decimal total) => new
    {
        line, text, code, qty, case_price = casePrice, unit_price = unitPrice, line_total = total, readable = true, upc = (string?)null, note = (string?)null,
    };

    private static Dictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
}
