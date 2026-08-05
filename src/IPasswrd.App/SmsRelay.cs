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

            _smsRelay = new TcpListener(IPAddress.Any, SmsRelayPort);
            _smsRelay.Start();
            _ = Task.Run(() => SmsAcceptLoop(_bridgeCts!.Token));
            WriteSmsRelayInfo();
        }
        catch { _smsRelay = null; /* порт занят — фича просто выключена */ }
        StartSmsDropFolder();
        StartNotifSmsListener();
    }

    // ---- главный канал: уведомления Windows от «Связи с телефоном» (Phone Link) ----
    // Айфон спарен с ПК по Bluetooth; входящая СМС всплывает уведомлением Windows —
    // ловим его событием + частым опросом (уведомление могут быстро прочитать/смахнуть),
    // вытаскиваем код и кладём в тот же ящик, что и остальные каналы.
    private UserNotificationListener? _notifListener;
    private readonly HashSet<uint> _notifSeen = new();

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
                try
                {
                    var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding is not null)
                        text = string.Join(" ", binding.GetTextElements().Select(t => t.Text));
                }
                catch { }

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
            sb.AppendLine("ОБЩИЙ БУФЕР ОБМЕНА (Быстрые команды, запуск вручную; та же сеть):");
            sb.AppendLine($"  Телефон → ПК: POST http://{Environment.MachineName.ToLowerInvariant()}.local:{SmsRelayPort}/clip?t={_smsToken}   (тело — текст буфера)");
            sb.AppendLine($"  ПК → телефон: GET  http://{Environment.MachineName.ToLowerInvariant()}.local:{SmsRelayPort}/clip?t={_smsToken}   (в ответе JSON, поле text)");
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
            string token = "";
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == "t") token = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }

            bool smsUp  = path == "/sms"  && method == "POST";
            bool clipUp = path == "/clip" && method == "POST";
            bool clipDn = path == "/clip" && method == "GET";
            if (!smsUp && !clipUp && !clipDn) { await Send("404 Not Found", "{\"ok\":false}"); return; }
            if (_smsToken.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_smsToken)))
            { await Send("403 Forbidden", "{\"ok\":false}"); return; }

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
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => Clipboard?.SetTextAsync(raw) ?? Task.CompletedTask);
                }
                catch { await Send("200 OK", "{\"ok\":false}"); return; }
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
