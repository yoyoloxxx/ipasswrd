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
                    v.MergeFrom(bytes);
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

    // ================= сейф =================

    private async void OnChangePassword(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new ChangePasswordPage());

    private void OnLock(object? sender, EventArgs e) => Svc.State.Lock();
}
