using System.Windows;
using System.Windows.Controls;

namespace EstreamAssistant.App.Views;

/// <summary>
/// A confirmation listing what will happen. Cancel is the default button (Enter and Esc cancel).
/// With <c>mustType</c>, the OK button only enables once the owner has typed that exact text (F6.4).
/// </summary>
public partial class ConfirmActionWindow : Window
{
    private readonly string? _mustType;

    public ConfirmActionWindow(string title, string intro, IReadOnlyList<string> lines, string okText, string? mustType)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Intro.Text = intro;
        Lines.ItemsSource = lines;
        OkButton.Content = okText;
        _mustType = mustType;
        if (mustType is not null)
        {
            TypePanel.Visibility = Visibility.Visible;
            TypeLabel.Text = mustType == AnyNumber ? "Number:" : $"Type {mustType} to confirm:";
            OkButton.IsEnabled = false;
            Loaded += (_, _) => TypeBox.Focus();
        }
    }

    /// <summary>What the owner typed (or "" when nothing had to be typed). Null when cancelled.</summary>
    public string? Typed { get; private set; }

    /// <summary>"*" = any whole number above zero (asking for a number rather than confirming one).</summary>
    private bool Matches(string text) => _mustType == AnyNumber
        ? int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
        : text == _mustType;

    public const string AnyNumber = "*";

    private void TypeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = Matches(TypeBox.Text.Trim());
    }

    /// <summary>Enter in the box confirms only once the typed text matches; otherwise Enter does nothing.</summary>
    private void TypeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (Matches(TypeBox.Text.Trim())) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_mustType is not null && !Matches(TypeBox.Text.Trim())) return;
        Typed = _mustType is null ? "" : TypeBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public static string? Ask(Window owner, string title, string intro, IReadOnlyList<string> lines, string okText, string? mustType)
    {
        var w = new ConfirmActionWindow(title, intro, lines, okText, mustType) { Owner = owner };
        return w.ShowDialog() == true ? w.Typed : null;
    }
}
