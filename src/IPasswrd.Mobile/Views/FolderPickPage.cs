using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

/// <summary>
/// Экран выбора папок записи. Папок может быть несколько, поэтому это НЕ выбор одной, а
/// переключатели: отметил нужные, создал новую — и всё разом. В отличие от системного
/// ActionSheet (закрывался после каждого тапа — на Android положить запись в три папки
/// значило открыть лист трижды), здесь всё в одном экране.
///
/// Меняем список прямо в переданной записи (та же ссылка, что в форме редактирования);
/// в сейф уедет с кнопкой «Сохранить» в форме, как и остальные поля.
/// </summary>
public sealed class FolderPickPage : ContentPage
{
    private readonly VaultItem _item;
    private readonly VerticalStackLayout _list = new() { Spacing = 0 };
    private readonly Entry _new = new() { Placeholder = "Новая папка", ReturnType = ReturnType.Done, MaxLength = 40 };

    public FolderPickPage(VaultItem item)
    {
        _item = item;
        Title = "Папки записи";

        var addBtn = new Button { Text = "Добавить", WidthRequest = 120 };
        ApplyStyle(addBtn, "Primary");
        addBtn.Clicked += (_, _) => AddNew();
        _new.Completed += (_, _) => AddNew();

        var newRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
        newRow.Add(Card(_new), 0, 0);
        newRow.Add(addBtn, 1, 0);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 14),
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Отметьте папки, в которых должна быть запись. Их может быть несколько.",
                                FontSize = 14, Opacity = 0.75 },
                    newRow,
                    new Label { Text = "ПАПКИ", Style = SectionStyle() },
                    Card(_list),
                },
            },
        };

        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();

        var known = new List<string>();
        try
        {
            Vault? v = Svc.State.Vault;
            if (v is not null)
                known = v.Items()
                    .SelectMany(x => ItemFolders.Of(x.Item))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
        }
        catch (Exception) { }

        // Папки самой записи показываем, даже если во всём сейфе она в них пока одна.
        foreach (string f in ItemFolders.Of(_item))
            if (!known.Contains(f, StringComparer.Ordinal)) known.Add(f);

        known = known.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();

        if (known.Count == 0)
        {
            _list.Children.Add(new Label
            {
                Text = "Пока нет ни одной папки — создайте первую выше.",
                Style = MutedStyle(), FontSize = 13, Margin = new Thickness(14, 12),
            });
            return;
        }

        bool first = true;
        foreach (string folder in known)
        {
            if (!first) _list.Children.Add(Hairline());
            first = false;

            var row = new Grid
            {
                ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
                Padding = new Thickness(14, 8),
                ColumnSpacing = 12,
            };
            row.Add(new Label { Text = folder, VerticalOptions = LayoutOptions.Center }, 0, 0);

            var sw = new Switch { IsToggled = ItemFolders.In(_item, folder) };
            string captured = folder;
            sw.Toggled += (_, e) =>
            {
                if (e.Value) ItemFolders.Add(_item, captured);
                else ItemFolders.Remove(_item, captured);
            };
            row.Add(sw, 1, 0);
            _list.Children.Add(row);
        }
    }

    private void AddNew()
    {
        string name = (_new.Text ?? "").Trim().Trim(',');   // запятая — разделитель папок на ПК, в имени ей не место
        if (name.Length == 0) return;
        ItemFolders.Add(_item, name);   // создаётся и сразу отмечается
        _new.Text = "";
        Rebuild();
    }

    // ================= стили из ресурсов =================

    private static View Card(View inner)
    {
        var b = new Border { Padding = new Thickness(14, 4), StrokeThickness = 0, Content = inner };
        ApplyStyle(b, "Card");
        return b;
    }

    private static View Hairline()
    {
        var bv = new BoxView { HeightRequest = 1 };
        if (Application.Current?.Resources.TryGetValue("IpHair", out var c) == true && c is Color color)
            bv.Color = color;
        return bv;
    }

    private static Style? MutedStyle() =>
        Application.Current?.Resources.TryGetValue("Muted", out var s) == true && s is Style st ? st : null;

    private static Style? SectionStyle() =>
        Application.Current?.Resources.TryGetValue("Section", out var s) == true && s is Style st ? st : null;

    private static void ApplyStyle(VisualElement el, string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var s) == true && s is Style st) el.Style = st;
    }
}
