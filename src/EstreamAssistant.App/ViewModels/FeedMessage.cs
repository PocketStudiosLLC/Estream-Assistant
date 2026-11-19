using CommunityToolkit.Mvvm.ComponentModel;

namespace EstreamAssistant.App.ViewModels;

public enum StatusLevel
{
    Unknown,
    Ok,
    Warn,
    Bad,
}

public enum FeedKind
{
    /// <summary>What the owner typed or scanned.</summary>
    Owner,
    /// <summary>Claude's replies.</summary>
    Assistant,
    /// <summary>Messages from the app itself (lookups, status).</summary>
    App,
    Error,
}

/// <summary>
/// A numbered choice shown as a button (never guess the item).
/// <paramref name="ForClaude"/>: the choice came from Claude's reply, so picking it
/// answers Claude instead of doing a local lookup.
/// </summary>
public sealed record FeedChoice(int Number, string Label, string Detail, long? Upc, bool ForClaude = false);

/// <summary>One message in the chat/activity feed. Assistant messages fill in while streaming.</summary>
public sealed partial class FeedMessage : ObservableObject
{
    public FeedMessage(FeedKind kind, string text, IReadOnlyList<FeedChoice>? choices = null)
    {
        Kind = kind;
        _text = text;
        _choices = choices ?? [];
        Time = DateTime.Now;
    }

    public FeedKind Kind { get; }
    public DateTime Time { get; }
    public string TimeText => Time.ToString("h:mm tt");

    [ObservableProperty] private string _text;

    /// <summary>Muted line under the text, e.g. "Searching for "lays"…" while Claude works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChoices))]
    private IReadOnlyList<FeedChoice> _choices;

    public bool HasChoices => Choices.Count > 0;
    public bool HasStatus => Status.Length > 0;
}
