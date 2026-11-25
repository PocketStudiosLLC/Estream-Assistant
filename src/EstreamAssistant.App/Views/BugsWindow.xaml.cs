using System.Diagnostics;
using System.IO;
using System.Windows;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.App.Views;

/// <summary>The "Bugs" window: problems the app ran into while doing a task, read from issues.txt.</summary>
public partial class BugsWindow : Window
{
    private readonly IssueLog _issues;

    public BugsWindow(IssueLog issues)
    {
        InitializeComponent();
        _issues = issues;
        FilePath.Text = issues.File;
        Load();
        issues.MarkSeen();
    }

    private void Load()
    {
        var all = _issues.ReadAll();
        List.ItemsSource = all;
        Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Explorer with the file selected; the bare folder when there's no file yet.
            var args = File.Exists(_issues.File)
                ? $"/select,\"{_issues.File}\""
                : $"\"{Path.GetDirectoryName(_issues.File)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open File Explorer: " + ex.Message + "\n\nThe file is at:\n" + _issues.File,
                "eStream Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear every problem from the list and from issues.txt?", "eStream Assistant",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _issues.Clear();
        Load();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
