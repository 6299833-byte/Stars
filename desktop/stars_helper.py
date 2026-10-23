"""Stars wallpaper helper for Windows.

Serves the solar-system page to Lively Wallpaper (or any browser) at http://127.0.0.1:8777/ and a small
JSON feed at /api/feed with today's meetings and recent notifications:

* Calendar: an ICS link published from Outlook on the web (Settings > Calendar > Shared calendars >
  Publish a calendar), set as "ics_url" in config.json. No app registration or admin consent needed.
* Notifications: read-only from the Windows notification database
  (%LOCALAPPDATA%/Microsoft/Windows/Notifications/wpndatabase.db). Toasts from Outlook and from
  WhatsApp Web (shown by Edge or Chrome) are picked out by app id and site.

Listens on 127.0.0.1 only; nothing leaves the computer except the ICS download. Standard library only.
"""

import datetime as dt
import functools
import http.server
import json
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import threading
import time
import urllib.request
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
CONFIG_PATH = os.path.join(HERE, "config.json")
DEFAULTS = {
    "port": 8777,
    "ics_url": "",
    "show_text": True,         # False: sender only, no message text
    "max_notifications": 12,
    "notification_hours": 12,  # how far back the feed goes
    "days_ahead": 7,
    "web_root": "",            # folder with index.html; default: ./www next to the helper, else the repo
}


def load_config():
    cfg = dict(DEFAULTS)
    try:
        with open(CONFIG_PATH, encoding="utf-8") as f:
            cfg.update(json.load(f))
    except FileNotFoundError:
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(DEFAULTS, f, ensure_ascii=False, indent=2)
    except Exception as e:  # a broken config should not stop the wallpaper
        print("config.json:", e)
    return cfg


def find_web_root(cfg):
    for p in [cfg.get("web_root"), os.path.join(HERE, "www"), os.path.join(HERE, "..", "www"), os.path.join(HERE, "..")]:
        if p and os.path.isfile(os.path.join(p, "index.html")) or (p and os.path.isfile(os.path.join(p, "solar-system.html"))):
            return os.path.abspath(p)
    return HERE


# ---------------------------------------------------------------- calendar (ICS)

def unfold(text):
    return re.sub(r"\r?\n[ \t]", "", text).splitlines()


def parse_props(lines):
    """VEVENT/VTIMEZONE blocks as dicts of name -> list of (params, value)."""
    stack, blocks = [], []
    for line in lines:
        if ":" not in line:
            continue
        head, value = line.split(":", 1)
        name, *params = head.split(";")
        name = name.upper()
        if name == "BEGIN":
            stack.append({"_type": value.strip().upper(), "_children": []})
        elif name == "END":
            if not stack:
                continue
            b = stack.pop()
            (stack[-1]["_children"] if stack else blocks).append(b)
        elif stack:
            p = {}
            for kv in params:
                if "=" in kv:
                    k, v = kv.split("=", 1)
                    p[k.upper()] = v.strip('"')
            stack[-1].setdefault(name, []).append((p, value))
    return blocks


def ics_text(v):
    return v.replace("\\n", " ").replace("\\N", " ").replace("\\,", ",").replace("\\;", ";").replace("\\\\", "\\").strip()


WD = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"]


def nth_weekday(year, month, byday):
    """Date of e.g. '-1SU' (last Sunday) or '2SU' in a month."""
    m = re.match(r"([+-]?\d+)?(MO|TU|WE|TH|FR|SA|SU)", byday)
    n, wd = int(m.group(1) or 1), WD.index(m.group(2))
    if n > 0:
        d = dt.date(year, month, 1)
        d += dt.timedelta(days=(wd - d.weekday()) % 7 + 7 * (n - 1))
    else:
        nxt = dt.date(year + (month == 12), month % 12 + 1, 1)
        d = nxt - dt.timedelta(days=1)
        d -= dt.timedelta(days=(d.weekday() - wd) % 7 + 7 * (-n - 1))
    return d


