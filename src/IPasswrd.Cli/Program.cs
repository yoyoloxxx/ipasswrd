using System.Text;
using IPasswrd.Core;
using IPasswrd.Core.Import;

namespace IPasswrd.Cli;

/// <summary>
/// Thin command-line front-end over IPasswrd.Core. It exists to exercise the
/// vault engine end-to-end with real file persistence and to be the stand for
/// import (CSV / Apple Passwords) next. Not the product UI — that is Avalonia later.
///
/// Vault file: %LOCALAPPDATA%\IPasswrd\vault.ipvault (override with IPASSWRD_VAULT).
/// Master-password input is hidden in a real console, and read as a plain line
/// when stdin is redirected (so the flow can be scripted/tested).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected/no console */ }

        // Ввод — тоже UTF-8. Иначе строка, поданная из файла или конвейера, читается в кодировке
        // консоли, и «Петров» приезжает в сейф как «??????» — молча, без единой ошибки и без
        // возможности восстановить исходное. Скрипты, заливающие записи пачкой, — ровно тот
        // случай, где это заметят последним.
        if (Console.IsInputRedirected)
        {
            try { Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))); }
            catch { /* оставим как есть */ }
        }
        try
        {
            if (args.Length == 0) { Usage(); return 1; }
            return args[0].ToLowerInvariant() switch
            {
                "init" => CmdInit(args),
                "add" => CmdAdd(args),
                "list" or "ls" => CmdList(),
                "get" or "show" => CmdGet(args),
                "del" or "delete" or "rm" => CmdDel(args),
                "edit" or "set" => CmdEdit(args),
                "passwd" or "password" => CmdPasswd(),
                "gen" or "generate" => CmdGen(args),
                "audit" or "check" => CmdAudit(),
                "totp" or "2fa" => CmdTotp(args),
                "import" => CmdImport(args),
                "help" or "--help" or "-h" => Ok(Usage),
                _ => Unknown(args[0]),
            };
        }
        catch (WrongMasterPasswordException)
        {
            Console.Error.WriteLine("Неверный мастер-пароль.");
            return 2;
        }
        catch (VaultIntegrityException e)
        {
            Console.Error.WriteLine("Нарушена целостность сейфа: " + e.Message);
            return 3;
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine("Сейф не найден. Сначала выполните: ipasswrd init");
            return 4;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Ошибка: " + e.Message);
            return 10;
        }
    }

    // ---- commands ----

    private static int CmdInit(string[] args)
    {
        string path = VaultPath();
        if (File.Exists(path) && !args.Contains("--force"))
        {
            Console.Error.WriteLine($"Сейф уже существует: {path}");
            Console.Error.WriteLine("Добавьте --force, чтобы перезаписать (данные будут потеряны).");
            return 1;
        }
        string p1 = ReadSecret("Придумайте мастер-пароль: ");
        string p2 = ReadSecret("Повторите мастер-пароль: ");
        if (p1 != p2) { Console.Error.WriteLine("Пароли не совпадают."); return 1; }
        if (p1.Length < 8) { Console.Error.WriteLine("Слишком короткий мастер-пароль (минимум 8 символов)."); return 1; }

        Vault v = Vault.Create(p1);
        Save(v);
        Console.WriteLine($"Сейф создан: {path}");
        return 0;
    }

    private static int CmdAdd(string[] args)
    {
        string type = args.Length > 1 ? args[1].ToLowerInvariant() : "account";
        Vault v = Unlock();
        VaultItem item = type switch
        {
            "card" => AddCard(),
            "note" => AddNote(),
            "doc" or "document" => AddDoc(),
            "identity" or "id" => AddIdentity(),
            _ => AddAccount(),
        };
        string id = v.Add(item);
        Save(v);
        Console.WriteLine($"Добавлено: {item.Title}  [{Short(id)}]  ({item.Type})");
        return 0;
    }

    private static VaultItem AddAccount()
    {
        var item = new VaultItem { Type = "account", Title = Prompt("Название: ") };
        string url = Prompt("Сайт: ");
        if (!string.IsNullOrEmpty(url)) item.Fields["url"] = url;
        string user = Prompt("Имя пользователя: ");
        if (!string.IsNullOrEmpty(user)) item.Fields["username"] = user;
        string pw = ReadSecret("Пароль (пусто = сгенерировать): ");
        if (string.IsNullOrEmpty(pw)) { pw = Generate(20); Console.WriteLine($"Сгенерирован пароль: {pw}"); }
        item.Fields["password"] = pw;
        string totp = Prompt("TOTP-секрет или otpauth:// (необязательно): ");
        if (!string.IsNullOrEmpty(totp)) item.Fields["totp"] = totp;
        string notes = Prompt("Заметка (необязательно): ");
        if (!string.IsNullOrEmpty(notes)) item.Notes = notes;
        return item;
    }

    /// <summary>Личные данные для форм доставки — те же поля и в том же порядке, что в окне приложения.</summary>
    private static VaultItem AddIdentity()
    {
        var item = new VaultItem { Type = "identity", Title = Prompt("Название (пусто = собрать из ФИО): ") };
        foreach (var (key, label) in new[]
        {
            ("lastName", "Фамилия: "), ("firstName", "Имя: "), ("middleName", "Отчество: "),
            ("phone", "Телефон: "), ("email", "Почта: "), ("zip", "Индекс: "),
            ("country", "Страна: "), ("city", "Город: "), ("street", "Адрес (улица, дом, квартира): "),
        })
        {
            string v = Prompt(label);
            if (!string.IsNullOrEmpty(v)) item.Fields[key] = v;
        }
        if (string.IsNullOrWhiteSpace(item.Title))
            item.Title = string.Join(" ", new[] { "lastName", "firstName", "middleName" }
                .Select(k => item.Fields.GetValueOrDefault(k, ""))
                .Where(x => x.Length > 0));
        return item;
    }

    private static VaultItem AddCard()
    {
        var item = new VaultItem { Type = "card", Title = Prompt("Название: ") };
        item.Fields["number"] = Prompt("Номер карты: ");
        item.Fields["expiry"] = Prompt("Срок (ММ/ГГ): ");
        item.Fields["cvc"] = ReadSecret("CVC: ");
        item.Fields["holder"] = Prompt("Владелец: ");
        string notes = Prompt("Заметка (необязательно): ");
        if (!string.IsNullOrEmpty(notes)) item.Notes = notes;
        return item;
    }

    private static VaultItem AddNote()
    {
        var item = new VaultItem { Type = "note", Title = Prompt("Название: ") };
        item.Notes = Prompt("Текст заметки: ");
        return item;
    }

    private static VaultItem AddDoc()
    {
        var item = new VaultItem { Type = "doc", Title = Prompt("Название: ") };
        item.Fields["number"] = Prompt("Серия и номер: ");
        item.Fields["issued"] = Prompt("Кем выдан: ");
        string notes = Prompt("Заметка (необязательно): ");
        if (!string.IsNullOrEmpty(notes)) item.Notes = notes;
        return item;
    }

    private static int CmdList()
    {
        Vault v = Unlock();
        var items = v.Items();
        if (items.Count == 0) { Console.WriteLine("(сейф пуст)"); return 0; }

        Console.WriteLine();
        foreach (var e in items.OrderBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase))
        {
            string user = e.Item.Fields.TryGetValue("username", out var u) ? u : "";
            Console.WriteLine($"  {Short(e.Id)}  {Pad(e.Item.Title, 24)}  {user}");
        }
        Console.WriteLine($"\n{items.Count} {Plural(items.Count)}.");
        return 0;
    }

    private static int CmdGet(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Использование: ipasswrd get <id|название> [--show]"); return 1; }
        bool show = args.Contains("--show");
        Vault v = Unlock();
        VaultEntry? match = FindOne(v, args[1]);
        if (match is null) { Console.Error.WriteLine("Не найдено."); return 1; }

        Console.WriteLine();
        Console.WriteLine($"  {match.Item.Title}   [{Short(match.Id)}]");
        foreach (var kv in match.Item.Fields)
        {
            if (kv.Key == "totp") continue;   // rendered as a live code below
            bool masked = (kv.Key is "password" or "cvc") && !show;
            string val = masked
                ? new string('•', Math.Min(Math.Max(kv.Value.Length, 1), 12))
                : kv.Value;
            Console.WriteLine($"    {Pad(kv.Key, 12)}: {val}");
        }
        if (match.Item.Fields.TryGetValue("totp", out var totpSecret) && !string.IsNullOrWhiteSpace(totpSecret))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cfg = Totp.Parse(totpSecret);
            Console.WriteLine($"    {Pad("код 2FA", 12)}: {Totp.GenerateFrom(totpSecret, now)}  (осталось {cfg.SecondsRemaining()} с)");
            if (show) Console.WriteLine($"    {Pad("totp", 12)}: {totpSecret}");
        }
        if (!string.IsNullOrEmpty(match.Item.Notes))
            Console.WriteLine($"    {Pad("notes", 12)}: {match.Item.Notes}");
        if (!show)
            Console.WriteLine("  (добавьте --show, чтобы раскрыть пароль)");
        return 0;
    }

    private static int CmdTotp(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Использование: ipasswrd totp <id|название>"); return 1; }
        Vault v = Unlock();
        VaultEntry? m = FindOne(v, args[1]);
        if (m is null) { Console.Error.WriteLine("Не найдено."); return 1; }
        if (!m.Item.Fields.TryGetValue("totp", out var secret) || string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine("У записи нет кода проверки (TOTP).");
            return 1;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cfg = Totp.Parse(secret);
        Console.WriteLine($"{Totp.GenerateFrom(secret, now)}  (осталось {cfg.SecondsRemaining()} с)");
        return 0;
    }

    private static int CmdDel(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Использование: ipasswrd del <id|название>"); return 1; }
        Vault v = Unlock();
        VaultEntry? match = FindOne(v, args[1]);
        if (match is null) { Console.Error.WriteLine("Не найдено."); return 1; }
        v.Delete(match.Id);
        Save(v);
        Console.WriteLine($"Удалено: {match.Item.Title}");
        return 0;
    }

    private static int CmdEdit(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Использование: ipasswrd edit <id|название> <поле> <значение>");
            Console.Error.WriteLine("  поле: title, notes, либо имя поля (username, password, url, number, ...). Пустое значение удаляет поле.");
            return 1;
        }
        string query = args[1];
        string field = args[2].ToLowerInvariant();
        string value = string.Join(' ', args.Skip(3));

        Vault v = Unlock();
        VaultEntry? m = FindOne(v, query);
        if (m is null) { Console.Error.WriteLine("Не найдено."); return 1; }

        VaultItem item = m.Item;
        if (field == "title") item.Title = value;
        else if (field is "notes" or "note") item.Notes = value;
        else if (string.IsNullOrEmpty(value)) item.Fields.Remove(field);
        else item.Fields[field] = value;

        v.Update(m.Id, item);
        Save(v);
        Console.WriteLine($"Обновлено: {item.Title}  [{Short(m.Id)}]  ({field})");
        return 0;
    }

    private static int CmdPasswd()
    {
        string path = VaultPath();
        if (!File.Exists(path)) throw new FileNotFoundException();
        byte[] blob = File.ReadAllBytes(path);
        string old = ReadSecret("Текущий мастер-пароль: ");
        Vault v = Vault.Unlock(blob, old);          // throws WrongMasterPasswordException
        string n1 = ReadSecret("Новый мастер-пароль: ");
        string n2 = ReadSecret("Повторите новый: ");
        if (n1 != n2) { Console.Error.WriteLine("Пароли не совпадают."); return 1; }
        if (n1.Length < 8) { Console.Error.WriteLine("Слишком короткий мастер-пароль (минимум 8 символов)."); return 1; }
        v.ChangeMasterPassword(old, n1);
        Save(v);
        Console.WriteLine("Мастер-пароль изменён.");
        return 0;
    }

    private static int CmdGen(string[] args)
    {
        int len = 20;
        if (args.Length > 1 && int.TryParse(args[1], out int l)) len = Math.Clamp(l, 8, 128);
        Console.WriteLine(Generate(len));
        return 0;
    }

    private static int CmdAudit()
    {
        Vault v = Unlock();
        var report = Auditor.Audit(v.Items());
        Console.WriteLine();
        Console.WriteLine($"  Проверено аккаунтов: {report.AccountsChecked}");
        Console.WriteLine($"  Надёжных: {report.Ok}   Ненадёжных: {report.Weak.Count}   Повторяющихся: {report.Reused.Count}");
        if (report.Weak.Count > 0)
        {
            Console.WriteLine("\n  Ненадёжные пароли:");
            foreach (var f in report.Weak) Console.WriteLine($"    - {f.Title}  [{Short(f.Id)}]");
        }
        if (report.Reused.Count > 0)
        {
            Console.WriteLine("\n  Повторяющиеся пароли:");
            foreach (var f in report.Reused) Console.WriteLine($"    - {f.Title}  [{Short(f.Id)}]");
        }
        if (report.Weak.Count == 0 && report.Reused.Count == 0)
            Console.WriteLine("\n  Замечаний нет.");
        return 0;
    }

    private static int CmdImport(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: ipasswrd import <файл> [--format chromium|kaspersky|auto]");
            return 1;
        }
        string file = args[1];
        if (!File.Exists(file)) { Console.Error.WriteLine($"Файл не найден: {file}"); return 1; }

        ImportFormat fmt = ImportFormat.Auto;
        int fi = Array.IndexOf(args, "--format");
        if (fi >= 0 && fi + 1 < args.Length)
            fmt = args[fi + 1].ToLowerInvariant() switch
            {
                "chromium" or "chrome" or "edge" or "yandex" or "csv" or "brave" or "opera" => ImportFormat.ChromiumCsv,
                "kaspersky" or "kpm" => ImportFormat.KasperskyTxt,
                _ => ImportFormat.Auto,
            };

        var parsed = Importer.Parse(File.ReadAllText(file), fmt);
        if (parsed.Count == 0) { Console.WriteLine("Нечего импортировать (формат не распознан или файл пуст)."); return 0; }

        Vault v = Unlock();
        var seen = new HashSet<string>(v.Items().Select(e => DedupKey(e.Item)), StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0;
        foreach (var it in parsed)
        {
            string k = DedupKey(it);
            if (seen.Contains(k)) { skipped++; continue; }
            v.Add(it); seen.Add(k); added++;
        }
        Save(v);
        Console.WriteLine($"Импортировано: {added}; пропущено дубликатов: {skipped}; всего в файле: {parsed.Count}.");
        return 0;
    }

    private static string DedupKey(VaultItem it)
    {
        it.Fields.TryGetValue("url", out var url);
        it.Fields.TryGetValue("username", out var user);
        return $"{it.Type}|{url}|{user}|{it.Title}".ToLowerInvariant();
    }

    // ---- helpers ----

    private static Vault Unlock()
    {
        string path = VaultPath();
        if (!File.Exists(path)) throw new FileNotFoundException();
        byte[] blob = File.ReadAllBytes(path);
        string pw = ReadSecret("Мастер-пароль: ");
        return Vault.Unlock(blob, pw);
    }

    private static void Save(Vault v)
    {
        string path = VaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, v.Serialize());
    }

    private static string VaultPath()
    {
        string? env = Environment.GetEnvironmentVariable("IPASSWRD_VAULT");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPasswrd");
        return Path.Combine(dir, "vault.ipvault");
    }

    private static VaultEntry? FindOne(Vault v, string query)
    {
        var items = v.Items();
        return items.FirstOrDefault(e => e.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(e => e.Item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string Generate(int len) => Generator.Generate(len);

    private static string Prompt(string label)
    {
        Console.Write(label);
        return (Console.ReadLine() ?? "").Trim();
    }

    private static string ReadSecret(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)          // scripted / piped: read a plain line
            return Console.ReadLine() ?? "";

        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    private static string Short(string id) => id.Length >= 8 ? id[..8] : id;
    private static string Pad(string s, int w) => s.Length >= w ? s[..w] : s.PadRight(w);

    private static string Plural(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return "запись";
        if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return "записи";
        return "записей";
    }

    private static int Ok(Action a) { a(); return 0; }
    private static int Unknown(string cmd) { Console.Error.WriteLine($"Неизвестная команда: {cmd}"); Usage(); return 1; }

    private static void Usage()
    {
        Console.WriteLine(
"""
IPasswrd — консольный доступ к зашифрованному сейфу.

  ipasswrd init                создать новый сейф (спросит мастер-пароль)
  ipasswrd add [card|note|doc] добавить запись (по умолчанию аккаунт)
  ipasswrd list                показать все записи
  ipasswrd get  <id|название>  показать запись (--show раскрывает пароль)
  ipasswrd del  <id|название>  удалить запись
  ipasswrd edit <id|название> <поле> <значение>   изменить поле записи
  ipasswrd passwd              сменить мастер-пароль
  ipasswrd gen  [длина]        сгенерировать пароль
  ipasswrd audit               проверка паролей (слабые/повторяющиеся)
  ipasswrd totp <id|название>  показать текущий код проверки (2FA)
  ipasswrd import <файл>       импорт из Chrome/Edge/Yandex/Kaspersky (--format для явного выбора)

Файл сейфа: %LOCALAPPDATA%\IPasswrd\vault.ipvault  (переопределяется IPASSWRD_VAULT)
""");
    }
}
