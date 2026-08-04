using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class GeneratorPage : ContentPage
{
    private string _current = "";

    public GeneratorPage()
    {
        InitializeComponent();
        Regenerate();
    }

    private GeneratorOptions Options() => new(
        Length: (int)Math.Round(LengthSlider.Value),
        Lower: LowerSwitch.IsToggled,
        Upper: UpperSwitch.IsToggled,
        Digits: DigitsSwitch.IsToggled,
        Symbols: SymbolsSwitch.IsToggled,
        ExcludeAmbiguous: AmbiguousSwitch.IsToggled);

    private void Regenerate()
    {
        var o = Options();
        string pool = Generator.Pool(o);
        if (pool.Length == 0)
        {
            _current = "";
            PasswordLabel.Text = "выберите хотя бы один набор символов";
            StrengthLabel.Text = "";
            return;
        }

        _current = Generator.Generate(o);
        PasswordLabel.Text = _current;

        double bits = o.Length * Math.Log2(pool.Length);
        string verdict = bits >= 90 ? "отличный" : bits >= 60 ? "надёжный" : bits >= 40 ? "средний" : "слабый";
        StrengthLabel.Text = $"{o.Length} символов · ~{(int)bits} бит · {verdict}";
    }

    private void OnLengthChanged(object? sender, ValueChangedEventArgs e)
    {
        int len = (int)Math.Round(e.NewValue);
        LengthLabel.Text = len.ToString();
        Regenerate();
    }

    private void OnOptionToggled(object? sender, ToggledEventArgs e) => Regenerate();

    private void OnRegenerate(object? sender, EventArgs e) => Regenerate();

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (_current.Length == 0) return;
        await SecureClipboard.CopyAsync(_current);
        await ShowToastAsync("Пароль скопирован");
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
        try { await Task.Delay(1500, cts.Token); } catch (TaskCanceledException) { return; }
        await Toast.FadeTo(0, 250);
        if (!cts.IsCancellationRequested) Toast.IsVisible = false;
    }
}
