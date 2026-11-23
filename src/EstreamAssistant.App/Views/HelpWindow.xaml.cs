using System.Windows;
using System.Windows.Controls;

namespace EstreamAssistant.App.Views;

/// <summary>One feature in the help window: what it does, in plain English, and things to ask.</summary>
public sealed record HelpSection(string Title, string Text, IReadOnlyList<string> Examples);

/// <summary>The "?" help window: a short plain-English guide to every feature, with clickable example questions.</summary>
public partial class HelpWindow : Window
{
    private readonly Action<string> _useExample;

    public static IReadOnlyList<HelpSection> AllSections { get; } =
    [
        new("Look up an item",
            "Scan a barcode or type a UPC or name in the box at the top. The item card shows its price, cost, deposit, stock and margin.",
            ["What's the price of Lays Classic 235g?", "Look up 060410079591"]),
        new("Check stock",
            "Ask how many of something you have. Stock is what eStream says is on hand.",
            ["How many cans of Coke 355ml are in stock?", "How much milk do we have?"]),
        new("List a whole brand",
            "Get every item with a brand or word in its name, with a count.",
            ["Show me all the Lays items", "How many Pepsi items do we carry?"]),
        new("Change prices and costs",
            "Ask for new prices, \"2 for\" deals, case costs, deposits or eco fees. Changes go into Pending changes first; " +
            "check them, then press SAVE to put them in eStream.",
            ["Set Coke 2L to $3.79", "Make Kraft Dinner 2 for $4", "Change the case cost of Pepsi 2L to $21.60"]),
        new("Work out a price",
            "Claude does markup and margin math for you, and can round to your price endings (set in Settings).",
            ["What should I charge with a 30% markup on a $4.29 cost?", "Price Doritos at a 35% margin"]),
        new("Dry run",
            "Before saving, a dry run types the changes into eStream to check they'd work, then throws them away. " +
            "Don't touch the mouse or keyboard while it runs.",
            ["Do a dry run of the pending changes"]),
        new("Receipts and invoices",
            "Drop, paste or add a photo or PDF of a supplier receipt. Claude reads each line, matches it to your items and shows new " +
            "unit costs. You can then update costs or set prices from it.",
            ["Update the costs from this receipt", "Put a 30% markup on the chips from this receipt"]),
        new("Stock counts",
            "Tell Claude what you counted (or send a photo of a count sheet) to see over/short against eStream. " +
            "The Stock counts tab keeps the list and can make an eStream stock sheet from it.",
            ["I counted 24 Coke 2L and 10 Pepsi 2L", "Make a stock sheet from my counts"]),
        new("Stock reports",
            "Items with negative stock, zero stock, below their minimum or above their max. The full list is on the Reports tab, " +
            "where you can export it to CSV or print it.",
            ["Which items are negative?", "What's below minimum?", "What's out of stock?"]),
        new("eStream stock sheets",
            "See the stock sheets in eStream and, for one sheet, what's over or short before you post it. Read-only: posting is done in eStream.",
            ["Show me the open stock sheets", "What's short on stock sheet 12?"]),
        new("Deposit and eco-fee codes",
            "List eStream's deposit and levy codes with their amounts.",
            ["What are the deposit codes?"]),
        new("History and settings",
            "The History tab shows what was saved and when. Settings has your API key, price endings, the Claude models and the name refresh. " +
            "\"New chat\" starts over when Claude gets mixed up.",
            []),
    ];

    public HelpWindow(Action<string> useExample)
    {
        InitializeComponent();
        _useExample = useExample;
        Sections.ItemsSource = AllSections;
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text })
        {
            _useExample(text);
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
