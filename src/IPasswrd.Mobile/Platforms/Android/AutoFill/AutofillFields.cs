using Android.App.Assist;
using Android.Text;
using Android.Views;
using Android.Views.Autofill;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>Что нашлось в разбираемой форме: поля логина/пароля/кода и «чей» это экран.</summary>
internal sealed class AutofillFields
{
    public AutofillId? Username { get; set; }
    public AutofillId? Password { get; set; }
    public AutofillId? Otp { get; set; }

    /// <summary>Домен страницы, если заполняется браузер (node.WebDomain).</summary>
    public string? WebDomain { get; set; }

    /// <summary>Пакет приложения, если заполняется нативная форма.</summary>
    public string? PackageName { get; set; }

    public bool HasAny => Username is not null || Password is not null || Otp is not null;

    public AutofillId[] Ids()
    {
        var list = new List<AutofillId>(3);
        if (Username is not null) list.Add(Username);
        if (Password is not null) list.Add(Password);
        if (Otp is not null) list.Add(Otp);
        return list.ToArray();
    }
}

/// <summary>
/// Разбор структуры экрана, которую отдаёт система. Ищем поля логина, пароля и
/// одноразового кода — сначала по официальным подсказкам autofillHints, потом по
/// эвристикам (id ресурса, hint, тип ввода, html-атрибуты в браузере).
/// Это android-аналог того, что на iOS делает сама система по ASCredentialServiceIdentifier.
/// </summary>
internal static class AutofillParser
{
    /// <summary>Диагностика: сколько текстовых полей увидели в последней структуре.</summary>
    public static int LastTextNodeCount { get; private set; }

    private static readonly string[] UserWords =
    {
        "username", "user_name", "userid", "user_id", "login", "email", "e-mail", "mail",
        "phone", "tel", "account", "логин", "почта", "телефон",
    };

    private static readonly string[] PassWords =
    {
        "password", "passwd", "pwd", "pass", "пароль",
    };

    private static readonly string[] OtpWords =
    {
        "otp", "one-time-code", "onetimecode", "totp", "2fa", "twofactor", "sms_code",
        "smscode", "verification", "verify_code", "code", "код",
    };

    public static AutofillFields Parse(AssistStructure structure)
    {
        var f = new AutofillFields
        {
            PackageName = structure.ActivityComponent?.PackageName,
        };
        LastTextNodeCount = 0;

        // Порядок важен: поля собираем так, как они идут по экрану.
        var found = new List<(Kind Kind, AutofillId Id)>();

        int windows = structure.WindowNodeCount;
        for (int i = 0; i < windows; i++)
        {
            AssistStructure.ViewNode? root = structure.GetWindowNodeAt(i)?.RootViewNode;
            if (root is not null) Walk(root, f, found);
        }

        int passIndex = found.FindIndex(x => x.Kind == Kind.Password);
        f.Password = passIndex >= 0 ? found[passIndex].Id : null;

        // Логин — ПОСЛЕДНЕЕ подходящее поле ПЕРЕД паролем. Просто «первое на экране»
        // не годится: сверху часто висит строка поиска или подписка на рассылку,
        // и пароль уехал бы не к тому логину.
        AutofillId? user = null;
        for (int i = 0; i < found.Count; i++)
        {
            if (passIndex >= 0 && i >= passIndex) break;
            if (found[i].Kind == Kind.Username) user = found[i].Id;
        }
        // Пароля на экране нет (или он выше всех логинов) — берём первый логин.
        user ??= found.FirstOrDefault(x => x.Kind == Kind.Username).Id;
        f.Username = user;

        f.Otp = found.FirstOrDefault(x => x.Kind == Kind.Otp).Id;

        // Форма только с кодом (второй шаг входа) — логин туда подставлять не надо.
        if (f.Password is null && f.Otp is not null) f.Username = null;

        return f;
    }

    /// <summary>Текст, который пользователь ввёл в поле с этим id (нужно для «сохранить пароль»).</summary>
    public static string? ValueOf(AssistStructure structure, AutofillId? id)
    {
        if (id is null) return null;
        int windows = structure.WindowNodeCount;
        for (int i = 0; i < windows; i++)
        {
            AssistStructure.ViewNode? root = structure.GetWindowNodeAt(i)?.RootViewNode;
            string? v = root is null ? null : FindValue(root, id);
            if (v is not null) return v;
        }
        return null;
    }

