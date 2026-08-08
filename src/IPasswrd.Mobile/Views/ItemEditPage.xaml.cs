using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class ItemEditPage : ContentPage
{
    private readonly string? _id;          // null — новая запись
    private readonly string _type;
    private VaultItem _item = new();

    public ItemEditPage(string? id, string type)
    {
        InitializeComponent();
        _id = id;
        _type = type;

        Title = _id is null
            ? _type switch { "card" => "Новая карта", "doc" => "Новый документ", "note" => "Новая заметка", "identity" => "Личные данные", _ => "Новый аккаунт" }
            : "Изменение";

        AccountForm.IsVisible = _type is "account" or "passkey";
        CardForm.IsVisible = _type == "card";
        DocumentForm.IsVisible = _type == "doc";
        IdentityForm.IsVisible = _type == "identity";
        // Имя записи соберётся из ФИО — подсказка не должна требовать его вводить.
        if (_type == "identity") TitleBox.Placeholder = "Название — необязательно";

        LoadExisting();
        RenderAttachments();
        UpdateFolderRow();
    }

    // ================= папки =================

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateFolderRow();   // вернулись с экрана выбора папок — показать актуальный список
    }

    private void UpdateFolderRow()
    {
        var folders = ItemFolders.Of(_item);
        FolderValue.Text = folders.Count > 0 ? string.Join(", ", folders) : "Без папки";
    }

    /// <summary>
    /// Папок у записи может быть несколько, поэтому открываем экран мультивыбора
    /// (переключатели + создание новой), а не системный лист выбора одной.
    /// Экран правит список прямо в _item; в сейф уедет с кнопкой «Сохранить».
    /// </summary>
    private async void OnPickFolder(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new FolderPickPage(_item));
        // строка обновится в OnAppearing при возврате
    }

    // ================= вложения =================

    /// <summary>
    /// Вложения набираются в самой записи и уходят в сейф одним сохранением.
    ///
    /// Раньше добавить файл можно было только в карточке уже сохранённой записи, а человек,
    /// заводящий паспорт, упирался в форму, где фотографии просто нет. Подсказка «сначала
    /// сохраните» тут не помогает: она объясняет неудобство, а не убирает его.
    ///
    /// Отдельного хранилища для «ещё не сохранённых» файлов не потребовалось: вложения лежат
    /// внутри записи, и Add пишет их вместе со всем остальным. Пределы — десять штук, 2 МБ на
    /// файл — проверяет сейф при сохранении, а не форма: правило должно быть одно, где бы
    /// запись ни заводили.
    /// </summary>
    private void RenderAttachments()
    {
        AttachRows.Children.Clear();

        foreach (Attachment a in _item.Attachments)
        {
            Attachment att = a;

            var name = new Label { Text = att.Name, FontSize = 15, LineBreakMode = LineBreakMode.MiddleTruncation };
            var meta = new Label { Text = Attachments.HumanSize(att.Bytes), FontSize = 12, Style = MutedStyle() };

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
            // Запись ещё не сохранена, удалять нечего — поэтому без вопроса «точно?»: отмена —
            // это просто добавить файл заново.
            kill.Clicked += (_, _) => { _item.Attachments.Remove(att); RenderAttachments(); };
            grid.Add(kill, 2, 0);

            AttachRows.Children.Add(grid);
        }

        AttachAdd.Text = _item.Attachments.Count == 0 ? "＋ Добавить фото или файл" : "＋ Добавить ещё";
    }

    private static Style MutedStyle() => (Style)Application.Current!.Resources["Muted"];

    private async void OnAddAttachment(object? sender, EventArgs e)
    {
        Attachment? att = await AttachmentPick.PickAsync(this, _item.Attachments.Count);
        if (att is null) return;

        _item.Attachments.Add(att);
        RenderAttachments();
    }

    private void LoadExisting()
    {
        if (_id is null) return;
        Vault? v = Svc.State.Vault;
        if (v is null) return;
        try { _item = v.Get(_id); } catch (Exception) { return; }

        TitleBox.Text = _item.Title;
        NotesBox.Text = _item.Notes;
        switch (_type)
        {
            case "account":
            case "passkey":
                UrlBox.Text = _item.Fields.GetValueOrDefault("url", "");
                UserBox.Text = _item.Fields.GetValueOrDefault("username", "");
                PasswordEdit.Text = _item.Fields.GetValueOrDefault("password", "");
                TotpBox.Text = _item.Fields.GetValueOrDefault("totp", "");
                break;
            case "card":
                NumberBox.Text = Fmt.GroupDigits(_item.Fields.GetValueOrDefault("number", ""));
                ExpiryBox.Text = _item.Fields.GetValueOrDefault("expiry", "");
                CvcBox.Text = _item.Fields.GetValueOrDefault("cvc", "");
                HolderBox.Text = _item.Fields.GetValueOrDefault("holder", "");
                break;
            case "doc":
                DocNumberBox.Text = _item.Fields.GetValueOrDefault("number", "");
                IssuedBox.Text = _item.Fields.GetValueOrDefault("issued", "");
                break;
            case "identity":
                LastNameBox.Text = _item.Fields.GetValueOrDefault("lastName", "");
                FirstNameBox.Text = _item.Fields.GetValueOrDefault("firstName", "");
                MiddleNameBox.Text = _item.Fields.GetValueOrDefault("middleName", "");
                PhoneBox.Text = _item.Fields.GetValueOrDefault("phone", "");
                EmailBox.Text = _item.Fields.GetValueOrDefault("email", "");
                ZipBox.Text = _item.Fields.GetValueOrDefault("zip", "");
                CountryBox.Text = _item.Fields.GetValueOrDefault("country", "");
                CityBox.Text = _item.Fields.GetValueOrDefault("city", "");
                StreetBox.Text = _item.Fields.GetValueOrDefault("street", "");
                break;
        }
    }

    // ================= маски (как на Windows: номер по 4, срок с авто-слэшем) =================

    private bool _masking;

    private void OnNumberChanged(object? sender, TextChangedEventArgs e)
    {
        if (_masking) return;
        _masking = true;
        string digits = new((e.NewTextValue ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length > 19) digits = digits[..19];
        NumberBox.Text = Fmt.GroupDigits(digits);
        NumberBox.CursorPosition = NumberBox.Text.Length;   // иначе на части Android курсор прыгает в начало
        BrandLabel.Text = Fmt.CardBrand(digits);
        _masking = false;
    }

    private void OnExpiryChanged(object? sender, TextChangedEventArgs e)
    {
        if (_masking) return;
        _masking = true;
        string digits = new((e.NewTextValue ?? "").Where(char.IsDigit).ToArray());
        // 2–9 в первой позиции → сразу 02–09 (как AttachExpiryMask на Windows)
        if (digits.Length >= 1 && digits[0] >= '2' && digits[0] <= '9')
            digits = "0" + digits;
        if (digits.Length > 4) digits = digits[..4];
        ExpiryBox.Text = digits.Length > 2 ? digits[..2] + "/" + digits[2..] : digits;
        ExpiryBox.CursorPosition = ExpiryBox.Text.Length;
        _masking = false;
    }

    // ================= пароль =================

    private void OnRevealPassword(object? sender, EventArgs e) =>
        PasswordEdit.IsPassword = !PasswordEdit.IsPassword;

    private void OnGeneratePassword(object? sender, EventArgs e) =>
        PasswordEdit.Text = Generator.Generate();

    // ================= код проверки по QR =================

    private async void OnScanTotp(object? sender, EventArgs e)
    {
        string? raw = await Svc.Qr.ScanAsync();
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!Totp.IsValidSecret(raw))
        {
            await DisplayAlert("Не получилось", "В этом QR-коде нет кода для аутентификатора.", "Ок");
            return;
        }
        TotpBox.Text = raw.Trim();
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            var cfg = Totp.Parse(raw);
            if (cfg.Issuer.Length > 0) TitleBox.Text = cfg.Issuer;
        }
    }

    // ================= сохранение =================

    private async void OnSave(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        _item.Type = _type;
        _item.Title = (TitleBox.Text ?? "").Trim();
        _item.Notes = NotesBox.Text ?? "";

        switch (_type)
        {
            case "account":
            case "passkey":
                SetField("url", UrlBox.Text);
                SetField("username", UserBox.Text);
                SetField("password", PasswordEdit.Text);
                string totp = (TotpBox.Text ?? "").Trim();
                if (totp.Length > 0 && !Totp.IsValidSecret(totp))
                {
                    ShowError("Код проверки не распознан: нужен Base32-секрет или ссылка otpauth://.");
                    return;
                }
                SetField("totp", totp);
                if (_item.Title.Length == 0)
                    _item.Title = SiteGroups.HostOf(UrlBox.Text ?? "") is { Length: > 0 } h ? h : (UserBox.Text ?? "").Trim();
                break;

            case "card":
                string number = new((NumberBox.Text ?? "").Where(char.IsDigit).ToArray());
                SetField("number", number);             // чистые цифры — как ждёт Windows и автозаполнение
                SetField("expiry", (ExpiryBox.Text ?? "").Trim());
                SetField("cvc", (CvcBox.Text ?? "").Trim());
                SetField("holder", (HolderBox.Text ?? "").Trim().ToUpperInvariant());
                if (_item.Title.Length == 0)
                    _item.Title = ($"{Fmt.CardBrand(number)} {Fmt.MaskCard(number)}").Trim();
                break;

            case "doc":
                SetField("number", DocNumberBox.Text);
                SetField("issued", IssuedBox.Text);
                break;

            case "identity":
                SetField("lastName", LastNameBox.Text);
                SetField("firstName", FirstNameBox.Text);
                SetField("middleName", MiddleNameBox.Text);
                SetField("phone", PhoneBox.Text);
                SetField("email", EmailBox.Text);
                SetField("zip", ZipBox.Text);
                SetField("country", CountryBox.Text);
                SetField("city", CityBox.Text);
                SetField("street", StreetBox.Text);
                // Заполняют ведь себя, а не «запись» — название собираем из ФИО, если его не задали.
                if (_item.Title.Length == 0)
                    _item.Title = string.Join(" ", new[] { LastNameBox.Text, FirstNameBox.Text, MiddleNameBox.Text }
                        .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0));
                break;
        }

        if (_item.Title.Length == 0 && _type != "note")
        {
            ShowError("Дайте записи название.");
            return;
        }

        try
        {
            if (_id is null) v.Add(_item);
            else v.Update(_id, _item);
        }
        // Отказ из-за вложений объясняется словами сейфа: «Не удалось сохранить» на одиннадцатом
        // файле оставляет человека гадать, что именно не так с записью.
        catch (AttachmentTooLargeException ex)
        {
            ShowError(ex.Message);
            return;
        }
        catch (Exception)
        {
            ShowError("Не удалось сохранить запись.");
            return;
        }

        await Svc.State.SaveAsync();
        await Navigation.PopAsync();
    }

    private void SetField(string key, string? value)
    {
        string s = (value ?? "").Trim();
        if (s.Length == 0) _item.Fields.Remove(key);
        else _item.Fields[key] = s;
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }
}
