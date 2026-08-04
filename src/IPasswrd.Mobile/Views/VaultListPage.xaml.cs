using System.Collections.ObjectModel;
using System.Text;
using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

/// <summary>Строка списка: одна карточка — один сайт (для аккаунтов и ключей доступа)
/// или одна запись (карты, документы, заметки). Ids — все записи карточки по порядку.</summary>
public sealed class VaultRow
{
    public List<string> Ids { get; init; } = new();
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool HasSubtitle => Subtitle.Length > 0;
    public string Badge { get; init; } = "";
    public string Star { get; init; } = "";
    public bool Fav { get; init; }
    public Thickness Pad { get; set; } = new(16, 5);
}

public partial class VaultListPage : ContentPage
{
    private static readonly (string Key, string Label)[] ChipDefs =
    {
        ("all", "Все"), ("account", "Аккаунты"), ("card", "Карты"),
        ("document", "Документы"), ("note", "Заметки"), ("passkey", "Ключи доступа"),
    };

    private string _filter = "all";

    public VaultListPage()
    {
        InitializeComponent();
        UpdateSectionButton();
        Svc.State.VaultChanged += OnVaultChanged;

        // Клавиатура поиска закрывается по «Найти» и при прокрутке списка.
        Search.SearchButtonPressed += (_, _) => Search.Unfocus();
        List.Scrolled += (_, _) => { if (Search.IsFocused) Search.Unfocus(); };
    }

