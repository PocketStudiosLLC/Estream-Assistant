using System.Globalization;
using System.Text;

namespace EstreamAssistant.Core.Receipts;

/// <summary>A line of text the PC's own text reader (OCR) found on a receipt picture: where it is, in percent of the picture's height.</summary>
public sealed record OcrLine(int Picture, string Text, double Top, double Bottom);

/// <summary>Where a receipt row is on its picture (1-based picture, percent of its height).</summary>
public readonly record struct LinePlace(int Picture, double Top, double Bottom);

/// <summary>
/// Finds receipt rows on the receipt's pictures from the PC's text reader, for rows Claude didn't place (receipts
/// sent as a PDF, like the store scanner's Costco receipts before 0.2.7), so the Pending tab can still highlight
/// the row it's asking about. Rows are matched in receipt order, by printed code, words and price, so a row scanned
/// three times lands on its own three lines. Nothing here uses Claude.
/// </summary>
public static class ReceiptLocator
{
    /// <summary>How far ahead (in text lines) the next row is looked for before searching the whole receipt.</summary>
    private const int Window = 30;
    private const double Enough = 1.5;

    public static IReadOnlyDictionary<int, LinePlace> Locate(IEnumerable<ReceiptLineInput> rows, IReadOnlyList<OcrLine> ocr)
    {
        var lines = ocr.Where(o => o.Bottom > o.Top).OrderBy(o => o.Picture).ThenBy(o => o.Top)
            .Select(o => (o, Tokens: Tokens(o.Text))).Where(x => x.Tokens.Count > 0).ToList();
        var found = new Dictionary<int, LinePlace>();
        int cursor = 0;
        foreach (var input in rows.OrderBy(r => r.Line))
        {
            var number = input.Line;
            var code = Bare(input.Code);
            var words = Tokens(input.Text).Where(w => w.Length >= 2 && !w.All(char.IsDigit)).Distinct().ToList();
            var prices = new[] { input.LineTotal, input.CasePrice, input.UnitPrice }.OfType<decimal>()
                .Select(p => Digits(p.ToString("0.00", CultureInfo.InvariantCulture))).Where(p => p.Length >= 3).ToList();
            if (code is null && words.Count == 0) continue;

            double Score(List<string> t)
            {
                double s = 0;
                if (code is not null && t.Any(x => Bare(x) == code)) s += 2;
                if (words.Count > 0) s += 2.0 * words.Count(w => t.Any(x => Like(w, x))) / words.Count;
                if (prices.Count > 0 && t.Any(x => prices.Contains(Digits(x)))) s += 0.5;
                return s;
            }

            // The earliest line nearly as good as the best one ahead: a misread price ("4.9") mustn't send the first
            // of two same rows to the second one's line.
            int Pick(int from, int to)
            {
                var scores = Enumerable.Range(from, Math.Max(0, to - from)).Select(i => (i, s: Score(lines[i].Tokens))).ToList();
                if (scores.Count == 0) return -1;
                var top = scores.Max(x => x.s);
                if (top < Enough) return -1;
                return scores.First(x => x.s >= Math.Max(Enough, top - 0.5 - 0.01)).i;
            }
            var best = Pick(cursor, Math.Min(lines.Count, cursor + Window));
            if (best < 0) best = Pick(0, lines.Count);
            if (best < 0) continue;
            var o = lines[best].o;
            found[number] = new LinePlace(o.Picture, o.Top, o.Bottom);
            cursor = best + 1;
        }
        return found;
    }

    /// <summary>Upper-case words of letters and digits ("GR MAN/ORNGE 9.99" → GR, MAN, ORNGE, 9.99).</summary>
    internal static List<string> Tokens(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in text.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c) || (c == '.' && sb.Length > 0)) sb.Append(c);
            else Flush();
        }
        Flush();
        return list;

        void Flush()
        {
            var w = sb.ToString().Trim('.');
            if (w.Length > 0) list.Add(w);
            sb.Clear();
        }
    }

    /// <summary>A printed code as letters and digits without leading zeros ("0000397895" → "397895"), or null if too short.</summary>
    private static string? Bare(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var d = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).TrimStart('0');
        return d.Length >= 3 ? d : null;
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>The same word allowing one misread letter in longer words (OCR reads "0G" for "OG").</summary>
    private static bool Like(string want, string got)
    {
        if (want == got) return true;
        if (want.Length < 4 || Math.Abs(want.Length - got.Length) > 1) return false;
        return Distance(want, got) <= 1;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
