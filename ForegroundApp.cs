using System.Runtime.InteropServices;
using System.Text;

namespace PcMonitor;

/// <summary>前台窗口检测：进程名 + 窗口标题。</summary>
public static class ForegroundApp
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "iexplore",
        "maxthon", "chromium", "safari", "360se", "360chrome", "qqbrowser", "ucbrowser",
    };

    public static string ProcessName { get; private set; } = "";
    public static string WindowTitle { get; private set; } = "";

    /// <summary>更新前台窗口信息，返回 true=有变化。</summary>
    public static bool Update()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, 256);

        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        string proc = "";
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { }

        bool changed = proc != ProcessName;
        ProcessName = proc;
        WindowTitle = sb.ToString();
        return changed;
    }

    public static bool IsBrowser => BrowserProcesses.Contains(ProcessName);
    public static bool IsFullscreenGame
    {
        get
        {
            // 简单启发式：全屏无边框窗口（RECT 覆盖整个虚拟屏幕）
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || ProcessName == "" ||
                ProcessName == "explorer" || IsBrowser) return false;
            RECT r;
            GetWindowRect(hwnd, out r);
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            return r.Left <= vx && r.Top <= vy &&
                   r.Right >= vx + vw && r.Bottom >= vy + vh;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
}