    private void OnVaultChanged() => MainThread.BeginInvokeOnMainThread(Reload);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Reload();
    }

    // ===== выбор раздела: одна кнопка с текущим разделом (как в Kaspersky), по тапу — меню =====

    /// <summary>Количество записей в каждом разделе (без totp и meta).</summary>
    private Dictionary<string, int> SectionCounts()
    {
        var counts = ChipDefs.ToDictionary(d => d.Key, _ => 0);
        Vault? v = Svc.State.Vault;
        if (v is null) return counts;
        try
        {
            foreach (VaultEntry e in v.Items())
            {
                string t = e.Item.Type;
                if (t is "totp" or "meta") continue;
                counts["all"]++;
                if (counts.ContainsKey(t)) counts[t]++;
            }
        }
        catch (Exception) { }
        return counts;
    }

    private static string LabelFor(string key, string label, IReadOnlyDictionary<string, int> counts) =>
        counts.GetValueOrDefault(key) > 0 ? $"{label} ({counts[key]})" : label;

    private void UpdateSectionButton()
    {
        var counts = SectionCounts();
        var (_, label) = ChipDefs.First(d => d.Key == _filter);
        SectionLabel.Text = LabelFor(_filter, label, counts);
    }

    private async void OnSectionMenu(object? sender, EventArgs e)
    {
        var counts = SectionCounts();
        string[] options = ChipDefs
            .Select(d => (d.Key == _filter ? "✓ " : "") + LabelFor(d.Key, d.Label, counts))
            .ToArray();
        string? choice = await DisplayActionSheet("Показать", "Отмена", null, options);
        if (choice is null) return;
        int idx = Array.IndexOf(options, choice);
        if (idx < 0 || ChipDefs[idx].Key == _filter) return;
        _filter = ChipDefs[idx].Key;
        UpdateSectionButton();
        Reload();
    }

    private void OnSearch(object? sender, TextChangedEventArgs e) => Reload();

    private void Reload()
    {
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        UpdateSectionButton();   // счётчики на кнопке раздела обновляются вместе со списком

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
                || x.Item.Notes.ToLowerInvariant().Contains(q)
                || (x.Item.Type is "account" or "passkey"
                    && SiteGroups.DisplayName(SiteGroups.KeyFor(x.Item), siteNames).ToLowerInvariant().Contains(q)));

        var list = visible.ToList();

        // Избранное — сверху, как на ПК: карточка (группа сайта) поднимается целиком,
        // если в ней есть хоть одна избранная запись; внутри карточки избранные — первыми.
        var allRows = BuildCards(list, siteNames);
        var favRows = allRows.Where(r => r.Fav).ToList();
        var restRows = allRows.Where(r => !r.Fav).ToList();
        if (favRows.Count > 0 && restRows.Count > 0)
            restRows[0].Pad = new Thickness(16, 21, 16, 5);

        var rows = new List<VaultRow>(favRows.Count + restRows.Count);
        rows.AddRange(favRows);
        rows.AddRange(restRows);

        List.ItemsSource = new ObservableCollection<VaultRow>(rows);
    }

    /// <summary>Карточки: аккаунты и ключи доступа схлопываются по сайту (название сайта на карточке),
    /// остальные типы — по одной записи на карточку.</summary>
    private static List<VaultRow> BuildCards(List<VaultEntry> entries, Dictionary<string, string> siteNames)
    {
        var rows = new List<VaultRow>();

        // Одна карточка на сайт.
        var siteCards = entries
            .Where(x => x.Item.Type == "account")
            .GroupBy(x => SiteGroups.KeyFor(x.Item))
            .OrderBy(g => SiteGroups.DisplayName(g.Key, siteNames), StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in siteCards)
            rows.Add(MakeSiteCard(g.Key, g.ToList(), siteNames, passkeys: false));

        AddSingles(rows, entries, "card", "💳",
            it => $"{Fmt.CardBrand(it.Fields.GetValueOrDefault("number", ""))} {Fmt.MaskCard(it.Fields.GetValueOrDefault("number", ""))}".Trim());
        AddSingles(rows, entries, "document", "📄", it => it.Fields.GetValueOrDefault("number", ""));
        AddSingles(rows, entries, "note", "🗒", it => it.Notes.Split('\n').FirstOrDefault() ?? "");

        var passkeyCards = entries
            .Where(x => x.Item.Type == "passkey")
            .GroupBy(x => SiteGroups.KeyFor(x.Item))
            .OrderBy(g => SiteGroups.DisplayName(g.Key, siteNames), StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in passkeyCards)
            rows.Add(MakeSiteCard(g.Key, g.ToList(), siteNames, passkeys: true));

        AddSingles(rows, entries, null, "•", it => it.Notes.Split('\n').FirstOrDefault() ?? "");

        return rows;
    }

    private static VaultRow MakeSiteCard(string key, List<VaultEntry> members, Dictionary<string, string> siteNames, bool passkeys)
    {
        members = members
            .OrderByDescending(x => x.Item.Favorite)
            .ThenBy(x => x.Item.Fields.GetValueOrDefault("username", ""), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        string name = SiteGroups.DisplayName(key, siteNames);
        if (name.Length == 0) name = "(без сайта)";

        string subtitle;
        if (members.Count == 1)
        {
            VaultItem it = members[0].Item;
            subtitle = it.Fields.GetValueOrDefault("username", "");
            if (subtitle.Length == 0
                && it.Title.Length > 0
                && !it.Title.Equals(name, StringComparison.OrdinalIgnoreCase))
                subtitle = it.Title;
            if (passkeys)
                subtitle = subtitle.Length > 0 ? subtitle + " · ключ доступа" : "ключ доступа";
        }
        else
        {
            subtitle = passkeys
                ? Plural(members.Count, "ключ доступа", "ключа доступа", "ключей доступа")
                : Plural(members.Count, "аккаунт", "аккаунта", "аккаунтов");
        }

        return new VaultRow
        {
            Ids = members.Select(m => m.Id).ToList(),
            Title = name,
            Subtitle = subtitle,
            Badge = passkeys ? "🔑" : FirstLetter(name),
            Star = members.Any(m => m.Item.Favorite) ? "★" : "",
            Fav = members.Any(m => m.Item.Favorite),
        };
    }

    private static readonly HashSet<string> KnownTypes = new() { "account", "card", "document", "note", "passkey" };

    private static void AddSingles(List<VaultRow> rows, List<VaultEntry> entries, string? type, string badge, Func<VaultItem, string> subtitleOf)
    {
        var items = entries
            .Where(x => type is null ? !KnownTypes.Contains(x.Item.Type) : x.Item.Type == type)
            .OrderBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase);
        foreach (var e in items)
        {
            VaultItem it = e.Item;
            rows.Add(new VaultRow
            {
                Ids = new List<string> { e.Id },
                Title = it.Title.Length > 0 ? it.Title : "(без названия)",
                Subtitle = subtitleOf(it),
                Badge = badge,
                Star = it.Favorite ? "★" : "",
                Fav = it.Favorite,
            });
        }
    }

    private static string FirstLetter(string s)
    {
        foreach (Rune r in s.EnumerateRunes())
            return r.ToString().ToUpperInvariant();
        return "•";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        string w = m10 == 1 && m100 != 11 ? one
            : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few
            : many;
        return $"{n} {w}";
    }

    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is VaultRow row && row.Ids.Count > 0)
            await Navigation.PushAsync(new ItemDetailPage(row.Ids, 0));
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
