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

            string title = First(cur, "Website name", "Name", "Application", "Title");
            string url = First(cur, "Website URL", "URL", "Website", "Web-site");
            string user = First(cur, "Login", "Login name", "User name", "Username");
            string pass = First(cur, "Password");
            string note = First(cur, "Comment", "Notes", "Note", "Text");

            bool empty = string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(user) &&
                         string.IsNullOrWhiteSpace(pass) && string.IsNullOrWhiteSpace(note);
            if (!empty)
            {
                bool isNote = (section.Contains("note", StringComparison.OrdinalIgnoreCase) ||
                               section.Contains("заметк", StringComparison.OrdinalIgnoreCase)) &&
                              string.IsNullOrWhiteSpace(pass) && string.IsNullOrWhiteSpace(user);
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
                    if (!string.IsNullOrEmpty(note)) item.Notes = note;
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
