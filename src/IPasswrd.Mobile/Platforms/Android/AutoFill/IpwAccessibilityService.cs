using Android.AccessibilityServices;
using Android.Runtime;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using IPasswrd.Core;
using IPasswrd.Mobile.Services;
using AndroidView = Android.Views.View;
using AndroidColor = Android.Graphics.Color;
using AndroidButton = Android.Widget.Button;
using AndroidOrientation = Android.Widget.Orientation;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>
/// Автозаполнение через спец-возможности — для браузеров, которые не отдают формы
/// системному AutofillService (Яндекс, Opera, Firefox…). Chrome сюда не входит.
///
/// Идея как у Bitwarden/KeePassDX: при фокусе на поле ввода в браузере рисуем поверх
/// маленькую кнопку «IPasswrd» (через TYPE_ACCESSIBILITY_OVERLAY — отдельного разрешения
/// «поверх окон» не нужно). По тапу — подбираем записи по домену из адресной строки и
/// вставляем логин/пароль в поля через ACTION_SET_TEXT. Данные не покидают устройство.
/// </summary>
[Service(Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/ipw_accessibility_config")]
public sealed class IpwAccessibilityService : AccessibilityService
{
    // Браузеры, где системное автозаполнение НЕ работает и нужен этот путь.
    // Chrome намеренно исключён — там работает AutofillService, дублировать не нужно.
    private static readonly string[] Browsers =
    {
        "com.yandex.browser", "com.yandex.browser.beta", "com.yandex.browser.alpha",
        "org.mozilla.firefox", "org.mozilla.firefox_beta",
        "com.opera.browser", "com.opera.mini.native",
        "com.huawei.browser", "com.microsoft.emmx", "com.brave.browser",
        "com.duckduckgo.mobile.android",
    };

    private WindowManagerLayoutParams? _btnParams;
    private AndroidView? _button;
    private AndroidView? _menu;
    private string _domain = "";
    private string _pkg = "";

    public static bool IsRunning { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        IsRunning = true;
        Console.WriteLine("[IPW-A11Y] connected");
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        HideButton();
        HideMenu();
        base.OnDestroy();
    }

    public override void OnInterrupt() { }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        try
        {
            if (e is null) return;
            string pkg = e.PackageName ?? "";

            // Ушли из браузера — прибрать кнопку.
            if (!Browsers.Contains(pkg))
            {
                if (_button is not null) { HideButton(); HideMenu(); }
                return;
            }

            AccessibilityNodeInfo? src = e.Source;
            AccessibilityNodeInfo? root = RootInActiveWindow;

            // Показываем кнопку, когда фокус на редактируемом поле (или оно есть в окне).
            bool editableFocused =
                (src is not null && src.Editable) ||
                (e.EventType == EventTypes.WindowContentChanged && HasEditable(root));

            if (editableFocused)
            {
                _pkg = pkg;
                if (_button is null)          // домен ищем и кнопку строим один раз на форму
                {
                    _domain = FindDomain(root) ?? "";
                    ShowButton();
                }
            }
            else if (e.EventType == EventTypes.WindowStateChanged)
            {
                HideButton(); HideMenu();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[IPW-A11Y] event error: " + ex.Message);
        }
    }

    // ================= кнопка-триггер =================

    private void ShowButton()
    {
        if (_button is not null) return;
        var wm = GetSystemService(WindowService)?.JavaCast<IWindowManager>();
        if (wm is null) return;

        var btn = new TextView(this)
        {
            Text = "IP",
            Gravity = GravityFlags.Center,
        };
        btn.SetTextColor(AndroidColor.Argb(255, 26, 20, 8));
        btn.SetTextSize(ComplexUnitType.Sp, 15);
        btn.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);
        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        bg.SetColor(AndroidColor.Argb(242, 214, 170, 78));       // латунь
        bg.SetStroke(Dp(2), AndroidColor.Argb(255, 150, 112, 40));
        btn.Background = bg;
        btn.Clickable = true;
        btn.Click += (_, _) => ToggleMenu();

        int size = Dp(44);
        int screenH = Resources?.DisplayMetrics?.HeightPixels ?? Dp(640);
        var lp = new WindowManagerLayoutParams(
            size, size,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.End | GravityFlags.Top,   // ~четверть от верха, у правого края
            X = Dp(4),
            Y = screenH / 4,
        };

        try { wm.AddView(btn, lp); _button = btn; _btnParams = lp; }
        catch (Exception ex) { Console.WriteLine("[IPW-A11Y] addButton: " + ex.Message); }
    }

    private void HideButton()
    {
        if (_button is null) return;
        try { (GetSystemService(WindowService)?.JavaCast<IWindowManager>())?.RemoveView(_button); } catch (Exception) { }
        _button = null;
    }

    // ================= меню записей =================

    private void ToggleMenu()
    {
        if (_menu is not null) { HideMenu(); return; }

        if (!Svc.State.IsUnlocked)
        {
            // Сейф закрыт — открываем приложение на разблокировку.
            Toast.MakeText(this, "Откройте сейф IPasswrd и повторите", ToastLength.Long)?.Show();
            try
            {
                Intent? launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
                if (launch is not null) { launch.AddFlags(ActivityFlags.NewTask); StartActivity(launch); }
            }
            catch (Exception) { }
            return;
        }

        Vault? vault = Svc.State.Vault;
        if (vault is null) return;

        List<AutofillCandidate> items = AutofillMatcher.Rank(vault, _domain.Length > 0 ? _domain : null, null);
        var matched = items.Where(c => c.Score > 0).ToList();
        // совпавшие по домену — все; иначе первые 8 (выбор вручную)
        bool haveMatch = matched.Count > 0;
        var shown = haveMatch ? matched.Take(12).ToList() : items.Take(8).ToList();

        var wm = GetSystemService(WindowService)?.JavaCast<IWindowManager>();
        if (wm is null) return;

        var panel = new LinearLayout(this) { Orientation = AndroidOrientation.Vertical };
        var pbg = new GradientDrawable();
        pbg.SetShape(ShapeType.Rectangle);
        pbg.SetCornerRadius(Dp(14));
        pbg.SetColor(AndroidColor.Argb(255, 20, 26, 33));
        panel.Background = pbg;
        panel.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));

        var header = new TextView(this)
        {
            Text = _domain.Length == 0 ? "Выбор записи"
                 : haveMatch ? "Для " + _domain
                 : "Для " + _domain + " записей нет — выберите вручную",
        };
        header.SetTextColor(AndroidColor.Argb(255, 140, 152, 165));
        header.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        header.TextSize = 13;
        panel.AddView(header);

        foreach (AutofillCandidate c in shown)
        {
            var row = new AndroidButton(this) { Text = c.Title + (c.Login.Length > 0 ? "  ·  " + c.Login : "") };
            row.SetAllCaps(false);
            row.Gravity = GravityFlags.CenterVertical | GravityFlags.Start;
            row.SetTextColor(AndroidColor.Argb(255, 236, 240, 245));
            row.SetBackgroundColor(AndroidColor.Transparent);
            AutofillCandidate cap = c;
            row.Click += (_, _) => { Fill(cap); HideMenu(); };
            panel.AddView(row);
        }

        var lp = new WindowManagerLayoutParams(
            (int)(Resources!.DisplayMetrics!.WidthPixels * 0.86),
            ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            Y = Dp(150),
        };

        try { wm.AddView(panel, lp); _menu = panel; }
        catch (Exception ex) { Console.WriteLine("[IPW-A11Y] addMenu: " + ex.Message); }
    }

    private void HideMenu()
    {
        if (_menu is null) return;
        try { (GetSystemService(WindowService)?.JavaCast<IWindowManager>())?.RemoveView(_menu); } catch (Exception) { }
        _menu = null;
    }

    // ================= вставка =================

    private void Fill(AutofillCandidate c)
    {
        try
        {
            AccessibilityNodeInfo? root = BrowserRoot();
            if (root is null) return;

            var edits = new List<AccessibilityNodeInfo>();
            CollectEditable(root, edits);
            Console.WriteLine("[IPW-A11Y] fields=" + edits.Count + " [" +
                string.Join(", ", edits.Take(6).Select(n => ((n.ClassName ?? "").ToString()?.Split('.').LastOrDefault() ?? "") + (n.Password ? ":pw" : ""))) + "]");
            if (edits.Count == 0) { Console.WriteLine("[IPW-A11Y] no editable fields (win search)"); return; }

            AccessibilityNodeInfo? passField = edits.FirstOrDefault(n => n.Password);
            AccessibilityNodeInfo? userField = null;

            if (passField is not null)
            {
                // логин — ближайшее НЕ-парольное поле выше пароля
                int passTop = Bounds(passField).Top;
                userField = edits
                    .Where(n => !n.Password && Bounds(n).Top <= passTop)
                    .OrderByDescending(n => Bounds(n).Top)
                    .FirstOrDefault();
            }
            else
            {
                // только логин на экране (первый шаг входа) — берём сфокусированное или первое
                userField = edits.FirstOrDefault(n => n.Focused) ?? edits[0];
            }

            if (userField is not null && c.Login.Length > 0) SetText(userField, c.Login);
            if (passField is not null && c.Password.Length > 0) SetText(passField, c.Password);

            Console.WriteLine($"[IPW-A11Y] filled user={(userField != null)} pass={(passField != null)} edits={edits.Count} dom={_domain}");
            Toast.MakeText(this, "IPasswrd: заполнено", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[IPW-A11Y] fill error: " + ex.Message);
        }
    }

    private void SetText(AccessibilityNodeInfo node, string value)
    {
        try { node.PerformAction(global::Android.Views.Accessibility.Action.Focus); } catch (Exception) { }
        try { node.PerformAction(global::Android.Views.Accessibility.Action.AccessibilityFocus); } catch (Exception) { }

        var args = new Bundle();
        args.PutCharSequence(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, new Java.Lang.String(value));
        bool ok = false;
        try { ok = node.PerformAction(global::Android.Views.Accessibility.Action.SetText, args); } catch (Exception) { }

        // Chromium-поля иногда игнорируют SET_TEXT — тогда кладём в буфер и вставляем.
        if (!ok)
        {
            try
            {
                var cm = (global::Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
                if (cm is not null) cm.PrimaryClip = global::Android.Content.ClipData.NewPlainText("ipw", value);
                node.PerformAction(global::Android.Views.Accessibility.Action.Focus);
                node.PerformAction(global::Android.Views.Accessibility.Action.Paste);
            }
            catch (Exception) { }
        }
        Console.WriteLine("[IPW-A11Y] setText ok=" + ok + " len=" + value.Length);
    }

    // ================= обход дерева =================

    /// <summary>Корень окна браузера. RootInActiveWindow во время нашего меню-оверлея указывает
    /// на оверлей, а не на страницу — поэтому идём по всем окнам и берём то, чей пакет — браузер.</summary>
    private AccessibilityNodeInfo? BrowserRoot()
    {
        try
        {
            var wins = Windows;
            if (wins is not null)
            {
                foreach (AccessibilityWindowInfo w in wins)
                {
                    AccessibilityNodeInfo? r = w?.Root;
                    if (r is not null && Browsers.Contains(r.PackageName ?? "")) return r;
                }
            }
        }
        catch (Exception) { }
        return RootInActiveWindow;
    }

    private static void CollectEditable(AccessibilityNodeInfo? node, List<AccessibilityNodeInfo> into)
    {
        if (node is null) return;
        try
        {
            string cls = (node.ClassName ?? "").ToString() ?? "";
            bool fieldish = node.Editable || node.Password
                || cls.Contains("EditText")
                || (node.Focusable && (cls.Contains("TextField") || cls.Contains("Input")));
            if (fieldish && node.VisibleToUser) into.Add(node);
        }
        catch (Exception) { }
        int n = node.ChildCount;
        for (int i = 0; i < n; i++) CollectEditable(node.GetChild(i), into);
    }

    private static bool HasEditable(AccessibilityNodeInfo? node)
    {
        if (node is null) return false;
        try { if (node.Editable) return true; } catch (Exception) { }
        int n = node.ChildCount;
        for (int i = 0; i < n; i++) if (HasEditable(node.GetChild(i))) return true;
        return false;
    }

    /// <summary>Домен из адресной строки браузера. Адресная строка у Яндекса ВНИЗУ, поэтому
    /// по расположению не ориентируемся: приоритет — узел с «адресным» id (url/omnibox/address/
    /// location/host), иначе любой видимый НЕ-редактируемый текст, из которого получается домен.</summary>
    private string? FindDomain(AccessibilityNodeInfo? root)
    {
        if (root is null) return null;
        string? byId = null;
        string? byText = null;
        string dbgIds = "";

        void Walk(AccessibilityNodeInfo? node)
        {
            if (node is null) return;
            try
            {
                string id = (node.ViewIdResourceName ?? "").ToLowerInvariant();
                string text = (node.Text ?? "").ToString() ?? "";
                bool editable = false; try { editable = node.Editable; } catch (Exception) { }

                if (!editable && text.Length >= 4 && !text.Contains(' '))
                {
                    string? d = ExtractDomain(text);
                    if (d is not null && d.Contains('.'))
                    {
                        bool urlish = id.Contains("url") || id.Contains("omnibox") || id.Contains("address")
                            || id.Contains("location") || id.Contains("host") || id.Contains("domain");
                        if (urlish) { if (byId is null) { byId = d; dbgIds = id; } }
                        else byText ??= d;
                    }
                }
            }
            catch (Exception) { }
            int n = node.ChildCount;
            for (int i = 0; i < n; i++) Walk(node.GetChild(i));
        }

        Walk(root);
        string? result = byId ?? byText;
        Console.WriteLine($"[IPW-A11Y] domain byId={byId}({dbgIds}) byText={byText} -> {result} pkg={_pkg}");
        return result;
    }

    private static string? ExtractDomain(string raw)
    {
        try
        {
            string s = raw.Trim();
            int sp = s.IndexOf(' '); if (sp > 0) s = s[..sp];
            if (!s.Contains("://")) s = "https://" + s;
            return Dedup.RegistrableDomain(s) is { Length: > 0 } d ? d : null;
        }
        catch (Exception) { return null; }
    }

    private static global::Android.Graphics.Rect Bounds(AccessibilityNodeInfo node)
    {
        var r = new global::Android.Graphics.Rect();
        try { node.GetBoundsInScreen(r); } catch (Exception) { }
        return r;
    }

    private int Dp(double v)
    {
        float d = Resources?.DisplayMetrics?.Density ?? 2f;
        return (int)Math.Round(v * d);
    }
}
