using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Tests;

public class ChatToolsTests
{
    [Fact]
    public void Every_schema_is_strict_and_valid()
    {
        foreach (var t in ChatTools.All)
        {
            var s = t.Schema;
            Assert.Equal("object", s["type"].GetString());
            Assert.False(s["additionalProperties"].GetBoolean());
            var props = s["properties"].EnumerateObject().Select(p => p.Name).Order().ToArray();
            var required = s["required"].EnumerateArray().Select(r => r.GetString()!).Order().ToArray();
            Assert.Equal(props, required); // strict: every property required
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        }
    }

    [Fact]
    public void Strict_schemas_stay_inside_the_API_limits()
    {
        // API limits across all strict tools in one request: 20 tools, 24 optional params, 16 union-typed params.
        // Going over fails EVERY chat request with a 400 (v0.1.4 hit 16 unions). Keep a margin.
        static int Unions(JsonElement schema)
        {
            int n = 0;
            if (schema.ValueKind != JsonValueKind.Object) return 0;
            if (schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Array) n++;
            if (schema.TryGetProperty("anyOf", out _)) n++;
            if (schema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject()) n += Unions(p.Value);
            if (schema.TryGetProperty("items", out var items)) n += Unions(items);
            return n;
        }
        var strict = ChatTools.All.Where(t => t.Strict).ToList();
        int unions = strict.Sum(t => Unions(JsonDocument.Parse(t.SchemaJson).RootElement));
        Assert.True(strict.Count <= 18, $"{strict.Count} strict tools (limit 20)");
        Assert.True(unions <= 15, $"{unions} nullable/union parameters across all tools (API limit 16)");
    }

    [Fact]
    public void No_write_tools_exist()
    {
        // Spec S3: Claude must never be able to save or post on its own.
        var names = ChatTools.All.Select(t => t.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("apply") || n.Contains("save") || n.Contains("post") || n.Contains("publish"));
    }
}

public class ToolExecutorTests
{
    private static ToolExecutor Make(AppSettings? s = null)
    {
        var dir = Directory.CreateTempSubdirectory("esa-tools-").FullName;
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(dir, "n.json")));
        return new ToolExecutor(lookup, s ?? new AppSettings());
    }

    private static Dictionary<string, JsonElement> In(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    [Fact]
    public async Task Lookup_found_shows_item()
    {
        var r = await Make().ExecuteAsync(ChatTools.LookupItem, In(new { upc = "060410079591" }));
        Assert.False(r.IsError);
        Assert.NotNull(r.ShownItem);
        using var doc = JsonDocument.Parse(r.Json);
        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("LAYS ALL DRESS 220 g", doc.RootElement.GetProperty("description").GetString());
        Assert.Equal(4.29m, doc.RootElement.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Lookup_unknown_is_not_an_error()
    {
        var r = await Make().ExecuteAsync(ChatTools.LookupItem, In(new { upc = "99999999999" }));
        Assert.False(r.IsError);
        Assert.Contains("\"found\":false", r.Json);
    }

    [Fact]
    public async Task Lookup_rejects_names() =>
        Assert.True((await Make().ExecuteAsync(ChatTools.LookupItem, In(new { upc = "coke" }))).IsError);

    [Fact]
    public async Task Find_ambiguous_needs_choice()
    {
        var r = await Make().ExecuteAsync(ChatTools.FindItems, In(new { text = "lays cucumber", max = 8 }));
        using var doc = JsonDocument.Parse(r.Json);
        Assert.True(doc.RootElement.GetProperty("needs_choice").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("matches").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task List_items_returns_every_brand_item_with_count()
    {
        var r = await Make().ExecuteAsync(ChatTools.ListItems, In(new { text = "LAYS", include_inactive = false }));
        Assert.False(r.IsError);
        using var doc = JsonDocument.Parse(r.Json);
        Assert.Equal(7, doc.RootElement.GetProperty("total_items").GetInt32());
        Assert.Equal(7, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task List_items_rejects_empty_text() =>
        Assert.True((await Make().ExecuteAsync(ChatTools.ListItems, In(new { text = " ", include_inactive = false }))).IsError);

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task Find_max_out_of_range(int max) =>
        Assert.True((await Make().ExecuteAsync(ChatTools.FindItems, In(new { text = "coke", max }))).IsError);

    [Fact]
    public async Task Calc_price_matches_plan_examples()
    {
        var ex = Make(new AppSettings { PriceEndings = [29, 49, 79, 99] });
        var markup = await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = 4.29, mode = "markup", pct = 30, apply_endings = false }));
        var margin = await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = 4.29, mode = "margin", pct = 30, apply_endings = false }));
        var ended = await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = 4.29, mode = "markup", pct = 30, apply_endings = true }));
        Assert.Equal(5.58m, JsonDocument.Parse(markup.Json).RootElement.GetProperty("final_price").GetDecimal());
        Assert.Equal(6.13m, JsonDocument.Parse(margin.Json).RootElement.GetProperty("final_price").GetDecimal());
        Assert.Equal(5.79m, JsonDocument.Parse(ended.Json).RootElement.GetProperty("final_price").GetDecimal());
    }

    [Fact]
    public async Task Calc_price_rejects_bad_input()
    {
        var ex = Make();
        Assert.True((await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = 1, mode = "margin", pct = 100, apply_endings = false }))).IsError);
        Assert.True((await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = -1, mode = "markup", pct = 10, apply_endings = false }))).IsError);
        Assert.True((await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = 1, mode = "double", pct = 10, apply_endings = false }))).IsError);
        Assert.True((await ex.ExecuteAsync(ChatTools.CalcPrice, In(new { cost = "1", mode = "markup", pct = 10, apply_endings = false }))).IsError);
    }

    [Fact]
    public async Task Get_codes_lists_deposits()
    {
        var r = await Make().ExecuteAsync(ChatTools.GetCodes, In(new { }));
        Assert.True(JsonDocument.Parse(r.Json).RootElement.GetProperty("deposits").GetArrayLength() > 10);
    }

    [Theory]
    [InlineData("apply_changes")]
    [InlineData("post_stock_sheet")]
    public async Task Unknown_tools_are_refused(string name) =>
        Assert.True((await Make().ExecuteAsync(name, In(new { }))).IsError);
}

