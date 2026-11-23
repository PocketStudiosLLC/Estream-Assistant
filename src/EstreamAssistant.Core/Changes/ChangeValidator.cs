using System.Globalization;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Changes;

public sealed record ValidationResult(IReadOnlyList<FieldChange> Fields, string? SkipReason, bool NeedsForce)
{
    public bool IsValid => SkipReason is null;
}

/// <summary>
/// Safety rules S7 (mirror of Invoke-EsCsv.ps1). A row is Skipped when the price or case
/// cost changes by more than 50%, on-hand changes by more than 500 units (those three can be
/// allowed with Force after the owner confirms), the deposit or levy code is unknown, the item
/// is inactive, Min &gt; Max, For is not a whole number of at least 1, or nothing changes.
/// The engine checks again itself; if they disagree, the engine's result counts.
/// Checked against Invoke-EsCsv.ps1 (Phase C): Force=Y skips the 50% price/case cost rule and the 500-unit on-hand rule.
/// </summary>
public static class ChangeValidator
{
    public const decimal MaxPctChange = 50m;
    public const decimal MaxOnHandChange = 500m;
    public const decimal MaxPrice = 9999.99m;

    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-CA");

    public static ValidationResult Validate(Item? item, ChangeRequest req, CodeTables codes, bool force)
    {
        if (item is null) return Skip("Not in eStream. New items have to be added in eStream by hand.");
        if (item.Inactive) return Skip("The item is inactive, so it can't be changed.");

        var fields = new List<FieldChange>();
        var large = new List<string>();

        if (req.Price is decimal price)
        {
            if (price <= 0 || price > MaxPrice) return Skip($"Price {M(price)} isn't valid.");
            if (decimal.Round(price, 2) != price) return Skip($"Price {price} has more than 2 decimals.");
            if (price != item.Price)
            {
                if (PctChange(item.Price, price) > MaxPctChange) large.Add($"price changes by {PctChange(item.Price, price):0}%");
                fields.Add(new(ChangeField.Price, M(item.Price), M(price), item.Price, price));
            }
        }

        if (req.For is decimal f)
        {
            if (f < 1 || f != decimal.Truncate(f)) return Skip($"For must be a whole number of at least 1 (got {f}).");
            if ((int)f != item.For) fields.Add(new(ChangeField.For, item.For.ToString(), ((int)f).ToString(), item.For, f));
        }

        if (req.CaseCost is decimal cc)
        {
            if (cc < 0 || cc > 100_000) return Skip($"Case cost {M(cc)} isn't valid.");
            if (decimal.Round(cc, 2) != cc) return Skip($"Case cost {cc} has more than 2 decimals.");
            if (cc != item.CaseCost)
            {
                if (PctChange(item.CaseCost, cc) > MaxPctChange) large.Add($"case cost changes by {PctChange(item.CaseCost, cc):0}%");
                fields.Add(new(ChangeField.CaseCost, M(item.CaseCost), M(cc), item.CaseCost, cc));
            }
        }

        if (req.Deposit is int dep)
        {
            if (dep != 0 && codes.Deposits.All(d => d.Code != dep)) return Skip($"Deposit code {dep} doesn't exist.");
            if (dep != item.Deposit) fields.Add(new(ChangeField.Deposit, CodeText(item.Deposit), CodeText(dep), item.Deposit, dep));
        }

        if (req.Levy is int levy)
        {
            if (levy != 0 && codes.Levies.All(l => l.Code != levy)) return Skip($"Fee/levy code {levy} doesn't exist.");
            if (levy != item.Levy) fields.Add(new(ChangeField.Levy, CodeText(item.Levy), CodeText(levy), item.Levy, levy));
        }

        if (req.OnHand is decimal oh)
        {
            if (Math.Abs(oh) > 1_000_000) return Skip($"On hand {oh} isn't valid.");
            if (oh != item.OnHand)
            {
                if (Math.Abs(oh - item.OnHand) > MaxOnHandChange) large.Add($"on hand changes by {Math.Abs(oh - item.OnHand):0.##} units");
                fields.Add(new(ChangeField.OnHand, N(item.OnHand), N(oh), item.OnHand, oh));
            }
        }

        if (req.Min is decimal min)
        {
            if (min < 0 || min > 1_000_000) return Skip($"Min {min} isn't valid.");
            if (min != item.Min) fields.Add(new(ChangeField.Min, N(item.Min), N(min), item.Min, min));
        }

        if (req.Max is decimal max)
        {
            if (max < 0 || max > 1_000_000) return Skip($"Max {max} isn't valid.");
            if (max != item.Max) fields.Add(new(ChangeField.Max, N(item.Max), N(max), item.Max, max));
        }

        if (req.Min is not null || req.Max is not null)
        {
            var newMin = req.Min ?? item.Min;
            var newMax = req.Max ?? item.Max;
            if (newMin > newMax) return Skip($"Min ({N(newMin)}) would be more than Max ({N(newMax)}).", fields);
        }

        if (fields.Count == 0) return Skip("No change: eStream already has these values.");

        if (large.Count > 0 && !force)
            return new(fields, $"Large change: {string.Join(", ", large)}. The owner can allow it on the row.", NeedsForce: true);

        return new(fields, null, false);
    }

    private static ValidationResult Skip(string reason, IReadOnlyList<FieldChange>? fields = null) => new(fields ?? [], reason, false);

    private static decimal PctChange(decimal from, decimal to) =>
        from == 0 ? (to == 0 ? 0 : 100) : Math.Abs(to - from) / from * 100m;

    internal static string M(decimal v) => v.ToString("C2", Money);
    internal static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string CodeText(int code) => code == 0 ? "none" : code.ToString(CultureInfo.InvariantCulture);
}
