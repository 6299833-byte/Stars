# Подпись, версии и выпуск

## 1. Ключ загрузки (upload key)

Создаётся один раз, на своём компьютере (нужен JDK 17+):

```sh
keytool -genkeypair -v \
  -keystore stars-upload.jks -storetype JKS \
  -alias upload -keyalg RSA -keysize 4096 -validity 10000 \
  -dname "CN=<Имя>, O=<Организация>, C=RU"
```

- Пароль хранилища и пароль ключа — длинные, сохранить в менеджере паролей.
- `stars-upload.jks` **не коммитить** (`*.jks`, `*.keystore` уже в `.gitignore`). Сделать резервную копию вне репозитория.
- Отпечаток (понадобится при сбросе ключа): `keytool -list -v -keystore stars-upload.jks -alias upload`.

## 2. Секреты GitHub Actions

Репозиторий → Settings → Secrets and variables → Actions → New repository secret. Workflow `.github/workflows/android.yml` (job `release`) ждёт:

| Секрет | Значение |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | `base64 -w0 stars-upload.jks` (macOS: `base64 -i stars-upload.jks`) |
| `ANDROID_KEYSTORE_PASSWORD` | пароль хранилища |
| `ANDROID_KEY_ALIAS` | `upload` |
| `ANDROID_KEY_PASSWORD` | пароль ключа |

Без `ANDROID_KEYSTORE_BASE64` собирается только debug-APK (job `build`). С секретами job `release` раскодирует ключ во временный файл (`ANDROID_KEYSTORE_PATH`), выполнит `./gradlew bundleRelease assembleRelease` и выложит артефакт `app-release` (`.aab` для Play, `.apk` для ручной установки).

## 3. Версии

`android/project/app/build.gradle`:
- `versionCode = VERSION_CODE` из окружения; в CI это `github.run_number` (растёт с каждым запуском workflow). Локально без переменной — `1`.
- `versionName = "1.0.<VERSION_CODE>"`.

Правила:
- Play принимает только **строго возрастающий** `versionCode`; уже загруженный номер повторно использовать нельзя (даже после удаления сборки).
- Загружать только AAB из CI, а не локальные сборки с `versionCode 1`.
- Если номер запусков когда-нибудь сбросится (новый репозиторий, переименование workflow), добавить смещение отдельным шагом перед сборкой: `echo "VERSION_CODE=$((GITHUB_RUN_NUMBER+100))" >> "$GITHUB_ENV"` (в выражениях `${{ }}` арифметики нет).
- Значимые релизы можно отмечать поднятием мажорной части `versionName` (`1.0` → `1.1`) правкой `build.gradle`.
- targetSdk 36 (`variables.gradle`) — требование Play для новых приложений и обновлений с 31.08.2026.

## 4. Play App Signing

1. Play Console → создать приложение (имя «Солнечная система», язык ru-RU, приложение, бесплатное).
2. При первой загрузке AAB согласиться на **Play App Signing** (по умолчанию): Google хранит ключ подписи приложения, а вы подписываете загрузки ключом `upload`.
3. Тестирование → «Внутреннее тестирование» → создать выпуск → загрузить `app-release.aab` из артефакта CI.
4. Для новых личных аккаунтов разработчика Play требует **закрытое тестирование минимум с 12 тестировщиками в течение 14 дней** перед доступом к рабочей версии — заложить это время.
5. Потеря upload-ключа не фатальна: Play Console → «Целостность приложения» → «Запросить сброс ключа загрузки» (новый ключ + PEM-сертификат). Потерять ключ подписи нельзя — его хранит Google.

## 5. Перед каждым выпуском

- `npm ci && npm run build && npx cap sync android` проходит локально;
- скачать APK из артефакта, проверить на устройстве: запуск без белой вспышки, кнопка «Назад», поворот, крупный план Сатурна;
- обновить скриншоты в `screenshots/`, если интерфейс заметно изменился;
- если появились сеть/данные — пересмотреть `data-safety.md` и `privacy-policy.md`.
