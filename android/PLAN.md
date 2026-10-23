# Android-приложение «Солнечная система» — план

Автор: android-architect · 2026-10-23 · исполнитель: android-engineer

## 1. Проверенные факты окружения (2026-10-23)

| Что | Факт |
|---|---|
| Java | OpenJDK 21.0.12 (`/usr/bin/java`) |
| Gradle | 8.14.3 (`/opt/gradle`) — совпадает с wrapper шаблона Capacitor 8 |
| Node / npm | Node v22.22.0 (`/opt/node22`) — Capacitor CLI 8 требует `node >= 22` |
| npm registry, Maven Central | доступны (200) |
| `dl.google.com` | **заблокирован** (прокси 403) → Google Maven и Android SDK недоступны, APK здесь не собрать |
| Android SDK | отсутствует |
| Актуальные пакеты npm | `@capacitor/core`, `@capacitor/cli`, `@capacitor/android` **8.5.3**; `@capacitor/app` 8.1.2; `@capacitor/status-bar` 8.0.4; `@fontsource/cormorant-garamond` и `@fontsource/jetbrains-mono` 5.3.0 (OFL-1.1) |
| Шаблон Android в `@capacitor/cli@8.5.3` | minSdk 24, compileSdk 36, targetSdk 36, AGP 8.13.0, Gradle wrapper 8.14.3, androidx.webkit 1.14.0 |
| Репозиторий | `github.com/6299833-byte/Stars`, ветка `master` |

Вывод: сборка APK/AAB только в GitHub Actions; локально — `npm ci`, сборка `www/`, `npx cap sync android` и проверка результата.

## 2. Решение

**Capacitor 8.5.x**, без бандлера. `solar-system.html` остаётся единственным источником; скрипт сборки делает из него `www/index.html`. Нативная часть — сгенерированный проект Capacitor, закоммиченный в репозиторий (с нашими правками `MainActivity`, манифеста и тем).

Почему не голый WebView/TWA: Capacitor даёт готовый локальный сервер `https://localhost` (WebViewAssetLoader), безопасный origin для WebGL/картинок, стандартный Gradle-проект и `cap sync`; TWA требует сети и Chrome.

## 3. Раскладка репозитория

```
package.json            # зависимости + скрипты (коммит)
package-lock.json       # коммит, CI использует npm ci
capacitor.config.json   # коммит
scripts/build-www.mjs   # сборка www/ (коммит)
www/                    # ГЕНЕРИРУЕТСЯ, в .gitignore
android/PLAN.md         # этот файл
android/project/        # нативный проект Capacitor (коммит; см. ниже про .gitignore)
.github/workflows/android.yml
```

`android/` уже занят этим планом, а `cap add android` отказывается работать в существующей папке, поэтому нативный проект лежит в `android/project` (`"android": {"path": "android/project"}` в конфиге).

Не коммитим (корневой `.gitignore`): `node_modules/`, `www/`, `android/project/app/src/main/assets/public/`, `android/project/app/src/main/assets/capacitor.config.json`, `android/project/app/src/main/assets/capacitor.plugins.json`, `android/project/**/build/`, `android/project/.gradle/`, `android/project/local.properties`, `*.keystore`, `*.jks`. Шаблон Capacitor уже содержит свой `android/project/.gitignore` — его оставить.

### package.json (суть)

```json
{
  "name": "stars-solar-system", "private": true, "type": "module",
  "scripts": {
    "build": "node scripts/build-www.mjs",
    "sync": "npm run build && cap sync android"
  },
  "dependencies": { "@capacitor/android": "8.5.3", "@capacitor/core": "8.5.3" },
  "devDependencies": {
    "@capacitor/cli": "8.5.3",
    "@fontsource/cormorant-garamond": "5.3.0",
    "@fontsource/jetbrains-mono": "5.3.0"
  }
}
```

Версии фиксировать точно. `@capacitor/app` и `@capacitor/status-bar` **не нужны**: кнопку «назад» и immersive-режим делаем в `MainActivity` (несколько строк Java, без JS-бандлера). Меньше плагинов — меньше разрешений и точек отказа.

### capacitor.config.json

