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
#if ANDROID
        AttachAndroidSwipes();
#endif
    }

#if ANDROID
    /// <summary>Свайпы влево/вправо между записями сайта. На Android горизонтальный жест
    /// не спорит с вертикальной прокруткой, поэтому хватает обычных распознавателей MAUI.</summary>
    private void AttachAndroidSwipes()
    {
        if (Content is null) return;

        var left = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
        left.Swiped += (_, _) => Switch(_index + 1);
        var right = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
        right.Swiped += (_, _) => Switch(_index - 1);

        Content.GestureRecognizers.Add(left);
        Content.GestureRecognizers.Add(right);
    }
#endif

#if IOS
    // Свайп влево/вправо переключает аккаунты. MAUI-жест не срабатывает поверх ScrollView,
    // поэтому вешаем нативный распознаватель на корневой view страницы —
    // с одновременным распознаванием, чтобы не мешать вертикальной прокрутке.
    private bool _nativeSwipes;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (_nativeSwipes || Handler?.PlatformView is not UIKit.UIView v) return;
        _nativeSwipes = true;

        var left = new UIKit.UISwipeGestureRecognizer(() => Switch(_index + 1))
        {
            Direction = UIKit.UISwipeGestureRecognizerDirection.Left,
            ShouldRecognizeSimultaneously = (_, _) => true,
        };
        var right = new UIKit.UISwipeGestureRecognizer(() => Switch(_index - 1))
        {
            Direction = UIKit.UISwipeGestureRecognizerDirection.Right,
            ShouldRecognizeSimultaneously = (_, _) => true,
        };
        v.AddGestureRecognizer(left);
        v.AddGestureRecognizer(right);
    }
