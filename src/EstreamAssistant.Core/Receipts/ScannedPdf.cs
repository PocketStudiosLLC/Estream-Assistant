using System.Text;
using System.Text.RegularExpressions;

namespace EstreamAssistant.Core.Receipts;

/// <summary>
/// A scanner's PDF is just one photo per page with no text in it: a JPEG, or a black-and-white fax-style (CCITT)
/// image, which comes out as a TIFF the Windows decoder reads (Costco receipts from the store's scanner). Claude reads PDFs by turning each page into
/// a picture at its own size, so a long till-roll page comes out too small to read. For those PDFs the app takes the
/// scans out and sends them as pictures (cut into strips when long) instead.
/// Anything else (a PDF with text, rotated pages, other image formats) is left as a PDF.
/// </summary>
public static partial class ScannedPdf
{
    /// <summary>The JPEG or TIFF (see <see cref="IsTiff"/>) of each page, in file order, or null if this isn't a plain scanned PDF.</summary>
    public static IReadOnlyList<byte[]>? Pages(byte[] pdf)
    {
        // Latin-1 keeps one char per byte, so string positions are byte positions.
        var text = Encoding.Latin1.GetString(pdf);
        var images = new List<byte[]>();
        int pages = 0;
        int pos = 0;
        while (true)
        {
            var m = ObjStart().Match(text, pos);
            if (!m.Success) break;
            int dictStart = m.Index + m.Length;
            int end = text.IndexOf("endobj", dictStart, StringComparison.Ordinal);
            int streamAt = StreamKeyword().Match(text, dictStart).Index;
            bool hasStream = streamAt > 0 && (end < 0 || streamAt < end);
            var dict = text[dictStart..(hasStream ? streamAt : end < 0 ? text.Length : end)];

            // Text, compressed object lists (which could hide fonts or pages) and turned pages: leave as a PDF.
            if (dict.Contains("/Font", StringComparison.Ordinal) || ObjStmType().IsMatch(dict)) return null;
            if (PageType().IsMatch(dict))
            {
                pages++;
                if (RotateNonZero().IsMatch(dict)) return null;
            }

            if (!hasStream)
            {
                pos = end < 0 ? text.Length : end + 6;
                continue;
            }

            int data = streamAt + "stream".Length;
            if (data < text.Length && text[data] == '\r') data++;
            if (data < text.Length && text[data] == '\n') data++;
            int dataEnd = StreamEnd(text, dict, data);
            if (dataEnd < 0) return null;

            if (ImageSubtype().IsMatch(dict))
            {
                if (CcittOnly().IsMatch(dict))
                {
                    if (FaxTiff(dict, pdf.AsSpan(data, dataEnd - data)) is not { } tiff) return null;
                    images.Add(tiff);
                }
                else
                {
                    if (!DctOnly().IsMatch(dict)) return null;
                    var jpeg = pdf.AsSpan(data, dataEnd - data).TrimEnd("\r\n "u8);
                    if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
                    images.Add(jpeg.ToArray());
                }
            }
            else if (ContentWithText(pdf, dict, data, dataEnd))
            {
                return null;
            }
            pos = dataEnd;
        }
        // One scan per page, or it's something else (a logo on a typed page, a page in tiles).
        return pages > 0 && images.Count == pages ? images : null;
    }

    /// <summary>A page that <see cref="Pages"/> turned into a TIFF (a fax-style scan), not a JPEG.</summary>
    public static bool IsTiff(byte[] page) => page.Length > 4 && page[0] == (byte)'I' && page[1] == (byte)'I' && page[2] == 42 && page[3] == 0;

