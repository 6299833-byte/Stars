#!/usr/bin/env python3
"""
NASA Image and Video Library — поиск и скачивание фото.
API: https://images-api.nasa.gov (без ключа)

Как модуль:
    from nasa_images import search, best_image, metadata, download
    for r in search("mars rover", year_start=2020, limit=20):
        print(r["nasa_id"], r["title"])

Как CLI:
    python nasa_images.py "saturn rings" --from 2004 --to 2017 --limit 30
    python nasa_images.py "apollo 11" --csv apollo.csv --download ./photos --size orig
"""
import argparse
import csv
import os
import time
from urllib.parse import quote

import requests

API = "https://images-api.nasa.gov"
MAX_HITS = 10_000          # потолок выдачи API на один запрос
SIZES = ["orig", "large", "medium", "small", "thumb"]
IMG_EXT = (".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif")

S = requests.Session()
S.headers["User-Agent"] = "nasa-photo-finder/1.0"


def _get(url, params=None, retries=4):
    """GET -> JSON с повторами при 429/5xx и сетевых ошибках."""
    for i in range(retries):
        try:
            r = S.get(url, params=params, timeout=30)
            if r.status_code in (429, 500, 502, 503, 504):
                time.sleep(2 ** i)
                continue
            r.raise_for_status()
            return r.json()
        except requests.ConnectionError:
            if i == retries - 1:
                raise
            time.sleep(2 ** i)
    raise RuntimeError(f"Не удалось получить {url}")


def _fix(url):
    """В ссылках NASA бывают пробелы."""
    return url.replace(" ", "%20")


def search(q=None, media_type="image", year_start=None, year_end=None,
           center=None, keywords=None, title=None, description=None,
           location=None, photographer=None, limit=None,
           page_size=100, delay=0.2):
    """Генератор результатов поиска (сам листает страницы)."""
    params = {k: v for k, v in dict(
        q=q, media_type=media_type, center=center, keywords=keywords,
        title=title, description=description, location=location,
        photographer=photographer,
        year_start=str(year_start) if year_start else None,
        year_end=str(year_end) if year_end else None,
        page_size=page_size,
    ).items() if v}

    page, n = 1, 0
    while (page - 1) * page_size < MAX_HITS:
        params["page"] = page
        data = _get(f"{API}/search", params)["collection"]
        items = data.get("items", [])
        if not items:
            return
        for it in items:
            d = it["data"][0]
            thumb = next((l["href"] for l in it.get("links", [])
                          if l.get("rel") == "preview"), None)
            yield {
                "nasa_id": d["nasa_id"],
                "title": d.get("title", ""),
                "date": d.get("date_created", "")[:10],
                "center": d.get("center", ""),
                "photographer": d.get("photographer", ""),
                "location": d.get("location", ""),
                "keywords": ", ".join(d.get("keywords", [])),
                "description": d.get("description", ""),
                "thumb": _fix(thumb) if thumb else None,
                "page_url": f"https://images.nasa.gov/details/{d['nasa_id']}",
            }
            n += 1
            if limit and n >= limit:
                return
        if not any(l.get("rel") == "next" for l in data.get("links", [])):
            return
        page += 1
        time.sleep(delay)


def search_by_years(q, year_from, year_to, **kw):
    """Обход лимита 10 000: режет запрос по годам."""
    for y in range(year_from, year_to + 1):
        yield from search(q, year_start=y, year_end=y, **kw)


def files(nasa_id):
    """Все файлы ассета (разные размеры, metadata.json и т.д.)."""
    items = _get(f"{API}/asset/{quote(nasa_id)}")["collection"]["items"]
    return [_fix(i["href"]) for i in items]


def best_image(nasa_id, size="large"):
    """Ссылка на картинку нужного размера (или ближайшего меньшего)."""
    imgs = [f for f in files(nasa_id) if f.lower().endswith(IMG_EXT)]
    if not imgs:
        return None
    for s in SIZES[SIZES.index(size):]:
        for f in imgs:
            if f"~{s}." in f:
                return f
    return imgs[0]


def metadata(nasa_id):
    """Полные метаданные (EXIF, камера и т.д.)."""
    loc = _get(f"{API}/metadata/{quote(nasa_id)}")["location"]
    return _get(_fix(loc))


def download(url, folder):
    """Скачать файл; уже скачанные пропускает."""
    os.makedirs(folder, exist_ok=True)
    name = os.path.basename(url.split("?")[0]).replace("%20", " ")
    path = os.path.join(folder, name)
    if os.path.exists(path):
        return path
    with S.get(url, stream=True, timeout=120) as r:
        r.raise_for_status()
        with open(path + ".part", "wb") as f:
            for chunk in r.iter_content(1 << 16):
                f.write(chunk)
    os.replace(path + ".part", path)
    return path


def main():
    p = argparse.ArgumentParser(description="Поиск фото NASA")
    p.add_argument("query", nargs="?", help="поисковый запрос (англ.)")
    p.add_argument("--from", dest="year_start", type=int, help="год от")
    p.add_argument("--to", dest="year_end", type=int, help="год до")
    p.add_argument("--center", help="центр NASA: JPL, JSC, KSC, GSFC...")
    p.add_argument("--keywords", help="по ключевым словам, через запятую")
    p.add_argument("--title", help="искать только в заголовке")
    p.add_argument("--photographer")
    p.add_argument("--media", default="image", choices=["image", "video", "audio"])
    p.add_argument("--limit", type=int, default=50, help="0 = без лимита")
    p.add_argument("--csv", help="сохранить результаты в CSV")
    p.add_argument("--download", metavar="DIR", help="скачать картинки в папку")
    p.add_argument("--size", default="large", choices=SIZES)
    a = p.parse_args()

    if not any([a.query, a.keywords, a.title, a.center, a.photographer]):
        p.error("нужен запрос или хотя бы один фильтр")

    rows = list(search(
        a.query, media_type=a.media, year_start=a.year_start,
        year_end=a.year_end, center=a.center, keywords=a.keywords,
        title=a.title, photographer=a.photographer, limit=a.limit or None,
    ))

    for r in rows:
        print(f"{r['date']}  {r['nasa_id']:<28} {r['title'][:70]}")
    print(f"\nНайдено: {len(rows)}")

    if a.csv and rows:
        with open(a.csv, "w", newline="", encoding="utf-8-sig") as f:
            w = csv.DictWriter(f, fieldnames=rows[0].keys())
            w.writeheader()
            w.writerows(rows)
        print(f"CSV: {a.csv}")

    if a.download and a.media == "image":
        for i, r in enumerate(rows, 1):
            try:
                url = best_image(r["nasa_id"], a.size)
                if url:
                    path = download(url, a.download)
                    print(f"[{i}/{len(rows)}] {path}")
            except Exception as e:
                print(f"[{i}/{len(rows)}] {r['nasa_id']}: ошибка — {e}")
            time.sleep(0.2)


if __name__ == "__main__":
    main()
