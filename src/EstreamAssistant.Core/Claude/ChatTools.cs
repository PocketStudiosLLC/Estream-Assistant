using System.Text.Json;

namespace EstreamAssistant.Core.Claude;

/// <summary>
/// The tools Claude may call (spec 7.3). Every schema is strict (additionalProperties false,
/// all properties required; optional values are nullable).
/// There is deliberately NO apply/save tool and NO post-stock-sheet tool: only the owner's
/// buttons can do those (spec S3). stage_changes only fills the pending list and run_dry_run
/// only runs a dry run (eStream's Exit throws the typed values away). create_stock_sheet always
/// shows the owner an in-app confirmation first; posting a sheet is a button only.
/// Strict-schema limits per request (all tools together): 20 strict tools, 24 optional parameters,
/// 16 parameters with union/nullable types. Going over makes EVERY chat request fail with a 400,
/// so ChatToolsTests keeps a margin. Prefer a sentinel (e.g. 0) over a new nullable parameter.
/// </summary>
public static class ChatTools
{
    public const string LookupItem = "lookup_item";
    public const string FindItems = "find_items";
    public const string ListItems = "list_items";
    public const string GetCodes = "get_codes";
    public const string CalcPrice = "calc_price";
    public const string StageChanges = "stage_changes";
    public const string RunDryRun = "run_dry_run";
    public const string StockCountPreview = "stock_count_preview";
    public const string CreateStockSheet = "create_stock_sheet";
    public const string ShowReceiptLines = "show_receipt_lines";
    public const string PriceReceiptLines = "price_receipt_lines";
    public const string StockReport = "stock_report";
    public const string StockSheets = "stock_sheets";
    public const string StoreReport = "store_report";
    public const string ItemSales = "item_sales";
    public const string ReceiptStockSheet = "receipt_stock_sheet";
    public const string QueueShelfLabels = "queue_shelf_labels";
    public const string SavedReceipts = "search_receipts";

    /// <param name="Strict">false for simple read-only tools: they don't count toward the strict-schema limits,
    /// and ToolExecutor re-checks every input anyway.</param>
    public sealed record Definition(string Name, string Description, string SchemaJson, bool Strict = true)
    {
        /// <summary>The schema as the raw dictionary the SDK's InputSchema is built from.</summary>
        public IReadOnlyDictionary<string, JsonElement> Schema =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!;
    }

