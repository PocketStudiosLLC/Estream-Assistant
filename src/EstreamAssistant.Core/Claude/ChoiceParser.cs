using System.Text.RegularExpressions;

namespace EstreamAssistant.Core.Claude;

public sealed record ParsedChoice(int Number, string Label, string Detail);

/// <summary>
/// Finds the numbered choice list in Claude's reply ("1. DESC, UPC 123, $4.29")
/// so the app can show each choice as a button (spec U4). Needs 2+ consecutive
/// numbers starting at 1.
/// </summary>
public static partial class ChoiceParser
{
    [GeneratedRegex(@"^\s*(\d{1,2})[.)]\s+(.+?)\s*$")]
    private static partial Regex Line();

    public static IReadOnlyList<ParsedChoice> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<ParsedChoice>();
        foreach (var raw in text.Split('\n'))
        {
            var m = Line().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var n = int.Parse(m.Groups[1].Value);
            if (n != found.Count + 1)
            {
                // A new list starting at 1 replaces an earlier one; anything else is ignored.
                if (n == 1) found.Clear(); else continue;
            }
            var body = m.Groups[2].Value.Replace("**", "");
            var comma = body.IndexOf(',');
            var label = comma > 0 ? body[..comma].Trim() : body.Trim();
            var detail = comma > 0 ? body[(comma + 1)..].Trim() : "";
            found.Add(new ParsedChoice(n, label, detail));
        }
        return found.Count >= 2 ? found : [];
    }

    /// <summary>
    /// The reply text for display once its choices are shown as buttons: the choice
    /// lines are removed and markdown bold markers dropped.
    /// </summary>
    public static string ForDisplay(string text, bool hasChoices)
    {
        text = text.Replace("**", "");
        if (!hasChoices) return text.Trim();
        var kept = text.Split('\n').Where(l => !Line().IsMatch(l.TrimEnd('\r')));
        var joined = string.Join("\n", kept);
        while (joined.Contains("\n\n\n")) joined = joined.Replace("\n\n\n", "\n\n");
        return joined.Trim();
    }
}
