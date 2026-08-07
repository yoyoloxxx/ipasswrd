using IPasswrd.Core;
using IPasswrd.Core.Import;
using Xunit;

namespace IPasswrd.Core.Tests;

// Экспорт Kaspersky — это не только логины. Карты и адреса раньше превращались в аккаунты
// с одним названием: номер карты и адрес доставки исчезали при переезде молча. Эти тесты
// держат обещание, ради которого импорт вообще существует — увезти всё.
public class KasperskyImportTests
{
    private const string RussianExport = """
        Приложения

        Заметки

        Название: Код от домофона
        Текст: 24В7

        Банковские карты

        Название: Тинькофф Блэк
        Номер карты: 5536 9138 1234 5678
        Срок действия: 12/29
        Код безопасности: 123
        Держатель: IVAN PETROV
        ПИН: 4321

        Адреса

        Имя: Дом
        Фамилия: Петров
        Имя пользователя: Иван
        Телефон: +7 900 123-45-67
        Электронная почта: ivan.petrov@mail.ru
        Почтовый индекс: 190000
        Страна: Россия
        Город: Санкт-Петербург
        Адрес: Невский проспект, 28

        Веб-сайты

        Название: Ozon
        Адрес сайта: https://ozon.ru
        Логин: ivan.petrov@mail.ru
        Пароль: hunter2
        """;

    private const string EnglishExport = """
        Bank cards

        Name: Visa
        Card number: 4111111111111111
        Expiration date: 01/30
        CVC: 999
        Cardholder: IVAN PETROV

        Addresses

        Last name: Petrov
        First name: Ivan
        Phone: +7 900 000-00-00
        E-mail: ivan@example.com
        Postal code: 101000
        Country: Russia
        City: Moscow
        Address: Tverskaya 1
        """;

    [Fact] // 1
    public void CardComesInAsACardWithItsNumber()
    {
        List<VaultItem> items = Importer.Parse(RussianExport);
        VaultItem card = Assert.Single(items, x => x.Type == ItemTypes.Card);

        Assert.Equal("Тинькофф Блэк", card.Title);
        Assert.Equal("5536913812345678", card.Fields["number"]);   // без пробелов — как ждёт автозаполнение
        Assert.Equal("12/29", card.Fields["expiry"]);
        Assert.Equal("123", card.Fields["cvc"]);
        Assert.Equal("IVAN PETROV", card.Fields["holder"]);
    }

    [Fact] // 2
    public void PinHasNoFieldOfItsOwnAndSoLandsInTheNote()
    {
        // Поля под ПИН у нас нет. Выбор между «потерять» и «положить в заметку» — не выбор.
        VaultItem card = Importer.Parse(RussianExport).Single(x => x.Type == ItemTypes.Card);
        Assert.Contains("4321", card.Notes);
    }

    [Fact] // 3
    public void AddressBecomesPersonalDetails()
    {
        VaultItem id = Assert.Single(Importer.Parse(RussianExport), x => x.Type == ItemTypes.Identity);

        Assert.Equal("Петров", id.Fields["lastName"]);
        Assert.Equal("+7 900 123-45-67", id.Fields["phone"]);
        Assert.Equal("ivan.petrov@mail.ru", id.Fields["email"]);
        Assert.Equal("190000", id.Fields["zip"]);
        Assert.Equal("Россия", id.Fields["country"]);
        Assert.Equal("Санкт-Петербург", id.Fields["city"]);
        Assert.Equal("Невский проспект, 28", id.Fields["street"]);
    }

    [Fact] // 4
    public void RecordNameWinsOverTheAssembledFullName()
    {
        // В «Адресах» поле «Имя» — это название записи («Дом»), а не имя человека.
        VaultItem id = Importer.Parse(RussianExport).Single(x => x.Type == ItemTypes.Identity);
        Assert.Equal("Дом", id.Title);
    }

    [Fact] // 5
    public void EnglishExportReadsTheSame()
    {
        List<VaultItem> items = Importer.Parse(EnglishExport);

        VaultItem card = Assert.Single(items, x => x.Type == ItemTypes.Card);
        Assert.Equal("4111111111111111", card.Fields["number"]);

        VaultItem id = Assert.Single(items, x => x.Type == ItemTypes.Identity);
        Assert.Equal("Petrov Ivan", id.Title);            // названия нет — собрано из ФИО
        Assert.Equal("Tverskaya 1", id.Fields["street"]);
        Assert.Equal("Moscow", id.Fields["city"]);
    }

    [Fact] // 6
    public void LoginsAndNotesStillWork()
    {
        List<VaultItem> items = Importer.Parse(RussianExport);

        VaultItem acc = Assert.Single(items, x => x.Type == ItemTypes.Account);
        Assert.Equal("hunter2", acc.Fields["password"]);
        Assert.Equal("https://ozon.ru", acc.Fields["url"]);

        VaultItem note = Assert.Single(items, x => x.Type == ItemTypes.Note);
        Assert.Equal("Код от домофона", note.Title);
        Assert.Contains("24В7", note.Notes);
    }

    [Fact] // 7
    public void UnknownFieldOfAnAccountIsKeptToo()
    {
        // Чужой экспорт может нести поле, которого мы не знаем. Пропасть оно не должно.
        const string withOddField = """
            Веб-сайты

            Название: Банк
            Логин: ivan
            Пароль: hunter2
            Секретный вопрос: девичья фамилия матери
            """;

        VaultItem acc = Assert.Single(Importer.Parse(withOddField));
        Assert.Contains("девичья фамилия матери", acc.Notes);
    }
}
