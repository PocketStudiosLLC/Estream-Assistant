namespace EstreamAssistant.Core.Scanning;

/// <summary>
/// Detects USB barcode scanner bursts (Plan.txt sec. 11). The scanner is a
/// keyboard: it types 6-14 digits very fast (&lt; ~40 ms apart) and presses Enter.
/// UI-agnostic: feed it characters and the Enter key with timestamps in ms
/// (WPF InputEventArgs.Timestamp).
/// </summary>
public sealed class ScanDetector
{
    private readonly System.Text.StringBuilder _buffer = new();
    private long _lastTimestamp;

    public int MaxInterKeyMs { get; set; } = 40;
    public int MinDigits { get; set; } = 6;
    public int MaxDigits { get; set; } = 14;

    /// <summary>Digits collected in the current fast burst.</summary>
    public string Pending => _buffer.ToString();

    public void OnText(string text, long timestampMs)
    {
        foreach (var c in text)
        {
            if (c is >= '0' and <= '9')
            {
                if (_buffer.Length > 0 && !IsFast(timestampMs)) _buffer.Clear();
                _buffer.Append(c);
            }
            else
            {
                _buffer.Clear();
            }
            _lastTimestamp = timestampMs;
        }
    }

    /// <summary>
    /// Call on Enter. Returns the scanned code when the burst looks like a scan,
    /// otherwise null. Always resets the burst.
    /// </summary>
    public string? OnEnter(long timestampMs)
    {
        string? result = null;
        if (_buffer.Length >= MinDigits && _buffer.Length <= MaxDigits && IsFast(timestampMs))
            result = _buffer.ToString();
        _buffer.Clear();
        return result;
    }

    public void Reset() => _buffer.Clear();

    private bool IsFast(long timestampMs)
    {
        var gap = timestampMs - _lastTimestamp;
        // A negative gap means the tick counter wrapped; treat it as slow.
        return gap >= 0 && gap <= MaxInterKeyMs;
    }
}
