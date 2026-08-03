using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class UnlockPage : ContentPage
{
    private bool _creating;
    private bool _biometricTried;

    public UnlockPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();

        // Face ID сразу при входе, один раз за показ экрана (как ожидаешь от телефона)
        if (!_creating && !_biometricTried && Svc.State.QuickUnlockAvailable && Svc.State.LockoutRemaining == TimeSpan.Zero)
        {
            _biometricTried = true;
            Dispatcher.Dispatch(async () => await QuickUnlockAsync());
        }
    }

    private void Refresh()
    {
        _creating = !Svc.State.HasLocalVault && !Svc.External.IsConnected;

        Subtitle.Text = _creating ? "Создайте сейф и мастер-пароль" : "Сейф закрыт";
        ConfirmCard.IsVisible = _creating;
        ConnectButton.IsVisible = _creating;
        PrimaryButton.Text = _creating ? "Создать сейф" : "Открыть";

        BiometricButton.Text = Svc.Biometric.Kind;
        BiometricButton.IsVisible = !_creating && Svc.State.QuickUnlockAvailable;

        StorageHint.Text = Svc.External.IsConnected
            ? $"Сейф: {Svc.External.DisplayName ?? "iCloud Drive"} (общий с Windows)"
            : _creating ? "Файл сейфа будет создан на этом iPhone" : "Локальная копия на этом iPhone";

        TimeSpan left = Svc.State.LockoutRemaining;
        if (left > TimeSpan.Zero)
            ShowError($"Слишком много попыток. Подождите {Fmt.Duration(left)}.");
    }

    private void ShowError(string? text)
    {
        ErrorLabel.Text = text ?? "";
        ErrorLabel.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        Busy.IsVisible = Busy.IsRunning = busy;
        BusyHint.IsVisible = busy;
        PrimaryButton.IsEnabled = !busy;
        BiometricButton.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmBox.IsEnabled = !busy;
    }

    private async void OnPrimaryAction(object? sender, EventArgs e)
    {
        ShowError(null);
        string pw = PasswordBox.Text ?? "";

        if (_creating)
        {
            if (pw.Length < 8) { ShowError("Мастер-пароль должен быть не короче 8 символов."); return; }
            if (pw != (ConfirmBox.Text ?? "")) { ShowError("Пароли не совпадают."); return; }

            SetBusy(true);
            try { await Svc.State.CreateAsync(pw); }
            catch (Exception) { ShowError("Не удалось создать сейф."); }
            finally { SetBusy(false); }
            return;
        }

        if (pw.Length == 0) { ShowError("Введите мастер-пароль."); return; }

        SetBusy(true);
        string? err = await Svc.State.UnlockAsync(pw);
        SetBusy(false);
        if (err is not null) ShowError(err);
        else PasswordBox.Text = "";
    }

    private async void OnBiometric(object? sender, EventArgs e) => await QuickUnlockAsync();

    private async Task QuickUnlockAsync()
    {
        ShowError(null);
        SetBusy(true);
        string? err = await Svc.State.TryQuickUnlockAsync();
        SetBusy(false);
        if (!string.IsNullOrEmpty(err)) ShowError(err);
    }

    private async void OnConnectExisting(object? sender, EventArgs e)
    {
        ShowError(null);
        byte[]? bytes = await Svc.External.PickAndConnectAsync();
        if (bytes is null) return;   // отмена
        if (bytes.Length == 0)
        {
            Svc.External.Disconnect();
            ShowError("Выбранный файл пуст — это не сейф IPasswrd.");
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Svc.State.LocalVaultPath)!);
            File.WriteAllBytes(Svc.State.LocalVaultPath, bytes);
        }
        catch (Exception)
        {
            ShowError("Не удалось сохранить локальную копию сейфа.");
            return;
        }
        Refresh();
        ShowError(null);
        Subtitle.Text = "Сейф подключён — введите мастер-пароль";
    }
}
