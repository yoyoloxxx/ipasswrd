using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IPasswrd.Core;
using IPasswrd.Core.Import;

namespace IPasswrd.App;

public partial class MainWindow : Window
{
    private Vault? _vault;
    private string _section = "all";
    private string? _toolMode;            // null = section list; else authenticator/generator/security/settings

    private int _fails;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;
    private DispatcherTimer? _lockTimer;

    private string? _currentId;
    private string? _editId;              // null = adding a new item
    private string _editType = "account";
    private VaultItem? _editExisting;     // item being edited (to preserve hidden fields like totp)
    private Dictionary<string, TextBox> _editControls = new();

    // live TOTP refresh (detail pane + authenticator)
    private DispatcherTimer? _detailTimer;
    private readonly List<(string Secret, TextBlock Code, TextBlock Ring)> _liveTotps = new();

    // authenticator "add code" inline form
    private bool _authAdding;
    private string? _authEditId;          // when set, the form edits this standalone code instead of adding a new one
    private TextBox? _authName, _authAccount, _authSecret;
    private TextBlock? _authError;

    // auto-lock on inactivity
    private int _autolockMinutes;
    private Dictionary<string, string> _siteNames = new(StringComparer.Ordinal);   // exact-URL -> custom group name
    private HashSet<string> _keepAsIs = new(StringComparer.Ordinal);               // exact-URLs where the "shorten domain" hint was dismissed
    private IReadOnlyList<string> _groupIds = System.Array.Empty<string>();          // logins of the currently-open site group
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    // folder sync (the vault file lives in a synced folder — iCloud Drive today, Google Drive later)
    private string? _syncPath;            // full path of the synced vault file; null = local only
    private string _syncProvider = "";    // "icloud" | "google" | "" — which backend the sync is connected through
    private DateTime _vaultStamp;         // mtime of the vault file as we last wrote/merged it
    private string _vaultHash = "";       // SHA-256 of the bytes we last wrote (loop guard)

    // tray (close hides; the app keeps syncing in the background)
    private TrayIcon? _tray;
    private bool _reallyExit;

    // generator state
    private int _genLen = 20;
    private bool _genUpper = true, _genLower = true, _genDigits = true, _genSymbols = true, _genNoAmbig = true;
    private TextBlock? _genOut;
    private TextBlock? _genEntropy;

    // ---------- prototype palette (resolved per theme from App.axaml dictionaries) ----------
    private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, monospace");
    private bool _light;
    private string _lang = "ru";

    // Russian → English for every static UI string. The tree-walk localizer applies this
    // (and its reverse) in place, so most call sites keep their Russian literals untouched.
    private static readonly Dictionary<string, string> EnMap = new()
    {
        // sidebar / sections / tools
        ["Все записи"] = "All items", ["Аккаунты"] = "Accounts", ["Ключи доступа"] = "Passkeys",
        ["Карты"] = "Cards", ["Документы"] = "Documents", ["Заметки"] = "Notes", ["ИНСТРУМЕНТЫ"] = "TOOLS",
        ["Аутентификатор"] = "Authenticator", ["Генератор"] = "Generator", ["Проверка"] = "Security", ["Настройки"] = "Settings",
        ["Локальный сейф"] = "Local storage", ["без синхронизации"] = "no sync",
        ["Импорт из файла"] = "Import from file", ["Заблокировать"] = "Lock",
        ["Выбрать файл"] = "Choose file", ["Kaspersky, Chrome, Edge, Яндекс и другие"] = "Kaspersky, Chrome, Edge, Yandex and more",
        // list / search / empty states
        ["Поиск"] = "Search", ["Ничего не найдено"] = "Nothing found", ["Попробуйте другой запрос."] = "Try a different query.",
        ["Пока пусто"] = "Empty for now", ["Нажмите + вверху списка, чтобы добавить запись."] = "Press + at the top of the list to add an item.",
        ["Ключи доступа (passkey) появятся здесь после импорта. Вручную их добавлять не нужно."]
            = "Passkeys will appear here after import. No need to add them manually.",
        ["Вход по ключу доступа (passkey) настроен для этого аккаунта."] = "Passkey sign-in is set up for this account.",
        ["Создан "] = "Created ",
        // unlock
        ["Введите мастер-пароль"] = "Enter master password", ["Придумайте мастер-пароль"] = "Create a master password",
        ["Мастер-пароль"] = "Master password", ["Повторите пароль"] = "Repeat password",
        ["Открыть"] = "Open", ["Создать сейф"] = "Create storage",
        ["Пароли не совпадают."] = "Passwords don’t match.", ["Минимум 8 символов."] = "At least 8 characters.",
        ["Неверный мастер-пароль. Следующая попытка заблокирует вход."] = "Wrong master password. The next attempt will lock sign-in.",
        // detail
        ["Сейф IPasswrd"] = "IPasswrd storage", ["Имя пользователя"] = "Username", ["Пароль"] = "Password",
        ["ненадёжный"] = "weak", ["средний"] = "medium", ["надёжный"] = "strong",
        ["Код проверки"] = "One-time code", ["Веб-сайт"] = "Website", ["Сайт"] = "Site", ["Устройство"] = "Device",
        ["Этот пароль используется и на других сайтах. Стоит сделать его уникальным."] = "This password is reused on other sites. Consider making it unique.",
        ["Ненадёжный пароль: слишком короткий или предсказуемый."] = "Weak password: too short or predictable.",
        ["Номер карты"] = "Card number", ["Срок действия"] = "Expires", ["Владелец"] = "Cardholder",
        ["Серия и номер"] = "Series and number", ["Кем выдан"] = "Issued by", ["Заметка"] = "Note",
        ["Вход по ключу (passkey) — пароль вводить не нужно."] = "Passkey sign-in — no password needed.",
        // editor
        ["Изменение записи"] = "Edit item", ["Название"] = "Name", ["Аккаунт"] = "Account", ["Ключ доступа"] = "Passkey",
        ["Карта"] = "Card", ["Документ"] = "Document", ["Срок (ММ/ГГ)"] = "Expiry (MM/YY)", ["Текст"] = "Text",
        ["Сгенерировать"] = "Generate", ["Сохранить"] = "Save", ["← Отмена"] = "← Cancel",
        // generator
        ["Генератор паролей"] = "Password generator", ["Создавайте надёжные пароли и сохраняйте их прямо в сейф."] = "Create strong passwords and save them straight to storage.",
        ["Копировать"] = "Copy", ["Обновить"] = "Refresh", ["Скопировано"] = "Copied", ["Длина"] = "Length",
        ["Заглавные буквы"] = "Uppercase", ["Строчные буквы"] = "Lowercase", ["Цифры"] = "Digits", ["Символы"] = "Symbols",
        ["Исключать похожие"] = "Exclude similar", ["выберите хотя бы один набор символов"] = "pick at least one character set",
        // security
        ["Проверка безопасности"] = "Security check",
        ["Слабые и повторяющиеся пароли в вашем сейфе. Проверка идёт локально, без сети."] = "Weak and reused passwords in your storage. Checked locally, no network.",
        ["Пока нет аккаунтов"] = "No accounts yet", ["Хорошая защита"] = "Good protection", ["Есть над чем поработать"] = "Room for improvement",
        ["Ненадёжные пароли"] = "Weak passwords", ["Повторяющиеся пароли"] = "Reused passwords",
        ["слабый"] = "weak", ["повтор"] = "reused", ["Проблем не найдено — все пароли надёжные и уникальные."] = "No problems found — all passwords are strong and unique.",
        // authenticator
        ["Коды двухфакторной проверки считаются локально и обновляются каждые 30 секунд."] = "Two-factor codes are computed locally and refresh every 30 seconds.",
        ["Пока нет кодов"] = "No codes yet",
        ["Добавьте код кнопкой выше — вставьте секрет или ссылку otpauth://. Коды, импортированные из других менеджеров, тоже появятся здесь."]
            = "Add a code with the button above — paste a secret or an otpauth:// link. Codes imported from other managers appear here too.",
        ["Добавить код"] = "Add code", ["Новый код проверки"] = "New verification code", ["Изменение кода"] = "Editing code", ["Сохранить код"] = "Save code",
        ["Секрет"] = "Secret", ["Удалить код"] = "Delete code",
        ["Название — например, GitHub"] = "Name — e.g. GitHub",
        ["Аккаунт — имя или почта (необязательно)"] = "Account — name or email (optional)",
        ["Секрет или ссылка otpauth://"] = "Secret or otpauth:// link",
        ["Не удаётся прочитать секрет. Вставьте ключ (Base32) или ссылку otpauth://."]
            = "Couldn’t read the secret. Paste a Base32 key or an otpauth:// link.",
        // settings
        ["Приложение, защита и синхронизация."] = "App, security and sync.",
        ["Автоблокировка"] = "Auto-lock", ["Заблокировать сейф после простоя"] = "Lock the storage after inactivity",
        ["Выкл"] = "Off", ["1 минута"] = "1 minute", ["5 минут"] = "5 minutes", ["15 минут"] = "15 minutes",
        ["1 час"] = "1 hour", ["3 часа"] = "3 hours", ["12 часов"] = "12 hours",
        ["Сменить пароль от сейфа"] = "Change the storage password", ["Изменить"] = "Change", ["Отмена"] = "Cancel",
        ["Текущий пароль"] = "Current password", ["Новый пароль (минимум 8)"] = "New password (min 8)", ["Повторите новый пароль"] = "Repeat new password",
        ["Сменить пароль"] = "Change password", ["Новый пароль — минимум 8 символов."] = "New password — at least 8 characters.",
        ["Новые пароли не совпадают."] = "New passwords don’t match.", ["Текущий пароль неверный."] = "Current password is incorrect.",
        ["Мастер-пароль изменён. Он потребуется при следующем входе."] = "Master password changed. You’ll need it next time you sign in.",
        ["Тема оформления"] = "Theme", ["Тёмное или светлое оформление"] = "Dark or light appearance", ["Тёмная"] = "Dark", ["Светлая"] = "Light",
        // clipboard auto-clear
        ["Очистка буфера обмена"] = "Clear clipboard", ["Стирать скопированный пароль через заданное время"] = "Erase a copied password after a set time",
        ["15 секунд"] = "15 seconds", ["30 секунд"] = "30 seconds", ["1 минута"] = "1 minute", ["2 минуты"] = "2 minutes",
        ["Код проверки (2FA)"] = "One-time code (2FA)", ["Необязательно — ключ Base32 или ссылка otpauth://"] = "Optional — Base32 key or an otpauth:// link",
        ["Открыть ключ доступа"] = "Open passkey",
        // browser extension install
        ["Расширение для браузера"] = "Browser extension", ["Автозаполнение в Chrome, Edge и других"] = "Autofill in Chrome, Edge and others",
        ["Установить"] = "Install", ["Скрыть"] = "Hide",
        ["Открыть папку расширения"] = "Open the extension folder", ["Открыть страницу расширений"] = "Open the extensions page",
        ["1. Откройте страницу расширений браузера (кнопка ниже)."] = "1. Open the browser's extensions page (button below).",
        ["2. Включите «Режим разработчика» в правом верхнем углу."] = "2. Turn on “Developer mode” in the top-right corner.",
        ["3. Нажмите «Загрузить распакованное» и выберите папку расширения (кнопка ниже — путь уже скопирован)."]
            = "3. Click “Load unpacked” and pick the extension folder (button below — the path is already copied).",
        ["4. Готово: значок IPasswrd появится на панели браузера."] = "4. Done: the IPasswrd icon appears on the browser toolbar.",
        ["Связь с браузером настроена. Осталось загрузить папку расширения по шагам выше."]
            = "The browser link is set up. Now load the extension folder using the steps above.",
        ["Не найден файл IPasswrd.Host.exe рядом с приложением. Переустановите IPasswrd целиком."]
            = "IPasswrd.Host.exe was not found next to the app. Reinstall IPasswrd in full.",
        ["Не найдена папка расширения рядом с приложением."] = "The extension folder was not found next to the app.",
        ["Не удалось зарегистрировать связь с браузером: "] = "Couldn’t register the browser link: ",
        // HIBP breach check
        ["Проверка по базе утечек"] = "Breach database check",
        ["Have I Been Pwned — крупнейшая база украденных паролей. Проверка k-анонимна: наружу уходят только первые 5 символов SHA-1, сам пароль не передаётся."]
            = "Have I Been Pwned is the largest database of stolen passwords. The check is k-anonymous: only the first 5 characters of the SHA-1 leave your machine, never the password itself.",
        ["Проверить пароли"] = "Check passwords", ["Проверяем…"] = "Checking…",
        ["Проверяем пароли в базе утечек…"] = "Checking passwords against the breach database…",
        ["Не удалось связаться с базой утечек. Проверьте интернет и попробуйте снова."]
            = "Couldn’t reach the breach database. Check your connection and try again.",
        ["Ни один пароль не найден в известных утечках."] = "No password was found in known breaches.",
        ["Эти пароли найдены в утечках — их стоит сменить:"] = "These passwords appear in breaches — you should change them:",
        ["в {0} утечках"] = "in {0} breaches",
        ["Язык"] = "Language", ["Язык интерфейса"] = "Interface language",
        ["Синхронизация"] = "Sync", ["Облачная синхронизация — в планах"] = "Cloud sync — planned",
        ["IPasswrd · локальный зашифрованный сейф (AES-256-GCM, Argon2id)"] = "IPasswrd · local encrypted storage (AES-256-GCM, Argon2id)",
        // import
        ["Импорт завершён"] = "Import complete", ["Формат не распознан или файл пуст."] = "Unrecognized format or empty file.",
        // remove duplicates
        ["Удалить дубликаты"] = "Remove duplicates",
        ["Схлопнуть одинаковые аккаунты (один логин и пароль, разные поддомены)"] = "Collapse identical accounts (same login & password, different subdomains)",
        ["Очистить"] = "Clean up", ["Удаление дубликатов"] = "Removing duplicates",
        ["Удалено дублей"] = "Duplicates removed", ["Осталось записей"] = "Records remaining",
        ["Дубликаты не найдены"] = "No duplicates found",
        // tooltips
        ["Показать"] = "Show", ["Скрыть"] = "Hide", ["В избранное"] = "Add to favorites", ["Убрать из избранного"] = "Remove from favorites",
        ["Удалить"] = "Delete", ["Нажмите ещё раз, чтобы удалить"] = "Click again to delete", ["Открыть сайт"] = "Open site", ["Новая запись"] = "New item",
        ["Сократить до"] = "Shorten to", ["Аккаунт на этом сайте"] = "Account on this site", ["Оставить"] = "Keep",
        // fragments used inside interpolated strings (via Tr)
        ["бит энтропии"] = "bits of entropy", ["Изменено"] = "Modified",
        ["Проверено аккаунтов"] = "Accounts checked", ["Надёжных"] = "Strong",
        ["ненадёжных"] = "weak", ["повторяются"] = "reused",
        ["Добавлено"] = "Added", ["Пропущено дубликатов"] = "Skipped duplicates", ["Всего в файле"] = "Total in file",
        ["Ошибка импорта: "] = "Import error: ",
        ["Выберите файл экспорта паролей"] = "Choose a password export file", ["Экспорт паролей (CSV / TXT)"] = "Password export (CSV / TXT)",
        // folder sync
        ["синхронизация включена"] = "sync on",
        ["Хранить файл сейфа в iCloud Drive"] = "Keep the storage file in iCloud Drive",
        ["Включить"] = "Turn on", ["Отключить"] = "Turn off",
        ["iCloud Drive не найден. Установите iCloud для Windows и включите iCloud Drive."]
            = "iCloud Drive not found. Install iCloud for Windows and turn on iCloud Drive.",
        ["В iCloud уже лежит другой сейф. Сначала решите, какой оставить."]
            = "A different storage file is already in iCloud. Decide which one to keep first.",
        ["Сначала откройте сейф."] = "Open the storage first.",
        ["Ошибка: "] = "Error: ",
        // iCloud sign-in
        ["Войти через iCloud"] = "Sign in with iCloud", ["Google-синхронизация — скоро"] = "Google sync — coming soon",
        // Google Drive sync
        ["Подключить Google"] = "Connect Google", ["Как получить ключи?"] = "How to get the keys?",
        ["Открываю браузер для входа в Google…"] = "Opening the browser to sign in to Google…",
        ["Заполните Client ID и Client secret."] = "Fill in the Client ID and Client secret.",
        ["Сначала вставьте Client ID и Client secret от Google."] = "First paste your Google Client ID and Client secret.",
        ["Не удалось войти в Google: "] = "Couldn’t sign in to Google: ", ["Ошибка Google Drive: "] = "Google Drive error: ",
        ["В Google уже лежит другой сейф. Сначала решите, какой оставить."]
            = "A different vault is already in Google Drive. Decide which one to keep first.",
        ["Вставьте Client ID и Client secret из вашего проекта Google Cloud (тип «Desktop app»). Пароль от Google приложение не увидит — вход идёт в браузере."]
            = "Paste the Client ID and Client secret from your Google Cloud project (type “Desktop app”). The app never sees your Google password — sign-in happens in the browser.",
        ["Выход"] = "Exit",
        ["Запускать с Windows"] = "Start with Windows", ["Открывать в трее при входе в систему"] = "Open in the tray at sign-in",
        ["Синхронизация сейфа через ваш Apple ID"] = "Sync the storage through your Apple ID",
        ["подключено"] = "connected",
        ["Не найдена папка iCloud Drive. Я открыл окно iCloud — войдите в Apple ID, включите iCloud Drive и нажмите кнопку ещё раз. Если окно не открылось, установите «iCloud» из Microsoft Store."]
            = "iCloud Drive folder not found. I opened the iCloud window — sign in with your Apple ID, turn on iCloud Drive and press the button again. If no window appeared, install “iCloud” from the Microsoft Store.",
    };

