using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class ItemDetailPage : ContentPage
{
    private readonly string _id;
    private VaultItem? _item;
    private IDispatcherTimer? _timer;
    private Label? _totpCode;
    private Label? _totpSeconds;
    private ProgressBar? _totpBar;
    private TotpConfig? _totp;

    public ItemDetailPage(string id)
    {
        InitializeComponent();
        _id = id;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Load();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private void Load()
    {
        Vault? v = Svc.State.Vault;
        if (v is null) return;
        try { _item = v.Get(_id); }
        catch (Exception) { Navigation.PopAsync(); return; }

        Title = _item.Title.Length > 0 ? _item.Title : "(без названия)";
        FavItem.Text = _item.Favorite ? "★" : "☆";
        Rows.Children.Clear();
        _timer?.Stop(); _timer = null; _totp = null;

        switch (_item.Type)
        {
            case "account":
                AddCopyRow("Сайт", _item.Fields.GetValueOrDefault("url", ""));
                AddCopyRow("Логин", _item.Fields.GetValueOrDefault("username", ""));
                AddCopyRow("Пароль", _item.Fields.GetValueOrDefault("password", ""), secret: true);
                AddTotpBlock(_item.Fields.GetValueOrDefault("totp", ""));
                AddExtraFields("url", "username", "password", "totp");
                break;
            case "card":
                string number = _item.Fields.GetValueOrDefault("number", "");
                AddCopyRow(("Номер " + Fmt.CardBrand(number)).Trim(), Fmt.GroupDigits(number), copyValue: number, secret: true);
                AddCopyRow("Срок", _item.Fields.GetValueOrDefault("expiry", ""));
                AddCopyRow("CVC/CVV", _item.Fields.GetValueOrDefault("cvc", ""), secret: true);
                AddCopyRow("Держатель", _item.Fields.GetValueOrDefault("holder", ""));
                AddExtraFields("number", "expiry", "cvc", "holder");
                break;
            case "document":
                AddCopyRow("Номер", _item.Fields.GetValueOrDefault("number", ""));
                AddCopyRow("Выдан", _item.Fields.GetValueOrDefault("issued", ""));
                AddExtraFields("number", "issued");
                break;
            case "passkey":
                AddCopyRow("Сайт", _item.Fields.GetValueOrDefault("url", ""));
                AddCopyRow("Логин", _item.Fields.GetValueOrDefault("username", ""));
                AddInfo("Ключ доступа привязан к устройству, где был создан. Вход по нему на iPhone появится позже.");
                AddExtraFields("url", "username");
                break;
            default:
                AddExtraFields();
                break;
        }

        if (!string.IsNullOrWhiteSpace(_item.Notes))
        {
            AddSection("Заметки");
            var border = new Border { Style = CardStyle() };
            border.Content = new Label { Text = _item.Notes, FontSize = 15 };
            Rows.Children.Add(border);
        }

        var del = new Button { Text = "Удалить", Style = (Style)Application.Current!.Resources["Danger"], Margin = new Thickness(0, 24, 0, 0) };
        del.Clicked += OnDelete;
        Rows.Children.Add(del);
    }

    private static Style CardStyle() => (Style)Application.Current!.Resources["Card"];
    private static Style MutedStyle() => (Style)Application.Current!.Resources["Muted"];

    private void AddSection(string text) =>
        Rows.Children.Add(new Label { Text = text, Style = (Style)Application.Current!.Resources["Section"] });

    private void AddInfo(string text) =>
        Rows.Children.Add(new Label { Text = text, Style = MutedStyle(), FontSize = 13, Margin = new Thickness(4, 2) });

    private void AddExtraFields(params string[] known)
    {
        if (_item is null) return;
        foreach (var kv in _item.Fields)
        {
            if (known.Contains(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            AddCopyRow(kv.Key, kv.Value);
        }
    }

    private void AddCopyRow(string label, string value, bool secret = false, string? copyValue = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var valueLabel = new Label
        {
            Text = secret ? Mask(value) : value,
            FontSize = 16,
            FontFamily = secret ? "Menlo" : null,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var caption = new Label { Text = label, Style = MutedStyle(), FontSize = 12 };

        var stack = new VerticalStackLayout { Spacing = 1 };
        stack.Children.Add(caption);
        stack.Children.Add(valueLabel);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.Add(stack, 0, 0);

        bool revealed = false;
        if (secret)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var eye = new Button { Text = "👁", FontSize = 15, Padding = new Thickness(8, 4), BackgroundColor = Colors.Transparent };
            eye.Clicked += (_, _) =>
            {
                revealed = !revealed;
                valueLabel.Text = revealed ? value : Mask(value);
            };
            grid.Add(eye, 1, 0);
        }

        var border = new Border { Style = CardStyle(), Content = grid };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await CopyAsync(copyValue ?? value, label);
        border.GestureRecognizers.Add(tap);
        Rows.Children.Add(border);
    }

    private static string Mask(string value) => new('•', Math.Min(Math.Max(value.Length, 6), 14));

    // ================= код проверки (TOTP) =================

    private void AddTotpBlock(string secretOrUri)
    {
        if (string.IsNullOrWhiteSpace(secretOrUri)) return;
        try { _totp = Totp.Parse(secretOrUri); }
        catch (Exception) { return; }

        _totpCode = new Label { FontSize = 30, FontFamily = "Menlo", FontAttributes = FontAttributes.Bold };
        _totpSeconds = new Label { Style = MutedStyle(), FontSize = 13, HorizontalOptions = LayoutOptions.End };
        _totpBar = new ProgressBar();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        head.Add(new Label { Text = "Код проверки", Style = MutedStyle(), FontSize = 12 }, 0, 0);
        head.Add(_totpSeconds, 1, 0);

        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Children.Add(head);
        stack.Children.Add(_totpCode);
        stack.Children.Add(_totpBar);

        var border = new Border { Style = CardStyle(), Content = stack };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_totp is not null)
                await CopyAsync(Totp.GenerateFrom(secretOrUri, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "Код проверки");
        };
        border.GestureRecognizers.Add(tap);
        Rows.Children.Add(border);

        TickTotp();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => TickTotp();
        _timer.Start();
    }

    private void TickTotp()
    {
        if (_totp is null || _totpCode is null) return;
        try
        {
            string code = _totp.Now();
            int left = _totp.SecondsRemaining();
            _totpCode.Text = Fmt.SplitCode(code);
            if (_totpSeconds is not null) _totpSeconds.Text = left + " с";
            if (_totpBar is not null) _totpBar.Progress = (double)left / Math.Max(1, _totp.Period);
        }
        catch (Exception)
        {
            _totpCode.Text = "——— ———";
        }
    }

    // ================= действия =================

    private async Task CopyAsync(string value, string what)
    {
        await Clipboard.Default.SetTextAsync(value);
        await ShowToastAsync($"{what}: скопировано");
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

    private async void OnEdit(object? sender, EventArgs e)
    {
        if (_item is not null)
            await Navigation.PushAsync(new ItemEditPage(_id, _item.Type));
    }

    private async void OnToggleFavorite(object? sender, EventArgs e)
    {
        Vault? v = Svc.State.Vault;
        if (v is null || _item is null) return;
        _item.Favorite = !_item.Favorite;
        try { v.Update(_id, _item); } catch (Exception) { return; }
        FavItem.Text = _item.Favorite ? "★" : "☆";
        await Svc.State.SaveAsync();
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        Vault? v = Svc.State.Vault;
        if (v is null) return;
        bool ok = await DisplayAlert("Удалить запись?", Title, "Удалить", "Отмена");
        if (!ok) return;
        try { v.Delete(_id); } catch (Exception) { }
        await Svc.State.SaveAsync();
        await Navigation.PopAsync();
    }
}
