namespace EstreamAssistant.Core.Changes;

/// <summary>
/// A requested change to one item, from Claude (stage_changes) or the item card.
/// Null = leave that field alone. Values are what eStream should end up with.
/// </summary>
public sealed record ChangeRequest
{
    public required string Upc { get; init; }
    public decimal? Price { get; init; }
    /// <summary>"For" quantity. Decimal so a non-whole value can be rejected with a clear reason.</summary>
    public decimal? For { get; init; }
    public decimal? CaseCost { get; init; }
    public int? Deposit { get; init; }
    public int? Levy { get; init; }
    public decimal? OnHand { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>Overlays the non-null fields of <paramref name="newer"/> on this request.</summary>
    public ChangeRequest Merge(ChangeRequest newer) => this with
    {
        Price = newer.Price ?? Price,
        For = newer.For ?? For,
        CaseCost = newer.CaseCost ?? CaseCost,
        Deposit = newer.Deposit ?? Deposit,
        Levy = newer.Levy ?? Levy,
        OnHand = newer.OnHand ?? OnHand,
        Min = newer.Min ?? Min,
        Max = newer.Max ?? Max,
        Reason = string.IsNullOrWhiteSpace(newer.Reason) ? Reason : newer.Reason,
    };
}

public enum ChangeField { Price, For, CaseCost, Deposit, Levy, OnHand, Min, Max }

/// <summary>One field going from Now to New, e.g. Price $4.29 → $4.79.</summary>
public sealed record FieldChange(ChangeField Field, string Now, string New, decimal NowValue, decimal NewValue)
{
    public string Label => Field switch
    {
        ChangeField.CaseCost => "Case cost",
        ChangeField.OnHand => "On hand",
        ChangeField.Levy => "Fee/levy",
        _ => Field.ToString(),
    };

    public override string ToString() => $"{Label} {Now} → {New}";
}

public enum RowStatus
{
    /// <summary>Valid, waiting for a dry run.</summary>
    Staged,
    /// <summary>Not run: failed a safety rule (app or engine).</summary>
    Skipped,
    /// <summary>The engine dry run typed the values in eStream and threw them away: OK to save.</summary>
    DryRunOk,
    Failed,
    Applied,
    /// <summary>Saved, but the value read back from the database didn't match.</summary>
    Unverified,
    /// <summary>The engine returned a status this app doesn't know: treated as not successful.</summary>
    Unknown,
}

/// <summary>A row in the pending changes list (immutable snapshot).</summary>
public sealed record ChangeRow
{
    public required long Upc { get; init; }
    public required string Description { get; init; }
    public required ChangeRequest Request { get; init; }
    public IReadOnlyList<FieldChange> Fields { get; init; } = [];
    public RowStatus Status { get; init; }
    public string Message { get; init; } = "";
    /// <summary>The row was skipped only because of a large-change limit; the owner can allow it.</summary>
    public bool NeedsForce { get; init; }
    /// <summary>The owner confirmed "Allow large change" in a dialog (Force=Y). Claude can't set this.</summary>
    public bool ForceConfirmed { get; init; }
    /// <summary>The engine's own status text, shown as-is.</summary>
    public string? EngineStatus { get; init; }
    /// <summary>GM% before and after, when the price, For or case cost changes.</summary>
    public decimal? GmNow { get; init; }
    public decimal? GmNew { get; init; }
    /// <summary>Things in eStream that change what this does: mix match group, specials, pending price batches.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>The item's Mix Match group when the price or For changes (eStream reprices the whole group).</summary>
    public MixMatchGroupRef? Group { get; init; }
    /// <summary>The item is in a Mix Match group (whatever changes). The price batch script refuses these.</summary>
    public bool InMixMatchGroup { get; init; }
    /// <summary>The last dry run sent this row through a price batch (not the item editor); SAVE does the same.</summary>
    public bool ViaPriceBatch { get; init; }

    public string ChangesText => Fields.Count == 0 ? "" : string.Join("; ", Fields);
}

/// <summary>The group a price change spreads to, kept so the save can report the other items afterwards.</summary>
public sealed record MixMatchGroupRef(int Id, string Description, int Others);
