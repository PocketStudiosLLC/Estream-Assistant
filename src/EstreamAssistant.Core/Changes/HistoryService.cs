namespace EstreamAssistant.Core.Changes;

/// <summary>One past run: a results CSV from Done\ (spec F7.1), or a save that never finished.</summary>
public sealed record HistoryRun(string Name, string? Path, DateTime Time, bool IsDryRun, IReadOnlyList<EngineResult> Rows, string? Problem = null)
{
    public int Ok => Rows.Count(r => r.MappedStatus is RowStatus.Applied or RowStatus.DryRunOk);
    public int Unverified => Rows.Count(r => r.MappedStatus == RowStatus.Unverified);
    public int Failed => Rows.Count(r => r.MappedStatus is RowStatus.Failed or RowStatus.Unknown);
    public int Skipped => Rows.Count(r => r.MappedStatus == RowStatus.Skipped);
    public bool IsInterrupted => Problem is not null;

    public string Kind => IsInterrupted ? "Unfinished save" : IsDryRun ? "Dry run" : "Save";

    public string Summary => IsInterrupted
        ? Problem!
        : IsDryRun
            ? $"{Ok} OK, {Skipped} skipped, {Failed} failed"
            : $"{Ok} applied, {Unverified} unverified, {Failed} failed, {Skipped} skipped";

    /// <summary>Free-text filter over the file name, UPCs, descriptions, statuses and messages (F7.1).</summary>
    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        bool Has(string s) => s.Contains(f, StringComparison.OrdinalIgnoreCase);
        return Has(Name) || Has(Kind) || Rows.Any(r => Has(r.Upc) || Has(r.Description) || Has(r.Status) || Has(r.Message) || Has(r.Changes));
    }
}

/// <summary>
/// Read-only history (F7.3): results CSVs named &lt;file&gt;_&lt;time&gt;[_DRYRUN]_results.csv, newest first,
/// plus the journals of saves that never finished.
/// </summary>
public sealed class HistoryService(IReadOnlyList<string> resultsFolders, string? journalFolder, string? logsFolder)
{
    public const int MaxRuns = 300;

    public IReadOnlyList<HistoryRun> Load()
    {
        var runs = new List<HistoryRun>();
        foreach (var dir in resultsFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*_results.csv").OrderByDescending(f => f.LastWriteTime).Take(MaxRuns))
            {
                var name = f.Name[..^"_results.csv".Length];
                bool dry = name.Contains("_DRYRUN", StringComparison.OrdinalIgnoreCase);
                try
                {
                    runs.Add(new HistoryRun(name, f.FullName, f.LastWriteTime, dry, ResultsCsv.Parse(File.ReadAllText(f.FullName))));
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                {
                    runs.Add(new HistoryRun(name, f.FullName, f.LastWriteTime, dry, [], "Couldn't read this file: " + ex.Message));
                }
            }
        }

        if (journalFolder is not null && Directory.Exists(journalFolder))
        {
            foreach (var j in new DirectoryInfo(journalFolder).GetFiles("save-interrupted-*.json"))
                runs.Add(new HistoryRun(j.Name, j.FullName, j.LastWriteTime, false, [],
                    "This save started but the app closed before it finished. Check these items in eStream; nothing was re-applied."));
        }

        return runs.OrderByDescending(r => r.Time).Take(MaxRuns).ToList();
    }

    /// <summary>F7.2: lines of Logs\&lt;yyyy-MM-dd&gt;.log that mention this run (by file name).</summary>
    public string LogExcerpt(HistoryRun run, int maxLines = 60)
    {
        if (logsFolder is null) return "";
        // Engine logs are Logs\<yyyy-MM-dd>.log; the app's own are logs\app-<yyyy-MM-dd>.log.
        var log = new[] { $"{run.Time:yyyy-MM-dd}.log", $"app-{run.Time:yyyy-MM-dd}.log" }
            .Select(n => Path.Combine(logsFolder, n)).FirstOrDefault(File.Exists);
        if (log is null) return $"(No log file for {run.Time:MMM d}.)";
        try
        {
            var key = run.Name.Split("_DRYRUN", StringSplitOptions.None)[0];
            // The engine's results are named <csv base>_<time>; match on the CSV base name.
            var parts = key.Split('_');
            if (parts.Length > 2) key = string.Join('_', parts.Take(2));
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                if (line.Contains(key, StringComparison.OrdinalIgnoreCase)) lines.Add(line);
            return lines.Count == 0 ? "(No log lines mention this run.)" : string.Join("\n", lines.TakeLast(maxLines));
        }
        catch (IOException ex) { return "(Couldn't read the log: " + ex.Message + ")"; }
    }
}
