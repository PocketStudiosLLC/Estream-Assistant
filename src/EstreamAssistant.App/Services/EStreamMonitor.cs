using System.Runtime.InteropServices;
using System.Text;

namespace EstreamAssistant.App.Services;

public enum EStreamState
{
    NotRunning,
    NotMaximized,
    Ready,
}

/// <summary>
/// Watches for the eStream Office main window. Read-only: it never sends input.
/// The main window is found by title (Plan.txt sec. 6: Process.MainWindowHandle
/// can point at a popup).
/// </summary>
public sealed class EStreamMonitor
{
    public const string MainWindowTitle = "eStream System 5 - Office";

    public EStreamState Check()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var title = GetTitle(hWnd);
            if (title.StartsWith(MainWindowTitle, StringComparison.Ordinal))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return EStreamState.NotRunning;
        return IsZoomed(found) ? EStreamState.Ready : EStreamState.NotMaximized;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);
}