public class ChoiceParserTests
{
    [Fact]
    public void Parses_numbered_choices()
    {
        var c = ChoiceParser.Parse("""
            Which one did you mean?
            1. LAY'S CUCUMBER 40G, UPC 60410004128, $1.79
            2. **CUCMBER LAYS**, UPC 60410079966, $4.29
            Type the number.
            """);
        Assert.Equal(2, c.Count);
        Assert.Equal("LAY'S CUCUMBER 40G", c[0].Label);
        Assert.Equal("UPC 60410004128, $1.79", c[0].Detail);
        Assert.Equal("CUCMBER LAYS", c[1].Label);
    }

    [Theory]
    [InlineData("Price is $4.29.")]
    [InlineData("1. Only one option")]
    [InlineData("2. starts at two\n3. three")]
    [InlineData("")]
    public void No_choices(string text) => Assert.Empty(ChoiceParser.Parse(text));

    [Fact]
    public void Display_text_drops_choice_lines_and_bold()
    {
        var shown = ChoiceParser.ForDisplay("Which one?\n1. **A**, UPC 1\n2. B, UPC 2\n\n\nType the number.", hasChoices: true);
        Assert.Equal("Which one?\n\nType the number.", shown);
        Assert.Equal("Price is $4.29.", ChoiceParser.ForDisplay("Price is **$4.29**.", hasChoices: false));
    }

    [Fact]
    public void Last_list_wins()
    {
        var c = ChoiceParser.Parse("1. a\n2. b\n\nOr:\n1. x\n2. y\n3. z");
        Assert.Equal(["x", "y", "z"], c.Select(x => x.Label));
    }
}

public class UsageTrackerTests
{
    [Fact]
    public void Cost_uses_model_prices()
    {
        // 1M in + 1M out on Opus 5.5 = $4 + $20
        Assert.Equal(24m, UsageTracker.Cost("claude-opus-5-5", new UsageSample(1_000_000, 1_000_000, 0, 0)));
        // cache read $0.20/M, cache write 1.25x input
        Assert.Equal(0.20m + 5m, UsageTracker.Cost("claude-opus-5-5", new UsageSample(0, 0, 1_000_000, 1_000_000)));
        Assert.Equal(12m, UsageTracker.Cost("claude-sonnet-5-5", new UsageSample(1_000_000, 1_000_000, 0, 0)));
    }

    [Fact]
    public void Totals_persist_per_day()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "usage.json");
        var day = new DateTime(2026, 11, 19);
        var t = new UsageTracker(path);
        t.Record("claude-opus-5-5", new UsageSample(1000, 500, 0, 0), day);
        t.Record("claude-opus-5-5", new UsageSample(1000, 500, 0, 0), day);
        var again = new UsageTracker(path);
        Assert.Equal(2, again.Day(day).Requests);
        Assert.Equal(2000, again.Day(day).InputTokens);
        Assert.Equal(0.028m, again.MonthCost(day));
    }
}

