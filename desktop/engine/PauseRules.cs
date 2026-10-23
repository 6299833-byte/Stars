using System.Diagnostics;
using Microsoft.Win32;
using static StarsDesktop.Native;

namespace StarsDesktop;

// Decides when the wallpaper should stop drawing: per monitor for fullscreen (and optionally maximised) windows,
// everywhere for the lock screen, remote sessions, battery, chosen apps or the tray's Pause.
sealed class PauseRules : IDisposable
{
    bool locked;
    public bool UserPaused { get; set; }
    static readonly HashSet<string> ShellClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow" };

    public PauseRules()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
    }

    // Reason the whole desktop is paused, or null.
    public string? Global(Settings s)
    {
        if (UserPaused) return "пауза";
        if (locked) return "экран заблокирован";
        if (s.PauseRemote && SystemInformation.TerminalServerSession) return "удалённый сеанс";
        if (s.PauseBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) return "работа от батареи";
        if (s.PauseApps.Count > 0)
            foreach (var name in s.PauseApps)
            {
                var n = Path.GetFileNameWithoutExtension(name);
                Process[] ps = Array.Empty<Process>();
                try { ps = Process.GetProcessesByName(n); }
                catch { }
                bool running = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (running) return n;
            }
        return null;
    }

    // Is this monitor covered by the foreground window?
    public bool Covered(Settings s, Screen screen)
    {
        if (!s.PauseFullscreen && !s.PauseMaximised) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsWindowVisible(fg) || IsIconic(fg) || IsCloaked(fg)) return false;
        if (ShellClasses.Contains(ClassOf(fg))) return false;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return false;
        var r = FrameOf(fg);
        var b = screen.Bounds;
        bool onThis = Screen.FromHandle(fg).DeviceName == screen.DeviceName;
        if (!onThis) return false;
        bool fullscreen = r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        if (s.PauseFullscreen && fullscreen) return true;
        return s.PauseMaximised && IsZoomed(fg);
    }

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;
}
