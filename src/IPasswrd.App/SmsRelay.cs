using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace IPasswrd.App;

// Приём СМС-кодов с телефона.
//
// На iPhone приложениям читать СМС нельзя (запрет платформы), поэтому мост такой:
// «Быстрые команды» → автоматизация «Сообщение» (срабатывает сама) → POST текста СМС
// сюда по домашней сети. Мы вытаскиваем из текста код (4–8 цифр) и ~3 минуты отдаём его
// расширению (BridgeCredentials → smsCodes) — карточка «Код проверки» предлагает вставить.
//
// Безопасность:
//  - принимается ТОЛЬКО POST /sms с верным токеном в query (?t=…); всё прочее — 403/404;
//  - токен генерируется локально; готовый URL лежит в %LOCALAPPDATA%\IPasswrd\sms-relay.txt;
//  - наружу не отдаётся ничего (ответ всегда только {"ok":…}), сейф в этом пути не участвует;
//  - код живёт в памяти 3 минуты и уходит только расширению (pipe CurrentUserOnly /
//    loopback-HTTP с проверкой Origin) — с текстом СМС ничего больше не происходит.
public partial class MainWindow
{
    private const int SmsRelayPort = 17346;
    private static readonly TimeSpan SmsTtl = TimeSpan.FromMinutes(3);

    private TcpListener? _smsRelay;
    private string _smsToken = "";
    private readonly object _smsLock = new();
    private readonly List<(string Code, string Hint, DateTimeOffset At)> _smsInbox = new();

    /// <summary>Whether GET /clip (PC clipboard → phone) is served. OFF by default: it is the only
    /// route that could hand a just-copied PC password to a LAN peer. Loaded from settings.</summary>
    private bool _lanClipDownEnabled;

    private static string SmsDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPasswrd");

    private void StartSmsRelay()
    {
        try
        {
            string tokenPath = Path.Combine(SmsDir(), "sms-relay.token");
            if (File.Exists(tokenPath)) _smsToken = File.ReadAllText(tokenPath).Trim();
            if (_smsToken.Length < 16)
            {
                _smsToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                Directory.CreateDirectory(SmsDir());
                File.WriteAllText(tokenPath, _smsToken);
            }

            // TcpListener.Create — двухстековый сокет (IPv4 + IPv6). Важно: айфон резолвит
            // имя «yoyoloxxx.local» через Bonjour и часто предпочитает IPv6 — на IPv4-only
            // слушателе такое соединение просто не проходило бы.
            _smsRelay = TcpListener.Create(SmsRelayPort);
            _smsRelay.Start();
            _ = Task.Run(() => SmsAcceptLoop(_bridgeCts!.Token));
            WriteSmsRelayInfo();
        }
        catch { _smsRelay = null; /* порт занят — фича просто выключена */ }
        StartSmsDropFolder();
        StartClipDropFolder();
        StartNetBeacon();
        StartNotifSmsListener();
    }

    // ---- главный канал: уведомления Windows от «Связи с телефоном» (Phone Link) ----
    // Айфон спарен с ПК по Bluetooth; входящая СМС всплывает уведомлением Windows —
    // ловим его событием + частым опросом (уведомление могут быстро прочитать/смахнуть),
    // вытаскиваем код и кладём в тот же ящик, что и остальные каналы.
    private UserNotificationListener? _notifListener;
    private readonly HashSet<uint> _notifSeen = new();