    private static string? FindValue(AssistStructure.ViewNode node, AutofillId id)
    {
        try
        {
            if (node.AutofillId is not null && node.AutofillId.Equals(id))
            {
                AutofillValue? value = node.AutofillValue;
                if (value is not null && value.IsText) return value.TextValue?.ToString();
                return node.Text?.ToString();
            }
        }
        catch (Exception) { }

        int count = node.ChildCount;
        for (int i = 0; i < count; i++)
        {
            AssistStructure.ViewNode? child = node.GetChildAt(i);
            string? v = child is null ? null : FindValue(child, id);
            if (v is not null) return v;
        }
        return null;
    }

    private static void Walk(AssistStructure.ViewNode node, AutofillFields f,
        List<(Kind Kind, AutofillId Id)> found)
    {
        try
        {
            if (!string.IsNullOrEmpty(node.WebDomain) && string.IsNullOrEmpty(f.WebDomain))
                f.WebDomain = node.WebDomain;

            AutofillId? id = node.AutofillId;
            if (id is not null && node.AutofillType == AutofillType.Text)
            {
                LastTextNodeCount++;
                Kind kind = Classify(node);
                if (kind != Kind.None) found.Add((kind, id));
            }
        }
        catch (Exception) { /* один битый узел не должен ронять разбор */ }

        int count = node.ChildCount;
        for (int i = 0; i < count; i++)
        {
            AssistStructure.ViewNode? child = node.GetChildAt(i);
            if (child is not null) Walk(child, f, found);
        }
    }

    private enum Kind { None, Username, Password, Otp }

    private static Kind Classify(AssistStructure.ViewNode node)
    {
        // 1) официальные подсказки
        string[]? hints = node.GetAutofillHints();
        if (hints is not null)
        {
            foreach (string raw in hints)
            {
                string h = raw.ToLowerInvariant();
                if (h.Contains("password")) return Kind.Password;
                if (h.Contains("username") || h.Contains("emailaddress") || h.Contains("email")
                    || h.Contains("phonenumber") || h.Contains("phone")) return Kind.Username;
                if (h.Contains("otp") || h.Contains("one-time") || h.Contains("smsotp")) return Kind.Otp;
            }
        }

        // 2) тип ввода: маскированный текст — почти наверняка пароль
        InputTypes input = node.InputType;
        if (IsPasswordInput(input)) return Kind.Password;

        // 3) html в браузере: <input type=password | autocomplete=one-time-code>
        try
        {
            var html = node.HtmlInfo;
            if (html is not null)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(html.Tag ?? "").Append(' ');
                // Тип коллекции атрибутов в биндинге — список пар; читаем нейтрально,
                // нам важны только слова («password», «one-time-code», «email»).
                if (html.Attributes is System.Collections.IEnumerable attrs)
                {
                    foreach (object? a in attrs) sb.Append(a?.ToString() ?? "").Append(' ');
                }
                Kind byHtml = ByWords(sb.ToString().ToLowerInvariant());
                if (byHtml != Kind.None) return byHtml;
            }
        }
        catch (Exception) { /* html-разбор — необязательный путь */ }

        // 4) эвристика по id ресурса / подсказке / тексту
        string bag = string.Join(' ',
            node.IdEntry ?? "", node.Hint ?? "", node.ContentDescription ?? "").ToLowerInvariant();
        return ByWords(bag);
    }

    private static Kind ByWords(string bag)
    {
        if (bag.Length == 0) return Kind.None;
        foreach (string w in PassWords) if (bag.Contains(w)) return Kind.Password;
        foreach (string w in OtpWords) if (bag.Contains(w)) return Kind.Otp;
        foreach (string w in UserWords) if (bag.Contains(w)) return Kind.Username;
        return Kind.None;
    }

    private static bool IsPasswordInput(InputTypes input)
    {
        InputTypes cls = input & InputTypes.MaskClass;
        InputTypes variation = input & InputTypes.MaskVariation;
        if (cls == InputTypes.ClassText && (variation == InputTypes.TextVariationPassword
            || variation == InputTypes.TextVariationVisiblePassword
            || variation == InputTypes.TextVariationWebPassword)) return true;
        if (cls == InputTypes.ClassNumber && variation == InputTypes.NumberVariationPassword) return true;
        return false;
    }

}
