using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Поиск отвечал «ничего не найдено» про записи, которые лежали в сейфе, — просто смотрел на три
// поля из десяти. Эти тесты держат две границы: по чему искать можно (почти по всему) и по чему
// нельзя (пароль, CVC, машинный текст ключей доступа).
public class ItemSearchTests
{
    private static VaultItem Card() => new()
    {
        Type = ItemTypes.Card,
        Title = "Сбербанк",
        Folder = "Финансы",
        Notes = "зарплатная",
        Fields =
        {
            ["number"] = "5469380012345678",
            ["holder"] = "IVAN IVANOV",
            ["expiry"] = "09/29",
            ["cvc"] = "731",
        },
    };

    private static VaultItem Identity() => new()
    {
        Type = ItemTypes.Identity,
        Title = "Домашний адрес",
        Fields =
        {
            ["lastName"] = "Иванов",
            ["firstName"] = "Иван",
            ["street"] = "Ленинградский проспект, 15",
            ["city"] = "Москва",
            ["zip"] = "125040",
            ["email"] = "ivan@example.com",
        },
    };

    [Fact] // 1
    public void CardIsFoundByHolder()
    {
        Assert.True(ItemSearch.Matches(Card(), "ivanov"));
    }

    [Fact] // 2
    public void AddressIsFoundByStreet()
    {
        Assert.True(ItemSearch.Matches(Identity(), "ленинградский"));
    }

    [Fact] // 3
    public void FoundByFolder()
    {
        Assert.True(ItemSearch.Matches(Card(), "финансы"));
    }

    [Fact] // 4
    public void FoundByNotes()
    {
        Assert.True(ItemSearch.Matches(Card(), "зарплатная"));
    }

    [Fact] // 5
    public void CaseDoesNotMatter()
    {
        Assert.True(ItemSearch.Matches(Identity(), "МОСКВА"));
        Assert.True(ItemSearch.Matches(Card(), "IvAnOv"));
    }

    // Совпадение внутри пароля выдало бы запись, которую не искали, и объяснить её появление
    // в списке было бы нечем: поля, по которому она нашлась, на экране не видно.
    [Fact] // 6
    public void PasswordIsNotSearched()
    {
        var it = new VaultItem { Title = "Почта", Fields = { ["password"] = "уникальнаястрока" } };

        Assert.False(ItemSearch.Matches(it, "уникальнаястрока"));
    }

    [Fact] // 7
    public void CvcIsNotSearched()
    {
        Assert.False(ItemSearch.Matches(Card(), "731"));
    }

    [Fact] // 8
    public void PasskeyInternalsAreNotSearched()
    {
        var it = new VaultItem
        {
            Type = ItemTypes.Passkey,
            Title = "GitHub",
            Fields = { ["privJwk"] = "eyJrdHkiOiJFQyJ9", ["credId"] = "AAECAwQFBgc", ["userHandle"] = "ZGVtbw" },
        };

        Assert.False(ItemSearch.Matches(it, "eyjrdhki"));
        Assert.False(ItemSearch.Matches(it, "aaecawqfbgc"));
        Assert.True(ItemSearch.Matches(it, "github"));
    }

    // Номер карты искать можно: он написан на самой карте, и найти запись по последним цифрам —
    // самый частый способ понять, какая из двух карт нужна.
    [Fact] // 9
    public void CardNumberIsSearchable()
    {
        Assert.True(ItemSearch.Matches(Card(), "5678"));
    }

    // Без этого расширенный поиск бесполезен там, где нужнее всего: «сбер» — из названия,
    // «ivanov» — из держателя, и целиком такая строка не встречается ни в одном поле.
    [Fact] // 10
    public void WordsMayComeFromDifferentFields()
    {
        Assert.True(ItemSearch.Matches(Card(), "сбер ivanov"));
    }

    [Fact] // 11
    public void WordOrderDoesNotMatter()
    {
        Assert.True(ItemSearch.Matches(Identity(), "иван иванов"));
        Assert.True(ItemSearch.Matches(Identity(), "иванов иван"));
    }

    [Fact] // 12
    public void EveryWordMustBeFound()
    {
        Assert.False(ItemSearch.Matches(Card(), "сбер альфа"));
    }

    [Fact] // 13
    public void ExtraSpacesAreIgnored()
    {
        Assert.True(ItemSearch.Matches(Card(), "  сбер   ivanov  "));
    }

    // Половина слова тоже находит: человек не помнит запись целиком, иначе он бы её не искал.
    [Fact] // 14
    public void PrefixOfAWordIsEnough()
    {
        Assert.True(ItemSearch.Matches(Card(), "сберб"));
        Assert.True(ItemSearch.Matches(Identity(), "москв"));
    }

    [Fact] // 15
    public void EmptyQueryMatchesEverything()
    {
        Assert.True(ItemSearch.Matches(Card(), ""));
        Assert.True(ItemSearch.Matches(Card(), "   "));
        Assert.True(ItemSearch.Matches(Card(), null));
    }

    // Название карточки сайта собирается из группы записей и в самой записи не хранится, но на
    // экране человек видит именно его.
    [Fact] // 16
    public void ExtraTextIsSearchedToo()
    {
        var it = new VaultItem { Title = "ivan@example.com", Fields = { ["url"] = "https://mail.example.com" } };

        Assert.False(ItemSearch.Matches(it, "почта"));
        Assert.True(ItemSearch.Matches(it, "почта", "Почта Example"));
        Assert.True(ItemSearch.Matches(it, "почта ivan", "Почта Example"));
    }

    [Fact] // 17
    public void UnknownFieldsAreSearchable()
    {
        // Импорт из чужого менеджера кладёт незнакомые поля как есть — искать по ним нужно,
        // иначе перенесённая запись оказывается менее находимой, чем заведённая вручную.
        var it = new VaultItem { Title = "Полис", Fields = { ["policyNumber"] = "ЕЕЕ1234567890" } };

        Assert.True(ItemSearch.Matches(it, "еее12345"));
    }

    [Fact] // 18
    public void SearchableFlagMatchesTheSkipList()
    {
        Assert.False(ItemSearch.IsSearchable("password"));
        Assert.False(ItemSearch.IsSearchable("PASSWORD"));
        Assert.False(ItemSearch.IsSearchable("cvc"));
        Assert.False(ItemSearch.IsSearchable("totp"));
        Assert.False(ItemSearch.IsSearchable(""));
        Assert.False(ItemSearch.IsSearchable(null));
        Assert.True(ItemSearch.IsSearchable("username"));
        Assert.True(ItemSearch.IsSearchable("holder"));
    }
}
