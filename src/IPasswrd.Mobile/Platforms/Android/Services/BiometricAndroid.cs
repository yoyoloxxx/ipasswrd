using Android.Content.PM;
using Android.OS;
using AndroidX.Biometric;
using AndroidX.Fragment.App;
using IPasswrd.Mobile.Services;
using AndroidApp = Android.App.Application;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Отпечаток / лицо (с откатом на код разблокировки устройства) — системный BiometricPrompt.
/// Полный аналог BiometricIos: та же семантика IsAvailable / Kind / AuthenticateAsync.
/// </summary>
public sealed class BiometricAndroid : IBiometricAuth
{
    // BIOMETRIC_WEAK, а не STRONG: на многих телефонах разблокировка лицом проходит только
    // как «weak». Ключ быстрой разблокировки всё равно лежит в Keystore, а не за биометрией.
    private static readonly int Weak = BiometricManager.Authenticators.BiometricWeak;
    private static readonly int DeviceCredential = BiometricManager.Authenticators.DeviceCredential;

    private static int AllowedAuthenticators =>
        // Android 11+ умеет комбинацию «биометрия ИЛИ код устройства».
        // На 8.0-10 такая комбинация не поддерживается в canAuthenticate — только биометрия.
        Build.VERSION.SdkInt >= BuildVersionCodes.R ? Weak | DeviceCredential : Weak;

    public bool IsAvailable
    {
        get
        {
            try
            {
                var mgr = BiometricManager.From(AndroidApp.Context);
                int can = mgr.CanAuthenticate(AllowedAuthenticators);
                if (can == BiometricManager.BiometricSuccess) return true;

                // На старых версиях код устройства проверяем отдельно (KeyguardManager).
                if (Build.VERSION.SdkInt < BuildVersionCodes.R)
                {
                    var km = (global::Android.App.KeyguardManager?)AndroidApp.Context
                        .GetSystemService(global::Android.Content.Context.KeyguardService);
                    return km?.IsDeviceSecure == true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string Kind
    {
        get
        {
            try
            {
                var mgr = BiometricManager.From(AndroidApp.Context);
                if (mgr.CanAuthenticate(Weak) != BiometricManager.BiometricSuccess)
                    return "Код устройства";

                PackageManager? pm = AndroidApp.Context.PackageManager;
                if (pm is null) return "Биометрия";
                if (pm.HasSystemFeature(PackageManager.FeatureFace)) return "Разблокировка по лицу";
                if (pm.HasSystemFeature(PackageManager.FeatureFingerprint)) return "Отпечаток пальца";
                if (pm.HasSystemFeature(PackageManager.FeatureIris)) return "Сканер радужки";
                return "Биометрия";
            }
            catch (Exception)
            {
                return "Биометрия";
            }
        }
    }

    public Task<bool> AuthenticateAsync(string reason)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Platform.CurrentActivity is not FragmentActivity host)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var builder = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle("Разблокировка IPasswrd")!
                    .SetSubtitle(reason)!;

                int authenticators = AllowedAuthenticators;
                builder.SetAllowedAuthenticators(authenticators);
                // Кнопка «Отмена» запрещена, когда разрешён код устройства (система рисует свою).
                if ((authenticators & DeviceCredential) == 0)
                    builder.SetNegativeButtonText("Отмена");

                var callback = new PromptCallback(tcs);
                var prompt = new BiometricPrompt(host, new MainThreadExecutor(), callback);
                prompt.Authenticate(builder.Build());
            }
            catch (Exception)
            {
                tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    private sealed class PromptCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;
        public PromptCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
            => _tcs.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
            => _tcs.TrySetResult(false);

        // OnAuthenticationFailed — одна неудачная попытка, диалог остаётся открытым: не завершаем.
    }

    private sealed class MainThreadExecutor : Java.Lang.Object, Java.Util.Concurrent.IExecutor
    {
        private readonly Handler _handler = new(Looper.MainLooper!);
        public void Execute(Java.Lang.IRunnable? command)
        {
            if (command is not null) _handler.Post(command);
        }
    }
}
