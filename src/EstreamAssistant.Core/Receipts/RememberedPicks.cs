using System.Text.Json;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Receipts;

/// <summary>
/// The items the owner picked or scanned for a receipt line, remembered by store and printed code (or the printed
/// name when there's no code), so the next receipt from that store matches them by itself. Costco's item numbers
/// mostly aren't in eStream, so without this the same items need picking on every Costco receipt.
/// Kept on this PC only (nothing changes in eStream). Picking a different item for the code replaces it.
/// </summary>
public sealed class RememberedPicks
{
    public sealed record Pick(long Upc, string Description, DateTime When);

    private readonly string? _file;
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pick> _picks;

    /// <param name="file">null keeps them in memory only (tests).</param>
    public RememberedPicks(string? file, AppLog? log = null)
    {
        _file = file;
        _log = log;
        _picks = Load(file, log);
    }

    public int Count { get { lock (_gate) return _picks.Count; } }

    /// <summary>The remembered item for this line on a receipt from <paramref name="store"/>, if any.</summary>
    public Pick? Find(string store, ReceiptLineInput input)
    {
        if (Key(store, input) is not { } key) return null;
        lock (_gate) return _picks.GetValueOrDefault(key);
    }

    /// <summary>Remembers the owner's item for this line (replaces an earlier pick for the same code).</summary>
    public void Remember(string store, ReceiptLineInput input, long upc, string description, DateTime when)
    {
        if (Key(store, input) is not { } key) return;
        lock (_gate)
        {
            if (_picks.TryGetValue(key, out var old) && old.Upc == upc) return;
            _picks[key] = new Pick(upc, description, when);
            Save();
        }
    }

    /// <summary>"COSTCO|code:14094": the store (letters and digits only) and the printed code, else the printed name.</summary>
    internal static string? Key(string store, ReceiptLineInput input)
    {
        var item = ReceiptQuestions.SameItemKey(input);
        if (item is null) return null;
        var shop = new string(store.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return shop + "|" + item;
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_picks, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Error("Saving remembered receipt picks failed", ex);
        }
    }

    private static Dictionary<string, Pick> Load(string? file, AppLog? log)
    {
        if (file is null || !File.Exists(file)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Pick>>(File.ReadAllText(file)) ?? []; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Error("Reading remembered receipt picks failed; starting fresh", ex);
            return [];
        }
    }
}
