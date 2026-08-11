namespace IPasswrd.Core.Import;

public enum ImportFormat
{
    Auto,
    /// <summary>Chromium browsers: Chrome, Edge, Yandex Browser, Brave, Opera. CSV: name,url,username,password[,note].</summary>
    ChromiumCsv,
    /// <summary>Kaspersky Password Manager plain-text export (key: value blocks, section headers).</summary>
    KasperskyTxt,
}

/// <summary>
/// Turns a browser / password-manager export into <see cref="VaultItem"/>s.
/// Import only produces items; the caller decides how to add/dedupe them into a vault.
/// </summary>
public static class Importer
{
    public static ImportFormat Detect(string content)
    {
        string head = content.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (head.Length == 0) return ImportFormat.ChromiumCsv;

        // Kaspersky text export has "Key: value" lines with these labels.
        if (head.Contains("Website name:", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("Website URL:", StringComparison.OrdinalIgnoreCase) ||
            head.Contains("Login name:", StringComparison.OrdinalIgnoreCase))
            return ImportFormat.KasperskyTxt;

        string firstLine = head.Split('\n', 2)[0].ToLowerInvariant();
        bool looksCsvHeader = firstLine.Contains(',') && firstLine.Contains("password") &&
                              (firstLine.Contains("username") || firstLine.Contains("login") || firstLine.Contains("url"));
        if (looksCsvHeader) return ImportFormat.ChromiumCsv;

        // Fallback: "key: value" blocks with no commas look like Kaspersky; otherwise CSV.
        return !firstLine.Contains(',') && head.Contains(':') ? ImportFormat.KasperskyTxt : ImportFormat.ChromiumCsv;
    }

    public static List<VaultItem> Parse(string content, ImportFormat format = ImportFormat.Auto)
    {
        if (format == ImportFormat.Auto) format = Detect(content);
        return format == ImportFormat.KasperskyTxt ? ParseKaspersky(content) : ParseChromium(content);
    }

    // ---- Chromium CSV (Chrome / Edge / Yandex / Brave / Opera) ----

    private static List<VaultItem> ParseChromium(string content)
    {
        var items = new List<VaultItem>();
        var rows = Csv.Parse(content);
        if (rows.Count == 0) return items;

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        // Column names across the popular exporters — Chrome/Edge/Yandex/Brave/Opera, Apple Passwords,
        // Firefox, Bitwarden, LastPass, 1Password, Dashlane, NordPass, Proton Pass. Order = priority.
        int iName = IndexOfAny(header, "name", "title", "account", "item name");
        int iUrl = IndexOfAny(header, "url", "urls", "website", "web site", "web address", "website url", "login_uri", "uri");
        int iUser = IndexOfAny(header, "username", "login", "login name", "login_username", "user", "user name", "email", "e-mail");
        int iPass = IndexOfAny(header, "password", "login_password", "pass");
        int iNote = IndexOfAny(header, "note", "notes", "comment", "extra");
        int iOtp = IndexOfAny(header, "otpauth", "otpauth_url", "otp", "totp", "login_totp", "one-time password");

        bool hasHeader = iPass >= 0 || iUser >= 0;
        int start = hasHeader ? 1 : 0;
        if (!hasHeader) { iName = 0; iUrl = 1; iUser = 2; iPass = 3; iNote = -1; }   // positional fallback

        for (int r = start; r < rows.Count; r++)
        {
            var row = rows[r];
            string Cell(int idx) => idx >= 0 && idx < row.Count ? row[idx] : "";

            string url = Cell(iUrl);
            string user = Cell(iUser);
            string pass = Cell(iPass);
            string name = Cell(iName);
            if (string.IsNullOrWhiteSpace(name)) name = TitleFromUrl(url);

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(pass))
                continue;

            var item = new VaultItem { Type = "account", Title = name };
            if (!string.IsNullOrEmpty(url)) item.Fields["url"] = url;
            if (!string.IsNullOrEmpty(user)) item.Fields["username"] = user;
            if (!string.IsNullOrEmpty(pass)) item.Fields["password"] = pass;
            string otp = Cell(iOtp);
            if (!string.IsNullOrEmpty(otp)) item.Fields["totp"] = otp;
            string note = Cell(iNote);
            if (!string.IsNullOrEmpty(note)) item.Notes = note;
            items.Add(item);
        }
        return items;
    }

    // ---- Kaspersky Password Manager text export ----

