# Recon map: live-wallpaper engines (Windows desktop)

Scope: the core of a live-wallpaper engine — showing an interactive web page as the desktop background behind the icons, keeping it light, and rules for when to pause — plus what our product adds: a feed of system notifications and the user's meetings.
Originals studied: Lively Wallpaper (open source, GPL-3.0) and Wallpaper Engine (paid, Steam). Clean room: features and behaviour from public docs, listings and help pages only; no code, assets, names or copy taken.
For: the owner of this repo, on his own Windows PC; the "Солнечная система" page is the wallpaper.
Date: 2026-10-23

## Sources

| # | source | URL | notes |
| --- | --- | --- | --- |
| 1 | Lively project page | https://github.com/rocksdanister/lively | feature list in the README |
| 2 | Lively package listing | https://www.chocolatey.org/packages/lively | pause on fullscreen, battery, remote desktop; multi-monitor modes |
| 3 | Wallpaper Engine help: application rules | https://help.wallpaperengine.io/en/functionality/applicationrules.html | per-.exe rules: is running / is playing audio → pause, stop, keep running |
| 4 | Wallpaper Engine help: wallpaper per app | https://help.wallpaperengine.io/en/functionality/wallpaperperapp.html | switch wallpaper when an app starts |
| 5 | Wallpaper Engine help: game performance | https://help.wallpaperengine.io/en/performance/game.html | pause in games by default; "other app maximised" setting |
| 6 | Wallpaper Engine docs: web audio | https://docs.wallpaperengine.io/en/web/audio/visualizer.html | page registers an audio listener, 64 bands per channel |
| 7 | Wallpaper Engine docs: media integration | https://docs.wallpaperengine.io/en/web/audio/media.html | now-playing title, artist, cover from the system media session |
| 8 | Microsoft Q&A, Processing forum | https://learn.microsoft.com/en-us/answers/questions/1386630/doubts-about-the-window-of-program-manager | the WorkerW technique (public, undocumented by Microsoft) |

## Core loop

Pick a wallpaper → it runs behind the desktop icons on every monitor → it gets out of the way (pauses) when you work, game or run on battery.

## Screens

| ID | screen | how to reach | purpose | key components | states seen |
| --- | --- | --- | --- | --- | --- |
| S01 | desktop wallpaper | always | the live page behind icons, one per monitor | WebView, monitor layout | playing, paused, loading, error (page failed) |
| S02 | tray menu | tray icon | pause/resume, choose layout, open settings, autostart, exit | context menu | playing, paused |
| S03 | settings | tray → Настройки | performance rules, notifications filter, calendar link, privacy | form | empty (first run), filled, invalid link |
| S04 | wallpaper library | originals: main window | choose and preview wallpapers | grid, preview | **skip** for us: one wallpaper (ours) |
| S05 | wallpaper panel (ours) | on S01 | clock, meetings, notification feed | side panel | no helper, no calendar, no messages, filled |

## Flows

```
F01 Set the wallpaper
    run StarsDesktop.exe -> S01 on all monitors
    happy path clicks: 1 (double-click the exe)
    edge: Windows 11 24H2 desktop layout, explorer.exe restarts, monitor added/removed, DPI scaling
F02 Get out of the way
    fullscreen app or game in front -> S01 paused; back to desktop -> resumes
    edge: maximised (not fullscreen) windows, multiple monitors (pause only the covered one), lock screen, battery
F03 See what's new
    a notification arrives in Windows -> S05 shows sender + text within seconds
    edge: Do Not Disturb, app not in allow list, privacy mode (sender only), notification removed
F04 Next meeting
    calendar link set -> S05 shows today's meetings and a countdown
    edge: recurring meetings, time zones and DST, all-day events, cancelled, link revoked
F05 Configure
    tray -> S03 -> change -> applied without restart
```

## Components

| component | variants | states | used on |
| --- | --- | --- | --- |
| Wallpaper surface | per monitor / span | playing, paused, error | S01 |
| Tray icon + menu | — | playing, paused | S02 |
| Side panel cards | clock, meetings, messages | empty, filled | S05 |

## Inferred data model

```
Settings      fps, pause_fullscreen, pause_maximised, pause_battery, pause_remote, layout (per_monitor | span),
              ics_url, show_text, apps_allow[], apps_hide[], notification_hours
              evidence: sources 2, 3, 5; confidence: high
AppRule       exe_name, condition (running | playing_audio), action (pause | keep | stop)
              evidence: source 3; confidence: high
Notification  app, sender, text, time            (read from the Windows notification store)
Meeting       title, start, end, all_day, where, online
```

## Feature matrix

See `features.csv`. Must: 10, should: 8, could: 5, skip: 6.

## Out of scope (cannot or should not be cloned)

- Workshop / wallpaper store and its content (Wallpaper Engine's Steam Workshop): their network and their creators' content.
- Wallpaper editor (Wallpaper Engine scene editor): not needed for one wallpaper.
- Video / GIF / scene wallpaper formats: our wallpaper is a web page.
- Their names, icons, UI copy and the JavaScript property-listener APIs' exact names.

## Size

Screens 4 (one skipped), flows 5, entities 4. Hard parts: embedding behind the icons across Windows builds (24H2 changed the layout), pausing correctly per monitor, reading notifications without a packaged app, calendar recurrence and time zones. Size: M.
