namespace EstreamAssistant.Core.Receipts;

/// <summary>One strip of a long receipt, in the source picture's pixels.</summary>
public readonly record struct StripRect(int Y, int Height);

/// <summary>
/// How to cut a long receipt into strips: crop to the printed area (X, Width), scale by Scale, then cut each strip.
/// Overlaps is true when some cut had no blank row to land on, so those strips share a band of the receipt.
/// </summary>
public sealed record StripPlan(int X, int Width, double Scale, IReadOnlyList<StripRect> Strips, bool Overlaps);

/// <summary>
/// Long receipts (a till roll scanned at 1275 × 13974) can't go to Claude as one picture: the API shrinks every
/// picture to fit about 2576 px on its long edge, which leaves a till roll ~230 px wide and unreadable.
/// This works out where to cut it instead, from the picture's grey levels: trim the blank side margins, then cut
/// at blank rows between printed lines, so every strip is close to full resolution and no line is cut in half.
/// </summary>
public static class ReceiptStrips
{
    /// <summary>Long edge of each strip. Current models read up to 2576 px without shrinking; stay under it.</summary>
    public const int MaxEdge = 2400;
    /// <summary>Pixels per strip (current models: 3.75 MP before shrinking).</summary>
    public const long MaxPixels = 3_500_000;
    /// <summary>A receipt this long is shrunk rather than sent as more strips.</summary>
    public const int MaxStrips = 16;

    /// <summary>
    /// The cut plan for a picture (8-bit grey, <paramref name="stride"/> bytes a row), or null when it isn't a long
    /// receipt (no taller than twice its printed width) and can be sent whole.
    /// </summary>
    public static StripPlan? Plan(ReadOnlySpan<byte> gray, int width, int height, int stride,
        int maxEdge = MaxEdge, long maxPixels = MaxPixels)
    {
        if (width <= 0 || height <= 0 || gray.Length < (long)stride * (height - 1) + width) return null;
        var threshold = InkThreshold(gray, width, height, stride);

        // Columns with print in them: trim the blank (or scanner-lid grey) margins left and right.
        var colInk = new int[width];
        for (int y = 0; y < height; y++)
        {
            var row = gray.Slice(y * stride, width);
            for (int x = 0; x < width; x++) if (row[x] < threshold) colInk[x]++;
        }
        int minCol = Math.Max(2, height / 400);
        int x0 = Array.FindIndex(colInk, c => c > minCol), x1 = Array.FindLastIndex(colInk, c => c > minCol);
        if (x0 < 0) { x0 = 0; x1 = width - 1; }
        int margin = Math.Max(16, width / 40);
        x0 = Math.Max(0, x0 - margin);
        x1 = Math.Min(width, x1 + margin + 1);
        int w = x1 - x0;

        var rowInk = new int[height];
        for (int y = 0; y < height; y++)
        {
            var row = gray.Slice(y * stride + x0, w);
            int n = 0;
            foreach (var p in row) if (p < threshold) n++;
            rowInk[y] = n;
        }
        int y0 = Array.FindIndex(rowInk, r => r > 1), y1 = Array.FindLastIndex(rowInk, r => r > 1);
        if (y0 < 0) return null;
        y0 = Math.Max(0, y0 - margin);
        y1 = Math.Min(height, y1 + margin + 1);
        int h = y1 - y0;
        if (h <= 2 * w) return null;

        double scale = Math.Min(1.0, maxEdge / (double)w);
        int stripMax = StripHeight(w, scale, maxEdge, maxPixels);
        if ((h + stripMax - 1) / stripMax > MaxStrips - 1)
        {
            // Very long: make the strips longer and shrink each one to fit. Plan one strip fewer than the most
            // allowed, because cuts land on blank rows a little before the aim.
            int longer = (h + MaxStrips - 2) / (MaxStrips - 1);
            scale = Math.Min(scale, Math.Min(maxEdge / (double)longer, Math.Sqrt(maxPixels / ((double)w * longer))));
            stripMax = Math.Max(longer, StripHeight(w, scale, maxEdge, maxPixels));
        }

        // A row counts as blank with a few specks of scanner dust in it.
        int blank = Math.Max(1, w / 300);
        int overlap = Math.Max(1, stripMax / 12);
        var strips = new List<StripRect>();
        bool overlaps = false;
        int top = y0;
        while (y1 - top > stripMax)
        {
            // Aim for equal strips; look for the lightest row near the aim, never past the longest strip allowed.
            int remaining = y1 - top, left = (remaining + stripMax - 1) / stripMax;
            int aim = top + remaining / left;
            int lo = Math.Max(top + stripMax / 2, aim - stripMax / 6);
            int hi = Math.Min(top + stripMax, aim + stripMax / 6);
            int cut = aim, best = int.MaxValue;
            for (int y = lo; y < hi; y++)
            {
                if (rowInk[y] < best || (rowInk[y] == best && Math.Abs(y - aim) < Math.Abs(cut - aim)))
                {
                    best = rowInk[y];
                    cut = y;
                }
            }
            if (best <= blank)
            {
                // Cut in the middle of the gap, clear of the faint tails of the letters on either side.
                int a = cut, b = cut;
                while (a > lo && rowInk[a - 1] <= blank) a--;
                while (b < hi - 1 && rowInk[b + 1] <= blank) b++;
                cut = (a + b + 1) / 2;
            }
            strips.Add(new StripRect(top, cut - top));
            if (best <= blank) top = cut;
            else
            {
                // No gap between lines here (a picture or a smudge): repeat a band so no line is lost in the cut.
                top = cut - overlap;
                overlaps = true;
            }
        }
        strips.Add(new StripRect(top, y1 - top));
        return new StripPlan(x0, w, scale, strips, overlaps);
    }

    private static int StripHeight(int w, double scale, int maxEdge, long maxPixels) =>
        (int)(Math.Min(maxEdge, maxPixels / (w * scale)) / scale);

    /// <summary>Darker than this is print: a bit over half the paper's brightness (the median grey).</summary>
    private static byte InkThreshold(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        var hist = new long[256];
        int step = Math.Max(1, height / 2000);
        long count = 0;
        for (int y = 0; y < height; y += step)
        {
            foreach (var p in gray.Slice(y * stride, width)) hist[p]++;
            count += width;
        }
        long half = count / 2, seen = 0;
        int median = 255;
        for (int v = 0; v < 256; v++)
        {
            seen += hist[v];
            if (seen >= half) { median = v; break; }
        }
        return (byte)Math.Clamp(median * 55 / 100, 40, 140);
    }
}