class VTZ:
    """Fixed STANDARD offset with an optional yearly DAYLIGHT rule, enough for Outlook's VTIMEZONE blocks."""

    def __init__(self, block):
        self.std = self.dst = None
        for ch in block["_children"]:
            off = ch.get("TZOFFSETTO", [({}, "+0000")])[0][1].strip()
            sign = -1 if off.startswith("-") else 1
            off = off.lstrip("+-")
            minutes = sign * (int(off[:2]) * 60 + int(off[2:4] or 0))
            rule = ch.get("RRULE", [({}, "")])[0][1]
            start = ch.get("DTSTART", [({}, "16010101T000000")])[0][1]
            entry = (minutes, dict(kv.split("=", 1) for kv in rule.split(";") if "=" in kv), start)
            if ch["_type"] == "STANDARD":
                self.std = entry
            elif ch["_type"] == "DAYLIGHT":
                self.dst = entry

    def offset(self, local):
        if not self.std:
            return dt.timedelta(0)
        if not self.dst or not self.std[1] or not self.dst[1]:
            return dt.timedelta(minutes=self.std[0])
        try:
            def change(entry):
                r, start = entry[1], entry[2]
                d = nth_weekday(local.year, int(r["BYMONTH"]), r["BYDAY"])
                hh, mm = int(start[9:11] or 0), int(start[11:13] or 0)
                return dt.datetime(d.year, d.month, d.day, hh, mm)
            on, off = change(self.dst), change(self.std)
            in_dst = on <= local < off if on < off else not (off <= local < on)
            return dt.timedelta(minutes=(self.dst if in_dst else self.std)[0])
        except Exception:
            return dt.timedelta(minutes=self.std[0])


def parse_dt(params, value, tzs):
    """-> (aware UTC datetime, all_day)."""
    value = value.strip()
    if params.get("VALUE") == "DATE" or re.fullmatch(r"\d{8}", value):
        d = dt.datetime.strptime(value[:8], "%Y%m%d")
        return d.astimezone(), True   # local midnight
    if value.endswith("Z"):
        return dt.datetime.strptime(value, "%Y%m%dT%H%M%SZ").replace(tzinfo=dt.timezone.utc), False
    local = dt.datetime.strptime(value[:15], "%Y%m%dT%H%M%S")
    tz = tzs.get(params.get("TZID", ""))
    if tz:
        return (local - tz.offset(local)).replace(tzinfo=dt.timezone.utc), False
    return local.astimezone(), False   # floating time: treat as this computer's local time


