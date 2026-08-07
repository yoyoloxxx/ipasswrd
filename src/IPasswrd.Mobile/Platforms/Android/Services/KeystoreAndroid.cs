using System.Security.Cryptography;
using Android.Content;
using Android.Security.Keystore;
using IPasswrd.Mobile.Services;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using AndroidApp = Android.App.Application;
using CipherMode = Javax.Crypto.CipherMode;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Аналог Keychain-хранилища iOS. Маленькие секреты (сессионный ключ быстрой разблокировки,
/// refresh-токен Google) шифруются AES-256-GCM ключом из аппаратного Android Keystore
/// и кладутся в приватные SharedPreferences приложения.
///
/// Ключ Keystore не покидает устройство и не экспортируется — как
/// kSecAttrAccessibleWhenUnlockedThisDeviceOnly на iOS. Бэкап приложения выключен
/// (allowBackup=false), так что зашифрованный блоб тоже никуда не уезжает.
/// </summary>
public sealed class KeystoreAndroid : ISecureKeyStore
{
    private const string KeystoreName = "AndroidKeyStore";
    private const string KeyAlias = "com.yoyoloxxx.ipasswrd.secrets";
    private const string PrefsName = "ipw.secure";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;
    private const int IvBytes = 12;

    private static readonly object Gate = new();

    private static ISharedPreferences? Prefs =>
        AndroidApp.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);

    // ================= ключ Keystore =================

    private static ISecretKey? EnsureKey()
    {
        try
        {
            KeyStore ks = KeyStore.GetInstance(KeystoreName)!;
            ks.Load(null);

            if (ks.IsKeyEntry(KeyAlias))
            {
                if (ks.GetKey(KeyAlias, null) is ISecretKey existing) return existing;
                ks.DeleteEntry(KeyAlias);   // запись есть, но ключ не читается — пересоздаём
            }

            KeyGenerator gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreName)!;
            var spec = new KeyGenParameterSpec.Builder(
                    KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                // IV генерируем сами (RandomNumberGenerator, свежий на каждую запись) — иначе
                // пришлось бы читать его у провайдера, а имя биндинга getIV() нестабильно.
                .SetRandomizedEncryptionRequired(false)!
                .SetUserAuthenticationRequired(false)!
                .Build();
            gen.Init(spec);
            return gen.GenerateKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[IPW] keystore EnsureKey failed: " + ex);
            return null;
        }
    }

    // ================= API =================

    public bool Save(string name, byte[] data)
    {
        lock (Gate)
        {
            try
            {
                ISecretKey? key = EnsureKey();
                ISharedPreferences? prefs = Prefs;
                if (key is null || prefs is null) return false;

                byte[] iv = RandomNumberGenerator.GetBytes(IvBytes);
                Cipher cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.EncryptMode, key, new GCMParameterSpec(GcmTagBits, iv));
                byte[]? ct = cipher.DoFinal(data);
                if (ct is null) return false;

                byte[] blob = new byte[1 + iv.Length + ct.Length];
                blob[0] = (byte)iv.Length;
                Buffer.BlockCopy(iv, 0, blob, 1, iv.Length);
                Buffer.BlockCopy(ct, 0, blob, 1 + iv.Length, ct.Length);

                ISharedPreferencesEditor? ed = prefs.Edit();
                if (ed is null) return false;
                ed.PutString(Key(name), Convert.ToBase64String(blob));
                bool ok = ed.Commit();
                if (!ok) Console.WriteLine("[IPW] keystore Save: prefs commit=false for " + name);
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IPW] keystore Save failed for " + name + ": " + ex);
                return false;
            }
        }
    }

    public byte[]? Load(string name)
    {
        lock (Gate)
        {
            try
            {
                ISharedPreferences? prefs = Prefs;
                string? b64 = prefs?.GetString(Key(name), null);
                if (string.IsNullOrEmpty(b64)) return null;

                byte[] blob = Convert.FromBase64String(b64!);
                if (blob.Length < 2) return null;
                int ivLen = blob[0];
                if (ivLen <= 0 || blob.Length < 1 + ivLen + 1) return null;

                byte[] iv = new byte[ivLen];
                Buffer.BlockCopy(blob, 1, iv, 0, ivLen);
                byte[] ct = new byte[blob.Length - 1 - ivLen];
                Buffer.BlockCopy(blob, 1 + ivLen, ct, 0, ct.Length);

                ISecretKey? key = EnsureKey();
                if (key is null) return null;

                Cipher cipher = Cipher.GetInstance(Transformation)!;
                cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(GcmTagBits, iv));
                return cipher.DoFinal(ct);
            }
            catch (Exception ex)
            {
                // ключ Keystore мог быть сброшен (смена блокировки экрана, сброс биометрии) —
                // тогда быстрая разблокировка просто перестаёт работать и просит мастер-пароль
                Console.WriteLine("[IPW] keystore Load failed for " + name + ": " + ex);
                return null;
            }
        }
    }

    public void Delete(string name)
    {
        lock (Gate)
        {
            try
            {
                ISharedPreferencesEditor? ed = Prefs?.Edit();
                if (ed is null) return;
                ed.Remove(Key(name));
                ed.Commit();
            }
            catch (Exception) { }
        }
    }

    private static string Key(string name) => "ks." + name;
}
