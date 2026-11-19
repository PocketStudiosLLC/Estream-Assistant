using System.Text.Json;
using EstreamAssistant.Core.Models;
using EstreamAssistant.Core.Search;

namespace EstreamAssistant.Core.Data;

/// <summary>
/// Local cache of item names for fuzzy search (Plan.txt sec. 5 performance warning:
/// reading Inventory.Description takes ~16 s on the store PC). Refreshed every 12 h,
/// on demand, and once automatically when a search finds nothing.
/// Stored as JSON in %LOCALAPPDATA%\eStreamAssistant.
/// </summary>
public sealed class NameCache
{
    private sealed record CacheFile(DateTime RefreshedUtc, string Source, List<ItemName> Items);

    private readonly string _path;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile FuzzyMatcher _matcher = new([]);

    public NameCache(string path) => _path = path;

    public DateTime? LastRefreshedUtc { get; private set; }
    public string? Source { get; private set; }
    public int Count => _matcher.Count;
    public bool IsRefreshing => _refreshLock.CurrentCount == 0;

    public FuzzyMatcher Matcher => _matcher;

    public bool IsStale(TimeSpan maxAge, DateTime? nowUtc = null) =>
        LastRefreshedUtc is null || (nowUtc ?? DateTime.UtcNow) - LastRefreshedUtc.Value > maxAge;

    /// <summary>Loads the cache file if it exists and was made from the same data source.</summary>
    public async Task<bool> LoadAsync(string expectedSource, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return false;
        try
        {
            await using var fs = File.OpenRead(_path);
            var file = await JsonSerializer.DeserializeAsync<CacheFile>(fs, cancellationToken: ct).ConfigureAwait(false);
            if (file is null || file.Source != expectedSource) return false;
            Set(file.Items, file.RefreshedUtc, file.Source);
            return true;
        }
        catch (JsonException)
        {
            return false; // corrupt cache: just rebuild it
        }
    }

    /// <summary>Pulls all names from the repository and saves them. Only one refresh runs at a time.</summary>
    public async Task RefreshAsync(IItemRepository repo, CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = await repo.GetAllNamesAsync(ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            Set(items, now, repo.SourceName);
            await SaveAsync(new CacheFile(now, repo.SourceName, items.ToList()), ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Set(IEnumerable<ItemName> items, DateTime refreshedUtc, string source)
    {
        _matcher = new FuzzyMatcher(items);
        LastRefreshedUtc = refreshedUtc;
        Source = source;
    }

    private async Task SaveAsync(CacheFile file, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, file, cancellationToken: ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }
}
