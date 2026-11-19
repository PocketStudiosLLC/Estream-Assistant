using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Changes;

namespace EstreamAssistant.App.ViewModels;

/// <summary>History tab (F7): past dry runs and saves, read-only.</summary>
public sealed partial class HistoryViewModel(HistoryService history) : ObservableObject
{
    private IReadOnlyList<HistoryRun> _all = [];

    public ObservableCollection<HistoryRun> Runs { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _showDryRuns = true;
    [ObservableProperty] private HistoryRun? _selected;
    [ObservableProperty] private string _logExcerpt = "";
    [ObservableProperty] private string _countText = "";

    partial void OnFilterChanged(string value) => Apply();
    partial void OnShowDryRunsChanged(bool value) => Apply();
    partial void OnSelectedChanged(HistoryRun? value) => LogExcerpt = value is null ? "" : history.LogExcerpt(value);

    [RelayCommand]
    public async Task Refresh()
    {
        var keep = Selected?.Path;
        _all = await Task.Run(history.Load);
        Apply();
        Selected = Runs.FirstOrDefault(r => r.Path == keep) ?? Runs.FirstOrDefault();
    }

    private void Apply()
    {
        Runs.Clear();
        foreach (var r in _all.Where(r => (ShowDryRuns || !r.IsDryRun) && r.Matches(Filter))) Runs.Add(r);
        CountText = _all.Count == 0
            ? "No runs yet. Dry runs and saves show up here."
            : $"{Runs.Count} of {_all.Count} runs";
    }
}
