# IPasswrd для iPhone — как собрать и запустить

Приложение: `src/IPasswrd.Mobile` (.NET MAUI 9, `net9.0-ios`), использует то же ядро
`IPasswrd.Core` и тот же файл сейфа `vault.ipvault`, что и Windows-приложение.

## Что уже умеет

- Разблокировка мастер-паролем (Argon2id + AES-256-GCM, формат сейфа v1 — бит-в-бит общий с Windows), локаут 4 свободные попытки → 5м/15м/1ч/5ч/24ч/7д/30д.
- Face ID / Touch ID: быстрая разблокировка сессионным ключом из Keychain (аналог DPAPI-пути на Windows; мастер-пароль переспрашивается раз в 30 дней).
- Сейф: аккаунты (группы по сайтам, имена групп подхватываются из meta-записи), карты (маски: номер по 4, срок с авто-слэшем, МИР раньше Mastercard), документы, заметки, ключи доступа (просмотр), избранное, поиск.
- **Аутентификатор: добавление кода проверки сканированием QR (нативная камера, otpauth:// разбирает ядро), ручной ввод, живые коды с обратным отсчётом, копирование по тапу.**
- Генератор паролей (те же наборы символов, что на Windows).
- Синхронизация: подключение `iCloud Drive → IPasswrd → vault.ipvault` через «Файлы» (security-scoped bookmark, NSFileCoordinator, поэлементный LWW-мердж `Vault.MergeFrom`).
- Смена мастер-пароля, экспорт копии сейфа, автоблокировка.

## Путь 1 (без Mac): Hot Restart — на ваш iPhone по USB

Требования (всё на этом ПК уже готово, кроме перечисленного):
1. **Visual Studio 2022** (Community подойдёт) с нагрузкой «.NET Multi-platform App UI development». Именно VS 2022 — в VS 2026 Hot Restart убрали, а с net10 он сломан (поэтому проект на net9.0-ios).
2. **iTunes** (из Microsoft Store или 64-бит с apple.com) — для связи с iPhone.
3. **Apple Developer Program (платный, $99/год)** — Hot Restart с бесплатным Apple ID не работает.

Шаги: открыть `src/IPasswrd.Mobile/IPasswrd.Mobile.csproj` в VS 2022 → цель отладки «iOS Local Devices → Local Device» → подключить iPhone кабелем → мастер настройки сам проведёт вход в Apple-аккаунт и провижининг → F5. Ограничения Hot Restart: это Debug-режим, иконка будет стандартной .NET, первая разблокировка медленнее (интерпретатор).

## Путь 2 (без Mac и без $99): GitHub Actions + AltStore

Когда решим включить git: приватный репозиторий на GitHub → workflow `.github/workflows/ios-build.yml`
собирает неподписанный IPA на облачном macOS-раннере (бесплатных минут хватает) →
IPA ставится на iPhone через AltStore/SideStore с бесплатным Apple ID (переподпись раз в 7 дней).

## Путь 3: любой Mac (свой/облачный)

`dotnet build -f net9.0-ios -c Release` на Mac, подпись и установка как обычно; оттуда же — TestFlight/App Store.

## Проверка компиляции на этом ПК (без iPhone)

```cmd
dotnet build src\IPasswrd.Mobile\IPasswrd.Mobile.csproj -f net9.0-ios
```

C#-компиляция и XAML проходят на Windows; на финальном шаге упаковки без Mac/VS сборка
останавливается — это ожидаемо, ошибки кода при этом видны полностью.

## Заметки

- `IPasswrd.Core` теперь мультитаргет `net10.0;net9.0` (Windows-приложение — по-прежнему net10).
- Argon2id — управляемый (Konscious): в Debug на телефоне первая разблокировка занимает несколько секунд; в Release с AOT — быстро. Дальше работает Face ID.
- Автозаполнение паролей в Safari (Credential Provider Extension) — отдельная фаза: расширениям iOS тесно с .NET-рантаймом по памяти; заложено на потом.
- Android: добавить `net9.0-android` в TargetFrameworks + `dotnet build -t:InstallAndroidDependencies`.
