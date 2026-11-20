using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstreamAssistant.Core.Changes;

namespace EstreamAssistant.App.ViewModels;

/// <summary>History tab (F7): past dry runs and saves, read-only.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly HistoryService history;

    public HistoryViewModel(HistoryService history)
    {
        this.history = history;
        _period = Periods[0];
        _status = Statuses[0];
    }

    private IReadOnlyList<HistoryRun> _all = [];

    public ObservableCollection<HistoryRun> Runs { get; } = [];

    public sealed record Option<T>(T Value, string Label);

    public IReadOnlyList<Option<HistoryPeriod>> Periods { get; } =
    [
        new(HistoryPeriod.AnyTime, "Any time"), new(HistoryPeriod.Today, "Today"),
        new(HistoryPeriod.Last7Days, "Last 7 days"), new(HistoryPeriod.Last30Days, "Last 30 days"),
    ];

    public IReadOnlyList<Option<HistoryStatusFilter>> Statuses { get; } =
    [
        new(HistoryStatusFilter.All, "All results"), new(HistoryStatusFilter.AllOk, "Everything OK"),
        new(HistoryStatusFilter.Problems, "With problems"),
    ];

    [ObservableProperty] private Option<HistoryPeriod>? _period;
    [ObservableProperty] private Option<HistoryStatusFilter>? _status;
    partial void OnPeriodChanged(Option<HistoryPeriod>? value) => Apply();
    partial void OnStatusChanged(Option<HistoryStatusFilter>? value) => Apply();

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
        var now = DateTime.Now;
        var period = Period?.Value ?? HistoryPeriod.AnyTime;
        var status = Status?.Value ?? HistoryStatusFilter.All;
        foreach (var r in _all.Where(r => (ShowDryRuns || !r.IsDryRun) && r.InPeriod(period, now) && r.HasStatus(status) && r.Matches(Filter)))
            Runs.Add(r);
        CountText = _all.Count == 0
            ? "No runs yet. Dry runs and saves show up here."
            : $"{Runs.Count} of {_all.Count} runs";
    }
}
