using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using IPasswrd.Mobile.Services;
using View = Android.Views.View;
using Color = Android.Graphics.Color;
using Button = Android.Widget.Button;
using Orientation = Android.Widget.Orientation;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>
/// «Сохранить пароль?» — система уже спросила пользователя, нам осталось записать пару
/// логин/пароль в сейф. Если сейф закрыт, сначала просим мастер-пароль или биометрию.
/// </summary>
[Activity(
    Label = "IPasswrd",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = false,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.Density)]
public class AutofillSaveActivity : AppCompatActivity
{
    public const string ExtraUsername = "ipw.save.user";
    public const string ExtraPassword = "ipw.save.pass";
    public const string ExtraDomain = "ipw.save.domain";
    public const string ExtraPackage = "ipw.save.package";

    private static readonly Color Bg = Color.Argb(255, 9, 12, 16);
    private static readonly Color Card = Color.Argb(255, 20, 26, 33);
    private static readonly Color Accent = Color.Argb(255, 225, 184, 94);
    private static readonly Color TextMain = Color.Argb(255, 236, 240, 245);
    private static readonly Color TextDim = Color.Argb(255, 140, 152, 165);
    private static readonly Color Danger = Color.Argb(255, 235, 110, 110);

    private string _user = "", _pass = "", _domain = "", _package = "";
    private EditText? _master;
    private TextView? _error;
    private Button? _save;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);

        _user = Intent?.GetStringExtra(ExtraUsername) ?? "";
        _pass = Intent?.GetStringExtra(ExtraPassword) ?? "";
        _domain = Intent?.GetStringExtra(ExtraDomain) ?? "";
        _package = Intent?.GetStringExtra(ExtraPackage) ?? "";

        if (_pass.Length == 0) { Finish(); return; }

        SetContentView(BuildUi());

        if (Svc.State.IsUnlocked) _ = SaveAndFinishAsync();
        else if (Svc.State.QuickUnlockAvailable) _ = TryQuickUnlockAsync();
    }

    private View BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Bg);
        root.SetPadding(Dp(20), Dp(28), Dp(20), Dp(16));

        var title = new TextView(this) { Text = "Сохранить в IPasswrd" };
        title.SetTextColor(Accent);
        title.SetTextSize(ComplexUnitType.Sp, 22);
        title.SetTypeface(null, TypefaceStyle.Bold);
        root.AddView(title);

        string target = _domain.Length > 0 ? _domain : (_package.Length > 0 ? _package : "новая запись");
        var sub = new TextView(this) { Text = target + (_user.Length > 0 ? " · " + _user : "") };
        sub.SetTextColor(TextDim);
        sub.SetTextSize(ComplexUnitType.Sp, 13);
        sub.SetPadding(0, Dp(4), 0, Dp(18));
        root.AddView(sub);

        _master = new EditText(this)
        {
            Hint = "Мастер-пароль",
            InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
        };
        _master.SetTextColor(TextMain);
        _master.SetHintTextColor(TextDim);
        _master.Background = RoundedBg(Card);
        _master.SetPadding(Dp(14), Dp(14), Dp(14), Dp(14));
        _master.Visibility = Svc.State.IsUnlocked ? ViewStates.Gone : ViewStates.Visible;
        root.AddView(_master);

        _error = new TextView(this) { Visibility = ViewStates.Gone };
        _error.SetTextColor(Danger);
        _error.SetTextSize(ComplexUnitType.Sp, 13);
        _error.SetPadding(Dp(2), Dp(10), Dp(2), 0);
        root.AddView(_error);

        _save = new Button(this) { Text = "Сохранить" };
        _save.SetAllCaps(false);
        _save.SetTextColor(Color.Argb(255, 9, 12, 16));
        _save.Background = RoundedBg(Accent);
        _save.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(50)) { TopMargin = Dp(16) };
        _save.Click += (_, _) => _ = OnSaveClickedAsync();
        root.AddView(_save);

        var cancel = new Button(this) { Text = "Не сохранять" };
        cancel.SetAllCaps(false);
        cancel.SetTextColor(TextMain);
        cancel.Background = RoundedBg(Card);
        cancel.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(50)) { TopMargin = Dp(10) };
        cancel.Click += (_, _) => Finish();
        root.AddView(cancel);

        return root;
    }

    private async Task OnSaveClickedAsync()
    {
        if (!Svc.State.IsUnlocked)
        {
            string pw = _master?.Text ?? "";
            if (pw.Length == 0) { ShowError("Введите мастер-пароль."); return; }
            string? err = await Svc.State.UnlockAsync(pw);
            if (err is not null) { ShowError(err); return; }
        }
        await SaveAndFinishAsync();
    }

    private async Task TryQuickUnlockAsync()
    {
        string? err = await Svc.State.TryQuickUnlockAsync();
        if (err is null) await SaveAndFinishAsync();
        else if (err.Length > 0) ShowError(err);
    }

    private async Task SaveAndFinishAsync()
    {
        try
        {
            await AutofillVaultWriter.SaveAsync(_user, _pass, _domain, _package);
            Toast.MakeText(this, "Сохранено в IPasswrd", ToastLength.Short)?.Show();
        }
        catch (Exception)
        {
            Toast.MakeText(this, "Не удалось сохранить", ToastLength.Short)?.Show();
        }
        Finish();
    }

    private void ShowError(string? text)
    {
        if (_error is null) return;
        _error.Text = text ?? "";
        _error.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        if (_master is not null) _master.Visibility = ViewStates.Visible;
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
}
