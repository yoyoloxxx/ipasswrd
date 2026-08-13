using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using IPasswrd.Core;
using Microsoft.Win32;

namespace IPasswrd.App;

// Extra desktop features kept out of the giant MainWindow file:
//   • clipboard auto-clear (wipe a copied secret after N seconds)
//   • HIBP breach check (online, opt-in, k-anonymous)
//   • "Install extension" one-click helper (register the native host + guide load-unpacked)
public partial class MainWindow
{
    // ================= общий буфер обмена ПК↔телефон — ВРЕМЕННО ВЫКЛЮЧЕН =================
    //
    // Решение перед релизом в Store: канал общего буфера (Google Диск, iCloud clip-inbox,
    // Bluetooth-уведомления IPWCLIP, LAN /clip) отключён ЦЕЛИКОМ одним флагом — до полной
    // переработки и пересмотра после релиза. Код остаётся на месте: вернём обновлением.
    // Локальная гигиена буфера (копирование с защитой от Win+V и автоочистка) НЕ затронута,
    // как и перехват СМС-кодов — это отдельная функция.
    private static readonly bool ClipSyncEnabled = false;

    // ================= clipboard auto-clear =================

    private int _clipboardClearSeconds = 30;   // 0 = off
    private long _clipGen;                      // bumps on every copy / wipe to cancel stale timers
    private string? _lastClip;                  // the secret currently believed to be on the clipboard

    private static readonly (string Label, int Secs)[] _clipOptions =
    {
        ("Выкл", 0), ("15 секунд", 15), ("30 секунд", 30), ("1 минута", 60), ("2 минуты", 120),
    };

    /// <summary>Arm a delayed clear for a secret we just put on the clipboard. Only clears if the
    /// clipboard still holds exactly that value (so we never wipe something the user copied later).</summary>
    private async void ScheduleClipboardClear(string value)
    {
        if (_clipboardClearSeconds <= 0 || string.IsNullOrEmpty(value)) return;
        long gen = ++_clipGen;
        _lastClip = value;
        try { await Task.Delay(_clipboardClearSeconds * 1000); } catch { return; }
        if (gen != _clipGen) return;               // a newer copy (or a wipe) superseded this one
        await ClearIfMatches(value);
    }

    private async Task ClearIfMatches(string value)
    {
        try
        {
            if (Clipboard is { } cb)
            {
                string? cur = await cb.GetTextAsync();
                if (cur == value) await cb.SetTextAsync("");
            }
        }
        catch { /* clipboard busy / unavailable — best effort */ }
        if (_lastClip == value) _lastClip = null;
    }

    /// <summary>Immediately clear any pending secret (called when the vault locks).</summary>
    private void WipePendingClipboard()
    {
        if (_lastClip is null) return;
        _clipGen++;                                 // cancel the pending timer
        string v = _lastClip;
        _lastClip = null;
        _ = ClearIfMatches(v);
    }