def expand(ev, tzs, start, end):
    """Occurrences (begin, finish) of one VEVENT between start and end (UTC), with simple RRULE support."""
    p, v = ev["DTSTART"][0]
    begin, all_day = parse_dt(p, v, tzs)
    if "DTEND" in ev:
        finish = parse_dt(*ev["DTEND"][0], tzs)[0]
    else:
        finish = begin + (dt.timedelta(days=1) if all_day else dt.timedelta(minutes=30))
    length = finish - begin
    exdates = set()
    for ep, evv in ev.get("EXDATE", []):
        for one in evv.split(","):
            try:
                exdates.add(parse_dt(ep, one, tzs)[0])
            except ValueError:
                pass
    if "RRULE" not in ev:
        return [(begin, begin + length, all_day)] if begin < end and begin + length > start else []
    r = dict(kv.split("=", 1) for kv in ev["RRULE"][0][1].split(";") if "=" in kv)
    freq, interval = r.get("FREQ", "WEEKLY"), int(r.get("INTERVAL", 1))
    count = int(r["COUNT"]) if "COUNT" in r else None
    until = parse_dt({}, r["UNTIL"], tzs)[0] if "UNTIL" in r else None
    bydays = [WD.index(x[-2:]) for x in r.get("BYDAY", "").split(",") if x[-2:] in WD]
    out, n, cur = [], 0, begin
    step = {"DAILY": dt.timedelta(days=interval), "WEEKLY": dt.timedelta(weeks=interval)}.get(freq)
    for _ in range(2000):
        if (until and cur > until) or cur >= end or (count is not None and n >= count):
            break
        candidates = [cur]
        if freq == "WEEKLY" and bydays:
            week0 = cur - dt.timedelta(days=cur.astimezone().weekday())
            candidates = sorted(week0 + dt.timedelta(days=d) for d in bydays)
            candidates = [c for c in candidates if c >= begin]
        for c in candidates:
            if (until and c > until) or (count is not None and n >= count):
                break
            n += 1
            if c not in exdates and c < end and c + length > start:
                out.append((c, c + length, all_day))
        if freq == "MONTHLY":
            m = cur.month - 1 + interval
            try:
                cur = cur.replace(year=cur.year + m // 12, month=m % 12 + 1)
            except ValueError:
                break
        elif freq == "YEARLY":
            cur = cur.replace(year=cur.year + interval)
        elif step:
            cur = cur + step
        else:
            break
    return out


def read_calendar(cfg):
    url = cfg.get("ics_url", "").strip()
    if not url:
        return None, "Ссылка на календарь не задана (ics_url в config.json)"
    req = urllib.request.Request(url.replace("webcal://", "https://"), headers={"User-Agent": "StarsWallpaper/1.0"})
    with urllib.request.urlopen(req, timeout=30) as r:
        text = r.read().decode("utf-8", "replace")
    blocks = parse_props(unfold(text))
    cal = next((b for b in blocks if b["_type"] == "VCALENDAR"), {"_children": blocks})
    tzs = {b["TZID"][0][1]: VTZ(b) for b in cal["_children"] if b["_type"] == "VTIMEZONE" and "TZID" in b}
    now = dt.datetime.now(dt.timezone.utc)
    day0 = now.astimezone().replace(hour=0, minute=0, second=0, microsecond=0)
    start, end = day0, day0 + dt.timedelta(days=cfg.get("days_ahead", 7))
    overrides = {}
    events = []
    for ev in cal["_children"]:
        if ev["_type"] != "VEVENT" or "DTSTART" not in ev:
            continue
        if "RECURRENCE-ID" in ev:   # a moved or edited occurrence replaces the generated one
            uid = ev.get("UID", [({}, "")])[0][1]
            overrides[(uid, parse_dt(*ev["RECURRENCE-ID"][0], tzs)[0])] = True
    for ev in cal["_children"]:
        if ev["_type"] != "VEVENT" or "DTSTART" not in ev:
            continue
        status = ev.get("STATUS", [({}, "")])[0][1].upper()
        if status == "CANCELLED":
            continue
        uid = ev.get("UID", [({}, "")])[0][1]
        title = ics_text(ev.get("SUMMARY", [({}, "Без названия")])[0][1])
        where = ics_text(ev.get("LOCATION", [({}, "")])[0][1])
        desc = ev.get("DESCRIPTION", [({}, "")])[0][1]
        online = bool(re.search(r"teams\.microsoft\.com|zoom\.us|meet\.google", desc + where, re.I))
        for b, f, all_day in expand(ev, tzs, start, end):
            if "RECURRENCE-ID" not in ev and (uid, b) in overrides:
                continue
            events.append({"title": title, "where": where, "online": online, "all_day": all_day,
                           "start": b.isoformat(), "end": f.isoformat()})
    events.sort(key=lambda e: e["start"])
    return events, None


# ---------------------------------------------------------------- notifications (Windows toasts)

def wpn_path():
    base = os.environ.get("LOCALAPPDATA", "")
    return os.path.join(base, "Microsoft", "Windows", "Notifications", "wpndatabase.db")


def classify(handler, payload):
    h, low = (handler or "").lower(), payload.lower()
    if "whatsapp" in h or "web.whatsapp.com" in low or "whatsapp" in low[:400]:
        return "whatsapp"
    if "outlook" in h or "olk" in h or "outlook.office" in low or "outlook.live" in low:
        return "outlook"
    return None


def toast_texts(payload):
    try:
        root = ET.fromstring(payload)
    except ET.ParseError:
        return []
    texts = []
    for t in root.iter("text"):
        if t.get("placement") == "attribution":
            continue
        s = (t.text or "").strip()
        if s:
            texts.append(s)
    return texts


def read_notifications(cfg, db_path=None):
    src = db_path or wpn_path()
    if not os.path.exists(src):
        return None, "База уведомлений Windows не найдена"
    tmp = tempfile.mkdtemp(prefix="stars-wpn-")
    try:   # copy the database and its WAL so the live one is never locked by us
        for ext in ("", "-wal", "-shm"):
            if os.path.exists(src + ext):
                shutil.copy2(src + ext, os.path.join(tmp, "wpn.db" + ext))
        con = sqlite3.connect(os.path.join(tmp, "wpn.db"))
        rows = con.execute(
            "SELECT n.Payload, n.ArrivalTime, h.PrimaryId FROM Notification n "
            "LEFT JOIN NotificationHandler h ON h.RecordId = n.HandlerId "
            "WHERE n.Type = 'toast' ORDER BY n.ArrivalTime DESC LIMIT 400").fetchall()
        con.close()
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    cutoff = dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=cfg.get("notification_hours", 12))
    out = []
    for payload, arrival, handler in rows:
        if isinstance(payload, bytes):
            payload = payload.decode("utf-8", "replace")
        app = classify(handler, payload or "")
        if not app:
            continue
        when = dt.datetime(1601, 1, 1, tzinfo=dt.timezone.utc) + dt.timedelta(microseconds=int(arrival or 0) / 10)
        if when < cutoff:
            break
        texts = [t for t in toast_texts(payload) if "web.whatsapp.com" not in t.lower()]
        if not texts:
            continue
        sender = texts[0]
        body = " ".join(texts[1:])[:160] if cfg.get("show_text", True) else ""
        out.append({"app": app, "from": sender, "text": body, "time": when.isoformat()})
        if len(out) >= cfg.get("max_notifications", 12):
            break
    return out, None


