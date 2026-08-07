using IPasswrd.Core;
using IPasswrd.Core.Import;
using Xunit;

namespace IPasswrd.Core.Tests;

// Экспорт — это обещание, что из сейфа можно уйти. Тесты держат две его части: файл должен
// читаться другими менеджерами (и нашим же импортом), и из него ничего не должно тихо
// пропадать по дороге.
public class ExportTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Account(string title, string user, string pass, string url = "", string note = "")
    {
        var it = new VaultItem { Type = "account", Title = title, Notes = note };
        if (url.Length > 0) it.Fields["url"] = url;
        it.Fields["username"] = user;
        it.Fields["password"] = pass;
        return it;
    }

    [Fact] // 1
    public void HeaderMatchesWhatBrowsersWrite()
    {
        // Ровно те же колонки, что в экспорте Chrome: иначе чужой импорт не сопоставит поля.
        string csv = Exporter.ToCsv(Array.Empty<VaultEntry>());
        Assert.Equal("name,url,username,password,note,totp\r\n", csv);
    }

    [Fact] // 2
    public void CommasQuotesAndNewlinesSurviveTheRoundTrip()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("Ozon, но с запятой", "me@example.com", "a\"b,c\nd", "ozon.ru"));

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ozon, но с запятой", rows[1][0]);
        Assert.Equal("a\"b,c\nd", rows[1][3]);
    }

    [Fact] // 3
    public void ExportedFileImportsBackWithTheSameLogins()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("Ozon", "me@example.com", "hunter2", "ozon.ru"));
        v.Add(Account("Госуслуги", "+79990000000", "p@ss,word", "gosuslugi.ru"));

        List<VaultItem> back = Importer.Parse(Exporter.ToCsv(v.Items()));

        Assert.Equal(2, back.Count);
        Assert.Contains(back, x => x.Fields.GetValueOrDefault("password") == "hunter2");
        Assert.Contains(back, x => x.Fields.GetValueOrDefault("password") == "p@ss,word");
    }

    [Fact] // 4
    public void TotpTravelsInItsOwnColumn()
    {
        var v = Vault.Create(Pw, Fast);
        var it = Account("Google", "me@gmail.com", "hunter2", "google.com");
        it.Fields["totp"] = "JBSWY3DPEHPK3PXP";
        v.Add(it);

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        Assert.Equal("JBSWY3DPEHPK3PXP", rows[1][5]);
    }

    [Fact] // 5
    public void CardFieldsGoIntoTheNoteInsteadOfDisappearing()
    {
        // У карты нет колонки под номер и срок. Молча их потерять - худший из вариантов.
        var v = Vault.Create(Pw, Fast);
        var card = new VaultItem { Type = "card", Title = "Тинькофф" };
        card.Fields["number"] = "4111111111111111";
        card.Fields["expiry"] = "12/29";
        v.Add(card);

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        string note = rows[1][4];

        Assert.Contains("4111111111111111", note);
        Assert.Contains("12/29", note);
    }

    [Fact] // 6
    public void FolderIsMentionedSoTheGroupingIsNotLostSilently()
    {
        var v = Vault.Create(Pw, Fast);
        var it = Account("Ozon", "me@example.com", "hunter2", "ozon.ru");
        it.Folder = "Покупки";
        v.Add(it);

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        Assert.Contains("Покупки", rows[1][4]);
    }

    [Fact] // 7
    public void AttachmentsAreAnnouncedRatherThanDroppedInSilence()
    {
        var v = Vault.Create(Pw, Fast);
        var doc = new VaultItem { Type = "document", Title = "Паспорт" };
        doc.Attachments.Add(new Attachment
        {
            Name = "scan.jpg", Mime = "image/jpeg", Bytes = 3,
            Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            AddedAt = "2026-08-07T09:00:00Z",
        });
        v.Add(doc);

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        string note = rows[1][4];

        Assert.Contains("Вложений: 1", note);
        // Сами байты в текстовом файле делать нечего - там только упоминание.
        Assert.DoesNotContain(Convert.ToBase64String(new byte[] { 1, 2, 3 }), note);
    }

    [Fact] // 8
    public void ServiceRecordStaysBehind()
    {
        // Запись meta - это настройки синхронизации, а не то, что человек заводил руками.
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("Ozon", "me@example.com", "hunter2", "ozon.ru"));
        v.Add(new VaultItem { Type = "meta", Title = "sync" });

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        Assert.Equal(2, rows.Count);   // заголовок + одна запись
    }

    [Fact] // 9
    public void UserNotesComeBeforeTheGeneratedLines()
    {
        var v = Vault.Create(Pw, Fast);
        var it = Account("Ozon", "me@example.com", "hunter2", "ozon.ru", note: "любимый магазин");
        it.Folder = "Покупки";
        v.Add(it);

        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        Assert.StartsWith("любимый магазин", rows[1][4]);
    }

    [Fact] // 10
    public void EmptyVaultStillProducesAUsableFile()
    {
        var v = Vault.Create(Pw, Fast);
        List<List<string>> rows = Csv.Parse(Exporter.ToCsv(v.Items()));
        Assert.Single(rows);
        Assert.Equal(Exporter.Header, rows[0]);
    }
}
