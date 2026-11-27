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
    /// <summary>
    /// These receipts have no more uses (every line on a posted stock sheet, or finished by the owner): take their
    /// pictures out of the chat at the start of the next message, so they aren't sent again with every message after.
    /// Returns true if any of them still had pictures in the chat.
    /// </summary>
    bool DropReceiptPictures(IEnumerable<ReceiptFile> files);
    /// <summary>The [picture N] numbers a sent receipt's pictures were labelled with, in order (empty for a PDF sent whole).</summary>
    IReadOnlyList<int> PictureNumbers(ReceiptFile file);
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
    /// <summary>Pictures in one message once long receipts are cut into strips (the API allows 100).</summary>
    public const int MaxPictures = 48;
    /// <summary>Raw bytes of receipts in one message; base64 adds a third and the request limit is 32 MB.</summary>
    public const long MaxAttachmentBytes = 20_000_000;

    private readonly Func<string?> _apiKey;
    private readonly AppSettings _settings;
    private readonly ToolExecutor _tools;
    private readonly UsageTracker _usage;
    private readonly AppLog? _log;
    private readonly List<BetaMessageParam> _history = [];
    private readonly List<string> _notes = [];
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    /// <summary>Where each sent receipt's pictures are: the history message and its picture blocks.</summary>
    private readonly Dictionary<ReceiptFile, (int Message, List<int> Blocks)> _sentPictures = [];
    /// <summary>Receipts done with; their pictures go at the start of the next message (between turns).</summary>
    private readonly HashSet<ReceiptFile> _dropPending = [];
    private AnthropicClient? _client;
    private string? _clientKey;
    /// <summary>Model of the last completed turn (a switch means a cold cache, not a problem).</summary>
    private string? _lastModel;
    /// <summary>When the last request was answered: the prompt cache lives about 5 minutes.</summary>
    private DateTime _lastResponse = DateTime.MinValue;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Set by the app: a one-line summary of what is on screen NOW (pending list, count list, receipt), added to
    /// every owner message. Test 3: Claude said rows were "already in Pending changes" after the owner had cleared
    /// the list, because nothing told it so.
    /// </summary>
    public Func<string?> StateSnapshot { get; set; } = () => null;

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

    public bool DropReceiptPictures(IEnumerable<ReceiptFile> files)
    {
        bool any = false;
        lock (_sentPictures)
            foreach (var f in files)
                if (_sentPictures.ContainsKey(f) && _dropPending.Add(f)) any = true;
        return any;
    }

    internal const string PicturesRemovedNote =
        "[receipt pictures removed to save tokens: every line of this receipt is on a posted stock sheet, or the owner finished it or saved it for later. " +
        "Its lines are in the receipt table or in search_receipts. To look at the receipt itself again, ask the owner to attach it again.]";
    internal const string PictureRemovedMark = "[picture removed]";

    /// <summary>
    /// Swaps the pictures of receipts that are done with for a short note (the first picture) and a tiny mark (the
    /// rest, so the other blocks keep their places). The pictures went with every message (16,000+ tokens for a long
    /// receipt cut into strips), and the receipt table holds what was read.
    /// Editing an earlier message breaks the thinking blocks after it (preserved thinking: a 400 on newer accounts),
    /// so those are taken out too: the text and tool calls stay. Done once, between messages, so it costs one cold
    /// cache. Returns the number of pictures removed.
    /// </summary>
    private int ApplyPendingDrops()
    {
        List<(int Message, List<int> Blocks)> drops;
        lock (_sentPictures)
        {
            drops = _dropPending.Where(_sentPictures.ContainsKey).Select(f => _sentPictures[f]).ToList();
            foreach (var f in _dropPending) _sentPictures.Remove(f);
            _dropPending.Clear();
        }
        if (drops.Count == 0) return 0;

        int removed = 0;
        foreach (var (msg, picBlocks) in drops)
        {
            if (msg >= _history.Count || !_history[msg].TryPickUser(out var user)
                || !user.Content.TryPickBetaContentBlockParams(out var blocks)) continue;
            var edited = blocks.ToList();
            bool noted = false;
            foreach (var i in picBlocks)
            {
                if (i >= edited.Count || !IsPicture(edited[i])) continue;
                edited[i] = new BetaTextBlockParam { Text = noted ? PictureRemovedMark : PicturesRemovedNote };
                noted = true;
                removed++;
            }
            _history[msg] = new BetaUserMessageParam { Content = edited };
        }

        int first = drops.Min(d => d.Message);
        for (int i = first + 1; i < _history.Count; i++)
        {
            if (!_history[i].TryPickAssistant(out var asst) || !asst.Content.TryPickBetaContentBlockParams(out var blocks)) continue;
            var kept = blocks.Where(b => !b.TryPickThinking(out _) && !b.TryPickRedactedThinking(out _)).ToList();
            if (kept.Count == blocks.Count) continue;
            if (kept.Count == 0) kept.Add(new BetaTextBlockParam { Text = "(no reply)" });
            _history[i] = new BetaAssistantMessageParam { Content = kept };
        }
        _log?.Info($"Took {removed} receipt picture(s) out of the chat (receipt done with); they won't be sent again.");
        return removed;
    }

    private static bool IsPicture(BetaContentBlockParam b) => b.TryPickImage(out _) || b.TryPickRequestDocumentBlock(out _);

    public void Reset()
    {
        lock (_sentPictures)
        {
            _sentPictures.Clear();
            _dropPending.Clear();
        }
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
        if (attachments.Sum(a => Math.Max(1, a.Parts.Count)) > MaxPictures || attachments.Sum(a => a.SentBytes) > MaxAttachmentBytes)
            throw new ChatException("These receipts are too much for one message. Send fewer at a time.");
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        ApplyPendingDrops();
        int snapshot = _history.Count;
        try
        {
            var client = Client();
            var (ownerContent, pictures) = BuildOwnerContent(ownerText, attachments);
            _history.Add(new BetaUserMessageParam { Content = ownerContent });
            lock (_sentPictures)
                foreach (var (file, blocks) in pictures) _sentPictures[file] = (snapshot, blocks);
            if (_settings.DebugLogging) _log?.Info($"[debug] Owner: {ownerText}");
            var route = RouteFor(_settings, attachments.Count);
            // A warm cache only exists for the model the previous turn used.
            // ...and only for about 5 minutes after the last request (test 3 warned after a 19-minute gap).
            bool warm = snapshot > 0 && _lastModel == route.Model && DateTime.Now - _lastResponse < CacheLifetime;
            if (snapshot > 0 && _lastModel is not null && _lastModel != route.Model)
                _log?.Info($"Switching model {_lastModel} -> {route.Model}{(route.IsReceipt ? " (receipt)" : "")}; this message writes a fresh cache.");
            if (attachments.Count > 0)
                _log?.Info($"Sent {attachments.Count} receipt file(s): {string.Join(", ", attachments.Select(a => $"{a.FileName} {a.MediaType} {a.SizeText}{(a.Parts.Count > 0 ? $" as {a.Parts.Count} picture(s)" : "")}"))}");

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
                    _log?.Warn("Prompt cache: 0 tokens read on a warm conversation (something before the cache breakpoint changed).");
                _lastResponse = DateTime.Now;
                _log?.Info($"Claude {model} ({(route.IsReceipt ? "receipt" : "chat")}, effort {route.Effort}): stop={msg.StopReason} in={sample.InputTokens} out={sample.OutputTokens} " +
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

    /// <summary>The owner's message, and where each receipt's pictures are in it.</summary>
    private (List<BetaContentBlockParam> Content, List<(ReceiptFile File, List<int> Blocks)> Pictures) BuildOwnerContent(
        string ownerText, IReadOnlyList<ReceiptFile> attachments)
    {
        var content = new List<BetaContentBlockParam>();
        var pictures = new List<(ReceiptFile, List<int>)>();
        // Every picture gets a number for the whole app run, so Claude can say which picture a line is on.
        string Number(ReceiptFile file)
        {
            var n = Interlocked.Increment(ref _pictureCount);
            lock (_pictureNumbers)
            {
                if (!_pictureNumbers.TryGetValue(file, out var list)) _pictureNumbers[file] = list = [];
                list.Add(n);
            }
            return $"picture {n}";
        }
        // Receipts go first (before the text that refers to them), each with a short label.
        for (int i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            var head = $"[receipt attached] {a.FileName} (page {i + 1} of {attachments.Count})";
            var at = new List<int>();
            pictures.Add((a, at));
            lock (_pictureNumbers) _pictureNumbers.Remove(a);
            if (a.Parts.Count == 0)
            {
                content.Add(new BetaTextBlockParam { Text = a.Kind == ReceiptKind.Image ? $"{head} [{Number(a)}]" : head });
                at.Add(content.Count);
                content.Add(AttachmentBlock(a));
                continue;
            }
            content.Add(new BetaTextBlockParam { Text = a.Note.Length > 0 ? head + " " + a.Note : head });
            foreach (var part in a.Parts)
            {
                var number = Number(a);
                content.Add(new BetaTextBlockParam { Text = part.Label.Length > 0 ? $"[{number}: {a.FileName}, {part.Label}]" : $"[{number}: {a.FileName}]" });
                at.Add(content.Count);
                content.Add(ImageBlock(part.Data, part.MediaType));
            }
        }
        lock (_notes)
        {
            if (_notes.Count > 0)
            {
                content.Add(new BetaTextBlockParam { Text = string.Join("\n", _notes) });
                _notes.Clear();
            }
        }
        string? state = null;
        try { state = StateSnapshot(); }
        catch (Exception ex) { _log?.Error("App state for Claude failed", ex); }
        if (!string.IsNullOrWhiteSpace(state)) content.Add(new BetaTextBlockParam { Text = "[app now] " + state });
        content.Add(new BetaTextBlockParam { Text = ownerText });
        return (content, pictures);
    }

    internal static BetaContentBlockParam AttachmentBlock(ReceiptFile a)
    {
        if (a.Kind == ReceiptKind.Pdf)
            return new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(a.Data) }, Title = a.FileName };
        return ImageBlock(a.Data, a.MediaType);
    }

    internal static BetaImageBlockParam ImageBlock(byte[] bytes, string mediaType) =>
        new()
        {
            Source = new BetaBase64ImageSource
            {
                Data = Convert.ToBase64String(bytes),
                MediaType = mediaType switch
                {
                    "image/png" => MediaType.ImagePng,
                    "image/gif" => MediaType.ImageGif,
                    "image/webp" => MediaType.ImageWebP,
                    _ => MediaType.ImageJpeg,
                },
            },
        };

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
            ChatTools.ListItems => $"Listing every item with \"{Arg("text")}\"…",
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

    private int _pictureCount;
    private readonly Dictionary<ReceiptFile, List<int>> _pictureNumbers = [];

    public IReadOnlyList<int> PictureNumbers(ReceiptFile file)
    {
        lock (_pictureNumbers) return _pictureNumbers.TryGetValue(file, out var list) ? list.ToList() : [];
    }

    private void Rollback(int snapshot)
    {
        if (_history.Count > snapshot) _history.RemoveRange(snapshot, _history.Count - snapshot);
        lock (_sentPictures)
            foreach (var f in _sentPictures.Where(kv => kv.Value.Message >= snapshot).Select(kv => kv.Key).ToList())
                _sentPictures.Remove(f);
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
        return new(FriendlyText(ex), ex);
    }

    /// <summary>
    /// What to tell the store owner when a request fails: what happened and what to do, in plain words.
    /// The API's own wording (JSON, status codes, request ids) only goes to the app log and the Bugs list.
    /// </summary>
    internal static string FriendlyText(Exception ex)
    {
        if (ex is ChatException c) return c.Message;
        var why = ex.Message;
        bool Says(params string[] words) => words.Any(w => why.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (ex is AnthropicPaymentRequiredException || Says("credit balance", "billing", "spend limit", "usage limit"))
            return "The Claude account has run out of credit, so Claude can't answer right now. Whoever pays for " +
                   "the Claude account needs to add credit at console.anthropic.com (under Plans & Billing).";
        if (ex is AnthropicApiException && Says("on hold"))
            return "The Claude account is on hold, so Claude can't answer. Whoever runs the Claude account needs " +
                   "to check it at console.anthropic.com.";
        if (ex is AnthropicUnauthorizedException) return "Claude didn't accept the API key. Check it in Settings.";
        if (ex is AnthropicForbiddenException) return "This API key isn't allowed to use that model. Check Settings.";
        if (ex is AnthropicRateLimitException)
            return "Claude is busy right now (too many messages at once). Wait a minute and try again.";
        if (ex is AnthropicOverloadedException or Anthropic5xxException or AnthropicServiceUnavailableException
                or AnthropicGatewayTimeoutException)
            return "Claude's service is having trouble right now. Try again in a minute.";
        if (ex is AnthropicApiException && Says("compiled grammar", "tool schema", "strict tools"))
            return "This version of eStream Assistant has a problem talking to Claude, so no message will go through. " +
                   "Send in the Bugs list and install the next update. Everything else in the app still works.";
        if (ex is AnthropicContentTooLargeException || Says("prompt is too long", "too many tokens", "context window", "request too large"))
            return "This chat has got too long for Claude. Start a new chat. If you sent receipts, send fewer at a time.";
        if (ex is AnthropicApiException && Says("image", "pdf", "document"))
            return "Claude couldn't open one of the attached files. Try a clearer or smaller copy, or a different file.";
        if (ex is AnthropicIOException or HttpRequestException or TaskCanceledException)
            return "Can't reach Claude. Check the internet connection.";
        if (ex is AnthropicBadRequestException)
            return "Claude couldn't handle that message. Start a new chat and try again. If it keeps happening, " +
                   "send in the Bugs list.";
        return "Something went wrong talking to Claude. If it keeps happening, send in the Bugs list.";
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
