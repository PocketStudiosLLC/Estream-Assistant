using System.Globalization;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Receipts;

public enum ReceiptKind { Image, Pdf }

/// <summary>Where a receipt came from. Decides what "handled" does with the file (F5.8).</summary>
public enum ReceiptOrigin
{
    /// <summary>Found in the watched Receipts folder: moved to Receipts\Done when handled.</summary>
    Inbox,
    /// <summary>Dragged in or picked from somewhere else: the original stays put, a copy goes to Done.</summary>
    File,
    /// <summary>Pasted from the clipboard: written to Done.</summary>
    Paste,
}

/// <summary>One picture sent to Claude for a receipt: the whole photo, or one strip or page of it.</summary>
/// <param name="Label">How the picture is labelled for Claude, e.g. "strip 2 of 6"; empty for a single picture.</param>
public sealed record ReceiptPart(byte[] Data, string MediaType, string Label = "");

/// <summary>
/// One receipt or invoice ready to send to Claude: an image (already downscaled) or a PDF.
/// <see cref="Data"/> is the file itself (shown as the thumbnail, and saved for pasted pictures). What gets sent is
/// <see cref="Parts"/> when the app cut the receipt into strips or took the scans out of a PDF, otherwise Data.
/// </summary>
public sealed record ReceiptFile(string FileName, string MediaType, byte[] Data, ReceiptOrigin Origin, string? SourcePath)
{
    public ReceiptKind Kind => MediaType == ReceiptIngest.PdfType ? ReceiptKind.Pdf : ReceiptKind.Image;

    /// <summary>The pictures sent instead of Data (empty: send Data as it is).</summary>
    public IReadOnlyList<ReceiptPart> Parts { get; init; } = [];

    /// <summary>Told to Claude with the receipt when it is sent in parts (how they fit together).</summary>
    public string Note { get; init; } = "";

    /// <summary>Bytes that go to Claude.</summary>
    public long SentBytes => Parts.Count > 0 ? Parts.Sum(p => (long)p.Data.Length) : Data.Length;

    public string SizeText => SentBytes >= 1024 * 1024
        ? (SentBytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (SentBytes / 1024) + " KB";
}

public sealed class ReceiptException(string friendlyMessage) : Exception(friendlyMessage);

/// <summary>Downscales and cuts up photos. Implemented in the WPF app (Windows imaging); a fake in tests.</summary>
public interface IImageShrinker
{
    /// <summary>
    /// Returns the image with its long edge at most <paramref name="maxEdge"/> px, re-encoded
    /// (JPEG for photos), or null if it is already small enough and can be sent as it is.
    /// Throws if the bytes aren't a readable image.
    /// </summary>
    (byte[] Data, string MediaType)? Shrink(byte[] data, string mediaType, int maxEdge);

    /// <summary>
    /// A long receipt cut into strips Claude can read (see <see cref="ReceiptStrips"/>), or null when the picture
    /// isn't long and can be sent whole. Throws if the bytes aren't a readable image.
    /// </summary>
    StripResult? Strips(byte[] data, string mediaType);
}

/// <summary>The strips of one long picture, top to bottom.</summary>
public sealed record StripResult(IReadOnlyList<(byte[] Data, string MediaType)> Strips, bool Overlaps);

/// <summary>
/// Drag/drop, paste and folder input for receipts (spec F5.1, F5.2).
/// Accepts JPEG, PNG, WebP, GIF and PDF, checked by their first bytes as well as the name.
/// </summary>
public sealed class ReceiptIngest(IImageShrinker shrinker)
{
    public const string PdfType = "application/pdf";
    /// <summary>F5.2: long edge for photos sent to Claude.</summary>
    public const int MaxEdgePx = 1600;
    /// <summary>The API limit is 5 MB per image; stay under it after shrinking.</summary>
    public const int MaxImageBytes = 4_500_000;
    /// <summary>The whole request must stay under 32 MB; base64 adds a third.</summary>
    public const int MaxPdfBytes = 20_000_000;
    /// <summary>Anything bigger isn't a receipt photo; don't even read it.</summary>
    public const long MaxInputBytes = 60_000_000;

    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".pdf"];

