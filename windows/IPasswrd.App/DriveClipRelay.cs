using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using IPasswrd.Core;

namespace IPasswrd.App;

// Общий буфер обмена с телефоном ЧЕРЕЗ GOOGLE ДИСК — канал для Android.
//
// С айфоном буфер уже ходит двумя путями (уведомление по Bluetooth и файл в iCloud) — Android
// ни того ни другого не умеет: уведомления не читаются Быстрыми командами, iCloud недоступен.
// Зато у Android-приложения уже есть тот же Google-аккаунт, что и у ПК, — сейф синхронизируется
// через папку IPasswrd на Диске. Буфер ездит через неё же, двумя файлами:
//
//   clip-to-pc.bin   — телефон положил, ПК подобрал, применил и УДАЛИЛ;
//   clip-from-pc.bin — ПК держит там свой последний буфер, телефон забирает по кнопке.
//
// Содержимое — только конверт ClipEnvelope (AES-GCM ключом сейфа): в буфере бывают пароли,
// и открытым текстом в облаке им лежать нельзя ни минуты. Отсюда же правило: канал работает,
// только пока сейф ОТКРЫТ на обоих концах — запертое устройство конверт не прочтёт.
public partial class MainWindow
{
    private CancellationTokenSource? _clipRelayCts;
    private string _clipLastSeen = "";        // последний текст, который мы сами видели в буфере ПК
    private bool _clipSeenPrimed;             // первый тик только запоминает буфер, не отправляет:
                                              // то, что лежало в буфере ДО запуска, никто не «копировал сейчас»

    private const string ClipToPc = "clip-to-pc.bin";
    private const string ClipFromPc = "clip-from-pc.bin";

    /// <summary>Запускается один раз при старте окна и дальше живёт сам: пока сейф заперт или
    /// синхронизация не Google — просто спит.</summary>
    private void StartDriveClipRelay()
    {
        if (!ClipSyncEnabled) return;   // общий буфер ПК↔телефон выключен до после-релизной переработки
        _clipRelayCts?.Cancel();
        var cts = _clipRelayCts = new CancellationTokenSource();
        _ = Task.Run(() => DriveClipLoop(cts.Token));
    }

    private async Task DriveClipLoop(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { return; }
            tick++;

            try
            {
                if (_vault is null || _syncProvider != "google") { _clipSeenPrimed = false; continue; }
                var g = _gdrive;
                if (g is null || !g.IsSignedIn) continue;

                await PushLocalClipboardIfChangedAsync(g, ct);

                // Входящее опрашиваем реже: у Диска есть квоты, а буфер с телефона — событие
                // редкое. Полминуты ожидания честно названы в интерфейсе телефона.
                if (tick % 6 == 0) await PullPhoneClipboardAsync(g, ct);
            }
            catch { /* оффлайн или гонка с Диском — следующий тик попробует снова */ }
        }
    }

    private async Task PushLocalClipboardIfChangedAsync(GoogleDriveSync g, CancellationToken ct)
    {
        string text = "";
        try
        {
            text = await Dispatcher.UIThread.InvokeAsync(
                () => Clipboard?.GetTextAsync() ?? Task.FromResult<string?>(null)) ?? "";
        }
        catch { return; }

        if (!_clipSeenPrimed)
        {
            // Первый взгляд после разблокировки: запомнить и молчать. Иначе бы мы отправляли
            // на телефон то, что человек копировал вчера.
            _clipLastSeen = text;
            _clipSeenPrimed = true;
            return;
        }

        if (text.Length == 0 || text.Length > ClipEnvelope.MaxTextChars) return;
        if (text == _clipLastSeen) return;
        lock (_smsLock)
        {
            // Только что применённый ПРИШЕДШИЙ буфер не должен уехать обратно — иначе эхо
            // ходило бы по кругу между устройствами.
            if (text == _lastApplied) { _clipLastSeen = text; return; }
        }

        var vault = _vault;
        if (vault is null) return;
        byte[] clipKey = vault.ExportSessionKey();
        // Гонка с блокировкой: Wipe() мог стереть ключ, пока мы держим ссылку на сейф. Нулевым
        // ключом печатать нельзя — это шифрование предсказуемым ключом, т.е. почти открытый текст.
        if (System.MemoryExtensions.IndexOfAnyExcept<byte>(clipKey, (byte)0) < 0) return;
        byte[] blob = ClipEnvelope.Seal(clipKey, text);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(clipKey);
        await g.UploadSmallAsync(ClipFromPc, blob, ct);
        _clipLastSeen = text;
        NotifLog($"буфер → Диск: {text.Length} симв.");
    }

    private async Task PullPhoneClipboardAsync(GoogleDriveSync g, CancellationToken ct)
    {
        byte[]? blob = await g.DownloadSmallAsync(ClipToPc, ct);
        if (blob is null || blob.Length == 0) return;

        var vault = _vault;
        if (vault is null) return;
        byte[] clipKey = vault.ExportSessionKey();
        if (System.MemoryExtensions.IndexOfAnyExcept<byte>(clipKey, (byte)0) < 0) return;   // заперли: ключ стёрт — конверт телефона НЕ удаляем
        bool opened = ClipEnvelope.TryOpen(clipKey, blob, out string text);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(clipKey);
        if (!opened)
        {
            // Чужой или битый конверт: применить нечего, но и оставлять его крутиться в опросе
            // незачем — удаляем, телефон при желании положит новый.
            await g.DeleteSmallAsync(ClipToPc, ct);
            return;
        }

        bool applied = await ApplyClipboardAsync(text, "Google Диск (телефон)");
        if (applied)
        {
            _clipLastSeen = text;   // чтобы исходящий вотчер не отправил это же обратно
            await g.DeleteSmallAsync(ClipToPc, ct);
        }
    }
}