#endif

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
                if (ownTotp.Length > 0)
                {
                    AddTotpBlock(ownTotp);
                }
                else
                {
                    // Как на ПК: однозначный код из «Кодов» показывается сам, без привязок
                    // (совпали сайт и логин, или у сайта единственный аккаунт).
                    string? linked = TotpMeta.FindLinkedTotp(v, _item);
                    if (linked is not null)
                    {
                        AddTotpBlock(linked);
                    }
                    else
                    {
                        // Неоднозначные коды сайта (несколько аккаунтов, в коде логин не записан) —
                        // показываем как общие с кнопкой «Привязать к этому аккаунту».
                        foreach (VaultEntry t in MatchedTotps(v, _item, ownTotp))
                        {
                            string raw = t.Item.Fields.GetValueOrDefault("totp", "");
                            var (_, acc) = TotpMeta.IssuerAccount(raw);
                            AddTotpBlock(raw, acc.Length > 0 ? acc : t.Item.Title, (t.Id, raw, t.Item.Title));
                        }
                    }
                }
                AddExtraFields("url", "username", "password", "totp");
                AddPasswordHistory();
                break;
            case "card":
                string number = _item.Fields.GetValueOrDefault("number", "");
                AddCopyRow(("Номер " + Fmt.CardBrand(number)).Trim(), Fmt.GroupDigits(number), copyValue: number, secret: true);
                AddCopyRow("Срок", _item.Fields.GetValueOrDefault("expiry", ""));
                AddCopyRow("CVC/CVV", _item.Fields.GetValueOrDefault("cvc", ""), secret: true);
                AddCopyRow("Держатель", _item.Fields.GetValueOrDefault("holder", ""));
                AddExtraFields("number", "expiry", "cvc", "holder");
                break;
            case "doc":
                AddCopyRow("Номер", _item.Fields.GetValueOrDefault("number", ""));
                AddCopyRow("Выдан", _item.Fields.GetValueOrDefault("issued", ""));
                AddExtraFields("number", "issued");
                break;
            case "identity":
                // Порядок — как в форме доставки: кто, как связаться, куда везти.
                AddCopyRow("ФИО", string.Join(" ", new[] { "lastName", "firstName", "middleName" }
                    .Select(k => _item.Fields.GetValueOrDefault(k, "")).Where(x => x.Length > 0)));
                AddCopyRow("Телефон", _item.Fields.GetValueOrDefault("phone", ""));
                AddCopyRow("Почта", _item.Fields.GetValueOrDefault("email", ""));
                AddCopyRow("Индекс", _item.Fields.GetValueOrDefault("zip", ""));
                AddCopyRow("Страна", _item.Fields.GetValueOrDefault("country", ""));
                AddCopyRow("Город", _item.Fields.GetValueOrDefault("city", ""));
                AddCopyRow("Адрес", _item.Fields.GetValueOrDefault("street", ""));
                // Чаще всего нужно именно это — вставить адрес целиком в одно поле.
                AddCopyRow("Адрес одной строкой", string.Join(", ", new[] { "zip", "country", "city", "street" }
                    .Select(k => _item.Fields.GetValueOrDefault(k, "")).Where(x => x.Length > 0)));
                AddExtraFields("lastName", "firstName", "middleName", "phone", "email", "zip", "country", "city", "street");
                break;
            case "passkey":
                AddCopyRow("Сайт", _item.Fields.GetValueOrDefault("url", ""));
                AddCopyRow("Логин", _item.Fields.GetValueOrDefault("username", ""));
                AddInfo("Ключ доступа привязан к устройству, где был создан. Вход по нему на телефоне появится позже.");
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

        AddAttachments();

        // Где лежит запись — видно из карточки, без похода в редактор. Меняются папки там же,
        // где и остальные поля, — через «Изменить».
        var itemFolders = ItemFolders.Of(_item);
        if (itemFolders.Count > 0)
            AddInfo($"📁 {(itemFolders.Count == 1 ? "Папка" : "Папки")}: {string.Join(", ", itemFolders)}");

        var del = new Button { Text = "Удалить", Style = (Style)Application.Current!.Resources["Danger"], Margin = new Thickness(0, 24, 0, 0) };
        del.Clicked += OnDelete;
        Rows.Children.Add(del);
    }

    // ================= вложения =================

    /// <summary>Раздел показывается всегда, даже пустой: функция, на которую нельзя случайно
    /// наткнуться, всё равно что отсутствует — на папках это уже проверено.</summary>
    private void AddAttachments()
    {
        if (_item is null) return;

        AddSection("Вложения");

        foreach (Attachment a in _item.Attachments)
        {
            Attachment att = a;

            var name = new Label { Text = att.Name, FontSize = 15, LineBreakMode = LineBreakMode.MiddleTruncation };
            string when = Attachments.AddedOn(att);
            var meta = new Label
            {
                Text = Attachments.HumanSize(att.Bytes) + (when.Length > 0 ? " · " + when : ""),
                Style = MutedStyle(),
                FontSize = 12,
            };

            var stack = new VerticalStackLayout { Spacing = 1 };
            stack.Children.Add(name);
            stack.Children.Add(meta);

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.Add(new Label
            {
                Text = Attachments.IsPicture(att) ? "🖼" : "📄",
                FontSize = 20,
                VerticalOptions = LayoutOptions.Center,
            }, 0, 0);
            grid.Add(stack, 1, 0);

            var kill = new Button { Text = "✕", FontSize = 15, Padding = new Thickness(10, 4), BackgroundColor = Colors.Transparent };
            kill.Clicked += async (_, _) => await RemoveAttachmentAsync(att);
            grid.Add(kill, 2, 0);

            var border = new Border { Style = CardStyle(), Content = grid };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await OpenAttachmentAsync(att);
            border.GestureRecognizers.Add(tap);
            Rows.Children.Add(border);
        }

        var add = new Button
        {
            Text = _item.Attachments.Count == 0 ? "＋ Добавить фото или файл" : "＋ Добавить ещё",
            FontSize = 14,
            Padding = new Thickness(0, 6),
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Start,
        };
        AccentText(add);
        add.Clicked += OnAddAttachment;
        Rows.Children.Add(add);
    }

    private static void AccentText(Button b)
    {
        if (Application.Current?.Resources.TryGetValue("IpAccent", out var dc) == true && dc is Color dark
            && Application.Current.Resources.TryGetValue("IpAccentL", out var lc) && lc is Color light)
            b.SetAppThemeColor(Button.TextColorProperty, light, dark);
    }

    private async void OnAddAttachment(object? sender, EventArgs e)
    {
        if (_item is null) return;

        Attachment? att = await AttachmentPick.PickAsync(this, _item.Attachments.Count);
        if (att is null) return;

        Vault? v = Svc.State.Vault;
        if (v is null) return;

        _item.Attachments.Add(att);
        try { v.Update(_id, _item); }
        catch (Exception ex)
        {
            _item.Attachments.Remove(att);
            await DisplayAlert("Не получилось", ex.Message, "Ок");
            return;
        }

        await Svc.State.SaveAsync();
        Load();
        await ShowToastAsync("Вложение добавлено");
    }

    private async Task RemoveAttachmentAsync(Attachment att)
    {
        Vault? v = Svc.State.Vault;
        if (v is null || _item is null) return;

        bool ok = await DisplayAlert("Удалить вложение?", att.Name, "Удалить", "Отмена");
        if (!ok) return;

        _item.Attachments.Remove(att);
        try { v.Update(_id, _item); }
        catch (Exception) { return; }

        await Svc.State.SaveAsync();
        Load();
    }

    private async Task OpenAttachmentAsync(Attachment att)
    {
        byte[] data;
        try { data = Convert.FromBase64String(att.Data); }
        catch (Exception)
        {
            await DisplayAlert("Не получилось", "Вложение повреждено.", "Ок");
            return;
        }

        if (Attachments.IsPicture(att))
        {
            await Navigation.PushAsync(new AttachmentPage(att.Name, data));
            return;
        }

        // PDF и прочее показывать нечем — отдаём системе. Временную копию кладём в кэш
        // приложения и подчищаем прошлые перед каждым разом: удалять сразу нельзя,
        // лист «Поделиться» читает файл уже после возврата из RequestAsync.
        string dir = Path.Combine(FileSystem.CacheDirectory, "attach");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Attachments.SafeFileName(att.Name));
            await File.WriteAllBytesAsync(path, data);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = att.Name, File = new ShareFile(path) });
        }
        catch (Exception)
        {
            await DisplayAlert("Не получилось", "Файл не открылся.", "Ок");
        }
    }

    /// <summary>Отдельные totp-записи, подходящие сайту аккаунта (кроме дубля собственного секрета).
    /// Как на ПК: код с ЧУЖИМ логином (в username-поле записи или в otpauth://) — это код другого
    /// аккаунта этого сайта, здесь его не показываем и привязать не предлагаем. Остаются только
    /// действительно неоднозначные коды: без логина, когда аккаунтов на сайте несколько,
    /// либо несколько кодов с тем же логином.</summary>
    private static List<VaultEntry> MatchedTotps(Vault v, VaultItem account, string ownTotp)
    {
        var res = new List<VaultEntry>();
        string key = SiteGroups.KeyFor(account);
        string user = account.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
        try
        {
            foreach (VaultEntry t in v.Items())
            {
                if (t.Item.Type != "totp") continue;
                string raw = t.Item.Fields.GetValueOrDefault("totp", "").Trim();
                if (raw.Length == 0 || raw == ownTotp) continue;
                if (!TotpMeta.MatchesSite(t.Item, key)) continue;
                string tuser = t.Item.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
                if (tuser.Length == 0) tuser = TotpMeta.IssuerAccount(raw).Account.Trim().ToLowerInvariant();
                if (tuser.Length > 0 && tuser != user) continue;   // чужой код — как на ПК, пропускаем
                res.Add(t);
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

    private void AddPasswordHistory()
    {
        if (_item is null || _item.History.Count == 0) return;

        AddSection("Прежние пароли");
        // от новых к старым
        foreach (PasswordChange h in _item.History.AsEnumerable().Reverse())
        {
            string when = "";
            if (!string.IsNullOrEmpty(h.ReplacedAt) && DateTimeOffset.TryParse(h.ReplacedAt, out var dt))
                when = dt.ToLocalTime().ToString("dd.MM.yyyy");
            AddCopyRow(when.Length > 0 ? "Заменён " + when : "Прежний пароль", h.Password, secret: true);
        }

        var forget = new Button
        {
            Text = "Забыть историю",
            BackgroundColor = Colors.Transparent,
            TextColor = BadColor(),
            HorizontalOptions = LayoutOptions.Start,
        };
        forget.Clicked += async (_, _) =>
        {
            bool ok = await DisplayAlert("Забыть историю паролей?",
                "Список прежних паролей этой записи будет удалён без возможности восстановления.", "Забыть", "Отмена");
            if (!ok) return;
            Vault? v = Svc.State.Vault;
            if (v is null || _item is null) return;
            try { v.ClearPasswordHistory(_id); } catch (Exception) { return; }
            await Svc.State.SaveAsync();
            Load();
        };
        Rows.Children.Add(forget);
    }

    private static Color BadColor()
    {
        if (Application.Current?.Resources.TryGetValue("IpBad", out var c) == true && c is Color color) return color;
        return Color.FromArgb("#EB6E6E");
    }

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