    private static List<VaultItem> ParseKaspersky(string content)
    {
        var items = new List<VaultItem>();
        var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";

        void Flush()
        {
            if (cur.Count == 0) return;

            string title = First(cur, "Website name", "Name", "Application", "Title", "Название", "Имя");
            string url = First(cur, "Website URL", "URL", "Website", "Web-site", "Адрес сайта", "Сайт");
            string user = First(cur, "Login", "Login name", "User name", "Username", "Логин", "Имя пользователя");
            string pass = First(cur, "Password", "Пароль");
            string note = First(cur, "Comment", "Notes", "Note", "Text", "Комментарий", "Заметка", "Текст");

            string kind = SectionKind(section);

            // Карты и адреса разбираются своими правилами: раньше они превращались в аккаунты
            // с одним названием — номер карты и адрес доставки просто исчезали при переезде.
            if (kind == "card") { items.Add(BuildKasperskyCard(cur, title, note)); cur.Clear(); return; }
            if (kind == "identity") { items.Add(BuildKasperskyIdentity(cur, title, note)); cur.Clear(); return; }

            bool empty = string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(user) &&
                         string.IsNullOrWhiteSpace(pass) && string.IsNullOrWhiteSpace(note);
            if (!empty)
            {
                bool isNote = kind == "note" && string.IsNullOrWhiteSpace(pass) && string.IsNullOrWhiteSpace(user);
                if (isNote)
                {
                    items.Add(new VaultItem { Type = "note", Title = string.IsNullOrWhiteSpace(title) ? "Заметка" : title, Notes = note });
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(title)) title = TitleFromUrl(url);
                    var item = new VaultItem { Type = "account", Title = title };
                    if (!string.IsNullOrEmpty(url)) item.Fields["url"] = url;
                    if (!string.IsNullOrEmpty(user)) item.Fields["username"] = user;
                    if (!string.IsNullOrEmpty(pass)) item.Fields["password"] = pass;
                    item.Notes = WithLeftovers(note, cur, "Website name", "Name", "Application", "Title", "Название", "Имя",
                        "Website URL", "URL", "Website", "Web-site", "Адрес сайта", "Сайт",
                        "Login", "Login name", "User name", "Username", "Логин", "Имя пользователя",
                        "Password", "Пароль", "Comment", "Notes", "Note", "Text", "Комментарий", "Заметка", "Текст");
                    items.Add(item);
                }
            }
            cur.Clear();
        }

        bool prevBlank = true;      // start-of-file behaves like a record boundary
        string? lastKey = null;     // the field a bare continuation line appends to

