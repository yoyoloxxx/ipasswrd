using IPasswrd.Core;
using IPasswrd.Core.Import;
using Xunit;

namespace IPasswrd.Core.Tests;

public class ImportTests
{
    // Chrome / Edge / Yandex all export the Chromium CSV shape: name,url,username,password[,note].
    private const string ChromiumCsv =
        "name,url,username,password,note\n" +
        "GitHub,https://github.com/,glebdev,\"p@ss,word\",work account\n" +   // comma inside quoted password
        "Yandex,https://yandex.ru/,gleb.lavr,zima2022,\n";

    // Older / minimal Chromium export without the note column (also what Yandex Browser emits).
    private const string ChromiumCsvNoNote =
        "name,url,username,password\n" +
        "Ozon,https://ozon.ru/,+7 921,ozon12345\n";

    // Apple Passwords / iCloud Passwords export: Title, URL, Username, Password, Notes, OTPAuth.
    private const string AppleCsv =
        "Title,URL,Username,Password,Notes,OTPAuth\n" +
        "GitHub,https://github.com,glebdev,K#94vRq7,work note,otpauth://totp/GitHub?secret=GEZDGNBV\n";

    private const string KasperskyTxt =
        "Websites\n\n" +
        "Website name: Gmail\n" +
        "Website URL: https://mail.google.com\n" +
        "Login: gleb.hse@gmail.com\n" +
        "Password: s3cr3t!\n" +
        "Comment: personal\n\n" +
        "Website name: Habr\n" +
        "Website URL: https://habr.com\n" +
        "Login: gleb\n" +
        "Password: habrapass\n\n" +
        "Notes\n\n" +
        "Name: Wi-Fi\n" +
        "Text: SSID Lavrentev / pass 9armaturaKot\n";

    // The REAL Kaspersky Password Manager text export shape, verified against a live export:
    //  - entries divided by "---" separator lines
    //  - a "Login name:" label that is left blank while the real credential is on "Login:"
    //  - a Note whose "Text:" body spans many lines (a numbered list), only the first line carrying the key
    private const string KasperskyRealShape =
        "Websites\n\n" +
        "Website name: Gosuslugi\n" +
        "Website URL: https://gosuslugi.ru/\n" +
        "Login name: \n" +
        "Login: user@example.com\n" +
        "Password: Alpha123!\n" +
        "Comment: \n\n" +
        "---\n\n" +
        "Website name: Wildberries\n" +
        "Website URL: https://www.wildberries.ru/\n" +
        "Login name: \n" +
        "Login: +79990001122\n" +
        "Password: Bravo456!\n" +
        "Comment: \n\n" +
        "---\n\n" +
        "Notes\n\n" +
        "Name: Checklist\n" +
        "Text: 1. alpha\n" +
        "2. bravo\n" +
        "3. charlie\n" +
        "4. delta\n\n" +
        "---\n";

    [Fact]
    public void Kaspersky_RealShape_Separators_LoginName_And_MultilineNote()
    {
        var items = Importer.Parse(KasperskyRealShape);
        var accounts = items.Where(i => i.Type == "account").ToList();
        var notes = items.Where(i => i.Type == "note").ToList();

        // "---" separators must not create phantom records.
        Assert.Equal(2, accounts.Count);
        Assert.Single(notes);

        // The credential comes from "Login:", never the blank "Login name:".
        var gos = accounts.First(a => a.Title == "Gosuslugi");
        Assert.Equal("user@example.com", gos.Fields["username"]);
        Assert.Equal("Alpha123!", gos.Fields["password"]);
        Assert.Equal("https://gosuslugi.ru/", gos.Fields["url"]);
        Assert.False(gos.Fields.ContainsKey("note"));    // empty Comment -> no note field

        // The whole multi-line note body survives (the bug dropped everything after line 1).
        Assert.Equal("Checklist", notes[0].Title);
        Assert.Contains("1. alpha", notes[0].Notes);
        Assert.Contains("2. bravo", notes[0].Notes);
        Assert.Contains("3. charlie", notes[0].Notes);
        Assert.Contains("4. delta", notes[0].Notes);
    }

    [Fact]
    public void Detects_Chromium_And_Kaspersky()
    {
        Assert.Equal(ImportFormat.ChromiumCsv, Importer.Detect(ChromiumCsv));
        Assert.Equal(ImportFormat.ChromiumCsv, Importer.Detect(ChromiumCsvNoNote));
        Assert.Equal(ImportFormat.KasperskyTxt, Importer.Detect(KasperskyTxt));
    }

