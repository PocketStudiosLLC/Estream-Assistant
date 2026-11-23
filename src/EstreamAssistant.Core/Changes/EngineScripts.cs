using System.Globalization;
using System.Text.Json;
using EstreamAssistant.Core.Logging;

namespace EstreamAssistant.Core.Changes;

/// <summary>
/// The last output line of every Phase C script (Import/Apply-EsPriceBatch, Add-EsLabelBatch, New-EsStockSheet):
///   RESULT: {"ok":true,"dryRun":false,"id":10811,"kind":"PriceBatch","message":"...","resultsCsv":"C:\\...\\x_results.csv"}
/// "id" is the new batch / sheet number, or the label design id; null for a dry run.
/// </summary>
public sealed record ScriptResult(bool Ok, bool DryRun, int? Id, string Kind, string Message, string? ResultsCsv)
{
    public const string Prefix = "RESULT:";

    /// <summary>The LAST "RESULT:" line in the output, or null if there is none or it isn't valid JSON.</summary>
    public static ScriptResult? Parse(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line[Prefix.Length..].Trim());
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? id = null;
            if (r.TryGetProperty("id", out var idv))
            {
                if (idv.ValueKind == JsonValueKind.Number && idv.TryGetInt32(out var n)) id = n;
                else if (idv.ValueKind == JsonValueKind.String && int.TryParse(idv.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s)) id = s;
            }
            var csv = S("resultsCsv");
            return new ScriptResult(B("ok"), B("dryRun"), id, S("kind") ?? "", S("message") ?? "", string.IsNullOrWhiteSpace(csv) ? null : csv);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One finished script run: exit code, output, its RESULT line and the rows of its results CSV.</summary>
public sealed record ScriptRun(int Exit, bool TimedOut, string Output, ScriptResult? Result, IReadOnlyList<EngineResult> Rows, string? ResultsFile)
{
    /// <summary>Exit 2 = refused before touching eStream (bad input, eStream not open, protected record).</summary>
    public bool Refused => Exit == 2;
    public bool Ok => !TimedOut && Exit == 0 && Result is { Ok: true };

    /// <summary>The script's own message, else the last output line.</summary>
    public string Message => Result?.Message is { Length: > 0 } m ? m : ScriptRunner.LastLine(Output);

    /// <summary>
    /// As an <see cref="ExecutorRun"/>: a run-level error only when there are no per-row results to show
    /// (a timeout always counts as an error: the rows may be incomplete).
    /// </summary>
    public ExecutorRun ToExecutorRun(string what)
    {
        string? error = null;
        if (TimedOut) error = $"The {what} took too long and was stopped. Check the eStream screen.";
        else if (Rows.Count == 0 && !Ok)
            error = Result is null
                ? $"The {what} failed (exit code {Exit}). {ScriptRunner.LastLine(Output)}".Trim()
                : $"The {what} {(Refused ? "was refused" : "failed")}: {Message}";
        return new ExecutorRun(Rows, error, Output, ResultsFile) { Id = Result?.Id, Message = Message, Exit = Exit };
    }
}

/// <summary>Runs one engine script that ends with a RESULT line, under the engine lock, and reads its results CSV.</summary>
public static class EngineScripts
{
    public static async Task<ScriptRun> RunAsync(string engineFolder, string scriptName, IReadOnlyList<string> args,
        TimeSpan timeout, CancellationToken ct, AppLog? log = null)
    {
        var script = Path.Combine(engineFolder, scriptName);
        if (!File.Exists(script))
            return new ScriptRun(-1, false, $"Can't find {script}. Check the engine folder in Settings.", null, [], null);
        if (!await ScriptRunner.EngineLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return new ScriptRun(-1, false, "Another eStream operation is already running.", null, [], null);
        try
        {
            var (exit, output, timedOut) = await ScriptRunner.RunAsync(engineFolder, script, args, timeout, ct).ConfigureAwait(false);
            log?.Info($"{scriptName} {string.Join(' ', args)}: exit {exit}{(timedOut ? " (timeout)" : "")}\n{Clip(output, 6000)}");
            var result = ScriptResult.Parse(output);
            IReadOnlyList<EngineResult> rows = [];
            string? file = null;
            if (result?.ResultsCsv is { } f && File.Exists(f))
            {
                try
                {
                    rows = ResultsCsv.Parse(await File.ReadAllTextAsync(f, CancellationToken.None).ConfigureAwait(false));
                    file = f;
                }
                catch (Exception ex) when (ex is IOException or FormatException)
                {
                    log?.Error($"Reading {f} failed", ex);
                }
            }
            return new ScriptRun(exit, timedOut, output, result, rows, file);
        }
        finally
        {
            ScriptRunner.EngineLock.Release();
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
