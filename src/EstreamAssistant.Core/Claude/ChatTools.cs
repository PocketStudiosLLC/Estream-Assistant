using System.Text.Json;

namespace EstreamAssistant.Core.Claude;

/// <summary>
/// The tools Claude may call (Plan.txt sec. 10). Phase 2 = READ-ONLY tools only.
/// Every schema is strict (additionalProperties false, all properties required).
/// There is deliberately NO apply/save tool and NO post-stock-sheet tool: only
/// the owner's buttons can do those (spec S3).
/// </summary>
public static class ChatTools
{
    public const string LookupItem = "lookup_item";
    public const string FindItems = "find_items";
    public const string GetCodes = "get_codes";
    public const string CalcPrice = "calc_price";

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
    ];
}
