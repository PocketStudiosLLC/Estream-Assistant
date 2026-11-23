namespace EstreamAssistant.Core.Models;

/// <summary>
/// What else in eStream affects an item's price (read-only, from the CMSystems manual):
///  - a Mix Match group: changing the price of one item changes the price of every item in it;
///  - Special Events: while one runs, its price is what the register charges (the cheapest wins);
///  - pending Price Batches: one with an Apply On date of today or later is applied automatically.
/// </summary>
public sealed record ItemContext
{
    public static ItemContext Empty { get; } = new();

    public MixMatchGroup? Group { get; init; }
    public IReadOnlyList<ItemSpecial> Specials { get; init; } = [];
    public IReadOnlyList<PendingBatchLine> Batches { get; init; } = [];

    /// <summary>Set when part of the context couldn't be read (the app log has the reason).</summary>
    public string? Problem { get; init; }

    public bool IsEmpty => Group is null && Specials.Count == 0 && Batches.Count == 0;
}

/// <summary>A Mix Match group (MixMatch table) and the other active items in it (Inventory.MixmatchID).</summary>
public sealed record MixMatchGroup
{
    public required int Id { get; init; }
    public string Description { get; init; } = "";
    /// <summary>MixMatch.MMPrice: the group price.</summary>
    public decimal Price { get; init; }
    /// <summary>Active items in the group other than the one being looked at.</summary>
    public IReadOnlyList<GroupMember> Others { get; init; } = [];
    /// <summary>True when there were more members than were read.</summary>
    public bool MoreOthers { get; init; }
}

public sealed record GroupMember(long Upc, decimal Price, int For)
{
    public string Description { get; init; } = "";
}

/// <summary>An item on a Special Event that hasn't ended (SpecialInfo + SpecialDetail).</summary>
public sealed record ItemSpecial
{
    public required int EventId { get; init; }
    public string Title { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public decimal Price { get; init; }
    public int For { get; init; } = 1;

    /// <summary>Events run from 12:01 AM on the first day to 11:59 PM on the last day.</summary>
    public bool IsOn(DateTime now) => now.Date >= Start.Date && now.Date <= End.Date;
}

/// <summary>An item in a price batch that hasn't been applied yet (PBInfo + PBDetail).</summary>
public sealed record PendingBatchLine
{
    public required int BatchId { get; init; }
    public DateTime? ApplyOn { get; init; }
    public string Comment { get; init; } = "";
    public decimal NewPrice { get; init; }
    public int NewFor { get; init; } = 1;
    public decimal NewCaseCost { get; init; }

    /// <summary>
    /// eStream applies a pending batch automatically when its Apply On date is today or later
    /// (new batches get yesterday, so they never apply on their own).
    /// </summary>
    public bool AppliesAutomatically(DateTime now) => ApplyOn is { } d && d.Date >= now.Date;
}
