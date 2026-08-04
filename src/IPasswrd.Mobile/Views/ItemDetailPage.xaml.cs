using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class ItemDetailPage : ContentPage
{
    private readonly List<string> _ids;
    private int _index;
    private string _id;
    private VaultItem? _item;
    private IDispatcherTimer? _timer;
    private readonly List<Action> _totpTicks = new();

    public ItemDetailPage(string id) : this(new List<string> { id }, 0) { }

    /// <summary>Карточка сайта: ids — все записи этого сайта по порядку, startIndex — с какой начать.
    /// Когда записей несколько, работают стрелки ◀ ▶ и свайпы влево/вправо.</summary>
    public ItemDetailPage(IEnumerable<string> ids, int startIndex)
    {
        InitializeComponent();
        _ids = ids.ToList();
        if (_ids.Count == 0) _ids.Add("");
        _index = Math.Clamp(startIndex, 0, _ids.Count - 1);
        _id = _ids[_index];

        // Свайп влево/вправо переключает аккаунты (в дополнение к стрелкам).
        AttachSwitchSwipes(Root);
        AttachSwitchSwipes(Rows);
    }

    private void AttachSwitchSwipes(View target)
    {
        var left = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
        left.Swiped += (_, _) => Switch(_index + 1);
        var right = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
        right.Swiped += (_, _) => Switch(_index - 1);
        target.GestureRecognizers.Add(left);
        target.GestureRecognizers.Add(right);
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
        catch (Exception)
        {
            // Запись могла исчезнуть (удалена на другом устройстве) — покажем следующую или выйдем.
            _ids.Remove(_id);
            if (_ids.Count == 0) { Navigation.PopAsync(); return; }
            _index = Math.Min(_index, _ids.Count - 1);
            _id = _ids[_index];
            try { _item = v.Get(_id); }
            catch (Exception) { Navigation.PopAsync(); return; }
        }

        Title = _item.Title.Length > 0 ? _item.Title : "(без названия)";
        FavItem.Text = _item.Favorite ? "★" : "☆";

        SwitchBar.IsVisible = _ids.Count > 1;
        if (_ids.Count > 1) SwitchLabel.Text = $"{_index + 1} из {_ids.Count}";

        Rows.Children.Clear();
        _timer?.Stop(); _timer = null; _totpTicks.Clear();

        switch (_item.Type)
        {
            case "account":
                AddCopyRow("Сайт", _item.Fields.GetValueOrDefault("url", ""));
                AddCopyRow("Логин", _item.Fields.GetValueOrDefault("username", ""));
                AddCopyRow("Пароль", _item.Fields.GetValueOrDefault("password", ""), secret: true);
                string ownTotp = _item.Fields.GetValueOrDefault("totp", "").Trim();
                AddTotpBlock(ownTotp);
                // Отдельные записи из «Кодов», подходящие этому сайту (google.com ↔ «google»).
                // Они общие для всех аккаунтов сайта, поэтому предлагаем привязать к конкретному.
                foreach (VaultEntry t in MatchedTotps(v, _item, ownTotp))
                {
                    string raw = t.Item.Fields.GetValueOrDefault("totp", "");
                    var (_, acc) = TotpMeta.IssuerAccount(raw);
                    AddTotpBlock(raw, acc.Length > 0 ? acc : t.Item.Title, (t.Id, raw, t.Item.Title));
                }
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

    /// <summary>Отдельные totp-записи, подходящие сайту аккаунта (кроме дубля собственного секрета).</summary>
    private static List<VaultEntry> MatchedTotps(Vault v, VaultItem account, string ownTotp)
    {
        var res = new List<VaultEntry>();
        string key = SiteGroups.KeyFor(account);
        try
        {
            foreach (VaultEntry t in v.Items())
            {
                if (t.Item.Type != "totp") continue;
                string raw = t.Item.Fields.GetValueOrDefault("totp", "").Trim();
                if (raw.Length == 0 || raw == ownTotp) continue;
                if (TotpMeta.MatchesSite(t.Item, key)) res.Add(t);
            }
        }
        catch (Exception) { }
        return res.OrderBy(t => t.Item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ================= переключение аккаунтов =================

    private void OnPrev(object? sender, EventArgs e) => Switch(_index - 1);
    private void OnNext(object? sender, EventArgs e) => Switch(_index + 1);

    private void Switch(int i)
    {
        if (_ids.Count < 2) return;
        _index = ((i % _ids.Count) + _ids.Count) % _ids.Count; // по кругу
        _id = _ids[_index];
        _ = Scroll.ScrollToAsync(0, 0, false);
        Load();
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

    /// <summary>Блок с живым кодом. Может быть несколько (свой секрет аккаунта + подходящие записи из «Кодов»).
    /// bind — для общих кодов сайта: кнопка «Привязать к этому аккаунту».</summary>
    private void AddTotpBlock(string secretOrUri, string? label = null, (string Id, string Raw, string RecTitle)? bind = null)
    {
        if (string.IsNullOrWhiteSpace(secretOrUri)) return;
        TotpConfig cfg;
        try { cfg = Totp.Parse(secretOrUri); }
        catch (Exception) { return; }

        string extra = (label ?? "").Trim();
        if (extra.Length == 0) extra = (cfg.Account ?? "").Trim();
        string caption = extra.Length > 0 ? $"Код проверки · {extra}" : "Код проверки";

        var codeLabel = new Label { FontSize = 30, FontFamily = "Menlo", FontAttributes = FontAttributes.Bold };
        var secondsLabel = new Label { Style = MutedStyle(), FontSize = 13, HorizontalOptions = LayoutOptions.End };
        var bar = new ProgressBar();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        head.Add(new Label { Text = caption, Style = MutedStyle(), FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation }, 0, 0);
        head.Add(secondsLabel, 1, 0);

        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Children.Add(head);
        stack.Children.Add(codeLabel);
        stack.Children.Add(bar);

        if (bind is { } b)
        {
            var link = new Button
            {
                Text = "Привязать к этому аккаунту",
                FontSize = 13,
                Padding = new Thickness(0, 4),
                BackgroundColor = Colors.Transparent,
                HorizontalOptions = LayoutOptions.Start,
            };
            if (Application.Current?.Resources.TryGetValue("IpAccent", out var dc) == true && dc is Color darkAccent
                && Application.Current.Resources.TryGetValue("IpAccentL", out var lc) && lc is Color lightAccent)
                link.SetAppThemeColor(Button.TextColorProperty, lightAccent, darkAccent);
            link.Clicked += async (_, _) => await BindTotpAsync(b.Id, b.Raw, b.RecTitle);
            stack.Children.Add(link);
        }

        var border = new Border { Style = CardStyle(), Content = stack };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                string code = Totp.Generate(cfg.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cfg.Digits, cfg.Period, cfg.Algorithm);
                await CopyAsync(code, "Код проверки");
            }
            catch (Exception) { }
        };
        border.GestureRecognizers.Add(tap);
        Rows.Children.Add(border);

        void Tick()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                codeLabel.Text = Fmt.SplitCode(Totp.Generate(cfg.Secret, now, cfg.Digits, cfg.Period, cfg.Algorithm));
                int left = Totp.SecondsRemaining(now, cfg.Period);
                secondsLabel.Text = left + " с";
                bar.Progress = (double)left / Math.Max(1, cfg.Period);
            }
            catch (Exception) { codeLabel.Text = "——— ———"; }
        }

        _totpTicks.Add(Tick);
        Tick();
        EnsureTimer();
    }

    private void EnsureTimer()
    {
        if (_timer is not null) return;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => { foreach (Action t in _totpTicks) t(); };
        _timer.Start();
    }

    /// <summary>Общий код сайта становится личным кодом этого аккаунта:
    /// секрет переезжает в поле totp записи, отдельная запись из «Кодов» удаляется.</summary>
    private async Task BindTotpAsync(string totpRecordId, string raw, string recordTitle)
    {
        Vault? v = Svc.State.Vault;
        if (v is null || _item is null) return;

        string login = _item.Fields.GetValueOrDefault("username", "");
        string who = login.Length > 0 ? login : Title;
        string msg = $"Код «{recordTitle}» станет кодом аккаунта {who} и перестанет показываться у остальных аккаунтов этого сайта.";
        if (_item.Fields.GetValueOrDefault("totp", "").Trim().Length > 0)
            msg += "\n\nТекущий код этого аккаунта будет заменён.";

        bool ok = await DisplayAlert("Привязать код к этому аккаунту?", msg, "Привязать", "Отмена");
        if (!ok) return;

        _item.Fields["totp"] = raw;
        try { v.Update(_id, _item); } catch (Exception) { return; }
        try { v.Delete(totpRecordId); } catch (Exception) { }
        await Svc.State.SaveAsync();
        Load();
        await ShowToastAsync("Код привязан к аккаунту");
    }

    // ================= действия =================

    private async Task CopyAsync(string value, string what)
    {
        await SecureClipboard.CopyAsync(value);
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

        // Если в карточке сайта остались другие аккаунты — покажем следующий, иначе выходим.
        _ids.Remove(_id);
        if (_ids.Count == 0) { await Navigation.PopAsync(); return; }
        _index = Math.Min(_index, _ids.Count - 1);
        _id = _ids[_index];
        Load();
    }
}
