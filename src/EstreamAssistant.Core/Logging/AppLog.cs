namespace EstreamAssistant.Core.Logging;

/// <summary>
/// Simple daily app log (logs\app-yyyy-MM-dd.log). Keeps 90 days.
/// Never log the API key or other secrets. Separate from the engine's own
/// C:\eStreamAutomation\Logs, which the app leaves untouched.
/// </summary>
public sealed class AppLog
{
    private readonly string _folder;
    private readonly object _gate = new();

    public AppLog(string folder, int keepDays = 90)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        Prune(keepDays);
    }

    public string CurrentFile => Path.Combine(_folder, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(CurrentFile, line); }
            catch (IOException) { /* logging must never crash the app */ }
        }
    }

    private void Prune(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(_folder, "app-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