    private static readonly Dictionary<string, string> RuMap = BuildReverse();
    private static Dictionary<string, string> BuildReverse()
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in EnMap) d[kv.Value] = kv.Key;
        return d;
    }

    /// <summary>Translate a single Russian literal to the current language (for popup/menu content built off-tree).</summary>
    private string Tr(string ru) => _lang == "en" && EnMap.TryGetValue(ru, out var e) ? e : ru;

    /// <summary>Walk the window and translate every static string to the current language (bidirectional, idempotent).</summary>
    private void Relocalize()
    {
        var map = _lang == "en" ? EnMap : RuMap;
        foreach (var v in this.GetLogicalDescendants())
        {
            switch (v)
            {
                case TextBlock tb when tb.Text is string t && map.TryGetValue(t, out var et): tb.Text = et; break;
                case TextBox box when box.Watermark is string w && map.TryGetValue(w, out var ew): box.Watermark = ew; break;
                case Button btn when btn.Content is string bc && map.TryGetValue(bc, out var eb): btn.Content = eb; break;
            }
        }
    }

    private IBrush Bg = Brushes.Black, Surface = Brushes.Black, Surface2 = Brushes.Black,
                   Text = Brushes.White, Text2 = Brushes.Gray, Text3 = Brushes.Gray,
                   Accent = Brushes.Goldenrod, Ok = Brushes.Green, Warn = Brushes.Orange, Bad = Brushes.Red,
                   Hair = Brushes.Transparent, HairStrong = Brushes.Transparent,
                   BadWash = Brushes.Transparent, WarnWash = Brushes.Transparent, AccentWash = Brushes.Transparent;

    // Brand mark = the tray icon ("Латунный всплеск"), embedded as a PNG (TrayIconB64) and shown
    // straight as an image. Fresh instance per call — one bitmap can't live in two visual slots.
    private Control BrandLogo()
    {
        try
        {
            using var ms = new System.IO.MemoryStream(Convert.FromBase64String(TrayIconB64));
            return new Image { Source = new Avalonia.Media.Imaging.Bitmap(ms), Stretch = Stretch.Uniform };
        }
        catch { return new Panel(); }
    }

    private void InstallBrandLogos()
    {
        if (LogoSide is not null) LogoSide.Content = BrandLogo();
        if (LogoUnlock is not null) LogoUnlock.Content = BrandLogo();
    }

    private IBrush Res(string key)
    {
        var variant = _light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (this.TryFindResource(key, variant, out var v) && v is IBrush b) return b;
        return Brushes.Magenta;
    }

    private void ApplyTheme()
    {
        Bg = Res("IpBg"); Surface = Res("IpSurface"); Surface2 = Res("IpSurface2");
        Text = Res("IpText"); Text2 = Res("IpText2"); Text3 = Res("IpText3");
        Accent = Res("IpAccent"); Ok = Res("IpOk"); Warn = Res("IpWarn"); Bad = Res("IpBad");
        Hair = Res("IpHair"); HairStrong = Res("IpHairStrong");
        BadWash = Res("IpBadWash"); WarnWash = Res("IpWarnWash"); AccentWash = Res("IpAccentWash");
        InstallBrandLogos();
    }

    private static readonly (string Label, string Type, string Icon)[] _sections =
    {
        ("Все записи",        "all",     "grid"),
        ("Аккаунты",          "account", "key"),
        ("Карты",             "card",    "card"),
        ("Документы",         "doc",     "doc"),
        ("Заметки",           "note",    "note"),
        ("Ключи доступа",     "passkey", "passkey"),
    };

    private static readonly (string Label, string Type, string Icon)[] _tools =
    {
        ("Аутентификатор", "authenticator", "timer"),
        ("Генератор",      "generator",     "wand"),
        ("Проверка",       "security",      "shield"),
        ("Настройки",      "settings",      "gear"),
    };

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        MaterializeSyncCopy();
        RequestedThemeVariant = _light ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyTheme();
        SetupTray();
        StartBrowserBridge();   // named-pipe server for the browser extension (see BrowserBridge.cs)
        PointerMoved += (_, _) => _lastActivity = DateTimeOffset.UtcNow;
        KeyDown += (_, _) => _lastActivity = DateTimeOffset.UtcNow;
        SetupUnlock();

        // --tray (background) start is handled in App.axaml.cs: MainWindow is simply never
        // assigned/shown. No Opened+=Hide hack here — it used to re-hide the window the first
        // time the user explicitly opened it from the browser.
    }

    // ================= tray =================

    // App/tray icons embedded as 128x128 PNGs. Robust for single-file publish;
    // avares/.ico decoding proved unreliable for the runtime window icon. Masters live in Assets/.
    private const string AppIconB64 =
        "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";
    private const string TrayIconB64 =
        "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";

    private void SetupTray()
    {
        try
        {
            Icon = IconFromB64(AppIconB64);                                                  // window / taskbar
            _tray = new TrayIcon { Icon = IconFromB64(TrayIconB64), ToolTipText = "IPasswrd" };   // tray (brass splash)
            _tray.Clicked += (_, _) => ShowFromTray();

            var menu = new NativeMenu();
            var open = new NativeMenuItem(Tr("Открыть"));
            open.Click += (_, _) => ShowFromTray();
            var lockItem = new NativeMenuItem(Tr("Заблокировать"));
            lockItem.Click += (_, _) => { OnLockClick(null, new RoutedEventArgs()); ShowFromTray(); };
            var exit = new NativeMenuItem(Tr("Выход"));
            exit.Click += (_, _) => ExitApp();
            menu.Add(open);
            menu.Add(lockItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exit);
            _tray.Menu = menu;

            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        }
        catch { /* tray is a nicety; the app still works windowed */ }
    }

    private static WindowIcon IconFromB64(string b64)
    {
        using var ms = new System.IO.MemoryStream(Convert.FromBase64String(b64));
        return new WindowIcon(ms);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Called when the user launches the app again (single-instance): surface this window.</summary>
    public void BringToFront() => ShowFromTray();

    private void ExitApp()
    {
        _reallyExit = true;
        try { SaveQuickUnlock(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
        else
            Close();
    }

    /// <summary>The ✕ button hides to tray (background sync keeps running); real exit is in the tray menu.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ================= app settings =================

    /// <summary>Per-device data dir: settings + lockout live here and are never synced.</summary>
    private static string LocalDataDir()
    {
        string? env = Environment.GetEnvironmentVariable("IPASSWRD_VAULT");
        if (!string.IsNullOrWhiteSpace(env)) return System.IO.Path.GetDirectoryName(env)!;
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPasswrd");
    }

    private static string SettingsPath() => System.IO.Path.Combine(LocalDataDir(), "settings.json");

    private sealed class AppSettings
    {
        public int AutolockMinutes { get; set; }
        public bool Light { get; set; }
        public string Lang { get; set; } = "ru";
        public string? SyncPath { get; set; }
        public Dictionary<string, string>? SiteNames { get; set; }
        public HashSet<string>? KeepAsIs { get; set; }
        public string? SyncProvider { get; set; }
        public int ClipboardClearSeconds { get; set; } = 30;
    }

    private void LoadSettings()
    {
        try
        {
            string p = SettingsPath();
            if (!System.IO.File.Exists(p)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(p));
            if (s is not null) { _autolockMinutes = s.AutolockMinutes; _light = s.Light; _lang = string.IsNullOrEmpty(s.Lang) ? "ru" : s.Lang; _syncPath = s.SyncPath; _siteNames = s.SiteNames is null ? new(StringComparer.Ordinal) : new(s.SiteNames, StringComparer.Ordinal); _keepAsIs = s.KeepAsIs is null ? new(StringComparer.Ordinal) : new(s.KeepAsIs, StringComparer.Ordinal); _syncProvider = s.SyncProvider ?? ""; _clipboardClearSeconds = s.ClipboardClearSeconds; }
        }
        catch { /* ignore */ }
    }

    private void SaveSettings()
    {
        try
        {
            string p = SettingsPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllText(p, JsonSerializer.Serialize(new AppSettings { AutolockMinutes = _autolockMinutes, Light = _light, Lang = _lang, SyncPath = _syncPath, SiteNames = _siteNames.Count > 0 ? _siteNames : null, KeepAsIs = _keepAsIs.Count > 0 ? _keepAsIs : null, SyncProvider = string.IsNullOrEmpty(_syncProvider) ? null : _syncProvider, ClipboardClearSeconds = _clipboardClearSeconds }));
        }
        catch { /* best effort */ }
        SavePrefsToVault();   // mirror the syncable prefs (site names + keep-marks) into the vault
    }

    // Custom site names and dismissed "shorten domain" hints are the only content the user
    // creates by hand that otherwise stayed in local settings.json. Keep them in a single
    // fixed-id record inside the encrypted vault so they ride the same iCloud sync as everything
    // else (last-write-wins per record, like the rest of the vault).
    private const string PrefsRecordId = "a11a5000-0000-4000-8000-000000000001";

    private void SavePrefsToVault()
    {
        if (_vault is null) return;
        try
        {
            var item = new VaultItem { Type = "meta", Title = "prefs" };
            item.Fields["siteNames"] = JsonSerializer.Serialize(_siteNames);
            item.Fields["keepAsIs"] = JsonSerializer.Serialize(_keepAsIs.ToList());
            _vault.Put(PrefsRecordId, item);
            Save();
        }
        catch { /* best effort */ }
    }

    private void LoadPrefsFromVault()
    {
        if (_vault is null) return;
        try
        {
            var rec = _vault.Items().FirstOrDefault(x => x.Id == PrefsRecordId);
            if (rec is null)
            {
                // First run after this update: push whatever is in local settings.json up into the vault.
                if (_siteNames.Count > 0 || _keepAsIs.Count > 0) SavePrefsToVault();
                return;
            }
            if (rec.Item.Fields.TryGetValue("siteNames", out var sn) && !string.IsNullOrWhiteSpace(sn))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(sn);
                if (d is not null) _siteNames = new Dictionary<string, string>(d, StringComparer.Ordinal);
            }
            if (rec.Item.Fields.TryGetValue("keepAsIs", out var ka) && !string.IsNullOrWhiteSpace(ka))
            {
                var l = JsonSerializer.Deserialize<List<string>>(ka);
                if (l is not null) _keepAsIs = new HashSet<string>(l, StringComparer.Ordinal);
            }
        }
        catch { /* best effort — fall back to the local settings.json values */ }
    }

    // ================= title bar =================

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ================= vault file =================

    private string VaultPath()
    {
        string? env = Environment.GetEnvironmentVariable("IPASSWRD_VAULT");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string local = System.IO.Path.Combine(LocalDataDir(), "vault.ipvault");
        if (!string.IsNullOrEmpty(_syncPath))
        {
            // use the synced file whenever its folder is reachable (the file itself may not
            // exist yet right after enabling); fall back to local only if iCloud is unavailable
            string? dir = System.IO.Path.GetDirectoryName(_syncPath);
            if (dir is not null && System.IO.Directory.Exists(dir)) return _syncPath!;
            return local;
        }
        return local;
    }

    private bool Creating => !System.IO.File.Exists(VaultPath());

    private void Save()
    {
        string p = VaultPath();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        byte[] data = _vault!.Serialize();
        System.IO.File.WriteAllBytes(p, data);
        try { _vaultStamp = System.IO.File.GetLastWriteTimeUtc(p); } catch { _vaultStamp = default; }
        _vaultHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        GooglePushKick();   // if Google sync is on, mirror this write up to Drive (best-effort, async)
    }

    // ================= brute-force lockout =================

    private bool IsLocked => DateTimeOffset.UtcNow < _lockedUntil;

    private static string LockoutPath() => System.IO.Path.Combine(LocalDataDir(), "lockout.json");

    // ================= start with Windows =================
    // A per-user Run entry launches the app minimized to tray on sign-in (like Kaspersky).

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "IPasswrd";

    private static bool IsAutostartOn()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private static void SetAutostart(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null) return;
            if (on)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) k.SetValue(RunValue, $"\"{exe}\" --tray");
            }
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { /* best effort */ }
    }

    // ================= quick unlock (DPAPI) =================
    // After a successful unlock the session key is stored encrypted by Windows (DPAPI,
    // current user) with an expiry equal to the auto-lock interval. Locking — manual or
    // by timer — wipes it, so the master password is asked exactly when the user expects.

    private static string QuickUnlockPath() => System.IO.Path.Combine(LocalDataDir(), "quickunlock.bin");
    private static readonly byte[] QuickEntropy = System.Text.Encoding.UTF8.GetBytes("IPasswrd.QuickUnlock.v1");
    private DateTimeOffset _quickRefreshedAt = DateTimeOffset.MinValue;

    private sealed class QuickUnlockData
    {
        public string Dek { get; set; } = "";
        public long ExpiresAt { get; set; }          // unix seconds; 0 = no expiry (auto-lock off)
    }

    private void SaveQuickUnlock()
    {
        if (_vault is null) return;
        try
        {
            long exp = _autolockMinutes > 0
                ? DateTimeOffset.UtcNow.AddMinutes(_autolockMinutes).ToUnixTimeSeconds()
                : 0;
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new QuickUnlockData
            {
                Dek = Convert.ToBase64String(_vault.ExportSessionKey()),
                ExpiresAt = exp,
            });
            byte[] prot = System.Security.Cryptography.ProtectedData.Protect(
                plain, QuickEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            System.IO.Directory.CreateDirectory(LocalDataDir());
            System.IO.File.WriteAllBytes(QuickUnlockPath(), prot);
            _quickRefreshedAt = DateTimeOffset.UtcNow;
        }
        catch { /* quick unlock is best-effort; the password path always works */ }
    }

    private static void WipeQuickUnlock()
    {
        try { string p = QuickUnlockPath(); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
        catch { /* ignore */ }
    }

    private bool TryQuickUnlock()
    {
        try
        {
            string p = QuickUnlockPath();
            if (Creating || !System.IO.File.Exists(p)) return false;
            byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(
                System.IO.File.ReadAllBytes(p), QuickEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var d = JsonSerializer.Deserialize<QuickUnlockData>(plain);
            if (d is null || string.IsNullOrEmpty(d.Dek)) { WipeQuickUnlock(); return false; }
            if (d.ExpiresAt != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > d.ExpiresAt) { WipeQuickUnlock(); return false; }
            _vault = Vault.UnlockWithSessionKey(System.IO.File.ReadAllBytes(VaultPath()), Convert.FromBase64String(d.Dek));
            return true;
        }
        catch { WipeQuickUnlock(); return false; }
    }

    private sealed class LockoutData
    {
        public int Fails { get; set; }
        public long LockedUntil { get; set; }
    }

    private void LoadLockout()
    {
        try
        {
            string p = LockoutPath();
            if (!System.IO.File.Exists(p)) { _fails = 0; _lockedUntil = DateTimeOffset.MinValue; return; }
            var d = JsonSerializer.Deserialize<LockoutData>(System.IO.File.ReadAllText(p));
            if (d is not null)
            {
                _fails = d.Fails;
                _lockedUntil = DateTimeOffset.FromUnixTimeSeconds(d.LockedUntil);
            }
        }
        catch { /* ignore corrupt state */ }
    }

    private void SaveLockout()
    {
        try
        {
            string p = LockoutPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllText(p, JsonSerializer.Serialize(
                new LockoutData { Fails = _fails, LockedUntil = _lockedUntil.ToUnixTimeSeconds() }));
        }
        catch { /* best effort */ }
    }

    private void ResetLockout()
    {
        _fails = 0;
        _lockedUntil = DateTimeOffset.MinValue;
        try { string p = LockoutPath(); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { /* ignore */ }
    }

    private void SetUnlockEnabled(bool on)
    {
        MasterBox.IsEnabled = on;
        MasterBox2.IsEnabled = on;
        UnlockButton.IsEnabled = on;
    }

    private void StartLockCountdown()
    {
        SetUnlockEnabled(false);
        UpdateLockText();
        _lockTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockTimer.Tick -= OnLockTick;
        _lockTimer.Tick += OnLockTick;
        _lockTimer.Start();
    }

    private void OnLockTick(object? sender, EventArgs e)
    {
        if (IsLocked) { UpdateLockText(); return; }
        _lockTimer?.Stop();
        UnlockError.IsVisible = false;
        SetUnlockEnabled(true);
        MasterBox.Focus();
    }

    private void UpdateLockText()
    {
        TimeSpan left = _lockedUntil - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        UnlockError.Text = $"Слишком много попыток. Повторите через {FormatSpan(left)}";
        UnlockError.IsVisible = true;
    }

    private static string FormatSpan(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} д {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    // ================= unlock =================

    private void SetupUnlock()
    {
        LoadLockout();
        _detailTimer?.Stop();

        if (TryQuickUnlock())      // session key still valid — no password prompt
        {
            EnterVault();
            return;
        }

        if (Creating)
        {
            UnlockSub.Text = "Придумайте мастер-пароль";
            MasterBox2.IsVisible = true;
            UnlockButton.Content = "Создать сейф";
        }
        else
        {
            UnlockSub.Text = "Введите мастер-пароль";
            MasterBox2.IsVisible = false;
            UnlockButton.Content = "Открыть";
        }
        UnlockError.IsVisible = false;
        MasterBox.Text = "";
        MasterBox2.Text = "";
        UnlockScreen.IsVisible = true;
        VaultScreen.IsVisible = false;
        EditorScreen.IsVisible = false;

        if (!Creating && IsLocked)
        {
            StartLockCountdown();
        }
        else
        {
            SetUnlockEnabled(true);
            MasterBox.Focus();
        }
        Relocalize();
    }

    private void OnMasterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnUnlockClick(sender, new RoutedEventArgs());
    }

    private void OnUnlockClick(object? sender, RoutedEventArgs e)
    {
        string pw = MasterBox.Text ?? "";
        try
        {
            if (Creating)
            {
                if (pw != (MasterBox2.Text ?? "")) { Err("Пароли не совпадают."); return; }
                if (pw.Length < 8) { Err("Минимум 8 символов."); return; }
                _vault = Vault.Create(pw);
                Save();
            }
            else
            {
                _vault = Vault.Unlock(System.IO.File.ReadAllBytes(VaultPath()), pw);
            }
        }
        catch (WrongMasterPasswordException)
        {
            _fails++;
            TimeSpan pen = Lockout.PenaltyFor(_fails);
            if (pen > TimeSpan.Zero)
            {
                _lockedUntil = DateTimeOffset.UtcNow + pen;
                SaveLockout();
                MasterBox.Text = "";
                StartLockCountdown();
            }
            else
            {
                SaveLockout();
                int left = Lockout.AttemptsLeft(_fails);
                Err(left > 0
                    ? $"Неверный мастер-пароль. Осталось попыток до блокировки: {left}"
                    : "Неверный мастер-пароль. Следующая попытка заблокирует вход.");
            }
            return;
        }
        catch (Exception ex) { Err("Ошибка: " + ex.Message); return; }

        ResetLockout();
        SaveQuickUnlock();
        EnterVault();

        void Err(string m) { UnlockError.Text = Tr(m); UnlockError.IsVisible = true; }
    }

    private void OnLockClick(object? sender, RoutedEventArgs e)
    {
        _detailTimer?.Stop();
        _vault = null;
        WipePendingClipboard();
        WipeQuickUnlock();
        SetupUnlock();
    }

    // ================= vault view =================

    private void EnterVault()
    {
        _lockTimer?.Stop();
        _lastActivity = DateTimeOffset.UtcNow;
        try
        {
            string vp = VaultPath();
            _vaultStamp = System.IO.File.GetLastWriteTimeUtc(vp);
            _vaultHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(vp)));
        }
        catch { _vaultStamp = default; _vaultHash = ""; }
        _section = "all";
        _toolMode = null;
        ListTitle.Text = "Все записи";
        ToolPane.IsVisible = false;
        LoadPrefsFromVault();   // pull synced site names / keep-marks out of the vault before drawing
        RenderSidebar();
        LoadEntries();
        UnlockScreen.IsVisible = false;
        VaultScreen.IsVisible = true;

        _detailTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _detailTimer.Tick -= OnDetailTick;
        _detailTimer.Tick += OnDetailTick;
        _detailTimer.Start();

        if (_syncProvider == "google") GoogleResumeAfterUnlock();   // start from the freshest Drive copy
    }

    private void RenderSidebar()
    {
        if (_vault is null) return;
        SectionHost.Children.Clear();
        var all = _vault.Items().ToList();
        var attachedPk = AttachedPasskeyIds();   // hidden from «Все записи» (represented by their account) → excluded from that count only

        foreach (var s in _sections)
        {
            int count = s.Type == "all"
                ? all.Count(x => x.Item.Type != "totp" && x.Item.Type != "meta" && !(x.Item.Type == "passkey" && attachedPk.Contains(x.Id)))
                : all.Count(x => x.Item.Type == s.Type);   // the «Ключи доступа» pill shows every passkey
            bool active = _toolMode is null && _section == s.Type;
            string type = s.Type, label = s.Label;
            SectionHost.Children.Add(SideButton(label, s.Icon, count, active, () =>
            {
                _toolMode = null;
                _authAdding = false;
                _authEditId = null;
                _section = type;
                ListTitle.Text = label;
                ToolPane.IsVisible = false;
                LoadEntries();
                RenderSidebar();
            }));
        }

        SectionHost.Children.Add(new TextBlock
        {
            Text = "ИНСТРУМЕНТЫ", FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = Text3,
            Margin = new Thickness(10, 14, 10, 5),
        });

        foreach (var tl in _tools)
        {
            bool active = _toolMode == tl.Type;
            string type = tl.Type;
            SectionHost.Children.Add(SideButton(tl.Label, tl.Icon, null, active, () =>
            {
                _toolMode = type;
                ToolPane.IsVisible = true;
                RenderSidebar();
                ShowTool(type);
            }));
        }
        UpdateSyncChip();
        Relocalize();
    }

    private void UpdateSyncChip()
    {
        if (_syncProvider == "google")
        {
            SyncTitle.Text = "Google Drive";
            SyncSub.Text = _gdrive?.Email ?? Tr("синхронизация включена");
        }
        else if (_syncProvider == "icloud" || !string.IsNullOrEmpty(_syncPath))
        {
            SyncTitle.Text = "iCloud";
            SyncSub.Text = ICloudEmail() ?? Tr("синхронизация включена");
        }
        else
        {
            SyncTitle.Text = Tr("Локальный сейф");
            SyncSub.Text = Tr("без синхронизации");
        }
    }

    private string? _icloudEmailCache;
    private bool _icloudEmailProbed;

    /// <summary>Apple ID signed into iCloud for Windows, if discoverable (display only).</summary>
    private string? ICloudEmail()
    {
        if (_icloudEmailProbed) return _icloudEmailCache;
        _icloudEmailProbed = true;
        try
        {
            string p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "AppleInc.iCloud_nzyj5cx40ttqa", "LocalCache", "Local", "Outlook", "AccountInfo.ini");
            if (System.IO.File.Exists(p))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    System.IO.File.ReadAllText(p), @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
                if (m.Success) _icloudEmailCache = m.Value;
            }
        }
        catch { /* display-only nicety */ }
        return _icloudEmailCache;
    }

    /// <summary>Open the official Apple sign-in (iCloud for Windows). Auth stays with Apple — we never see the password.</summary>
    private static void TryOpenICloudApp()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\AppleInc.iCloud_nzyj5cx40ttqa!iCloud",
                UseShellExecute = true,
            });
        }
        catch { /* guidance text covers the manual path */ }
    }

    private Control SideButton(string label, string icon, int? count, bool active, Action onClick)
    {
        var ic = MakeIcon(IconData(icon), 17, active ? Accent : Text3, 1.7);
        ((Control)ic).Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn((Control)ic, 0);

        var lbl = new TextBlock
        {
            Text = label, Foreground = active ? Text : Text2, FontSize = 13,
            FontWeight = active ? FontWeight.SemiBold : FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(lbl, 1);

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        g.Children.Add((Control)ic);
        g.Children.Add(lbl);
        if (count is int c)
        {
            var cnt = new TextBlock { Text = c.ToString(), Foreground = Text3, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(cnt, 2);
            g.Children.Add(cnt);
        }

        var btn = new Button { Content = g };
        btn.Classes.Add("side");
        if (active) btn.Classes.Add("on");
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => LoadEntries();

    private void LoadEntries(bool selectFirst = true)
    {
        if (_vault is null) return;

        string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        IEnumerable<VaultEntry> items = _vault.Items()
            .Where(x => x.Item.Type != "totp" && x.Item.Type != "meta");   // 2FA codes live in the Authenticator; meta is the synced-prefs record
        if (_section != "all")
            items = items.Where(x => x.Item.Type == _section);   // a type section (incl. «Ключи доступа») lists every record of that type
        else
        {
            var attachedPk = AttachedPasskeyIds();   // in «Все записи» an attached passkey is represented by its account tile, so skip it here
            items = items.Where(x => !(x.Item.Type == "passkey" && attachedPk.Contains(x.Id)));
        }

        // Group accounts that live at the SAME address into one tile; everything else is its own tile.
        var groups = new Dictionary<string, List<VaultEntry>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var x in items)
        {
            string nu = x.Item.Type == "account" ? NormUrl(x.Item.Fields.GetValueOrDefault("url", "")) : "";
            string key = nu.Length > 0 ? "s:" + nu : "i:" + x.Id;
            if (!groups.TryGetValue(key, out var lst)) { lst = new List<VaultEntry>(); groups[key] = lst; order.Add(key); }
            lst.Add(x);
        }

        var rows = new List<EntryRow>();
        foreach (var key in order)
        {
            var g = groups[key];
            string label = GroupLabel(g);
            if (q.Length > 0)
            {
                bool hit = label.ToLowerInvariant().Contains(q)
                    || g.Any(e => (e.Item.Title + " " + Subtitle(e.Item) + " " + e.Item.Fields.GetValueOrDefault("url", "")).ToLowerInvariant().Contains(q));
                if (!hit) continue;
            }
            rows.Add(MakeGroupRow(g, label));
        }
        rows = rows.OrderByDescending(r => r.Fav).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();   // favorites first, then A→Z

        EntryList.ItemsSource = rows;
        ClearLiveTotps();

        if (rows.Count == 0)
        {
            EntryList.SelectedItem = null;
            DetailPanel.Children.Clear();
            if (_section == "passkey")
                DetailPanel.Children.Add(EmptyState("Ключи доступа",
                    "Ключи доступа (passkey) появятся здесь после импорта. Вручную их добавлять не нужно."));
            else if (q.Length > 0)
                DetailPanel.Children.Add(EmptyState("Ничего не найдено", "Попробуйте другой запрос."));
            else
                DetailPanel.Children.Add(EmptyState("Пока пусто", "Нажмите + вверху списка, чтобы добавить запись."));
            Relocalize();
            return;
        }

        if (selectFirst)
        {
            EntryList.SelectedIndex = 0;   // triggers OnEntrySelected -> ShowDetail
        }
        else
        {
            EntryList.SelectedItem = null;
            DetailPanel.Children.Clear();
        }
        Relocalize();
    }

    // Display label for a site group: custom group name if set, else the site host, else the record's own title.
    private string GroupLabel(List<VaultEntry> g)
    {
        var rep = g[0].Item;
        if (rep.Type is "account" or "passkey")   // name after the site only; the login shows as the subtitle
        {
            string url = rep.Fields.GetValueOrDefault("url", "");
            if (string.IsNullOrWhiteSpace(url)) url = rep.Fields.GetValueOrDefault("rpId", "");
            string t = GroupTitle(url);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return string.IsNullOrWhiteSpace(rep.Title) ? TypeLabel(rep.Type) : rep.Title;
    }

    private EntryRow MakeGroupRow(List<VaultEntry> g, string label)
    {
        var rep = g[0];
        int h = Hue(label);
        bool bad = g.Any(e => FlagsFor(e.Item).bad);
        bool warn = !bad && g.Any(e => FlagsFor(e.Item).warn);
        bool fav = g.Any(e => e.Item.Favorite);
        return new EntryRow
        {
            Id = rep.Id,
            Ids = g.Select(e => e.Id).ToList(),
            Title = label,
            Subtitle = Subtitle(rep.Item),
            Mono = Mono1(label),
            TileBg = TileBgFor(h),
            TileFg = TileFgFor(h),
            TileBorder = TileBorderFor(h),
            Fav = fav,
            HasBad = bad,
            HasWarn = warn,
            IsGroup = g.Count > 1,
            CountLabel = g.Count > 1 ? g.Count.ToString() : "",
        };
    }

    // ---- site grouping / naming helpers ----

    private static string NormUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        string s = url.Trim().ToLowerInvariant();
        int i = s.IndexOf("://"); if (i >= 0) s = s[(i + 3)..];
        if (s.StartsWith("www.")) s = s[4..];
        return s.TrimEnd('/');
    }

    private static string SiteLabel(string? url)
    {
        string s = NormUrl(url);
        int slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash];
        return s;
    }

    private string SiteName(string? url) => _siteNames.TryGetValue(NormUrl(url), out var n) ? n : "";

    private string GroupTitle(string? url)
    {
        string custom = SiteName(url);
        return !string.IsNullOrEmpty(custom) ? custom : SiteLabel(url);
    }

    // Title shown at the top of a record's card: the site/group name for accounts, else the record's own title.
    private string HeaderName(VaultItem item)
    {
        if (item.Type is "account" or "passkey")
        {
            string url = item.Fields.GetValueOrDefault("url", "");
            if (!string.IsNullOrWhiteSpace(url)) return GroupTitle(url);
        }
        return string.IsNullOrWhiteSpace(item.Title) ? TypeLabel(item.Type) : item.Title;
    }

    // monogram tile colours — darker foreground in light theme so the letter stays readable
    private IBrush TileBgFor(int h) => OklchBrush(_light ? 0.60 : 0.70, 0.10, h, 0.13);
    private IBrush TileFgFor(int h) => OklchBrush(_light ? 0.48 : 0.80, _light ? 0.12 : 0.11, h, 1.0);
    private IBrush TileBorderFor(int h) => OklchBrush(0.70, 0.10, h, 0.22);

    private static (bool bad, bool warn) FlagsFor(VaultItem it)
    {
        if (it.Type != "account") return (false, false);
        if (!it.Fields.TryGetValue("password", out var p) || string.IsNullOrEmpty(p)) return (false, false);
        return Auditor.Rate(p) switch
        {
            Strength.Weak => (true, false),
            Strength.Fair => (false, true),
            _ => (false, false),
        };
    }

    private void OnEntrySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_vault is null || EntryList.SelectedItem is not EntryRow row) return;
        _groupIds = row.Ids.Count > 0 ? row.Ids : new[] { row.Id };
        try { ShowDetail(_vault.Get(row.Id), row.Id); }
        catch { /* entry vanished */ }
    }

    // Arrows to page through the logins of one site (shown when the open tile groups several accounts).
    private Control? GroupSwitcher(string id)
    {
        var ids = _groupIds;
        if (ids.Count <= 1) return null;
        int idx = -1;
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) { idx = i; break; }
        if (idx < 0) return null;

        var prev = new Button { Content = "‹", Padding = new Thickness(11, 2), CornerRadius = new CornerRadius(7) };
        prev.Click += (_, _) => SwitchGroup(id, -1);
        var next = new Button { Content = "›", Padding = new Thickness(11, 2), CornerRadius = new CornerRadius(7) };
        next.Click += (_, _) => SwitchGroup(id, +1);
        var lbl = new TextBlock
        {
            Text = Tr("Аккаунт на этом сайте") + $"  {idx + 1}/{ids.Count}",
            Foreground = Text2, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(prev);
        bar.Children.Add(lbl);
        bar.Children.Add(next);
        return new Border
        {
            Child = bar, Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 5), Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private void SwitchGroup(string curId, int delta)
    {
        var ids = _groupIds;
        if (ids.Count <= 1) return;
        int idx = -1;
        for (int i = 0; i < ids.Count; i++) if (ids[i] == curId) { idx = i; break; }
        if (idx < 0) return;
        int ni = (idx + delta + ids.Count) % ids.Count;
        try { ShowDetail(_vault!.Get(ids[ni]), ids[ni]); } catch { /* ignore */ }
    }

    // ================= detail =================

    private void ClearLiveTotps() => _liveTotps.Clear();

    private void ShowDetail(VaultItem item, string id)
    {
        _currentId = id;
        ClearLiveTotps();
        DetailPanel.Children.Clear();

        var wrap = new StackPanel { Spacing = 0, MaxWidth = 592, HorizontalAlignment = HorizontalAlignment.Stretch };   // stretch to a stable width so the card doesn't resize per login
        var sw = GroupSwitcher(id);
        if (sw is not null) wrap.Children.Add(sw);
        wrap.Children.Add(DetailHeader(item, id));

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        meta.Children.Add(SourceBadge());
        if (item.Type == "account")   // show a passkey chip next to the source badge if this site has one
        {
            var pk = MatchingPasskey(item);
            if (pk is not null) meta.Children.Add(PasskeyChip(pk.Value.item));
        }
        wrap.Children.Add(meta);

        switch (item.Type)
        {
            case "card":    BuildCardDetail(wrap, item); break;
            case "doc":     BuildDocDetail(wrap, item); break;
            case "note":    BuildNoteDetail(wrap, item); break;
            case "passkey": BuildPasskeyDetail(wrap, item); break;
            default:        BuildAccountDetail(wrap, item, id); break;
        }

        if (item.Type != "note" && !string.IsNullOrWhiteSpace(item.Notes))
            wrap.Children.Add(NoteSection(item.Notes, mono: false, title: "Заметки"));

        wrap.Children.Add(Foot(id));
        var shorten = ShortenDomainButton(item, id);
        if (shorten is not null) wrap.Children.Add(shorten);
        DetailPanel.Children.Add(wrap);
        Relocalize();
    }

    private Control DetailHeader(VaultItem item, string id)
    {
        string display = HeaderName(item);
        var tile = MonoTile(display, 52, 13, 21);
        tile.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tile, 0);

        var tt = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 10, 0) };
        tt.Children.Add(new TextBlock { Text = display, FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis });
        string url = item.Fields.GetValueOrDefault("url", "");
        if (!string.IsNullOrWhiteSpace(url))
        {
            var us = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            us.Children.Add((Control)MakeIcon(IconData("globe"), 12, Text3, 1.5));
            us.Children.Add(new TextBlock { Text = url, Foreground = Text2, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            tt.Children.Add(us);
        }
        Grid.SetColumn(tt, 1);

        var star = IconButton("star", item.Favorite ? Accent : Text2, item.Favorite ? "Убрать из избранного" : "В избранное", 16);
        if (item.Favorite) star.Content = MakeIcon(IconData("star"), 16, Accent, 1.5, fill: Accent);   // filled star when favorited
        star.Click += (_, _) =>
        {
            item.Favorite = !item.Favorite;
            _vault!.Update(id, item);
            Save();
            LoadEntries(selectFirst: false);
            var again = (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Id == id);
            if (again is not null) EntryList.SelectedItem = again;
            ShowDetail(_vault.Get(id), id);
        };

        var edit = IconButton("edit", Text2, "Изменить", 16);
        edit.Click += (_, _) => OpenEditor(item, id);

        var trash = IconButton("trash", Text2, "Удалить", 16);
        bool confirm = false;
        trash.Click += (_, _) =>
        {
            if (!confirm)
            {
                confirm = true;
                trash.Content = MakeIcon(IconData("trash"), 16, Bad, 1.7);
                ToolTip.SetTip(trash, "Нажмите ещё раз, чтобы удалить");
                return;
            }
            // if this is one login inside a multi-account site group, show the NEXT login after deleting
            var group = _groupIds;
            int posInGroup = -1;
            for (int i = 0; i < group.Count; i++) if (group[i] == id) { posInGroup = i; break; }
            string? nextId = group.Count > 1 && posInGroup >= 0
                ? group[posInGroup < group.Count - 1 ? posInGroup + 1 : posInGroup - 1]
                : null;

            int at = EntryList.SelectedIndex;   // remember position so a plain delete doesn't jump to the top
            _vault!.Delete(id);
            Save();
            _currentId = null;
            LoadEntries(selectFirst: false);
            RenderSidebar();

            if (nextId is not null &&
                (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Ids.Contains(nextId)) is { } grp)
            {
                _groupIds = grp.Ids;
                EntryList.SelectedItem = grp;                                       // keep the same site tile selected
                try { ShowDetail(_vault!.Get(nextId), nextId); } catch { /* ignore */ }   // ...and show the next login in it
            }
            else if (EntryList.ItemsSource is IReadOnlyList<EntryRow> left && left.Count > 0)
            {
                EntryList.SelectedIndex = Math.Clamp(at, 0, left.Count - 1);        // singleton: nearest neighbour tile
            }
        };

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        acts.Children.Add(star);
        acts.Children.Add(edit);
        acts.Children.Add(trash);
        Grid.SetColumn(acts, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(tile);
        grid.Children.Add(tt);
        grid.Children.Add(acts);
        return grid;
    }

    // ---------- account ⇄ 2FA / passkey linking (by site brand or login) ----------

    /// <summary>Normalize a URL or issuer name to a comparable brand token, e.g. "https://google.com/" and
    /// "Google" both → "google".</summary>
    private static string BrandToken(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim().ToLowerInvariant();
        int p = s.IndexOf("://"); if (p >= 0) s = s[(p + 3)..];
        if (s.StartsWith("www.")) s = s[4..];
        int slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash];
        if (s.Contains('.'))
        {
            string rd = Dedup.RegistrableDomain("https://" + s);
            if (!string.IsNullOrEmpty(rd)) s = rd;
            var parts = s.Split('.');
            return parts.Length >= 2 ? parts[^2] : s;
        }
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>A standalone Authenticator code (type "totp") that unambiguously belongs to this account —
    /// matched by site brand or by the same login. Returns the secret, or null if none / ambiguous.</summary>
    /// <summary>Accounts sharing the same registrable domain (used to gate login-less matches).</summary>
    private int SiteAccountCount(string site)
    {
        if (_vault is null || string.IsNullOrEmpty(site)) return 0;
        return _vault.Items().Count(x => x.Item.Type == "account"
            && Dedup.RegistrableDomain(x.Item.Fields.GetValueOrDefault("url", "")) == site);
    }

    private string? FindLinkedTotp(VaultItem account)
    {
        if (_vault is null) return null;
        string brand = BrandToken(account.Fields.GetValueOrDefault("url", ""));
        if (brand.Length == 0) return null;                // no site brand → can't tell which code belongs here
        string site = Dedup.RegistrableDomain(account.Fields.GetValueOrDefault("url", ""));
        string user = account.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
        string? strong = null; int strongCount = 0;        // same site AND same login = belongs to this account
        string? weak = null; int weakCount = 0;            // same site, code carries no login
        foreach (var e in _vault.Items())
        {
            if (e.Item.Type != "totp") continue;
            string sec = e.Item.Fields.GetValueOrDefault("totp", "");
            if (string.IsNullOrWhiteSpace(sec)) continue;
            string issuer = "", acct = "";
            try { var cfg = Totp.Parse(sec); issuer = cfg.Issuer ?? ""; acct = cfg.Account ?? ""; } catch { /* bare secret */ }
            string tbrand = BrandToken(issuer);
            if (tbrand.Length == 0) tbrand = BrandToken(e.Item.Title);
            if (tbrand.Length == 0 || tbrand != brand) continue;   // the code must be for THIS site — a shared login (same email) is not enough
            string tuser = e.Item.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
            if (tuser.Length == 0) tuser = acct.Trim().ToLowerInvariant();
            if (user.Length > 0 && tuser.Length > 0 && tuser == user) { strong = sec; strongCount++; }
            else if (tuser.Length == 0) { weak = sec; weakCount++; }
            // same site but a different login → another account here → skip
        }
        if (strongCount == 1) return strong;                                        // same-site exact login match wins
        if (strongCount == 0 && weakCount == 1 && SiteAccountCount(site) <= 1) return weak;  // login-less code: only if the site has one account
        return null;
    }

    /// <summary>The passkey that belongs to THIS account — matched by login on the same site.
    /// A login-less passkey is attached only when the site has a single account (otherwise ambiguous).</summary>
    private (string id, VaultItem item)? MatchingPasskey(VaultItem account)
    {
        if (_vault is null) return null;
        string site = Dedup.RegistrableDomain(account.Fields.GetValueOrDefault("url", ""));
        if (string.IsNullOrEmpty(site)) return null;
        string user = account.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
        (string id, VaultItem item)? strong = null; int strongCount = 0;
        (string id, VaultItem item)? weak = null; int weakCount = 0;
        foreach (var e in _vault.Items())
        {
            if (e.Item.Type != "passkey") continue;
            string rp = e.Item.Fields.GetValueOrDefault("rpId", "");
            if (string.IsNullOrEmpty(rp)) rp = e.Item.Fields.GetValueOrDefault("url", "");
            if (string.IsNullOrEmpty(rp)) continue;
            bool siteMatch = string.Equals(rp, site, StringComparison.OrdinalIgnoreCase)
                || Dedup.RegistrableDomain(rp.Contains("://") ? rp : "https://" + rp) == site;
            if (!siteMatch) continue;
            string puser = e.Item.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
            if (user.Length > 0 && puser.Length > 0 && puser == user) { strong = (e.Id, e.Item); strongCount++; }
            else if (puser.Length == 0) { weak = (e.Id, e.Item); weakCount++; }
            // puser != user (both present) → a different account's passkey → skip
        }
        if (strongCount == 1) return strong;
        if (strongCount == 0 && weakCount == 1 && SiteAccountCount(site) <= 1) return weak;
        return null;
    }

    /// <summary>Ids of passkeys that already belong to an account (surfaced as a chip on that account's card).
    /// These are hidden from the lists and have no standalone card — the account represents them.</summary>
    private HashSet<string> AttachedPasskeyIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_vault is null) return set;
        foreach (var e in _vault.Items())
        {
            if (e.Item.Type != "account") continue;
            var pk = MatchingPasskey(e.Item);
            if (pk is not null) set.Add(pk.Value.id);
        }
        return set;
    }

    /// <summary>Small brass "Ключ доступа" badge shown on an account that owns a passkey. It's an indicator,
    /// not a link — the passkey lives inside this card, so there's no separate record to open.</summary>
    private Control PasskeyChip(VaultItem passkey)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add((Control)MakeIcon(IconData("passkey"), 12, Accent, 1.6));
        sp.Children.Add(new TextBlock { Text = "Ключ доступа", Foreground = Accent, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            Child = sp, Background = AccentWash, BorderBrush = HairStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Left,
        };
        string created = passkey.Fields.GetValueOrDefault("created", "");
        string tip = Tr("Вход по ключу доступа (passkey) настроен для этого аккаунта.");
        if (DateTimeOffset.TryParse(created, out var dt)) tip += " " + Tr("Создан ") + dt.LocalDateTime.ToString("d MMM yyyy");
        ToolTip.SetTip(chip, tip);
        return chip;
    }

    private void BuildAccountDetail(StackPanel wrap, VaultItem item, string id)
    {
        string user = item.Fields.GetValueOrDefault("username", "");
        string pass = item.Fields.GetValueOrDefault("password", "");
        string url  = item.Fields.GetValueOrDefault("url", "");
        string totp = item.Fields.GetValueOrDefault("totp", "");
        if (string.IsNullOrWhiteSpace(totp)) totp = FindLinkedTotp(item) ?? "";   // auto-show a standalone 2FA code that matches this account

        var st = Auditor.Rate(pass);
        if (!string.IsNullOrEmpty(pass))
        {
            if (Reused(pass))
                wrap.Children.Add(Margined(WarnNote("Этот пароль используется и на других сайтах. Стоит сделать его уникальным.", false), 16));
            else if (st == Strength.Weak)
                wrap.Children.Add(Margined(WarnNote("Ненадёжный пароль: слишком короткий или предсказуемый.", true), 16));
        }

        var rows = new List<Control>();

        var uv = new TextBlock { Text = user, Foreground = Text, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        rows.Add(FRow("Имя пользователя", uv, Actions(CopyButton(user))));

        string Masked() => new string('•', Math.Clamp(pass.Length, 6, 14));
        var pv = new TextBlock { Text = string.IsNullOrEmpty(pass) ? "" : Masked(), FontFamily = MonoFont, Foreground = Text, FontSize = 13.5 };
        rows.Add(FRow("Пароль", pv, Actions(EyeButton(pv, pass, Masked), CopyButton(pass))));
        if (!string.IsNullOrEmpty(pass))
            rows.Add(FRow("", StrengthLine(st)));

        if (!string.IsNullOrWhiteSpace(totp))
            rows.Add(TotpRow(totp));

        if (!string.IsNullOrWhiteSpace(url))
        {
            var link = new TextBlock
            {
                Text = url, Foreground = Accent, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            ToolTip.SetTip(link, Tr("Открыть сайт"));
            link.PointerPressed += async (_, _) =>
            {
                try
                {
                    string u = url.Contains("://") ? url : "https://" + url;
                    var top = TopLevel.GetTopLevel(this);
                    if (top?.Launcher is { } l) await l.LaunchUriAsync(new Uri(u));
                }
                catch { /* ignore */ }
            };
            rows.Add(FRow("Веб-сайт", link));
        }

        wrap.Children.Add(Margined(FGroup(rows), 22));
    }

    private void BuildCardDetail(StackPanel wrap, VaultItem item)
    {
        string number = item.Fields.GetValueOrDefault("number", "");
        string cvc    = item.Fields.GetValueOrDefault("cvc", "");
        string expiry = item.Fields.GetValueOrDefault("expiry", "");
        string holder = item.Fields.GetValueOrDefault("holder", "");
        string digits = new string(number.Where(char.IsDigit).ToArray());

        wrap.Children.Add(Margined(BankCard(item.Title, holder, expiry, digits), 22));

        var rows = new List<Control>();

        string full = GroupCard(digits), mask = MaskCard(digits);
        var nv = new TextBlock { Text = string.IsNullOrEmpty(digits) ? "" : mask, FontFamily = MonoFont, Foreground = Text, FontSize = 13.5 };
        rows.Add(FRow("Номер карты", nv, Actions(EyeButton(nv, full, () => mask), CopyButton(digits))));

        if (!string.IsNullOrEmpty(cvc))
        {
            var cv = new TextBlock { Text = "•••", FontFamily = MonoFont, Foreground = Text, FontSize = 13.5 };
            rows.Add(FRow("CVC / CVV", cv, Actions(EyeButton(cv, cvc, () => "•••"), CopyButton(cvc))));
        }
        if (!string.IsNullOrEmpty(expiry))
        {
            string ex = FmtExpiry(expiry);
            rows.Add(FRow("Срок действия", new TextBlock { Text = ex, FontFamily = MonoFont, Foreground = Text, FontSize = 13.5 }, Actions(CopyButton(ex))));
        }
        if (!string.IsNullOrEmpty(holder))
            rows.Add(FRow("Владелец", new TextBlock { Text = holder, Foreground = Text, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis }, Actions(CopyButton(holder))));

        wrap.Children.Add(Margined(FGroup(rows), 18));
    }

    private void BuildDocDetail(StackPanel wrap, VaultItem item)
    {
        string number = item.Fields.GetValueOrDefault("number", "");
        string issued = item.Fields.GetValueOrDefault("issued", "");

        var rows = new List<Control>();

        string Masked() => new string('•', Math.Clamp(number.Length, 6, 16));
        var nv = new TextBlock { Text = string.IsNullOrEmpty(number) ? "" : Masked(), FontFamily = MonoFont, Foreground = Text, FontSize = 13.5 };
        rows.Add(FRow("Серия и номер", nv, Actions(EyeButton(nv, number, Masked), CopyButton(number))));

        if (!string.IsNullOrEmpty(issued))
            rows.Add(FRow("Кем выдан", new TextBlock { Text = issued, Foreground = Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap }, Actions(CopyButton(issued))));

        wrap.Children.Add(Margined(FGroup(rows), 22));
    }

    private void BuildNoteDetail(StackPanel wrap, VaultItem item)
    {
        wrap.Children.Add(NoteSection(string.IsNullOrEmpty(item.Notes) ? "—" : item.Notes, mono: true, title: "Заметка"));
    }

    private void BuildPasskeyDetail(StackPanel wrap, VaultItem item)
    {
        string user = item.Fields.GetValueOrDefault("username", "");
        string url = item.Fields.GetValueOrDefault("url", "");

        var hero = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        hero.Children.Add((Control)MakeIcon(IconData("passkey"), 18, Accent, 1.6));
        hero.Children.Add(new TextBlock { Text = "Вход по ключу (passkey) — пароль вводить не нужно.", Foreground = Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        wrap.Children.Add(Margined(new Border
        {
            Child = hero, Background = AccentWash, BorderBrush = HairStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 11),
        }, 16));

        var rows = new List<Control>();   // a passkey card shows only the account and the site — nothing to copy/reveal like a password
        if (!string.IsNullOrEmpty(user))
            rows.Add(FRow("Имя пользователя", new TextBlock { Text = user, Foreground = Text, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis }, Actions(CopyButton(user))));
        if (!string.IsNullOrWhiteSpace(url))
            rows.Add(FRow("Веб-сайт", new TextBlock { Text = url, Foreground = Accent, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis }, Actions(CopyButton(url))));
        if (rows.Count == 0)
            rows.Add(FRow("Имя пользователя", new TextBlock { Text = "—", Foreground = Text3, FontSize = 13.5 }));

        wrap.Children.Add(Margined(FGroup(rows), 16));
    }

    // ---------- detail building blocks ----------

    private Control SourceBadge()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add((Control)MakeIcon(IconData("lock"), 11, Accent, 1.6));
        sp.Children.Add(new TextBlock { Text = "Сейф IPasswrd", Foreground = Text2, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = sp, Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static Control Margined(Control c, double top) { c.Margin = new Thickness(0, top, 0, 0); return c; }

    private static Control Actions(params Control[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    private Control Hairline() => new Border { Height = 1, Background = Hair };

    private Control FGroup(IEnumerable<Control> rows)
    {
        var sp = new StackPanel();
        bool first = true;
        foreach (var r in rows)
        {
            if (!first) sp.Children.Add(Hairline());
            sp.Children.Add(r);
            first = false;
        }
        return new Border
        {
            Child = sp, Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), ClipToBounds = true,
        };
    }

    private Control FRow(string label, Control value, Control? actions = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("132,*,Auto"), VerticalAlignment = VerticalAlignment.Center };

        var lbl = new TextBlock { Text = label, Foreground = Text2, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        if (actions is not null)
        {
            actions.VerticalAlignment = VerticalAlignment.Center;
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
        }

        return new Border { Child = grid, Padding = new Thickness(15, 11), MinHeight = 52 };
    }

    private Control StrengthLine(Strength s)
    {
        var (frac, brush, label) = s switch
        {
            Strength.Weak => (0.30, Bad, "ненадёжный"),
            Strength.Fair => (0.60, Warn, "средний"),
            _             => (1.00, Ok, "надёжный"),
        };
        var fill = new Border { Width = 88 * frac, Height = 4, CornerRadius = new CornerRadius(99), Background = brush, HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Border { Width = 88, Height = 4, CornerRadius = new CornerRadius(99), Background = Hair, ClipToBounds = true, Child = fill, VerticalAlignment = VerticalAlignment.Center };
        var lbl = new TextBlock { Text = label, Foreground = brush, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(track);
        sp.Children.Add(lbl);
        return sp;
    }

    private Control TotpRow(string totp)
    {
        string code = "——————"; int secs = 30;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            code = Totp.GenerateFrom(totp, now);
            secs = Totp.Parse(totp).SecondsRemaining();
        }
        catch { /* bad secret */ }

        var ringTx = new TextBlock { Text = secs.ToString(), FontSize = 9, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderBrush = Accent, BorderThickness = new Thickness(2), Child = ringTx, VerticalAlignment = VerticalAlignment.Center };
        var codeTx = new TextBlock { Text = Group3(code), FontFamily = MonoFont, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Text, VerticalAlignment = VerticalAlignment.Center };
        _liveTotps.Add((totp, codeTx, ringTx));

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(ring);
        sp.Children.Add(codeTx);

        return FRow("Код проверки", sp, Actions(CopyButton(() =>
        {
            try { return Totp.GenerateFrom(totp, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
            catch { return ""; }
        })));
    }

    private void OnDetailTick(object? sender, EventArgs e)
    {
        // auto-lock after inactivity
        if (_autolockMinutes > 0 && _vault is not null && (VaultScreen.IsVisible || EditorScreen.IsVisible)
            && (DateTimeOffset.UtcNow - _lastActivity).TotalMinutes >= _autolockMinutes)
        {
            _detailTimer?.Stop();
            _vault = null;
            WipePendingClipboard();
            WipeQuickUnlock();
            SetupUnlock();
            return;
        }

        // pick up changes synced in from another device: merge record-by-record, persist the union
        if (_vault is not null && (VaultScreen.IsVisible || EditorScreen.IsVisible))
        {
            try
            {
                string vp = VaultPath();
                if (System.IO.File.Exists(vp))
                {
                    DateTime m = System.IO.File.GetLastWriteTimeUtc(vp);
                    if (_vaultStamp != default && m != _vaultStamp)
                    {
                        byte[] bytes = System.IO.File.ReadAllBytes(vp);
                        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                        if (hash == _vaultHash)
                        {
                            _vaultStamp = m;               // touched but same content — nothing to do
                        }
                        else
                        {
                            int changed = _vault.MergeFrom(bytes);
                            _vaultStamp = m;
                            Save();                        // canonical union back to the file
                            if (changed > 0 && VaultScreen.IsVisible)
                            {
                                string? keep = _currentId;
                                LoadEntries(selectFirst: false);
                                RenderSidebar();
                                var row = keep is null ? null
                                    : (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Id == keep);
                                if (row is not null) EntryList.SelectedItem = row;
                            }
                        }
                    }
                }
            }
            catch (System.IO.IOException) { /* file busy while the sync client uploads — retry next tick */ }
            catch (VaultIntegrityException) { /* a foreign vault landed at our path — keep local, do not merge */ }
            catch { /* best effort */ }
        }

        // Google Drive sync (API, not a folder): pull + merge on a slower cadence
        GooglePullMaybe();

        // slide the quick-unlock expiry while the session is alive (once a minute)
        if (_vault is not null && (DateTimeOffset.UtcNow - _quickRefreshedAt).TotalSeconds >= 60)
            SaveQuickUnlock();

        if (_liveTotps.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (secret, code, ring) in _liveTotps)
        {
            try
            {
                code.Text = Group3(Totp.GenerateFrom(secret, now));
                ring.Text = Totp.Parse(secret).SecondsRemaining().ToString();
            }
            catch { /* ignore */ }
        }
    }

    private Control BankCard(string title, string holder, string expiry, string digits)
    {
        var cardBg = Brush.Parse("#161A22");
        var cardFg = Brush.Parse("#F3F4F2");
        string paysys = PaySys(digits);

        var bank = new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? "Банковская карта" : title, FontWeight = FontWeight.Bold, FontSize = 13.5, Foreground = cardFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bank, 0);
        var pay = new TextBlock { Text = paysys, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = cardFg, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pay, 1);
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        topRow.Children.Add(bank);
        topRow.Children.Add(pay);
        Grid.SetRow(topRow, 0);

        var chip = new Border { Width = 40, Height = 29, CornerRadius = new CornerRadius(6), Background = Accent, Opacity = 0.92, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(chip, 1);

        var num = new TextBlock
        {
            Text = string.IsNullOrEmpty(digits) ? "•••• •••• •••• ••••" : MaskCard(digits),
            FontFamily = MonoFont, FontSize = 18.5, Foreground = cardFg, Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(num, 3);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26 };
        meta.Children.Add(CardMeta("ВЛАДЕЛЕЦ", string.IsNullOrWhiteSpace(holder) ? "—" : holder.ToUpperInvariant(), cardFg));
        meta.Children.Add(CardMeta("СРОК", string.IsNullOrEmpty(expiry) ? "—" : FmtExpiry(expiry), cardFg));
        Grid.SetRow(meta, 4);

        var inner = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(20, 18) };
        inner.Children.Add(topRow);
        inner.Children.Add(chip);
        inner.Children.Add(num);
        inner.Children.Add(meta);

        var innerBorder = new Border { Child = inner, CornerRadius = new CornerRadius(14), BorderBrush = Brush.Parse("#24FFFFFF"), BorderThickness = new Thickness(1), Margin = new Thickness(4) };
        return new Border { Child = innerBorder, Width = 384, Height = 242, CornerRadius = new CornerRadius(18), Background = cardBg, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private static Control CardMeta(string label, string value, IBrush fg)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = fg, Opacity = 0.6 });
        sp.Children.Add(new TextBlock { Text = value, FontFamily = MonoFont, FontSize = 13, Foreground = fg });
        return sp;
    }

    private Control NoteSection(string text, bool mono, string title)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        sp.Children.Add(new TextBlock { Text = Tr(title).ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Text3, Margin = new Thickness(2, 0, 0, 8) });
        sp.Children.Add(new Border
        {
            Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(15, 13),
            Child = new TextBlock { Text = text, Foreground = mono ? Text : Text2, FontSize = mono ? 12.5 : 13, FontFamily = mono ? MonoFont : FontFamily.Default, TextWrapping = TextWrapping.Wrap },
        });
        return sp;
    }

    private Control Foot(string id)
    {
        string upd = "";
        try { upd = _vault!.Items().First(x => x.Id == id).UpdatedAt; } catch { /* ignore */ }
        string disp = FormatDate(upd);
        var sp = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        if (disp.Length > 0)
            sp.Children.Add(new TextBlock { Text = Tr("Изменено") + ": " + disp, Foreground = Text3, FontSize = 12 });
        return sp;
    }

    // On an account/passkey whose site has a sub-domain (accounts.google.com), offer to trim it to
    // the registrable 2nd-level domain (google.com) — or "Оставить" to keep it and hide the hint for good.
    private Control? ShortenDomainButton(VaultItem item, string id)
    {
        if (item.Type is not ("account" or "passkey")) return null;
        string url = item.Fields.GetValueOrDefault("url", "");
        if (string.IsNullOrWhiteSpace(url)) return null;
        string reg = Dedup.RegistrableDomain(url);
        if (reg.Length == 0) return null;
        int regLabels = reg.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
        if (Dedup.HostDepth(url) <= regLabels) return null;   // already at the 2nd level — nothing to trim
        if (_keepAsIs.Contains(NormUrl(url))) return null;    // user chose to keep this exact address

        var shorten = new Button { Content = Tr("Сократить до") + " " + reg, Padding = new Thickness(12, 6) };
        shorten.Click += (_, _) =>
        {
            int at = url.IndexOf("://");
            string scheme = at >= 0 ? url[..(at + 3)] : "";
            string newUrl = scheme + reg;
            item.Fields["url"] = newUrl;
            item.Title = DeriveTitle(newUrl, item.Fields.GetValueOrDefault("username", ""));   // the record's own site title follows the new 2nd-level domain
            _vault!.Update(id, item);
            Save();
            LoadEntries(selectFirst: false);   // regroup: the record moves to the 2nd-level-domain tile, name updates to the new site
            var again = (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Ids.Contains(id) || r.Id == id);
            if (again is not null) EntryList.SelectedItem = again;
            else { try { ShowDetail(_vault.Get(id), id); } catch { /* ignore */ } }
        };

        var keep = new Button { Content = Tr("Оставить"), Padding = new Thickness(12, 6) };
        keep.Click += (_, _) =>
        {
            _keepAsIs.Add(NormUrl(url));
            SaveSettings();
            ShowDetail(_vault!.Get(id), id);   // the hint disappears for this address, for good
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(shorten);
        row.Children.Add(keep);
        return row;
    }

    private Control WarnNote(string text, bool bad)
    {
        var icon = (Control)MakeIcon(IconData("alert"), 16, bad ? Bad : Warn, 1.6);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 0, 0);
        Grid.SetColumn(icon, 0);
        var txt = new TextBlock { Text = text, Foreground = Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(txt, 1);
        var sp = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };   // "*" column lets the text wrap instead of overflowing
        sp.Children.Add(icon);
        sp.Children.Add(txt);
        return new Border
        {
            Child = sp,
            Background = bad ? BadWash : WarnWash,
            BorderBrush = bad ? Brush.Parse("#33E95048") : Brush.Parse("#33EA8E49"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 11),
        };
    }

    private Control EmptyState(string title, string sub)
    {
        var sp = new StackPanel { Spacing = 8, Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 360 };
        sp.Children.Add(new TextBlock { Text = title, Foreground = Text2, FontWeight = FontWeight.Bold, FontSize = 14 });
        sp.Children.Add(new TextBlock { Text = sub, Foreground = Text3, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    // ---------- copy / eye / icon buttons ----------

    private Button IconButton(string icon, IBrush? stroke = null, string? tip = null, double size = 15)
    {
        var b = new Button { Padding = new Thickness(6), Content = MakeIcon(IconData(icon), size, stroke ?? Text2, 1.5) };
        b.Classes.Add("icon");
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }

    private Button CopyButton(string value) => CopyButton(() => value);

    private Button CopyButton(Func<string> get)
    {
        var b = IconButton("copy", Text2, "Копировать");
        b.Click += async (_, _) =>
        {
            string val = get() ?? "";
            try { if (Clipboard is { } cb) await cb.SetTextAsync(val); } catch { /* ignore */ }
            ScheduleClipboardClear(val);
            b.Content = MakeIcon(IconData("check"), 15, Ok, 1.7);
            try { await Task.Delay(1100); } catch { /* ignore */ }
            b.Content = MakeIcon(IconData("copy"), 15, Text2, 1.5);
        };
        return b;
    }

    private Button EyeButton(TextBlock target, string real, Func<string> masked)
    {
        bool shown = false;
        var b = IconButton("eye", Text2, "Показать");
        b.Click += (_, _) =>
        {
            shown = !shown;
            target.Text = shown ? real : masked();
            b.Content = MakeIcon(IconData(shown ? "eyeoff" : "eye"), 15, Text2, 1.5);
            ToolTip.SetTip(b, shown ? "Скрыть" : "Показать");
        };
        return b;
    }

    // ================= tools (authenticator / generator / security / settings) =================

    private void ShowTool(string tool)
    {
        ClearLiveTotps();
        if (tool != "authenticator") { _authAdding = false; _authEditId = null; }   // don't carry the form into another tool
        ToolHost.Children.Clear();
        switch (tool)
        {
            case "generator":     BuildGenerator(); break;
            case "security":      BuildSecurity(); break;
            case "authenticator": BuildAuthenticator(); break;
            case "settings":      BuildSettings(); break;
        }
        Relocalize();
    }

    private void ToolHeader(string title, string sub)
    {
        ToolHost.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Text });
        if (!string.IsNullOrEmpty(sub))
            ToolHost.Children.Add(new TextBlock { Text = sub, Foreground = Text2, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left });
    }

    // ----- generator -----

    private void BuildGenerator()
    {
        ToolHeader("Генератор паролей", "Создавайте надёжные пароли и сохраняйте их прямо в сейф.");

        _genOut = new TextBlock { FontFamily = MonoFont, FontSize = 20, Foreground = Text, TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
        _genEntropy = new TextBlock { Foreground = Text3, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        var copy = new Button { Content = "Копировать", Padding = new Thickness(16, 9) };
        copy.Classes.Add("primary");
        copy.Click += async (_, _) =>
        {
            string val = _genOut!.Text ?? "";
            try { if (Clipboard is { } cb) await cb.SetTextAsync(val); } catch { /* ignore */ }
            ScheduleClipboardClear(val);
            copy.Content = "Скопировано";
            try { await Task.Delay(1100); } catch { /* ignore */ }
            copy.Content = "Копировать";
        };
        var refresh = new Button { Content = "Обновить", Padding = new Thickness(16, 9) };
        refresh.Click += (_, _) => RegenPassword();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        actions.Children.Add(copy);
        actions.Children.Add(refresh);
        Grid.SetColumn(actions, 0);
        Grid.SetColumn(_genEntropy, 1);
        _genEntropy.HorizontalAlignment = HorizontalAlignment.Right;

        var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        metaRow.Children.Add(actions);
        metaRow.Children.Add(_genEntropy);

        var outStack = new StackPanel { Spacing = 16 };
        outStack.Children.Add(_genOut);
        outStack.Children.Add(metaRow);
        ToolHost.Children.Add(new Border { Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(0, 20, 0, 0), Child = outStack });

        var controls = new StackPanel();
        controls.Children.Add(GenSliderRow());
        controls.Children.Add(Hairline());
        controls.Children.Add(GenToggleRow("Заглавные буквы", "A–Z", _genUpper, v => { _genUpper = v; RegenPassword(); }));
        controls.Children.Add(Hairline());
        controls.Children.Add(GenToggleRow("Строчные буквы", "a–z", _genLower, v => { _genLower = v; RegenPassword(); }));
        controls.Children.Add(Hairline());
        controls.Children.Add(GenToggleRow("Цифры", "0–9", _genDigits, v => { _genDigits = v; RegenPassword(); }));
        controls.Children.Add(Hairline());
        controls.Children.Add(GenToggleRow("Символы", "!@#$%…", _genSymbols, v => { _genSymbols = v; RegenPassword(); }));
        controls.Children.Add(Hairline());
        controls.Children.Add(GenToggleRow("Исключать похожие", "l, 1, O, 0", _genNoAmbig, v => { _genNoAmbig = v; RegenPassword(); }));
        ToolHost.Children.Add(new Border { Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 14, 0, 0), Child = controls, ClipToBounds = true });

        RegenPassword();
    }

    private Control GenSliderRow()
    {
        var slider = new Slider { Minimum = 8, Maximum = 48, Value = _genLen, Width = 200, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var val = new TextBlock { Text = _genLen.ToString(), FontFamily = MonoFont, Foreground = Accent, FontWeight = FontWeight.Bold, FontSize = 14, Width = 30, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, _) => { _genLen = (int)slider.Value; val.Text = _genLen.ToString(); RegenPassword(); };

        var left = new TextBlock { Text = "Длина", Foreground = Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(val, 2);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(17, 11) };
        grid.Children.Add(left);
        grid.Children.Add(slider);
        grid.Children.Add(val);
        return grid;
    }

    private Control GenToggleRow(string label, string sub, bool value, Action<bool> onChange)
    {
        var ts = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        ts.Checked += (_, _) => onChange(true);
        ts.Unchecked += (_, _) => onChange(false);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = label, Foreground = Text, FontSize = 13 });
        left.Children.Add(new TextBlock { Text = sub, Foreground = Text3, FontSize = 11.5 });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(ts, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(17, 8) };
        grid.Children.Add(left);
        grid.Children.Add(ts);
        return grid;
    }

    private void RegenPassword()
    {
        if (_genOut is null) return;
        var opts = new GeneratorOptions(_genLen, _genLower, _genUpper, _genDigits, _genSymbols, _genNoAmbig);
        string pool = Generator.Pool(opts);
        if (pool.Length == 0)
        {
            _genOut.Text = "—";
            if (_genEntropy is not null) _genEntropy.Text = "выберите хотя бы один набор символов";
            return;
        }
        _genOut.Text = Generator.Generate(opts);
        double bits = _genLen * Math.Log2(pool.Length);
        if (_genEntropy is not null) _genEntropy.Text = $"~{(int)Math.Round(bits)} {Tr("бит энтропии")}";
    }

    // ----- security -----

    private void BuildSecurity()
    {
        ToolHeader("Проверка безопасности", "Слабые и повторяющиеся пароли в вашем сейфе. Проверка идёт локально, без сети.");
        var report = Auditor.Audit(_vault!.Items());
        int total = report.AccountsChecked;
        int score = total == 0 ? 100 : (int)Math.Round(100.0 * report.Ok / total);
        IBrush scoreColor = score >= 80 ? Ok : score >= 50 ? Warn : Bad;

        var scoreTx = new TextBlock { Text = score.ToString(), FontSize = 28, FontWeight = FontWeight.Bold, Foreground = scoreColor, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var scoreBox = new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(42), BorderBrush = scoreColor, BorderThickness = new Thickness(4), Child = scoreTx, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        Grid.SetColumn(scoreBox, 0);

        var sum = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        sum.Children.Add(new TextBlock { Text = total == 0 ? "Пока нет аккаунтов" : score >= 80 ? "Хорошая защита" : "Есть над чем поработать", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Text });
        sum.Children.Add(new TextBlock { Text = $"{Tr("Проверено аккаунтов")}: {total}. {Tr("Надёжных")}: {report.Ok}.", Foreground = Text2, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        int reusedDistinct = report.Reused.Select(r => r.Id).Distinct().Count();
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        chips.Children.Add(SecChip($"{report.Weak.Count} {Tr("ненадёжных")}", Bad, BadWash));
        chips.Children.Add(SecChip($"{reusedDistinct} {Tr("повторяются")}", Warn, WarnWash));
        sum.Children.Add(chips);
        Grid.SetColumn(sum, 1);

        var headGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        headGrid.Children.Add(scoreBox);
        headGrid.Children.Add(sum);
        ToolHost.Children.Add(new Border { Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(0, 20, 0, 0), Child = headGrid });

        if (report.Weak.Count > 0)
            ToolHost.Children.Add(SecGroup("Ненадёжные пароли", report.Weak));
        if (report.Reused.Count > 0)
        {
            var distinct = report.Reused.GroupBy(r => r.Id).Select(g => g.First()).ToList();
            ToolHost.Children.Add(SecGroup("Повторяющиеся пароли", distinct));
        }
        if (report.Weak.Count == 0 && report.Reused.Count == 0 && total > 0)
            ToolHost.Children.Add(new TextBlock { Text = "Проблем не найдено — все пароли надёжные и уникальные.", Foreground = Ok, FontSize = 13, Margin = new Thickness(2, 20, 0, 0) });

        ToolHost.Children.Add(BuildBreachSection());   // online, opt-in HIBP check
    }

    private Control SecChip(string text, IBrush color, IBrush wash)
    {
        return new Border
        {
            Background = wash, CornerRadius = new CornerRadius(99), Padding = new Thickness(11, 4),
            Child = new TextBlock { Text = text, Foreground = color, FontSize = 12, FontWeight = FontWeight.SemiBold },
        };
    }

    private Control SecGroup(string title, IReadOnlyList<AuditFinding> findings)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        sp.Children.Add(new TextBlock { Text = Tr(title).ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Text3, Margin = new Thickness(2, 0, 0, 8) });
        foreach (var f in findings)
        {
            var tile = MonoTile(f.Title, 30, 8, 12);
            tile.Margin = new Thickness(0, 0, 11, 0);
            Grid.SetColumn(tile, 0);
            var name = new TextBlock { Text = f.Title, Foreground = Text, FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            bool weak = f.Reason == "weak";
            var pill = new Border
            {
                Background = weak ? BadWash : WarnWash, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = weak ? "слабый" : "повтор", Foreground = weak ? Bad : Warn, FontSize = 11, FontWeight = FontWeight.Bold },
            };
            Grid.SetColumn(pill, 2);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6, 8) };
            g.Children.Add(tile);
            g.Children.Add(name);
            g.Children.Add(pill);
            var btn = new Button { Content = g, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 2), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            string id = f.Id;
            btn.Click += (_, _) => OpenEntryFromTool(id);
            sp.Children.Add(btn);
        }
        return sp;
    }

    private void OpenEntryFromTool(string id)
    {
        _toolMode = null;
        _section = "all";
        ListTitle.Text = "Все записи";
        ToolPane.IsVisible = false;
        LoadEntries(selectFirst: false);
        RenderSidebar();
        var row = (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Id == id);
        if (row is not null) EntryList.SelectedItem = row;
        else { try { ShowDetail(_vault!.Get(id), id); } catch { /* ignore */ } }
    }

    // ----- authenticator -----

    private void BuildAuthenticator()
    {
        ToolHeader("Аутентификатор", "Коды двухфакторной проверки считаются локально и обновляются каждые 30 секунд.");

        if (_authAdding)
        {
            ToolHost.Children.Add(AuthAddForm());
        }
        else
        {
            var add = new Button { Content = "Добавить код", Padding = new Thickness(16, 9), Margin = new Thickness(0, 18, 0, 0) };
            add.Classes.Add("primary");
            add.Click += (_, _) => { _authEditId = null; _authAdding = true; ShowTool("authenticator"); };
            ToolHost.Children.Add(add);
        }

        var accts = _vault!.Items()
            .Where(x => x.Item.Fields.TryGetValue("totp", out var t) && !string.IsNullOrWhiteSpace(t))
            .OrderBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (accts.Count == 0)
        {
            if (!_authAdding)
                ToolHost.Children.Add(EmptyState("Пока нет кодов",
                    "Добавьте код кнопкой выше — вставьте секрет или ссылку otpauth://. Коды, импортированные из других менеджеров, тоже появятся здесь."));
            return;
        }

        var card = new StackPanel();
        bool first = true;
        foreach (var e in accts)
        {
            if (!first) card.Children.Add(Hairline());
            first = false;
            card.Children.Add(AuthRow(e));
        }
        ToolHost.Children.Add(new Border { Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 16, 0, 0), Child = card, ClipToBounds = true });
    }

    private Control AuthRow(VaultEntry e)
    {
        string title = e.Item.Title;
        string totp = e.Item.Fields["totp"];
        string user = e.Item.Fields.GetValueOrDefault("username", "");
        bool standalone = e.Item.Type == "totp";   // added here in the authenticator (vs. attached to an account)

        var tile = MonoTile(title, 34, 9, 14);
        tile.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(tile, 0);

        var tt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tt.Children.Add(new TextBlock { Text = title, Foreground = Text, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(user)) tt.Children.Add(new TextBlock { Text = user, Foreground = Text3, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(tt, 1);

        string code = "——————"; int secs = 30;
        try { long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); code = Totp.GenerateFrom(totp, now); secs = Totp.Parse(totp).SecondsRemaining(); }
        catch { /* bad secret */ }

        var codeTx = new TextBlock { Text = Group3(code), FontFamily = MonoFont, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center };
        var ringTx = new TextBlock { Text = secs.ToString(), FontSize = 9, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderBrush = Accent, BorderThickness = new Thickness(2), Child = ringTx, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
        _liveTotps.Add((totp, codeTx, ringTx));

        var copy = CopyButton(() => { try { return Totp.GenerateFrom(totp, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } catch { return ""; } });

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(codeTx);
        right.Children.Add(ring);
        right.Children.Add(copy);
        if (standalone)
        {
            var edit = IconButton("edit", Text2, "Изменить");
            edit.Click += (_, _) => { _authEditId = e.Id; _authAdding = true; ShowTool("authenticator"); };
            right.Children.Add(edit);
            var del = IconButton("trash", Bad, "Удалить код");
            del.Click += (_, _) => { try { _vault!.Delete(e.Id); Save(); } catch { /* best effort */ } RenderSidebar(); ShowTool("authenticator"); };
            right.Children.Add(del);
        }
        Grid.SetColumn(right, 2);

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(15, 12) };
        g.Children.Add(tile);
        g.Children.Add(tt);
        g.Children.Add(right);
        return g;
    }

    // Inline "add a verification code" form shown at the top of the authenticator.
    private Control AuthAddForm()
    {
        _authName = new TextBox { Watermark = "Название — например, GitHub" };
        _authAccount = new TextBox { Watermark = "Аккаунт — имя или почта (необязательно)" };
        _authSecret = new TextBox { Watermark = "Секрет или ссылка otpauth://", FontFamily = MonoFont };
        _authSecret.LostFocus += (_, _) => TryPrefillFromUri();   // paste a URI → fill name/account
        _authError = new TextBlock { Foreground = Bad, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        if (_authEditId is not null)   // editing an existing standalone code — prefill from its record
        {
            try
            {
                var it = _vault!.Get(_authEditId);
                _authName.Text = it.Title;
                _authAccount.Text = it.Fields.GetValueOrDefault("username", "");
                _authSecret.Text = it.Fields.GetValueOrDefault("totp", "");
            }
            catch { _authEditId = null; }   // record vanished — fall back to a fresh add form
        }

        var save = new Button { Content = "Сохранить код", Padding = new Thickness(16, 9) };
        save.Classes.Add("primary");
        save.Click += (_, _) => SaveAuthCode();
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 9) };
        cancel.Click += (_, _) => { _authAdding = false; _authEditId = null; ShowTool("authenticator"); };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(save);
        actions.Children.Add(cancel);

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock { Text = _authEditId is null ? "Новый код проверки" : "Изменение кода", Foreground = Text, FontWeight = FontWeight.SemiBold, FontSize = 14 });
        form.Children.Add(Labeled("Название", _authName));
        form.Children.Add(Labeled("Аккаунт", _authAccount));
        form.Children.Add(Labeled("Секрет", _authSecret));
        form.Children.Add(_authError);
        form.Children.Add(actions);

        return new Border { Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 18, 0, 0), Child = form };
    }

    private void TryPrefillFromUri()
    {
        string s = (_authSecret?.Text ?? "").Trim();
        if (!s.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var c = Totp.Parse(s);
            if (_authName is not null && string.IsNullOrWhiteSpace(_authName.Text) && !string.IsNullOrWhiteSpace(c.Issuer)) _authName.Text = c.Issuer;
            if (_authAccount is not null && string.IsNullOrWhiteSpace(_authAccount.Text) && !string.IsNullOrWhiteSpace(c.Account)) _authAccount.Text = c.Account;
        }
        catch { /* ignore */ }
    }

    private void SaveAuthCode()
    {
        if (_vault is null) return;
        string secret = (_authSecret?.Text ?? "").Trim();
        string name = (_authName?.Text ?? "").Trim();
        string account = (_authAccount?.Text ?? "").Trim();

        if (!Totp.IsValidSecret(secret))
        {
            if (_authError is not null)
            {
                _authError.Text = "Не удаётся прочитать секрет. Вставьте ключ (Base32) или ссылку otpauth://.";
                _authError.IsVisible = true;
            }
            return;
        }

        var cfg = Totp.Parse(secret);   // fall back to the URI's own issuer/account when a field is blank
        if (string.IsNullOrWhiteSpace(name))
            name = !string.IsNullOrWhiteSpace(cfg.Issuer) ? cfg.Issuer
                 : !string.IsNullOrWhiteSpace(cfg.Account) ? cfg.Account : "Код проверки";
        if (string.IsNullOrWhiteSpace(account)) account = cfg.Account;

        var item = new VaultItem { Type = "totp", Title = name };
        item.Fields["totp"] = secret;
        if (!string.IsNullOrWhiteSpace(account)) item.Fields["username"] = account;
        try
        {
            if (_authEditId is not null) _vault.Update(_authEditId, item);   // editing an existing code
            else _vault.Add(item);                                          // adding a new one
            Save();
        }
        catch { /* best effort */ }

        _authAdding = false;
        _authEditId = null;
        RenderSidebar();
        ShowTool("authenticator");
    }

    // ----- settings (mostly placeholders) -----

    private void BuildSettings()
    {
        ToolHeader("Настройки", "Приложение, защита и синхронизация.");
        var g = new StackPanel();

        g.Children.Add(SetRowControl("Автоблокировка", "Заблокировать сейф после простоя", AutolockControl()));
        g.Children.Add(Hairline());
        g.Children.Add(SetRowControl("Очистка буфера обмена", "Стирать скопированный пароль через заданное время", ClipboardClearControl()));
        g.Children.Add(Hairline());
        g.Children.Add(ChangePasswordRow());
        g.Children.Add(Hairline());
        g.Children.Add(InstallExtensionRow());
        g.Children.Add(Hairline());
        g.Children.Add(SetRowControl("Тема оформления", "Тёмное или светлое оформление", ThemeControl()));
        g.Children.Add(Hairline());
        g.Children.Add(SetRowControl("Язык", "Язык интерфейса", LanguageControl()));
        g.Children.Add(Hairline());
        g.Children.Add(SetRowControl("Запускать с Windows", "Открывать в трее при входе в систему", AutostartControl()));
        g.Children.Add(Hairline());
        g.Children.Add(SyncRow());
        g.Children.Add(Hairline());
        g.Children.Add(SetRowControl("Импорт из файла", "Kaspersky, Chrome, Edge, Яндекс и другие", ImportControl()));
        g.Children.Add(Hairline());
        g.Children.Add(SetRowControl("Удалить дубликаты", "Схлопнуть одинаковые аккаунты (один логин и пароль, разные поддомены)", DedupeControl()));

        ToolHost.Children.Add(new Border { Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 20, 0, 0), Child = g, ClipToBounds = true });
        ToolHost.Children.Add(new TextBlock { Text = "IPasswrd · локальный зашифрованный сейф (AES-256-GCM, Argon2id)", Foreground = Text3, FontSize = 12, Margin = new Thickness(2, 16, 0, 0) });
    }

    private Control SetRow(string title, string value, string hint)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, Foreground = Text, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        left.Children.Add(new TextBlock { Text = hint, Foreground = Text3, FontSize = 11.5 });
        Grid.SetColumn(left, 0);
        var val = new TextBlock { Text = value, Foreground = Text2, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(val, 1);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(17, 13) };
        g.Children.Add(left);
        g.Children.Add(val);
        return g;
    }

    private Control SetRowControl(string title, string hint, Control right)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, Foreground = Text, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        left.Children.Add(new TextBlock { Text = hint, Foreground = Text3, FontSize = 11.5 });
        Grid.SetColumn(left, 0);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(17, 11) };
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    private Control DedupeControl()
    {
        var b = new Button
        {
            Content = "Очистить",
            Padding = new Thickness(14, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Click += OnDedupeClick;
        return b;
    }

    private Control ImportControl()
    {
        var b = new Button
        {
            Content = "Выбрать файл",
            Padding = new Thickness(14, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Click += OnImportClick;
        return b;
    }

    private static readonly (string Label, int Min)[] _autolockOptions =
    {
        ("Выкл", 0), ("1 минута", 1), ("5 минут", 5), ("15 минут", 15),
        ("1 час", 60), ("3 часа", 180), ("12 часов", 720),
    };

    private Control AutolockControl()
    {
        var combo = new ComboBox
        {
            ItemsSource = _autolockOptions.Select(o => Tr(o.Label)).ToList(),
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        int idx = Array.FindIndex(_autolockOptions, o => o.Min == _autolockMinutes);
        combo.SelectedIndex = idx < 0 ? 0 : idx;
        combo.SelectionChanged += (_, _) =>
        {
            int i = combo.SelectedIndex;
            if (i >= 0 && i < _autolockOptions.Length)
            {
                _autolockMinutes = _autolockOptions[i].Min;
                SaveSettings();
                SaveQuickUnlock();          // re-stamp the cached key with the new interval
                _lastActivity = DateTimeOffset.UtcNow;
            }
        };
        return combo;
    }

    private Control ChangePasswordRow()
    {
        var sp = new StackPanel();

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = "Мастер-пароль", Foreground = Text, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        left.Children.Add(new TextBlock { Text = "Сменить пароль от сейфа", Foreground = Text3, FontSize = 11.5 });
        Grid.SetColumn(left, 0);
        var toggle = new Button { Content = "Изменить", Padding = new Thickness(13, 6) };
        Grid.SetColumn(toggle, 1);
        var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(17, 13) };
        rowGrid.Children.Add(left);
        rowGrid.Children.Add(toggle);
        sp.Children.Add(rowGrid);

        var cur = new TextBox { PasswordChar = '●', Watermark = "Текущий пароль", FontFamily = MonoFont };
        var nw  = new TextBox { PasswordChar = '●', Watermark = "Новый пароль (минимум 8)", FontFamily = MonoFont };
        var cf  = new TextBox { PasswordChar = '●', Watermark = "Повторите новый пароль", FontFamily = MonoFont };
        var status = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var apply = new Button { Content = "Сменить пароль", Padding = new Thickness(16, 9), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Classes.Add("primary");

        var form = new StackPanel { Spacing = 9, Margin = new Thickness(17, 0, 17, 14), IsVisible = false };
        form.Children.Add(cur);
        form.Children.Add(nw);
        form.Children.Add(cf);
        form.Children.Add(status);
        form.Children.Add(apply);
        sp.Children.Add(form);

        void SetStatus(string m, bool error)
        {
            status.Text = m;
            status.Foreground = error ? Bad : Ok;
            status.IsVisible = true;
        }

        toggle.Click += (_, _) =>
        {
            form.IsVisible = !form.IsVisible;
            toggle.Content = form.IsVisible ? "Отмена" : "Изменить";
            status.IsVisible = false;
            cur.Text = nw.Text = cf.Text = "";
        };

        apply.Click += (_, _) =>
        {
            _lastActivity = DateTimeOffset.UtcNow;
            string o = cur.Text ?? "", n = nw.Text ?? "", c = cf.Text ?? "";
            if (n.Length < 8) { SetStatus("Новый пароль — минимум 8 символов.", true); return; }
            if (n != c) { SetStatus("Новые пароли не совпадают.", true); return; }
            try
            {
                _vault!.ChangeMasterPassword(o, n);
                Save();
            }
            catch (WrongMasterPasswordException) { SetStatus("Текущий пароль неверный.", true); return; }
            catch (Exception ex) { SetStatus("Ошибка: " + ex.Message, true); return; }

            cur.Text = nw.Text = cf.Text = "";
            SetStatus("Мастер-пароль изменён. Он потребуется при следующем входе.", false);
        };

        return sp;
    }

    private Control ThemeControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (label, light) in new[] { ("Тёмная", false), ("Светлая", true) })
        {
            bool active = _light == light;
            var b = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = active ? Text : Text2 },
                Padding = new Thickness(13, 5), CornerRadius = new CornerRadius(6),
                Background = active ? Surface2 : Brushes.Transparent, BorderThickness = new Thickness(0),
            };
            bool l = light;
            b.Click += (_, _) => SetTheme(l);
            row.Children.Add(b);
        }
        return new Border
        {
            Child = row, Background = Bg, BorderBrush = HairStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right,
        };
    }

    private void SetTheme(bool light)
    {
        if (_light == light) return;
        _light = light;
        SaveSettings();
        RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyTheme();
        RebuildAfterTheme();
    }

    private void RebuildAfterTheme()
    {
        RenderSidebar();
        if (_toolMode is not null)
        {
            ShowTool(_toolMode);
        }
        else
        {
            LoadEntries(selectFirst: false);
            if (_currentId is not null) { try { ShowDetail(_vault!.Get(_currentId), _currentId); } catch { /* ignore */ } }
        }
    }

    private Control AutostartControl()
    {
        var ts = new ToggleSwitch { IsChecked = IsAutostartOn(), OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        ts.Checked += (_, _) => SetAutostart(true);
        ts.Unchecked += (_, _) => SetAutostart(false);
        return ts;
    }

    private Control LanguageControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (label, code) in new[] { ("Русский", "ru"), ("English", "en") })
        {
            bool active = _lang == code;
            var b = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = active ? Text : Text2 },
                Padding = new Thickness(13, 5), CornerRadius = new CornerRadius(6),
                Background = active ? Surface2 : Brushes.Transparent, BorderThickness = new Thickness(0),
            };
            string c = code;
            b.Click += (_, _) => SetLanguage(c);
            row.Children.Add(b);
        }
        return new Border
        {
            Child = row, Background = Bg, BorderBrush = HairStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right,
        };
    }

    private void SetLanguage(string lang)
    {
        if (_lang == lang) return;
        _lang = lang;
        SaveSettings();
        if (VaultScreen.IsVisible)
        {
            RenderSidebar();
            if (_toolMode is not null) ShowTool(_toolMode);
            else
            {
                LoadEntries(selectFirst: false);
                if (_currentId is not null) { try { ShowDetail(_vault!.Get(_currentId), _currentId); } catch { /* ignore */ } }
            }
        }
        else if (EditorScreen.IsVisible)
        {
            BuildEditorForm(_editExisting);
        }
        else
        {
            SetupUnlock();
        }
        Relocalize();
    }

    // ================= folder sync (iCloud Drive) =================

    private Control SyncRow()
    {
        var sp = new StackPanel();
        bool on = !string.IsNullOrEmpty(_syncProvider) || !string.IsNullOrEmpty(_syncPath);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = Tr("Синхронизация"), Foreground = Text, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        if (on)
        {
            string prov = _syncProvider == "google" ? "Google Drive" : "iCloud";
            string who = _syncProvider == "google" ? (_gdrive?.Email ?? Tr("подключено")) : (ICloudEmail() ?? Tr("подключено"));
            left.Children.Add(new TextBlock { Text = prov + " · " + who, Foreground = Text3, FontSize = 11.5 });
        }
        Grid.SetColumn(left, 0);

        var status = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Bad, Margin = new Thickness(17, 0, 17, 12) };
        var googlePanel = new StackPanel();   // holds the inline Client ID/secret + connect form when expanded

        Control right;
        if (on)
        {
            var off = new Button { Content = Tr("Отключить"), Padding = new Thickness(13, 6) };
            bool google = _syncProvider == "google";
            off.Click += (_, _) =>
            {
                if (google) DisableGoogleSync();
                else { DisableFolderSync(); _syncProvider = ""; SaveSettings(); }
                UpdateSyncChip(); ShowTool("settings");
            };
            right = off;
        }
        else
        {
            // Google — brand button opens the inline connect form (Client ID/secret → browser sign-in)
            var google = BrandButton(GoogleLogo(16), "Google", Brushes.White, Brush.Parse("#3C4043"), Brush.Parse("#DADCE0"));
            google.Click += async (_, _) =>
            {
                if (EnsureGdrive().IsConfigured)
                {
                    // Shipped build carries its own OAuth client → straight to Google sign-in, no key entry.
                    status.Foreground = Text3; status.Text = Tr("Открываю браузер для входа в Google…"); status.IsVisible = true;
                    string? err = await EnableGoogleAsync();
                    if (err is null) { UpdateSyncChip(); ShowTool("settings"); }
                    else { status.Foreground = Bad; status.Text = err; status.IsVisible = true; }
                }
                else
                {
                    // Source/dev build with no embedded client → show the Client ID / secret form.
                    googlePanel.Children.Clear();
                    if (googlePanel.Tag as string == "open") { googlePanel.Tag = null; return; }
                    googlePanel.Children.Add(BuildGoogleConnectPanel());
                    googlePanel.Tag = "open";
                }
            };

            // iCloud — Apple-style dark button with the cloud logo
            var cloud = (Control)MakeIcon("M7 18a4.5 4.5 0 0 1-.4-9A6 6 0 0 1 18.3 10 4 4 0 0 1 17.5 18H7z", 16, Brush.Parse("#4AA3F0"), 1.6, fill: Brush.Parse("#4AA3F0"));
            var icloud = BrandButton(cloud, "iCloud", Brush.Parse("#1D1D1F"), Brushes.White, Brush.Parse("#3A3A3C"));
            icloud.Click += (_, _) =>
            {
                status.Foreground = Bad;
                string? err = EnableFolderSync();
                if (err is not null) { status.Text = err; status.IsVisible = true; return; }
                _syncProvider = "icloud"; SaveSettings();
                UpdateSyncChip(); ShowTool("settings");
            };

            var brands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            brands.Children.Add(google);
            brands.Children.Add(icloud);
            right = brands;
        }
        Grid.SetColumn(right, 1);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(17, 13) };
        row.Children.Add(left);
        row.Children.Add(right);
        sp.Children.Add(row);
        sp.Children.Add(googlePanel);
        sp.Children.Add(status);
        return sp;
    }

    private static Button BrandButton(Control logo, string text, IBrush bg, IBrush fg, IBrush border)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(logo);
        content.Children.Add(new TextBlock { Text = text, Foreground = fg, FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        return new Button { Content = content, Background = bg, BorderBrush = border, BorderThickness = new Thickness(1), Padding = new Thickness(13, 7), CornerRadius = new CornerRadius(8) };
    }

    // The four-colour Google "G" mark (18×18 brand geometry).
    private static Control GoogleLogo(double size)
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        void Add(string d, string color) => canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(d), Fill = Brush.Parse(color) });
        Add("M17.64 9.205c0-.639-.057-1.252-.164-1.841H9v3.481h4.844c-.209 1.125-.843 2.078-1.796 2.717v2.258h2.909c1.702-1.567 2.684-3.874 2.684-6.615z", "#4285F4");
        Add("M9 18c2.43 0 4.467-.806 5.956-2.18l-2.909-2.259c-.806.54-1.837.859-3.048.859-2.344 0-4.328-1.583-5.036-3.71H.957v2.332C2.438 15.983 5.482 18 9 18z", "#34A853");
        Add("M3.964 10.71A5.41 5.41 0 0 1 3.682 9c0-.593.102-1.17.282-1.71V4.958H.957A9.006 9.006 0 0 0 0 9c0 1.452.348 2.827.957 4.042l3.007-2.332z", "#FBBC05");
        Add("M9 3.58c1.321 0 2.508.454 3.44 1.346l2.582-2.581C13.463.891 11.426 0 9 0 5.482 0 2.438 2.017.957 4.958L3.964 7.29C4.672 5.163 6.656 3.58 9 3.58z", "#EA4335");
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>Move the vault into iCloud Drive, merging with any copy already there. Returns an error text or null.</summary>
    private string? EnableFolderSync()
    {
        if (_vault is null) return Tr("Сначала откройте сейф.");
        string icloud = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive");
        if (!System.IO.Directory.Exists(icloud))
        {
            TryOpenICloudApp();   // official Apple sign-in window — the password never touches this app
            return Tr("Не найдена папка iCloud Drive. Я открыл окно iCloud — войдите в Apple ID, включите iCloud Drive и нажмите кнопку ещё раз. Если окно не открылось, установите «iCloud» из Microsoft Store.");
        }
        try
        {
            string dir = System.IO.Path.Combine(icloud, "IPasswrd");
            System.IO.Directory.CreateDirectory(dir);
            string target = System.IO.Path.Combine(dir, "vault.ipvault");

            if (System.IO.File.Exists(target))
            {
                try { _vault.MergeFrom(System.IO.File.ReadAllBytes(target)); }   // union with the copy from another device
                catch (VaultIntegrityException) { return Tr("В iCloud уже лежит другой сейф. Сначала решите, какой оставить."); }
            }

            _syncPath = target;
            SaveSettings();
            System.IO.File.WriteAllBytes(target, _vault.Serialize());   // materialize in iCloud right now
            Save();                                  // canonical rewrite + stamp/hash bookkeeping
            LoadEntries(selectFirst: false);
            RenderSidebar();
            return null;
        }
        catch (Exception ex)
        {
            _syncPath = null;
            SaveSettings();
            return Tr("Ошибка: ") + ex.Message;
        }
    }

    /// <summary>If sync is on but the synced file is missing while a local one exists
    /// (sync was enabled before this fix landed), materialize the copy at startup.</summary>
    private void MaterializeSyncCopy()
    {
        try
        {
            if (string.IsNullOrEmpty(_syncPath)) return;
            string? dir = System.IO.Path.GetDirectoryName(_syncPath);
            if (dir is null || !System.IO.Directory.Exists(dir)) return;
            string local = System.IO.Path.Combine(LocalDataDir(), "vault.ipvault");
            if (!System.IO.File.Exists(_syncPath) && System.IO.File.Exists(local))
                System.IO.File.Copy(local, _syncPath);
        }
        catch { /* best effort; the password unlock path still works from local */ }
    }

    private void DisableFolderSync()
    {
        _syncPath = null;
        SaveSettings();
        try { if (_vault is not null) Save(); } catch { /* best effort */ }   // current state back to the local file
        RenderSidebar();
    }

    // ================= add / edit =================

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        // On a specific section tab the "+" creates that type directly;
        // only "Все записи" offers the pick-a-type menu.
        if (_toolMode is null && _section is "account" or "passkey" or "card" or "doc" or "note")
        {
            OpenEditor(null, null, _section);
            return;
        }

        var mf = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var (label, type, icon) in new[]
        {
            ("Аккаунт", "account", "key"),
            ("Карта", "card", "card"),
            ("Документ", "doc", "doc"),
            ("Заметка", "note", "note"),
        })
        {
            var mi = new MenuItem { Header = Tr(label), Icon = MakeIcon(IconData(icon), 16, Text2, 1.5) };
            string t = type;
            mi.Click += (_, _) => OpenEditor(null, null, t);
            mf.Items.Add(mi);
        }
        mf.ShowAt(sender as Control ?? this);
    }

    private void OpenEditor(VaultItem? existing, string? id, string? forceType = null)
    {
        _editId = id;
        _editExisting = existing;
        _editType = forceType ?? existing?.Type ?? "account";
        _editControls = new Dictionary<string, TextBox>();
        BuildEditorForm(existing);
        VaultScreen.IsVisible = false;
        EditorScreen.IsVisible = true;
    }

    private void BuildEditorForm(VaultItem? existing)
    {
        string titleText = _editControls.TryGetValue("title", out var t0) ? (t0.Text ?? "") : (existing?.Title ?? "");
        _editControls = new Dictionary<string, TextBox>();
        EditorForm.Children.Clear();

        if (_editId is null)
        {
            EditorTitle.Text = Tr("Новая запись") + " · " + Tr(TypeLabel(_editType));
            EditorForm.Children.Add(LabeledBlock("Новая запись", SegType(existing)));
        }
        else
        {
            EditorTitle.Text = Tr("Изменение записи");
        }

        switch (_editType)
        {
            case "card":
                AddField("title", "Название", existing?.Title ?? titleText);
                AddField("number", "Номер карты", existing?.Fields.GetValueOrDefault("number"));
                AddField("expiry", "Срок (ММ/ГГ)", existing?.Fields.GetValueOrDefault("expiry"));
                AddField("cvc", "CVC", existing?.Fields.GetValueOrDefault("cvc"));
                AddField("holder", "Владелец", existing?.Fields.GetValueOrDefault("holder"));
                AttachDigitMask(_editControls["number"], 19, 4);   // "1234 5678 9012 3456"
                AttachExpiryMask(_editControls["expiry"]);         // "09/29" — slash appears by itself
                AttachDigitMask(_editControls["cvc"], 4, 4);       // digits only
                break;
            case "doc":
                AddField("title", "Название", existing?.Title ?? titleText);
                AddField("number", "Серия и номер", existing?.Fields.GetValueOrDefault("number"));
                AddField("issued", "Кем выдан", existing?.Fields.GetValueOrDefault("issued"));
                break;
            case "note":
                AddField("title", "Название", existing?.Title ?? titleText);
                AddField("notes", "Текст", existing?.Notes, multiline: true);
                break;
            case "passkey":
                AddField("url", "Сайт", existing?.Fields.GetValueOrDefault("url"));
                AddField("username", "Имя пользователя", existing?.Fields.GetValueOrDefault("username"));
                break;
            default: // account
                AddField("title", "Название", SiteName(existing?.Fields.GetValueOrDefault("url") ?? ""),
                    watermark: "Необязательно — иначе покажем сайт");
                AddField("url", "Сайт", existing?.Fields.GetValueOrDefault("url"));
                AddField("username", "Имя пользователя", existing?.Fields.GetValueOrDefault("username"));
                AddPasswordField(existing?.Fields.GetValueOrDefault("password"));
                AddField("totp", "Код проверки (2FA)", existing?.Fields.GetValueOrDefault("totp"),
                    watermark: "Необязательно — ключ Base32 или ссылка otpauth://");
                break;
        }

        if (_editType != "note")
            AddField("notes", "Заметка", existing?.Notes, multiline: true);

        // bottom action row (prototype: source badge left, Save right)
        var save = new Button { Content = "Сохранить", Padding = new Thickness(18, 9) };
        save.Classes.Add("primary");
        save.Click += OnEditorSave;
        var badge = SourceBadge();
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(save, 2);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(badge);
        bar.Children.Add(save);
        EditorForm.Children.Add(bar);
        Relocalize();
    }

    private void AddField(string key, string label, string? value, bool multiline = false, string? watermark = null)
    {
        var tb = new TextBox
        {
            Text = value ?? "",
            Watermark = watermark,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 70 : 0,
        };
        _editControls[key] = tb;
        EditorForm.Children.Add(Labeled(label, tb));
    }

    private void AddPasswordField(string? value)
    {
        var tb = new TextBox { Text = value ?? "", FontFamily = MonoFont };
        _editControls["password"] = tb;
        Grid.SetColumn(tb, 0);

        var gen = new Button { Content = "Сгенерировать", Margin = new Thickness(8, 0, 0, 0) };
        gen.Click += (_, _) => tb.Text = Generator.Generate(20);
        Grid.SetColumn(gen, 1);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(tb);
        row.Children.Add(gen);
        EditorForm.Children.Add(Labeled("Пароль", row));
    }

    // ---------- input masks (card editor) ----------

    // Digits-only field grouped by <group>: the card number becomes "1234 5678 9012 3456".
    private static void AttachDigitMask(TextBox tb, int maxDigits, int group)
    {
        bool busy = false;
        tb.TextChanged += (_, _) =>
        {
            if (busy) return;
            busy = true;
            try
            {
                string raw = tb.Text ?? "";
                int caret = tb.CaretIndex;
                int digitsBefore = 0;
                for (int i = 0; i < Math.Min(caret, raw.Length); i++) if (char.IsDigit(raw[i])) digitsBefore++;
                var digits = new string(raw.Where(char.IsDigit).Take(maxDigits).ToArray());
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < digits.Length; i++)
                {
                    if (i > 0 && i % group == 0) sb.Append(' ');
                    sb.Append(digits[i]);
                }
                string outp = sb.ToString();
                if (outp == raw) return;
                tb.Text = outp;
                int pos = 0, seen = 0;
                while (pos < outp.Length && seen < digitsBefore) { if (char.IsDigit(outp[pos])) seen++; pos++; }
                tb.CaretIndex = pos;
            }
            finally { busy = false; }
        };
    }

    // Expiry "ММ/ГГ": digits only, the slash appears by itself; a leading 2-9 becomes 02-09.
    private static void AttachExpiryMask(TextBox tb)
    {
        bool busy = false;
        tb.TextChanged += (_, _) =>
        {
            if (busy) return;
            busy = true;
            try
            {
                string raw = tb.Text ?? "";
                int caret = tb.CaretIndex;
                int digitsBefore = 0;
                for (int i = 0; i < Math.Min(caret, raw.Length); i++) if (char.IsDigit(raw[i])) digitsBefore++;
                var d = new string(raw.Where(char.IsDigit).ToArray());
                if (d.Length > 0 && d[0] >= '2' && d[0] <= '9') { d = "0" + d; digitsBefore++; }
                if (d.Length > 4) d = d[..4];
                string outp = d.Length <= 2 ? d : d[..2] + "/" + d[2..];
                if (outp == raw) return;
                tb.Text = outp;
                int pos = 0, seen = 0;
                while (pos < outp.Length && seen < digitsBefore) { if (char.IsDigit(outp[pos])) seen++; pos++; }
                if (pos < outp.Length && outp[pos] == '/') pos++;   // hop over the slash while typing forward
                tb.CaretIndex = pos;
            }
            finally { busy = false; }
        };
    }

    private Control Labeled(string label, Control control)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Text2, FontSize = 12, FontWeight = FontWeight.SemiBold });
        sp.Children.Add(control);
        return sp;
    }

    private Control LabeledBlock(string label, Control control)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock { Text = Tr(label).ToUpperInvariant(), Foreground = Text3, FontSize = 11, FontWeight = FontWeight.Bold, Margin = new Thickness(2, 0, 0, 0) });
        sp.Children.Add(control);
        return sp;
    }

    private Control SegType(VaultItem? existing)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, type) in new[] { ("Аккаунт", "account"), ("Карта", "card"), ("Документ", "doc"), ("Заметка", "note") })
        {
            bool active = _editType == type;
            var b = new Button
            {
                Content = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = active ? Text : Text2 },
                Padding = new Thickness(11, 5),
                Margin = new Thickness(0, 0, 2, 2),
                CornerRadius = new CornerRadius(6),
                Background = active ? Surface2 : Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            string t = type;
            b.Click += (_, _) => { _editType = t; BuildEditorForm(existing); };
            row.Children.Add(b);
        }
        return new Border
        {
            Child = row, Background = Bg, BorderBrush = HairStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private void OnEditorCancel(object? sender, RoutedEventArgs e)
    {
        EditorScreen.IsVisible = false;
        VaultScreen.IsVisible = true;
    }

    private void OnEditorSave(object? sender, RoutedEventArgs e)
    {
        if (_vault is null) return;
        string Get(string k) => _editControls.TryGetValue(k, out var tb) ? (tb.Text ?? "").Trim() : "";

        string groupName = Get("title");   // for accounts the "Название" field is the per-SITE group name (kept in _siteNames)
        var item = new VaultItem { Type = _editType, Title = groupName };
        if (_editType == "account" || _editType == "passkey")
            item.Title = DeriveTitle(Get("url"), Get("username"));   // record keeps its own site title; the group name lives in _siteNames
        if (string.IsNullOrWhiteSpace(item.Title)) item.Title = TypeLabel(_editType);

        switch (_editType)
        {
            case "card":
                SetIf(item, "number", Get("number")); SetIf(item, "expiry", Get("expiry"));
                SetIf(item, "cvc", Get("cvc")); SetIf(item, "holder", Get("holder"));
                break;
            case "doc":
                SetIf(item, "number", Get("number")); SetIf(item, "issued", Get("issued"));
                break;
            case "note":
                break;
            case "passkey":
                SetIf(item, "url", Get("url")); SetIf(item, "username", Get("username")); SetIf(item, "device", Get("device"));
                break;
            default:
                SetIf(item, "url", Get("url")); SetIf(item, "username", Get("username"));
                SetIf(item, "password", Get("password"));
                // 2FA code attached to the account: empty = remove; valid = store; invalid = keep the old one.
                string totpIn = Get("totp");
                if (totpIn.Length == 0) { /* cleared → no totp */ }
                else if (Totp.IsValidSecret(totpIn)) item.Fields["totp"] = totpIn;
                else if (_editExisting?.Fields.GetValueOrDefault("totp") is { Length: > 0 } oldTotp) item.Fields["totp"] = oldTotp;
                break;
        }
        string notes = Get("notes");
        if (!string.IsNullOrEmpty(notes)) item.Notes = notes;

        string id;
        if (_editId is null) id = _vault.Add(item);
        else { _vault.Update(_editId, item); id = _editId; }
        Save();

        if (_editType == "account")   // per-site group name, keyed by the exact URL
        {
            string nu = NormUrl(Get("url"));
            if (nu.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(groupName)) _siteNames[nu] = groupName.Trim();
                else _siteNames.Remove(nu);
                SaveSettings();
            }
        }

        EditorScreen.IsVisible = false;
        VaultScreen.IsVisible = true;
        LoadEntries(selectFirst: false);
        RenderSidebar();

        var row = (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Id == id);
        if (row is not null) EntryList.SelectedItem = row;   // triggers ShowDetail
        else { try { ShowDetail(_vault.Get(id), id); } catch { /* ignore */ } }
    }

    private static void SetIf(VaultItem it, string key, string value)
    {
        if (!string.IsNullOrEmpty(value)) it.Fields[key] = value;
    }

    // ================= import =================

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_vault is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Tr("Выберите файл экспорта паролей"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(Tr("Экспорт паролей (CSV / TXT)")) { Patterns = new[] { "*.csv", "*.txt" } },
                    FilePickerFileTypes.All,
                },
            });
            if (files.Count == 0) return;

            string content;
            await using (var stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
                content = await reader.ReadToEndAsync();

            var parsed = Importer.Parse(content);
            var toAdd = Dedup.Collapse(parsed);   // fold google.com + accounts.google.com (same login+password) inside the file
            // Ordinal (case-sensitive): passwords are part of the key and must not be case-folded.
            var seen = new HashSet<string>(_vault.Items().Select(x => Dedup.Key(x.Item)), StringComparer.Ordinal);
            int added = 0, skipped = 0;
            foreach (var it in toAdd)
            {
                if (!seen.Add(Dedup.Key(it))) { skipped++; continue; }   // already in the vault
                _vault.Add(it);
                added++;
            }
            skipped += parsed.Count - toAdd.Count;   // in-file folds also count as skipped duplicates
            Save();

            _toolMode = null;
            _section = "all";
            ListTitle.Text = "Все записи";
            ToolPane.IsVisible = false;
            LoadEntries(selectFirst: false);
            RenderSidebar();

            _currentId = null;
            ClearLiveTotps();
            DetailPanel.Children.Clear();
            var wrap = new StackPanel { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            wrap.Children.Add(new TextBlock { Text = "Импорт завершён", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Text });
            wrap.Children.Add(new TextBlock
            {
                Text = parsed.Count == 0
                    ? "Формат не распознан или файл пуст."
                    : $"{Tr("Добавлено")}: {added}\n{Tr("Пропущено дубликатов")}: {skipped}\n{Tr("Всего в файле")}: {parsed.Count}",
                Foreground = Text2,
                TextWrapping = TextWrapping.Wrap,
            });
            DetailPanel.Children.Add(wrap);
            Relocalize();
        }
        catch (Exception ex)
        {
            DetailPanel.Children.Clear();
            DetailPanel.Children.Add(new TextBlock { Text = Tr("Ошибка импорта: ") + ex.Message, Foreground = Bad, TextWrapping = TextWrapping.Wrap });
        }
    }

    // ================= remove duplicates =================

    private void OnDedupeClick(object? sender, RoutedEventArgs e)
    {
        if (_vault is null) return;
        try
        {
            int removed = 0;
            // Never fold 2FA codes (distinct secrets can share a service name) or the internal prefs record.
            foreach (var grp in _vault.Items()
                         .Where(x => x.Item.Type != "totp" && x.Item.Type != "meta")
                         .GroupBy(x => Dedup.Key(x.Item), StringComparer.Ordinal))
            {
                var list = grp.ToList();
                if (list.Count <= 1) continue;                 // unique record — keep it
                var survivor = list[0];
                for (int i = 1; i < list.Count; i++)           // keep the lowest-level host (google.com over accounts.google.com)
                    if (Dedup.Prefer(list[i].Item, survivor.Item)) survivor = list[i];
                foreach (var en in list)
                    if (en.Id != survivor.Id) { _vault.Delete(en.Id); removed++; }
            }
            if (removed > 0) Save();

            _toolMode = null;
            _section = "all";
            ListTitle.Text = "Все записи";
            ToolPane.IsVisible = false;
            LoadEntries(selectFirst: false);
            RenderSidebar();

            _currentId = null;
            ClearLiveTotps();
            DetailPanel.Children.Clear();
            var wrap = new StackPanel { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            wrap.Children.Add(new TextBlock { Text = "Удаление дубликатов", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Text });
            wrap.Children.Add(new TextBlock
            {
                Text = removed > 0
                    ? $"{Tr("Удалено дублей")}: {removed}\n{Tr("Осталось записей")}: {_vault.Items().Count(x => x.Item.Type != "totp" && x.Item.Type != "meta")}"
                    : Tr("Дубликаты не найдены"),
                Foreground = Text2,
                TextWrapping = TextWrapping.Wrap,
            });
            DetailPanel.Children.Add(wrap);
            Relocalize();
        }
        catch (Exception ex)
        {
            DetailPanel.Children.Clear();
            DetailPanel.Children.Add(new TextBlock { Text = Tr("Ошибка импорта: ") + ex.Message, Foreground = Bad, TextWrapping = TextWrapping.Wrap });
        }
    }

    // Duplicate detection lives in Core (IPasswrd.Core.Dedup) so import and, later,
    // password auto-save share one rule: same registrable domain + login + password.
    private static string DedupKey(VaultItem it) => Dedup.Key(it);

    // ================= helpers =================

    private static string Subtitle(VaultItem it) => it.Type switch
    {
        "account" => it.Fields.TryGetValue("username", out var u) ? u : "",
        "passkey" => it.Fields.TryGetValue("username", out var pu) ? pu : "",
        "card" => it.Fields.TryGetValue("number", out var n) && n.Length >= 4 ? "•••• " + LastDigits(n, 4) : "карта",
        "doc" => it.Fields.TryGetValue("number", out var d) ? d : "документ",
        "note" => (it.Notes ?? "").Split('\n')[0],
        _ => "",
    };

    private static string TypeLabel(string t) => t switch
    {
        "account" => "Аккаунт", "passkey" => "Ключ доступа", "card" => "Карта",
        "doc" => "Документ", "note" => "Заметка", _ => t,
    };

    /// <summary>Name an account/passkey after its site (host only), falling back to the username.</summary>
    private static string DeriveTitle(string url, string user)
    {
        string u = (url ?? "").Trim();
        if (u.Length > 0)
        {
            int i = u.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) u = u[(i + 3)..];
            if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = u[4..];
            int cut = u.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) u = u[..cut];
            u = u.Trim();
            if (u.Length > 0) return u;
        }
        return (user ?? "").Trim();
    }

    private static string LastDigits(string s, int n)
    {
        string d = new string(s.Where(char.IsDigit).ToArray());
        return d.Length >= n ? d[^n..] : d;
    }

    private bool Reused(string pw)
    {
        if (_vault is null || string.IsNullOrEmpty(pw)) return false;
        return _vault.Items().Count(x => x.Item.Type == "account"
            && x.Item.Fields.TryGetValue("password", out var p) && p == pw) >= 2;
    }

    private static string Group3(string code)
    {
        if (code.Length == 6) return code[..3] + " " + code[3..];
        if (code.Length == 8) return code[..4] + " " + code[4..];
        return code;
    }

    private static string GroupCard(string digits)
    {
        if (string.IsNullOrEmpty(digits)) return "";
        var parts = new List<string>();
        for (int i = 0; i < digits.Length; i += 4)
            parts.Add(digits.Substring(i, Math.Min(4, digits.Length - i)));
        return string.Join(" ", parts);
    }

    private static string MaskCard(string digits)
    {
        if (string.IsNullOrEmpty(digits)) return "";
        string last = digits.Length >= 4 ? digits[^4..] : digits;
        return "•••• •••• •••• " + last;
    }

    private static string FmtExpiry(string s)
    {
        string d = new string(s.Where(char.IsDigit).ToArray());
        if (d.Length >= 4) return d[..2] + "/" + d.Substring(2, 2);
        if (d.Length == 3) return d[..1] + "/" + d.Substring(1, 2);
        return s;
    }

    // Payment system by IIN. Order matters: МИР (2200-2204) sits INSIDE the naive "2x" zone,
    // so it must be checked BEFORE the Mastercard 2221-2720 range.
    private static string PaySys(string digits)
    {
        if (string.IsNullOrEmpty(digits)) return "";
        if (digits[0] == '4') return "VISA";
        int p2 = digits.Length >= 2 && int.TryParse(digits[..2], out var a) ? a : -1;
        int p4 = digits.Length >= 4 && int.TryParse(digits[..4], out var b) ? b : -1;
        if (p4 >= 2200 && p4 <= 2204) return "МИР";
        if (p2 >= 51 && p2 <= 55) return "Mastercard";
        if (p4 >= 2221 && p4 <= 2720) return "Mastercard";
        if (p2 == 34 || p2 == 37) return "AMEX";
        if (p2 == 62) return "UnionPay";
        if (p2 == 50 || (p2 >= 56 && p2 <= 58) || p2 == 67) return "Maestro";
        if (p4 >= 3528 && p4 <= 3589) return "JCB";
        return "";
    }

    private static string FormatDate(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var d)) return d.ToLocalTime().ToString("dd.MM.yyyy");
        return iso ?? "";
    }

    // ---------- monogram tile (per-title hue, oklch → sRGB) ----------

    private static int Hue(string s)
    {
        int h = 0;
        foreach (char c in s) h = (h * 31 + c) % 360;
        if (h < 0) h += 360;
        return h;
    }

    private static string Mono1(string s)
    {
        foreach (var r in s.EnumerateRunes()) return r.ToString().ToUpperInvariant();
        return "?";
    }

    private Control MonoTile(string title, double size, double radius, double font)
    {
        int h = Hue(title);
        var tb = new TextBlock
        {
            Text = Mono1(title), FontSize = font, FontWeight = FontWeight.Bold,
            Foreground = TileFgFor(h),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Background = TileBgFor(h),
            BorderBrush = TileBorderFor(h), BorderThickness = new Thickness(1),
            Child = tb,
        };
    }

    /// <summary>oklch(L C h / alpha) → sRGB SolidColorBrush (matches the prototype tiles).</summary>
    private static IBrush OklchBrush(double L, double C, double hDeg, double alpha)
    {
        double hr = hDeg * Math.PI / 180.0;
        double a = C * Math.Cos(hr);
        double b = C * Math.Sin(hr);

        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;
        double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;

        double r = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        byte R = Enc(r), G = Enc(g), B = Enc(bl);
        byte A = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return new SolidColorBrush(Color.FromArgb(A, R, G, B));

        static byte Enc(double c)
        {
            c = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            return (byte)Math.Round(Math.Clamp(c, 0, 1) * 255);
        }
    }

    // ---------- thin-line icons (prototype SVG paths, 24×24) ----------

    private static Control MakeIcon(string data, double size, IBrush stroke, double thickness = 1.5, IBrush? fill = null)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = fill,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    private static string IconData(string name) => name switch
    {
        "key"     => "M21 2l-2 2m-7.6 7.6a5.5 5.5 0 1 1-7.78 7.78 5.5 5.5 0 0 1 7.78-7.78zm0 0L19 4m-3.5 3.5L18 10",
        "lock"    => "M5 11h14v9a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-9zm3 0V7a4 4 0 0 1 8 0v4",
        "grid"    => "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
        "globe"   => "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zm0 0c2.5 2.4 3.8 5.5 3.8 9s-1.3 6.6-3.8 9m0-18c-2.5 2.4-3.8 5.5-3.8 9s1.3 6.6 3.8 9M3.5 9h17M3.5 15h17",
        "card"    => "M3 6.5A1.5 1.5 0 0 1 4.5 5h15A1.5 1.5 0 0 1 21 6.5v11a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5v-11zM3 9.5h18M6.5 14.5H11",
        "doc"     => "M6 3h8l4 4v13a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zm8 0v4h4M9 12h6M9 16h6",
        "note"    => "M5 4h14a1 1 0 0 1 1 1v10l-5 5H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zm10 16v-5h5M8 9h8M8 13h4",
        "copy"    => "M9 9h10a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H10a1 1 0 0 1-1-1V9zm0 0V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h4",
        "check"   => "M4 12.5l5 5L20 6.5",
        "eye"     => "M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12zm9.5 2.8a2.8 2.8 0 1 0 0-5.6 2.8 2.8 0 0 0 0 5.6z",
        "eyeoff"  => "M4 4l16 16M9.9 5.9A9.6 9.6 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.5 17.5 0 0 1-3 3.8m-2.4 1.8A9.3 9.3 0 0 1 12 18.5C6 18.5 2.5 12 2.5 12a17.7 17.7 0 0 1 4-4.6M10 10.2a2.8 2.8 0 0 0 3.9 3.9",
        "star"    => "M12 3.5l2.5 5.2 5.7.7-4.2 3.9 1.1 5.6-5.1-2.8-5.1 2.8 1.1-5.6L3.8 9.4l5.7-.7L12 3.5z",
        "trash"   => "M4.5 6.5h15M9.5 6V4.5a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1V6M6.5 6.5l.8 13a1 1 0 0 0 1 .9h7.4a1 1 0 0 0 1-.9l.8-13M10 10.5v6M14 10.5v6",
        "edit"    => "M14.5 5.5l4 4L8 20H4v-4L14.5 5.5zM12.5 7.5l4 4",
        "alert"   => "M12 3.5l9.5 16.5h-19L12 3.5zM12 10v4.5m0 3v.2",
        "plus"    => "M12 5v14M5 12h14",
        "search"  => "M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM20 20l-4.9-4.9",
        "shield"  => "M12 3l7 3v5c0 4.4-3 8.4-7 9.6C8 19.4 5 15.4 5 11V6l7-3z",
        "wand"    => "M6 21l9.6-9.6M17 3l.9 2.1L20 6l-2.1.9L17 9l-.9-2.1L14 6l2.1-.9L17 3zM8 4l.6 1.4L10 6l-1.4.6L8 8l-.6-1.4L6 6l1.4-.6L8 4zM20 12l.6 1.4L22 14l-1.4.6L20 16l-.6-1.4L18 14l1.4-.6.6-1.4z",
        "gear"    => "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zm8.5 3a8.4 8.4 0 0 0-.1-1.2l2-1.5-2-3.4-2.3 1a8.6 8.6 0 0 0-2.1-1.3L15.6 3h-4l-.4 2.6a8.6 8.6 0 0 0-2.1 1.2l-2.3-1-2 3.5 2 1.5a8.4 8.4 0 0 0 0 2.4l-2 1.5 2 3.4 2.3-1a8.6 8.6 0 0 0 2.1 1.3l.4 2.6h4l.4-2.6a8.6 8.6 0 0 0 2.1-1.2l2.3 1 2-3.5-2-1.5c.06-.4.1-.8.1-1.2z",
        "timer"   => "M12 8.5v4l2.7 1.6M12 5.5a8 8 0 1 0 0 16 8 8 0 0 0 0-16zM12 5.5V3M9.5 3h5",
        "passkey" => "M11 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7zM3.5 20.5c.8-3.6 3.9-6 7.5-6 1 0 1.9.16 2.8.47M18 17.6a2.7 2.7 0 1 0-1.5-5.2 2.7 2.7 0 0 0 1.5 5.2zM17.6 17.5l.7 3 1.7-1M17.9 12.3l.4-1.6",
        _          => "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z",
    };
}

public sealed class EntryRow
{
    public string Id { get; init; } = "";
    public IReadOnlyList<string> Ids { get; init; } = System.Array.Empty<string>();   // all logins in this site group
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Mono { get; init; } = "";
    public IBrush? TileBg { get; init; }
    public IBrush? TileFg { get; init; }
    public IBrush? TileBorder { get; init; }
    public bool Fav { get; init; }
    public bool HasBad { get; init; }
    public bool HasWarn { get; init; }
    public bool IsGroup { get; init; }
    public string CountLabel { get; init; } = "";
}
