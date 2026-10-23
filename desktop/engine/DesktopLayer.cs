using static StarsDesktop.Native;

namespace StarsDesktop;

// Finds the window that sits behind the desktop icons, so our windows can be its children.
//
// Asking Progman (the desktop) with message 0x052C makes Explorer create a WorkerW window for wallpapers.
// Where that WorkerW lives depends on the Windows build:
//   Windows 11 24H2 and later: it is a child of Progman, below SHELLDLL_DefView (the icons).
//   Earlier builds:            it is a top-level window right after the one that holds SHELLDLL_DefView.
static class DesktopLayer
{
    public static IntPtr Find(out string how)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { how = "Progman не найден"; return IntPtr.Zero; }
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);

        // 24H2+: WorkerW inside Progman
        var inner = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (inner != IntPtr.Zero)
        {
            // keep it under the icons
            var icons = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons != IntPtr.Zero) SetWindowPos(inner, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            how = "WorkerW внутри Progman (Windows 11 24H2+)";
            return inner;
        }

        // earlier builds: the top-level WorkerW after the window holding the icons
        IntPtr worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (worker != IntPtr.Zero) { how = "отдельный WorkerW"; return worker; }

        // last resort: Progman itself (the icons stay on top of our window as its later sibling)
        how = "Progman (запасной вариант)";
        return progman;
    }

    // Makes Explorer repaint the normal wallpaper after we leave.
    public static void RestoreWallpaper()
    {
        var sb = new System.Text.StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, sb.ToString(), 0);
    }
}
