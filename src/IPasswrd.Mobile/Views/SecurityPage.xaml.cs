using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

public partial class SecurityPage : ContentPage
{
    private Dictionary<string, long>? _breachCounts;   // пароль → число утечек (только найденные)
    private bool _breachRunning;
    private string? _breachStatus;

    public SecurityPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Build();
    }

    private void OnRefresh(object? sender, EventArgs e)
    {
        Build();
        Refresh.IsRefreshing = false;
    }

    // ================= палитра =================

    private static Color C(string dark, string light)
    {
        bool d = Application.Current?.RequestedTheme != AppTheme.Light;
        string key = d ? dark : light;
        return Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : Colors.Gray;
    }
    private static Style CardStyle => (Style)Application.Current!.Resources["Card"];
    private static Style Muted => (Style)Application.Current!.Resources["Muted"];
    private static Style Section => (Style)Application.Current!.Resources["Section"];

    // ================= построение =================

    private void Build()
    {
        Container.Children.Clear();
        Vault? v = Svc.State.Vault;
        if (v is null) return;

        AuditReport rep;
        try { rep = Auditor.Audit(v.Items()); }
        catch (Exception) { return; }

        // ----- обзор -----
        Container.Children.Add(new Label { Text = "ОБЗОР", Style = Section });
        var overview = new VerticalStackLayout { Spacing = 0 };
        overview.Children.Add(StatRow("Проверено аккаунтов", rep.AccountsChecked.ToString(), null));
        overview.Children.Add(Hair());
        overview.Children.Add(StatRow("Надёжных", rep.Ok.ToString(), C("IpOk", "IpOkL")));
        overview.Children.Add(Hair());
        overview.Children.Add(StatRow("Слабых", rep.Weak.Count.ToString(), rep.Weak.Count > 0 ? C("IpWarn", "IpWarnL") : null));
        overview.Children.Add(Hair());
        overview.Children.Add(StatRow("Повторяющихся", DistinctReused(rep.Reused).ToString(), rep.Reused.Count > 0 ? C("IpBad", "IpBadL") : null));
        Container.Children.Add(new Border { Style = CardStyle, Padding = new Thickness(14, 2), Content = overview });

        if (rep.Weak.Count == 0 && rep.Reused.Count == 0 && rep.AccountsChecked > 0)
            Container.Children.Add(new Label
            {
                Text = "Слабых и повторяющихся паролей не найдено 👍",
                Style = Muted, FontSize = 13, Margin = new Thickness(4, 8, 4, 0),
            });

        // ----- слабые -----
        if (rep.Weak.Count > 0)
        {
            Container.Children.Add(new Label { Text = "СЛАБЫЕ ПАРОЛИ", Style = Section });
            Container.Children.Add(FindingsCard(rep.Weak, C("IpWarn", "IpWarnL"), "слабый"));
        }

        // ----- повторяющиеся -----
        if (rep.Reused.Count > 0)
        {
            Container.Children.Add(new Label { Text = "ПОВТОРЯЮЩИЕСЯ ПАРОЛИ", Style = Section });
            Container.Children.Add(FindingsCard(rep.Reused, C("IpBad", "IpBadL"), "повтор"));
        }

        // ----- HIBP -----
        Container.Children.Add(new Label { Text = "ПРОВЕРКА ПО БАЗЕ УТЕЧЕК", Style = Section });
        Container.Children.Add(BuildBreachCard());
    }

    private static int DistinctReused(IReadOnlyList<AuditFinding> reused) => reused.Count;

    private View StatRow(string label, string value, Color? valueColor)
    {
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Padding = new Thickness(0, 10) };
        g.Add(new Label { Text = label, VerticalOptions = LayoutOptions.Center }, 0, 0);
        g.Add(new Label
        {
            Text = value, FontAttributes = FontAttributes.Bold, FontSize = 17,
            TextColor = valueColor ?? C("IpText", "IpTextL"), VerticalOptions = LayoutOptions.Center,
        }, 1, 0);
        return g;
    }

    private BoxView Hair() => new() { HeightRequest = 1, Color = C("IpHair", "IpHairL") };

    private Border FindingsCard(IReadOnlyList<AuditFinding> findings, Color accent, string tag)
    {
        var stack = new VerticalStackLayout { Spacing = 0 };
        bool first = true;
        foreach (AuditFinding f in findings.OrderBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!first) stack.Children.Add(Hair());
            first = false;
            stack.Children.Add(FindingRow(f.Id, f.Title, tag, accent));
        }
        return new Border { Style = CardStyle, Padding = new Thickness(6, 2), Content = stack };
    }

    private View FindingRow(string id, string title, string tag, Color accent)
    {
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Padding = new Thickness(10, 12), ColumnSpacing = 8 };
        g.Add(new Label { Text = title, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center }, 0, 0);
        var pill = new Border
        {
            BackgroundColor = accent.WithAlpha(0.16f), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(8, 3), VerticalOptions = LayoutOptions.Center,
            Content = new Label { Text = tag, TextColor = accent, FontSize = 11, FontAttributes = FontAttributes.Bold },
        };
        g.Add(pill, 1, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Navigation.PushAsync(new ItemDetailPage(id));
        g.GestureRecognizers.Add(tap);
        return g;
    }

    // ================= HIBP =================

    private Border BuildBreachCard()
    {
        var stack = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(14, 12) };
        stack.Children.Add(new Label
        {
            Text = "Have I Been Pwned — крупнейшая база украденных паролей. Проверка k-анонимна: наружу уходят только первые 5 символов SHA-1, сам пароль не передаётся.",
            Style = Muted, FontSize = 12.5,
        });

        var btn = new Button
        {
            Text = _breachRunning ? "Проверяем…" : "Проверить пароли",
            Style = (Style)Application.Current!.Resources["Primary"],
            IsEnabled = !_breachRunning,
        };
        btn.Clicked += OnBreachCheck;
        stack.Children.Add(btn);

        if (_breachStatus is not null)
        {
            stack.Children.Add(new Label
            {
                Text = _breachStatus,
                TextColor = _breachRunning ? C("IpText2", "IpText2L") : C("IpBad", "IpBadL"),
                FontSize = 12.5,
            });
        }
        else if (_breachCounts is not null)
        {
            Vault? v = Svc.State.Vault;
            var hits = (v?.Items() ?? Enumerable.Empty<VaultEntry>())
                .Where(x => x.Item.Type == "account"
                            && x.Item.Fields.TryGetValue("password", out var p) && _breachCounts.ContainsKey(p))
                .OrderByDescending(x => _breachCounts[x.Item.Fields["password"]])
                .ToList();

            if (hits.Count == 0)
                stack.Children.Add(new Label { Text = "Ни один пароль не найден в известных утечках 👍", TextColor = C("IpOk", "IpOkL"), FontSize = 13 });
            else
            {
                stack.Children.Add(new Label
                {
                    Text = "Эти пароли найдены в утечках — их стоит сменить:",
                    TextColor = C("IpBad", "IpBadL"), FontSize = 12.5, FontAttributes = FontAttributes.Bold,
                });
                Color bad = C("IpBad", "IpBadL");
                foreach (VaultEntry e in hits)
                {
                    long n = _breachCounts[e.Item.Fields["password"]];
                    stack.Children.Add(FindingRow(e.Id, e.Item.Title.Length > 0 ? e.Item.Title : "(без названия)",
                        $"в {Hibp.FormatCount(n)} утечках", bad));
                }
            }
        }

        return new Border { Style = CardStyle, Padding = 0, Content = stack };
    }

    private async void OnBreachCheck(object? sender, EventArgs e)
    {
        Vault? v = Svc.State.Vault;
        if (v is null || _breachRunning) return;

        _breachRunning = true;
        _breachStatus = "Проверяем пароли в базе утечек…";
        _breachCounts = null;
        Build();

        try
        {
            var pwds = v.Items()
                .Where(x => x.Item.Type == "account"
                            && x.Item.Fields.TryGetValue("password", out var p) && !string.IsNullOrEmpty(p))
                .Select(x => x.Item.Fields["password"]);
            _breachCounts = await Hibp.CheckAsync(pwds);
            _breachStatus = null;
        }
        catch (Exception)
        {
            _breachStatus = "Не удалось связаться с базой утечек. Проверьте интернет и попробуйте снова.";
        }
        finally
        {
            _breachRunning = false;
            Build();
        }
    }
}
