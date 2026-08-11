using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace IPasswrd.Core;

/// <summary>KDF cost. Persisted in the vault header so it can be raised later.</summary>
public readonly record struct KdfConfig(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>64 MiB / t=3 / p=1 — mobile-safe strong baseline (≈ Bitwarden's Argon2 default).</summary>
    public static readonly KdfConfig Default = new(65536, 3, 1);

    /// <summary>Deliberately weak — unit tests only, never for real vaults.</summary>
    public static readonly KdfConfig Fast = new(8192, 1, 1);

    // Ceiling so a tampered vault header can't ask Argon2 for absurd memory/time and wedge the app
    // on unlock (resource-exhaustion DoS) before any authentication happens.
    private const int MaxMemoryKiB = 1 << 21;   // 2 GiB
    private const int MaxIterations = 40;
    private const int MaxParallelism = 16;

    /// <summary>
    /// Cap cost read from an untrusted vault header to a sane ceiling, so a bloated header cannot
    /// exhaust memory/CPU on unlock (DoS). Does NOT raise a legitimately-lower cost, so it never
    /// changes the key derived for an existing vault — safe on every load / unlock / merge path.
    /// </summary>
    public KdfConfig Capped() => new(
        Math.Clamp(MemoryKiB <= 0 ? Default.MemoryKiB : MemoryKiB, 1, MaxMemoryKiB),
        Math.Clamp(Iterations <= 0 ? Default.Iterations : Iterations, 1, MaxIterations),
        Math.Clamp(Parallelism <= 0 ? Default.Parallelism : Parallelism, 1, MaxParallelism));

    /// <summary>
    /// Floor to the strong baseline AND cap. Used only when a NEW wrap is created (recovery reset),
    /// so a tampered/downgraded header can never weaken the KDF that will protect the reset vault.
    /// </summary>
    public KdfConfig Sanitized() => new(
        Math.Clamp(MemoryKiB <= 0 ? Default.MemoryKiB : MemoryKiB, Default.MemoryKiB, MaxMemoryKiB),
        Math.Clamp(Iterations <= 0 ? Default.Iterations : Iterations, Default.Iterations, MaxIterations),
        Math.Clamp(Parallelism <= 0 ? Default.Parallelism : Parallelism, Default.Parallelism, MaxParallelism));
}

/// <summary>
/// Low-level primitives: Argon2id key derivation and AES-256-GCM sealing.
/// Kept in one place so the algorithm choice can be swapped without touching Vault.
/// </summary>
internal static class Crypto
{
    public const int KeyLen = 32;    // 256-bit
    public const int NonceLen = 12;  // AES-GCM standard nonce
    public const int TagLen = 16;
    public const int SaltLen = 16;

    public static readonly byte[] AadVaultKey = Encoding.ASCII.GetBytes("ipasswrd/vault-key/v1");

    /// <summary>Domain separation: a recovery envelope can never be swapped in for the master one.</summary>
    public static readonly byte[] AadRecoveryKey = Encoding.ASCII.GetBytes("ipasswrd/recovery-key/v1");
    private static readonly byte[] AadRecordPrefix = Encoding.ASCII.GetBytes("ipasswrd/record/v1/");

    public static byte[] RecordAad(string id)
    {
        byte[] idBytes = Encoding.ASCII.GetBytes(id);
        byte[] aad = new byte[AadRecordPrefix.Length + idBytes.Length];
        Buffer.BlockCopy(AadRecordPrefix, 0, aad, 0, AadRecordPrefix.Length);
        Buffer.BlockCopy(idBytes, 0, aad, AadRecordPrefix.Length, idBytes.Length);
        return aad;
    }

    public static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>Argon2id(password, salt, params) → 32-byte key.</summary>
    public static byte[] DeriveKey(string password, byte[] salt, KdfConfig cfg)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = cfg.Parallelism,
            Iterations = cfg.Iterations,
            MemorySize = cfg.MemoryKiB,   // Konscious expects KiB
        };
        return argon2.GetBytes(KeyLen);
    }

    /// <summary>AES-256-GCM encrypt; returns cipher||tag (matches libsodium/pyca layout).</summary>
    public static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        using var aes = new AesGcm(key, TagLen);
        byte[] cipher = new byte[plaintext.Length];
        byte[] tag = new byte[TagLen];
        aes.Encrypt(nonce, plaintext, cipher, tag, aad);

        byte[] combined = new byte[cipher.Length + TagLen];
        Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, cipher.Length, TagLen);
        return combined;
    }

    /// <summary>AES-256-GCM decrypt of cipher||tag. Throws <see cref="CryptographicException"/> on auth failure.</summary>
    public static byte[] Open(byte[] key, byte[] nonce, byte[] combined, byte[] aad)
    {
        if (combined.Length < TagLen)
            throw new CryptographicException("ciphertext too short");

        int cipherLen = combined.Length - TagLen;
        var cipher = new ReadOnlySpan<byte>(combined, 0, cipherLen);
        var tag = new ReadOnlySpan<byte>(combined, cipherLen, TagLen);

        using var aes = new AesGcm(key, TagLen);
        byte[] plaintext = new byte[cipherLen];
        aes.Decrypt(nonce, cipher, tag, plaintext, aad);
        return plaintext;
    }
}
