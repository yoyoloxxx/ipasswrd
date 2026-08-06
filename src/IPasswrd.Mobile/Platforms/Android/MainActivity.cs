using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using IPasswrd.Mobile.Platforms.Android.Services;

namespace IPasswrd.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Менеджер паролей: запрещаем скриншоты и показ содержимого в списке недавних задач.
        // Прямого аналога на iOS нет (там система сама размывает снимок), поэтому это
        // «android-специфика», без которой приложение было бы слабее iOS-версии.
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        // Собственный реестр (SAF-пикер, сканер QR); MAUI-платформа тоже должна увидеть результат.
        ActivityResults.Deliver(requestCode, resultCode, data);
        base.OnActivityResult(requestCode, resultCode, data);
    }
}