    public static IReadOnlyList<Definition> All { get; } =
    [
        new(LookupItem,
            "Look up one eStream item by its UPC/PLU barcode number. Handles leading zeros and missing or extra check digits. " +
            "Use this whenever the owner gives or scans a number. Returns the item's price, For quantity, costs, deposit, levy, stock and GM%, or found=false.",
            """
            {"type":"object","properties":{
              "upc":{"type":"string","description":"The barcode digits exactly as given or scanned."}
            },"required":["upc"],"additionalProperties":false}
            """),
        new(FindItems,
            "Search eStream items by name. eStream names are abbreviated and misspelled (e.g. 'CUCMBER LAYS'), so this is a fuzzy search. " +
            "Returns up to 'max' matches with a match percent, and 'needs_choice'=true when the owner must pick. Search ONCE per item the owner mentions.",
            """
            {"type":"object","properties":{
              "text":{"type":"string","description":"The item name words the owner used, e.g. 'lays cucumber'."},
              "max":{"type":"integer","description":"How many matches to return, 1 to 8. Use 8 unless told otherwise."}
            },"required":["text","max"],"additionalProperties":false}
            """),
        new(ListItems,
            "List EVERY eStream item whose name contains a brand or keyword, e.g. all 'lays' or all 'coke 2l'. Read-only. " +
            "Ignores capitals, apostrophes and spacing (LAYS, Lays, LAY'S and LAY S all match 'lays'), so ONE call finds them all; " +
            "never repeat it with other spellings or capitals. Every word given must be in the name. Returns the total count and the items " +
            "(UPC, name, units sold). Use find_items instead to pin down one specific item.",
            """
            {"type":"object","properties":{
              "text":{"type":"string","description":"The brand or keyword(s), e.g. 'lays' or 'lays 40g'."},
              "include_inactive":{"type":"boolean","description":"true to include inactive items. Use false unless the owner asks for them."}
            },"required":["text","include_inactive"],"additionalProperties":false}
            """, Strict: false),
        new(GetCodes,
            "List eStream's deposit codes and fee/levy codes with their dollar amounts.",
            """
            {"type":"object","properties":{},"required":[],"additionalProperties":false}
            """),
        new(CalcPrice,
            "Calculate a selling price from a cost. ALWAYS use this for price math; never do the arithmetic yourself. " +
            "mode 'markup': price = cost x (1 + pct/100). mode 'margin': price = cost / (1 - pct/100). " +
            "Returns the exact price, the price rounded to the cent, the final price (after the store's price-ending rule if apply_endings is true) and the GM%.",
            """
            {"type":"object","properties":{
              "cost":{"type":"number","description":"Cost of ONE item sold at the till (lookup_item's sell_cost = case cost / (packs per case x units per pack)), times the For quantity for a 'N for' price. Dollars, excluding deposit and eco fees."},
              "mode":{"type":"string","enum":["markup","margin"]},
              "pct":{"type":"number","description":"The markup or margin percent, e.g. 30 for 30%."},
              "apply_endings":{"type":"boolean","description":"true to round up to the store's price endings (e.g. .49/.99) from Settings."}
            },"required":["cost","mode","pct","apply_endings"],"additionalProperties":false}
            """),
        new(StageChanges,
            "Add or update rows in the owner's Pending changes list. This does NOT change eStream: the owner reviews the list, " +
            "a dry run checks it, and only the owner's SAVE button can save. Give the NEW values eStream should end up with; " +
            "use null for every field that should stay as it is. Only stage items the owner has confirmed (never a guess). " +
            "The app re-checks every row with the safety rules and tells you which rows were skipped and why. " +
            "Large changes (price or case cost more than 50%, on hand more than 500) are skipped until the owner allows them on the row.",
            """
            {"type":"object","properties":{
              "rows":{"type":"array","description":"One entry per item.","items":{"type":"object","properties":{
                "upc":{"type":"string","description":"The item's UPC as returned by lookup_item or find_items."},
                "price":{"type":["number","null"],"description":"New selling price in dollars for the 'for' quantity, e.g. 5.00 for '2 for $5'."},
                "for":{"type":["integer","null"],"description":"New For quantity (2 for '2 for $5'); null to keep."},
                "case_cost":{"type":["number","null"]},
                "deposit":{"type":["integer","null"],"description":"Deposit code from get_codes; 0 = none."},
                "levy":{"type":["integer","null"],"description":"Fee/levy code from get_codes; 0 = none."},
                "on_hand":{"type":["number","null"]},
                "min":{"type":["number","null"]},
                "max":{"type":["number","null"]},
                "reason":{"type":"string","description":"Short reason in the owner's words, e.g. 'new cost from Pepsi invoice'."}
              },"required":["upc","price","for","case_cost","deposit","levy","on_hand","min","max","reason"],"additionalProperties":false}}
            },"required":["rows"],"additionalProperties":false}
            """),
        new(RunDryRun,
            "Run a dry run of the whole Pending changes list: the engine types each change into eStream and then exits WITHOUT saving, " +
            "to check it would work. Takes about 10-20 seconds per item, and the owner must not touch the mouse or keyboard meanwhile. " +
            "Only call it when the owner asks for a dry run or agrees to one. Returns each row's result.",
            """
            {"type":"object","properties":{},"required":[],"additionalProperties":false}
            """),
        new(StockReport,
            "Stock report: list ALL items with negative stock, zero stock, stock below their minimum, or stock above their re-order max. " +
            "Read-only. The full list goes to the Reports tab (the owner can export it to CSV or print it); you get the totals and the " +
            "first rows (worst first). Use it for questions like 'which items are negative?' or 'what's below min?'.",
            """
            {"type":"object","properties":{
              "filter":{"type":"string","enum":["negative","zero","below_min","above_max"],
                "description":"negative: on hand below 0. zero: on hand exactly 0. below_min: a minimum is set and on hand is below it. above_max: a re-order max is set and on hand is above it."},
              "include_inactive":{"type":"boolean","description":"true to include inactive items. Use false unless the owner asks for them."}
            },"required":["filter","include_inactive"],"additionalProperties":false}
            """, Strict: false),
        new(StoreReport,
            "Store reports, read-only. report: suggested_order (what to order from one vendor, using eStream's own suggested-order math), " +
            "vendors (list vendors), margins (items below cost, at cost, with no cost or no price, or a suspicious 1 x 1 case size), " +
            "margins_with_targets (the same plus items under their category's target GP%), stock_value (stock value at cost and retail by category), " +
            "mix_match_groups, specials (special events on now / coming), price_batches (pending price batches and which apply by themselves), " +
            "estream_label_flags (items eStream has flagged for a new shelf label), shelf_labels (prices saved by this app that need new shelf labels). " +
            "The full list goes to the Reports tab; you get totals and the first rows. Nothing is ordered or changed.",
            """
            {"type":"object","properties":{
              "report":{"type":"string","enum":["suggested_order","vendors","margins","margins_with_targets","stock_value","mix_match_groups","specials","price_batches","estream_label_flags","shelf_labels"]},
              "vendor":{"type":"string","description":"suggested_order only: vendor number or part of its name. Empty string otherwise."},
              "method":{"type":"string","enum":["reorder_quantity","top_up_to_max","sales_history"],
                "description":"suggested_order only. reorder_quantity: order the Re-Order/Max amount when on hand is at or below Min. top_up_to_max: order up to Re-Order/Max when on hand is at or below Min. sales_history: Min + sold per day x days_needed - on hand. Use the owner's choice; default top_up_to_max."},
              "history_days":{"type":"integer","description":"sales_history only: days of sales to look at (e.g. 28). 0 for the default (28)."},
              "days_needed":{"type":"integer","description":"sales_history only: days of stock the order should cover (e.g. 7). 0 for the default (7)."}
            },"required":["report","vendor","method","history_days","days_needed"],"additionalProperties":false}
            """, Strict: false),
        new(ItemSales,
            "One item's sales from the registers, day by day (read-only): units and dollars per day, units per day, and how many days the stock on hand lasts. " +
            "The day-by-day list goes to the Reports tab.",
            """
            {"type":"object","properties":{
              "upc":{"type":"string","description":"The item's UPC."},
              "days":{"type":"integer","description":"How many days back, 1 to 366 (e.g. 7, 30, 90)."}
            },"required":["upc","days"],"additionalProperties":false}
            """, Strict: false),
        new(StockSheets,
            "Stock sheets in eStream, read-only. With sheet=0: lists the stock sheets (open ones first) with their cut-off date, comment " +
            "and number of items. With a sheet number: that sheet's items (system vs counted, over/short), totals, and checks to look at " +
            "before posting (items counted 0, big differences, negative system stock, a sheet that wasn't counted yet). " +
            "The full list goes to the Reports tab. This never changes or posts a sheet.",
            """
            {"type":"object","properties":{
              "sheet":{"type":"integer","description":"The stock sheet number, or 0 to list the sheets."},
              "include_closed":{"type":"boolean","description":"When listing: true to include closed sheets too. Ignored when a sheet number is given."}
            },"required":["sheet","include_closed"],"additionalProperties":false}
            """, Strict: false),
        new(StockCountPreview,
            "Stock counts: show counted vs system on-hand (over/short) for items. Changes nothing in eStream. " +
            "With add_to_list=true the counts also go into the owner's count list on the Stock counts tab " +
            "(only for counts the owner gave you, e.g. typed or read from a photo they confirmed).",
            """
            {"type":"object","properties":{
              "rows":{"type":"array","items":{"type":"object","properties":{
                "upc":{"type":"string","description":"The item's UPC."},
                "count":{"type":"number","description":"Units counted on the shelf and in the back."}
              },"required":["upc","count"],"additionalProperties":false}},
              "add_to_list":{"type":"boolean","description":"true to put these counts in the count list (replacing earlier counts of the same items)."}
            },"required":["rows","add_to_list"],"additionalProperties":false}
            """),
        new(CreateStockSheet,
            "Stock counts stage 1: make an eStream stock sheet from the owner's count list (Stock counts tab) and import the counts. " +
            "The owner gets a confirmation window first (it uses up a sheet number). Only call it when the owner asks for a stock sheet. " +
            "This does NOT post the sheet: posting (no undo) is only the owner's button on the Stock counts tab.",
            """
            {"type":"object","properties":{
              "comment":{"type":"string","description":"Short sheet comment, e.g. 'Chips aisle'. Up to 60 characters."}
            },"required":["comment"],"additionalProperties":false}
            """),
        new(ReceiptStockSheet,
            "Receipts: add the received stock to eStream through a stock sheet (stage 1). For each receipt line the app works out " +
            "count = on hand now + units received (cases x packs x units; a negative on hand counts as 0), makes a new stock sheet and imports " +
            "those counts. The owner gets a confirmation window first. Only call it when the owner asks to add or receive the stock. " +
            "Lines already on a stock sheet are left out. This does NOT post the sheet: posting (no undo) is only the owner's button on the Stock counts tab.",
            """
            {"type":"object","properties":{
              "lines":{"type":"array","items":{"type":"integer"},"description":"Receipt line numbers; empty for every matched line."}
            },"required":["lines"],"additionalProperties":false}
            """, Strict: false),
        new(QueueShelfLabels,
            "Shelf labels: queue the app's whole 'Shelf labels to print' list in eStream (in the label design from Settings), so the owner " +
            "can print them from eStream's Labels Print. Nothing is printed. The owner gets a confirmation window first. Only call it when the owner asks.",
            """
            {"type":"object","properties":{},"required":[],"additionalProperties":false}
            """, Strict: false),
        new(SavedReceipts,
            "Receipts: search the lines of receipts the owner saved for later (Pending tab) or finished before, by item words, code or UPC. " +
            "Returns each matching line with its receipt, date, quantity, case/unit price and line total as printed, and the item it matched. " +
            "Use it for questions about what was bought or paid, instead of asking for the receipt again. Empty text lists the saved receipts.",
            """
            {"type":"object","properties":{
              "text":{"type":"string","description":"Item words, a code or a UPC, e.g. 'lays 220'; empty to list the receipts."}
            },"required":["text"],"additionalProperties":false}
            """, Strict: false),
        new(ShowReceiptLines,
            "Receipts: put the lines you read from the attached receipt or invoice into the owner's receipt table. " +
            "The app matches each line's code to an eStream item as a UPC AND as the store's Shelf Code (e.g. Costco item numbers), then by name and works out the unit cost itself " +
            "(case price / (packs per case x units per pack)). Copy numbers exactly as printed; NEVER invent or guess a number: " +
            "if any part of a line can't be read for sure, send it with readable=false. Leave out tax, totals, discounts, deposit and eco-fee lines. " +
            "Sending a line number again replaces that line (use this with upc set after the owner picks the item). Changes nothing in eStream.",
            """
            {"type":"object","properties":{
              "source":{"type":"string","description":"Short name for the receipt, e.g. 'Pepsi invoice Nov 18'."},
              "store":{"type":"string","description":"The store or supplier printed on the receipt, short, e.g. 'Superstore', 'Costco', 'Pepsi'; '' if not printed."},
              "date":{"type":"string","description":"The date printed on the receipt as YYYY-MM-DD; '' if not printed or not readable."},
              "lines":{"type":"array","items":{"type":"object","properties":{
                "line":{"type":"integer","description":"Line number on the receipt, from 1, top to bottom."},
                "text":{"type":"string","description":"The item description exactly as printed."},
                "code":{"type":["string","null"],"description":"UPC or vendor item code as printed, or null."},
                "qty":{"type":["number","null"],"description":"How many cases/units were bought, or null."},
                "case_price":{"type":["number","null"],"description":"Price of ONE case or pack before deposits and eco fees (not the line total), or null."},
                "unit_price":{"type":["number","null"],"description":"Price of ONE selling unit, only if the receipt prints it per unit; otherwise null."},
                "line_total":{"type":["number","null"],"description":"The line's extended total as printed, or null."},
                "readable":{"type":"boolean","description":"false if you can't read the line's item or numbers for sure."},
                "upc":{"type":["string","null"],"description":"The eStream UPC the owner confirmed for this line; null if not confirmed."},
                "note":{"type":["string","null"],"description":"Anything the owner should know, e.g. 'price smudged'."},
                "picture":{"type":"integer","description":"N of the [picture N] the line is on; 0 if not known."},
                "top":{"type":"integer","description":"Where the line starts on that picture: percent of its height from the top, 0-100; 0 if not known."},
                "bottom":{"type":"integer","description":"Where the line ends on that picture: percent of its height from the top, 0-100; 0 if not known."}
              },"required":["line","text","code","qty","case_price","unit_price","line_total","readable","upc","note","picture","top","bottom"],"additionalProperties":false}}
            },"required":["source","store","date","lines"],"additionalProperties":false}
            """),
        new(PriceReceiptLines,
            "Receipts: work out proposed selling prices for receipt table lines from their new unit cost, e.g. when the owner says " +
            "'30% markup on the chips'. Shows the prices in the receipt table; it does not stage them. Then tell the owner the prices (Now -> New) " +
            "and stage them with stage_changes (price and case_cost) only once they agree.",
            """
            {"type":"object","properties":{
              "lines":{"type":"array","items":{"type":"integer"},"description":"Receipt line numbers; empty for every matched line."},
              "mode":{"type":"string","enum":["markup","margin"]},
              "pct":{"type":"number","description":"The markup or margin percent, e.g. 30."},
              "apply_endings":{"type":"boolean","description":"true to round up to the store's price endings."}
            },"required":["lines","mode","pct","apply_endings"],"additionalProperties":false}
            """),
    ];
}
