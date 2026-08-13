using Foundation;
using IPasswrd.Mobile.Services;
using LocalAuthentication;
using Security;

namespace IPasswrd.Mobile.Platforms.iOS.Services;

/// <summary>
/// Biometric-CRYPTO-gated store for the vault session key on iOS — parity with the desktop Hello/TPM
/// gate. The Keychain item carries a SecAccessControl requiring user presence (Face ID / Touch ID, with
/// the device passcode as fallback), so the OS itself releases the bytes only after that gesture. The
/// item is <c>WhenUnlockedThisDeviceOnly</c> — device-bound, never synced or backed up. Any failure
/// returns null/false, so AppState falls back to the master password; nothing here can lock the user out.
/// </summary>
public sealed class BiometricKeychainIos : IBiometricSecret
{
    private const string Service = "com.yoyoloxxx.ipasswrd.bio";

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var ctx = new LAContext();
                return ctx.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out NSError _);
            }
            catch (Exception) { return false; }
        }
    }

    public Task<bool> ProtectAsync(string name, byte[] data)
    {
        try
        {
            // drop any prior copy first
            SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = Service, Account = name });

            // BiometryCurrentSet (а не UserPresence): ключ привязан к ТЕКУЩЕМУ набору биометрии —
            // перезаписали Face ID / добавили палец, и запись умирает; вход уходит на мастер-пароль
            // и перезаряжается заново. Паритет с Android-инвалидацией. Passcode-фолбэк не нужен:
            // запасной вход — мастер-пароль уровнем выше.
            using var access = new SecAccessControl(
                SecAccessible.WhenUnlockedThisDeviceOnly, SecAccessControlCreateFlags.BiometryCurrentSet);
            var rec = new SecRecord(SecKind.GenericPassword)
            {
                Service = Service,
                Account = name,
                ValueData = NSData.FromArray(data),
                AccessControl = access,
            };
            SecStatusCode code = SecKeyChain.Add(rec);
            return Task.FromResult(code == SecStatusCode.Success);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    public Task<byte[]?> RevealAsync(string name, string reason) => Task.Run<byte[]?>(() =>
    {
        try
        {
            // Reading a user-presence item makes the OS present the Face ID / passcode sheet and blocks
            // until it resolves — hence the background thread so the UI stays responsive.
            var query = new SecRecord(SecKind.GenericPassword)
            {
                Service = Service,
                Account = name,
                UseOperationPrompt = reason,
            };
            NSData? data = SecKeyChain.QueryAsData(query, false, out SecStatusCode code);
            if (code != SecStatusCode.Success || data is null) return null;
            return data.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    });

    public void Delete(string name)
    {
        try { SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = Service, Account = name }); }
        catch (Exception) { }
    }
}
