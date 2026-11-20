using System.Diagnostics;
using System.Text;

namespace EstreamAssistant.Core.Changes;

/// <summary>
/// Runs one engine script (spec 8.2): powershell.exe -NoProfile -ExecutionPolicy Bypass -File &lt;script&gt; &lt;args&gt;,
/// in the user's session, no console window, stdout/stderr captured, process tree killed on timeout.
/// </summary>
public static class ScriptRunner
{
    /// <summary>UI automation can't run in parallel: every engine operation (changes or stock sheets) takes this lock.</summary>
    public static readonly SemaphoreSlim EngineLock = new(1, 1);

    /// <summary>Spec 11: every engine call is logged with its arguments, exit code and duration. Set at startup.</summary>
    public static Action<string>? Log { get; set; }

    public static async Task<(int Exit, string Output, bool TimedOut)> RunAsync(
        string engineFolder, string scriptPath, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = engineFolder,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }.Concat(args))
            psi.ArgumentList.Add(a);

        var started = Stopwatch.StartNew();
        var argText = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine("ERR: " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Log?.Invoke($"Engine {Path.GetFileName(scriptPath)} {argText} -> {(ct.IsCancellationRequested ? "cancelled" : "TIMED OUT, killed")} after {started.Elapsed.TotalSeconds:0.0} s");
            ct.ThrowIfCancellationRequested();
            lock (output) return (-1, output.ToString(), true);
        }
        Log?.Invoke($"Engine {Path.GetFileName(scriptPath)} {argText} -> exit {p.ExitCode} in {started.Elapsed.TotalSeconds:0.0} s");
        lock (output) return (p.ExitCode, output.ToString(), false);
    }

    public static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
