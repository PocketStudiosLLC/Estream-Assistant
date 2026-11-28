using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Receipts;

/// <summary>One picture of a saved receipt (a photo, a strip of a long receipt, or a scanned page), top to bottom.</summary>
public sealed record SavedPicture(string File, string MediaType, string Label);

/// <summary>An original receipt file kept with the saved receipt, so it can be filed in Receipts\Done at the end.</summary>
public sealed record SavedFile(string FileName, string MediaType, string StoredAs);

/// <summary>A receipt line as saved: what was read, plus the item it matched and its state then (for the list and for Claude).</summary>
public sealed record SavedLine(ReceiptLineInput Input, long? Upc, string? Item, ReceiptLineStatus Status, bool NeedsOwner);

/// <summary>A receipt saved for later (the Pending tab), or a finished one kept for price questions.</summary>
public sealed record SavedReceipt
{
    public required string Id { get; init; }
    public string Source { get; init; } = "";
    /// <summary>Store or supplier printed on the receipt.</summary>
    public string Store { get; init; } = "";
    /// <summary>Date printed on the receipt.</summary>
    public DateOnly? Date { get; init; }
    /// <summary>The owner's own name for it (Rename on the Pending tab); empty = store and date.</summary>
    public string Name { get; init; } = "";
    public DateTime Saved { get; init; }
    public DateTime Updated { get; init; }
    /// <summary>Set once the receipt is finished: it leaves the Pending tab, its lines stay for search_receipts.</summary>
    public DateTime? Finished { get; init; }
    public List<SavedLine> Lines { get; init; } = [];
    /// <summary>Receipt line → stock sheet it went on (null: number unknown).</summary>
    public Dictionary<int, int?> OnSheet { get; init; } = [];
    public List<SavedPicture> Pictures { get; init; } = [];
    public List<SavedFile> Files { get; init; } = [];

    /// <summary>"Superstore · Nov 25, 2026", the owner's name for it, or the file name and the day it was saved.</summary>
    [JsonIgnore] public string Title => Name.Length > 0 ? Name
        : ReceiptNames.Title(Store, Date, Source) is { Length: > 0 } t ? t
        : $"{Files.FirstOrDefault()?.FileName ?? "Receipt"} · saved {Saved.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-CA"))}";
    /// <summary>Things left to do, counted like the Pending tab's questions: repeat scans of an item that needs its
    /// barcode or a pick are one.</summary>
    [JsonIgnore] public int NeedsOwner => Lines.Where(l => l.NeedsOwner && !OnSheet.ContainsKey(l.Input.Line))
        .DistinctBy(l => l.Status is ReceiptLineStatus.NoMatch or ReceiptLineStatus.NeedsChoice && ReceiptQuestions.SameItemKey(l.Input) is { } key
            ? key + "|" + l.Status : "line " + l.Input.Line)
        .Count();
}

