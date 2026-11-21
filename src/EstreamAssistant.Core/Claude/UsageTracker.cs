using System.Text.Json;

namespace EstreamAssistant.Core.Claude;

public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok, decimal CacheReadPerMTok)
{
    /// <summary>5-minute cache writes cost 1.25x the input price.</summary>
    public decimal CacheWritePerMTok => InputPerMTok * 1.25m;
}

public sealed record UsageSample(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

/// <summary>
/// Running cost estimate from response usage (Plan.txt sec. 10), saved per day
/// in usage.json. It's an estimate: the Anthropic Console has the real bill.
/// </summary>
public sealed class UsageTracker
{
    /// <summary>US$ per million tokens. Prices change: check the Anthropic pricing page.</summary>
    public static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice>
    {
        ["claude-opus-5-5"] = new(4.00m, 20.00m, 0.20m),
        ["claude-sonnet-5-5"] = new(2.00m, 10.00m, 0.10m),
        ["claude-haiku-5-5"] = new(0.10m, 0.50m, 0.01m),
        ["claude-opus-5"] = new(5.00m, 25.00m, 0.50m),
        ["claude-opus-4-8"] = new(5.00m, 25.00m, 0.50m),
        ["claude-sonnet-5"] = new(2.00m, 10.00m, 0.20m),
    };

    public sealed class DayTotal
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public decimal CostUsd { get; set; }
        public int Requests { get; set; }
    }

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, DayTotal> _days;

    private readonly Func<string, ModelPrice?>? _priceOverride;

    /// <param name="priceOverride">Prices from Settings, when set for a model (spec 7.1).</param>
    public UsageTracker(string? path = null, Func<string, ModelPrice?>? priceOverride = null)
    {
        _path = path;
        _priceOverride = priceOverride;
        _days = Load(path);
    }

    /// <summary>The prices used for a model: Settings first, then the built-in table.</summary>
    public ModelPrice PriceFor(string model) =>
        _priceOverride?.Invoke(model) ?? Prices.GetValueOrDefault(model) ?? Prices["claude-opus-5-5"];

    public event EventHandler? Changed;

    public static decimal Cost(string model, UsageSample u, ModelPrice? price = null)
    {
        var p = price ?? Prices.GetValueOrDefault(model) ?? Prices["claude-opus-5-5"];
        return (u.InputTokens * p.InputPerMTok
              + u.OutputTokens * p.OutputPerMTok
              + u.CacheReadTokens * p.CacheReadPerMTok
              + u.CacheWriteTokens * p.CacheWritePerMTok) / 1_000_000m;
    }

    public decimal Record(string model, UsageSample u, DateTime? when = null)
    {
        var cost = Cost(model, u, PriceFor(model));
        var key = (when ?? DateTime.Now).ToString("yyyy-MM-dd");
        lock (_gate)
        {
            if (!_days.TryGetValue(key, out var d)) _days[key] = d = new DayTotal();
            d.InputTokens += u.InputTokens;
            d.OutputTokens += u.OutputTokens;
            d.CacheReadTokens += u.CacheReadTokens;
            d.CacheWriteTokens += u.CacheWriteTokens;
            d.CostUsd += cost;
            d.Requests++;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return cost;
    }

    public DayTotal Today => Day(DateTime.Now);

    public DayTotal Day(DateTime day)
    {
        lock (_gate) return _days.GetValueOrDefault(day.ToString("yyyy-MM-dd")) ?? new DayTotal();
    }

    /// <summary>All the days of one month added up.</summary>
    public DayTotal Month(DateTime anyDayInMonth)
    {
        var prefix = anyDayInMonth.ToString("yyyy-MM-");
        var t = new DayTotal();
        lock (_gate)
        {
            foreach (var d in _days.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kv => kv.Value))
            {
                t.InputTokens += d.InputTokens;
                t.OutputTokens += d.OutputTokens;
                t.CacheReadTokens += d.CacheReadTokens;
                t.CacheWriteTokens += d.CacheWriteTokens;
                t.CostUsd += d.CostUsd;
                t.Requests += d.Requests;
            }
        }
        return t;
    }

    public decimal MonthCost(DateTime anyDayInMonth)
    {
        var prefix = anyDayInMonth.ToString("yyyy-MM-");
        lock (_gate) return _days.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value.CostUsd);
    }

    private static Dictionary<string, DayTotal> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, DayTotal>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException) { }
        catch (IOException) { }
        return [];
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_days));
        }
        catch (IOException) { }
    }
}
