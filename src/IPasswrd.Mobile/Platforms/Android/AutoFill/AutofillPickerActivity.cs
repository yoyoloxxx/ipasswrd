using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Service.Autofill;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.Autofill;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.AppCompat.App;
using IPasswrd.Core;
using IPasswrd.Mobile.Services;
using AndroidResult = Android.App.Result;
using View = Android.Views.View;
using Color = Android.Graphics.Color;
using Button = Android.Widget.Button;
using Orientation = Android.Widget.Orientation;
using ListView = Android.Widget.ListView;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>
/// Экран, который открывается из подсказки автозаполнения: при необходимости разблокирует
/// сейф (мастер-пароль или отпечаток/лицо), затем даёт выбрать запись. Выбранная запись
/// возвращается системе как Dataset — она сама подставит значения в поля.
/// </summary>
[Activity(
    Label = "IPasswrd",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = false,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.Density)]
public class AutofillPickerActivity : AppCompatActivity
{
    public const string ExtraUsernameId = "ipw.af.user";
    public const string ExtraPasswordId = "ipw.af.pass";
    public const string ExtraOtpId = "ipw.af.otp";
    public const string ExtraDomain = "ipw.af.domain";
    public const string ExtraPackage = "ipw.af.package";

    private static readonly Color Bg = Color.Argb(255, 9, 12, 16);
    private static readonly Color Card = Color.Argb(255, 20, 26, 33);
    private static readonly Color Accent = Color.Argb(255, 225, 184, 94);
    private static readonly Color Text1 = Color.Argb(255, 236, 240, 245);
    private static readonly Color Text3 = Color.Argb(255, 140, 152, 165);
    private static readonly Color Danger = Color.Argb(255, 235, 110, 110);

    private AutofillId? _userId, _passId, _otpId;
    private string _domain = "", _package = "";