```json
{
  "appId": "io.github.stars.solarsystem",
  "appName": "Солнечная система",
  "webDir": "www",
  "backgroundColor": "#020309",
  "android": {
    "path": "android/project",
    "backgroundColor": "#020309",
    "allowMixedContent": false,
    "captureInput": false,
    "webContentsDebuggingEnabled": false
  }
}
```

`server.androidScheme` оставить по умолчанию (`https`). Для отладки на устройстве `webContentsDebuggingEnabled` можно временно включить в debug.

## 4. Скрипт `scripts/build-www.mjs`

1. Очистить и создать `www/`, скопировать `img/` → `www/img/` (пути в странице относительные: `img/rubin-*.jpg`).
2. Прочитать `solar-system.html`. В файле **нет** `<!doctype>`, `<head>`, `charset` и `viewport` (страница пишется под обёртку claude.ai). Записать `www/index.html` как:
   ```html
   <!doctype html><html lang="ru"><head><meta charset="utf-8">
   <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover">
   </head><body>
   …содержимое solar-system.html…
   </body></html>
   ```
   (браузер перенесёт `<title>`/`<style>`/`<link>` куда надо; это проверено поведением HTML-парсера). `viewport-fit=cover` нужен, чтобы `env(safe-area-inset-*)` в CSS меню (`.menu-btn`, `.panel`) получали значения.
3. Шрифты офлайн: заменить строку `<link rel="stylesheet" href="https://fonts.googleapis.com/css2?…">` регуляркой `/<link[^>]+fonts\.googleapis\.com[^>]*>/` на `<link rel="stylesheet" href="fonts/fonts.css">`. Если совпадений не 1 — **падать с ошибкой** (страницу параллельно правят, тихий пропуск недопустим). Также удалить возможные `preconnect` на `fonts.gstatic.com`.
4. `www/fonts/`: скопировать из `node_modules/@fontsource/*/files/` только нужные woff2 — Cormorant Garamond 500 normal + 500 italic, JetBrains Mono 400 normal, подмножества `latin` и `cyrillic` (интерфейс русский) — и сгенерировать `fonts.css` с `@font-face { font-family: "Cormorant Garamond" | "JetBrains Mono"; font-display: swap; unicode-range: … }` (unicode-range взять из css-файлов fontsource). Положить рядом `LICENSE-CormorantGaramond.txt` и `LICENSE-JetBrainsMono.txt` (файлы `LICENSE` из пакетов, OFL-1.1). Имена семейств совпадают с `--serif`/`--mono` и `ctx.font` в странице, поэтому canvas-подписи тоже получат шрифт.
5. Проверка в конце скрипта: в `www/index.html` нет `http://`/`https://` ссылок на ресурсы (кроме `<a href>` в «О проекте»), все `img/*` из страницы существуют.

## 5. Хук в странице (единственная правка `solar-system.html`)

Страница не знает про Android. Одна строка рядом с `setMenu` (после строки с `addEventListener('keydown', … 'Escape' …)`, внутри IIFE, где видны `panel`, `setMenu`, `closeTarget`, `pendingSel`, `focus`, `choose`):

```js
window.solarBack = () => { if (!panel.hidden) { setMenu(false); return true; } if (closeTarget === 1 || pendingSel !== undefined || focus) { choose(null); return true; } return false; };
```

Логика: меню открыто → закрыть; крупный план (`closeTarget === 1`), перелёт (`pendingSel`) или фокус на группе (`focus`, «нажми „Обзор“») → `choose(null)` = кнопка «Обзор»; иначе `false` → выход. В браузере хук безвреден (никто не вызывает). Согласовать с графическим отделом, который параллельно правит файл: вставлять отдельным маленьким коммитом, не трогая соседний код. Если имена переменных к моменту вставки поменяются — поправить хук, а не логику страницы.

## 6. Нативная часть (`android/project`)

Создать: `npm ci && npm run build && npx cap add android` (работает без SDK: распаковывает шаблон и копирует `www`). Затем правки:

