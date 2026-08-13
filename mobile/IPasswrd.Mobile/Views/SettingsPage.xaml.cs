using IPasswrd.Core;
using IPasswrd.Core.Import;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    private static readonly (int Minutes, string Label)[] AutolockOptions =
    {
        (1, "1 минута"), (5, "5 минут"), (15, "15 минут"), (60, "1 час"), (0, "Никогда"),
    };

    private bool _initializing;

    public SettingsPage()
    {
        InitializeComponent();
        AutolockPicker.ItemsSource = AutolockOptions.Select(o => o.Label).ToList();
        ClipboardPicker.ItemsSource = SecureClipboard.Options.Select(o => o.Label).ToList();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    private void Refresh()
    {
        _initializing = true;

        BiometricLabel.Text = $"Разблокировка: {Svc.Biometric.Kind}";
        AccessHint.Text = $"После простоя сейф закрывается; {Svc.Biometric.Kind} открывает его без мастер-пароля. Мастер-пароль переспрашивается раз в 30 дней.";
        AboutTitle.Text = DeviceInfo.Platform == DevicePlatform.iOS ? "IPasswrd для iPhone" : "IPasswrd для Android";
        BiometricSwitch.IsToggled = Svc.State.BiometricUnlockEnabled && Svc.Biometric.IsAvailable;
        BiometricSwitch.IsEnabled = Svc.Biometric.IsAvailable;

        int current = Svc.State.AutolockMinutes;
        int idx = Array.FindIndex(AutolockOptions, o => o.Minutes == current);
        AutolockPicker.SelectedIndex = idx >= 0 ? idx : 1;

        int clip = Svc.State.ClipboardClearSeconds;
        int ci = Array.FindIndex(SecureClipboard.Options, o => o.Secs == clip);
        ClipboardPicker.SelectedIndex = ci >= 0 ? ci : 2;

        bool google = IPasswrd.Mobile.Services.GoogleDrive.IsConnected;
        bool icloud = Svc.External.IsConnected;
        bool connected = google || icloud;
        bool iosDevice = DeviceInfo.Platform == DevicePlatform.iOS;
        SyncTitle.Text = google ? "Google Диск"
            : icloud ? (iosDevice ? "iCloud Drive" : (Svc.External.DisplayName ?? "Внешний файл"))
            : "Локальный сейф";
        SyncConnectButton.Text = iosDevice ? "Выбрать файл сейфа в iCloud…" : "Выбрать файл сейфа…";
        SyncHint.Text = iosDevice
            ? "Вход через Google — тот же Google Диск, что на Windows. Либо выберите файл в iCloud Drive. Изменения объединяются поэлементно."
            : "Вход через Google — тот же Google Диск, что на Windows (папка IPasswrd, файл vault.ipvault). Изменения объединяются поэлементно.";
        SyncSub.Text = google
            ? (Svc.State.LastSyncStatus ?? IPasswrd.Mobile.Services.GoogleDrive.Email ?? "вход выполнен")
            : icloud
                ? (Svc.State.LastSyncStatus ?? Svc.External.DisplayName ?? "синхронизация включена")
                : "без синхронизации";
        SyncGoogleButton.IsVisible = !connected && IPasswrd.Mobile.Services.GoogleDrive.IsConfigured;
        SyncConnectButton.IsVisible = !connected;
        SyncNowButton.IsVisible = connected;
        SyncDisconnectButton.IsVisible = connected;

        RefreshA11y();
        RefreshRecovery();

        VersionLabel.Text = $"Версия {AppInfo.Current.VersionString} · формат сейфа v1";

        _initializing = false;
    }

    private void OnBiometricToggled(object? sender, ToggledEventArgs e)
    {
        if (_initializing) return;
        Svc.State.BiometricUnlockEnabled = e.Value;
    }

    private void OnAutolockChanged(object? sender, EventArgs e)
    {
        if (_initializing || AutolockPicker.SelectedIndex < 0) return;
        Svc.State.AutolockMinutes = AutolockOptions[AutolockPicker.SelectedIndex].Minutes;
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        if (_initializing || ClipboardPicker.SelectedIndex < 0) return;
        Svc.State.ClipboardClearSeconds = SecureClipboard.Options[ClipboardPicker.SelectedIndex].Secs;
    }

    // ================= импорт =================

    private async void OnImport(object? sender, EventArgs e)
    {
        try
        {
            FileResult? file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Выберите файл экспорта" });
            if (file is null) return;

            string content;
            using (var stream = await file.OpenReadAsync())
            using (var reader = new StreamReader(stream))
                content = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                await DisplayAlert("Импорт", "Файл пустой.", "Ок");
                return;
            }

            (int added, int parsed) = await Svc.State.ImportAsync(content);
            if (parsed == 0)
                await DisplayAlert("Импорт", "Не удалось распознать записи в файле. Поддерживаются CSV (Chrome/Bitwarden/…) и текстовый экспорт Kaspersky.", "Ок");
            else if (added == 0)
                await DisplayAlert("Импорт", $"Найдено записей: {parsed}. Все они уже есть в сейфе — ничего не добавлено.", "Ок");
            else
                await DisplayAlert("Импорт", $"Добавлено записей: {added} (из {parsed}). Дубликаты пропущены.", "Готово");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Импорт", "Не удалось прочитать файл: " + ex.Message, "Ок");
        }
    }

    // ================= синхронизация =================

    private async void OnConnectSync(object? sender, EventArgs e)
    {
        byte[]? bytes = await Svc.External.PickAndConnectAsync();
        if (bytes is null) return;

        if (bytes.Length > 0)
        {
            // слить содержимое выбранного файла с текущим сейфом
            var v = Svc.State.Vault;
            if (v is not null)
            {
                try
                {
                    Svc.State.MergeExternal(bytes);
                    await Svc.State.SaveAsync();
                }
                catch (IPasswrd.Core.VaultIntegrityException)
                {
                    Svc.External.Disconnect();
                    await DisplayAlert("Это другой сейф",
                        "В выбранном файле — другой сейф. Сначала решите, какой оставить: экспортируйте нужный и подключите его на обоих устройствах.", "Ок");
                    Refresh();
                    return;
                }
                catch (Exception)
                {
                    Svc.External.Disconnect();
                    await DisplayAlert("Не получилось", "Выбранный файл не похож на сейф IPasswrd.", "Ок");
                    Refresh();
                    return;
                }
            }
        }
        else
        {
            // пустой файл — просто зальём туда наш сейф
            await Svc.State.SyncAsync();
        }

        Refresh();
    }

    private async void OnConnectGoogle(object? sender, EventArgs e)
    {
        SyncGoogleButton.IsEnabled = false;
        try
        {
            string? email = await Svc.State.ConnectGoogleAsync();
            await DisplayAlert("Google подключён",
                (email is { Length: > 0 } ? email + "\n\n" : "") +
                "Сейф связан с Google Drive — тот же, что на Windows. Изменения синхронизируются автоматически.", "Готово");
        }
        catch (IPasswrd.Core.VaultIntegrityException)
        {
            Svc.State.DisconnectGoogle();
            await DisplayAlert("Это другой сейф",
                "В Google Drive уже лежит другой сейф. Сначала решите, какой оставить: экспортируйте нужный и подключите его на обоих устройствах.", "Ок");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[IPW] google connect failed: " + ex);
            Svc.State.DisconnectGoogle();
            string msg = ex.Message.Contains("client_not_configured")
                ? "Google-вход ещё не настроен в этой сборке."
                : ex.Message.Contains("no_code") || ex.Message.Contains("consent")
                    ? "Вход отменён."
                    : "Не удалось войти в Google. Проверьте соединение и попробуйте снова.";
            await DisplayAlert("Google", msg, "Ок");
        }
        finally { SyncGoogleButton.IsEnabled = true; Refresh(); }
    }

    private async void OnSyncNow(object? sender, EventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        await Svc.State.SyncAsync();
        SyncNowButton.IsEnabled = true;
        Refresh();
    }

    private void OnDisconnectSync(object? sender, EventArgs e)
    {
        if (IPasswrd.Mobile.Services.GoogleDrive.IsConnected) Svc.State.DisconnectGoogle();
        else Svc.External.Disconnect();
        Refresh();
    }

    private async void OnExportCopy(object? sender, EventArgs e)
    {
        var v = Svc.State.Vault;
        if (v is null) return;
        bool ok = await Svc.External.ExportCopyAsync(v.Serialize(), AppState.VaultFileName);
        if (ok)
            await DisplayAlert("Готово",
                DeviceInfo.Platform == DevicePlatform.iOS
                    ? "Копия сейфа выгружена. Чтобы Windows видел её, положите файл в iCloud Drive → IPasswrd с именем vault.ipvault."
                    : "Копия сейфа выгружена. Это просто запасная копия: для синхронизации с Windows используйте вход через Google.", "Ок");
    }

    /// <summary>
    /// CSV — открытый текст со всеми паролями, поэтому сначала прямой вопрос, как на ПК.
    /// Временный файл стирается сразу после передачи: лежать в песочнице приложения в открытом
    /// виде ему незачем ни минуты дольше, чем нужно системному листу «Поделиться».
    /// </summary>
    private async void OnExportCsv(object? sender, EventArgs e)
    {
        var v = Svc.State.Vault;
        if (v is null) return;

        bool sure = await DisplayAlert("Выгрузить CSV?",
            "В файле будут все пароли открытым текстом — без шифрования. Он нужен только для переезда в другой менеджер. После переноса удалите его там, куда сохранили.",
            "Выгрузить", "Отмена");
        if (!sure) return;

        string path = Path.Combine(FileSystem.CacheDirectory, "ipasswrd-export.csv");
        try
        {
            File.WriteAllText(path, Exporter.ToCsv(v.Items()), System.Text.Encoding.UTF8);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Экспорт IPasswrd",
                File = new ShareFile(path),
            });
        }
        catch (Exception)
        {
            await DisplayAlert("Не получилось", "Файл не выгрузился.", "Ок");
        }
        finally
        {
            try { File.Delete(path); } catch { /* лучшее усилие */ }
        }
    }

    /// <summary>
    /// Восстановление из локальной копии — те же копии, что делает ПК: снимаются перед каждой
    /// перезаписью сейфа, включая подмену файла синхронизацией. После восстановления сейф
    /// запирается — открыть его должен мастер-пароль, а не наша догадка о содержимом.
    /// </summary>
    private async void OnRestoreBackup(object? sender, EventArgs e)
    {
        var backups = VaultBackups.List(Svc.State.LocalVaultPath);
        if (backups.Count == 0)
        {
            await DisplayAlert("Резервных копий пока нет",
                "Они появляются сами при первом изменении сейфа — включать ничего не нужно.", "Ок");
            return;
        }

        string[] options = backups
            .Select(b => b.TakenUtc.ToLocalTime().ToString("dd.MM HH:mm") + " · " + Attachments.HumanSize((int)Math.Min(b.Bytes, int.MaxValue)))
            .ToArray();
        string choice = await DisplayActionSheet("Какую копию вернуть?", "Отмена", null, options);
        int idx = Array.IndexOf(options, choice);
        if (idx < 0) return;

        bool sure = await DisplayAlert("Восстановить?",
            $"Сейф вернётся к состоянию от {options[idx]}. Сегодняшнее состояние тоже сохранится копией. Если включена синхронизация, она может привезти свежие изменения обратно.",
            "Восстановить", "Отмена");
        if (!sure) return;

        try
        {
            VaultBackups.Restore(Svc.State.LocalVaultPath, backups[idx].Path);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Не получилось", ex.Message, "Ок");
            return;
        }

        Svc.State.Lock();
        await DisplayAlert("Готово", "Сейф восстановлен из копии — введите мастер-пароль.", "Ок");
    }

    // ================= сейф =================

    private async void OnChangePassword(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new ChangePasswordPage());

    // ================= автозаполнение в браузерах (спец-возможности) =================

    private void RefreshA11y()
    {
#if ANDROID
        A11ySection.IsVisible = true;
        bool on = IPasswrd.Mobile.Platforms.Android.AutoFill.IpwAccessibilityService.IsRunning;
        A11yStatus.Text = on
            ? "Включено. В Яндекс.Браузере тапните поле логина — появится кнопка IPasswrd."
            : "Выключено. Нужно один раз включить IPasswrd в спец-возможностях.";
        A11yButton.Text = on ? "Открыть настройки спец-возможностей" : "Включить";
#else
        A11ySection.IsVisible = false;
#endif
    }

    private void OnOpenAccessibility(object? sender, EventArgs e)
    {
#if ANDROID
        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception) { }