# ---------------------------------------------------------------- feed + server

STATE = {"events": [], "calendar_error": None, "notifications": [], "notifications_error": None, "calendar_at": 0}
LOCK = threading.Lock()


def poll(cfg):
    while True:
        try:
            notes, err = read_notifications(cfg)
        except Exception as e:
            notes, err = [], f"Уведомления: {e}"
        with LOCK:
            STATE["notifications"], STATE["notifications_error"] = notes or [], err
        if time.time() - STATE["calendar_at"] > 600:
            try:
                events, cerr = read_calendar(cfg)
            except Exception as e:
                events, cerr = None, f"Календарь: {e}"
            with LOCK:
                if events is not None:
                    STATE["events"] = events
                STATE["calendar_error"], STATE["calendar_at"] = cerr, time.time()
        time.sleep(15)


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, ".html": "text/html; charset=utf-8",
                      ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8"}

    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path.startswith("/api/feed"):
            with LOCK:
                body = json.dumps({"now": dt.datetime.now(dt.timezone.utc).isoformat(), **STATE}, ensure_ascii=False).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return
        if self.path in ("/", "") or self.path.startswith("/#") or self.path.startswith("/?"):
            root = self.directory
            self.path = "/index.html" if os.path.isfile(os.path.join(root, "index.html")) else "/solar-system.html"
        return super().do_GET()


def main():
    cfg = load_config()
    root = find_web_root(cfg)
    threading.Thread(target=poll, args=(cfg,), daemon=True).start()
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", int(cfg["port"])), functools.partial(Handler, directory=root))
    print(f"Обои: http://127.0.0.1:{cfg['port']}/#wallpaper  (папка {root})")
    srv.serve_forever()


if __name__ == "__main__":
    main()
