using System.Text.Json;

namespace EstreamAssistant.Core.Claude;

/// <summary>
/// The tools Claude may call (spec 7.3). Every schema is strict (additionalProperties false,
/// all properties required; optional values are nullable).
/// There is deliberately NO apply/save tool and NO post-stock-sheet tool: only the owner's
/// buttons can do those (spec S3). stage_changes only fills the pending list and run_dry_run
/// only runs a dry run (eStream's Exit throws the typed values away). create_stock_sheet always
/// shows the owner an in-app confirmation first; posting a sheet is a button only.
/// </summary>
public static class ChatTools
{
    public const string LookupItem = "lookup_item";
    public const string FindItems = "find_items";
    public const string GetCodes = "get_codes";
    public const string CalcPrice = "calc_price";
    public const string StageChanges = "stage_changes";
    public const string RunDryRun = "run_dry_run";
    public const string StockCountPreview = "stock_count_preview";
    public const string CreateStockSheet = "create_stock_sheet";

    public sealed record Definition(string Name, string Description, string SchemaJson)
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
        new(GetCodes,
            "List eStream's deposit codes and fee/levy codes with their dollar amounts.",
            """
            {"type":"object","properties":{},"required":[],"additionalProperties":false}
            """),
        new(CalcPrice,
            "Calculate a selling price from a unit cost. ALWAYS use this for price math; never do the arithmetic yourself. " +
            "mode 'markup': price = cost x (1 + pct/100). mode 'margin': price = cost / (1 - pct/100). " +
            "Returns the exact price, the price rounded to the cent, the final price (after the store's price-ending rule if apply_endings is true) and the GM%.",
            """
            {"type":"object","properties":{
              "cost":{"type":"number","description":"Unit cost in dollars, excluding deposit and eco fees."},
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
    ];
}