    private LinearLayout? _root;
    private LinearLayout? _unlockBox;
    private LinearLayout? _listBox;
    private EditText? _master;
    private TextView? _error;
    private EditText? _search;
    private ListView? _list;
    private CandidateAdapter? _adapter;
    private List<AutofillCandidate> _all = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);

        Intent? intent = Intent;
        _userId = GetId(intent, ExtraUsernameId);
        _passId = GetId(intent, ExtraPasswordId);
        _otpId = GetId(intent, ExtraOtpId);
        _domain = intent?.GetStringExtra(ExtraDomain) ?? "";
        _package = intent?.GetStringExtra(ExtraPackage) ?? "";

        SetResult(AndroidResult.Canceled);
        SetContentView(BuildUi());

        if (Svc.State.IsUnlocked) ShowList();
        else
        {
            ShowUnlock();
            if (Svc.State.QuickUnlockAvailable) _ = TryQuickUnlockAsync();
        }
    }

    private static AutofillId? GetId(Intent? intent, string key)
    {
        try { return intent?.GetParcelableExtra(key) as AutofillId; }
        catch (Exception) { return null; }
    }

    // ================= интерфейс =================

    private View BuildUi()
    {
        _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.SetBackgroundColor(Bg);
        _root.SetPadding(Dp(20), Dp(28), Dp(20), Dp(16));

        var title = new TextView(this) { Text = "IPasswrd" };
        title.SetTextColor(Accent);
        title.SetTextSize(ComplexUnitType.Sp, 22);
        title.SetTypeface(null, TypefaceStyle.Bold);
        _root.AddView(title);

        var subtitle = new TextView(this) { Text = TargetLabel() };
        subtitle.SetTextColor(Text3);
        subtitle.SetTextSize(ComplexUnitType.Sp, 13);
        subtitle.SetPadding(0, Dp(4), 0, Dp(18));
        _root.AddView(subtitle);

        _root.AddView(BuildUnlockBox());
        _root.AddView(BuildListBox());
        return _root;
    }

    private string TargetLabel()
    {
        if (_domain.Length > 0) return "Заполнение для " + _domain;
        if (_package.Length > 0) return "Заполнение для " + _package;
        return "Выбор записи";
    }

    private View BuildUnlockBox()
    {
        _unlockBox = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent),
        };

        _master = new EditText(this)
        {
            Hint = "Мастер-пароль",
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
        };
        _master.SetTextColor(Text1);
        _master.SetHintTextColor(Text3);
        _master.Background = RoundedBg(Card);
        _master.SetPadding(Dp(14), Dp(14), Dp(14), Dp(14));
        _master.EditorAction += (_, e) =>
        {
            if (e.ActionId == ImeAction.Done || e.ActionId == ImeAction.Go) { e.Handled = true; _ = UnlockAsync(); }
        };
        _unlockBox.AddView(_master);

        _error = new TextView(this) { Visibility = ViewStates.Gone };
        _error.SetTextColor(Danger);
        _error.SetTextSize(ComplexUnitType.Sp, 13);
        _error.SetPadding(Dp(2), Dp(10), Dp(2), 0);
        _unlockBox.AddView(_error);

        var unlock = new Button(this) { Text = "Разблокировать" };
        unlock.SetAllCaps(false);
        unlock.SetTextColor(Color.Argb(255, 9, 12, 16));
        unlock.Background = RoundedBg(Accent);
        unlock.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(50)) { TopMargin = Dp(16) };
        unlock.Click += (_, _) => _ = UnlockAsync();
        _unlockBox.AddView(unlock);

        if (Svc.State.QuickUnlockAvailable)
        {
            var bio = new Button(this) { Text = Svc.Biometric.Kind };
            bio.SetAllCaps(false);
            bio.SetTextColor(Text1);
            bio.Background = RoundedBg(Card);
            bio.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(50)) { TopMargin = Dp(10) };
            bio.Click += (_, _) => _ = TryQuickUnlockAsync();
            _unlockBox.AddView(bio);
        }

        return _unlockBox;
    }

    private View BuildListBox()
    {
        _listBox = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone,
            // 0 + вес: в вертикальном LinearLayout это «занять весь остаток»,
            // MatchParent без веса ведёт себя непредсказуемо
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f),
        };

        _search = new EditText(this)
        {
            Hint = "Поиск по записям",
            InputType = InputTypes.ClassText,
        };
        _search.SetTextColor(Text1);
        _search.SetHintTextColor(Text3);
        _search.Background = RoundedBg(Card);
        _search.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        _search.TextChanged += (_, _) => ApplyFilter();
        _listBox.AddView(_search);

        _list = new ListView(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f) { TopMargin = Dp(12) },
        };
        _list.SetBackgroundColor(Bg);
        _list.Divider = null;
        _list.DividerHeight = Dp(8);   // прозрачные промежутки между карточками
        _list.ItemClick += (_, e) =>
        {
            AutofillCandidate? c = _adapter?.ItemAt(e.Position);
            if (c is not null) Deliver(c);
        };
        _listBox.AddView(_list);

        return _listBox;
    }

    private Drawable RoundedBg(Color color)
    {
        var d = new GradientDrawable();
        d.SetShape(ShapeType.Rectangle);
        d.SetCornerRadius(Dp(12));
        d.SetColor(color);
        return d;
    }

    private int Dp(double value)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2f;
        return (int)Math.Round(value * density);
    }

    // ================= состояния =================

    private void ShowUnlock()
    {
        if (_unlockBox is not null) _unlockBox.Visibility = ViewStates.Visible;
        if (_listBox is not null) _listBox.Visibility = ViewStates.Gone;
        _master?.RequestFocus();
    }

    private void ShowList()
    {
        Vault? vault = Svc.State.Vault;
        if (vault is null) { ShowUnlock(); return; }

        _all = AutofillMatcher.Rank(vault, _domain.Length > 0 ? _domain : null,
                                    _package.Length > 0 ? _package : null);

        _adapter = new CandidateAdapter(this, _all);
        if (_list is not null) _list.Adapter = _adapter;

        if (_unlockBox is not null) _unlockBox.Visibility = ViewStates.Gone;
        if (_listBox is not null) _listBox.Visibility = ViewStates.Visible;

        HideKeyboard();
    }

    private void ApplyFilter()
    {
        string q = (_search?.Text ?? "").Trim();
        List<AutofillCandidate> shown = q.Length == 0
            ? _all
            : _all.Where(c => c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                           || c.Login.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _adapter?.Replace(shown);
    }

    private async Task UnlockAsync()
    {
        string pw = _master?.Text ?? "";
        if (pw.Length == 0) { ShowError("Введите мастер-пароль."); return; }

        ShowError(null);
        string? err = await Svc.State.UnlockAsync(pw);
        if (err is not null) { ShowError(err); return; }

        if (_master is not null) _master.Text = "";
        ShowList();
    }

    private async Task TryQuickUnlockAsync()
    {
        string? err = await Svc.State.TryQuickUnlockAsync();
        if (err is null) { ShowList(); return; }
        if (err.Length > 0) ShowError(err);
    }

    private void ShowError(string? text)
    {
        if (_error is null) return;
        _error.Text = text ?? "";
        _error.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    private void HideKeyboard()
    {
        try
        {
            var imm = (InputMethodManager?)GetSystemService(global::Android.Content.Context.InputMethodService);
            imm?.HideSoftInputFromWindow(_root?.WindowToken, HideSoftInputFlags.None);
        }
        catch (Exception) { }
    }

    // ================= результат =================

    private void Deliver(AutofillCandidate c)
    {
        try
        {
            Vault? vault = Svc.State.Vault;
            string login = c.Login;
            string password = c.Password;
            string? code = (_otpId is not null && vault is not null)
                ? AutofillMatcher.CodeFor(vault, c.Item) : null;

            bool any = (_userId is not null && login.Length > 0)
                    || (_passId is not null && password.Length > 0)
                    || (_otpId is not null && !string.IsNullOrEmpty(code));
            if (!any) { Finish(); return; }

            string subtitle = login.Length > 0 ? login : "без логина";
            var ds = new Dataset.Builder(IpwAutofillService.Presentation(c.Title, subtitle));
            if (_userId is not null && login.Length > 0)
                ds.SetValue(_userId, AutofillValue.ForText(login));
            if (_passId is not null && password.Length > 0)
                ds.SetValue(_passId, AutofillValue.ForText(password));
            if (_otpId is not null && !string.IsNullOrEmpty(code))
                ds.SetValue(_otpId, AutofillValue.ForText(code));

            var data = new Intent();
            data.PutExtra(AutofillManager.ExtraAuthenticationResult, ds.Build());
            SetResult(AndroidResult.Ok, data);
        }
        catch (Exception)
        {
            SetResult(AndroidResult.Canceled);
        }
        Finish();
    }

    // ================= список =================

    private sealed class CandidateAdapter : BaseAdapter<AutofillCandidate>
    {
        private readonly AutofillPickerActivity _host;
        private List<AutofillCandidate> _items;

        public CandidateAdapter(AutofillPickerActivity host, List<AutofillCandidate> items)
        {
            _host = host;
            _items = items;
        }

        public void Replace(List<AutofillCandidate> items)
        {
            _items = items;
            NotifyDataSetChanged();
        }

        public AutofillCandidate? ItemAt(int position) =>
            position >= 0 && position < _items.Count ? _items[position] : null;

        public override int Count => _items.Count;
        public override AutofillCandidate this[int position] => _items[position];
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var row = convertView as LinearLayout;
            RowHolder holder;

            if (row is null || row.Tag is not RowHolder existing)
            {
                row = new LinearLayout(_host) { Orientation = Orientation.Vertical };
                row.SetPadding(_host.Dp(14), _host.Dp(12), _host.Dp(14), _host.Dp(12));
                row.Background = _host.RoundedBg(Card);
                row.LayoutParameters = new AbsListView.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

                var title = new TextView(_host);
                title.SetTextColor(Text1);
                title.SetTextSize(ComplexUnitType.Sp, 16);
                title.SetMaxLines(1);
                row.AddView(title);

                var sub = new TextView(_host);
                sub.SetTextColor(Text3);
                sub.SetTextSize(ComplexUnitType.Sp, 13);
                sub.SetMaxLines(1);
                row.AddView(sub);

                holder = new RowHolder(title, sub);
                row.Tag = holder;   // ключевой SetTag(int,…) требует id из своего пакета — обходимся простым Tag
            }
            else
            {
                holder = existing;
            }

            AutofillCandidate c = _items[position];
            holder.Title.Text = c.Title;
            holder.Sub.Text = c.Login.Length > 0 ? c.Login : "без логина";
            return row;
        }

        private sealed class RowHolder : Java.Lang.Object
        {
            public RowHolder(TextView title, TextView sub) { Title = title; Sub = sub; }
            public TextView Title { get; }
            public TextView Sub { get; }
        }
    }
}
