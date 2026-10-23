using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarsDesktop;

// config.json next to the exe; the same file stars_helper.py reads (it ignores the keys it does not know).
sealed class Settings
{
    [JsonPropertyName("ics_url")] public string IcsUrl { get; set; } = "";
    [JsonPropertyName("show_text")] public bool ShowText { get; set; } = true;
    [JsonPropertyName("apps_only")] public List<string> AppsOnly { get; set; } = new();
    [JsonPropertyName("apps_hide")] public List<string> AppsHide { get; set; } = new() { "windows" };
    [JsonPropertyName("max_notifications")] public int MaxNotifications { get; set; } = 12;
    [JsonPropertyName("notification_hours")] public double NotificationHours { get; set; } = 12;
    [JsonPropertyName("days_ahead")] public int DaysAhead { get; set; } = 7;
    [JsonPropertyName("fps")] public int Fps { get; set; } = 30;
    [JsonPropertyName("quality")] public string Quality { get; set; } = "normal";   // normal | ultra (strong graphics card)
    [JsonPropertyName("pause_fullscreen")] public bool PauseFullscreen { get; set; } = true;
    [JsonPropertyName("pause_maximised")] public bool PauseMaximised { get; set; } = false;
    [JsonPropertyName("pause_battery")] public bool PauseBattery { get; set; } = true;
    [JsonPropertyName("pause_remote")] public bool PauseRemote { get; set; } = true;
    [JsonPropertyName("pause_apps")] public List<string> PauseApps { get; set; } = new();   // exe names, e.g. "obs64.exe"
    [JsonPropertyName("show_panel")] public bool ShowPanel { get; set; } = true;
    [JsonPropertyName("autostart")] public bool Autostart { get; set; } = true;
    [JsonPropertyName("web_root")] public string WebRoot { get; set; } = "";
    // one entry per monitor, counted from the left; missing entries get a different sky each
    [JsonPropertyName("monitors")] public List<MonitorSettings> Monitors { get; set; } = new();

    // monitors from left to right (then top to bottom), the order the "monitors" list uses
    public static Screen[] Ordered() =>
        Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();

    public MonitorSettings For(int index, Screen screen)
    {
        while (Monitors.Count <= index)
        {
            int i = Monitors.Count;
            Monitors.Add(new MonitorSettings { Scene = MonitorSettings.Scenes[i % MonitorSettings.Scenes.Length].Key, Panel = null });
        }
        var m = Monitors[index];
        m.Panel ??= screen.Primary;   // by default the clock and feed are on the main screen only
        return m;
    }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }   // keeps "port" and other helper keys

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir => AppContext.BaseDirectory;
    public static string PathOf => System.IO.Path.Combine(Dir, "config.json");

    public static Settings Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(PathOf)) { var s = new Settings(); s.Save(); return s; }
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf), Json) ?? new Settings();
        }
        catch (Exception e)
        {
            error = "config.json: " + e.Message;   // a broken file must not stop the wallpaper
            return new Settings();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(PathOf, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { Log.Write("save config: " + e.Message); }
    }

    // www/ next to the exe, or the repo's build when running from the source tree
    public string FindWebRoot()
    {
        foreach (var p in new[] { WebRoot, System.IO.Path.Combine(Dir, "www"), System.IO.Path.Combine(Dir, "..", "www"),
                                  System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, "..", "..", "..", "..", "..", "..", "www")) })
            if (!string.IsNullOrEmpty(p) && File.Exists(System.IO.Path.Combine(p, "index.html"))) return System.IO.Path.GetFullPath(p);
        return System.IO.Path.Combine(Dir, "www");
    }
}

sealed class MonitorSettings
{
    [JsonPropertyName("scene")] public string Scene { get; set; } = "cluster";   // cluster | nebula | field | milkyway
    [JsonPropertyName("view")] public string View { get; set; } = "tour";         // tour | overview | sun | mercury … neptune | far
    [JsonPropertyName("panel")] public bool? Panel { get; set; }                 // clock, meetings, messages

    public static readonly (string Key, string Name)[] Scenes =
    {
        ("cluster", "Скопление галактик Девы"),
        ("nebula", "Туманности Лагуна и Трёхраздельная"),
        ("field", "Rubin’s Cosmos (тысячи галактик)"),
        ("milkyway", "Млечный Путь (вся галактика)"),
    };

    public static readonly (string Key, string Name)[] Views =
    {
        ("tour", "Облёт планет"), ("overview", "Вся система"), ("sun", "Солнце"), ("mercury", "Меркурий"), ("venus", "Венера"),
        ("earth", "Земля"), ("mars", "Марс"), ("jupiter", "Юпитер"), ("saturn", "Сатурн"), ("uranus", "Уран"),
        ("neptune", "Нептун"), ("far", "За пределами: соседние звёзды"),
    };

    public bool MilkyWay => Scene == "milkyway";

    public string Url(string quality) => MilkyWay
        ? "https://stars.local/milky-way.html#wallpaper"
        : $"https://stars.local/index.html#wallpaper&sky={Uri.EscapeDataString(Scene)}&view={Uri.EscapeDataString(View)}&panel={(Panel != false ? 1 : 0)}"
          + (quality == "ultra" ? "&q=ultra" : "");
}

static class Log
{
    static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarsDesktop", "log.txt");
    static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static string Folder => System.IO.Path.GetDirectoryName(FilePath)!;
}
