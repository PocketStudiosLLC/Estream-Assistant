using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.Core.Claude;

/// <summary>Things that happen during a chat turn, reported to the UI as they happen.</summary>
public abstract record ChatEvent
{
    public sealed record TextDelta(string Text) : ChatEvent;
    public sealed record ToolStarted(string ToolName, string Summary) : ChatEvent;
    public sealed record ItemShown(UpcLookupResult Lookup) : ChatEvent;
}

public sealed record ChatTurnResult(string Text, bool Refused, bool Truncated, decimal CostUsd, long CacheReadTokens);

/// <summary>A chat failure with a message the owner can understand.</summary>
public sealed class ChatException(string friendlyMessage, Exception? inner = null) : Exception(friendlyMessage, inner);

public interface IChatService
{
    bool IsConfigured { get; }
    /// <summary>Adds app context (e.g. a scan) to the start of the next owner message.</summary>
    void AddContextNote(string note);
    void Reset();
    Task<ChatTurnResult> SendAsync(string ownerText, IProgress<ChatEvent>? progress, CancellationToken ct) =>
        SendAsync(ownerText, [], progress, ct);
    /// <summary>Sends the owner's message with receipt photos/PDFs (F5.2) placed before the text.</summary>
    Task<ChatTurnResult> SendAsync(string ownerText, IReadOnlyList<ReceiptFile> attachments, IProgress<ChatEvent>? progress, CancellationToken ct);
    Task TestConnectionAsync(CancellationToken ct);
}

/// <summary>
/// Claude conversation + manual tool loop over the beta Messages API (streaming).
///  - System prompt and tool list are frozen and cached (cache_control on the system block),
///    plus top-level automatic caching for the growing conversation.
///  - Model routing: a message with a photo or PDF (and its whole tool loop) uses the receipt model
///    and effort; every other message uses the cheaper chat model. Caches are per model, so the
///    first message after a switch writes a new cache instead of reading one.
///  - Effort comes from Settings; thinking is left at the model default (adaptive).
///  - Server-side fallbacks ("default") re-serve a safety-classifier refusal on another model.
///  - Claude can only call the read-only tools in <see cref="ChatTools"/>.
/// History is append-only: a failed turn is rolled back as a whole, never edited.
/// </summary>
public sealed class ClaudeChatService : IChatService
{
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public const int MaxToolRounds = 8;
    public const int MaxAttachments = 8;

    private readonly Func<string?> _apiKey;
    private readonly AppSettings _settings;
    private readonly ToolExecutor _tools;
    private readonly UsageTracker _usage;
    private readonly AppLog? _log;
    private readonly List<BetaMessageParam> _history = [];
    private readonly List<string> _notes = [];
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private AnthropicClient? _client;
    private string? _clientKey;
    /// <summary>Model of the last completed turn (a switch means a cold cache, not a problem).</summary>
    private string? _lastModel;

    /// <summary>Tests only: send requests to a local fake server instead of the real API.</summary>
    internal string? BaseUrlOverride { get; init; }

