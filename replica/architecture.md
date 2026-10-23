# Architecture: StarsDesktop (our live-wallpaper engine)

Built from `recon.md` and `features.csv`. Our own name, code and words.

## Stack

| layer | choice | why |
| --- | --- | --- |
| app | C# .NET 8, WinForms (`net8.0-windows`) | smallest native host for a window + tray icon; builds in GitHub Actions on `windows-latest` |
| renderer | Microsoft Edge WebView2 (`Microsoft.Web.WebView2`) | the page needs WebGL; WebView2 ships with Windows 10/11 (Evergreen runtime) |
| page | `www/` bundle from `scripts/build-www.mjs` | same offline page as the Android app |
| page loading | `SetVirtualHostNameToFolderMapping("stars.local", www)` | offline, no local HTTP server, images readable by canvas |
| engine → page | `CoreWebView2.PostWebMessageAsJson` | push feed and pause state; the page listens on `chrome.webview` |
| calendar | `Ical.Net` (MIT) | RRULE, EXDATE, RECURRENCE-ID and Windows time-zone names from Outlook |
| notifications | `Microsoft.Data.Sqlite` reading a copy of `%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db` | works unpackaged; `UserNotificationListener` needs package identity |
| settings | `config.json` next to the exe | portable; watched and re-read on change |
| distribution | `dotnet publish` self-contained single file, win-x64 | one exe + `www/` + `config.json`, no runtime install |

No database, no server, no network except downloading the user's own ICS link.

## Modules

```
Program            single instance (named mutex), tray icon and menu, timers, autostart (HKCU\...\Run)
DesktopLayer       finds the window behind the icons:
                     send 0x052C to Progman, then
                     24H2+: a WorkerW child of Progman        -> parent = that WorkerW (or Progman)
                     older: the top-level WorkerW after the one holding SHELLDLL_DefView -> parent = it
                   re-runs on the "TaskbarCreated" broadcast (explorer restarted)
WallpaperWindow    one borderless Form per monitor, WS_CHILD of the layer window, placed at the monitor's
                   rectangle in the layer's client coordinates; hosts WebView2
PauseRules         every 1.5 s: per monitor, is the foreground window fullscreen on it (and not the shell)?
                   + lock screen / remote session (SystemEvents), battery (PowerStatus), app rules
                   -> page gets {type:'pause', on:true|false}; on pause the page skips rendering
Feed               notifications (all apps, allow/hide lists, privacy) every 5 s and on file change;
                   calendar every 10 min -> {type:'feed', ...} to every window
```

## Messages to the page

```json
{"type":"feed","now":"…","events":[{"title","where","online","all_day","start","end"}],
 "notifications":[{"app","appName","from","text","time"}],"calendar_error":null,"notifications_error":null}
{"type":"pause","on":true}
```

The page uses the same feed shape as `desktop/stars_helper.py`, so it works with either the engine or Lively + helper.

## The parts that bite

- **Desktop layout differs by Windows build.** 24H2 parents WorkerW under Progman; older builds use a sibling WorkerW. Handle both; if neither is found, fall back to a bottom-most top-level window.
- **DPI.** Per-monitor DPI aware (v2) manifest; positions in physical pixels.
- **Explorer restarts.** Listen for `TaskbarCreated`, re-parent.
- **Notification store is undocumented.** Copy db + WAL before reading, never write; tolerate schema changes and report them in the feed.
- **Time zones / DST.** Delegate to Ical.Net + TimeZoneInfo.
- **Privacy.** Wallpaper is visible to anyone; `show_text: false` keeps only the sender.

## Build order

1. Vertical slice: one monitor, page behind icons, tray Exit.
2. Must-haves: all monitors, pause on fullscreen, fps cap, autostart, feed (notifications + calendar), privacy.
3. Should-haves: lock/remote/battery pause, settings hot reload, maximised option.
4. Could-haves: spanning, app rules, media and audio.

## Build log

| step | state | notes |
| --- | --- | --- |
| 1. vertical slice | written | `desktop/engine`: Program, DesktopLayer, WallpaperWindow; no Windows here, built and smoke-tested in GitHub Actions |
| 2. must-haves | written | all monitors, fullscreen pause per monitor, fps from config, HKCU autostart, feed for all apps, `show_text` |
| 3. should-haves | written | lock / remote / battery pause, `config.json` hot reload, `pause_maximised`, `pause_apps` |
| 4. could-haves | not started | spanning, media, audio |
| page bridge | done, tested | `chrome.webview` feed/pause/config messages checked in headless Chromium with a stub host |
