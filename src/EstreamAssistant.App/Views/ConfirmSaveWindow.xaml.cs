using System.Windows;
using EstreamAssistant.Core.Changes;

namespace EstreamAssistant.App.Views;

/// <summary>
/// U7: lists every row as "Description — Field: Now → New", with "Save N changes" and Cancel.
/// Cancel is the default button (Enter and Esc both cancel).
/// </summary>
public partial class ConfirmSaveWindow : Window
{
    public ConfirmSaveWindow(IReadOnlyList<ChangeRow> rows, bool drivesEStream)
    {
        InitializeComponent();
        int changes = rows.Sum(r => r.Fields.Count);
        Lines.ItemsSource = rows.SelectMany(r => r.Fields.Select(f => $"{r.Description} — {f.Label}: {f.Now} → {f.New}"
                + (r.ForceConfirmed ? "   (large change allowed)" : ""))
            .Concat(r.Warnings.Select(w => "      ⚠ " + w))).ToList();
        int grouped = rows.Count(r => r.Group is not null);
        int viaBatch = rows.Count(r => r.ViaPriceBatch);
        SaveButton.Content = $"Save {changes} change{(changes == 1 ? "" : "s")}";
        Intro.Text = (drivesEStream
                ? "eStream will be changed for real. While it saves, don't touch the mouse or keyboard. "
                : "Mock engine: only the sample data changes. ")
            + $"{rows.Count} item{(rows.Count == 1 ? "" : "s")}. Every value is read back afterwards to check it."
            + (grouped > 0 ? $" {grouped} of them {(grouped == 1 ? "is" : "are")} in a mix match group, so eStream also changes the other items in the group." : "")
            + (viaBatch > 0 ? $" {viaBatch} price change{(viaBatch == 1 ? "" : "s")} go{(viaBatch == 1 ? "es" : "")} into one eStream price batch, which is then applied (no undo; eStream can take a few minutes)." : "");
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