    private Control ClipboardClearControl()
    {
        var combo = new ComboBox
        {
            ItemsSource = _clipOptions.Select(o => Tr(o.Label)).ToList(),
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        int idx = Array.FindIndex(_clipOptions, o => o.Secs == _clipboardClearSeconds);
        combo.SelectedIndex = idx < 0 ? 2 : idx;    // default → 30 s
        combo.SelectionChanged += (_, _) =>
        {
            int i = combo.SelectedIndex;
            if (i >= 0 && i < _clipOptions.Length)
            {
                _clipboardClearSeconds = _clipOptions[i].Secs;
                SaveSettings();
                _lastActivity = DateTimeOffset.UtcNow;
            }
        };
        return combo;
    }

    // ================= HIBP breach check (online, opt-in) =================

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private Dictionary<string, long>? _breachCounts;   // password -> breach count (only breached passwords kept)
    private bool _breachRunning;
    private string? _breachStatus;                     // localized status / error line (null = none)

    // ---- автопроверка по расписанию ----
    // Ручную проверку никто не запускает — сейф следит сам: раз в N дней тихо прогоняет пароли
    // через ту же k-анонимную проверку и, если что-то нашлось, зажигает точку на «Проверке»
    // в боковой панели. Ни попапов, ни уведомлений: пользователь не думает об утечках,
    // пока думать не о чем.
    private int _breachEveryDays = 7;          // 0 = выкл; частота выбирается в самом разделе проверки
    private DateTime _breachLastUtc;           // последняя УСПЕШНАЯ проверка (UTC)
    private int _breachFound;                  // сколько аккаунтов затронуто (живёт в настройках ради точки после рестарта)
    private DateTime _breachTriedUtc;          // офлайн-щадящий режим: не пробовать чаще раза в час
    private bool _breachAutoPending;           // одна отложенная автопроверка за раз

    /// <summary>Прогон всех паролей по базе утечек (общее ядро ручной и автоматической проверки).</summary>
    private async Task<Dictionary<string, long>> RunBreachScanAsync()
    {
        var pwds = _vault!.Items()
            .Where(x => x.Item.Type == "account"
                        && x.Item.Fields.TryGetValue("password", out var p) && !string.IsNullOrEmpty(p))
            .Select(x => x.Item.Fields["password"])
            .Distinct()
            .ToList();

        var counts = new Dictionary<string, long>();
        var rangeCache = new Dictionary<string, string>();        // prefix -> body, so shared prefixes hit the API once
        foreach (var pw in pwds)
        {
            string prefix = BreachCheck.Prefix(pw);
            if (!rangeCache.TryGetValue(prefix, out var body))
            {
                body = await FetchRange(prefix);
                rangeCache[prefix] = body;
            }
            long n = BreachCheck.CountInBody(pw, body);
            if (n > 0) counts[pw] = n;
        }
        return counts;
    }

    /// <summary>Сколько аккаунтов используют пробитые пароли (для точки в боковой панели).</summary>
    private int BreachAffectedCount(Dictionary<string, long> counts) =>
        _vault is null ? 0 : _vault.Items().Count(x => x.Item.Type == "account"
            && x.Item.Fields.TryGetValue("password", out var p) && counts.ContainsKey(p));

    /// <summary>Запустить автопроверку, если пришло время. Безопасно дёргать сколько угодно:
    /// сама решает, надо ли, и никогда не мешает разблокировке (стартует с паузой).</summary>
    private async Task MaybeAutoBreachCheckAsync()
    {
        if (_breachAutoPending) return;
        _breachAutoPending = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));   // разблокировка должна оставаться мгновенной
            if (_vault is null || _breachRunning || _breachEveryDays <= 0) return;
            if (_breachLastUtc != default && (DateTime.UtcNow - _breachLastUtc).TotalDays < _breachEveryDays) return;
            if (_breachTriedUtc != default && (DateTime.UtcNow - _breachTriedUtc).TotalHours < 1) return;
            _breachTriedUtc = DateTime.UtcNow;
            _breachRunning = true;
            _breachStatus = Tr("Проверяем пароли в базе утечек…");
            if (_toolMode == "security") ShowTool("security");      // раздел открыт — показать, что идёт проверка
            try
            {
                var counts = await RunBreachScanAsync();
                _breachStatus = null;
                if (_vault is null) return;   // сейф заперли посреди проверки — не трогаем результаты, придём позже
                _breachCounts = counts;
                _breachFound = BreachAffectedCount(counts);
                _breachLastUtc = DateTime.UtcNow;
                SaveSettings();
                RenderSidebar();                                    // точка появляется/гаснет сама
            }
            catch { _breachStatus = null; /* офлайн или сеть моргнула — тихо попробуем в следующий раз */ }
            finally
            {
                _breachRunning = false;
                if (_vault is not null && _toolMode == "security") ShowTool("security");  // раздел открыт — обновить
            }
        }
        finally { _breachAutoPending = false; }
    }

    private void StartBreachWatch()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        timer.Tick += (_, _) => _ = MaybeAutoBreachCheckAsync();
        timer.Start();
    }

    private async Task<string> FetchRange(string prefix)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.pwnedpasswords.com/range/" + prefix);
        req.Headers.Add("Add-Padding", "true");                       // hide the real result count from the network
        req.Headers.UserAgent.ParseAdd("IPasswrd-PasswordManager");
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async void OnBreachCheckClick()
    {
        if (_vault is null || _breachRunning) return;
        _breachRunning = true;
        _breachStatus = Tr("Проверяем пароли в базе утечек…");
        ShowTool("security");
        try
        {
            var counts = await RunBreachScanAsync();
            _breachStatus = null;
            if (_vault is not null)   // сейф заперли, пока ходили в сеть? — тогда результат не запоминаем
            {
                _breachCounts = counts;
                _breachFound = BreachAffectedCount(counts);   // ручная проверка тоже двигает расписание и точку
                _breachLastUtc = DateTime.UtcNow;
                SaveSettings();
                RenderSidebar();
            }
        }
        catch
        {
            _breachStatus = Tr("Не удалось связаться с базой утечек. Проверьте интернет и попробуйте снова.");
        }
        finally
        {
            _breachRunning = false;
            if (_vault is not null) ShowTool("security");   // на запертом сейфе перерисовывать нечего (иначе краш)
        }
    }

    /// <summary>The online breach-check block appended to the Security screen.</summary>
    private Control BuildBreachSection()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = Tr("Проверка по базе утечек").ToUpperInvariant(),
            FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Text3, Margin = new Thickness(2, 0, 0, 8),
        });

        var card = new StackPanel();
        var head = new StackPanel { Margin = new Thickness(16, 14, 16, 12), Spacing = 4 };
        head.Children.Add(new TextBlock
        {
            Text = Tr("Have I Been Pwned — крупнейшая база украденных паролей. Проверка k-анонимна: наружу уходят только первые 5 символов SHA-1, сам пароль не передаётся."),
            Foreground = Text2, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        });

        var btn = new Button
        {
            Content = _breachRunning ? Tr("Проверяем…") : Tr("Проверить пароли"),
            Padding = new Thickness(16, 8), Margin = new Thickness(0, 6, 0, 0), IsEnabled = !_breachRunning,
        };
        btn.Classes.Add("primary");
        btn.Click += (_, _) => OnBreachCheckClick();
        head.Children.Add(btn);

        // частота автопроверки — прямо здесь, рядом с ручной кнопкой
        var freqLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        freqLeft.Children.Add(new TextBlock { Text = Tr("Проверять автоматически"), Foreground = Text, FontSize = 13, FontWeight = FontWeight.SemiBold });
        freqLeft.Children.Add(new TextBlock { Text = BreachLastLine(), Foreground = Text3, FontSize = 11.5 });
        Grid.SetColumn(freqLeft, 0);
        var freqCombo = new ComboBox
        {
            ItemsSource = _breachFreqOptions.Select(o => Tr(o.Label)).ToList(),
            MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        int fi = Array.FindIndex(_breachFreqOptions, o => o.Days == _breachEveryDays);
        freqCombo.SelectedIndex = fi < 0 ? 2 : fi;    // default → раз в неделю
        freqCombo.SelectionChanged += (_, _) =>
        {
            int i = freqCombo.SelectedIndex;
            if (i >= 0 && i < _breachFreqOptions.Length)
            {
                _breachEveryDays = _breachFreqOptions[i].Days;
                SaveSettings();
                _ = MaybeAutoBreachCheckAsync();   // включили, а срок уже вышел — проверить сразу
            }
        };
        Grid.SetColumn(freqCombo, 1);
        var freqRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        freqRow.Children.Add(freqLeft);
        freqRow.Children.Add(freqCombo);
        head.Children.Add(freqRow);
        card.Children.Add(head);

        if (_breachStatus is not null)
        {
            bool err = !_breachRunning;
            card.Children.Add(new TextBlock
            {
                Text = _breachStatus, Foreground = err ? Bad : Text2, FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 14),
            });
        }
        else if (_breachCounts is null && _breachFound > 0)
        {
            // после перезапуска: сама находка помнится (ради точки), но список заново не хранится на диске
            card.Children.Add(new TextBlock
            {
                Text = string.Format(Tr("При последней проверке найдено паролей из утечек: {0}. Нажмите «Проверить пароли», чтобы увидеть список."), _breachFound),
                Foreground = Bad, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 14),
            });
        }
        else if (_breachCounts is not null)
        {
            // Map breached passwords back to the accounts that use them.
            var hits = _vault!.Items()
                .Where(x => x.Item.Type == "account"
                            && x.Item.Fields.TryGetValue("password", out var p) && _breachCounts.ContainsKey(p))
                .OrderByDescending(x => _breachCounts[x.Item.Fields["password"]])
                .ToList();

            if (hits.Count == 0)
            {
                card.Children.Add(new TextBlock
                {
                    Text = Tr("Ни один пароль не найден в известных утечках."),
                    Foreground = Ok, FontSize = 13, Margin = new Thickness(16, 0, 16, 14),
                });
            }
            else
            {
                card.Children.Add(new TextBlock
                {
                    Text = Tr("Эти пароли найдены в утечках — их стоит сменить:"),
                    Foreground = Bad, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(16, 0, 16, 8),
                });
                foreach (var e in hits)
                {
                    long n = _breachCounts[e.Item.Fields["password"]];
                    card.Children.Add(BreachRow(e.Item.Title, e.Id, n));
                }
                card.Children.Add(new Control { Height = 6 });
            }
        }

        sp.Children.Add(new Border
        {
            Background = Surface, BorderBrush = Hair, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Child = card, ClipToBounds = true,
        });
        return sp;
    }

    private static readonly (string Label, int Days)[] _breachFreqOptions =
    {
        ("Выкл", 0), ("Каждый день", 1), ("Раз в неделю", 7), ("Раз в месяц", 30),
    };

    private string BreachLastLine()
    {
        if (_breachLastUtc == default) return Tr("Ещё не проверялось");
        var age = DateTime.UtcNow - _breachLastUtc;
        var local = _breachLastUtc.ToLocalTime().Date;
        string when = age.TotalMinutes < 2 ? Tr("только что")
            : local == DateTime.Now.Date ? Tr("сегодня")
            : local == DateTime.Now.Date.AddDays(-1) ? Tr("вчера")
            : string.Format(Tr("{0} дн. назад"), Math.Max(1, (int)age.TotalDays));
        return Tr("Последняя проверка: ") + when;
    }

    private Control BreachRow(string title, string id, long count)
    {
        var tile = MonoTile(title, 30, 8, 12);
        tile.Margin = new Thickness(0, 0, 11, 0);
        Grid.SetColumn(tile, 0);

        var name = new TextBlock
        {
            Text = title, Foreground = Text, FontWeight = FontWeight.SemiBold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);

        var pill = new Border
        {
            Background = BadWash, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.Format(Tr("в {0} утечках"), FormatCount(count)),
                Foreground = Bad, FontSize = 11, FontWeight = FontWeight.Bold,
            },
        };
        Grid.SetColumn(pill, 2);

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6, 8) };
        g.Children.Add(tile); g.Children.Add(name); g.Children.Add(pill);
        var btn = new Button
        {
            Content = g, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 2), CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        btn.Click += (_, _) => OpenEntryFromTool(id);
        return btn;
    }

    private static string FormatCount(long n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.#") + " млн"
        : n >= 1_000 ? (n / 1_000.0).ToString("0.#") + " тыс."
        : n.ToString();

    // ================= install browser extension =================

    /// <summary>Development identity — fixed by the "key" in extension/manifest.json.</summary>
    private const string DevExtensionId = "pedodpphbjijbfpepobhfeedodepmean";

    /// <summary>
    /// Identity assigned by the Chrome Web Store. It does NOT match the development one — the
    /// Store signs the package with its own key — and it became known only after the first
    /// upload. It is baked into the build on purpose: every user's app has to trust the
    /// published extension, so this cannot be a per-machine setting.
    /// </summary>
    private const string StoreExtensionId = "dpagdbbdjphchgihnheiiibgmnndhchd";

    /// <summary>The extension's public page in the Chrome Web Store — the only install path we offer.</summary>
    private const string StoreExtensionUrl = "https://chromewebstore.google.com/detail/" + StoreExtensionId;

    private const string NativeHostName = "com.yoyoloxxx.ipasswrd";

    /// <summary>
    /// Постоянный id расширения в Firefox. Там манифест нативного хоста перечисляет расширения
    /// по id (allowed_extensions), а не по origin, и ключа в манифесте расширения у него нет.
    /// </summary>
    private const string FirefoxExtensionId = "ipasswrd@yoyoloxxx.dev";

    /// <summary>
    /// Every extension identity this app answers to. The settings override exists so a build
    /// can be pointed at a freshly uploaded draft without waiting for a new release.
    /// </summary>
    private IEnumerable<string> TrustedExtensionIds()
    {
        yield return DevExtensionId;   // ключ в manifest.json закрепляет этот id на любой машине: распакованная
                                       // dev-копия и zip с сайта (временно, пока Store на проверке) доверены из коробки
        if (StoreExtensionId.Length > 0) yield return StoreExtensionId;
        if (!string.IsNullOrWhiteSpace(_extraExtensionId)) yield return _extraExtensionId.Trim();
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>Locate IPasswrd.Host.exe (the native-messaging bridge).</summary>
    private static string? ResolveHostExe()
    {
        string b = AppContext.BaseDirectory;
        return FirstExisting(
            Path.Combine(b, "IPasswrd.Host.exe"),
            Path.GetFullPath(Path.Combine(b, "..", "dist-host", "IPasswrd.Host.exe")),
            Path.GetFullPath(Path.Combine(b, "..", "IPasswrd.Host", "IPasswrd.Host.exe")),
            @"D:\MyProjects\IPasswrd\dist-host\IPasswrd.Host.exe");
    }

    /// <summary>Write the native-messaging manifest next to the host exe and register it for all
    /// Chromium-family browsers on this account (HKCU). Idempotent. Returns the manifest path.</summary>
    /// <summary>
    /// Re-write the native-messaging manifest on every launch. It used to be written only when
    /// someone pressed "Install extension", which meant anyone who merely UPDATED the app kept a
    /// manifest listing yesterday's extension ids — and the published extension would be turned
    /// away by Chrome. Cheap, idempotent, and it silently repairs installs we will never see.
    /// </summary>
    private void RefreshNativeHostRegistration()
    {
        try
        {
            string? hostExe = ResolveHostExe();
            if (hostExe is not null) RegisterNativeHost(hostExe);
        }
        catch { /* best effort: the extension still has the loopback bridge */ }
    }

    private string RegisterNativeHost(string hostExe)
    {
        string dir = Path.GetDirectoryName(hostExe)!;
        string manifestPath = Path.Combine(dir, NativeHostName + ".json");

        var manifest = new Dictionary<string, object>
        {
            ["name"] = NativeHostName,
            ["description"] = "IPasswrd browser bridge",
            ["path"] = hostExe,
            ["type"] = "stdio",
            // all trusted identities at once: the same host serves the unpacked copy and the
            // Store one, and a user may well have both during the switchover
            ["allowed_origins"] = TrustedExtensionIds().Distinct().Select(id => $"chrome-extension://{id}/").ToArray(),
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var basePath in new[]
        {
            @"Software\Google\Chrome\NativeMessagingHosts\",
            @"Software\Microsoft\Edge\NativeMessagingHosts\",
            @"Software\Chromium\NativeMessagingHosts\",
            @"Software\Yandex\YandexBrowser\NativeMessagingHosts\",
        })
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(basePath + NativeHostName);
                key?.SetValue(null, manifestPath);
            }
            catch { /* a browser family that isn't installed still gets its key attempted; ignore failures */ }
        }

        RegisterFirefoxHost(hostExe, dir);
        return manifestPath;
    }

    /// <summary>
    /// Тот же хост, но по-лисичьи. Firefox читает свою ветку реестра и ждёт в манифесте
    /// allowed_extensions со своими id вида name@domain — chrome-extension://… там не понимают, и общий
    /// файл на два браузера не сделать. Пишется всегда: лишний файл рядом с приложением дешевле,
    /// чем угадывание, установлен ли Firefox.
    /// </summary>
    private static void RegisterFirefoxHost(string hostExe, string dir)
    {
        try
        {
            string path = Path.Combine(dir, NativeHostName + ".firefox.json");
            var manifest = new Dictionary<string, object>
            {
                ["name"] = NativeHostName,
                ["description"] = "IPasswrd browser bridge",
                ["path"] = hostExe,
                ["type"] = "stdio",
                ["allowed_extensions"] = new[] { FirefoxExtensionId },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\NativeMessagingHosts\" + NativeHostName);
            key?.SetValue(null, path);
        }
        catch { /* Firefox может быть не установлен — это не повод ломать остальное */ }
    }

    /// <summary>
    /// Просьба раскрыть настройку расширения сразу, не дожидаясь нажатия «Установить».
    ///
    /// Ставится с панели пустого сейфа: человек только что нажал «Настроить расширение», и требовать
    /// от него второго нажатия на ту же кнопку, уже в другом месте, незачем.
    /// </summary>
    private bool _openExtensionSetup;

    private Control InstallExtensionRow()
    {
        var sp = new StackPanel();

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = Tr("Расширение для браузера"), Foreground = Text, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        left.Children.Add(new TextBlock { Text = Tr("Автозаполнение в Chrome, Edge и других"), Foreground = Text3, FontSize = 11.5 });
        var status = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
        left.Children.Add(status);
        Grid.SetColumn(left, 0);

        var install = new Button { Content = Tr("Установить"), Padding = new Thickness(13, 6), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(install, 1);

        var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(17, 13) };
        rowGrid.Children.Add(left);
        rowGrid.Children.Add(install);
        sp.Children.Add(rowGrid);

        // Honest status: green ONLY when the extension has actually talked to this app in the
        // current session. No optimistic "everything is connected" while nothing is installed.
        void Refresh()
        {
            bool alive = _extLastSeenUtc != default;
            status.Text = Tr(alive ? "Расширение подключено" : "Расширение не подключено");
            status.Foreground = alive ? Ok : Text3;
        }
        Refresh();

        // Flip to green the moment the extension first phones in (see HandleBridgeRequest).
        Action onSeen = Refresh;
        ExtensionSeen += onSeen;
        sp.DetachedFromVisualTree += (_, _) => ExtensionSeen -= onSeen;

        // One click, one action: open the Chrome Web Store listing — the user installs the
        // extension there like any other. The native-messaging side is registered here (and on
        // every launch anyway), so the freshly installed extension connects on its own; the
        // status above turns green only once it really does.
        install.Click += (_, _) =>
        {
            try { if (ResolveHostExe() is { } hostExe) RegisterNativeHost(hostExe); } catch { /* best effort */ }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = StoreExtensionUrl, UseShellExecute = true });
            }
            catch { /* no browser handler — nothing sensible left to do */ }
        };

        if (_openExtensionSetup) _openExtensionSetup = false;   // legacy onboarding flag — the row no longer expands

        return sp;
    }
}
