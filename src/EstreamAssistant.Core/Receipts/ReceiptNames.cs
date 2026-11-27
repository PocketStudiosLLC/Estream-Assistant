using System.Globalization;

namespace EstreamAssistant.Core.Receipts;

/// <summary>
/// What a receipt is called: the store and the date printed on it ("Superstore · Nov 25, 2026"), so saved receipts
/// and their stock sheets can be told apart. Falls back to Claude's short name for it, then to nothing.
/// </summary>
public static class ReceiptNames
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-CA");

    public static string Title(string store, DateOnly? date, string source)
    {
        store = store.Trim();
        source = source.Trim();
        var when = date?.ToString("MMM d, yyyy", En);
        if (store.Length > 0) return when is null ? store : $"{store} · {when}";
        if (source.Length > 0) return when is null || source.Contains(date!.Value.Year.ToString(CultureInfo.InvariantCulture)) ? source : $"{source} · {when}";
        return when is null ? "" : $"Receipt · {when}";
    }

    /// <summary>
    /// The eStream stock sheet comment: "Receipt Superstore 2026-11-25" (plain characters, at most 60), so the sheet
    /// says which receipt it came from.
    /// </summary>
    public static string SheetComment(string store, DateOnly? date, string source, string ownName = "")
    {
        var name = store.Trim().Length > 0 ? store.Trim() : source.Trim();
        var text = ownName.Trim().Length > 0 ? "Receipt " + ownName.Trim()
            : "Receipt " + name + (date is { } d ? " " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        text = text.Replace('·', '-');
        text = new string(text.Trim().Select(c => c < 128 && !char.IsControl(c) ? c : ' ').ToArray());
        return text.Length <= 60 ? text : text[..60].TrimEnd();
    }

    /// <summary>A date as printed in YYYY-MM-DD form, or null (empty, unreadable, or not a sensible receipt date).</summary>
    public static DateOnly? ParseDate(string? text, DateOnly today) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        && d >= today.AddYears(-5) && d <= today.AddDays(2)
            ? d
            : null;
}
