using System.Collections.ObjectModel;
using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public sealed class VaultRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Star { get; init; } = "";
    public VaultItem Item { get; init; } = new();
}

public sealed class VaultGroup : List<VaultRow>
{
    public string Name { get; }
    public VaultGroup(string name, IEnumerable<VaultRow> rows) : base(rows) => Name = name;
}

public partial class VaultListPage : ContentPage
{
    private static readonly (string Key, string Label)[] ChipDefs =
    {
        ("all", "Все"), ("account", "Аккаунты"), ("card", "Карты"),
        ("document", "Документы"), ("note", "Заметки"), ("passkey", "Ключи доступа"),
    };

    private string _filter = "all";
    private readonly List<Button> _chipButtons = new();

    public VaultListPage()
    {
        InitializeComponent();
        BuildChips();
        Svc.State.VaultChanged += OnVaultChanged;
    }

    private void OnVaultChanged() => MainThread.BeginInvokeOnMainThread(Reload);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Reload();
    }

    private void BuildChips()
    {
        foreach (var (key, label) in ChipDefs)
        {
            var b = new Button
            {
                Text = label,
                FontSize = 14,
                Padding = new Thickness(14, 6),
                CornerRadius = 14,
            };
            b.Clicked += (_, _) => { _filter = key; StyleChips(); Reload(); };
            _chipButtons.Add(b);
            Chips.Children.Add(b);
        }
        StyleChips();
    }

    private void StyleChips()
    {
        bool dark = Application.Current?.RequestedTheme != AppTheme.Light;
        for (int i = 0; i < _chipButtons.Count; i++)
        {
            bool on = ChipDefs[i].Key == _filter;
            _chipButtons[i].BackgroundColor = on
                ? GetColor(dark ? "IpAccent" : "IpAccentL")
                : GetColor(dark ? "IpSurface2" : "IpSurface2L");
            _chipButtons[i].TextColor = on
                ? GetColor(dark ? "IpOnAccent" : "IpOnAccentL")
                : GetColor(dark ? "IpText2" : "IpText2L");
        }
    }

    private static Color GetColor(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : Colors.Gray;

    private void OnSearch(object? sender, TextChangedEventArgs e) => Reload();

    private void Reload()
    {
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        string q = (Search.Text ?? "").Trim().ToLowerInvariant();
        Dictionary<string, string> siteNames = Svc.State.SiteNames();

        List<VaultEntry> all;
        try { all = v.Items().ToList(); }
        catch (Exception) { return; }

        IEnumerable<VaultEntry> visible = all
            .Where(x => x.Item.Type != "totp" && x.Item.Type != "meta")
            .Where(x => _filter == "all" || x.Item.Type == _filter);

        if (q.Length > 0)
            visible = visible.Where(x =>
                x.Item.Title.ToLowerInvariant().Contains(q)
                || x.Item.Fields.GetValueOrDefault("username", "").ToLowerInvariant().Contains(q)
                || x.Item.Fields.GetValueOrDefault("url", "").ToLowerInvariant().Contains(q)
                || x.Item.Notes.ToLowerInvariant().Contains(q));

        var rows = visible.Select(MakeRow).ToList();

        var groups = new List<VaultGroup>();

        // Избранное — сверху, отдельной группой.
        var fav = rows.Where(r => r.Item.Favorite)
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (fav.Count > 0) groups.Add(new VaultGroup("★ Избранное", fav));

        var rest = rows.Where(r => !r.Item.Favorite).ToList();

        // Аккаунты — группами по сайту (регистрируемый домен, имя из meta если задано), как на ПК.
        var accounts = rest.Where(r => r.Item.Type == "account")
            .GroupBy(r => SiteGroups.KeyFor(r.Item))
            .OrderBy(g => SiteGroups.DisplayName(g.Key, siteNames), StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in accounts)
            groups.Add(new VaultGroup(SiteGroups.DisplayName(g.Key, siteNames),
                g.OrderBy(r => r.Subtitle, StringComparer.CurrentCultureIgnoreCase)));

        AddTypeGroup(groups, rest, "card", "Карты");
        AddTypeGroup(groups, rest, "document", "Документы");
        AddTypeGroup(groups, rest, "note", "Заметки");
        AddTypeGroup(groups, rest, "passkey", "Ключи доступа");
        AddTypeGroup(groups, rest, null, "Прочее");

        List.ItemsSource = new ObservableCollection<VaultGroup>(groups);
    }

    private static readonly HashSet<string> KnownTypes = new() { "account", "card", "document", "note", "passkey" };

    private static void AddTypeGroup(List<VaultGroup> groups, List<VaultRow> rest, string? type, string title)
    {
        var items = rest
            .Where(r => type is null ? !KnownTypes.Contains(r.Item.Type) : r.Item.Type == type)
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (items.Count > 0) groups.Add(new VaultGroup(title, items));
    }

    private static VaultRow MakeRow(VaultEntry e)
    {
        VaultItem it = e.Item;
        string subtitle = it.Type switch
        {
            "account" => it.Fields.GetValueOrDefault("username", ""),
            "card" => $"{Fmt.CardBrand(it.Fields.GetValueOrDefault("number", ""))} {Fmt.MaskCard(it.Fields.GetValueOrDefault("number", ""))}".Trim(),
            "document" => it.Fields.GetValueOrDefault("number", ""),
            "passkey" => it.Fields.GetValueOrDefault("username", ""),
            _ => it.Notes.Split('\n').FirstOrDefault() ?? "",
        };
        string badge = it.Type switch
        {
            "card" => "💳",
            "document" => "📄",
            "note" => "🗒",
            "passkey" => "🔑",
            _ => it.Title.Length > 0 ? it.Title[..1].ToUpperInvariant() : "•",
        };
        return new VaultRow
        {
            Id = e.Id,
            Title = it.Title.Length > 0 ? it.Title : "(без названия)",
            Subtitle = subtitle,
            Badge = badge,
            Star = it.Favorite ? "★" : "",
            Item = it,
        };
    }

    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is VaultRow row)
            await Navigation.PushAsync(new ItemDetailPage(row.Id));
    }

    private async void OnAdd(object? sender, EventArgs e)
    {
        string? choice = await DisplayActionSheet("Добавить", "Отмена", null,
            "Аккаунт", "Банковскую карту", "Документ", "Заметку");
        string? type = choice switch
        {
            "Аккаунт" => "account",
            "Банковскую карту" => "card",
            "Документ" => "document",
            "Заметку" => "note",
            _ => null,
        };
        if (type is not null)
            await Navigation.PushAsync(new ItemEditPage(null, type));
    }
}
