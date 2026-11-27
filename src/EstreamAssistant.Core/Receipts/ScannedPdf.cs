using System.Text;
using System.Text.RegularExpressions;

namespace EstreamAssistant.Core.Receipts;

/// <summary>
/// A scanner's PDF is just one JPEG photo per page with no text in it. Claude reads PDFs by turning each page into
/// a picture at its own size, so a long till-roll page comes out too small to read. For those PDFs the app takes the
/// scans out and sends them as pictures (cut into strips when long) instead.
/// Anything else (a PDF with text, rotated pages, other image formats) is left as a PDF.
/// </summary>
public static partial class ScannedPdf
{
    /// <summary>The JPEG of each page, in file order, or null if this isn't a plain scanned PDF.</summary>
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
                if (!DctOnly().IsMatch(dict)) return null;
                var jpeg = pdf.AsSpan(data, dataEnd - data).TrimEnd("\r\n "u8);
                if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
                images.Add(jpeg.ToArray());
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
    [GeneratedRegex(@"/Length\s+(\d+)(?:\s+(\d+)\s+R)?")] private static partial Regex Length();
    [GeneratedRegex(@"\bBT\b[\s\S]*\bTj|\bBT\b[\s\S]*\bTJ")] private static partial Regex TextOp();
}
