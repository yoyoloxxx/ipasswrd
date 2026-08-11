using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.AppCompat.App;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>
/// Невидимый экран-хост: всплывает только системный отпечаток/лицо поверх браузера,
/// разблокирует сейф и тут же закрывается. Приложение при этом не выводится на экран —
/// так тап по кнопке автозаполнения не «перебрасывает» пользователя в IPasswrd.
/// </summary>
[Activity(
    Theme = "@style/Theme.Ipw.Transparent",
    ExcludeFromRecents = true,
    NoHistory = true,
    Exported = false,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.Density)]
public class QuickUnlockActivity : AppCompatActivity
{
    private bool _started;

    protected override void OnResume()
    {
        base.OnResume();
        if (_started) return;
        _started = true;
        // Отложенно: даём MAUI выставить Platform.CurrentActivity на этот экран,
        // иначе BiometricPrompt не найдёт host-активность.
        new Handler(Looper.MainLooper!).Post(async () =>
        {
            try
            {
                if (!Svc.State.IsUnlocked)
                    await Svc.State.TryQuickUnlockAsync();
            }
            catch (Exception) { }
            finally
            {
                Finish();
                OverridePendingTransition(0, 0);
            }
        });
    }
}
