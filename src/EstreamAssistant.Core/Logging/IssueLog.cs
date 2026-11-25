using System.Globalization;
using System.Text;

namespace EstreamAssistant.Core.Logging;

/// <summary>One problem the app ran into while doing a task.</summary>
public sealed record Issue(DateTime Time, string Area, string Message, string Detail)
{
    public string TimeText => Time.ToString("MMM d, h:mm:ss tt", CultureInfo.GetCultureInfo("en-CA"));
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// The "Bugs" list: errors and problems the app hit while doing a task, kept in a plain
/// text file (issues.txt in the app folder) so the owner can send it in. Newest at the end.
/// Each entry starts with a "=== time | area ===" line; the message and any detail follow.
/// Never put the API key or other secrets in here.
/// </summary>
public sealed class IssueLog
{
    private const string Marker = "=== ";
    private const string DetailMarker = "--- details ---";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly object _gate = new();

    public IssueLog(string file, int maxEntries = 500)
    {
        File = file;
        MaxEntries = maxEntries;
    }

    public string File { get; }
    public int MaxEntries { get; }

    /// <summary>Problems recorded since the owner last opened the Bugs window.</summary>
    public int UnseenCount { get; private set; }

    /// <summary>Raised (on the calling thread) after an issue is added or the list is cleared.</summary>
    public event EventHandler? Changed;

    public void Add(string area, string message, string? detail = null)
    {
        var entry = Format(new Issue(DateTime.Now, Clean(area), message.Trim(), (detail ?? "").Trim()));
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(File);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(File, entry, Encoding.UTF8);
                Trim();
                UnseenCount++;
            }
            catch (IOException) { return; } // the bug list must never crash the app
            catch (UnauthorizedAccessException) { return; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every issue in the file, newest first.</summary>
    public IReadOnlyList<Issue> ReadAll()
    {
        string text;
        lock (_gate)
        {
            try { text = System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File) : ""; }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }
        var list = Parse(text);
        list.Reverse();
        return list;
    }

    public void MarkSeen()
    {
        UnseenCount = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { if (System.IO.File.Exists(File)) System.IO.File.WriteAllText(File, ""); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            UnseenCount = 0;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string Format(Issue i)
    {
        var sb = new StringBuilder();
        sb.Append(Marker).Append(i.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append(" | ").Append(i.Area).AppendLine(" ===");
        sb.AppendLine(Escape(i.Message));
        if (i.Detail.Length > 0) sb.AppendLine(DetailMarker).AppendLine(Escape(i.Detail));
        sb.AppendLine();
        return sb.ToString();
    }

    internal static List<Issue> Parse(string text)
    {
        var result = new List<Issue>();
        DateTime time = default;
        string area = "";
        var message = new StringBuilder();
        var detail = new StringBuilder();
        bool inEntry = false, inDetail = false;

        void Flush()
        {
            if (inEntry) result.Add(new Issue(time, area, message.ToString().Trim(), detail.ToString().Trim()));
            message.Clear();
            detail.Clear();
            inDetail = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(Marker, StringComparison.Ordinal) && line.EndsWith(" ===", StringComparison.Ordinal) && line.Length > 8)
            {
                Flush();
                var head = line[Marker.Length..^4];
                var bar = head.IndexOf(" | ", StringComparison.Ordinal);
                var when = bar >= 0 ? head[..bar] : head;
                area = bar >= 0 ? head[(bar + 3)..] : "";
                inEntry = DateTime.TryParseExact(when, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
                continue;
            }
            if (!inEntry) continue;
            if (!inDetail && line == DetailMarker) { inDetail = true; continue; }
            (inDetail ? detail : message).AppendLine(line);
        }
        Flush();
        return result;
    }

    /// <summary>Keeps the file to the newest <see cref="MaxEntries"/> entries. Called under the lock.</summary>
    private void Trim()
    {
        var info = new FileInfo(File);
        if (!info.Exists || info.Length < 256 * 1024) return;
        var all = Parse(System.IO.File.ReadAllText(File));
        if (all.Count <= MaxEntries) return;
        System.IO.File.WriteAllText(File, string.Concat(all.Skip(all.Count - MaxEntries).Select(Format)), Encoding.UTF8);
    }

    private static string Clean(string area) => string.IsNullOrWhiteSpace(area) ? "App" : area.Replace("===", "=").Replace('\n', ' ').Trim();

    /// <summary>A line that looks like an entry header would split the entry when read back.</summary>
    private static string Escape(string s) => string.Join('\n', s.Replace("\r", "").Split('\n')
        .Select(l => l.StartsWith(Marker, StringComparison.Ordinal) || l == DetailMarker ? " " + l : l));
}
