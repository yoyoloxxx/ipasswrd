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

        bool ios = DeviceInfo.Platform == DevicePlatform.iOS;
        string device = ios ? "этом iPhone" : "этом телефоне";

        Subtitle.Text = _creating ? "Создайте сейф и мастер-пароль" : "Сейф закрыт";
        ConfirmCard.IsVisible = _creating;
        // Пути подключения готового сейфа: Google Диск (главный на Android — iCloud там нет)
        // и файл через системный выбор (на iPhone это iCloud Drive, на Android — любой провайдер).
        GoogleButton.IsVisible = _creating && GoogleDrive.IsConfigured;
        ConnectButton.IsVisible = _creating;
        ConnectButton.Text = ios
            ? "У меня уже есть сейф — подключить файл из iCloud"
            : "Или подключить файл сейфа вручную";
        PrimaryButton.Text = _creating ? "Создать сейф" : "Открыть";

        BiometricButton.Text = Svc.Biometric.Kind;
        BiometricButton.IsVisible = !_creating && Svc.State.QuickUnlockAvailable;

        StorageHint.Text = GoogleDrive.IsConnected
            ? $"Сейф: Google Диск{(GoogleDrive.Email is { Length: > 0 } em ? $" ({em})" : "")} — общий с Windows"
            : Svc.External.IsConnected
                ? $"Сейф: {Svc.External.DisplayName ?? (ios ? "iCloud Drive" : "внешний файл")} (общий с Windows)"
                : _creating ? $"Файл сейфа будет создан на {device}" : $"Локальная копия на {device}";

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

    /// <summary>Первый запуск: войти в Google и забрать сейф из папки IPasswrd на Google Диске —
    /// тот же vault.ipvault, который туда кладёт Windows-приложение.</summary>
    private async void OnConnectGoogle(object? sender, EventArgs e)
    {
        ShowError(null);
        SetBusy(true);
        try
        {
            string? email = await GoogleDrive.SignInAsync();
            byte[]? remote = await GoogleDrive.PullAsync();
            if (remote is { Length: > 0 })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Svc.State.LocalVaultPath)!);
                File.WriteAllBytes(Svc.State.LocalVaultPath, remote);
                Svc.External.Disconnect();   // Google — единственный канал синхронизации
                Refresh();
                Subtitle.Text = "Сейф с Google Диска — введите мастер-пароль";
            }
            else
            {
                // Вход удался, но сейфа на Диске нет. Остаёмся подключёнными:
                // созданный сейчас сейф сам уедет на Диск при первом сохранении.
                Refresh();
                ShowError($"На Google Диске ({email ?? "этот аккаунт"}) сейфа ещё нет. " +
                          "Создайте новый — он сам загрузится на Диск. " +
                          "Если сейф уже есть на ПК, включите там: Настройки → Синхронизация → Google Диск.");
            }
        }
        catch (Exception)
        {
            ShowError("Вход в Google не удался или был отменён. Попробуйте ещё раз.");
        }
        finally { SetBusy(false); }
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
