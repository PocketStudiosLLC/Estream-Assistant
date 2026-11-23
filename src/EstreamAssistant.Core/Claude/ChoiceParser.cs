using System.Text.RegularExpressions;

namespace EstreamAssistant.Core.Claude;

public sealed record ParsedChoice(int Number, string Label, string Detail);

/// <summary>
/// Finds the numbered choice list in Claude's reply ("1. DESC, UPC 123, $4.29") so the app can show each choice
/// as a button (spec U4). A list is only a choice list when it is one: its lines name items ("UPC ..."), or the
/// line before or after it asks the owner (ends with "?", or "which / pick / choose / would you like ..."). Other
/// numbered lists are facts and stay in the text as they are (test 4: a list of facts became buttons, and a second
/// list made the first one disappear). Needs 2+ consecutive numbers starting at 1; the last choice list wins.
/// </summary>
public static partial class ChoiceParser
{
    [GeneratedRegex(@"^\s*(\d{1,2})[.)]\s+(.+?)\s*$")]
    private static partial Regex Line();

    [GeneratedRegex(@"\b(which|pick|choose|select|would you like|do you want|do you mean|did you mean|tell me)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Asks();

    [GeneratedRegex(@"\bUPC\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex NamesAnItem();

    private sealed record Block(int FirstLine, int LastLine, List<(int Line, int Number, string Body)> Items);

    public static IReadOnlyList<ParsedChoice> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var block = ChoiceBlock(Lines(text));
        if (block is null) return [];
        return block.Items.Select(i =>
        {
            var body = i.Body.Replace("**", "");
            var comma = body.IndexOf(',');
            var label = comma > 0 ? body[..comma].Trim() : body.Trim();
            var detail = comma > 0 ? body[(comma + 1)..].Trim() : "";
            return new ParsedChoice(i.Number, label, detail);
        }).ToList();
    }

    /// <summary>
    /// The reply text for display once its choices are shown as buttons: only the choice list's lines are removed
    /// (numbered facts stay), and markdown bold markers are dropped.
    /// </summary>
    public static string ForDisplay(string text, bool hasChoices)
    {
        text = text.Replace("**", "");
        if (!hasChoices) return text.Trim();
        var lines = Lines(text);
        var block = ChoiceBlock(lines);
        if (block is null) return text.Trim();
        var remove = block.Items.Select(i => i.Line).ToHashSet();
        var joined = string.Join("\n", lines.Where((_, i) => !remove.Contains(i)));
        while (joined.Contains("\n\n\n")) joined = joined.Replace("\n\n\n", "\n\n");
        return joined.Trim();
    }

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>The last numbered list that is a choice list, or null.</summary>
    private static Block? ChoiceBlock(string[] lines)
    {
        Block? best = null;
        foreach (var b in Blocks(lines))
            if (b.Items.Count >= 2 && IsChoice(b, lines)) best = b;
        return best;
    }

    /// <summary>Runs of "1. .. 2. .. 3." (blank lines in between are allowed); a new "1." starts a new run.</summary>
    private static List<Block> Blocks(string[] lines)
    {
        var blocks = new List<Block>();
        Block? current = null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var m = Line().Match(lines[i]);
            if (!m.Success) { current = null; continue; }
            var n = int.Parse(m.Groups[1].Value);
            if (current is not null && n == current.Items.Count + 1)
            {
                current.Items.Add((i, n, m.Groups[2].Value));
                blocks[^1] = current with { LastLine = i };
                current = blocks[^1];
            }
            else if (n == 1)
            {
                current = new Block(i, i, [(i, 1, m.Groups[2].Value)]);
                blocks.Add(current);
            }
            else current = null;
        }
        return blocks;
    }

    private static bool IsChoice(Block b, string[] lines)
    {
        if (b.Items.All(i => NamesAnItem().IsMatch(i.Body))) return true;
        var before = lines.Take(b.FirstLine).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        var after = lines.Skip(b.LastLine + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return Prompts(before) || (after is not null && after.TrimEnd('*').EndsWith('?'));
    }

    private static bool Prompts(string? line) =>
        line is not null && (line.TrimEnd('*').EndsWith('?') || (line.TrimEnd('*').EndsWith(':') && Asks().IsMatch(line)));
}