    public static bool HasAcceptedExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The media type from the file's first bytes, or null if it isn't an accepted type.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return PdfType;
        return null;
    }

    public async Task<ReceiptFile> LoadFileAsync(string path, ReceiptOrigin origin, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        if (!HasAcceptedExtension(path))
            throw new ReceiptException($"{name}: only photos (JPEG, PNG, WebP, GIF) and PDFs can be read.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new ReceiptException($"{name} isn't there any more.");
        if (info.Length == 0) throw new ReceiptException($"{name} is empty.");
        if (info.Length > MaxInputBytes) throw new ReceiptException($"{name} is too big ({info.Length / 1024 / 1024} MB).");
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return FromBytes(name, bytes, origin, path);
    }

    /// <summary>A pasted image (PNG from the clipboard) or any other bytes.</summary>
    public ReceiptFile FromBytes(string fileName, byte[] bytes, ReceiptOrigin origin, string? sourcePath = null)
    {
        var type = Sniff(bytes) ?? throw new ReceiptException($"{fileName} isn't a photo or PDF that can be read.");
        if (type == PdfType)
        {
            if (bytes.Length > MaxPdfBytes)
                throw new ReceiptException($"{fileName} is too big to send ({bytes.Length / 1024 / 1024} MB). Save fewer pages, or take photos.");
            var file = new ReceiptFile(fileName, type, bytes, origin, sourcePath);
            return ScannedPdf.Pages(bytes) is { } scans ? FromScans(file, scans) : file;
        }

        (byte[] Data, string MediaType)? shrunk;
        StripResult? strips;
        try
        {
            shrunk = shrinker.Shrink(bytes, type, MaxEdgePx);
            strips = shrinker.Strips(bytes, type);
        }
        catch (Exception ex) when (ex is not ReceiptException)
        {
            throw new ReceiptException($"{fileName} couldn't be opened as a picture.");
        }
        var (data, mediaType) = shrunk ?? (bytes, type);
        if (data.Length > MaxImageBytes)
            throw new ReceiptException($"{fileName} is still too big after shrinking. Try a smaller photo.");
        var photo = new ReceiptFile(fileName, mediaType, data, origin, sourcePath);
        if (strips is null || strips.Strips.Count < 2 || strips.Strips.Any(p => p.Data.Length > MaxImageBytes)) return photo;
        var parts = Label(strips.Strips.Select(s => (s, 0)).ToList(), 1);
        return photo with { Parts = parts, Note = StripNote(parts.Count, 1, strips.Overlaps) };
    }

    /// <summary>
    /// A scanner's PDF: send its scans as pictures (long ones in strips), so Claude reads them at full size instead
    /// of a whole till roll squeezed onto one page. If a scan can't be decoded, the PDF goes as it is.
    /// </summary>
    private ReceiptFile FromScans(ReceiptFile pdf, IReadOnlyList<byte[]> scans)
    {
        var pictures = new List<((byte[] Data, string MediaType) Picture, int Page)>();
        bool overlaps = false;
        try
        {
            for (int i = 0; i < scans.Count; i++)
            {
                if (shrinker.Strips(scans[i], "image/jpeg") is { } s)
                {
                    overlaps |= s.Overlaps;
                    pictures.AddRange(s.Strips.Select(p => (p, i + 1)));
                }
                else pictures.Add((shrinker.Shrink(scans[i], "image/jpeg", ReceiptStrips.MaxEdge) ?? (scans[i], "image/jpeg"), i + 1));
            }
        }
        catch (Exception)
        {
            return pdf;
        }
        if (pictures.Any(p => p.Picture.Data.Length > MaxImageBytes)) return pdf;
        var parts = Label(pictures, scans.Count);
        bool cut = pictures.Count > scans.Count;
        return pdf with { Parts = parts, Note = cut ? StripNote(parts.Count, scans.Count, overlaps) : "" };
    }

    /// <summary>"strip 2 of 6", "page 2 of 3", or "page 2 of 3, strip 1 of 4".</summary>
    private static List<ReceiptPart> Label(IReadOnlyList<((byte[] Data, string MediaType) Picture, int Page)> pictures, int pages)
    {
        var parts = new List<ReceiptPart>();
        for (int i = 0; i < pictures.Count; i++)
        {
            var (pic, page) = pictures[i];
            int inPage = pictures.Count(p => p.Page == page);
            int nth = pictures.Take(i + 1).Count(p => p.Page == page);
            var pagePart = pages > 1 ? $"page {page} of {pages}" : "";
            var stripPart = inPage > 1 ? $"strip {nth} of {inPage}" : "";
            parts.Add(new ReceiptPart(pic.Data, pic.MediaType, string.Join(", ", new[] { pagePart, stripPart }.Where(s => s.Length > 0))));
        }
        return parts;
    }

    private static string StripNote(int parts, int pages, bool overlaps) =>
        $"This is a long receipt{(pages > 1 ? $" ({pages} pages)" : "")}, so the app cut it into {parts} pictures, in order from the top. " +
        "Each strip carries straight on from the one before: an item whose lines (code, description, quantity, price) run across a cut is ONE item. " +
        "Number the lines through the whole receipt." +
        (overlaps ? " Some cuts overlap, so a line at the bottom of one strip can show again at the top of the next: count it once." : "");
}

