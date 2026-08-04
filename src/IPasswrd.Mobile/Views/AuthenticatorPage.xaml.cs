using System.Collections.ObjectModel;
using System.ComponentModel;
using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public sealed class AuthRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public bool HasSub => Sub.Length > 0;
    public string Raw { get; init; } = "";
    public bool Standalone { get; init; }
    public TotpConfig? Cfg { get; init; }

    private string _code = "";
    public string Code { get => _code; set { if (_code == value) return; _code = value; OnChanged(nameof(Code)); } }

    private string _seconds = "";
    public string Seconds { get => _seconds; set { if (_seconds == value) return; _seconds = value; OnChanged(nameof(Seconds)); } }

    private double _progress;
    public double Progress { get => _progress; set { if (Math.Abs(_progress - value) < 0.001) return; _progress = value; OnChanged(nameof(Progress)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class AuthenticatorPage : ContentPage
{
    private readonly ObservableCollection<AuthRow> _rows = new();
    private IDispatcherTimer? _timer;

    public AuthenticatorPage()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        Svc.State.VaultChanged += OnVaultChanged;
    }

    private void OnVaultChanged() => MainThread.BeginInvokeOnMainThread(Reload);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Reload();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private void Reload()
    {
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        List<VaultEntry> all;
        try { all = v.Items().ToList(); }
        catch (Exception) { return; }

        var withTotp = all
            .Where(x => x.Item.Fields.TryGetValue("totp", out var t) && !string.IsNullOrWhiteSpace(t))
            .OrderBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase);
        List<VaultEntry> accounts = all.Where(x => x.Item.Type == "account").ToList();

        _rows.Clear();
        foreach (VaultEntry e in withTotp)
        {
            string raw = e.Item.Fields["totp"];
            TotpConfig? cfg = null;
            try { cfg = Totp.Parse(raw); } catch (Exception) { }

            string name = e.Item.Title;
            if (cfg is not null && string.IsNullOrWhiteSpace(name))
                name = cfg.Issuer.Length > 0 ? cfg.Issuer : cfg.Label;

            // Чей это код: аккаунт из otpauth://, логин записи-аккаунта,
            // либо логин единственного подходящего аккаунта этого сайта.
            string sub = "";
            if (e.Item.Type == "account")
                sub = e.Item.Fields.GetValueOrDefault("username", "");
            else
            {
                // Сначала аккаунт, сохранённый в самой записи кода (ручной ввод/редактирование),
                // затем аккаунт из otpauth://, затем — вывод по единственному логину сайта.
                sub = e.Item.Fields.GetValueOrDefault("username", "").Trim();
                if (sub.Length == 0) sub = (cfg?.Account ?? "").Trim();
                if (sub.Length == 0)
                {
                    var logins = accounts
                        .Where(a => TotpMeta.MatchesSite(e.Item, SiteGroups.KeyFor(a.Item)))
                        .Select(a => a.Item.Fields.GetValueOrDefault("username", "").Trim())
                        .Where(u => u.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (logins.Count == 1) sub = logins[0];
                    // У сайта несколько аккаунтов, а в секрете кода не записано, чей он:
                    // подскажем, как это исправить (Сейф → аккаунт → «Привязать к этому аккаунту»).
                    else if (logins.Count > 1) sub = "не привязан к аккаунту";
                }
            }
            if (sub.Equals(name, StringComparison.OrdinalIgnoreCase)) sub = "";

            _rows.Add(new AuthRow
            {
                Id = e.Id,
                Name = name.Length > 0 ? name : "(без названия)",
                Sub = sub,
                Raw = raw,
                Standalone = e.Item.Type == "totp",
                Cfg = cfg,
            });
        }
        Tick();
    }

    private void Tick()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (AuthRow r in _rows)
        {
            if (r.Cfg is null) { r.Code = "———"; continue; }
            try
            {
                r.Code = Fmt.SplitCode(Totp.Generate(r.Cfg.Secret, now, r.Cfg.Digits, r.Cfg.Period, r.Cfg.Algorithm));
                int left = Totp.SecondsRemaining(now, r.Cfg.Period);
                r.Seconds = left + " с";
                r.Progress = (double)left / Math.Max(1, r.Cfg.Period);
            }
            catch (Exception) { r.Code = "———"; }
        }
    }

    // ================= добавление =================

    private async void OnAdd(object? sender, EventArgs e)
    {
        string? choice = await DisplayActionSheet("Добавить код проверки", "Отмена", null,
            "Сканировать QR-код", "Ввести секрет вручную");
        if (choice == "Сканировать QR-код") await AddByQrAsync();
        else if (choice == "Ввести секрет вручную") await AddManualAsync();
    }

    private async Task AddByQrAsync()
    {
        string? raw = await Svc.Qr.ScanAsync();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (!Totp.IsValidSecret(raw))
        {
            await DisplayAlert("Не получилось", "В этом QR-коде нет кода для аутентификатора (нужен otpauth:// или Base32-секрет).", "Ок");
            return;
        }

        var cfg = Totp.Parse(raw);
        string suggested = cfg.Issuer.Length > 0 ? cfg.Issuer : cfg.Label;

        string? name = await DisplayPromptAsync("Название", "Как подписать этот код?",
            "Сохранить", "Отмена", initialValue: suggested);
        if (name is null) return;

        await SaveNewAsync(name.Trim(), raw.Trim(), cfg.Account.Trim());
    }

    private async Task AddManualAsync()
    {
        string? secret = await DisplayPromptAsync("Код проверки", "Секрет (Base32) или ссылка otpauth://",
            "Дальше", "Отмена");
        if (string.IsNullOrWhiteSpace(secret)) return;
        if (!Totp.IsValidSecret(secret))
        {
            await DisplayAlert("Не получилось", "Секрет не распознан. Проверьте, что это Base32 (буквы A–Z и цифры 2–7) или ссылка otpauth://.", "Ок");
            return;
        }

        // Как на ПК: название и аккаунт с подстановкой из otpauth://, оба поля можно оставить пустыми.
        var cfg = Totp.Parse(secret);
        string? name = await DisplayPromptAsync("Название", "Например, GitHub",
            "Дальше", "Отмена", initialValue: cfg.Issuer);
        if (name is null) return;

        string? account = await DisplayPromptAsync("Аккаунт", "Имя или почта (необязательно)",
            "Сохранить", "Отмена", initialValue: cfg.Account);
        if (account is null) return;

        await SaveNewAsync(name.Trim(), secret.Trim(), account.Trim());
    }

    private async Task SaveNewAsync(string name, string secret, string account = "")
    {
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        // Фолбэки как на ПК: пустое название → issuer, затем аккаунт, затем «Код проверки».
        var cfg = Totp.Parse(secret);
        if (string.IsNullOrWhiteSpace(name))
            name = !string.IsNullOrWhiteSpace(cfg.Issuer) ? cfg.Issuer
                 : !string.IsNullOrWhiteSpace(cfg.Account) ? cfg.Account : "Код проверки";
        if (string.IsNullOrWhiteSpace(account)) account = cfg.Account;

        var item = new VaultItem { Type = "totp", Title = name };
        item.Fields["totp"] = secret;
        if (!string.IsNullOrWhiteSpace(account)) item.Fields["username"] = account.Trim();
        v.Add(item);
        await Svc.State.SaveAsync();
        Reload();
        await ShowToastAsync("Код добавлен");
    }

    // ================= строки =================

    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not AuthRow r || r.Cfg is null) return;
        string code = Totp.Generate(r.Cfg.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), r.Cfg.Digits, r.Cfg.Period, r.Cfg.Algorithm);
        await SecureClipboard.CopyAsync(code);
        await ShowToastAsync($"{r.Name}: код скопирован");
    }

    private async void OnEditClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not AuthRow r) return;
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        if (!r.Standalone)
        {
            await DisplayAlert("Это код аккаунта",
                "Название и секрет этого кода меняются в самой записи (Сейф → запись → Изменить).", "Ок");
            return;
        }

        string? choice = await DisplayActionSheet(r.Name, "Отмена", null, "Переименовать", "Изменить аккаунт", "Заменить секрет");
        if (choice == "Переименовать")
        {
            string? name = await DisplayPromptAsync("Название", "Как подписать этот код?",
                "Сохранить", "Отмена", initialValue: r.Name);
            if (name is null || name.Trim().Length == 0 || name.Trim() == r.Name) return;
            try
            {
                VaultItem it = v.Get(r.Id);
                it.Title = name.Trim();
                v.Update(r.Id, it);
            }
            catch (Exception) { return; }
            await Svc.State.SaveAsync();
            Reload();
            await ShowToastAsync("Переименовано");
        }
        else if (choice == "Изменить аккаунт")
        {
            string current = "";
            try
            {
                VaultItem cur = v.Get(r.Id);
                current = cur.Fields.GetValueOrDefault("username", "").Trim();
                if (current.Length == 0) current = (r.Cfg?.Account ?? "").Trim();
            }
            catch (Exception) { }

            string? account = await DisplayPromptAsync("Аккаунт", "Имя или почта (пусто — убрать)",
                "Сохранить", "Отмена", initialValue: current);
            if (account is null) return;
            try
            {
                VaultItem it = v.Get(r.Id);
                if (account.Trim().Length == 0) it.Fields.Remove("username");
                else it.Fields["username"] = account.Trim();
                v.Update(r.Id, it);
            }
            catch (Exception) { return; }
            await Svc.State.SaveAsync();
            Reload();
            await ShowToastAsync("Аккаунт обновлён");
        }
        else if (choice == "Заменить секрет")
        {
            string? secret = await DisplayPromptAsync("Новый секрет", "Base32 или ссылка otpauth:// с сайта",
                "Сохранить", "Отмена");
            if (string.IsNullOrWhiteSpace(secret)) return;
            if (!Totp.IsValidSecret(secret))
            {
                await DisplayAlert("Не получилось", "Секрет не распознан. Нужен Base32 (A–Z, 2–7) или ссылка otpauth://.", "Ок");
                return;
            }
            try
            {
                VaultItem it = v.Get(r.Id);
                it.Fields["totp"] = secret.Trim();
                v.Update(r.Id, it);
            }
            catch (Exception) { return; }
            await Svc.State.SaveAsync();
            Reload();
            await ShowToastAsync("Секрет обновлён");
        }
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not AuthRow r) return;

        Vault? v = Svc.State.Vault;
        if (v is null) return;

        if (!r.Standalone)
        {
            await DisplayAlert("Это код аккаунта",
                "Этот код привязан к записи аккаунта. Уберите его в самой записи (Сейф → запись → Изменить).", "Ок");
            return;
        }

        string who = r.HasSub ? $"{r.Name} ({r.Sub})" : r.Name;
        bool ok = await DisplayAlert("Удалить код?",
            $"{who}\n\nУбедитесь, что двухэтапная проверка на сайте отключена или перенесена, иначе можно потерять доступ.",
            "Удалить", "Отмена");
        if (!ok) return;

        try { v.Delete(r.Id); } catch (Exception) { }
        await Svc.State.SaveAsync();
        Reload();
        await ShowToastAsync("Код удалён");
    }

    // ================= toast =================

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
