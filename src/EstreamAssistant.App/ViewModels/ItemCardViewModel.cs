using System.Globalization;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.App.ViewModels;

/// <summary>Read-only, display-formatted view of one item for the item card.</summary>
public sealed class ItemCardViewModel
{
    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-CA");

    public ItemCardViewModel(Item item, string? note, CodeTables codes)
    {
        Item = item;
        Note = note;

        PriceText = item.For > 1
            ? $"{item.For} for {item.Price.ToString("C", Money)}"
            : item.Price.ToString("C", Money);

        DepositText = item.Deposit == 0
            ? "None"
            : $"{item.Deposit}  ({item.DepositAmount.ToString("C", Money)})";

        var levy = codes.Levies.FirstOrDefault(l => l.Code == item.Levy);
        LevyText = item.Levy == 0
            ? "None"
            : levy is null ? $"{item.Levy}" : $"{item.Levy}  {levy.Description} ({levy.Amount.ToString("C", Money)})";

        CaseCostText = item.CaseCost.ToString("C", Money);
        PackText = $"{Num(item.PacksPerCase)} pack × {Num(item.UnitsPerPack)} units";
        UnitCostText = "$" + item.UnitCost.ToString("0.00##", CultureInfo.InvariantCulture);
        GmText = $"{item.GmPct:0.0}%";
        OnHandText = Num(item.OnHand);
        MinMaxText = $"{Num(item.Min)} / {Num(item.Max)}";
        SoldText = Num(item.Sold);
        LastUpdatedText = item.LastUpdated?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "—";
        // Case items that show 1 pack × 1 unit need a check before any cost math (Plan.txt sec. 11).
        CaseSizeWarning = item.PacksPerCase <= 1 && item.UnitsPerPack <= 1 && item.CaseCost > item.Price * 2
            ? "eStream shows 1 × 1 for this item. Check the case size before using the cost."
            : null;
    }

    public Item Item { get; }
    public long Upc => Item.Upc;
    public string Description => Item.Description;
    public string UpcText => Item.Upc.ToString(CultureInfo.InvariantCulture);
    public bool IsInactive => Item.Inactive;
    public string? Note { get; }
    public string? CaseSizeWarning { get; }

    public string PriceText { get; }
    public string DepositText { get; }
    public string LevyText { get; }
    public string CaseCostText { get; }
    public string PackText { get; }
    public string UnitCostText { get; }
    public string GmText { get; }
    public string OnHandText { get; }
    public string MinMaxText { get; }
    public string SoldText { get; }
    public string LastUpdatedText { get; }

    private static string Num(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One row in the scan list (several scans in a row build a list).</summary>
/// <summary>Scanning the same item again bumps its count instead of adding a row.</summary>
public sealed record ScannedEntry(long Upc, string Description, string PriceText, DateTime Time, int Count = 1)
{
    public string TimeText => Time.ToString("h:mm:ss tt");
    public string CountText => Count > 1 ? $"×{Count}" : "";
}
