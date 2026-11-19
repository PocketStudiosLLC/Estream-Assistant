using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Search;
using EstreamAssistant.Core.Upc;

namespace EstreamAssistant.Core.Data;

public sealed record UpcLookupResult(string Input, string Normalized, Item? Item, UpcCandidate? MatchedBy)
{
    public bool Found => Item is not null;
    public string? Note => MatchedBy?.Note;
}

public sealed record FindResult(string Query, IReadOnlyList<ItemMatch> Matches, bool CacheWasRefreshed)
{
    /// <summary>True when the best match is clear enough to show without asking (S4).</summary>
    public bool IsConfident => FuzzyMatcher.IsConfident(Matches);
}

/// <summary>
/// Item lookups used by the scan box, the item card and (later) Claude's tools.
/// Combines UPC fallbacks with the repository, and fuzzy search with the name cache.
/// </summary>
public sealed class ItemLookupService
{
    private readonly IItemRepository _repo;
    private readonly NameCache _cache;
    private DateTime _lastAutoRefreshUtc = DateTime.MinValue;

    public ItemLookupService(IItemRepository repo, NameCache cache)
    {
        _repo = repo;
        _cache = cache;
    }

    public IItemRepository Repository => _repo;
    public NameCache Cache => _cache;

    /// <summary>Cache max age before a background refresh (Plan.txt: 12 h).</summary>
    public TimeSpan CacheMaxAge { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Limits the "search found nothing -> refresh" rule so a typo can't trigger a 16 s pull every time.</summary>
    public TimeSpan AutoRefreshMinInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>UPC lookup: exact, then without last digit, then with check digit appended.</summary>
    public async Task<UpcLookupResult> LookupUpcAsync(string input, CancellationToken ct = default)
    {
        var normalized = UpcNormalizer.Normalize(input);
        foreach (var candidate in UpcNormalizer.Candidates(input))
        {
            var item = await _repo.GetByBarcodeAsync(candidate.Upc, ct).ConfigureAwait(false);
            if (item is not null) return new UpcLookupResult(input, normalized, item, candidate);
        }
        return new UpcLookupResult(input, normalized, null, null);
    }

    /// <summary>Fuzzy name search. If nothing is found, refreshes the cache once and retries.</summary>
    public async Task<FindResult> FindAsync(string query, int top = FuzzyMatcher.DefaultTop, CancellationToken ct = default)
    {
        var matches = _cache.Matcher.Find(query, top);
        if (matches.Count > 0) return new FindResult(query, matches, false);

        if (DateTime.UtcNow - _lastAutoRefreshUtc < AutoRefreshMinInterval || _cache.IsRefreshing)
            return new FindResult(query, matches, false);

        _lastAutoRefreshUtc = DateTime.UtcNow;
        await _cache.RefreshAsync(_repo, ct).ConfigureAwait(false);
        return new FindResult(query, _cache.Matcher.Find(query, top), true);
    }

    /// <summary>Loads the cache from disk and reports whether it should be refreshed.</summary>
    public async Task<bool> LoadCacheAsync(CancellationToken ct = default)
    {
        await _cache.LoadAsync(_repo.SourceName, ct).ConfigureAwait(false);
        return _cache.IsStale(CacheMaxAge);
    }

    public Task RefreshCacheAsync(CancellationToken ct = default) => _cache.RefreshAsync(_repo, ct);
}