/// <summary>
/// Receipts kept by the app (in its own folder, not the engine's): saved for later on the Pending tab, with their
/// pictures and original files, so the owner can finish them without sending the receipt to Claude again. Finished
/// receipts keep just their lines (a few KB) so Claude can answer "what did we pay for X" from search_receipts.
/// </summary>
public sealed class SavedReceipts
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppLog? _log;
    private readonly object _gate = new();
    private readonly List<SavedReceipt> _all = [];

    public SavedReceipts(string folder, AppLog? log = null)
    {
        PendingFolder = Path.Combine(folder, "pending");
        DoneFolder = Path.Combine(folder, "done");
        _log = log;
        Load();
    }

    public string PendingFolder { get; }
    public string DoneFolder { get; }

    public event EventHandler? Changed;

    /// <summary>Saved for later, oldest first.</summary>
    public IReadOnlyList<SavedReceipt> Pending { get { lock (_gate) return _all.Where(r => r.Finished is null).OrderBy(r => r.Saved).ToList(); } }
    public IReadOnlyList<SavedReceipt> All { get { lock (_gate) return _all.ToList(); } }

    public SavedReceipt? Get(string id) { lock (_gate) return _all.FirstOrDefault(r => r.Id == id); }

    /// <summary>
    /// Saves the open receipt for later: its lines, which are on a stock sheet, its pictures and its original files.
    /// Files from the engine's Receipts folder are moved here (so they aren't offered again at startup); others are copied.
    /// </summary>
    /// <param name="pictureOf">Picture number as labelled for Claude → (file, picture of that file).</param>
    public SavedReceipt SaveForLater(string source, IReadOnlyList<ReceiptLine> lines, IReadOnlyDictionary<int, int?> onSheet,
        IReadOnlyList<ReceiptFile> files, Func<int, (int File, int Part)?> pictureOf, DateTime now, string store = "", DateOnly? date = null)
    {
        var id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        lock (_gate) for (int n = 2; _all.Any(r => r.Id == id) || Directory.Exists(Path.Combine(PendingFolder, id)); n++)
            id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + n;
        var dir = Directory.CreateDirectory(Path.Combine(PendingFolder, id)).FullName;

        // The pictures as Claude saw them, top to bottom: picture k of file f is number first[f] + k.
        var pictures = new List<SavedPicture>();
        var first = new List<int>();
        foreach (var f in files)
        {
            first.Add(pictures.Count + 1);
            var shown = f.Parts.Count > 0 ? f.Parts.Select(p => (p.Data, p.MediaType, p.Label)).ToList()
                : f.Kind == ReceiptKind.Image ? [(f.Data, f.MediaType, "")]
                : [];
            foreach (var (data, type, label) in shown)
            {
                var name = $"picture{pictures.Count + 1:00}{Ext(type)}";
                File.WriteAllBytes(Path.Combine(dir, name), data);
                pictures.Add(new SavedPicture(name, type, string.Join(", ", new[] { files.Count > 1 ? f.FileName : "", label }.Where(s => s.Length > 0))));
            }
        }

        var saved = new List<SavedFile>();
        foreach (var f in files)
        {
            var target = ReceiptFolder.FreeName(dir, "original-" + f.FileName, now);
            try
            {
                if (f.Origin == ReceiptOrigin.Inbox && f.SourcePath is { } src && File.Exists(src)) File.Move(src, target);
                else if (f.Origin == ReceiptOrigin.File && f.SourcePath is { } src2 && File.Exists(src2)) File.Copy(src2, target);
                else File.WriteAllBytes(target, f.Data);
            }
            catch (IOException)
            {
                File.WriteAllBytes(target, f.Data);
            }
            saved.Add(new SavedFile(f.FileName, f.MediaType, Path.GetFileName(target)));
        }

        var receipt = new SavedReceipt
        {
            Id = id,
            Source = source,
            Store = store,
            Date = date,
            Saved = now,
            Updated = now,
            Lines = lines.Select(l => Line(l, Renumber(l.Input, pictureOf, first))).ToList(),
            OnSheet = new(onSheet),
            Pictures = pictures,
            Files = saved,
        };
        Write(receipt);
        lock (_gate) _all.Add(receipt);
        _log?.Info($"Receipt saved for later: {receipt.Title} ({lines.Count} lines, {pictures.Count} pictures) in {dir}");
        Changed?.Invoke(this, EventArgs.Empty);
        return receipt;
    }

    /// <summary>A receipt finished on the Assistant tab: keep its lines (no pictures) for price questions.</summary>
    public SavedReceipt RecordFinished(string source, IReadOnlyList<ReceiptLine> lines, IReadOnlyDictionary<int, int?> onSheet,
        IReadOnlyList<ReceiptFile> files, DateTime now, string store = "", DateOnly? date = null)
    {
        var receipt = new SavedReceipt
        {
            Id = "done-" + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
            Source = source,
            Store = store,
            Date = date,
            Saved = now,
            Updated = now,
            Finished = now,
            Lines = lines.Select(l => Line(l, l.Input with { Picture = 0, Top = 0, Bottom = 0 })).ToList(),
            OnSheet = new(onSheet),
            Files = files.Select(f => new SavedFile(f.FileName, f.MediaType, "")).ToList(),
        };
        Write(receipt);
        lock (_gate) _all.Add(receipt);
        Changed?.Invoke(this, EventArgs.Empty);
        return receipt;
    }

    /// <summary>The owner worked on a saved receipt: keep its new lines and stock sheet marks.</summary>
    public SavedReceipt Update(string id, string source, IReadOnlyList<ReceiptLine> lines, IReadOnlyDictionary<int, int?> onSheet, DateTime now)
        => Change(id, r => r with
        {
            Source = source.Length > 0 ? source : r.Source,
            Updated = now,
            Lines = lines.Select(l => Line(l, l.Input)).ToList(),
            OnSheet = new(onSheet),
        });

    /// <summary>The owner's name for a saved receipt; empty goes back to the store and date.</summary>
    public SavedReceipt Rename(string id, string name, DateTime now) =>
        Change(id, r => r with { Name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)) is var n && n.Length > 80 ? n[..80] : n, Updated = now });

    private SavedReceipt Change(string id, Func<SavedReceipt, SavedReceipt> change)
    {
        SavedReceipt updated;
        lock (_gate)
        {
            var i = _all.FindIndex(r => r.Id == id);
            if (i < 0) throw new InvalidOperationException($"Saved receipt {id} isn't there any more.");
            updated = change(_all[i]);
            _all[i] = updated;
        }
        Write(updated);
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>
    /// Finished from the Pending tab: the original files go to Receipts\Done (when there is a Receipts folder), the
    /// pictures are deleted, and the lines are kept for price questions.
    /// </summary>
    public IReadOnlyList<string> Finish(string id, ReceiptFolder? folder, DateTime now)
    {
        var r = Get(id) ?? throw new InvalidOperationException($"Saved receipt {id} isn't there any more.");
        var dir = Path.Combine(PendingFolder, id);
        var filed = new List<string>();
        if (folder is not null)
            foreach (var f in r.Files.Where(f => f.StoredAs.Length > 0))
            {
                var path = Path.Combine(dir, f.StoredAs);
                if (!File.Exists(path)) continue;
                filed.Add(Path.GetFileName(folder.MarkHandled(new ReceiptFile(f.FileName, f.MediaType, [], ReceiptOrigin.File, path), now)));
            }
        var done = r with { Finished = now, Updated = now, Pictures = [], Files = r.Files.Select(f => f with { StoredAs = "" }).ToList() };
        Write(done);
        lock (_gate)
        {
            var i = _all.FindIndex(x => x.Id == id);
            if (i >= 0) _all[i] = done;
        }
        TryDelete(dir);
        Changed?.Invoke(this, EventArgs.Empty);
        return filed;
    }

    /// <summary>Thrown away by the owner: gone from the Pending tab, files and all.</summary>
    public void Discard(string id)
    {
        lock (_gate) _all.RemoveAll(r => r.Id == id);
        TryDelete(Path.Combine(PendingFolder, id));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string PicturePath(SavedReceipt r, SavedPicture p) => Path.Combine(PendingFolder, r.Id, p.File);
    public string? OriginalPath(SavedReceipt r, SavedFile f) =>
        f.StoredAs.Length == 0 ? null : Path.Combine(PendingFolder, r.Id, f.StoredAs);

    /// <summary>
    /// Lines of saved and finished receipts whose description, code or matched item has every word of
    /// <paramref name="text"/> (or a UPC equal to it), newest receipt first.
    /// </summary>
    public IReadOnlyList<(SavedReceipt Receipt, SavedLine Line)> Search(string text, int max)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A number (a scanned UPC or a vendor code): match it exactly, leading zeros aside.
        var number = text.Trim();
        bool isNumber = number.Length >= 4 && number.All(char.IsDigit);
        number = number.TrimStart('0');
        bool Hit(SavedLine l)
        {
            if (isNumber)
                return l.Upc?.ToString(CultureInfo.InvariantCulture) == number
                    || l.Input.Code?.Trim().TrimStart('0') == number || l.Input.Upc?.Trim().TrimStart('0') == number;
            var hay = $"{l.Input.Text} {l.Input.Code} {l.Item} {l.Upc}";
            return words.Length > 0 && words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        bool HitWith(SavedReceipt r, SavedLine l)
        {
            var hay = $"{l.Input.Text} {l.Input.Code} {l.Item} {l.Upc}";
            var inLine = words.Where(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
            return inLine.Count > 0 && words.Except(inLine).All(w => InTitle(r, w));
        }
        // Words naming the receipt ("costco lays") narrow it to that store's receipts.
        bool InTitle(SavedReceipt r, string w) => $"{r.Title} {r.Store} {r.Source}".Contains(w, StringComparison.OrdinalIgnoreCase);
        lock (_gate)
            return _all.OrderByDescending(r => r.Date?.ToDateTime(TimeOnly.MinValue) ?? r.Saved)
                .SelectMany(r => r.Lines.Where(l => Hit(l) || (!isNumber && HitWith(r, l))).Select(l => (r, l)))
                .Take(max)
                .ToList();
    }

    private static SavedLine Line(ReceiptLine l, ReceiptLineInput input) =>
        new(input, l.Item?.Upc, l.Item?.Description, l.Status, ReceiptQuestions.For(l) is not null);

    /// <summary>The picture number Claude gave → this receipt's own picture number (1 = its first picture); 0 when unknown.</summary>
    private static ReceiptLineInput Renumber(ReceiptLineInput input, Func<int, (int File, int Part)?> pictureOf, IReadOnlyList<int> first)
    {
        if (!input.HasPosition || pictureOf(input.Picture) is not { } at || at.File >= first.Count)
            return input with { Picture = 0, Top = 0, Bottom = 0 };
        return input with { Picture = first[at.File] + at.Part };
    }

    private void Load()
    {
        foreach (var path in Enumerate())
        {
            try
            {
                if (JsonSerializer.Deserialize<SavedReceipt>(File.ReadAllText(path), Json) is { } r) _all.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                _log?.Warn($"Couldn't read saved receipt {path}: {ex.Message}");
            }
        }
    }

    private IEnumerable<string> Enumerate()
    {
        if (Directory.Exists(PendingFolder))
            foreach (var d in Directory.EnumerateDirectories(PendingFolder))
                if (File.Exists(Path.Combine(d, "receipt.json"))) yield return Path.Combine(d, "receipt.json");
        if (Directory.Exists(DoneFolder))
            foreach (var f in Directory.EnumerateFiles(DoneFolder, "*.json")) yield return f;
    }

    private void Write(SavedReceipt r)
    {
        var path = r.Finished is null
            ? Path.Combine(PendingFolder, r.Id, "receipt.json")
            : Path.Combine(DoneFolder, r.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.Warn($"Couldn't delete {dir}: {ex.Message}"); }
    }

    private static string Ext(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg",
    };
}
