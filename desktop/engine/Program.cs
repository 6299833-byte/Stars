using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace StarsDesktop;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, @"Local\StarsDesktop.Wallpaper", out bool first);
        if (!first) return;   // already running: the tray icon is there
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write("UI: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        Application.Run(new Engine());
    }
}

// Owns the tray icon, the wallpaper windows and the timers.
sealed class Engine : ApplicationContext
{
    Settings settings;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem pauseItem, autostartItem, statusItem, screensItem, qualityItem;
    readonly List<WallpaperWindow> windows = new();
    readonly PauseRules pause = new();
    readonly ShellWatcher shell;
    readonly System.Windows.Forms.Timer pauseTimer = new() { Interval = 1500 };
    readonly System.Windows.Forms.Timer notesTimer = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer calendarTimer = new() { Interval = 10 * 60 * 1000 };
    readonly System.Windows.Forms.Timer attachTimer = new() { Interval = 5000 };
    readonly Control ui = new();   // hands work from watcher threads back to the UI thread
    readonly FileSystemWatcher configWatch;
    readonly FileSystemWatcher? notesWatch;
    CoreWebView2Environment? env;
    IntPtr layer;
    FeedMessage feed = new();
    string? configError;
    bool notesBusy, calendarBusy, rebuilding;
    DateTime lastNotes;

    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Engine()
    {
        _ = ui.Handle;
        settings = Settings.Load(out configError);
        if (configError != null) Log.Write(configError);

        statusItem = new ToolStripMenuItem("Запуск…") { Enabled = false };
        screensItem = new ToolStripMenuItem("Экраны") { Enabled = false };
        qualityItem = new ToolStripMenuItem("Качество");
        foreach (var (key, fps, name) in Qualities)
            qualityItem.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SetQuality(key, fps)) { Tag = key + "/" + fps });
        MarkQuality();
        pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => { pause.UserPaused = !pause.UserPaused; pauseItem!.Checked = pause.UserPaused; ApplyPause(); });
        autostartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) => SetAutostart(!autostartItem!.Checked));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem,
            new ToolStripSeparator(),
            screensItem,
            qualityItem,
            pauseItem,
            new ToolStripMenuItem("Обновить встречи и сообщения", null, (_, _) => { _ = RefreshCalendar(); RefreshNotes(); }),
            new ToolStripMenuItem("Перезапустить обои", null, (_, _) => _ = Rebuild()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Настройки (config.json)", null, (_, _) => Open("notepad.exe", Settings.PathOf)),
            new ToolStripMenuItem("Папка с журналом", null, (_, _) => Open("explorer.exe", Log.Folder)),
            autostartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Выход", null, (_, _) => ExitThread()),
        });
        tray = new NotifyIcon { Icon = MakeIcon(), Text = "Солнечная система — обои", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => pauseItem.PerformClick();

        if (settings.Autostart && !AutostartOn()) SetAutostart(true);
        autostartItem.Checked = AutostartOn();

        shell = new ShellWatcher(() => _ = Rebuild());   // explorer.exe restarted
        SystemEvents.DisplaySettingsChanged += OnDisplays;

        configWatch = new FileSystemWatcher(Settings.Dir, "config.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        configWatch.Changed += (_, _) => ui.BeginInvoke(new Action(ReloadSettings));
        configWatch.EnableRaisingEvents = true;
        try
        {
            var dir = Path.GetDirectoryName(Notifications.DbPath)!;
            if (Directory.Exists(dir))
            {
                notesWatch = new FileSystemWatcher(dir, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
                notesWatch.Changed += (_, _) => ui.BeginInvoke(() => { if ((DateTime.Now - lastNotes).TotalSeconds > 1.5) RefreshNotes(); });
                notesWatch.EnableRaisingEvents = true;
            }
        }
        catch (Exception e) { Log.Write("watch notifications: " + e.Message); }

        pauseTimer.Tick += (_, _) => ApplyPause();
        notesTimer.Tick += (_, _) => RefreshNotes();
        calendarTimer.Tick += (_, _) => _ = RefreshCalendar();
        attachTimer.Tick += (_, _) => CheckAttached();

        _ = Start();
    }

    async Task Start()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarsDesktop", "WebView2");
            var opts = new CoreWebView2EnvironmentOptions(
                // the window sits behind the icons, so Chromium must not treat it as hidden and stop drawing
                "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --autoplay-policy=no-user-gesture-required " +
                // draw on the discrete graphics card (Direct3D 11 through ANGLE) rather than the integrated one, also on GPUs Chromium is unsure of
                "--force_high_performance_gpu --ignore-gpu-blocklist --enable-gpu-rasterization --enable-zero-copy");
            env = await CoreWebView2Environment.CreateAsync(null, data, opts);
        }
        catch (Exception e)
        {
            Log.Write("WebView2: " + e);
            statusItem.Text = "Нет WebView2 Runtime";
            tray.ShowBalloonTip(8000, "Солнечная система", "Нужен Microsoft Edge WebView2 Runtime: https://go.microsoft.com/fwlink/p/?LinkId=2124703", ToolTipIcon.Warning);
            return;
        }
        await Rebuild();
        RefreshNotes();
        _ = RefreshCalendar();
        pauseTimer.Start(); notesTimer.Start(); calendarTimer.Start(); attachTimer.Start();
    }

    // (Re)creates one wallpaper window per monitor under the current desktop layer.
    async Task Rebuild()
    {
        if (env == null || rebuilding) return;
        rebuilding = true;
        try
        {
            foreach (var w in windows) { w.Close(); w.Dispose(); }
            windows.Clear();
            layer = DesktopLayer.Find(out var how);
            Log.Write($"desktop layer: {how}, monitors: {Screen.AllScreens.Length}");
            if (layer == IntPtr.Zero) { statusItem.Text = "Рабочий стол не найден"; return; }
            var root = settings.FindWebRoot();
            if (!File.Exists(Path.Combine(root, "index.html")))
            {
                statusItem.Text = "Нет папки www рядом с программой";
                Log.Write("no index.html in " + root);
                return;
            }
            var screens = Settings.Ordered();
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                var w = new WallpaperWindow(screen, i, settings.For(i, screen));
                w.PageReady += OnPageReady;
                w.Show();
                w.Attach(layer);
                windows.Add(w);
                await w.StartAsync(env, root, settings.Quality);
            }
            statusItem.Text = $"Работает · мониторов: {windows.Count}";
            BuildScreensMenu();
            SaveQuietly();   // writes the "monitors" list, so config.json shows one entry per screen
        }
        catch (Exception e)
        {
            Log.Write("rebuild: " + e);
            statusItem.Text = "Ошибка — см. журнал";
        }
        finally { rebuilding = false; }
    }

    string ConfigJson(WallpaperWindow w) => JsonSerializer.Serialize(new
    {
        type = "config", fps = settings.Fps, sky = w.Monitor.Scene, view = w.Monitor.View,
        panel = settings.ShowPanel && w.Monitor.Panel != false,
        ufo = settings.Ufo, meteors = settings.Meteors,
    }, Json);

    // Tray → Экраны → one submenu per monitor: background (which galaxy), view, panel.
    void BuildScreensMenu()
    {
        screensItem.DropDownItems.Clear();
        foreach (var w in windows)
        {
            var item = new ToolStripMenuItem(w.Title(windows.Count));
            var m = w.Monitor;
            var sky = new ToolStripMenuItem("Фон");
            foreach (var (key, name) in MonitorSettings.Scenes)
                sky.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => Change(w, x => x.Scene = key)) { Checked = m.Scene == key });
            var view = new ToolStripMenuItem("Вид") { Enabled = !m.MilkyWay };
            foreach (var (key, name) in MonitorSettings.Views)
                view.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => Change(w, x => x.View = key)) { Checked = m.View == key });
            var panel = new ToolStripMenuItem("Часы, встречи и сообщения", null, (_, _) => Change(w, x => x.Panel = x.Panel == false))
                { Checked = m.Panel != false, Enabled = !m.MilkyWay };
            item.DropDownItems.AddRange(new ToolStripItem[] { sky, view, panel });
            screensItem.DropDownItems.Add(item);
        }
        screensItem.Enabled = windows.Count > 0;
    }

    void Change(WallpaperWindow w, Action<MonitorSettings> edit)
    {
        edit(w.Monitor);
        SaveQuietly();
        if (!w.Apply(w.Monitor, settings.Quality)) w.Post(ConfigJson(w));
        BuildScreensMenu();
    }

    static readonly (string Key, int Fps, string Name)[] Qualities =
    {
        ("normal", 30, "Обычное — 30 кадров/с (ноутбук)"),
        ("normal", 60, "Плавное — 60 кадров/с"),
        ("ultra", 60, "Максимальное — 60 кадров/с, ×1,5 разрешение, ×3 частиц, 2K-текстуры"),
    };

    void MarkQuality()
    {
        foreach (ToolStripMenuItem i in qualityItem.DropDownItems)
            i.Checked = (string?)i.Tag == (settings.Quality == "ultra" ? "ultra" : "normal") + "/" + settings.Fps;
        var gpu = windows.Select(w => w.Gpu).FirstOrDefault(g => g.Length > 0);
        qualityItem.ToolTipText = gpu != null ? "Видеокарта: " + gpu : null;
    }

    void SetQuality(string key, int fps)
    {
        settings.Quality = key; settings.Fps = fps;
        SaveQuietly();
        foreach (var w in windows) if (!w.Apply(w.Monitor, key)) w.Post(ConfigJson(w));
        MarkQuality();
    }

    void SaveQuietly()
    {
        configWatch.EnableRaisingEvents = false;
        settings.Save();
        configWatch.EnableRaisingEvents = true;
    }

    void OnPageReady(WallpaperWindow w)
    {
        MarkQuality();
        w.Post(ConfigJson(w));
        w.Post(JsonSerializer.Serialize(feed, Json));
        ApplyPause();
    }

    void OnDisplays(object? sender, EventArgs e) => ui.BeginInvoke(() => _ = Rebuild());

    // Explorer can recreate the desktop windows without a TaskbarCreated broadcast (e.g. after a theme change).
    void CheckAttached()
    {
        if (rebuilding || windows.Count == 0) return;
        if (windows.Any(w => !w.IsAttachedTo(layer) || w.ScreenChanged()) || windows.Count != Screen.AllScreens.Length) { _ = Rebuild(); return; }
        // Explorer can move or resize the desktop layer after the display change we rebuilt on (a monitor added left of
        // or above the main one moves its origin): put any window that no longer covers its monitor back in place
        foreach (var w in windows)
            if (!w.IsPlaced()) { Log.Write($"re-placing the window on {w.Screen.DeviceName} {w.Screen.Bounds}"); w.Attach(layer); }
    }

    void ApplyPause()
    {
        var all = pause.Global(settings);
        foreach (var w in windows) w.SetPaused(all != null || pause.Covered(settings, w.Screen));
        tray.Text = all != null ? $"Обои на паузе: {all}" : "Солнечная система — обои";
    }

    void RefreshNotes()
    {
        if (notesBusy) return;
        notesBusy = true; lastNotes = DateTime.Now;
        var s = settings;
        Task.Run(() => { var l = Notifications.Read(s, out var e); return (l, e); }).ContinueWith(t =>
        {
            notesBusy = false;
            if (t.IsFaulted) return;
            var (list, err) = t.Result;
            var changed = err != feed.NotificationsError || !list.SequenceEqual(feed.Notifications);
            feed.Notifications = list; feed.NotificationsError = err;
            if (changed) Broadcast();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    async Task RefreshCalendar()
    {
        if (calendarBusy) return;
        calendarBusy = true;
        try
        {
            var (events, err) = await Meetings.ReadAsync(settings);
            if (events != null) feed.Events = events;
            feed.CalendarError = err;
            if (err != null) Log.Write(err);
            Broadcast();
        }
        finally { calendarBusy = false; }
    }

    void Broadcast()
    {
        feed.Now = DateTimeOffset.Now.ToString("o");
        var json = JsonSerializer.Serialize(feed, Json);
        foreach (var w in windows) w.Post(json);
    }

    void ReloadSettings()
    {
        var s = Settings.Load(out var err);
        if (err != null) { Log.Write(err); return; }   // keep the old settings while the file is half-written
        var calendarChanged = s.IcsUrl != settings.IcsUrl || s.DaysAhead != settings.DaysAhead;
        settings = s;
        foreach (var w in windows)
        {
            var m = s.For(w.Index, w.Screen);
            if (!w.Apply(m, s.Quality)) w.Post(ConfigJson(w));
        }
        BuildScreensMenu();
        MarkQuality();
        lastNotes = DateTime.MinValue;
        RefreshNotes();
        if (calendarChanged) _ = RefreshCalendar();
        ApplyPause();
    }

    // ---- autostart (HKCU, no admin) ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    static bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("StarsDesktop") is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("StarsDesktop", $"\"{ExePath}\"");
            else k.DeleteValue("StarsDesktop", false);
            if (settings.Autostart != on) { settings.Autostart = on; SaveQuietly(); }
        }
        catch (Exception e) { Log.Write("autostart: " + e.Message); }
        autostartItem.Checked = AutostartOn();
    }

    static void Open(string exe, string arg)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"\"{arg}\"") { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("open: " + e.Message); }
    }

    // A small glowing planet with a ring, drawn at run time (no icon file to ship).
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var glow = new GraphicsPath();
            glow.AddEllipse(4, 4, 24, 24);
            using var pb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(255, 255, 236, 190), SurroundColors = new[] { Color.FromArgb(0, 255, 170, 60) } };
            g.FillEllipse(pb, 4, 4, 24, 24);
            using var ring = new Pen(Color.FromArgb(230, 150, 200, 255), 2.2f);
            g.DrawEllipse(ring, 2, 12, 28, 8);
            using var core = new SolidBrush(Color.FromArgb(255, 255, 214, 140));
            g.FillEllipse(core, 11, 10, 10, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        pauseTimer.Stop(); notesTimer.Stop(); calendarTimer.Stop(); attachTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplays;
        configWatch.Dispose(); notesWatch?.Dispose();
        foreach (var w in windows) { w.Close(); w.Dispose(); }
        windows.Clear();
        pause.Dispose();
        shell.DestroyHandle();
        tray.Visible = false; tray.Dispose();
        ui.Dispose();
        DesktopLayer.RestoreWallpaper();
        base.ExitThreadCore();
    }
}

// An invisible top-level window that hears the "TaskbarCreated" broadcast Explorer sends when it starts again.
sealed class ShellWatcher : NativeWindow
{
    readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly Action onRestart;

    public ShellWatcher(Action onRestart)
    {
        this.onRestart = onRestart;
        CreateHandle(new CreateParams { Caption = "StarsDesktop.Shell" });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)taskbarCreated) onRestart();
        base.WndProc(ref m);
    }
}