    [Fact]
    public void Chromium_Csv_Parses_Rows_And_Quoted_Commas()
    {
        var items = Importer.Parse(ChromiumCsv);
        Assert.Equal(2, items.Count);

        var gh = items[0];
        Assert.Equal("GitHub", gh.Title);
        Assert.Equal("account", gh.Type);
        Assert.Equal("glebdev", gh.Fields["username"]);
        Assert.Equal("p@ss,word", gh.Fields["password"]);     // comma preserved from inside quotes
        Assert.Equal("https://github.com/", gh.Fields["url"]);
        Assert.Equal("work account", gh.Notes);

        Assert.Equal("Yandex", items[1].Title);
        Assert.Equal("zima2022", items[1].Fields["password"]);
        Assert.False(items[1].Fields.ContainsKey("note"));    // empty note -> no field
    }

    [Fact]
    public void Chromium_Csv_Without_Note_Column_Works()
    {
        var items = Importer.Parse(ChromiumCsvNoNote);
        Assert.Single(items);
        Assert.Equal("Ozon", items[0].Title);
        Assert.Equal("ozon12345", items[0].Fields["password"]);
    }

    [Fact]
    public void Kaspersky_Parses_Accounts_And_Notes()
    {
        var items = Importer.Parse(KasperskyTxt);
        var accounts = items.Where(i => i.Type == "account").ToList();
        var notes = items.Where(i => i.Type == "note").ToList();

        Assert.Equal(2, accounts.Count);
        Assert.Single(notes);

        var gmail = accounts.First(a => a.Title == "Gmail");
        Assert.Equal("gleb.hse@gmail.com", gmail.Fields["username"]);
        Assert.Equal("s3cr3t!", gmail.Fields["password"]);
        Assert.Equal("personal", gmail.Notes);
        Assert.Equal("https://mail.google.com", gmail.Fields["url"]);

        Assert.Contains("9armaturaKot", notes[0].Notes);
    }

    [Fact]
    public void Apple_Passwords_Csv_Maps_Title_And_Otpauth()
    {
        Assert.Equal(ImportFormat.ChromiumCsv, Importer.Detect(AppleCsv));
        var items = Importer.Parse(AppleCsv);
        Assert.Single(items);
        var it = items[0];
        Assert.Equal("GitHub", it.Title);                               // from "Title"
        Assert.Equal("glebdev", it.Fields["username"]);
        Assert.Equal("K#94vRq7", it.Fields["password"]);
        Assert.Equal("work note", it.Notes);
        Assert.Equal("otpauth://totp/GitHub?secret=GEZDGNBV", it.Fields["totp"]);  // OTPAuth -> totp
    }

    [Fact]
    public void Imports_Bitwarden_LastPass_Firefox()
    {
        var bw = Importer.Parse(
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            ",,login,GitHub,,,0,https://github.com,glebdev,K#94vRq7,\n");
        Assert.Single(bw);
        Assert.Equal("GitHub", bw[0].Title);
        Assert.Equal("glebdev", bw[0].Fields["username"]);
        Assert.Equal("K#94vRq7", bw[0].Fields["password"]);
        Assert.Equal("https://github.com", bw[0].Fields["url"]);

        var lp = Importer.Parse(
            "url,username,password,totp,extra,name,grouping,fav\n" +
            "https://vk.com,gleb@vk.com,vkpass123,,,VK,Social,0\n");
        Assert.Single(lp);
        Assert.Equal("VK", lp[0].Title);
        Assert.Equal("gleb@vk.com", lp[0].Fields["username"]);
        Assert.Equal("vkpass123", lp[0].Fields["password"]);

        var ff = Importer.Parse(
            "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\"\n" +
            "\"https://ozon.ru\",\"gleb\",\"ozonpass\",\"\",\"https://ozon.ru\",\"{g}\"\n");
        Assert.Single(ff);
        Assert.Equal("ozon.ru", ff[0].Title);          // no name column -> derived from url
        Assert.Equal("ozonpass", ff[0].Fields["password"]);
    }

    [Fact]
    public void Empty_And_Header_Only_Yield_Nothing()
    {
        Assert.Empty(Importer.Parse(""));
        Assert.Empty(Importer.Parse("name,url,username,password\n"));
    }

    [Fact]
    public void Missing_Title_Falls_Back_To_Host()
    {
        var items = Importer.Parse("name,url,username,password\n,https://www.reddit.com/,gleb,pw\n");
        Assert.Single(items);
        Assert.Equal("reddit.com", items[0].Title);   // derived from URL, www. stripped
    }
}
