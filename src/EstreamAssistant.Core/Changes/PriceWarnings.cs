using System.Globalization;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Changes;

/// <summary>
/// Warnings about things in eStream that change what a price change does (CMSystems manual).
/// They never block a change; they're shown on the pending row, in the SAVE window and to Claude.
/// </summary>
public static class PriceWarnings
{
    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-CA");
    private const int NamesShown = 6;

    public static IReadOnlyList<string> For(IReadOnlyList<FieldChange> fields, ItemContext ctx, DateTime now)
    {
        bool price = fields.Any(f => f.Field is ChangeField.Price or ChangeField.For);
        bool cost = fields.Any(f => f.Field == ChangeField.CaseCost);
        if (!price && !cost) return [];
        var w = new List<string>();

        if (price && ctx.Group is { Others.Count: > 0 } g)
        {
            var names = string.Join(", ", g.Others.Take(NamesShown).Select(o => o.Description));
            int more = g.Others.Count - NamesShown + (g.MoreOthers ? 1 : 0);
            w.Add($"Mix match group {g.Id}{Quoted(g.Description)}: eStream gives every item in the group the same price, so this also changes " +
                  $"{Plural(g.Others.Count + (g.MoreOthers ? 1 : 0), "other item", g.MoreOthers)}: {names}{(more > 0 ? $" and {(g.MoreOthers ? "more" : more.ToString(CultureInfo.InvariantCulture) + " more")}" : "")}.");
        }

        if (price)
        {
            foreach (var s in ctx.Specials)
            {
                var sp = PriceText(s.Price, s.For);
                if (s.IsOn(now))
                    w.Add($"On special{Quoted(s.Title)} at {sp} until {Day(s.End)}: the register charges the special price until then, and the new price after.");
                else
                    w.Add($"Special{Quoted(s.Title)} at {sp} runs {Day(s.Start)} to {Day(s.End)}.");
            }
        }

        foreach (var b in ctx.Batches)
        {
            var what = PriceText(b.NewPrice, b.NewFor) + (b.NewCaseCost > 0 ? $", case cost {b.NewCaseCost.ToString("C2", Money)}" : "");
            w.Add(b.AppliesAutomatically(now)
                ? $"Price batch {b.BatchId}{Quoted(b.Comment)} is set to apply on {Day(b.ApplyOn!.Value)} and will change this item to {what}, replacing this change."
                : $"Pending price batch {b.BatchId}{Quoted(b.Comment)} has this item at {what}. If someone applies it in eStream, it replaces this change.");
        }
        return w;
    }

    /// <summary>Short notes for the item card (no change in mind).</summary>
    public static IReadOnlyList<string> ForCard(ItemContext ctx, DateTime now)
    {
        var w = new List<string>();
        if (ctx.Group is { } g)
            w.Add($"Mix match group {g.Id}{Quoted(g.Description)}" + (g.Others.Count > 0
                ? $" with {Plural(g.Others.Count, "other item", g.MoreOthers)}. A price change changes all of them."
                : "."));
        foreach (var s in ctx.Specials)
            w.Add(s.IsOn(now)
                ? $"On special{Quoted(s.Title)}: {PriceText(s.Price, s.For)} until {Day(s.End)}."
                : $"Special{Quoted(s.Title)} coming: {PriceText(s.Price, s.For)} from {Day(s.Start)} to {Day(s.End)}.");
        foreach (var b in ctx.Batches)
            w.Add(b.AppliesAutomatically(now)
                ? $"Price batch {b.BatchId} will set {PriceText(b.NewPrice, b.NewFor)} on {Day(b.ApplyOn!.Value)}."
                : $"In pending price batch {b.BatchId} at {PriceText(b.NewPrice, b.NewFor)} (not scheduled).");
        if (ctx.Problem is not null) w.Add(ctx.Problem);
        return w;
    }

    internal static string PriceText(decimal price, int forQty) =>
        forQty > 1 ? $"{forQty} for {price.ToString("C2", Money)}" : price.ToString("C2", Money);

    private static string Quoted(string s) => string.IsNullOrWhiteSpace(s) ? "" : $" \"{s.Trim()}\"";
    private static string Day(DateTime d) => d.ToString("MMM d", CultureInfo.InvariantCulture);
    private static string Plural(int n, string word, bool atLeast = false) => $"{(atLeast ? "at least " : "")}{n} {word}{(n == 1 ? "" : "s")}";
}
