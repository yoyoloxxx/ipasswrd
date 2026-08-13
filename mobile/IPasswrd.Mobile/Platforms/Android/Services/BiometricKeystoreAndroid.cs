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

    /// <summary>Явные параметры OAEP: keymaster в AndroidKeyStore умеет MGF1 только с SHA-1, а
    /// провайдер по умолчанию шифрует публичной половиной с MGF1-SHA256 — рассинхрон даёт
    /// IllegalBlockSizeException при расшифровке. Задаём одинаково с обеих сторон.</summary>
    private static Javax.Crypto.Spec.OAEPParameterSpec OaepSpec =>
        new("SHA-256", "MGF1", Java.Security.Spec.MGF1ParameterSpec.Sha1!, Javax.Crypto.Spec.PSource.PSpecified.Default!);

    private const int StrongOrCredential =
        (int)(BiometricManager.Authenticators.BiometricStrong | BiometricManager.Authenticators.DeviceCredential);

    /// <summary>API 30+: сильная биометрия или код устройства. Ниже 30 совмещённый запрос не
    /// поддерживается (BiometricManager отвечает ERROR_UNSUPPORTED) — крипто-гейт молча отключался
    /// бы на Android 8-10. Поэтому там спрашиваем только сильную биометрию; мастер-пароль и так
    /// остаётся запасным входом.</summary>
    private static int AllowedAuth =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
            ? StrongOrCredential
            : (int)BiometricManager.Authenticators.BiometricStrong;

    private static int _lastCanAuth = int.MinValue;   // последний залогированный код canAuthenticate

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
                int code = BiometricManager.From(AndroidApp.Context).CanAuthenticate(AllowedAuth);
                if (code != _lastCanAuth) { _lastCanAuth = code; Console.WriteLine("[IPW-BIO] canAuthenticate(" + AllowedAuth + ") = " + code); }
                // 0 = SUCCESS. -1 = BIOMETRIC_STATUS_UNKNOWN: старый API (EMUI/Android 10) не может
                // подтвердить «сильность» датчика заранее. Считаем гейт доступным — настоящая проверка
                // в самом аппаратном ключе: не сработает — RevealAsync вернёт null, вход уйдёт на пароль.
                if (code != BiometricManager.BiometricSuccess && code != -1) return false;

                // Решающая проверка — сам аппарат: отдаёт ли прошивка хэндл auth-bound ключа.
                // Часть EMUI создаёт пару и шифрует публичной половиной, а приватную не выдаёт
                // никогда — тогда гейт физически невозможен, честно живём старым путём. Кэшируем.
                ISharedPreferences? pr = Prefs;
                if (pr is not null && pr.GetBoolean("bio.hw.unsupported", false)) return false;
                if (pr is not null && pr.GetBoolean("bio.hw.ok", false)) return true;
                bool hw = ProbeAuthBound();
                try
                {
                    ISharedPreferencesEditor? ed0 = pr?.Edit();
                    if (ed0 is not null) { ed0.PutBoolean(hw ? "bio.hw.ok" : "bio.hw.unsupported", true); ed0.Commit(); }
                }
                catch (Exception) { }
                if (!hw) Console.WriteLine("[IPW-BIO] auth-bound keys unsupported on this firmware -> gate off");
                return hw;
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
            {
                IPublicKey? existing = ks.GetCertificate(KeyAlias)?.PublicKey;
                Java.Security.IKey? handle = null;
                try { handle = ks.GetKey(KeyAlias, null); } catch (Exception) { }
                if (existing is not null && handle is not null) return existing;
                // Алиас есть, но пара мертва (например, EMUI после инвалидации отпечатков):
                // сносим и генерируем заново, иначе гейт завис бы нерабочим навечно.
                Console.WriteLine("[IPW-BIO] EnsurePublicKey: stale alias, regenerating");
                try { ks.DeleteEntry(KeyAlias); } catch (Exception) { }
            }

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

    /// <summary>Правда ли прошивка отдаёт auth-bound ключи: пара создаётся (или уже есть) и
    /// хэндл приватной половины достаётся. Без этого крипто-гейт неработоспособен.</summary>
    private static bool ProbeAuthBound()
    {
        try
        {
            if (EnsurePublicKey() is null) return false;
            KeyStore ks = KeyStore.GetInstance(KeystoreName)!;
            ks.Load(null);
            return ks.GetKey(KeyAlias, null) is not null;
        }
        catch (Exception ex) { Console.WriteLine("[IPW-BIO] probe: " + ex.Message); return false; }
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
                cipher.Init(JCipherMode.EncryptMode, pub, OaepSpec);   // public-key encrypt: no user auth, silent
                byte[]? ct = cipher.DoFinal(data);
                if (ct is null || ct.Length == 0) return Task.FromResult(false);

                ISharedPreferencesEditor? ed = prefs.Edit();
                if (ed is null) return Task.FromResult(false);
                ed.PutString(PKey(name), Convert.ToBase64String(ct));
                bool stored = ed.Commit();
                // Проба: отдаёт ли прошивка хэндл приватной половины. На части EMUI ключ создаётся
                // и шифрует, а приватный хэндл не отдаёт — гейт нерабочий; честно возвращаем false,
                // и AppState тихо остаётся на старом пути (быстрый вход не теряется).
                if (stored)
                {
                    try
                    {
                        KeyStore ks2 = KeyStore.GetInstance(KeystoreName)!;
                        ks2.Load(null);
                        if (ks2.GetKey(KeyAlias, null) is null)
                        { Console.WriteLine("[IPW-BIO] Protect: private handle unavailable -> false"); return Task.FromResult(false); }
                    }
                    catch (Exception ex2)
                    { Console.WriteLine("[IPW-BIO] Protect probe: " + ex2.Message); return Task.FromResult(false); }
                }
                return Task.FromResult(stored);
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
            if (string.IsNullOrEmpty(b64)) { Console.WriteLine("[IPW-BIO] Reveal: no blob"); tcs.TrySetResult(null); return tcs.Task; }
            ct = Convert.FromBase64String(b64!);
        }
        catch (Exception) { tcs.TrySetResult(null); return tcs.Task; }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                KeyStore ks = KeyStore.GetInstance(KeystoreName)!;
                ks.Load(null);
                // ВАЖНО: не проверять «is IPrivateKey» — Xamarin оборачивает ключ из AndroidKeyStore
                // инвокером, который реализует только IKey, и проверка типа всегда падала, хотя ключ
                // настоящий. Cipher.Init принимает IKey напрямую.
                Java.Security.IKey? priv = ks.GetKey(KeyAlias, null);
                if (priv is null) { Console.WriteLine("[IPW-BIO] Reveal: private key handle is null"); tcs.TrySetResult(null); return; }

                JCipher cipher = JCipher.GetInstance(Transformation)!;
                cipher.Init(JCipherMode.DecryptMode, priv, OaepSpec);   // authorised for one op after BiometricPrompt

                if (Platform.CurrentActivity is not FragmentActivity host) { Console.WriteLine("[IPW-BIO] Reveal: no FragmentActivity"); tcs.TrySetResult(null); return; }

                var ib = new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle("Разблокировка IPasswrd")!
                    .SetSubtitle(reason)!;
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    ib = ib.SetAllowedAuthenticators(AllowedAuth)!;
                else
                    // API < 30: setAllowedAuthenticators вместе с CryptoObject валит промпт мгновенной
                    // ошибкой ещё до показа (EMUI). Рецепт совместимости: только негативная кнопка —
                    // androidx сам поведёт через сильную биометрию (FingerprintManager-путь).
                    ib = ib.SetNegativeButtonText("Мастер-пароль")!;
                BiometricPrompt.PromptInfo info = ib.Build()!;

                var callback = new RevealCallback(tcs, ct, name);
                var prompt = new BiometricPrompt(host, new MainExecutor(), callback);
                prompt.Authenticate(info, new BiometricPrompt.CryptoObject(cipher));
            }
            catch (Exception ex)
            {
                // Сюда прилетает и KeyPermanentlyInvalidatedException — набор отпечатков изменился.
                // Сносим мёртвую пару и блоб: после входа мастер-паролем гейт перезарядится свежим
                // ключом под новый набор, и биометрия оживёт сама.
                Console.WriteLine("[IPW-BIO] Reveal failed: " + ex.Message + " - resetting gate");
                try { KeyStore ksx = KeyStore.GetInstance(KeystoreName)!; ksx.Load(null); ksx.DeleteEntry(KeyAlias); } catch (Exception) { }
                try { ISharedPreferencesEditor? edx = Prefs?.Edit(); if (edx is not null) { edx.Remove(PKey(name)); edx.Commit(); } } catch (Exception) { }
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
        private readonly string _name;
        public RevealCallback(TaskCompletionSource<byte[]?> tcs, byte[] ct, string name) { _tcs = tcs; _ct = ct; _name = name; }

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
                Console.WriteLine("[IPW-BIO] doFinal failed: " + ex.Message + " - wiping stale blob");
                try { ISharedPreferencesEditor? edw = Prefs?.Edit(); if (edw is not null) { edw.Remove(PKey(_name)); edw.Commit(); } } catch (Exception) { }
                _tcs.TrySetResult(null);
            }
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString)
        {
            Console.WriteLine("[IPW-BIO] prompt error " + errorCode + ": " + errString);
            _tcs.TrySetResult(null);
        }
        // OnAuthenticationFailed: one bad attempt, dialog stays open — do not resolve.
    }

    private sealed class MainExecutor : Java.Lang.Object, Java.Util.Concurrent.IExecutor
    {
        private readonly Handler _handler = new(Looper.MainLooper!);
        public void Execute(Java.Lang.IRunnable? command) { if (command is not null) _handler.Post(command); }
    }
}
