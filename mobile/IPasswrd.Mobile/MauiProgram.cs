using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if IOS
        Svc.Init(
            qr: new Platforms.iOS.Services.QrScannerIos(),
            biometric: new Platforms.iOS.Services.BiometricIos(),
            keyStore: new Platforms.iOS.Services.KeychainStoreIos(),
            external: new Platforms.iOS.Services.ExternalVaultFileIos(),
            shrink: new Platforms.iOS.Services.ImageShrinkIos());
#elif ANDROID
        Svc.Init(
            qr: new Platforms.Android.Services.QrScannerAndroid(),
            biometric: new Platforms.Android.Services.BiometricAndroid(),
            keyStore: new Platforms.Android.Services.KeystoreAndroid(),
            external: new Platforms.Android.Services.ExternalVaultFileAndroid(),
            shrink: new Platforms.Android.Services.ImageShrinkAndroid());
#else
        Svc.Init(new NullQrScanner(), new NullBiometric(), new PrefsKeyStore(), new NullExternalVaultFile());
#endif

        return builder.Build();
    }
}
