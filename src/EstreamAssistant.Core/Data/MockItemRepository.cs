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
        lock (_items) return _items.GetValueOrDefault(upc);
    }

    public async Task<IReadOnlyList<long>> FindByShelfCodeAsync(string shelfCode, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        var code = shelfCode.Trim().TrimStart('0');
        if (code.Length == 0) return [];
        lock (_items) return _items.Values.Where(i => !i.Inactive && i.ShelfCode.Trim().TrimStart('0').Equals(code, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Upc).Take(10).ToList();
    }

    /// <summary>Supplier item numbers (eStream's VendorItems): code → UPC.</summary>
    public Dictionary<string, long> SupplierCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<long>> FindBySupplierCodeAsync(string code, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        var bare = code.Trim().TrimStart('0');
        lock (_items) return SupplierCodes.Where(p => p.Key.Trim().TrimStart('0').Equals(bare, StringComparison.OrdinalIgnoreCase)
                                                    && _items.TryGetValue(p.Value, out var i) && !i.Inactive)
            .Select(p => p.Value).Distinct().Take(10).ToList();
    }

    public async Task<IReadOnlyList<ItemName>> GetAllNamesAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        lock (_items) return _items.Values.Select(i => new ItemName(i.Upc, i.Description, i.Sold, i.Inactive)).ToList();
    }

    public async Task<CodeTables> GetCodesAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return _codes;
    }

    public Task<int> GetActiveCountAsync(CancellationToken ct = default)
    {
        lock (_items) return Task.FromResult(_items.Values.Count(i => !i.Inactive));
    }

    /// <summary>
    /// MOCK ONLY: the simulated engine "saves" into the sample data so lookups after a mock
    /// SAVE show the new values. The real eStream database is never written by this app (S1).
    /// </summary>
    public void SimulateEngineSave(Item updated)
    {
        lock (_items)
        {
            var before = _items.GetValueOrDefault(updated.Upc);
            _items[updated.Upc] = updated;
            // Like eStream: a new price on one Mix Match item changes the whole group.
            if (updated.MixMatchId > 0 && before is not null && (before.Price != updated.Price || before.For != updated.For))
                foreach (var other in _items.Values.Where(i => i.MixMatchId == updated.MixMatchId && i.Upc != updated.Upc && !i.Inactive).ToList())
                    _items[other.Upc] = other with { Price = updated.Price, For = updated.For, LastUpdated = updated.LastUpdated };
        }
    }

    // --- mix match groups, specials and price batches (MOCK, made up) -------------------

    public Dictionary<int, (string Description, decimal Price)> Groups { get; } = new()
    {
        [12] = ("LAYS 220-235G (sample)", 4.29m),
    };

    /// <summary>Dates are relative to today so the samples stay current.</summary>
    public List<(long Upc, ItemSpecial Special)> Specials { get; } =
    [
        (28400090360, new ItemSpecial { EventId = 501, Title = "Weekend flyer (sample)", Start = DateTime.Today.AddDays(-2), End = DateTime.Today.AddDays(5), Price = 3.99m }),
        (67000000409, new ItemSpecial { EventId = 502, Title = "Pop sale (sample)", Start = DateTime.Today.AddDays(7), End = DateTime.Today.AddDays(13), Price = 2.99m }),
    ];

    public List<(long Upc, PendingBatchLine Line)> Batches { get; } =
    [
        (69000002056, new PendingBatchLine { BatchId = 10208, ApplyOn = DateTime.Today.AddDays(-1), Comment = "Vendor cost update (sample)", NewPrice = 3.99m, NewCaseCost = 20.40m }),
        (57000002152, new PendingBatchLine { BatchId = 10211, ApplyOn = DateTime.Today.AddDays(3), Comment = "Price increase (sample)", NewPrice = 5.79m, NewCaseCost = 41.40m }),
    ];

    public Task<string?> CheckTablesAsync(DbFeature feature, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public async Task<ItemContext> GetItemContextAsync(Item item, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        MixMatchGroup? group = null;
        lock (_items)
        {
            if (item.MixMatchId > 0)
            {
                var (desc, price) = Groups.GetValueOrDefault(item.MixMatchId, ("", 0m));
                group = new MixMatchGroup
                {
                    Id = item.MixMatchId,
                    Description = desc,
                    Price = price,
                    Others = _items.Values.Where(i => i.MixMatchId == item.MixMatchId && i.Upc != item.Upc && !i.Inactive)
                        .Select(i => new GroupMember(i.Upc, i.Price, i.For)).ToList(),
                };
            }
        }
        return new ItemContext
        {
            Group = group,
            Specials = Specials.Where(s => s.Upc == item.Upc && s.Special.End.Date >= DateTime.Today).Select(s => s.Special).ToList(),
            Batches = Batches.Where(b => b.Upc == item.Upc).Select(b => b.Line).ToList(),
        };
    }

    // --- store reports (sample data) ------------------------------------------------

    private static readonly VendorInfo[] SampleVendors =
    [
        new(1, "FRITO LAY (sample)", false, 0), new(2, "COCA-COLA (sample)", false, 0),
        new(3, "PEPSI (sample)", false, 0), new(4, "LOCAL WHOLESALE (sample)", false, 0), new(9, "OLD SUPPLIER (sample)", true, 0),
    ];

    /// <summary>Sample vendor by UPC prefix.</summary>
    internal static int VendorOf(long upc) => (upc / 100000) switch
    {
        604100 or 284000 or 65633 => 1,
        670000 => 2,
        690000 or 611269 => 3,
        _ => 4,
    };

    private static readonly CategoryInfo[] SampleCategories =
    [
        new(10, "CHIPS & SNACKS", 1, "GROCERY", 35), new(20, "SOFT DRINKS", 2, "BEVERAGE", 30), new(30, "DAIRY & EGGS", 3, "PERISHABLE", 25),
        new(40, "GROCERY", 1, "GROCERY", 30), new(50, "PRODUCE", 3, "PERISHABLE", 40),
    ];

    internal static int CategoryOf(Item i) => i.CategoryId != 0 ? i.CategoryId : (i.Upc / 100000) switch
    {
        604100 or 284000 or 65633 => 10,
        670000 or 690000 or 611269 => 20,
        687000 or 620000 => 30,
        0 => 50,
        _ => 40,
    };

    private List<Item> Active() { lock (_items) return _items.Values.Where(i => !i.Inactive).ToList(); }

    public async Task<IReadOnlyList<VendorInfo>> GetVendorsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        List<Item> all;
        lock (_items) all = _items.Values.ToList();
        return SampleVendors.Select(v => v with { Items = all.Count(i => VendorOf(i.Upc) == v.Id && v.Id != 9) }).ToList();
    }

    public async Task<IReadOnlyList<OrderLine>> GetOrderLinesAsync(int vendorId, DateTime salesFrom, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        int days = Math.Max(1, (DateTime.Today - salesFrom.Date).Days + 1);
        List<Item> all;
        lock (_items) all = _items.Values.Where(i => VendorOf(i.Upc) == vendorId && vendorId != 9).ToList();
        return all.Select(i => new OrderLine
        {
            Upc = i.Upc,
            Description = i.Description,
            VendorCode = i.ShelfCode,
            CaseCost = i.CaseCost,
            PacksPerCase = i.PacksPerCase,
            UnitsPerPack = i.UnitsPerPack,
            OnHand = i.OnHand,
            Min = i.Min,
            Max = i.Max,
            PackQuantity = i.PacksPerCase * i.UnitsPerPack is var u && u is > 1 and <= 24 ? u : 0,
            // Made-up: roughly a week's share of the 4-week sales per 7 days of history.
            SoldInPeriod = Math.Round(i.FourWeekSales / 28m * days),
            BackOrdered = i.BackOrdered,
            Inactive = i.Inactive,
        }).ToList();
    }

    public async Task<IReadOnlyList<ItemFacts>> GetItemFactsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return Active().Select(i => new ItemFacts
        {
            Upc = i.Upc, OnHand = i.OnHand, UnitCost = i.UnitCost, Price = i.Price, For = i.For, CaseCost = i.CaseCost,
            PacksPerCase = i.PacksPerCase, UnitsPerPack = i.UnitsPerPack, CategoryId = CategoryOf(i), GmPct = i.GmPct,
            // Sample: eStream has two items flagged for labels.
            MakeLabel = i.Upc is 68000001236 or 65633001015, Sold = i.Sold,
        }).ToList();
    }

    public async Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return SampleCategories;
    }

    public async Task<IReadOnlyList<MixMatchInfo>> GetMixMatchGroupsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        var active = Active();
        return Groups.Select(g => new MixMatchInfo(g.Key, g.Value.Description, g.Value.Price, active.Count(i => i.MixMatchId == g.Key))).ToList();
    }

    public async Task<IReadOnlyList<SpecialEventInfo>> GetSpecialEventsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return Specials.GroupBy(s => s.Special.EventId)
            .Select(g => new SpecialEventInfo(g.Key, g.First().Special.Title, g.First().Special.Start, g.First().Special.End, g.Count()))
            .Where(e => e.End >= DateTime.Today.AddDays(-7)).OrderBy(e => e.Start).ToList();
    }

    public async Task<IReadOnlyList<PriceBatchInfo>> GetPendingBatchesAsync(CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return Batches.GroupBy(b => b.Line.BatchId)
            .Select(g => new PriceBatchInfo(g.Key, g.First().Line.ApplyOn, g.First().Line.Comment, g.Count(), 0)).OrderByDescending(b => b.Id).ToList();
    }

    public async Task<IReadOnlyList<DailySales>> GetDailySalesAsync(long upc, DateTime from, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        Item? item;
        lock (_items) item = _items.GetValueOrDefault(upc);
        if (item is null) return [];
        var list = new List<DailySales>();
        // Made-up but steady: the weekly figure spread over the days, a bit busier on weekends.
        for (var d = DateTime.Today; d >= from.Date && list.Count < 400; d = d.AddDays(-1))
        {
            int week = Math.Min(3, (DateTime.Today - d).Days / 7);
            var perDay = item.WeekSales.Count > week ? item.WeekSales[week] / 7m : 0;
            var qty = Math.Round(perDay * (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? 1.4m : 0.85m));
            list.Add(new DailySales(d, qty, qty * item.Price / item.For));
        }
        return list;
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<IReadOnlyList<StockRow>> GetStockRowsAsync(StockFilter filter, bool includeInactive, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        lock (_items)
            return _items.Values
                .Where(i => includeInactive || !i.Inactive)
                .Where(i => filter switch
                {
                    StockFilter.Negative => i.OnHand < 0,
                    StockFilter.Zero => i.OnHand == 0,
                    StockFilter.BelowMin => i.Min > 0 && i.OnHand < i.Min,
                    StockFilter.AboveMax => i.Max > 0 && i.OnHand > i.Max,
                    _ => false,
                })
                .Select(i => new StockRow
                {
                    Upc = i.Upc, OnHand = i.OnHand, Min = i.Min, Max = i.Max, Sold = i.Sold,
                    UnitCost = i.UnitCost, Price = i.Price, For = i.For, Inactive = i.Inactive,
                })
                .ToList();
    }

    /// <summary>MOCK stock sheets (made-up numbers): one open, one closed.</summary>
    public List<StockSheetInfo> Sheets { get; } =
    [
        new() { Id = 9002, CloseDate = new DateTime(2026, 11, 17), Comment = "Chips aisle (sample)", Supervisor = "OWNER", Closed = false, Lines = 4 },
        new() { Id = 9001, CloseDate = new DateTime(2026, 10, 30), Comment = "Drinks cooler (sample, posted)", Supervisor = "OWNER", Closed = true, Lines = 3 },
    ];

    public Dictionary<int, List<StockSheetLine>> SheetLines { get; } = new()
    {
        [9002] =
        [
            new() { Upc = 60410079591, System = 14, Counted = 12, UnitCost = 3.30m },
            new() { Upc = 60410079966, System = 9, Counted = 9, UnitCost = 3.30m },
            new() { Upc = 28400090360, System = 7, Counted = 0, UnitCost = 3.30m },
            new() { Upc = 60410041277, System = -3, Counted = 20, UnitCost = 3.30m },
        ],
        // Posted: eStream sets System = Counted, and the posted change is in Adjustment.
        [9001] =
        [
            new() { Upc = 67000000409, System = 28, Counted = 28, Adjustment = -2, UnitCost = 2.52m, SheetClosed = true },
            new() { Upc = 69000002056, System = 16, Counted = 16, Adjustment = 0, UnitCost = 2.40m, SheetClosed = true },
            new() { Upc = 61126999100, System = 5, Counted = 5, Adjustment = 3, UnitCost = 2.17m, SheetClosed = true },
        ],
    };

    public async Task<IReadOnlyList<StockSheetInfo>> GetStockSheetsAsync(bool includeClosed, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return Sheets.Where(s => includeClosed || !s.Closed).OrderBy(s => s.Closed).ThenByDescending(s => s.Id).ToList();
    }

    public async Task<IReadOnlyList<StockSheetLine>> GetStockSheetLinesAsync(int sheetId, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return SheetLines.TryGetValue(sheetId, out var lines) ? lines.ToList() : [];
    }

    public Task<string?> CheckStockSheetTablesAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

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
                GmPct = Pricing.PricingCalculator.GrossMarginPct(price / Math.Max(1, (int)Dec("PriceMult")),
                    Pricing.PricingCalculator.EStreamGmCost(Dec("CaseCost"), Dec("PacksPerCase"), Dec("UnitsPerPack"), unitCost)),
            });
        }
        return items;
    }

    /// <summary>
    /// MOCK sample items. The first two UPCs/names are real eStream items from the
    /// Phase 7 acceptance list; ALL prices, costs and stock numbers are made up.
    /// Case sizes as eStream keeps them: packs per case = items sold at the till, units per pack 1.
    /// </summary>
    public static IReadOnlyList<Item> SampleItems() =>
    [
        Mk(60410079591, "LAYS ALL DRESS 220 g", 4.29m, 1, 52.80m, 16, 1, 10, sold: 412, onHand: 14, min: 6, max: 24),
        Mk(60410079966, "CUCMBER LAYS", 4.29m, 1, 52.80m, 16, 1, 0, sold: 120, onHand: 9, min: 4, max: 16),
        Mk(60410004128, "LAY'S CUCUMBER 40G", 1.79m, 1, 30.00m, 30, 1, 0, sold: 88, onHand: 22, min: 10, max: 40),
        // Mix Match group 12 (sample): a price change on one of these changes both.
        Mk(60410041277, "LAYS CLASSIC 235G", 4.29m, 1, 52.80m, 16, 1, 0, sold: 530, onHand: 18, min: 6, max: 24, mixMatch: 12),
        // Same brand written different ways (lower case, split apostrophe, run together): for list_items.
        Mk(60410041444, "lays salt & vinegar 235g", 4.29m, 1, 52.80m, 16, 1, 0, sold: 140, onHand: 8, min: 0, max: 0, mixMatch: 12),
        Mk(60410040011, "LAY S BBQ 40G", 1.79m, 1, 30.00m, 30, 1, 0, sold: 61, onHand: 15, min: 0, max: 0),
        Mk(60410090055, "LAYSKETTLE JALAPENO", 4.49m, 1, 52.80m, 16, 1, 0, sold: 35, onHand: 6, min: 0, max: 0),
        Mk(67000000409, "COKE 2L", 3.99m, 1, 20.16m, 8, 1, 20, sold: 610, onHand: 30, min: 12, max: 48, levy: 1),
        Mk(67000001123, "COCA COLA 355ML 12PK", 9.49m, 1, 15.20m, 2, 1, 120, sold: 240, onHand: 11, min: 4, max: 16),
        Mk(69000002056, "PEPSI 2L", 3.79m, 1, 19.20m, 8, 1, 20, sold: 300, onHand: 16, min: 8, max: 32, levy: 1),
        Mk(28400090360, "DORITOS NACHO CHS 235G", 4.79m, 1, 52.80m, 16, 1, 0, sold: 275, onHand: 7, min: 6, max: 24),
        Mk(68700001017, "MILK 2% 4L", 6.49m, 1, 21.00m, 4, 1, 0, sold: 900, onHand: 12, min: 8, max: 24),
        Mk(68300001110, "BREAD WHITE 570G", 3.69m, 1, 24.00m, 12, 1, 0, sold: 700, onHand: 10, min: 6, max: 18),
        Mk(4011, "BANANAS", 0.39m, 1, 30.00m, 100, 1, 0, sold: 1500, onHand: 60, min: 20, max: 120),
        Mk(68000001236, "KRAFT DINNER ORIG 225G", 2.00m, 2, 13.92m, 12, 1, 0, sold: 450, onHand: 24, min: 12, max: 48),
        Mk(57000002152, "HEINZ KETCHUP 1L", 5.49m, 1, 39.00m, 12, 1, 0, sold: 130, onHand: 5, min: 3, max: 12),
        Mk(65633001015, "CHEERIOS 430G", 6.99m, 1, 59.40m, 12, 1, 0, sold: 160, onHand: 6, min: 3, max: 12),
        // Negative on hand (sold more than was received): for the stock reports.
        Mk(61126999100, "RED BULL 250ML", 3.49m, 1, 52.08m, 24, 1, 10, sold: 340, onHand: -6, min: 6, max: 24),
        Mk(62000000123, "EGGS LARGE 12", 4.99m, 1, 51.00m, 15, 1, 0, sold: 820, onHand: -11, min: 10, max: 30),
        Mk(62000000550, "ICE 3KG", 3.29m, 1, 18.00m, 12, 1, 0, sold: 260, onHand: -2, min: 0, max: 0),
        // Stored without its check digit (~2% of eStream codes are like this).
        Mk(6563300102, "OLD DUTCH RIPPLE 255G", 4.49m, 1, 48.00m, 16, 1, 0, sold: 50, onHand: 0, min: 0, max: 0, inactive: true),
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
        int deposit, decimal sold, decimal onHand, decimal min, decimal max, int levy = 0, bool inactive = false, int mixMatch = 0)
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
            GmPct = Pricing.PricingCalculator.GrossMarginPct(price / @for, Pricing.PricingCalculator.EStreamGmCost(caseCost, packs, units, unitCost)),
            Inactive = inactive,
            LastUpdated = new DateTime(2026, 11, 1, 9, 0, 0),
            MixMatchId = mixMatch,
            Location = 1,
            Bin = (int)(upc % 7) + 1,
            ShelfCode = "SC" + (upc % 100000).ToString("00000", CultureInfo.InvariantCulture),
            LastReceived = new DateTime(2026, 11, 10),
            LastCaseCost = caseCost,
            // Made-up weekly sales: a share of Sold, most recent week first.
            WeekSales = [Math.Round(sold / 40m), Math.Round(sold / 45m), Math.Round(sold / 50m), Math.Round(sold / 55m)],
        };
    }
}