    /// <summary>Журнал увиденных уведомлений — чтобы разбирать «почему не доехало» без гаданий.</summary>
    private static void NotifLog(string line)
    {
        try
        {
            string p = Path.Combine(SmsDir(), "notif-log.txt");
            File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}", new UTF8Encoding(false));
            var fi = new FileInfo(p);
            if (fi.Length > 256 * 1024) File.WriteAllText(p, "", new UTF8Encoding(false));
        }
        catch { }
    }

    private void StartNotifSmsListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var listener = UserNotificationListener.Current;
                var access = await listener.RequestAccessAsync();
                if (access != UserNotificationListenerAccessStatus.Allowed) return;
                _notifListener = listener;

                try { listener.NotificationChanged += (_, _) => _ = ScanNotifsSafeAsync(); }
                catch { /* событие бывает недоступно для обычных приложений — остаётся опрос */ }

                while (_bridgeCts is { IsCancellationRequested: false })
                {
                    await ScanNotifsSafeAsync();
                    try { await Task.Delay(2000, _bridgeCts.Token); } catch { return; }
                }
            }
            catch { /* WinRT недоступен — канал выключен, остальные работают */ }
        });
    }

    private async Task ScanNotifsSafeAsync()
    {
        try
        {
            if (_notifListener is null) return;
            var notifs = await _notifListener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (UserNotification n in notifs)
            {
                if (!_notifSeen.Add(n.Id)) continue;

                string app = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                // Только «Связь с телефоном» / Phone Link — там живут СМС с айфона.
                if (app.IndexOf("телефон", StringComparison.OrdinalIgnoreCase) < 0 &&
                    app.IndexOf("phone", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string text = "";
                string[] parts = Array.Empty<string>();
                try
                {
                    var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding is not null)
                    {
                        parts = binding.GetTextElements().Select(t => t.Text ?? "").ToArray();
                        text = string.Join(" ", parts);
                    }
                }
                catch { }

                // ---- общий буфер «телефон → ПК» БЕЗ СЕТИ ----
                // Быстрая команда на айфоне показывает уведомление «IPWCLIP: <буфер>»; оно
                // уходит по Bluetooth в «Связь с телефоном» и всплывает здесь. Кладём текст
                // в буфер Windows и стираем уведомление из центра, чтобы не мусорить.
                // Работает везде, где телефон рядом: Wi-Fi/LTE не нужны вовсе.
                string? clip = ClipSyncEnabled ? ExtractClipPayload(parts) : null;   // общий буфер выключен до после-релизной переработки
                // Never write the notification text itself to disk — it is the SMS / clipboard content
                // (i.e. potentially a code or a password). Log only shape and detected lengths.
                NotifLog($"[{app}] уведомление: {parts.Length} эл.  => clip:{(clip is null ? "-" : clip.Length + " симв.")}");
                if (clip is not null)
                {
                    // Уведомление приходит по Bluetooth на пару секунд ПОЗЖЕ мгновенного POST —
                    // и несёт обрезок (~150 символов и «…»). Затирать им уже приехавший
                    // полный текст нельзя: человек вставит огрызок вместо абзаца.
                    if (IsPrefixOfLastApplied(clip))
                    {
                        NotifLog("   Bluetooth: это обрезок уже приехавшего текста — пропускаю");
                        try { _notifListener?.RemoveNotification(n.Id); } catch { }
                        continue;
                    }
                    await ApplyClipboardAsync(clip, "Bluetooth");
                    try { _notifListener?.RemoveNotification(n.Id); } catch { }
                    continue;
                }

                string? code = ExtractSmsCode(text);
                if (code is null) continue;

                lock (_smsLock)
                {
                    _smsInbox.RemoveAll(x => DateTimeOffset.UtcNow - x.At > SmsTtl || x.Code == code);
                    _smsInbox.Add((code, "СМС", DateTimeOffset.UtcNow));
                    while (_smsInbox.Count > 5) _smsInbox.RemoveAt(0);
                }
            }
            if (_notifSeen.Count > 500) _notifSeen.Clear();   // не даём множеству расти вечно
        }
        catch { /* гонка с исчезающим уведомлением — не страшно */ }
    }

    // ---- фолбэк вне дома: файл через iCloud Drive ----
    // С мобильного интернета телефон до ПК не достучится, поэтому вне дома Быстрая команда
    // сохраняет текст СМС файлом в iCloud Drive\IPasswrd\sms-inbox\ — iCloud-клиент приносит
    // его сюда (обычно за 10-60 с, СМС-коду хватает), мы подхватываем и удаляем файл.
    private FileSystemWatcher? _smsDropWatcher;

    private static string SmsDropDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "iCloudDrive", "IPasswrd", "sms-inbox");

    private void StartSmsDropFolder()
    {
        try
        {
            string dir = SmsDropDir();
            Directory.CreateDirectory(dir);
            _smsDropWatcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, e) => _ = Task.Run(() => ConsumeSmsDropAsync(e.FullPath));
            _smsDropWatcher.Created += h;
            _smsDropWatcher.Changed += h;
            _smsDropWatcher.Renamed += (_, e) => _ = Task.Run(() => ConsumeSmsDropAsync(e.FullPath));
            foreach (string f in Directory.GetFiles(dir))                       // что уже дожидалось
                _ = Task.Run(() => ConsumeSmsDropAsync(f));
        }
        catch { _smsDropWatcher = null; /* нет iCloud Drive — фолбэк выключен */ }
    }

    private async Task ConsumeSmsDropAsync(string path)
    {
        try
        {
            await Task.Delay(500);                       // дать iCloud дописать файл
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 64 * 1024) { File.Delete(path); return; }
            string text = await File.ReadAllTextAsync(path);
            string? code = ExtractSmsCode(text);
            if (code is not null)
            {
                lock (_smsLock)
                {
                    _smsInbox.RemoveAll(x => DateTimeOffset.UtcNow - x.At > SmsTtl || x.Code == code);
                    _smsInbox.Add((code, "iCloud", DateTimeOffset.UtcNow));
                    while (_smsInbox.Count > 5) _smsInbox.RemoveAt(0);
                }
            }
            try { File.Delete(path); } catch { /* iCloud держит файл — заберём при следующем событии */ }
        }
        catch { /* файл ещё качается — придёт Changed */ }
    }

    // ---- ДЛИННЫЙ буфер через iCloud ----
    // Уведомление по Bluetooth режет текст на ~150 символах, поэтому в Быстрой команде
    // стоит развилка: короткое — уведомлением (мгновенно, без сети), длинное — файлом
    // в iCloud Drive\IPasswrd\clip-inbox (туда же, где живёт сейф). Здесь ловим файл,
    // кладём текст в буфер и сразу удаляем — копия буфера в облаке не задерживается.
    private FileSystemWatcher? _clipDropWatcher;
    private string _lastApplied = "";
    private DateTimeOffset _lastAppliedAt = DateTimeOffset.MinValue;

    private static string ClipDropDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "iCloudDrive", "IPasswrd", "clip-inbox");

    private void StartClipDropFolder()
    {
        if (!ClipSyncEnabled) return;   // iCloud clip-inbox (общий буфер) выключен до после-релизной переработки
        try
        {
            string dir = ClipDropDir();
            Directory.CreateDirectory(dir);
            _clipDropWatcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, e) => _ = Task.Run(() => ConsumeClipDropAsync(e.FullPath));
            _clipDropWatcher.Created += h;
            _clipDropWatcher.Changed += h;
            _clipDropWatcher.Renamed += (_, e) => _ = Task.Run(() => ConsumeClipDropAsync(e.FullPath));

            // Что уже дожидалось (ПК был выключен): берём ТОЛЬКО самый свежий —
            // старые копии буфера не нужны и лежать в облаке им точно не стоит.
            var waiting = new DirectoryInfo(dir).GetFiles()
                                                .OrderByDescending(f => f.LastWriteTimeUtc)
                                                .ToList();
            if (waiting.Count > 0)
            {
                foreach (var stale in waiting.Skip(1)) TryDelete(stale.FullName);
                string newest = waiting[0].FullName;
                _ = Task.Run(() => ConsumeClipDropAsync(newest));
            }

            // Подстраховка: iCloud кладёт файл своим способом и FileSystemWatcher его иногда
            // не видит (ловили живьём: файл лежит, события нет). Раз в 20 секунд смотрим сами.
            _ = Task.Run(async () =>
            {
                while (_bridgeCts is { IsCancellationRequested: false })
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(20), _bridgeCts.Token); } catch { return; }
                    try
                    {
                        var f = new DirectoryInfo(dir).GetFiles()
                                                      .OrderByDescending(x => x.LastWriteTimeUtc)
                                                      .FirstOrDefault();
                        if (f is not null) await ConsumeClipDropAsync(f.FullName);
                    }
                    catch { }
                }
            });
        }
        catch { _clipDropWatcher = null; /* нет iCloud Drive — длинный канал выключен */ }
    }

    private async Task ConsumeClipDropAsync(string path)
    {
        try
        {
            if (path.EndsWith(".icloud", StringComparison.OrdinalIgnoreCase)) return;   // ещё не скачан
            await Task.Delay(600);                       // дать iCloud дописать файл
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 1024 * 1024) { TryDelete(path); return; }

            string text;
            try { text = await File.ReadAllTextAsync(path); }
            catch { return; }                            // файл ещё качается — придёт Changed
            if (text.Length == 0) { TryDelete(path); return; }

            // Разворачиваем RTF ДО сравнения — иначе сравнивали бы разметку с готовым текстом
            // и дубль проходил бы как новый (видели: файл применялся дважды).
            if (text.StartsWith(@"{\rtf", StringComparison.Ordinal))
            {
                string plain = RtfToText(text);
                if (plain.Length > 0) text = plain;
            }

            lock (_smsLock)
            {
                // Два случая сразу: парные события Создание+Изменение и — главное — тот же
                // текст, что уже приехал мгновенным путём: его iCloud-копия приходит через
                // полминуты и НЕ должна затирать то, что человек скопировал на ПК позже.
                if (text == _lastApplied && DateTimeOffset.UtcNow - _lastAppliedAt < TimeSpan.FromMinutes(10))
                {
                    NotifLog($"   iCloud: тот же текст ({text.Length} симв.) уже приехал раньше — пропускаю");
                    TryDelete(path);
                    return;
                }
            }

            await ApplyClipboardAsync(text, "iCloud");
            TryDelete(path);
        }
        catch { /* гонка с синхронизацией — не страшно */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* iCloud держит файл — заберём позже */ }
    }

    // ---- маячок ·в какой я сети· ----
    // Телефону нужно понять, стоит ли биться напрямую в ПК. Пробовать и ловить ошибку
    // нельзя: в «Быстрых командах» нет обработки ошибок — неудачный запрос рвёт
    // всю команду с алертом. Поэтому ПК сам публикует имя своей текущей Wi-Fi-сети
    // в iCloud (pc-net.txt), а команда просто сравнивает его со своим — сравнение
    // упасть не может. Совпало → одна сеть → мгновенный POST; нет → через iCloud.
    // Плюс так оно само подхватывает любую новую сеть: дача, офис, чужой Wi-Fi,
    // переименованный роутер — ничего не надо править руками.
    private static string PcNetPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "iCloudDrive", "IPasswrd", "pc-net.txt");

    /// <summary>Готовый адрес приёмника для Быстрой команды. Имя «yoyoloxxx.local» годится
    /// не всегда: в режиме модема айфон не резолвит .local через свой же мост — запрос висит.
    /// Поэтому ПК публикует свой текущий IP — и он же сам меняется при смене сети.</summary>
    private static string PcUrlPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "iCloudDrive", "IPasswrd", "pc-url.txt");

    /// <summary>IPv4 той сетевой карты, через которую ПК виден телефону:
    /// сначала Wi-Fi, потом любая другая. Тоннели VPN исключаем — туда телефону не попасть.</summary>
    private static string BestLanIPv4()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && n.Description.IndexOf("tun", StringComparison.OrdinalIgnoreCase) < 0
                            && n.Name.IndexOf("tun", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            string Pick(Func<NetworkInterface, bool> where) => nics
                .Where(where)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal)) ?? "";

            string wifi = Pick(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            return wifi.Length > 0 ? wifi : Pick(_ => true);
        }
        catch { return ""; }
    }

    private readonly SemaphoreSlim _netChanged = new(0);

    private void StartNetBeacon()
    {
        // Пересаживание с домашнего Wi-Fi на раздачу телефона должно попадать в маячок
        // СРАЗУ, а не через полминуты: иначе команда всё это время считает, что ПК
        // в другой сети, и гонит длинный текст через облако вместо прямого пути.
        try { NetworkChange.NetworkAddressChanged += (_, _) => { try { _netChanged.Release(); } catch { } }; }
        catch { /* без событий останется опрос раз в 30 с */ }

        _ = Task.Run(async () =>
        {
            string last = " ";
            while (_bridgeCts is { IsCancellationRequested: false })
            {
                try
                {
                    string ssid = CurrentSsid();
                    string val = ssid.Length > 0 ? ssid : "(нет Wi-Fi)";
                    string ip  = BestLanIPv4();
                    string url = ip.Length > 0 ? $"http://{ip}:{SmsRelayPort}/clip?t={_smsToken}" : "";
                    string stamp = val + "|" + url;
                    if (stamp != last)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(PcNetPath())!);
                        File.WriteAllText(PcNetPath(), val, new UTF8Encoding(false));   // без перевода строки
                        if (url.Length > 0)
                            File.WriteAllText(PcUrlPath(), url, new UTF8Encoding(false));
                        NotifLog($"сеть ПК: {val}  →  {ip}");
                        last = stamp;
                    }
                }
                catch { /* нет iCloud Drive — просто нет быстрого пути */ }

                // Ждём либо смены сети, либо 30 секунд — что раньше.
                try
                {
                    if (await _netChanged.WaitAsync(TimeSpan.FromSeconds(30), _bridgeCts.Token))
                    {
                        while (await _netChanged.WaitAsync(0, _bridgeCts.Token)) { }   // события прилетают пачкой
                        await Task.Delay(TimeSpan.FromSeconds(2), _bridgeCts.Token);   // дать адаптеру устояться
                    }
                }
                catch { return; }
            }
        });
    }

    /// <summary>Имя текущей Wi-Fi-сети ПК (пусто, если кабель или Wi-Fi выключен).</summary>
    private static string CurrentSsid()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "";
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            foreach (string line in outp.Split('\n'))
            {
                string t = line.Trim();
                if (t.Contains("BSSID", StringComparison.OrdinalIgnoreCase)) continue;   // «AP BSSID» — не то
                int i = t.IndexOf(':');
                if (i <= 0) continue;
                if (!t[..i].Trim().Equals("SSID", StringComparison.OrdinalIgnoreCase)) continue;
                return t[(i + 1)..].Trim();
            }
        }
        catch { }
        return "";
    }

    /// <summary>RTF → обычный текст. Без полноценного парсера: хватает для того,
    /// что реально копируют — абзацы из браузера, заметок и переписки.</summary>
    internal static string RtfToText(string rtf)
    {
        string s = rtf;
        // служебные группы: таблицы шрифтов/цветов, метаданные, картинки
        for (int i = 0; i < 4; i++)
            s = Regex.Replace(s, @"\{\\\*?\\?(?:fonttbl|colortbl|stylesheet|info|pict|object|themedata|datastore|listtable|listoverridetable|expandedcolortbl|generator)[^{}]*\}", "");
        s = Regex.Replace(s, @"\\par[d]?(?![a-zA-Z])", "\n");
        s = Regex.Replace(s, @"\\line(?![a-zA-Z])", "\n");
        s = Regex.Replace(s, @"\\tab(?![a-zA-Z])", "\t");
        s = Regex.Replace(s, @"\\u(-?\d+)\s?\??", m =>                      // кириллица и эмодзи
        {
            if (!int.TryParse(m.Groups[1].Value, out int code)) return "";
            if (code < 0) code += 65536;
            return code is > 0 and < 0x110000 ? char.ConvertFromUtf32(code) : "";
        });
        s = Regex.Replace(s, @"\\'([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        s = Regex.Replace(s, @"\\[a-zA-Z]+-?\d*\s?", "");                    // остальные команды
        s = s.Replace("{", "").Replace("}", "").Replace("\\\n", "\n");
        s = Regex.Replace(s, @"[ \t]+(\n)", "$1");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>Is the remote peer on a private/local network (RFC1918 / link-local / loopback, or an
    /// IPv6 loopback / link-local / unique-local)? Public, routed peers are refused — the relay is a
    /// LAN convenience, never internet-facing.</summary>
    private static bool IsLanClient(System.Net.EndPoint? ep)
    {
        if (ep is not IPEndPoint ipep) return false;
        IPAddress a = ipep.Address;
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10) return true;                                 // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12 (incl. hotspot 172.20.10.x)
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 link-local
            if (b[0] == 127) return true;                                // loopback
            return false;
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv6LinkLocal) return true;                          // fe80::/10
            byte[] b = a.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 unique-local
            return false;
        }
        return false;
    }

    /// <summary>Пробелы/переводы строк — в один пробел: в тексте уведомления переносов нет.</summary>
    private static string NormWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Отпечаток для сравнения начал двух текстов: только буквы и цифры.
    /// Пунктуацию и пробелы выбрасываем: в уведомлении они часто другие, чем в исходном тексте
    /// (переносы становятся пробелами, тире и кавычки подменяются).</summary>
    private static string Sig(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Это обрезок («…») того же текста, что уже лежит в буфере целиком?</summary>
    private bool IsPrefixOfLastApplied(string candidate)
    {
        string head = Sig(candidate);
        if (head.Length < 20) return false;                 // слишком коротко, чтобы судить
        if (head.Length > 60) head = head[..60];            // хватит для уверенного совпадения
        string last;
        lock (_smsLock)
        {
            if (DateTimeOffset.UtcNow - _lastAppliedAt > TimeSpan.FromMinutes(3)) return false;
            last = Sig(_lastApplied);
        }
        bool same = last.Length > head.Length && last.StartsWith(head, StringComparison.Ordinal);
        if (!same && last.Length > 0)
            NotifLog($"   (не обрезок: {head.Length} симв. против {last.Length} в буфере)");   // без содержимого
        return same;
    }

    /// <summary>Единая точка записи буфера для всех трёх каналов (Bluetooth / сеть / iCloud):
    /// одинаковое поведение и одинаковая запись в журнале — видно, откуда пришёл текст.</summary>
    private async Task<bool> ApplyClipboardAsync(string text, string source)
    {
        try
        {
            // Если на телефоне скопирован ФОРМАТИРОВАННЫЙ текст (из Safari, Заметок, чата),
            // «Буфер обмена» в Быстрых командах отдаёт RTF — и в буфер ПК легла бы разметка
            // вместо текста. Разворачиваем сами — подстраховка на случай, если на телефоне
            // забыли действие «Получить текст из ввода».
            if (text.StartsWith(@"{\rtf", StringComparison.Ordinal))
            {
                string plain = RtfToText(text);
                if (plain.Length > 0)
                {
                    NotifLog($"   RTF → текст: {text.Length} → {plain.Length} симв.");
                    text = plain;
                }
            }

            await Dispatcher.UIThread.InvokeAsync(
                () => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask);
            lock (_smsLock) { _lastApplied = text; _lastAppliedAt = DateTimeOffset.UtcNow; }
            NotifLog($"   буфер ← {source}: {text.Length} симв. ✓");
            return true;
        }
        catch (Exception ex)
        {
            NotifLog($"   буфер ← {source}: НЕ записан — {ex.Message}");
            return false;
        }
    }

    /// <summary>Готовые адреса для вставки в Быструю команду — в sms-relay.txt рядом с сейфом.</summary>
    private void WriteSmsRelayInfo()
    {
        try
        {
            var ips = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Where(ip => !ip.StartsWith("169.254."))
                .Distinct()
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("IPasswrd — приём СМС-кодов с телефона (для автоматизации «Быстрых команд»).");
            sb.AppendLine();
            sb.AppendLine("В действии «Получить содержимое URL»: метод POST, тело — текст СМС.");
            sb.AppendLine("Телефон должен быть в одной сети с этим компьютером.");
            sb.AppendLine();
            sb.AppendLine($"URL по имени компьютера: http://{Environment.MachineName.ToLowerInvariant()}.local:{SmsRelayPort}/sms?t={_smsToken}");
            foreach (string ip in ips)
                sb.AppendLine($"URL по адресу:           http://{ip}:{SmsRelayPort}/sms?t={_smsToken}");
            sb.AppendLine();
            sb.AppendLine("Вне дома (мобильный интернет): вместо URL сохраняйте текст СМС файлом");
            sb.AppendLine("в iCloud Drive → IPasswrd → sms-inbox (действие «Сохранить файл», с заменой).");
            sb.AppendLine();
            sb.AppendLine("БУФЕР ТЕЛЕФОН → ПК, ОСНОВНОЙ ПУТЬ (сеть НЕ нужна):");
            sb.AppendLine("  Быстрая команда «На ПК»: Получить буфер обмена → Показать уведомление");
            sb.AppendLine("  (в теле — переменная «Буфер обмена»). Уведомление едет по Bluetooth");
            sb.AppendLine("  через «Связь с телефоном». Предел канала ~150 символов.");
            sb.AppendLine();
            sb.AppendLine("ДЛИННЫЙ ТЕКСТ (любая сеть с интернетом, задержка 10-60 с):");
            sb.AppendLine("  Действие «Сохранить файл» → iCloud Drive, путь /IPasswrd/clip-inbox/clip.txt");
            sb.AppendLine("  (с заменой, без вопроса куда сохранять). Приложение заберёт и удалит.");
            sb.AppendLine();
            sb.AppendLine("ЗАПАСНОЙ СЕТЕВОЙ ПУТЬ (та же сеть, мгновенно):");
            sb.AppendLine("  Токен можно слать заголовком X-IPW-Token (надёжнее — не попадает в логи URL)");
            sb.AppendLine("  ЛИБО как ?t=… в адресе. Принимаются только адреса из локальной сети.");
            sb.AppendLine($"  Телефон → ПК: POST http://{Environment.MachineName.ToLowerInvariant()}.local:{SmsRelayPort}/clip?t={_smsToken}   (тело — текст буфера)");
            sb.AppendLine($"  ПК → телефон: GET  http://{Environment.MachineName.ToLowerInvariant()}.local:{SmsRelayPort}/clip?t={_smsToken}   (в ответе JSON, поле text)");
            sb.AppendLine("     ⚠ ПК → телефон (GET /clip) по умолчанию ВЫКЛЮЧЕН — включается в Настройках.");
            File.WriteAllText(Path.Combine(SmsDir(), "sms-relay.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* информационный файл — не критично */ }
    }

    private async Task SmsAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _smsRelay!.AcceptTcpClientAsync(ct);
                var c = client; client = null;
                _ = Task.Run(() => SmsServe(c, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { client?.Dispose(); return; }
            catch { client?.Dispose(); try { await Task.Delay(300, ct); } catch { return; } }
        }
    }

    private async Task SmsServe(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            // Only ever talk to a peer on a private/local network (home Wi-Fi, phone hotspot).
            // If the machine ends up on a public IP or someone port-forwards this port, routed
            // clients from the internet are refused outright — the relay is a LAN convenience,
            // never an internet-facing service.
            if (!IsLanClient(client.Client.RemoteEndPoint)) return;
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();

            var head = new MemoryStream();
            var one = new byte[1];
            while (head.Length < 16 * 1024)
            {
                int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (n == 0) return;
                head.WriteByte(one[0]);
                if (head.Length >= 4)
                {
                    var b = head.GetBuffer();
                    long L = head.Length;
                    if (b[L - 4] == '\r' && b[L - 3] == '\n' && b[L - 2] == '\r' && b[L - 1] == '\n') break;
                }
            }
            string headText = Encoding.UTF8.GetString(head.GetBuffer(), 0, (int)head.Length);
            var lines = headText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var req = lines[0].Split(' ');
            if (req.Length < 2) return;
            string method = req[0].ToUpperInvariant();
            string target = req[1];

            string Header(string name)
            {
                foreach (var l in lines.Skip(1))
                {
                    int i = l.IndexOf(':');
                    if (i > 0 && l[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return l[(i + 1)..].Trim();
                }
                return "";
            }

            async Task Send(string status, string body)
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string h = $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(h), ct);
                await stream.WriteAsync(payload, ct);
            }

            // ---- маршруты: POST /sms, POST /clip (телефон→буфер ПК), GET /clip (буфер ПК→телефон) ----
            int q = target.IndexOf('?');
            string path = q < 0 ? target : target[..q];
            string query = q < 0 ? "" : target[(q + 1)..];
            // Token may arrive in the X-IPW-Token header (preferred — never lands in a URL log) or,
            // for the limited Shortcuts client, in the ?t= query parameter.
            string token = Header("X-IPW-Token");
            if (token.Length == 0)
            {
                foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && pair[..eq] == "t") token = Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }

            bool smsUp  = path == "/sms"  && method == "POST";
            bool clipUp = ClipSyncEnabled && path == "/clip" && method == "POST";   // общий буфер выключен до после-релизной переработки
            bool clipDn = ClipSyncEnabled && path == "/clip" && method == "GET";
            if (!smsUp && !clipUp && !clipDn) { await Send("404 Not Found", "{\"ok\":false}"); return; }
            if (_smsToken.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_smsToken)))
            { await Send("403 Forbidden", "{\"ok\":false}"); return; }

            // Handing the PC clipboard back over the LAN is the one route that can leak whatever was
            // just copied on the PC (often a password). It stays OFF unless explicitly turned on.
            if (clipDn && !_lanClipDownEnabled)
            { await Send("403 Forbidden", "{\"ok\":false,\"error\":\"clip_down_disabled\"}"); return; }

            if (clipDn)
            {
                // Буфер ПК → телефон (текст; всё прочее отдаём пустым).
                string cur = "";
                try
                {
                    cur = await Dispatcher.UIThread.InvokeAsync(
                        () => Clipboard?.GetTextAsync() ?? Task.FromResult<string?>(null)) ?? "";
                }
                catch { }
                if (cur.Length > 64 * 1024) cur = cur[..(64 * 1024)];
                await Send("200 OK", JsonSerializer.Serialize(new { ok = true, text = cur }));
                return;
            }

            int len = int.TryParse(Header("Content-Length"), out var cl) ? cl : 0;
            if (len < 0 || len > 64 * 1024) { await Send("400 Bad Request", "{\"ok\":false}"); return; }
            var body = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = await stream.ReadAsync(body.AsMemory(got, len - got), ct);
                if (n == 0) break;
                got += n;
            }
            string raw = Encoding.UTF8.GetString(body, 0, got);

            if (clipUp)
            {
                // Телефон → буфер ПК. Тело кладём как есть (никакого JSON-разбора:
                // вдруг пользователь копирует именно JSON).
                if (raw.Length == 0) { await Send("200 OK", "{\"ok\":true,\"clip\":false}"); return; }
                if (!await ApplyClipboardAsync(raw, "сеть")) { await Send("200 OK", "{\"ok\":false}"); return; }
                await Send("200 OK", "{\"ok\":true,\"clip\":true}");
                return;
            }

            string text = raw.Trim();
            string from = "";

            // Тело СМС: голый текст либо JSON {"text":"…","from":"…"}.
            if (text.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("text", out var t)) text = t.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("from", out var f)) from = f.GetString() ?? "";
                }
                catch { /* не JSON — оставляем как есть */ }
            }

            string? code = ExtractSmsCode(text);
            if (code is null) { await Send("200 OK", "{\"ok\":true,\"code\":false}"); return; }

            lock (_smsLock)
            {
                _smsInbox.RemoveAll(x => DateTimeOffset.UtcNow - x.At > SmsTtl || x.Code == code);
                _smsInbox.Add((code, from.Trim(), DateTimeOffset.UtcNow));
                while (_smsInbox.Count > 5) _smsInbox.RemoveAt(0);
            }
            await Send("200 OK", "{\"ok\":true,\"code\":true}");
        }
        catch { /* оборванный клиент — не важно */ }
    }

    /// <summary>Код из текста СМС: 4–8 цифр отдельным числом; при нескольких — сначала 6-значные,
    /// затем ближе к словам про код.</summary>
    internal static string? ExtractSmsCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var all = Regex.Matches(text, @"(?<!\d)\d{4,8}(?!\d)").Select(m => m).ToList();
        if (all.Count == 0) return null;
        if (all.Count == 1) return all[0].Value;

        var kw = Regex.Match(text, @"(?i)(код|code|пароль|otp|подтвержд|verif|провер)");
        return all
            .OrderBy(m => m.Value.Length == 6 ? 0 : m.Value.Length >= 5 ? 1 : 2)     // сначала похожие на код
            .ThenBy(m => kw.Success ? Math.Abs(m.Index - kw.Index) : m.Index)         // затем ближайшие к слову «код»
            .First().Value;
    }

    /// <summary>Имя Быстрой команды-отправителя буфера (оно же заголовок уведомления).</summary>
    private static readonly string[] ClipShortcutTitles = { "На ПК", "На ПК — буфер", "IPasswrd буфер" };

    /// <summary>Приложение «Команды» на айфоне — первый элемент зеркального уведомления.</summary>
    private static bool IsShortcutsApp(string s)
    {
        s = s.Trim();
        return s.Equals("Команды", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Быстрые команды", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Shortcuts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Текст буфера из уведомления Быстрой команды.
    /// Два способа узнать своё уведомление, чтобы не зависеть от одного:
    ///  1) метка IPWCLIP в тексте — работает для любого имени команды;
    ///  2) заголовок = имя нашей команды — работает, даже если в теле одна переменная без метки.
    /// Элементы зеркального уведомления: [приложение на айфоне, заголовок, тело…].</summary>
    internal static string? ExtractClipPayload(IReadOnlyList<string> parts)
    {
        if (parts is null || parts.Count == 0) return null;

        // 1) явная метка в любом месте текста
        string joined = string.Join(" ", parts);
        if (!string.IsNullOrWhiteSpace(joined))
        {
            var m = Regex.Match(joined, @"(?is)IPWCLIP\s*[:：]?\s*(.*)$");   // двоеточие необязательно
            if (m.Success)
            {
                string p = m.Groups[1].Value.Trim();
                if (p.Length > 0) return p;
            }
        }

        // 2) «Команды» + заголовок нашей команды → тело целиком есть буфер
        if (parts.Count >= 3 && IsShortcutsApp(parts[0]))
        {
            bool ours = ClipShortcutTitles.Any(
                t => parts[1].Trim().Equals(t, StringComparison.OrdinalIgnoreCase));
            if (ours)
            {
                string rest = string.Join(" ", parts.Skip(2)).Trim();
                if (rest.Length > 0) return rest;
            }
        }
        return null;
    }

    /// <summary>Свежие СМС-коды для расширения (и подчистка протухших).</summary>
    private List<object> FreshSmsCodes()
    {
        lock (_smsLock)
        {
            _smsInbox.RemoveAll(x => DateTimeOffset.UtcNow - x.At > SmsTtl);
            return _smsInbox
                .OrderByDescending(x => x.At)
                .Select(x => (object)new
                {
                    code = x.Code,
                    hint = x.Hint,
                    ageSec = (int)(DateTimeOffset.UtcNow - x.At).TotalSeconds,
                })
                .ToList();
        }
    }
}
