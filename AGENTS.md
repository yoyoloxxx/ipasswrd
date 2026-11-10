# AGENTS.md — IPasswrd

Инструкции для ИИ-агентов и участников. Общее описание продукта и криптосхемы —
в [`README.md`](README.md), модель угроз и сообщения об уязвимостях — в
[`SECURITY.md`](SECURITY.md). Репозиторий **публичный**.

## Что это

IPasswrd — бесплатный кроссплатформенный менеджер паролей с собственным
сквозным шифрованием хранилища (Argon2id + AES-256-GCM, схема KEK/DEK). Приложение
для Windows выпущено (GUI, автозаполнение через расширение браузера, passkeys, TOTP,
проверка утечек HIBP, Windows Hello, синхронизация через iCloud Drive / Google Drive,
автообновления); Android и iOS — в работе на том же ядре.

## Стек

C# / .NET 10 (LTS; мобильная целевая платформа — net9.0): ядро на чистом .NET,
Avalonia (Windows), .NET MAUI (Android/iOS + расширение AutoFill), Velopack
(установщик и обновления), xUnit. Расширение браузера — JavaScript (Chrome/Firefox,
native messaging). Эталон криптосхемы — Python (`reference/`).

## Карта каталогов

- `core/IPasswrd.Core/` — ядро: `Vault.cs`, `Crypto.cs`, `VaultDocument.cs`,
  `RecoveryCode.cs`, `Totp.cs`, `Generator.cs`, `BreachCheck.cs`, `SecurityAudit.cs`,
  `Import/` (Chrome, Edge, Yandex, Kaspersky), `Lockout.cs`, `Autolock.cs`.
  Без зависимостей от UI.
- `windows/IPasswrd.App/` — десктоп (Avalonia): синхронизация, Windows Hello,
  мост к браузеру, обновления. `windows/IPasswrd.Host/` — native-messaging хост.
  `windows/extension/` — расширение браузера. Скрипты сборки — `windows/*.cmd`.
- `mobile/` — MAUI-приложение и `IPasswrd.AutoFill` (см. `README-Android.md`,
  `README-iOS.md`).
- `tests/IPasswrd.Core.Tests/` — тесты ядра (свойства безопасности).
- `reference/` — исполняемая Python-спецификация той же схемы.
- `tools/` — `IPasswrd.Cli` (dev-CLI), `ImportCheck`, упаковка MSIX/Store.
- `docs/` — страница загрузки и политика конфиденциальности (GitHub Pages из `/docs`).
- `prototype/` — HTML-прототип интерфейса.

## Сборка и тесты

```bash
dotnet test                                   # из корня: сборка + тесты ядра
dotnet test tests/IPasswrd.Core.Tests -f net10.0
dotnet test tests/IPasswrd.Core.Tests -f net9.0
dotnet build windows/IPasswrd.App/IPasswrd.App.csproj -c Release
cd reference && python3 test_vault_reference.py   # эталон схемы
```

Windows-релиз: `windows/publish-installer.cmd <версия>` →
`windows/upload-release.cmd <версия>` (GitHub Releases). CI: `.github/workflows/`
(`tests.yml`, `android-build.yml`, `ios-build.yml`).

## Правила внесения изменений

- **Криптосхема и формат хранилища** — менять только вместе с тестами: свойство
  безопасности закрепляется тестом в `tests/IPasswrd.Core.Tests` и, если меняется
  схема, в `reference/`. Обе реализации должны оставаться согласованными.
- Совместимость: существующие хранилища пользователей должны открываться новой
  версией; изменение формата — только с миграцией и версией в заголовке.
- Каждый контекст AES-GCM — со своей строкой AAD (см. `SECURITY.md`, раздел 1).
- Не добавлять телеметрию и сетевые обращения, кроме описанных в `SECURITY.md`
  (HIBP k-anonymity, выбранная пользователем синхронизация, обновления).
- Мастер-пароль, ключи и содержимое хранилища никогда не логировать.
- Перед коммитом: `dotnet test` зелёный, приложение для Windows собирается.
- Коммиты — небольшие и осмысленные, без трейлеров `Co-authored-by`.
- Уязвимости сообщать приватно по адресу из `SECURITY.md`, не через публичный issue.

## Секреты

В репозитории не должно быть секретов. Подпись Android-сборки берётся из секретов
GitHub Actions (`ANDROID_KEYSTORE_B64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEY_ALIAS`);
ключи подписи, сертификаты, тестовые хранилища (`/demo-vault/`), сборочные
артефакты и релизы перечислены в `.gitignore`. OAuth client id для синхронизации с
Google Drive — публичный идентификатор установленного приложения; собственный
`client_secret` можно задать локально, но не коммитить.
