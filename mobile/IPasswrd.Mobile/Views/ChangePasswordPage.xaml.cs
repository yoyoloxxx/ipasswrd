using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class ChangePasswordPage : ContentPage
{
    public ChangePasswordPage()
    {
        InitializeComponent();
        // Индикатор стойкости — та же шкала, что на ПК и в аудите (SecurityAudit.Rate).
        NewBox.TextChanged += (_, _) => StrengthMeter.Show(MeterLabel, NewBox.Text);
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;

        string oldPw = OldBox.Text ?? "";
        string newPw = NewBox.Text ?? "";

        if (newPw.Length < 8) { ShowError("Новый мастер-пароль должен быть не короче 8 символов."); return; }
        if (newPw != (ConfirmBox.Text ?? "")) { ShowError("Пароли не совпадают."); return; }

        SaveButton.IsEnabled = false;
        Busy.IsVisible = Busy.IsRunning = true;

        string? err = await Svc.State.ChangeMasterPasswordAsync(oldPw, newPw);

        Busy.IsVisible = Busy.IsRunning = false;
        SaveButton.IsEnabled = true;

        if (err is not null) { ShowError(err); return; }

        await DisplayAlert("Готово", "Мастер-пароль изменён.", "Ок");
        await Navigation.PopAsync();
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }
}
