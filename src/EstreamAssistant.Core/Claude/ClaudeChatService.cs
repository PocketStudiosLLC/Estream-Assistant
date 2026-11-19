using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
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
    Task<ChatTurnResult> SendAsync(string ownerText, IProgress<ChatEvent>? progress, CancellationToken ct);
    Task TestConnectionAsync(CancellationToken ct);
}

/// <summary>
/// Claude conversation + manual tool loop over the beta Messages API (streaming).
///  - System prompt and tool list are frozen and cached (cache_control on the system block),
///    plus top-level automatic caching for the growing conversation.
///  - Effort comes from Settings; thinking is left at the model default (adaptive on Opus 5.5).
///  - Server-side fallbacks ("default") re-serve a safety-classifier refusal on another model.
///  - Claude can only call the read-only tools in <see cref="ChatTools"/>.
/// History is append-only: a failed turn is rolled back as a whole, never edited.
/// </summary>
public sealed class ClaudeChatService : IChatService
{
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public const int MaxToolRounds = 8;

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

    public void Reset()
    {
        _history.Clear();
        lock (_notes) _notes.Clear();
        _log?.Info($"Chat session reset (prompt {SystemPromptVersion}).");
    }

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        var client = Client();
        try
        {
            await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = _settings.Model,
                MaxTokens = 256,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                Messages = [new BetaUserMessageParam { Content = "Reply with the word OK." }],
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Friendly(ex);
        }
    }

    public async Task<ChatTurnResult> SendAsync(string ownerText, IProgress<ChatEvent>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerText)) throw new ArgumentException("Message is empty.", nameof(ownerText));
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        int snapshot = _history.Count;
        try
        {
            var client = Client();
            _history.Add(new BetaUserMessageParam { Content = BuildOwnerContent(ownerText) });

            decimal cost = 0;
            long cacheRead = 0;
            for (int round = 0; round < MaxToolRounds; round++)
            {
                BetaMessage msg = await StreamOneAsync(client, progress, ct).ConfigureAwait(false);

                var model = msg.Model.Raw() ?? _settings.Model;
                var u = msg.Usage;
                var sample = new UsageSample(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0);
                cost += _usage.Record(model, sample);
                cacheRead += sample.CacheReadTokens;
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
                    bool truncated = stop == "max_tokens";
                    if (truncated) text += "\n\n(My answer was cut off. Ask me to continue.)";
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

    private async Task<BetaMessage> StreamOneAsync(AnthropicClient client, IProgress<ChatEvent>? progress, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = 64000,
            // Frozen prefix: tools -> system. Nothing volatile before this breakpoint.
            System = new List<BetaTextBlockParam>
            {
                new() { Text = SystemPromptText, CacheControl = new BetaCacheControlEphemeral() },
            },
            Tools = Tools,
            // Automatic caching of the growing conversation.
            CacheControl = new BetaCacheControlEphemeral(),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_settings.Effort) },
            Messages = [.. _history],
        };
        if (SupportsServerFallback(_settings.Model))
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
            Strict = true,
        })
        .ToList();

    private List<BetaContentBlockParam> BuildOwnerContent(string ownerText)
    {
        var content = new List<BetaContentBlockParam>();
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
            AnthropicBadRequestException => new("Claude couldn't read that request. Start a new chat and try again.", ex),
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
