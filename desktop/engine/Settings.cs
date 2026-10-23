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
    [JsonPropertyName("pause_fullscreen")] public bool PauseFullscreen { get; set; } = true;
    [JsonPropertyName("pause_maximised")] public bool PauseMaximised { get; set; } = false;
    [JsonPropertyName("pause_battery")] public bool PauseBattery { get; set; } = true;
    [JsonPropertyName("pause_remote")] public bool PauseRemote { get; set; } = true;
    [JsonPropertyName("pause_apps")] public List<string> PauseApps { get; set; } = new();   // exe names, e.g. "obs64.exe"
    [JsonPropertyName("show_panel")] public bool ShowPanel { get; set; } = true;
    [JsonPropertyName("autostart")] public bool Autostart { get; set; } = true;
    [JsonPropertyName("web_root")] public string WebRoot { get; set; } = "";
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