    /// <summary>
    /// A CCITT fax image wrapped as a one-strip TIFF (the same compressed bytes, no decoding here), or null for
    /// settings a TIFF can't say the same way (byte-aligned rows, missing size).
    /// </summary>
    internal static byte[]? FaxTiff(string dict, ReadOnlySpan<byte> data)
    {
        int Int(string name, int fallback)
        {
            var m = Regex.Match(dict, $@"/{name}\s+(-?\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        }
        bool True(string name) => Regex.IsMatch(dict, $@"/{name}\s+true\b");
        if (True("EncodedByteAlign") || !Regex.IsMatch(dict, @"/BitsPerComponent\s+1\b")) return null;
        int width = Int("Columns", Int("Width", 0)), height = Int("Rows", Int("Height", 0)), k = Int("K", 0);
        if (width <= 0 || height <= 0 || data.Length == 0) return null;
        // Fax black runs show as black (TIFF WhiteIsZero), as PDF viewers show these scans whatever /BlackIs1 says
        // (the store's Costco scans say BlackIs1 true with /Decode [0 1] and read black on white). Only an explicit
        // /Decode [1 0] turns them the other way.
        bool decodeFlipped = Regex.IsMatch(dict, @"/Decode\s*\[\s*1(\.0*)?\s+0(\.0*)?\s*\]");
        ushort photometric = (ushort)(decodeFlipped ? 1 : 0);
        ushort compression = (ushort)(k < 0 ? 4 : 3);

        var tags = new List<(ushort Tag, ushort Type, uint Value)>
        {
            (256, 4, (uint)width), (257, 4, (uint)height), (258, 3, 1), (259, 3, compression), (262, 3, photometric),
            (273, 4, 0 /* data offset, set below */), (277, 3, 1), (278, 4, (uint)height), (279, 4, (uint)data.Length),
        };
        if (compression == 3) tags.Add((292, 4, k > 0 ? 1u : 0u)); // T4Options: 2-D coding when K > 0
        int ifdSize = 2 + tags.Count * 12 + 4;
        uint dataOffset = (uint)(8 + ifdSize);
        var tiff = new byte[dataOffset + data.Length];
        "II*\0"u8.CopyTo(tiff);
        BitConverter.TryWriteBytes(tiff.AsSpan(4), 8u);
        BitConverter.TryWriteBytes(tiff.AsSpan(8), (ushort)tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            var (tag, type, value) = tags[i];
            var at = tiff.AsSpan(10 + i * 12);
            BitConverter.TryWriteBytes(at, tag);
            BitConverter.TryWriteBytes(at[2..], type);
            BitConverter.TryWriteBytes(at[4..], 1u);
            if (type == 3) BitConverter.TryWriteBytes(at[8..], (ushort)value);
            else BitConverter.TryWriteBytes(at[8..], tag == 273 ? dataOffset : value);
        }
        data.CopyTo(tiff.AsSpan((int)dataOffset));
        return tiff;
    }

    /// <summary>Where the stream's bytes end: its /Length when that checks out, else the endstream keyword.</summary>
    private static int StreamEnd(string text, string dict, int data)
    {
        var len = Length().Match(dict);
        if (len.Success)
        {
            int n;
            if (len.Groups[2].Success)
            {
                var target = new Regex($@"(?<![0-9]){len.Groups[1].Value}\s+{len.Groups[2].Value}\s+obj\s*(\d+)\s*endobj").Match(text);
                n = target.Success ? int.Parse(target.Groups[1].Value) : -1;
            }
            else n = int.Parse(len.Groups[1].Value);
            if (n >= 0 && data + n <= text.Length && text.IndexOf("endstream", data + n, Math.Min(32, text.Length - data - n), StringComparison.Ordinal) >= 0)
                return data + n;
        }
        int e = text.IndexOf("endstream", data, StringComparison.Ordinal);
        return e;
    }

    /// <summary>An uncompressed page content stream that draws text (BT ... ET). Compressed ones were caught by /Font.</summary>
    private static bool ContentWithText(byte[] pdf, string dict, int data, int dataEnd) =>
        !dict.Contains("/Filter", StringComparison.Ordinal)
        && TextOp().IsMatch(Encoding.Latin1.GetString(pdf, data, dataEnd - data));

    [GeneratedRegex(@"(?<![0-9])\d+\s+\d+\s+obj\b")] private static partial Regex ObjStart();
    [GeneratedRegex(@"(?<![a-z])stream\r?\n")] private static partial Regex StreamKeyword();
    [GeneratedRegex(@"/Type\s*/ObjStm\b")] private static partial Regex ObjStmType();
    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")] private static partial Regex PageType();
    [GeneratedRegex(@"/Rotate\s+-?[1-9]")] private static partial Regex RotateNonZero();
    [GeneratedRegex(@"/Subtype\s*/Image\b")] private static partial Regex ImageSubtype();
    [GeneratedRegex(@"/Filter\s*(/DCTDecode\b|\[\s*/DCTDecode\s*\])")] private static partial Regex DctOnly();
    [GeneratedRegex(@"/Filter\s*(/CCITTFaxDecode\b|\[\s*/CCITTFaxDecode\s*\])")] private static partial Regex CcittOnly();
    [GeneratedRegex(@"/Length\s+(\d+)(?:\s+(\d+)\s+R)?")] private static partial Regex Length();
    [GeneratedRegex(@"\bBT\b[\s\S]*\bTj|\bBT\b[\s\S]*\bTJ")] private static partial Regex TextOp();
}
