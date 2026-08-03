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
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    private void Refresh()
    {
        _initializing = true;

        BiometricLabel.Text = $"Разблокировка {Svc.Biometric.Kind}";
        BiometricSwitch.IsToggled = Svc.State.BiometricUnlockEnabled && Svc.Biometric.IsAvailable;
        BiometricSwitch.IsEnabled = Svc.Biometric.IsAvailable;

        int current = Svc.State.AutolockMinutes;
        int idx = Array.FindIndex(AutolockOptions, o => o.Minutes == current);
        AutolockPicker.SelectedIndex = idx >= 0 ? idx : 1;

        bool connected = Svc.External.IsConnected;
        SyncTitle.Text = connected ? "iCloud Drive" : "Локальный сейф";
        SyncSub.Text = connected
            ? (Svc.State.LastSyncStatus ?? Svc.External.DisplayName ?? "синхронизация включена")
            : "без синхронизации";
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
                        "В iCloud уже лежит другой сейф. Сначала решите, какой оставить: экспортируйте нужный и подключите его на обоих устройствах.", "Ок");
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

    private async void OnSyncNow(object? sender, EventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        await Svc.State.SyncAsync();
        SyncNowButton.IsEnabled = true;
        Refresh();
    }

    private void OnDisconnectSync(object? sender, EventArgs e)
    {
        Svc.External.Disconnect();
        Refresh();
    }

    private async void OnExportCopy(object? sender, EventArgs e)
    {
        var v = Svc.State.Vault;
        if (v is null) return;
        bool ok = await Svc.External.ExportCopyAsync(v.Serialize(), AppState.VaultFileName);
        if (ok)
            await DisplayAlert("Готово",
                "Копия сейфа выгружена. Чтобы Windows видел её, положите файл в iCloud Drive → IPasswrd с именем vault.ipvault.", "Ок");
    }

    // ================= сейф =================

    private async void OnChangePassword(object? sender, EventArgs e) =>
        await Navigation.PushAsync(new ChangePasswordPage());

    private void OnLock(object? sender, EventArgs e) => Svc.State.Lock();
}
