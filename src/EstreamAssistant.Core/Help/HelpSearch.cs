using System.Text;

namespace EstreamAssistant.Core.Help;

/// <summary>
/// The search in the "?" help window: finds the explanation boxes that match what was typed ("stock sheet",
/// "check iventory counts"), forgiving a slip of a letter, word endings and the store's other words for things
/// (inventory = stock, invoice = receipt).
/// </summary>
public static class HelpSearch
{
    private static readonly HashSet<string> Filler =
    [
        "A", "AN", "THE", "TO", "OF", "IN", "ON", "FOR", "AND", "OR", "IS", "IT", "MY", "I", "HOW", "DO", "DOES", "CAN", "WHAT",
        "WHERE", "WHY", "ME", "WE", "OUR", "THIS", "THAT", "WITH", "AT", "BE", "SOME", "USE", "USING",
    ];

    /// <summary>Other words the store uses for the same thing (each word also matches itself).</summary>
    private static readonly string[][] SameMeaning =
    [
        ["STOCK", "INVENTORY", "ONHAND", "HAND", "QUANTITY", "QTY"],
        ["COUNT", "COUNTS", "COUNTED", "COUNTING", "TALLY"],
        ["RECEIPT", "INVOICE", "BILL"],
        ["PRICE", "PRICES", "PRICING", "RETAIL", "SELL"],
        ["COST", "COSTS", "PAID", "PAY"],
        ["DELETE", "REMOVE", "THROW", "DISCARD"],
        ["SAVE", "SAVED", "LATER", "PENDING"],
        ["SEARCH", "FIND", "LOOKUP", "LOOK"],
        ["BARCODE", "UPC", "SCAN", "SCANNER"],
        ["POST", "POSTED", "POSTING"],
        ["PROBLEM", "PROBLEMS", "BUG", "BUGS", "ERROR", "BROKEN"],
        ["REPORT", "REPORTS", "LIST"],
    ];

    /// <summary>
    /// The items that match, best first: each meaningful word typed counts once if it's in the title, examples or
    /// text (titles count more). Items well behind the best match are left out.
    /// </summary>
    public static IReadOnlyList<T> Find<T>(IEnumerable<T> items, string query, Func<T, string> title, Func<T, string> text)
    {
        var wanted = Words(query).Where(w => !Filler.Contains(w)).Distinct().ToList();
        if (wanted.Count == 0) return [];
        var scored = items.Select(item =>
        {
            var inTitle = Words(title(item)).ToHashSet();
            var inText = Words(text(item)).ToHashSet();
            double score = 0;
            foreach (var w in wanted)
            {
                if (inTitle.Any(x => Matches(w, x))) score += 2;
                else if (inText.Any(x => Matches(w, x))) score += 1;
            }
            // The words typed side by side in the title too ("stock sheet") put it first.
            if (wanted.Count > 1 && Normal(title(item)).Contains(string.Join(' ', wanted), StringComparison.Ordinal)) score += 2;
            return (item, score);
        }).Where(x => x.score > 0).ToList();
        if (scored.Count == 0) return [];
        var best = scored.Max(x => x.score);
        return scored.Where(x => x.score >= best / 2).OrderByDescending(x => x.score).Select(x => x.item).ToList();
    }

    internal static IEnumerable<string> Words(string text) => Normal(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Normal(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToUpperInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static bool Matches(string typed, string word)
    {
        if (Same(typed, word)) return true;
        foreach (var group in SameMeaning)
            if (group.Any(g => Same(typed, g)) && group.Any(g => Same(word, g))) return true;
        return false;
    }

    /// <summary>The same word, give or take an ending ("counts", "counting") or one wrong, missing or extra letter.</summary>
    private static bool Same(string a, string b)
    {
        if (a == b) return true;
        var (sa, sb) = (Stem(a), Stem(b));
        if (sa == sb) return true;
        if (Math.Min(a.Length, b.Length) >= 5 && Math.Abs(a.Length - b.Length) <= 1 && Distance(a, b) <= 1) return true;
        return Math.Min(sa.Length, sb.Length) >= 5 && Math.Abs(sa.Length - sb.Length) <= 1 && Distance(sa, sb) <= 1;
    }

    private static string Stem(string w)
    {
        foreach (var end in new[] { "ING", "ED", "ES", "S" })
            if (w.Length > end.Length + 2 && w.EndsWith(end, StringComparison.Ordinal)) return w[..^end.Length];
        return w;
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