        foreach (string raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();

            if (line.Length == 0)                       // blank line ends the current record
            {
                Flush();
                prevBlank = true;
                lastKey = null;
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                string t = line.Trim();
                bool allDashes = t.Length > 0 && t.All(c => c == '-');   // "---" record separator
                if (prevBlank || allDashes)
                {
                    Flush();                            // a section header (Websites / Notes / ...) or a separator
                    lastKey = null;
                    if (!allDashes) section = t;        // separators must not become the section name
                }
                else if (lastKey != null)
                {
                    cur[lastKey] += "\n" + t;           // continuation of a multi-line value (Note text, Comment ...)
                }
                prevBlank = false;
                continue;
            }

            string key = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();
            cur[key] = cur.TryGetValue(key, out var prev) ? prev + "\n" + val : val;
            lastKey = key;
            prevBlank = false;
        }
        Flush();
        return items;
    }

    // ---- Kaspersky: разделы, которые не про логины ----

    /// <summary>
    /// Заголовок раздела → тип записи. Язык экспорта зависит от языка программы, а не от выбора
    /// пользователя, поэтому смотрим и на русские, и на английские названия.
    /// </summary>
    private static string SectionKind(string section)
    {
        string s = (section ?? "").ToLowerInvariant();
        if (s.Contains("card") || s.Contains("карт")) return "card";
        if (s.Contains("address") || s.Contains("адрес") || s.Contains("identit") || s.Contains("личные данные")) return "identity";
        if (s.Contains("note") || s.Contains("заметк")) return "note";
        return "account";
    }

    private static VaultItem BuildKasperskyCard(Dictionary<string, string> cur, string title, string note)
    {
        var item = new VaultItem { Type = ItemTypes.Card };
        string number = First(cur, "Card number", "Number", "Номер карты", "Номер");
        string expiry = First(cur, "Expiration date", "Valid thru", "Expiry", "Срок действия", "Действительна до", "Срок");
        string cvc = First(cur, "CVC", "CVV", "CVC2", "CVV2", "Security code", "Код безопасности");
        string holder = First(cur, "Cardholder", "Card holder", "Holder", "Держатель", "Владелец", "Имя на карте");

        SetIfAny(item, "number", new string(number.Where(char.IsDigit).ToArray()));
        SetIfAny(item, "expiry", expiry);
        SetIfAny(item, "cvc", cvc);
        SetIfAny(item, "holder", holder);

        item.Title = string.IsNullOrWhiteSpace(title)
            ? (item.Fields.TryGetValue("number", out var n) && n.Length >= 4 ? "Карта •••• " + n[^4..] : "Карта")
            : title;

        // PIN и всё прочее, чему не нашлось поля, едет в заметку. Потерять его при переезде хуже,
        // чем показать в неидеальном месте.
        item.Notes = WithLeftovers(note, cur,
            "Card number", "Number", "Номер карты", "Номер",
            "Expiration date", "Valid thru", "Expiry", "Срок действия", "Действительна до", "Срок",
            "CVC", "CVV", "CVC2", "CVV2", "Security code", "Код безопасности",
            "Cardholder", "Card holder", "Holder", "Держатель", "Владелец", "Имя на карте",
            "Name", "Title", "Название", "Comment", "Notes", "Note", "Text", "Комментарий", "Заметка", "Текст");
        return item;
    }

    private static VaultItem BuildKasperskyIdentity(Dictionary<string, string> cur, string title, string note)
    {
        var item = new VaultItem { Type = ItemTypes.Identity };

        SetIfAny(item, "lastName", First(cur, "Last name", "Surname", "Фамилия"));
        SetIfAny(item, "firstName", First(cur, "First name", "Имя"));
        SetIfAny(item, "middleName", First(cur, "Middle name", "Отчество"));
        SetIfAny(item, "phone", First(cur, "Phone", "Phone number", "Mobile", "Телефон", "Номер телефона"));
        SetIfAny(item, "email", First(cur, "E-mail", "Email", "Почта", "Электронная почта"));
        SetIfAny(item, "zip", First(cur, "Postal code", "ZIP", "Zip code", "Индекс", "Почтовый индекс"));
        SetIfAny(item, "country", First(cur, "Country", "Страна"));
        SetIfAny(item, "city", First(cur, "City", "Town", "Город", "Населённый пункт"));
        SetIfAny(item, "street", First(cur, "Address", "Street", "Street address", "Адрес", "Улица"));

        // У Kaspersky в «Адресах» есть поле «Имя» — это название записи, а не имя человека;
        // если ФИО разобралось по частям — собираем название из них, как в самом приложении.
        string fio = string.Join(" ", new[] { "lastName", "firstName", "middleName" }
            .Select(k => item.Fields.GetValueOrDefault(k, ""))
            .Where(x => x.Length > 0));
        item.Title = !string.IsNullOrWhiteSpace(title) ? title
            : fio.Length > 0 ? fio
            : "Личные данные";

        item.Notes = WithLeftovers(note, cur,
            "Last name", "Surname", "Фамилия", "First name", "Имя", "Middle name", "Отчество",
            "Phone", "Phone number", "Mobile", "Телефон", "Номер телефона",
            "E-mail", "Email", "Почта", "Электронная почта",
            "Postal code", "ZIP", "Zip code", "Индекс", "Почтовый индекс",
            "Country", "Страна", "City", "Town", "Город", "Населённый пункт",
            "Address", "Street", "Street address", "Адрес", "Улица",
            "Name", "Title", "Название", "Comment", "Notes", "Note", "Text", "Комментарий", "Заметка", "Текст");
        return item;
    }

    private static void SetIfAny(VaultItem item, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) item.Fields[key] = value.Trim();
    }

    /// <summary>
    /// Заметка плюс всё, чему не нашлось колонки. Незнакомое поле из чужого экспорта раньше
    /// просто исчезало — а человек, переезжая, должен увезти всё, а не только то, что мы умеем
    /// показать красиво.
    /// </summary>
    private static string WithLeftovers(string note, Dictionary<string, string> cur, params string[] consumed)
    {
        var used = new HashSet<string>(consumed, StringComparer.OrdinalIgnoreCase);
        var extra = cur
            .Where(kv => !used.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key + ": " + kv.Value)
            .ToList();

        if (extra.Count == 0) return note ?? "";
        string head = string.IsNullOrWhiteSpace(note) ? "" : note.TrimEnd() + "\n";
        return head + string.Join("\n", extra);
    }

    // ---- helpers ----

    private static int IndexOfAny(List<string> header, params string[] names)
    {
        foreach (string nm in names) { int i = header.IndexOf(nm); if (i >= 0) return i; }
        return -1;
    }

    private static string First(Dictionary<string, string> d, params string[] keys)
    {
        foreach (string k in keys)
            if (d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return "";
    }

    private static string TitleFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var u = url.Contains("://") ? new Uri(url) : new Uri("https://" + url);
            string host = u.Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch { return url; }
    }
}
