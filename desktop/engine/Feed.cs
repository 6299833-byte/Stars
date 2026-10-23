using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ical.Net.CalendarComponents;
using Microsoft.Data.Sqlite;
using IcalCalendar = Ical.Net.Calendar;

namespace StarsDesktop;

sealed record Meeting(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("where")] string Where,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("all_day")] bool AllDay,
    [property: JsonPropertyName("start")] string Start,
    [property: JsonPropertyName("end")] string End);

sealed record Note(
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("appName")] string AppName,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("time")] string Time);

sealed class FeedMessage
{
    [JsonPropertyName("type")] public string Type => "feed";
    [JsonPropertyName("now")] public string Now { get; set; } = DateTimeOffset.Now.ToString("o");
    [JsonPropertyName("events")] public List<Meeting> Events { get; set; } = new();
    [JsonPropertyName("notifications")] public List<Note> Notifications { get; set; } = new();
    [JsonPropertyName("calendar_error")] public string? CalendarError { get; set; }
    [JsonPropertyName("notifications_error")] public string? NotificationsError { get; set; }
}

// Windows keeps every toast it shows in an SQLite file. We read a copy of it (never the live file, never writing),
// which works for any app without a packaged identity or admin rights.
static class Notifications
{
    // key, name, parts of the Windows app id, sites (browser notifications name the site in the attribution line)
    static readonly (string Key, string Name, string[] Ids, string[] Sites)[] Known =
    {
        ("whatsapp", "WhatsApp", new[] { "whatsapp" }, new[] { "web.whatsapp.com" }),
        ("outlook", "Outlook", new[] { "outlook", "olk" }, new[] { "outlook.office", "outlook.live", "outlook.cloud" }),
        ("teams", "Teams", new[] { "teams" }, new[] { "teams.microsoft.com", "teams.live.com" }),
        ("telegram", "Telegram", new[] { "telegram" }, new[] { "web.telegram.org" }),
        ("gmail", "Gmail", Array.Empty<string>(), new[] { "mail.google.com" }),
        ("slack", "Slack", new[] { "slack" }, new[] { "app.slack.com" }),
        ("discord", "Discord", new[] { "discord" }, new[] { "discord.com" }),
        ("viber", "Viber", new[] { "viber" }, Array.Empty<string>()),
        ("skype", "Skype", new[] { "skype" }, new[] { "web.skype.com" }),
        ("zoom", "Zoom", new[] { "zoom" }, new[] { "zoom.us" }),
        ("calendar", "Календарь", new[] { "windowscommunicationsapps", "calendar" }, Array.Empty<string>()),
        ("chrome", "Chrome", new[] { "chrome" }, Array.Empty<string>()),
        ("edge", "Edge", new[] { "msedge", "microsoftedge" }, Array.Empty<string>()),
        ("firefox", "Firefox", new[] { "firefox", "308046b0af4a39cb" }, Array.Empty<string>()),
        ("windows", "Windows", new[] { "windows.systemtoast", "microsoft.windows.", "windows.defender", "securityandmaintenance" }, Array.Empty<string>()),
    };

    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    public static (string Key, string Name) Classify(string handler, string attribution)
    {
        var h = handler.ToLowerInvariant();
        var site = attribution.ToLowerInvariant();
        if (site.Length > 0)
            foreach (var k in Known)
                if (k.Sites.Any(site.Contains)) return (k.Key, k.Name);
        foreach (var k in Known)
            if (k.Ids.Any(h.Contains)) return (k.Key, k.Name);
        // unknown app: a readable name from its id, e.g. "Contoso.Notes_8wekyb3d8bbwe!App" -> "Notes"
        var b = h.Split('!')[0].Split('_')[0].Replace(".exe", "").TrimEnd('\\');
        b = b.Split('\\')[^1].Split('.')[^1];
        if (b.Length == 0) b = "app";
        var key = Regex.Replace(b, "[^a-z0-9]+", "");
        return (key.Length > 0 ? key : "app", char.ToUpperInvariant(b[0]) + b[1..]);
    }

    public static bool Wanted(Settings s, string key, string name)
    {
        bool Match(string x) => x.Equals(key, StringComparison.OrdinalIgnoreCase) || x.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (s.AppsHide.Any(Match)) return false;
        return s.AppsOnly.Count == 0 || s.AppsOnly.Any(Match);
    }

    static readonly Regex BareSite = new(@"^[\w.-]+\.[a-z]{2,}$", RegexOptions.IgnoreCase);