/// <summary>
/// End-to-end chat loop against a fake Anthropic server on localhost: no real API
/// calls, no cost. Checks the request shape (caching, strict tools, effort,
/// fallbacks) and the streaming tool loop.
/// </summary>
public sealed class ClaudeChatServiceTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly Queue<string> _responses = new();
    public List<(string Beta, JsonDocument Body)> Requests { get; } = [];
    private readonly Task _serve;

    public ClaudeChatServiceTests()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_baseUrl + "/");
        _listener.Start();
        _serve = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            lock (Requests) Requests.Add((ctx.Request.Headers["anthropic-beta"] ?? "", JsonDocument.Parse(body)));
            var sse = _responses.Count > 0 ? _responses.Dequeue() : "";
            ctx.Response.ContentType = "text/event-stream";
            var bytes = Encoding.UTF8.GetBytes(sse);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    private static string Ev(string type, object data) =>
        $"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n";

    private static object Start(int inTok, int cacheRead) => new
    {
        type = "message_start",
        message = new
        {
            id = "msg_" + Guid.NewGuid().ToString("N")[..8], type = "message", role = "assistant", model = "claude-opus-5-5",
            content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, stop_details = (object?)null,
            container = (object?)null, context_management = (object?)null,
            usage = new { input_tokens = inTok, output_tokens = 1, cache_read_input_tokens = cacheRead, cache_creation_input_tokens = 0 },
        },
    };

    private static string ToolUseTurn() => ToolUseTurn("lookup_item", ["{\"upc\": \"0604100", "79591\"}"]);

    private static string ToolUseTurn(string tool, string[] jsonParts) =>
        Ev("message_start", Start(1200, 0)) +
        Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } }) +
        Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "sig-123" } }) +
        Ev("content_block_stop", new { type = "content_block_stop", index = 0 }) +
        Ev("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = "toolu_1", name = tool, input = new { }, caller = new { type = "direct" } } }) +
        string.Concat(jsonParts.Select(p => Ev("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = p } }))) +
        Ev("content_block_stop", new { type = "content_block_stop", index = 1 }) +
        Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 40 } }) +
        Ev("message_stop", new { type = "message_stop" });

    private static string TextTurn(string text, string stop = "end_turn") =>
        Ev("message_start", Start(1300, 1100)) +
        Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }) +
        Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = text[..(text.Length / 2)] } }) +
        Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = text[(text.Length / 2)..] } }) +
        Ev("content_block_stop", new { type = "content_block_stop", index = 0 }) +
        Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = stop, stop_sequence = (string?)null }, usage = new { output_tokens = 20 } }) +
        Ev("message_stop", new { type = "message_stop" });

    private static string RefusalTurn() =>
        Ev("message_start", Start(1200, 0)) +
        Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = "refusal", stop_sequence = (string?)null, stop_details = new { type = "refusal", category = "cyber", explanation = "x" } }, usage = new { output_tokens = 0 } }) +
        Ev("message_stop", new { type = "message_stop" });

    private ClaudeChatService Make(out List<ChatEvent> events)
    {
        var dir = Directory.CreateTempSubdirectory("esa-chat-").FullName;
        var lookup = new ItemLookupService(new MockItemRepository { Delay = TimeSpan.Zero }, new NameCache(Path.Combine(dir, "n.json")));
        var settings = new AppSettings();
        var svc = new ClaudeChatService(() => "sk-ant-test", settings, new ToolExecutor(lookup, settings), new UsageTracker())
        {
            BaseUrlOverride = _baseUrl,
        };
        var list = new List<ChatEvent>();
        events = list;
        return svc;
    }

    private sealed class SyncProgress(List<ChatEvent> list) : IProgress<ChatEvent>
    {
        public void Report(ChatEvent value) { lock (list) list.Add(value); }
    }

    [Fact]
    public async Task Tool_loop_streams_and_builds_correct_requests()
    {
        _responses.Enqueue(ToolUseTurn());
        _responses.Enqueue(TextTurn("LAYS ALL DRESS 220 g is $4.29."));
        var svc = Make(out var events);
        svc.AddContextNote("[scanned] 060410079591 → LAYS ALL DRESS 220 g, $4.29");

        var result = await svc.SendAsync("what's this one priced at?", new SyncProgress(events), CancellationToken.None);

        Assert.Equal("LAYS ALL DRESS 220 g is $4.29.", result.Text);
        Assert.False(result.Refused);
        Assert.True(result.CostUsd > 0);
        Assert.Equal(1100, result.CacheReadTokens);
        Assert.Equal(4, svc.HistoryCount); // user, assistant(tool_use), user(tool_result), assistant(text)

        Assert.Contains(events, e => e is ChatEvent.ToolStarted { ToolName: "lookup_item" });
        Assert.Contains(events, e => e is ChatEvent.ItemShown);
        Assert.Equal("LAYS ALL DRESS 220 g is $4.29.", string.Concat(events.OfType<ChatEvent.TextDelta>().Select(d => d.Text)));

        Assert.Equal(2, Requests.Count);
        var (beta, first) = Requests[0];
        var r = first.RootElement;
        Assert.Contains(ClaudeChatService.FallbackBeta, beta);
        Assert.Equal("default", r.GetProperty("fallbacks").GetString());
        Assert.Equal("claude-sonnet-5-5", r.GetProperty("model").GetString()); // text → chat model
        Assert.True(r.GetProperty("stream").GetBoolean());
        Assert.Equal("low", r.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(r.TryGetProperty("thinking", out _)); // model default (adaptive)
        Assert.Equal("ephemeral", r.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", r.GetProperty("cache_control").GetProperty("type").GetString());
        var tools = r.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(ChatTools.All.Count, tools.Count);
        Assert.All(tools, t => Assert.Equal(
            ChatTools.All.Single(d => d.Name == t.GetProperty("name").GetString()).Strict, t.GetProperty("strict").GetBoolean()));
        Assert.All(tools, t => Assert.False(t.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean()));
        // Context note goes into the owner's message, ahead of their text.
        var firstContent = r.GetProperty("messages")[0].GetProperty("content");
        Assert.StartsWith("[scanned]", firstContent[0].GetProperty("text").GetString());

        // Second request echoes the thinking block (with signature) and the tool_use, then the tool_result.
        var msgs = Requests[1].Body.RootElement.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        var assistant = msgs[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig-123", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[1].GetProperty("type").GetString());
        Assert.Equal("060410079591", assistant[1].GetProperty("input").GetProperty("upc").GetString());
        var toolResult = msgs[2].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolResult.GetProperty("tool_use_id").GetString());

        // System prompt and tools are byte-identical across requests (cache-safe).
        Assert.Equal(r.GetProperty("system").GetRawText(), Requests[1].Body.RootElement.GetProperty("system").GetRawText());
        Assert.Equal(r.GetProperty("tools").GetRawText(), Requests[1].Body.RootElement.GetProperty("tools").GetRawText());
    }

    [Fact]
    public async Task Receipts_go_before_the_text_as_image_and_document_blocks()
    {
        _responses.Enqueue(TextTurn("I read 3 lines."));
        var svc = Make(out _);
        var photo = new EstreamAssistant.Core.Receipts.ReceiptFile("pepsi.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 1],
            EstreamAssistant.Core.Receipts.ReceiptOrigin.File, null);
        var pdf = new EstreamAssistant.Core.Receipts.ReceiptFile("inv.pdf", "application/pdf", "%PDF-1.7"u8.ToArray(),
            EstreamAssistant.Core.Receipts.ReceiptOrigin.Inbox, null);

        await svc.SendAsync("30% markup on the chips", [photo, pdf], null, CancellationToken.None);

        var content = Requests[0].Body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(5, content.GetArrayLength());
        Assert.StartsWith("[receipt attached] pepsi.jpg (page 1 of 2)", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("base64", content[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", content[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(photo.Data), content[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("document", content[3].GetProperty("type").GetString());
        Assert.Equal("application/pdf", content[3].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("30% markup on the chips", content[4].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Photos_use_the_receipt_model_for_the_whole_turn_and_text_goes_back_to_the_chat_model()
    {
        _responses.Enqueue(TextTurn("Hi."));
        _responses.Enqueue(ToolUseTurn());
        _responses.Enqueue(TextTurn("I read 3 lines."));
        _responses.Enqueue(TextTurn("Done."));
        var svc = Make(out _);
        var photo = new EstreamAssistant.Core.Receipts.ReceiptFile("pepsi.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 1],
            EstreamAssistant.Core.Receipts.ReceiptOrigin.File, null);

        await svc.SendAsync("hello", null, CancellationToken.None);
        await svc.SendAsync("read this", [photo], null, CancellationToken.None);
        await svc.SendAsync("30% markup on the chips", null, CancellationToken.None);

        string Model(int i) => Requests[i].Body.RootElement.GetProperty("model").GetString()!;
        string Effort(int i) => Requests[i].Body.RootElement.GetProperty("output_config").GetProperty("effort").GetString()!;
        Assert.Equal(4, Requests.Count);
        Assert.Equal(("claude-sonnet-5-5", "low"), (Model(0), Effort(0)));
        // Both rounds of the receipt turn (tool call, then answer) stay on the receipt model.
        Assert.Equal(("claude-opus-5-5", "medium"), (Model(1), Effort(1)));
        Assert.Equal(("claude-opus-5-5", "medium"), (Model(2), Effort(2)));
        Assert.Equal(("claude-sonnet-5-5", "low"), (Model(3), Effort(3)));
        // Same conversation throughout: the last request carries the whole history.
        Assert.Equal(7, Requests[3].Body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData(0, "claude-haiku-5-5", "low", false)]
    [InlineData(2, "claude-opus-5-5", "high", true)]
    public void RouteFor_picks_the_model_by_attachments(int attachments, string model, string effort, bool receipt)
    {
        var s = new AppSettings { Model = " claude-haiku-5-5 ", Effort = "low", ReceiptModel = "claude-opus-5-5", ReceiptEffort = "high" };
        Assert.Equal(new ClaudeChatService.Route(model, effort, receipt), ClaudeChatService.RouteFor(s, attachments));
    }

    /// <summary>Spec 12.3: an attempt to save through chat is refused by the app, whatever the model asks for.</summary>
    [Theory]
    [InlineData("save_changes")]
    [InlineData("apply_changes")]
    [InlineData("post_stock_sheet")]
    public async Task Saving_through_chat_is_refused(string tool)
    {
        _responses.Enqueue(ToolUseTurn(tool, ["{}"]));
        _responses.Enqueue(TextTurn("I can't save. Please press SAVE yourself."));
        var svc = Make(out _);

        var result = await svc.SendAsync("save it now", null, CancellationToken.None);

        Assert.Equal("I can't save. Please press SAVE yourself.", result.Text);
        var toolResult = Requests[1].Body.RootElement.GetProperty("messages")[2].GetProperty("content")[0];
        Assert.True(toolResult.GetProperty("is_error").GetBoolean());
        Assert.Contains("Unknown tool", toolResult.GetProperty("content").GetRawText());
    }

    [Fact]
    public async Task Refusal_rolls_back_the_turn()
    {
        _responses.Enqueue(RefusalTurn());
        var svc = Make(out _);
        var result = await svc.SendAsync("hello", null, CancellationToken.None);
        Assert.True(result.Refused);
        Assert.Equal(0, svc.HistoryCount);
    }

    [Fact]
    public async Task Max_tokens_is_reported()
    {
        _responses.Enqueue(TextTurn("A long answer", "max_tokens"));
        var svc = Make(out _);
        var result = await svc.SendAsync("hi", null, CancellationToken.None);
        Assert.True(result.Truncated);
        Assert.Contains("cut off", result.Text);
    }

    [Fact]
    public async Task Server_error_gives_friendly_message_and_rolls_back()
    {
        // Empty body -> stream parse failure / error; history must stay clean.
        _responses.Enqueue("");
        var svc = Make(out _);
        var ex = await Assert.ThrowsAsync<ChatException>(() => svc.SendAsync("hi", null, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(0, svc.HistoryCount);
    }

    [Fact]
    public void System_prompt_is_embedded()
    {
        Assert.Contains("ask, don't hunt", ClaudeChatService.SystemPromptText);
        Assert.Equal(8, ClaudeChatService.SystemPromptVersion.Length);
    }

    [Fact]
    public async Task Missing_key_is_friendly()
    {
        var dir = Directory.CreateTempSubdirectory("esa-chat-").FullName;
        var lookup = new ItemLookupService(new MockItemRepository(), new NameCache(Path.Combine(dir, "n.json")));
        var svc = new ClaudeChatService(() => null, new AppSettings(), new ToolExecutor(lookup, new AppSettings()), new UsageTracker());
        Assert.False(svc.IsConfigured);
        var ex = await Assert.ThrowsAsync<ChatException>(() => svc.SendAsync("hi", null, CancellationToken.None));
        Assert.Contains("API key", ex.Message);
    }
}
