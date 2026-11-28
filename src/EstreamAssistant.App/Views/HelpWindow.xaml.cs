using System.Windows;
using System.Windows.Controls;
using EstreamAssistant.Core.Help;

namespace EstreamAssistant.App.Views;

/// <summary>One box in the help window: what it does, in plain English, and things to ask.</summary>
public sealed record HelpSection(string Title, string Text, IReadOnlyList<string> Examples)
{
    /// <summary>Which help tab it's on, shown on search results.</summary>
    public string Group { get; init; } = HelpWindow.WhatYouCanDo;
    public bool HasExamples => Examples.Count > 0;
}

/// <summary>
/// The "?" help window: a short plain-English guide to every feature, with clickable example questions, what each
/// tab of the app is for, and a search over both.
/// </summary>
public partial class HelpWindow : Window
{
    public const string WhatYouCanDo = "What you can do", TheTabs = "The tabs";
    private readonly Action<string> _useExample;

    /// <summary>The app's tabs and the words it uses, in very simple terms.</summary>
    public static IReadOnlyList<HelpSection> TabSections { get; } = new HelpSection[]
    {
        new("Assistant tab",
            "The main screen. Talk to Claude here: type a question in the chat box, or add a photo or PDF of a receipt. " +
            "Scan or type in the box at the very top to look up an item. Price and cost changes wait in Pending changes " +
            "until you press SAVE.",
            []),
        new("Pending tab",
            "Receipts you saved for later. Open one when you have the goods. The app asks one thing at a time " +
            "(scan this barcode, pick this item) and highlights that row on the receipt. Then press Make stock sheet. " +
            "Press Done to put the receipt away, or Delete to throw it out. This tab doesn't use Claude.",
            []),
        new("Stock counts tab",
            "For counting what's on the shelf. Scan an item, type how many you counted, press Enter. It shows if you have " +
            "more or less than eStream thinks. Then make a stock sheet, and post it to change the stock in eStream.",
            []),
        new("Reports tab",
            "Lists of items to check, like items with negative stock, no stock, or below their minimum. You can print them " +
            "or save them as a CSV file. Looking at a report changes nothing.",
            []),
        new("History tab",
            "Everything the app has saved into eStream, and its practice runs, with the date and time. Look here to see " +
            "what changed and when.",
            []),
        new("Settings tab",
            "The app's setup: the Claude key, how prices end (like .99), which Claude to use, and the eStream connection. " +
            "You won't need it day to day.",
            []),
        new("The lights and buttons at the top",
            "The dots at the top right show if eStream and Claude are connected: green is good. ? opens this help. " +
            "Bugs lists problems the app ran into, to send in.",
            []),
        new("What's a stock sheet?",
            "eStream's list of how many of each item you have. Making one changes nothing yet. Posting it (on the Stock counts " +
            "tab) puts the new numbers into eStream's stock.",
            ["Make a stock sheet from my counts", "Show me the open stock sheets"]),
        new("What's SAVE?",
            "Nothing Claude suggests changes eStream by itself. New prices and costs wait in Pending changes on the Assistant tab. " +
            "Check them, then press SAVE to put them in eStream.",
            []),
    }.Select(x => x with { Group = TheTabs }).ToList();

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
        new("Run (a dry run)",
            "The Run button under Pending changes types the changes into eStream to check they'd work, then throws them away: nothing is saved. " +
            "SAVE does the same check by itself, then asks you before putting the changes in eStream. " +
            "Don't touch the mouse or keyboard while it runs.",
            ["Do a dry run of the pending changes"]),
        new("Receipts and invoices",
            "Drop, paste or add a photo or PDF of a supplier receipt. Claude reads each line, matches it to your items and shows new " +
            "unit costs. You can then update costs or set prices from it.",
            ["Update the costs from this receipt", "Put a 30% markup on the chips from this receipt"]),
        new("Save a receipt for later",
            "When a receipt has rows you need to check (a barcode to scan, a quantity to read off the goods), press \"Save for later\" " +
            "on the receipt. It moves to the Pending tab with its pictures. Open it there when you have the goods: the app asks for " +
            "what's missing one row at a time (\"Scan the barcode for row 4 on the receipt\"), with that row highlighted on the receipt, " +
            "then makes the stock sheet. Press Done when it's finished, or Delete if you won't use it. The Pending tab doesn't use Claude. Claude can still answer questions about saved receipts, " +
            "like what you paid, without the picture.",
            ["What did we pay for Lays on the last receipt?"]),
        new("Stock counts",
            "Tell Claude what you counted (or send a photo of a count sheet) to see over/short against eStream. " +
            "The Stock counts tab keeps the list and can make an eStream stock sheet from it.",
            ["I counted 24 Coke 2L and 10 Pepsi 2L", "Make a stock sheet from my counts"]),
        new("Stock reports",
            "Items with negative stock, zero stock, below their minimum or above their max. The full list is on the Reports tab, " +
            "where you can export it to CSV or print it.",
            ["Which items are negative?", "What's below minimum?", "What's out of stock?"]),
        new("eStream stock sheets",
            "See the stock sheets in eStream and, for one sheet, what's over or short before you post it. Claude only looks: " +
            "you post a sheet yourself, on the Stock counts tab.",
            ["Show me the open stock sheets", "What's short on stock sheet 12?"]),
        new("Deposit and eco-fee codes",
            "List eStream's deposit and levy codes with their amounts.",
            ["What are the deposit codes?"]),
        new("History and settings",
            "The History tab shows what was saved and when. The Bugs button at the top right lists problems the app ran into, " +
            "and opens the issues.txt file to send in. Settings has your API key, price endings, the Claude models and the name refresh. " +
            "\"New chat\" starts over when Claude gets mixed up.",
            []),
    ];

    public HelpWindow(Action<string> useExample)
    {
        InitializeComponent();
        _useExample = useExample;
        Sections.ItemsSource = AllSections;
        Tabs.ItemsSource = TabSections;
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Every box on both help tabs that matches the search, best first.</summary>
    public static IReadOnlyList<HelpSection> Search(string query) =>
        HelpSearch.Find(AllSections.Concat(TabSections), query, s => s.Title, s => s.Text + " " + string.Join(" ", s.Examples));

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        bool searching = query.Length > 0;
        var found = searching ? Search(query) : [];
        Results.ItemsSource = found;
        HelpTabs.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        ResultsPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = searching && found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearch.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
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
