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
    string page = "";                          // the page loaded now (solar system or Milky Way)
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

    public async Task StartAsync(CoreWebView2Environment env, string webRoot)
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
                    Log.Write($"page ready on {Screen.DeviceName} {Screen.Bounds}: {e.WebMessageAsJson}");
                    ready = true; sentPause = null;
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
        page = Monitor.MilkyWay ? "milkyway" : "solar";
        core.Navigate(Monitor.Url);
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

    public bool IsAttachedTo(IntPtr layer) => IsHandleCreated && GetParent(Handle) == layer && IsWindow(layer);

    public void Post(string json)
    {
        if (ready && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json);
    }

    // New choice for this screen: the solar-system page switches sky, view and panel live; the Milky Way is another page.
    // Returns true when the page is reloaded (it will say "ready" again).
    public bool Apply(MonitorSettings m)
    {
        Monitor = m;
        var want = m.MilkyWay ? "milkyway" : "solar";
        if (want == page || web.CoreWebView2 == null) return false;
        page = want; ready = false;
        web.CoreWebView2.Navigate(m.Url);
        return true;
    }

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
