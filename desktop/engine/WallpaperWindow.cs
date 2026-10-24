using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static StarsDesktop.Native;

namespace StarsDesktop;

// One borderless window per monitor, made a child of the desktop layer, showing the page in WebView2.
sealed class WallpaperWindow : Form
{
    public Screen Screen { get; }
    public int Index { get; }                  // position from the left, 0-based
    public MonitorSettings Monitor { get; private set; }
    string page = "";                          // the page loaded now: "solar" or "milkyway", plus the quality
    public string Gpu { get; private set; } = "";
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    bool ready;            // the page said {type:'ready'}
    bool? sentPause;
    public event Action<WallpaperWindow>? PageReady;

    public WallpaperWindow(Screen screen, int index, MonitorSettings monitor)
    {
        Screen = screen;
        Index = index;
        Monitor = monitor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.Black;
        Text = "Stars wallpaper";
        Controls.Add(web);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);   // no Alt+Tab, never takes focus
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public async Task StartAsync(CoreWebView2Environment env, string webRoot, string quality)
    {
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        var st = core.Settings;
        st.AreDefaultContextMenusEnabled = false;
        st.AreDevToolsEnabled = false;
        st.IsZoomControlEnabled = false;
        st.IsStatusBarEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        st.IsPinchZoomEnabled = false;
        st.IsSwipeNavigationEnabled = false;
        // offline: the page and its images come from the www folder under a private host name
        core.SetVirtualHostNameToFolderMapping("stars.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
                {
                    Log.Write($"page ready on {Screen.DeviceName} {Screen.Bounds} at {ScalePercent()} %: {e.WebMessageAsJson}");   // size × dpr in the message should match
                    ready = true; sentPause = null;
                    if (doc.RootElement.TryGetProperty("gpu", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.String) Gpu = g.GetString() ?? "";
                    PageReady?.Invoke(this);
                }
            }
            catch { }
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"WebView2 process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                BeginInvoke(() => { ready = false; core.Reload(); });
        };
        // keep links on the wallpaper from navigating it away
        core.NewWindowRequested += (_, e) => e.Handled = true;
        page = PageKey(Monitor, quality);
        core.Navigate(Monitor.Url(quality));
    }

    // Moves the window under the desktop icons, covering this monitor.
    public void Attach(IntPtr layer)
    {
        var h = Handle;
        var style = (long)GetWindowLongPtr(h, GWL_STYLE);
        style = (style & ~0xC0000000L & ~0x00CF0000L) | WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;   // no popup, no frame
        SetWindowLongPtr(h, GWL_STYLE, new IntPtr(style));
        SetParent(h, layer);
        // monitor rectangle (physical pixels, we are per-monitor DPI aware) in the layer's client coordinates
        var b = Screen.Bounds;
        var r = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        MapWindowPoints(IntPtr.Zero, layer, ref r, 2);
        // under Progman directly (fallback), stay below the icons
        var after = ClassOf(layer) == "Progman" ? HWND_BOTTOM : IntPtr.Zero;
        SetWindowPos(h, after, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW | (after == IntPtr.Zero ? SWP_NOZORDER : 0));
    }

    // the monitor's own Windows scaling (125, 150, …), for the log: on mixed-DPI setups the page's devicePixelRatio should match it
    int ScalePercent()
    {
        var b = Screen.Bounds;
        var r = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        try { return GetDpiForMonitor(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), 0, out var dx, out _) == 0 ? (int)Math.Round(dx * 100 / 96.0) : 0; }
        catch { return 0; }
    }

    public bool IsAttachedTo(IntPtr layer) => IsHandleCreated && GetParent(Handle) == layer && IsWindow(layer);

    // the monitor this window was made for is gone, or has moved or changed resolution
    public bool ScreenChanged()
    {
        var now = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Screen.DeviceName);
        return now == null || now.Bounds != Screen.Bounds;
    }

    // does the window still cover exactly its monitor (screen coordinates, physical pixels; can be negative)?
    public bool IsPlaced()
    {
        if (!IsHandleCreated || !GetWindowRect(Handle, out var r)) return true;
        var b = Screen.Bounds;
        return r.Left == b.Left && r.Top == b.Top && r.Right == b.Right && r.Bottom == b.Bottom;
    }

    public void Post(string json)
    {
        if (ready && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json);
    }

    // New choice for this screen: the solar-system page switches sky, view and panel live; the Milky Way is another page.
    // Returns true when the page is reloaded (it will say "ready" again).
    public bool Apply(MonitorSettings m, string quality)
    {
        Monitor = m;
        var want = PageKey(m, quality);
        if (want == page || web.CoreWebView2 == null) return false;
        page = want; ready = false;
        web.CoreWebView2.Navigate(m.Url(quality));
        return true;
    }

    // the page has to be reloaded when this changes (particle counts and textures are made at start)
    static string PageKey(MonitorSettings m, string quality) => m.MilkyWay ? "milkyway" : "solar-" + quality;

    // "Screen 2 — in the middle, 2560×1440, main"
    public string Title(int count)
    {
        var where = count == 1 ? "" : Index == 0 ? "слева" : Index == count - 1 ? "справа" : count == 3 ? "по центру" : $"{Index + 1}-й слева";
        var parts = new List<string>();
        if (where.Length > 0) parts.Add(where);
        parts.Add($"{Screen.Bounds.Width}×{Screen.Bounds.Height}");
        if (Screen.Primary) parts.Add("основной");
        return $"Экран {Index + 1} — {string.Join(", ", parts)}";
    }

    public void SetPaused(bool on)
    {
        if (!ready || sentPause == on) return;
        sentPause = on;
        Post(on ? "{\"type\":\"pause\",\"on\":true}" : "{\"type\":\"pause\",\"on\":false}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) web.Dispose();
        base.Dispose(disposing);
    }
}
