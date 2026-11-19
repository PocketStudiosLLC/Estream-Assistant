using System.Text;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Search;

public sealed record ItemMatch(ItemName Item, double Score)
{
    public int MatchPercent => (int)Math.Round(Score * 100, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Port of Get-EsItem.ps1 -Find (Plan.txt sec. 11). eStream names are abbreviated
/// and misspelled ("CUCMBER LAYS", "LAY'S CUCUMBER"), so matching is per word:
/// exact 1.0, prefix 0.85, contains 0.75, else 1 - Levenshtein/maxLen when the
/// lengths are within 2. Scores are averaged over query words; keep >= 0.5;
/// sort by score then Sold.
/// TODO(store PC): copy the exact stop-word list from Get-EsItem.ps1.
/// </summary>
public sealed class FuzzyMatcher
{
    public const double MinScore = 0.5;
    /// <summary>Below this the owner must pick (never guess the item).</summary>
    public const double ConfidentScore = 0.85;
    /// <summary>Top two scores closer than this also count as ambiguous (tunable; 0.10 so
    /// "lays cucumber" -> 100% 40G bag vs 94% big bag still asks).</summary>
    public const double CloseScoreGap = 0.10;
    public const int DefaultTop = 8;

    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "THE", "AND", "A", "OF", "FOR", "WITH", "TO",
        "CHIPS", "CHIP", "BAG", "BAGS", "POP", "BOTTLE", "CAN", "CANS", "PACK", "PK",
    };

    private readonly record struct Entry(ItemName Item, string Upper, string[] Words);

    private readonly Entry[] _entries;

    public FuzzyMatcher(IEnumerable<ItemName> items)
    {
        _entries = items
            .Select(i => { var u = Clean(i.Description); return new Entry(i, u, SplitWords(u)); })
            .ToArray();
    }

    public int Count => _entries.Length;

    public IReadOnlyList<ItemMatch> Find(string query, int top = DefaultTop, bool includeInactive = false)
    {
        var queryWords = QueryWords(query);
        if (queryWords.Length == 0 || top <= 0) return [];

        // Candidate filter keys: the word itself, plus its first/last 3 letters.
        var keys = queryWords
            .SelectMany(w => w.Length > 3 ? new[] { w, w[..3], w[^3..] } : new[] { w })
            .Distinct()
            .ToArray();

        var matches = new List<ItemMatch>();
        foreach (var e in _entries)
        {
            if (!includeInactive && e.Item.Inactive) continue;
            if (e.Words.Length == 0) continue;
            bool candidate = false;
            foreach (var k in keys)
                if (e.Upper.Contains(k, StringComparison.Ordinal)) { candidate = true; break; }
            if (!candidate) continue;

            double total = 0;
            foreach (var qw in queryWords)
            {
                double best = 0;
                foreach (var dw in e.Words)
                {
                    var s = WordScore(qw, dw);
                    if (s > best) { best = s; if (best >= 1.0) break; }
                }
                total += best;
            }
            var score = total / queryWords.Length;
            if (score >= MinScore) matches.Add(new ItemMatch(e.Item, score));
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Item.Sold)
            .ThenBy(m => m.Item.Description, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    /// <summary>
    /// True when the best match is clear enough to show without asking:
    /// score >= 85% and no runner-up within 10 points.
    /// </summary>
    public static bool IsConfident(IReadOnlyList<ItemMatch> matches)
    {
        if (matches.Count == 0) return false;
        if (matches[0].Score < ConfidentScore) return false;
        return matches.Count == 1 || matches[0].Score - matches[1].Score >= CloseScoreGap;
    }

    internal static double WordScore(string q, string d)
    {
        if (q == d) return 1.0;
        if (d.StartsWith(q, StringComparison.Ordinal) || q.StartsWith(d, StringComparison.Ordinal))
            return Math.Min(q.Length, d.Length) >= 2 ? 0.85 : 0;
        if (d.Contains(q, StringComparison.Ordinal) || q.Contains(d, StringComparison.Ordinal))
            return Math.Min(q.Length, d.Length) >= 3 ? 0.75 : 0;
        if (Math.Abs(q.Length - d.Length) > 2) return 0;
        int maxLen = Math.Max(q.Length, d.Length);
        return Math.Max(0, 1.0 - (double)Levenshtein(q, d) / maxLen);
    }

    internal static string[] QueryWords(string query)
    {
        var words = SplitWords(Clean(query));
        var filtered = words.Where(w => !StopWords.Contains(w)).ToArray();
        // If every word is a stop-word ("pop"), search for them anyway.
        return (filtered.Length > 0 ? filtered : words).Distinct().ToArray();
    }

    /// <summary>Upper-case, apostrophes removed (LAY'S -> LAYS).</summary>
    internal static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\'' or '’' or '`') continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    internal static string[] SplitWords(string upper)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in upper)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words.ToArray();
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