    public static string SystemPromptText { get; } = LoadSystemPrompt();
    public static string SystemPromptVersion { get; } =
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(SystemPromptText)))[..8];

    public ClaudeChatService(Func<string?> apiKey, AppSettings settings, ToolExecutor tools, UsageTracker usage, AppLog? log = null)
    {
        _apiKey = apiKey;
        _settings = settings;
        _tools = tools;
        _usage = usage;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey());

    public int HistoryCount => _history.Count;

    public void AddContextNote(string note)
    {
        lock (_notes)
        {
            _notes.Add(note);
            while (_notes.Count > 30) _notes.RemoveAt(0);
        }
    }

    /// <summary>Which model and effort a message uses: photos/PDFs go to the receipt model.</summary>
    public readonly record struct Route(string Model, string Effort, bool IsReceipt);

    internal static Route RouteFor(AppSettings s, int attachmentCount) =>
        attachmentCount > 0 && !string.IsNullOrWhiteSpace(s.ReceiptModel)
            ? new Route(s.ReceiptModel.Trim(), s.ReceiptEffort, true)
            : new Route(s.Model.Trim(), s.Effort, false);

    public void Reset()
    {
        _history.Clear();
        _lastModel = null;
        lock (_notes) _notes.Clear();
        _log?.Info($"Chat session reset (prompt {SystemPromptVersion}).");
    }

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        var client = Client();
        // Test both models, so a key without access to one of them shows up here, not mid-receipt.
        foreach (var model in new[] { _settings.Model.Trim(), _settings.ReceiptModel.Trim() }.Where(m => m.Length > 0).Distinct())
        {
            try
            {
                await client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = 256,
                    OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                    Messages = [new BetaUserMessageParam { Content = "Reply with the word OK." }],
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var f = Friendly(ex);
                throw new ChatException($"{model}: {f.Message}", ex);
            }
        }
    }

    public Task<ChatTurnResult> SendAsync(string ownerText, IProgress<ChatEvent>? progress, CancellationToken ct) =>
        SendAsync(ownerText, [], progress, ct);

    public async Task<ChatTurnResult> SendAsync(string ownerText, IReadOnlyList<ReceiptFile> attachments, IProgress<ChatEvent>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerText)) throw new ArgumentException("Message is empty.", nameof(ownerText));
        if (attachments.Count > MaxAttachments) throw new ChatException($"Send at most {MaxAttachments} receipt pages at a time.");
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        int snapshot = _history.Count;
        try
        {
            var client = Client();
            _history.Add(new BetaUserMessageParam { Content = BuildOwnerContent(ownerText, attachments) });
            if (_settings.DebugLogging) _log?.Info($"[debug] Owner: {ownerText}");
            var route = RouteFor(_settings, attachments.Count);
            // A warm cache only exists for the model the previous turn used.
            bool warm = snapshot > 0 && _lastModel == route.Model;
            if (snapshot > 0 && _lastModel is not null && _lastModel != route.Model)
                _log?.Info($"Switching model {_lastModel} -> {route.Model}{(route.IsReceipt ? " (receipt)" : "")}; this message writes a fresh cache.");
            if (attachments.Count > 0)
                _log?.Info($"Sent {attachments.Count} receipt file(s): {string.Join(", ", attachments.Select(a => $"{a.FileName} {a.MediaType} {a.SizeText}"))}");

            decimal cost = 0;
            long cacheRead = 0;
            for (int round = 0; round < MaxToolRounds; round++)
            {
                BetaMessage msg = await StreamOneAsync(client, route, progress, ct).ConfigureAwait(false);

                var model = msg.Model.Raw() ?? route.Model;
                var u = msg.Usage;
                var sample = new UsageSample(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0);
                cost += _usage.Record(model, sample);
                cacheRead += sample.CacheReadTokens;
                // Spec 7.1: on a warm conversation the cached prefix should be read back.
                if ((warm || round > 0) && sample.CacheReadTokens == 0)
                    _log?.Warn("Prompt cache: 0 tokens read on a warm conversation (something before the cache breakpoint changed, or the cache expired).");
                _log?.Info($"Claude {model}: stop={msg.StopReason} in={sample.InputTokens} out={sample.OutputTokens} " +
                           $"cache_read={sample.CacheReadTokens} cache_write={sample.CacheWriteTokens} est=${cost:0.0000}");

                var stop = msg.StopReason?.Raw();
                if (stop == "refusal")
                {
                    var cat = msg.StopDetails?.Category?.Raw();
                    _log?.Warn($"Claude refused (category {cat ?? "none"}).");
                    Rollback(snapshot);
                    return new ChatTurnResult(
                        "Sorry, I can't help with that request. Try asking in a different way.", true, false, cost, cacheRead);
                }

                _history.Add(new BetaAssistantMessageParam { Content = ToParams(msg.Content) });
                var text = FinalText(msg.Content);

                if (stop != "tool_use")
                {
                    if (_settings.DebugLogging) _log?.Info($"[debug] Claude: {text}");
                    bool truncated = stop == "max_tokens";
                    if (truncated) text += "\n\n(My answer was cut off. Ask me to continue.)";
                    _lastModel = route.Model;
                    return new ChatTurnResult(text, false, truncated, cost, cacheRead);
                }

                // Run every tool call; ALL results go back in ONE user message.
                var results = new List<BetaContentBlockParam>();
                foreach (var block in msg.Content)
                {
                    if (!block.TryPickToolUse(out BetaToolUseBlock? tu)) continue;
                    progress?.Report(new ChatEvent.ToolStarted(tu.Name, Describe(tu)));
                    var outcome = await _tools.ExecuteAsync(tu.Name, tu.Input, ct).ConfigureAwait(false);
                    _log?.Info($"Tool {tu.Name} {JsonSerializer.Serialize(tu.Input)} -> {(outcome.IsError ? "ERROR " : "")}{Trim(outcome.Json, 300)}");
                    if (outcome.ShownItem is not null) progress?.Report(new ChatEvent.ItemShown(outcome.ShownItem));
                    results.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = tu.ID,
                        Content = outcome.Json,
                        IsError = outcome.IsError,
                    });
                }
                _history.Add(new BetaUserMessageParam { Content = results });
            }

            Rollback(snapshot);
            throw new ChatException("That took too many steps. Please ask again in a simpler way.");
        }
        catch (OperationCanceledException)
        {
            Rollback(snapshot);
            throw;
        }
        catch (ChatException)
        {
            Rollback(snapshot);
            throw;
        }
        catch (Exception ex)
        {
            Rollback(snapshot);
            throw Friendly(ex);
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private async Task<BetaMessage> StreamOneAsync(AnthropicClient client, Route route, IProgress<ChatEvent>? progress, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = route.Model,
            MaxTokens = 64000,
            // Frozen prefix: tools -> system. Nothing volatile before this breakpoint.
            System = new List<BetaTextBlockParam>
            {
                new() { Text = SystemPromptText, CacheControl = new BetaCacheControlEphemeral() },
            },
            Tools = Tools,
            // Automatic caching of the growing conversation.
            CacheControl = new BetaCacheControlEphemeral(),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(route.Effort) },
            Messages = [.. _history],
        };
        if (SupportsServerFallback(route.Model))
        {
            p = p with { Betas = [FallbackBeta], Fallbacks = new Default() };
        }

        var stream = client.Beta.Messages.CreateStreaming(p, ct);
        return await Tap(stream, progress, ct).Aggregate().ConfigureAwait(false);
    }

    /// <summary>Passes events through to the aggregator while reporting text as it streams.</summary>
    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Tap(
        IAsyncEnumerable<BetaRawMessageStreamEvent> source, IProgress<ChatEvent>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (progress is not null && e.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t))
                progress.Report(new ChatEvent.TextDelta(t.Text));
            yield return e;
        }
    }

    internal static bool SupportsServerFallback(string model) =>
        !model.Contains("haiku", StringComparison.OrdinalIgnoreCase);

    internal static Effort ParseEffort(string effort) => effort switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => Effort.Low,
    };

    private static readonly List<BetaToolUnion> Tools = ChatTools.All
        .Select(t => (BetaToolUnion)new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = InputSchema.FromRawUnchecked(t.Schema),
            Strict = t.Strict,
        })
        .ToList();

    private List<BetaContentBlockParam> BuildOwnerContent(string ownerText, IReadOnlyList<ReceiptFile> attachments)
    {
        var content = new List<BetaContentBlockParam>();
        // Receipts go first (before the text that refers to them), each with a short label.
        for (int i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            content.Add(new BetaTextBlockParam { Text = $"[receipt attached] {a.FileName} (page {i + 1} of {attachments.Count})" });
            content.Add(AttachmentBlock(a));
        }
        lock (_notes)
        {
            if (_notes.Count > 0)
            {
                content.Add(new BetaTextBlockParam { Text = string.Join("\n", _notes) });
                _notes.Clear();
            }
        }
        content.Add(new BetaTextBlockParam { Text = ownerText });
        return content;
    }

    internal static BetaContentBlockParam AttachmentBlock(ReceiptFile a)
    {
        var data = Convert.ToBase64String(a.Data);
        if (a.Kind == ReceiptKind.Pdf)
            return new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data }, Title = a.FileName };
        return new BetaImageBlockParam
        {
            Source = new BetaBase64ImageSource
            {
                Data = data,
                MediaType = a.MediaType switch
                {
                    "image/png" => MediaType.ImagePng,
                    "image/gif" => MediaType.ImageGif,
                    "image/webp" => MediaType.ImageWebP,
                    _ => MediaType.ImageJpeg,
                },
            },
        };
    }

    /// <summary>
    /// Converts the response back into params for the history, unchanged.
    /// After a mid-output server-side fallback, blocks BEFORE the last "fallback" block are
    /// reduced to their text (thinking/tool_use there belonged to the declined attempt), and the
    /// fallback block itself stays exactly where it was.
    /// </summary>
    internal static List<BetaContentBlockParam> ToParams(IReadOnlyList<BetaContentBlock> content)
    {
        int lastFallback = -1;
        for (int i = 0; i < content.Count; i++)
            if (content[i].TryPickFallback(out _)) lastFallback = i;

        var list = new List<BetaContentBlockParam>();
        for (int i = 0; i < content.Count; i++)
        {
            var b = content[i];
            bool beforeBoundary = i < lastFallback;
            if (b.TryPickText(out BetaTextBlock? text))
                list.Add(new BetaTextBlockParam { Text = text.Text });
            else if (b.TryPickFallback(out BetaFallbackBlock? fb))
                list.Add(BetaFallbackBlockParam.FromRawUnchecked(fb.RawData));
            else if (beforeBoundary)
                continue;
            else if (b.TryPickThinking(out BetaThinkingBlock? th))
                list.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (b.TryPickRedactedThinking(out BetaRedactedThinkingBlock? rt))
                list.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
            else if (b.TryPickToolUse(out BetaToolUseBlock? tu))
                list.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
        }
        if (list.Count == 0) list.Add(new BetaTextBlockParam { Text = "(no reply)" });
        return list;
    }

    private static string FinalText(IReadOnlyList<BetaContentBlock> content)
    {
        var sb = new StringBuilder();
        foreach (var b in content)
            if (b.TryPickText(out BetaTextBlock? t)) sb.Append(t.Text);
        return sb.ToString().Trim();
    }

    private static string Describe(BetaToolUseBlock tu)
    {
        string Arg(string k) => tu.Input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return tu.Name switch
        {
            ChatTools.LookupItem => $"Looking up {Arg("upc")}…",
            ChatTools.FindItems => $"Searching for \"{Arg("text")}\"…",
            ChatTools.GetCodes => "Reading deposit and levy codes…",
            ChatTools.CalcPrice => "Working out the price…",
            ChatTools.StageChanges => "Adding to pending changes…",
            ChatTools.RunDryRun => "Running a dry run in eStream (don't touch the mouse or keyboard)…",
            ChatTools.StockCountPreview => "Comparing counts with eStream…",
            ChatTools.CreateStockSheet => "Asking you to confirm a new stock sheet…",
            ChatTools.ShowReceiptLines => "Matching the receipt lines to eStream items…",
            ChatTools.PriceReceiptLines => "Working out prices for the receipt…",
            ChatTools.StockReport => "Making the stock report…",
            ChatTools.StockSheets => "Reading stock sheets…",
            _ => "Working…",
        };
    }

    private void Rollback(int snapshot)
    {
        if (_history.Count > snapshot) _history.RemoveRange(snapshot, _history.Count - snapshot);
    }

    private AnthropicClient Client()
    {
        var key = _apiKey();
        if (string.IsNullOrEmpty(key)) throw new ChatException("No Claude API key yet. Add one in Settings.");
        if (_client is null || _clientKey != key)
        {
            _client = BaseUrlOverride is null
                ? new AnthropicClient { ApiKey = key }
                : new AnthropicClient { ApiKey = key, BaseUrl = BaseUrlOverride, MaxRetries = 0 };
            _clientKey = key;
        }
        return _client;
    }

    private ChatException Friendly(Exception ex)
    {
        _log?.Error("Claude request failed", ex);
        return ex switch
        {
            ChatException c => c,
            AnthropicUnauthorizedException => new("Claude didn't accept the API key. Check it in Settings.", ex),
            AnthropicForbiddenException => new("This API key isn't allowed to use that model. Check Settings.", ex),
            AnthropicRateLimitException => new("Claude is busy right now (rate limit). Wait a minute and try again.", ex),
            AnthropicOverloadedException or Anthropic5xxException or AnthropicServiceUnavailableException =>
                new("Claude's service is having trouble right now. Try again in a minute.", ex),
            AnthropicBadRequestException => new("Claude couldn't read that request. Start a new chat and try again. " +
                $"If it keeps happening, the reason is: {Trim(ex.Message, 300)}", ex),
            AnthropicIOException or HttpRequestException or TaskCanceledException =>
                new("Can't reach Claude. Check the internet connection.", ex),
            _ => new("Something went wrong talking to Claude. The details are in the app log.", ex),
        };
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string LoadSystemPrompt()
    {
        var asm = typeof(ClaudeChatService).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("SystemPrompt.md", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n").Trim();
    }
}
