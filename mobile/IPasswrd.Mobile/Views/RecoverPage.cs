using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

/// <summary>Восстановление доступа кодом восстановления, когда мастер-пароль забыт:
/// вводится код + новый мастер-пароль. Аналог «Забыли мастер-пароль?» на Windows.</summary>
public sealed class RecoverPage : ContentPage
{
    private readonly Entry _code = new() { Placeholder = "Код восстановления (XXXXX-XXXXX-…)" };
    private readonly Entry _new = new() { Placeholder = "Новый мастер-пароль", IsPassword = true };
    private readonly Entry _confirm = new() { Placeholder = "Новый мастер-пароль ещё раз", IsPassword = true };
    private readonly Label _error = new() { IsVisible = false, FontSize = 14 };
    private readonly Button _save;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, IsRunning = false, HorizontalOptions = LayoutOptions.Center };

    public RecoverPage()
    {
        Title = "Восстановление доступа";

        if (Application.Current?.Resources.TryGetValue("IpBad", out var bad) == true && bad is Color badColor)
            _error.TextColor = badColor;

        _save = new Button { Text = "Восстановить доступ" };
        if (Application.Current?.Resources.TryGetValue("Primary", out var st) == true && st is Style primary)
            _save.Style = primary;
        _save.Clicked += OnSave;

        foreach (Entry e in new[] { _code, _new, _confirm })
        {
            e.ReturnType = ReturnType.Next;
        }

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 18),
                Spacing = 12,
                Children =
                {
                    new Label
                    {
                        Text = "Введите код восстановления и придумайте новый мастер-пароль. " +
                               "Старый мастер-пароль больше не понадобится.",
                        FontSize = 14, Opacity = 0.75,
                    },
                    Card(_code),
                    Card(_new),
                    Card(_confirm),
                    _error,
                    _save,
                    _busy,
                },
            },
        };
    }

    private static View Card(View inner)
    {
        var border = new Border { Padding = new Thickness(16, 6), StrokeThickness = 0, Content = inner };
        if (Application.Current?.Resources.TryGetValue("Card", out var st) == true && st is Style card)
            border.Style = card;
        return border;
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        _error.IsVisible = false;
        string code = (_code.Text ?? "").Trim();
        string pw = _new.Text ?? "";

        if (code.Length == 0) { ShowError("Введите код восстановления."); return; }
        if (pw.Length < 8) { ShowError("Новый мастер-пароль должен быть не короче 8 символов."); return; }
        if (pw != (_confirm.Text ?? "")) { ShowError("Пароли не совпадают."); return; }

        _save.IsEnabled = false;
        _busy.IsVisible = _busy.IsRunning = true;

        string? err = await Svc.State.RecoverWithCodeAsync(code, pw);

        _busy.IsVisible = _busy.IsRunning = false;
        _save.IsEnabled = true;

        if (err is not null) { ShowError(err); return; }

        // Успех: сейф уже открыт с новым паролем — состояние само переключит корень на AppShell.
        await DisplayAlert("Доступ восстановлен",
            "Мастер-пароль заменён на новый. Код восстановления продолжает действовать — " +
            "если бумажка могла попасть в чужие руки, пересоздайте код в Настройках.", "Ок");
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }
}