    public static List<Note> Read(Settings s, out string? error)
    {
        error = null;
        var list = new List<Note>();
        if (!File.Exists(DbPath)) { error = "База уведомлений Windows не найдена"; return list; }
        var tmp = Path.Combine(Path.GetTempPath(), "StarsDesktop-wpn");
        try
        {
            Directory.CreateDirectory(tmp);
            var copy = Path.Combine(tmp, "wpn.db");
            foreach (var ext in new[] { "", "-wal", "-shm" })   // the database and its write-ahead log
            {
                var src = DbPath + ext;
                var dst = copy + ext;
                if (File.Exists(src))
                {
                    using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var o = new FileStream(dst, FileMode.Create, FileAccess.Write);
                    i.CopyTo(o);
                }
                else if (File.Exists(dst)) File.Delete(dst);
            }
            var cutoff = DateTime.UtcNow.AddHours(-s.NotificationHours);
            using var con = new SqliteConnection($"Data Source={copy};Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "SELECT n.Payload, n.ArrivalTime, h.PrimaryId FROM Notification n " +
                "LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                "WHERE n.Type = 'toast' ORDER BY n.ArrivalTime DESC LIMIT 400";
            using var r = cmd.ExecuteReader();
            while (r.Read() && list.Count < s.MaxNotifications)
            {
                var when = DateTime.FromFileTimeUtc(r.IsDBNull(1) ? 0 : r.GetInt64(1));
                if (when < cutoff) break;
                var payload = r.IsDBNull(0) ? "" : r.GetValue(0) switch
                {
                    byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                    var v => v.ToString() ?? "",
                };
                var handler = r.IsDBNull(2) ? "" : r.GetString(2);
                var (texts, attribution) = Texts(payload);
                var (key, name) = Classify(handler, attribution);
                if (!Wanted(s, key, name)) continue;
                texts.RemoveAll(t => BareSite.IsMatch(t));
                if (texts.Count == 0) continue;
                var body = s.ShowText ? string.Join(" ", texts.Skip(1)) : "";
                if (body.Length > 160) body = body[..160];
                list.Add(new Note(key, name, texts[0], body, new DateTimeOffset(when).ToLocalTime().ToString("o")));
            }
        }
        catch (Exception e)
        {
            error = "Уведомления: " + e.Message;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
        return list;
    }

    static (List<string> Texts, string Attribution) Texts(string payload)
    {
        var texts = new List<string>();
        var attribution = "";
        try
        {
            foreach (var t in XDocument.Parse(payload).Descendants("text"))
            {
                var v = t.Value.Trim();
                if ((string?)t.Attribute("placement") == "attribution") { attribution += " " + v; continue; }
                if (v.Length > 0) texts.Add(v);
            }
        }
        catch { }
        return (texts, attribution.Trim());
    }
}

// Meetings from the calendar's published ICS link (Outlook: Settings → Calendar → Shared calendars → Publish).
static class Meetings
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly Regex OnlineLink = new(@"teams\.microsoft\.com|teams\.live\.com|zoom\.us|meet\.google\.com|webex\.com|telemost\.yandex", RegexOptions.IgnoreCase);

    public static async Task<(List<Meeting>? Events, string? Error)> ReadAsync(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.IcsUrl))
            return (new List<Meeting>(), "Нет ссылки на календарь (ics_url в config.json)");
        try
        {
            var url = s.IcsUrl.Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
            var text = await Http.GetStringAsync(url);
            var cal = IcalCalendar.Load(text);
            var from = DateTime.Now.Date.AddDays(-1);
            var to = DateTime.Now.Date.AddDays(s.DaysAhead + 1);
            var list = new List<Meeting>();
            foreach (var occ in cal.GetOccurrences(from, to))
            {
                if (occ.Source is not CalendarEvent ev) continue;
                if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                var title = ev.Summary ?? "";
                if (title.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase) || title.StartsWith("Отменено:", StringComparison.OrdinalIgnoreCase)) continue;
                var start = occ.Period.StartTime;
                var end = occ.Period.EndTime ?? start.Add(ev.Duration);
                bool allDay = ev.IsAllDay || !start.HasTime;
                string Iso(Ical.Net.DataTypes.IDateTime d) => allDay
                    ? new DateTimeOffset(DateTime.SpecifyKind(d.Value.Date, DateTimeKind.Local)).ToString("o")
                    : new DateTimeOffset(d.AsUtc, TimeSpan.Zero).ToLocalTime().ToString("o");
                var where = ev.Location ?? "";
                var online = OnlineLink.IsMatch(where) || OnlineLink.IsMatch(ev.Description ?? "");
                if (online && OnlineLink.IsMatch(where) && where.Contains("http")) where = "";
                list.Add(new Meeting(title.Trim(), where.Trim(), online, allDay, Iso(start), Iso(end)));
            }
            list.Sort((a, b) => DateTimeOffset.Parse(a.Start).CompareTo(DateTimeOffset.Parse(b.Start)));
            return (list, null);
        }
        catch (Exception e)
        {
            return (null, "Календарь: " + e.Message);
        }
    }
}