/// <summary>
/// The engine's Receipts folder (F5.1, F5.8): new files dropped there are reported, and
/// handled receipts end up in Receipts\Done. Only this folder's files are ever moved.
/// </summary>
public sealed class ReceiptFolder : IDisposable
{
    private readonly AppLog? _log;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public ReceiptFolder(string inbox, AppLog? log = null)
    {
        Inbox = inbox;
        Done = Path.Combine(inbox, "Done");
        _log = log;
    }

    public string Inbox { get; }
    public string Done { get; }

    /// <summary>A new receipt file in the folder, once it has finished being written. Raised off the UI thread.</summary>
    public event EventHandler<string>? FileArrived;

    /// <summary>Wait for a copy to finish before reading (scanner apps write in pieces).</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Receipts already waiting in the folder (oldest first).</summary>
    public IReadOnlyList<string> Waiting()
    {
        if (!Directory.Exists(Inbox)) return [];
        return Directory.EnumerateFiles(Inbox)
            .Where(ReceiptIngest.HasAcceptedExtension)
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Starts watching. Returns false if the folder doesn't exist (e.g. on the dev PC).</summary>
    public bool Start()
    {
        if (_watcher is not null) return true;
        if (!Directory.Exists(Inbox)) return false;
        lock (_seen) foreach (var f in Waiting()) _seen.Add(f);
        _watcher = new FileSystemWatcher(Inbox) { IncludeSubdirectories = false, EnableRaisingEvents = false };
        _watcher.Created += (_, e) => _ = SettleAsync(e.FullPath);
        _watcher.Renamed += (_, e) => _ = SettleAsync(e.FullPath);
        _watcher.EnableRaisingEvents = true;
        return true;
    }

    private async Task SettleAsync(string path)
    {
        if (!ReceiptIngest.HasAcceptedExtension(path)) return;
        lock (_seen) if (!_seen.Add(path)) return;
        try
        {
            long last = -1;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(SettleTime).ConfigureAwait(false);
                if (!File.Exists(path)) return;
                var len = new FileInfo(path).Length;
                if (len > 0 && len == last && CanOpen(path)) break;
                last = len;
            }
            FileArrived?.Invoke(this, path);
        }
        catch (Exception ex)
        {
            _log?.Error($"Receipt watcher: {path}", ex);
        }
    }

    private static bool CanOpen(string path)
    {
        try { using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// F5.8: puts a handled receipt in Receipts\Done, with a time suffix if that name is taken.
    /// Inbox files are moved; files from elsewhere stay where they are and are copied;
    /// pasted pictures are written. Returns the new path.
    /// </summary>
    public string MarkHandled(ReceiptFile file, DateTime now)
    {
        Directory.CreateDirectory(Done);
        var target = FreeName(Done, file.FileName, now);
        switch (file.Origin)
        {
            case ReceiptOrigin.Inbox when file.SourcePath is { } src && File.Exists(src)
                                          && string.Equals(Path.GetDirectoryName(Path.GetFullPath(src)), Path.GetFullPath(Inbox).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase):
                File.Move(src, target);
                break;
            case ReceiptOrigin.File when file.SourcePath is { } src2 && File.Exists(src2):
                File.Copy(src2, target);
                break;
            default:
                File.WriteAllBytes(target, file.Data);
                break;
        }
        _log?.Info($"Receipt handled: {file.FileName} -> {target}");
        return target;
    }

    internal static string FreeName(string folder, string fileName, DateTime now)
    {
        var safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0) safe = "receipt";
        var path = Path.Combine(folder, safe);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        path = Path.Combine(folder, $"{stem}_{stamp}{ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}_{stamp}_{n}{ext}");
        return path;
    }

    public void Dispose() => _watcher?.Dispose();
}