#endif
    }

    // ================= код восстановления =================

    private void RefreshRecovery()
    {
        bool has = Svc.State.HasRecoveryCode;
        RecoveryDeleteButton.IsVisible = has;
        RecoveryCreateButton.Text = has ? "Пересоздать код" : "Создать код восстановления";

        if (has)
        {
            string when = "";
            string? iso = Svc.State.RecoveryIssuedAt;
            if (!string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, out var dt))
                when = " от " + dt.ToLocalTime().ToString("dd.MM.yyyy");
            RecoveryStatus.Text = "Код создан" + when + ". Храните бумажку в надёжном месте.";
        }
        else
        {
            RecoveryStatus.Text = "Код не создан. Без него забытый мастер-пароль означает потерю сейфа.";
        }
    }

    private async void OnCreateRecovery(object? sender, EventArgs e)
    {
        if (Svc.State.HasRecoveryCode)
        {
            bool go = await DisplayAlert("Пересоздать код?",
                "Прежний код перестанет открывать сейф. Новый код нужно будет записать заново.", "Пересоздать", "Отмена");
            if (!go) return;
        }

        string? code = await Svc.State.EnableRecoveryAsync();
        if (string.IsNullOrEmpty(code)) { await DisplayAlert("Не получилось", "Сейф закрыт.", "Ок"); return; }

        RefreshRecovery();
        await ShowRecoveryCodeAsync(code);
    }

    private async Task ShowRecoveryCodeAsync(string code)
    {
        string pretty = IPasswrd.Core.RecoveryCode.Format(IPasswrd.Core.RecoveryCode.Normalize(code) ?? code);
        while (true)
        {
            string action = await DisplayActionSheet(
                "Ваш код восстановления:\n\n" + pretty,
                "Я записал(а)", null, "Скопировать", "Сохранить в файл…");
            if (action == "Скопировать")
            {
                await IPasswrd.Mobile.Services.SecureClipboard.CopyAsync(pretty);
                await ShowToastAsync("Код скопирован — вставьте в надёжное место");
            }
            else if (action == "Сохранить в файл…")
            {
                await SaveRecoveryFileAsync(pretty);
            }
            else return;   // «Я записал(а)» или закрытие
        }
    }

    private async Task SaveRecoveryFileAsync(string pretty)
    {
        string text =
            "IPasswrd — памятка восстановления\r\n\r\n" +
            "Код восстановления:\r\n" + pretty + "\r\n\r\n" +
            "Если мастер-пароль забыт: на экране входа нажмите «Забыли мастер-пароль?»,\r\n" +
            "введите этот код и придумайте новый мастер-пароль.\r\n\r\n" +
            "Храните эту памятку отдельно от устройства. Кто знает код и имеет файл сейфа — откроет сейф.\r\n";
        try
        {
            bool ok = await Svc.External.ExportCopyAsync(
                System.Text.Encoding.UTF8.GetBytes(text), "IPasswrd-памятка-восстановления.txt");
            if (ok) await ShowToastAsync("Памятка сохранена");
        }
        catch (Exception) { await DisplayAlert("Не получилось", "Не удалось сохранить файл.", "Ок"); }
    }

    private async void OnDeleteRecovery(object? sender, EventArgs e)
    {
        bool go = await DisplayAlert("Удалить код восстановления?",
            "Записанная бумажка перестанет открывать сейф. Восстановить доступ можно будет только мастер-паролем.", "Удалить", "Отмена");
        if (!go) return;
        await Svc.State.DisableRecoveryAsync();
        RefreshRecovery();
        await ShowToastAsync("Код удалён");
    }

    private CancellationTokenSource? _toastCts;

    private async Task ShowToastAsync(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastLabel.Text = text;
        Toast.IsVisible = true;
        Toast.Opacity = 0;
        await Toast.FadeTo(1, 120);
        try { await Task.Delay(1600, cts.Token); } catch (TaskCanceledException) { return; }
        await Toast.FadeTo(0, 250);
        if (!cts.IsCancellationRequested) Toast.IsVisible = false;
    }

    private void OnLock(object? sender, EventArgs e) => Svc.State.Lock();
}
