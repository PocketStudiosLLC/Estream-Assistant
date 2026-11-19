using System.Globalization;
using EstreamAssistant.Core.Models;

namespace EstreamAssistant.Core.Data;

/// <summary>
/// In-memory item data for the dev PC, where eStream does not exist (Plan.txt sec. 12).
/// Uses built-in sample items, or a sqlcmd export made on the store PC
/// (see <see cref="LoadSqlcmdExport"/>). Adds an artificial delay to feel realistic.
/// </summary>
public sealed class MockItemRepository : IItemRepository
{
    private readonly Dictionary<long, Item> _items;
    private readonly CodeTables _codes;

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);

    public string SourceName { get; }

    public MockItemRepository(IEnumerable<Item>? items = null, CodeTables? codes = null, string sourceName = "Mock data")
    {
        _codes = codes ?? SampleCodes();
        _items = new Dictionary<long, Item>();
        foreach (var i in items ?? SampleItems())
        {
            // Fill in the deposit amount the way the SQL query's LEFT JOIN would.
            var item = i.Deposit != 0 && i.DepositAmount == 0
                ? i with { DepositAmount = _codes.Deposits.FirstOrDefault(d => d.Code == i.Deposit)?.Amount ?? 0 }
                : i;
            _items[item.Upc] = item;
        }
        SourceName = sourceName;
    }

    public async Task<Item?> GetByBarcodeAsync(long upc, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return _items.GetValueOrDefault(upc);
    }

    public async Task<IReadOnlyList<ItemName>> GetAllNamesAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return _items.Values.Select(i => new ItemName(i.Upc, i.Description, i.Sold, i.Inactive)).ToList();
    }

    public async Task<CodeTables> GetCodesAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return _codes;
    }

    public Task<int> GetActiveCountAsync(CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Count(i => !i.Inactive));

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>
    /// Loads the output of the sqlcmd export in Plan.txt sec. 12
    /// (-s "|" -W, columns UPC|Description|SellingPrice|PriceMult|CaseCost|PacksPerCase|
    /// UnitsPerPack|CostPerUnit|DepositLink|Levy1ID|Units|MinSOH|MaxSOH|Sold|Inactive).
    /// The header and the "----" line are skipped; columns are found by name.
    /// </summary>
    public static IReadOnlyList<Item> LoadSqlcmdExport(string path)
    {
        var lines = File.ReadAllLines(path);
        var items = new List<Item>();
        string[]? header = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var cells = line.Split('|');
            if (header is null)
            {
                header = cells.Select(c => c.Trim()).ToArray();
                continue;
            }
            if (cells.All(c => c.Trim().All(ch => ch == '-'))) continue; // separator line
            if (line.StartsWith('(') && line.EndsWith("affected)")) continue;
            if (cells.Length != header.Length) continue;

            string Cell(string name)
            {
                var idx = Array.FindIndex(header, h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return "";
                var v = cells[idx].Trim();
                return v.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? "" : v;
            }
            decimal Dec(string name) =>
                decimal.TryParse(Cell(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

            if (!long.TryParse(Cell("UPC"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var upc)) continue;
            var price = Dec("SellingPrice");
            var unitCost = Dec("CostPerUnit");
            items.Add(new Item
            {
                Upc = upc,
                Description = Cell("Description"),
                Price = price,
                For = Math.Max(1, (int)Dec("PriceMult")),
                CaseCost = Dec("CaseCost"),
                PacksPerCase = Dec("PacksPerCase"),
                UnitsPerPack = Dec("UnitsPerPack"),
                UnitCost = unitCost,
                Deposit = (int)Dec("DepositLink"),
                Levy = (int)Dec("Levy1ID"),
                OnHand = Dec("Units"),
                Min = Dec("MinSOH"),
                Max = Dec("MaxSOH"),
                Sold = Dec("Sold"),
                Inactive = Dec("Inactive") != 0,
                GmPct = Pricing.PricingCalculator.GrossMarginPct(price, unitCost),
            });
        }
        return items;
    }

    /// <summary>
    /// MOCK sample items. The first two UPCs/names are real eStream items from the
    /// Phase 7 acceptance list; ALL prices, costs and stock numbers are made up.
    /// </summary>
    public static IReadOnlyList<Item> SampleItems() =>
    [
        Mk(60410079591, "LAYS ALL DRESS 220 g", 4.29m, 1, 52.80m, 1, 16, 10, sold: 412, onHand: 14, min: 6, max: 24),
        Mk(60410079966, "CUCMBER LAYS", 4.29m, 1, 52.80m, 1, 16, 0, sold: 120, onHand: 9, min: 4, max: 16),
        Mk(60410004128, "LAY'S CUCUMBER 40G", 1.79m, 1, 30.00m, 1, 30, 0, sold: 88, onHand: 22, min: 10, max: 40),
        Mk(60410041277, "LAYS CLASSIC 235G", 4.29m, 1, 52.80m, 1, 16, 0, sold: 530, onHand: 18, min: 6, max: 24),
        Mk(67000000409, "COKE 2L", 3.99m, 1, 20.16m, 1, 8, 20, sold: 610, onHand: 30, min: 12, max: 48, levy: 1),
        Mk(67000001123, "COCA COLA 355ML 12PK", 9.49m, 1, 15.20m, 2, 1, 120, sold: 240, onHand: 11, min: 4, max: 16),
        Mk(69000002056, "PEPSI 2L", 3.79m, 1, 19.20m, 1, 8, 20, sold: 300, onHand: 16, min: 8, max: 32, levy: 1),
        Mk(28400090360, "DORITOS NACHO CHS 235G", 4.79m, 1, 52.80m, 1, 16, 0, sold: 275, onHand: 7, min: 6, max: 24),
        Mk(68700001017, "MILK 2% 4L", 6.49m, 1, 21.00m, 1, 4, 0, sold: 900, onHand: 12, min: 8, max: 24),
        Mk(68300001110, "BREAD WHITE 570G", 3.69m, 1, 24.00m, 1, 12, 0, sold: 700, onHand: 10, min: 6, max: 18),
        Mk(4011, "BANANAS", 0.39m, 1, 30.00m, 1, 100, 0, sold: 1500, onHand: 60, min: 20, max: 120),
        Mk(68000001236, "KRAFT DINNER ORIG 225G", 2.00m, 2, 13.92m, 1, 12, 0, sold: 450, onHand: 24, min: 12, max: 48),
        Mk(57000002152, "HEINZ KETCHUP 1L", 5.49m, 1, 39.00m, 1, 12, 0, sold: 130, onHand: 5, min: 3, max: 12),
        Mk(65633001015, "CHEERIOS 430G", 6.99m, 1, 59.40m, 1, 12, 0, sold: 160, onHand: 6, min: 3, max: 12),
        // Stored without its check digit (~2% of eStream codes are like this).
        Mk(6563300102, "OLD DUTCH RIPPLE 255G", 4.49m, 1, 48.00m, 1, 16, 0, sold: 50, onHand: 0, min: 0, max: 0, inactive: true),
    ];

    public static CodeTables SampleCodes() => new(
        new[] { 2, 5, 6, 10, 15, 16, 17, 20, 24, 25, 30, 40, 50, 60, 61, 69, 75, 80, 100, 120, 140, 150, 180, 200, 240, 300, 320, 360 }
            .Select(c => new DepositCode(c, $"DEPOSIT {c / 100m:0.00}", c / 100m, $"+DEP {c / 100m:0.00}"))
            .ToList(),
        [
            new LevyCode(1, "ECO FEE BEVERAGE (MOCK)", 0.05m),
            new LevyCode(2, "ECO FEE ELECTRONICS (MOCK)", 0.50m),
            new LevyCode(3, "ECO FEE BATTERIES (MOCK)", 0.10m),
            new LevyCode(75, "ECO FEE TIRES (MOCK)", 5.00m),
            new LevyCode(80, "ECO FEE PAINT (MOCK)", 0.75m),
        ]);

    private static Item Mk(long upc, string desc, decimal price, int @for, decimal caseCost, decimal packs, decimal units,
        int deposit, decimal sold, decimal onHand, decimal min, decimal max, int levy = 0, bool inactive = false)
    {
        var unitCost = Math.Round(caseCost / (packs * units), 4);
        return new Item
        {
            Upc = upc,
            Description = desc,
            Price = price,
            For = @for,
            CaseCost = caseCost,
            PacksPerCase = packs,
            UnitsPerPack = units,
            UnitCost = unitCost,
            Deposit = deposit,
            DepositAmount = deposit / 100m,
            Levy = levy,
            OnHand = onHand,
            Min = min,
            Max = max,
            Sold = sold,
            GmPct = Pricing.PricingCalculator.GrossMarginPct(price / @for, unitCost),
            Inactive = inactive,
            LastUpdated = new DateTime(2026, 11, 1, 9, 0, 0),
        };
    }
}