**`variables.gradle`**: оставить `minSdkVersion = 24`, `compileSdkVersion = 36`, `targetSdkVersion = 36`. Обоснование (актуально на 2026 г.): с 31.08.2026 Google Play требует targetSdk 36 (Android 16) для новых приложений и обновлений; 37 станет обязательным ориентировочно в августе 2027. minSdk 24 — минимум Capacitor 8 (WebView обновляется через Play, WebGL1 есть везде).

**`app/build.gradle`**: `versionCode` из `System.getenv("VERSION_CODE") ?: 1` (в CI = `github.run_number`), `versionName "1.0.<run>"`; `signingConfigs.release` из env (`ANDROID_KEYSTORE_PATH`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`), подключать только если путь задан; `minifyEnabled false` (Java-кода почти нет).

**`AndroidManifest.xml`**:
- `<application android:hardwareAccelerated="true" …>` (явно; WebGL без него не работает);
- у activity: `android:screenOrientation="fullUser"` (обе ориентации, учитывая блокировку поворота) — `configChanges` из шаблона оставить, чтобы поворот не пересоздавал WebView и не терял состояние сцены;
- разрешение `INTERNET` **удалить** (`<uses-permission android:name="android.permission.INTERNET" tools:node="remove"/>`, добавить `xmlns:tools`): страница грузится через перехват запросов Capacitor, сети не нужно. Внешние ссылки в «О проекте» Capacitor открывает интентом во внешнем браузере — им разрешение не нужно. Проверить на устройстве, что всё грузится в режиме полёта.
- `android:allowBackup="false"` (нечего бэкапить).

**`MainActivity.java`** (`io.github.stars.solarsystem`), `extends BridgeActivity`, в `onCreate` после `super.onCreate`:
- WebView: `getBridge().getWebView()` → `settings.setTextZoom(100)` (системный размер шрифта не ломает вёрстку), `setSupportZoom(false)`, `setBuiltInZoomControls(false)`, `setLayerType(LAYER_TYPE_HARDWARE, null)`, `setOverScrollMode(OVER_SCROLL_NEVER)`, фон `#020309`;
- immersive: `WindowCompat.setDecorFitsSystemWindows(getWindow(), false)`; `WindowInsetsControllerCompat c = WindowCompat.getInsetsController(getWindow(), decor)`; `c.hide(WindowInsetsCompat.Type.systemBars())`; `c.setSystemBarsBehavior(BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE)`; повторять `hide` в `onWindowFocusChanged(true)`. Вырез экрана: `layoutInDisplayCutoutMode = ALWAYS` (API 30+) / `SHORT_EDGES` (API 28–29) в теме или коде;
- экран не гаснет: `getWindow().addFlags(FLAG_KEEP_SCREEN_ON)` — включить (это «планетарий», смотрят без касаний); при желании сделать отключаемым позже;
- кнопка «назад»: `getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) { handleOnBackPressed() { webView.evaluateJavascript("(window.solarBack&&window.solarBack())===true", v -> { if (!"true".equals(v)) { setEnabled(false); getOnBackPressedDispatcher().onBackPressed(); setEnabled(true); } }); } })`. Если хук не определён — обычный выход. Колбэк регистрируется после `super.onCreate`, поэтому имеет приоритет над стандартной обработкой Bridge (без `@capacitor/app` Bridge иначе делал бы `goBack`/finish).

**Темы (`styles.xml`)**: `windowBackground` и splash — `#020309` (без белой вспышки), `android:windowLayoutInDisplayCutoutMode`. Иконка: на первом этапе можно оставить шаблонную, затем сгенерировать адаптивную (`@capacitor/assets` 3.0.5 из `resources/icon.png`, тёмный фон) — отдельной задачей.

**Safe area**: с targetSdk 35+ Android 15+ принудительно включает edge-to-edge. Страница уже использует `env(safe-area-inset-*)`; Android WebView отдаёт эти значения только при `viewport-fit=cover` и в новых версиях WebView. Проверить на устройстве с вырезом/жестовой навигацией, что кнопка меню не уезжает под вырез. Если `env()` = 0 — включить в Capacitor 8 обработку insets ядром (настройка SystemBars/`adjustMarginsForEdgeToEdge` в `capacitor.config.json`, свериться с докой 8.x) — без правок страницы.

## 7. CI: `.github/workflows/android.yml`

- Триггеры: `push` в `master` (paths: `solar-system.html`, `img/**`, `package*.json`, `capacitor.config.json`, `scripts/**`, `android/project/**`, сам workflow), `pull_request`, `workflow_dispatch`.
- `runs-on: ubuntu-latest`; шаги: `actions/checkout@v4` → `actions/setup-node@v4` (node 22, cache npm) → `npm ci` → `npm run build` → `npx cap sync android` → `actions/setup-java@v4` (temurin 21, cache gradle) → `android-actions/setup-android@v3` + `sdkmanager "platforms;android-36" "build-tools;36.0.0"` (на раннере SDK есть, но платформа 36 может отсутствовать) → `cd android/project && chmod +x gradlew && ./gradlew assembleDebug` с `VERSION_CODE=${{ github.run_number }}`.
- Release: если задан секрет `ANDROID_KEYSTORE_BASE64` — декодировать в `$RUNNER_TEMP/release.jks`, выставить env подписи из секретов `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`, `./gradlew bundleRelease assembleRelease`. Условие через `env: HAS_KEY: ${{ secrets.ANDROID_KEYSTORE_BASE64 != '' }}` и `if: env.HAS_KEY == 'true'`.
- `actions/upload-artifact@v4`: `app-debug.apk`, и при наличии ключа `app-release.aab`/`app-release.apk`.

## 8. Риски

1. **Пуш `.github/workflows/*` может быть отклонён**, если токен без scope `workflow`. Тогда: закоммитить остальное, положить файл как `android/ci/android.yml` и попросить владельца перенести его через веб-интерфейс GitHub (или выдать токен со scope `workflow`).
2. Локально APK не собрать (`dl.google.com` закрыт) — первая настоящая сборка в CI; ошибки Gradle чинить по логам Actions.
3. Параллельные правки `solar-system.html`: build-скрипт обязан падать, если не нашёл ссылку Google Fonts; хук — одна строка.
4. Производительность WebGL на слабых телефонах: в странице уже `DPR = min(devicePixelRatio, 2)`; если тормозит — обсуждать с графическим отделом, не в Android-слое.
5. Safe area / edge-to-edge на Android 15+ — см. §6, проверка на устройстве.
6. Лицензии: фотографии Rubin (CC BY 4.0) — атрибуция уже в «О проекте»; шрифты OFL — лицензии в `www/fonts/`. Для Play нужна политика конфиденциальности (приложение ничего не собирает — одна строка).

## 9. Чек-лист инженера

- [ ] `package.json`, `package-lock.json` (`npm install` с точными версиями §3), `capacitor.config.json`, `.gitignore`.
- [ ] `scripts/build-www.mjs` по §4; `npm run build` → `www/index.html` с doctype/viewport, без ссылок на fonts.googleapis.com, `www/fonts/*.woff2` + `fonts.css` + 2 лицензии, `www/img/` (4 файла).
- [ ] Открыть `www/index.html` локальным сервером (`npx http-server www` или `python3 -m http.server -d www`) с отключённой сетью к Google — шрифты и картинки грузятся.
- [ ] Хук `window.solarBack` в `solar-system.html` (§5) — отдельный коммит; проверить в браузере из консоли: меню → `true` и закрывается; крупный план → `true` и «Обзор»; обзор → `false`.
- [ ] `npx cap add android` → `android/project`; правки `variables.gradle`, `app/build.gradle`, манифеста, `MainActivity.java`, `styles.xml` по §6.
- [ ] `npx cap sync android` проходит без ошибок; `android/project/app/src/main/assets/public/index.html` появился (и не попал в git).
- [ ] `.github/workflows/android.yml` по §7; при отказе пуша — риск 1.
- [ ] Первый прогон Actions зелёный, `app-debug.apk` в артефактах.
- [ ] Проверка на телефоне: офлайн (режим полёта), обе ориентации без сброса сцены, назад: меню → обзор → выход, immersive, нет белой вспышки, текст не масштабируется системным шрифтом, меню не под вырезом.
- [ ] (позже) иконка, ключ подписи в секретах, AAB для Play.
