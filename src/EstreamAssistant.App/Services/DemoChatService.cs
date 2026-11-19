using System.Globalization;
using System.Text;
using System.Text.Json;
using EstreamAssistant.Core.Claude;
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

    public async Task<ChatTurnResult> SendAsync(string ownerText, IProgress<ChatEvent>? progress, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        var text = ownerText.Trim();
        var digits = text.Split('.', ',')[0].Trim();
        string reply;

        if (UpcNormalizer.LooksLikeUpc(digits) || text.Contains("UPC", StringComparison.OrdinalIgnoreCase))
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

    private static Dictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;
}
