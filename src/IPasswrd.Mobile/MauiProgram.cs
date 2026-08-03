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
            external: new Platforms.iOS.Services.ExternalVaultFileIos());
#else
        Svc.Init(new NullQrScanner(), new NullBiometric(), new PrefsKeyStore(), new NullExternalVaultFile());
#endif

        return builder.Build();
    }
}
