# NASA Images — поиск и скачивание фото NASA

Модуль и консольная утилита для поиска фото, видео и аудио в
[NASA Image and Video Library](https://images.nasa.gov) через публичный API
`https://images-api.nasa.gov`. Ключ API и регистрация не нужны.

## Установка

```bash
pip install -r requirements.txt
```

## Использование из консоли

```bash
# Поиск, вывод списка
python nasa_images.py "saturn rings" --from 2004 --to 2017 --limit 30

# Сохранить результаты в CSV и скачать оригиналы
python nasa_images.py "apollo 11" --csv apollo.csv --download ./photos --size orig

# Только по центру NASA и ключевым словам
python nasa_images.py --center JPL --keywords "mars,rover" --limit 100
```

### Параметры

| Параметр | Описание |
|---|---|
| `query` | Поисковый запрос (на английском) |
| `--from`, `--to` | Диапазон годов |
| `--center` | Центр NASA: JPL, JSC, KSC, GSFC, MSFC… |
| `--keywords` | Ключевые слова через запятую |
| `--title` | Искать только в заголовке |
| `--photographer` | Фотограф |
| `--media` | `image` (по умолчанию), `video`, `audio` |
| `--limit` | Сколько результатов (по умолчанию 50, `0` — без лимита) |
| `--csv FILE` | Сохранить результаты в CSV (UTF-8, открывается в Excel) |
| `--download DIR` | Скачать картинки в папку |
| `--size` | `orig`, `large` (по умолчанию), `medium`, `small`, `thumb` |

## Использование как модуль

```python
from nasa_images import search, search_by_years, best_image, metadata, download

# Поиск с фильтрами
for r in search("jupiter", center="JPL", year_start=2016, limit=20):
    print(r["date"], r["nasa_id"], r["title"])
    url = best_image(r["nasa_id"], "large")
    download(url, "./photos")

# Большие выборки (больше 10 000) — по годам
for r in search_by_years("earth", 2000, 2025, limit=None):
    ...

# Полные метаданные с EXIF
meta = metadata("as11-40-5874")
```

### Функции

| Функция | Что делает |
|---|---|
| `search(q, **filters)` | Генератор результатов, сам листает страницы |
| `search_by_years(q, from, to)` | Обход лимита 10 000 результатов — запрос по каждому году |
| `files(nasa_id)` | Все файлы ассета (все размеры, metadata.json) |
| `best_image(nasa_id, size)` | Ссылка на нужный размер или ближайший меньший |
| `metadata(nasa_id)` | Полные метаданные: EXIF, камера, объектив |
| `download(url, folder)` | Скачивание; уже скачанные файлы пропускает |

### Поля результата `search()`

`nasa_id`, `title`, `date`, `center`, `photographer`, `location`,
`keywords`, `description`, `thumb` (превью), `page_url` (страница на сайте).

## Эндпоинты API

| Эндпоинт | Назначение |
|---|---|
| `GET /search?q=...` | Поиск |
| `GET /asset/{nasa_id}` | Список файлов ассета |
| `GET /metadata/{nasa_id}` | Ссылка на metadata.json |
| `GET /captions/{nasa_id}` | Субтитры видео (.srt) |
| `GET /album/{name}` | Содержимое альбома |

Документация: https://images.nasa.gov/docs/images.nasa.gov_api_docs.pdf

## Заметки

- Запросы пишите на английском — метаданные NASA на английском.
- API отдаёт максимум 10 000 результатов на запрос; для больших выборок
  используйте `search_by_years()`.
- Размер `orig` часто бывает тяжёлым TIFF; для просмотра хватает `large`.
- При ошибках 429/5xx модуль сам повторяет запрос с паузой.
- Изображения NASA в основном находятся в общественном достоянии, но у
  части материалов есть указание авторства — проверяйте описание.
