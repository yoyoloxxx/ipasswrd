using Android.Content;
using Android.OS;
using Android.Security.Keystore;
using AndroidX.Biometric;
using AndroidX.Fragment.App;
using IPasswrd.Mobile.Services;
using Java.Security;
using Javax.Crypto;
using AndroidApp = Android.App.Application;
using JCipher = Javax.Crypto.Cipher;
using JCipherMode = Javax.Crypto.CipherMode;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Biometric-CRYPTO-gated store for the vault session key — the mobile counterpart of the desktop
/// TPM/Hello gate. Unlike <see cref="KeystoreAndroid"/> (whose key is usable without user auth, so the
/// sync refresh token can load silently in the background), here an RSA keypair lives in the hardware
/// AndroidKeyStore with the PRIVATE key marked user-authentication-required: the PUBLIC key encrypts
/// silently on save, but decrypting on reveal releases the key only after the system BiometricPrompt
/// succeeds with a STRONG biometric or the device credential (PIN/pattern/password). The key is
/// invalidated if biometric enrollment changes. Any failure returns null/false, so AppState falls back
/// to the master password — nothing here can lock the user out.
/// </summary>
public sealed class BiometricKeystoreAndroid : IBiometricSecret
{
    private const string KeystoreName = "AndroidKeyStore";
    private const string KeyAlias = "com.yoyoloxxx.ipasswrd.vaultkey.bio";
    private const string PrefsName = "ipw.bio";
    private const string Transformation = "RSA/ECB/OAEPWithSHA-256AndMGF1Padding";

    private const int StrongOrCredential =
        (int)(BiometricManager.Authenticators.BiometricStrong | BiometricManager.Authenticators.DeviceCredential);

    private static readonly object Gate = new();

    private static ISharedPreferences? Prefs =>
        AndroidApp.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);

    private static string PKey(string name) => "bio." + name;

    /// <summary>True only when a STRONG biometric (or device credential) crypto gate is actually usable;
    /// otherwise AppState keeps the (audited-adequate) un-gated quick unlock, so no device loses it.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return BiometricManager.From(AndroidApp.Context).CanAuthenticate(StrongOrCredential)
                    == BiometricManager.BiometricSuccess;
            }
            catch (Exception) { return false; }
        }
    }

    // ---- key ----

    private static IPublicKey? EnsurePublicKey()
    {
        try
        {
            KeyStore ks = KeyStore.GetInstance(KeystoreName)!;
            ks.Load(null);
            if (ks.IsKeyEntry(KeyAlias))
                return ks.GetCertificate(KeyAlias)?.PublicKey;

            var builder = new KeyGenParameterSpec.Builder(
                    KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetDigests(KeyProperties.DigestSha256)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingRsaOaep)!
                .SetKeySize(2048)!
                .SetUserAuthenticationRequired(true)!
                .SetInvalidatedByBiometricEnrollment(true)!;

            // per-use authentication (timeout 0) via strong biometric OR device credential
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                builder = builder.SetUserAuthenticationParameters(0,
                    (int)(KeyPropertiesAuthType.BiometricStrong | KeyPropertiesAuthType.DeviceCredential))!;
            else
                builder = builder.SetUserAuthenticationValidityDurationSeconds(-1)!;

            var gen = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmRsa, KeystoreName)!;
            gen.Initialize(builder.Build());
            KeyPair kp = gen.GenerateKeyPair()!;
            return kp.Public;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[IPW-BIO] EnsurePublicKey failed: " + ex.Message);
            return null;
        }
    }

    // ---- API ----

    public Task<bool> ProtectAsync(string name, byte[] data)
    {
        lock (Gate)
        {
            try
            {
                IPublicKey? pub = EnsurePublicKey();
                ISharedPreferences? prefs = Prefs;
                if (pub is null || prefs is null) return Task.FromResult(false);

                JCipher cipher = JCipher.GetInstance(Transformation)!;
                cipher.Init(JCipherMode.EncryptMode, pub);   // public-key encrypt: no user auth, silent
                byte[]? ct = cipher.DoFinal(data);
                if (ct is null || ct.Length == 0) return Task.FromResult(false);

                ISharedPreferencesEditor? ed = prefs.Edit();
                if (ed is null) return Task.FromResult(false);
                ed.PutString(PKey(name), Convert.ToBase64String(ct));
                return Task.FromResult(ed.Commit());
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IPW-BIO] Protect failed: " + ex.Message);
                return Task.FromResult(false);
            }
        }
    }

    public Task<byte[]?> RevealAsync(string name, string reason)
    {
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        byte[] ct;
        try
        {
            string? b64 = Prefs?.GetString(PKey(name), null);
            if (string.IsNullOrEmpty(b64)) { tcs.TrySetResult(null); return tcs.Task; }
            ct = Convert.FromBase64String(b64!);
        }
        catch (Exception) { tcs.TrySetResult(null); return tcs.Task; }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                KeyStore ks = KeyStore.GetInstance(KeystoreName)!;
                ks.Load(null);
                if (ks.GetKey(KeyAlias, null) is not IPrivateKey priv) { tcs.TrySetResult(null); return; }

                JCipher cipher = JCipher.GetInstance(Transformation)!;
                cipher.Init(JCipherMode.DecryptMode, priv);   // authorised for one op after BiometricPrompt

                if (Platform.CurrentActivity is not FragmentActivity host) { tcs.TrySetResult(null); return; }

                var info = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle("Разблокировка IPasswrd")!
                    .SetSubtitle(reason)!
                    .SetAllowedAuthenticators(StrongOrCredential)!
                    .Build();

                var callback = new RevealCallback(tcs, ct);
                var prompt = new BiometricPrompt(host, new MainExecutor(), callback);
                prompt.Authenticate(info, new BiometricPrompt.CryptoObject(cipher));
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IPW-BIO] Reveal failed: " + ex.Message);
                tcs.TrySetResult(null);
            }
        });

        return tcs.Task;
    }

    public void Delete(string name)
    {
        lock (Gate)
        {
            try
            {
                ISharedPreferencesEditor? ed = Prefs?.Edit();
                if (ed is not null) { ed.Remove(PKey(name)); ed.Commit(); }
            }
            catch (Exception) { }
        }
    }

    private sealed class RevealCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<byte[]?> _tcs;
        private readonly byte[] _ct;
        public RevealCallback(TaskCompletionSource<byte[]?> tcs, byte[] ct) { _tcs = tcs; _ct = ct; }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            try
            {
                JCipher? cipher = result.CryptoObject?.Cipher;
                byte[]? pt = cipher?.DoFinal(_ct);
                _tcs.TrySetResult(pt);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IPW-BIO] doFinal failed: " + ex.Message);
                _tcs.TrySetResult(null);
            }
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
            => _tcs.TrySetResult(null);
        // OnAuthenticationFailed: one bad attempt, dialog stays open — do not resolve.
    }

    private sealed class MainExecutor : Java.Lang.Object, Java.Util.Concurrent.IExecutor
    {
        private readonly Handler _handler = new(Looper.MainLooper!);
        public void Execute(Java.Lang.IRunnable? command) { if (command is not null) _handler.Post(command); }
    }
}
