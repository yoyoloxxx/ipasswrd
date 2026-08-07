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
            ? _type switch { "card" => "Новая карта", "doc" => "Новый документ", "note" => "Новая заметка", _ => "Новый аккаунт" }
            : "Изменение";

        AccountForm.IsVisible = _type is "account" or "passkey";
        CardForm.IsVisible = _type == "card";
        DocumentForm.IsVisible = _type == "doc";

        LoadExisting();
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
