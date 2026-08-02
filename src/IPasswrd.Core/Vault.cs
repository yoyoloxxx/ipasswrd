using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace IPasswrd.Core;

/// <summary>
/// An unlocked vault held in memory. Envelope encryption with a KEK/DEK split:
///
///   master password ──Argon2id(salt, cfg)──▶ KEK
///   random 32-byte DEK, wrapped by the KEK (AES-256-GCM)
///   each record encrypted with the DEK (AES-256-GCM), AAD-bound to its id
///
/// Changing the master password only re-wraps the DEK, so records are never
/// re-encrypted. The vault serialises to a self-contained JSON blob, so it is
/// independent of how it is stored or synced. See the executable spec in
/// reference/vault_reference.py; this mirrors the same construction.
/// </summary>
public sealed class Vault
{
    private const int Format = 1;

    private KdfConfig _cfg;
    private byte[] _salt;
    private readonly byte[] _dek;
    private BlobDto _wrapped;
    private readonly List<RecordDto> _records;
    private string _vaultId;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    private Vault(KdfConfig cfg, byte[] salt, byte[] dek, BlobDto wrapped, List<RecordDto> records, string vaultId)
    {
        _cfg = cfg;
        _salt = salt;
        _dek = dek;
        _wrapped = wrapped;
        _records = records;
        _vaultId = vaultId;
    }

    /// <summary>Stable clear-text id of this vault's lineage (survives password changes; used to guard sync merges).</summary>
    public string VaultId => _vaultId;

    // ---- lifecycle ----

    /// <summary>Create a brand-new empty vault protected by <paramref name="masterPassword"/>.</summary>
    public static Vault Create(string masterPassword, KdfConfig? config = null)
    {
        KdfConfig cfg = config ?? KdfConfig.Default;
        byte[] salt = Crypto.RandomBytes(Crypto.SaltLen);
        byte[] dek = Crypto.RandomBytes(Crypto.KeyLen);
        BlobDto wrapped = Wrap(Crypto.DeriveKey(masterPassword, salt, cfg), dek);
        return new Vault(cfg, salt, dek, wrapped, new List<RecordDto>(), Guid.NewGuid().ToString());
    }

    /// <summary>Unlock a serialised vault. Throws <see cref="WrongMasterPasswordException"/> on a bad password.</summary>
    public static Vault Unlock(byte[] blob, string masterPassword)
    {
        VaultDocumentDto doc = JsonSerializer.Deserialize<VaultDocumentDto>(blob, Json)
                               ?? throw new FormatException("empty vault blob");
        if (doc.Format != Format)
            throw new NotSupportedException($"unsupported vault format: {doc.Format}");

        var cfg = new KdfConfig(doc.Kdf.MemoryKiB, doc.Kdf.Iterations, doc.Kdf.Parallelism);
        byte[] salt = Convert.FromBase64String(doc.Kdf.Salt);
        byte[] kek = Crypto.DeriveKey(masterPassword, salt, cfg);
        byte[] dek = Unwrap(kek, doc.WrappedKey);   // throws WrongMasterPasswordException
        string vaultId = string.IsNullOrEmpty(doc.VaultId) ? Guid.NewGuid().ToString() : doc.VaultId;
        return new Vault(cfg, salt, dek, doc.WrappedKey, doc.Records, vaultId);
    }

    /// <summary>Copy of the session key (DEK) for OS-protected quick unlock. Handle with care.</summary>
    public byte[] ExportSessionKey() => (byte[])_dek.Clone();

    /// <summary>
    /// Reopen a serialised vault with a cached session key, skipping the KDF (quick unlock).
    /// The key is verified against the first live record; a wrong key throws
    /// <see cref="WrongMasterPasswordException"/>. An empty vault is accepted as-is.
    /// </summary>
    public static Vault UnlockWithSessionKey(byte[] blob, byte[] dek)
    {
        VaultDocumentDto doc = JsonSerializer.Deserialize<VaultDocumentDto>(blob, Json)
                               ?? throw new FormatException("empty vault blob");
        if (doc.Format != Format)
            throw new NotSupportedException($"unsupported vault format: {doc.Format}");

        var cfg = new KdfConfig(doc.Kdf.MemoryKiB, doc.Kdf.Iterations, doc.Kdf.Parallelism);
        byte[] salt = Convert.FromBase64String(doc.Kdf.Salt);
        string vaultId = string.IsNullOrEmpty(doc.VaultId) ? Guid.NewGuid().ToString() : doc.VaultId;
        var v = new Vault(cfg, salt, dek, doc.WrappedKey, doc.Records, vaultId);

        foreach (RecordDto r in doc.Records)
        {
            if (r.Deleted) continue;
            try { v.DecryptRecord(r); }
            catch (VaultIntegrityException) { throw new WrongMasterPasswordException(); }
            break;
        }
        return v;
    }

    /// <summary>Serialise to a self-contained JSON blob. No password needed (the wrapped key is cached).</summary>
    public byte[] Serialize()
    {
        var doc = new VaultDocumentDto
        {
            Format = Format,
            VaultId = _vaultId,
            Kdf = new KdfDto
            {
                Algorithm = "argon2id",
                MemoryKiB = _cfg.MemoryKiB,
                Iterations = _cfg.Iterations,
                Parallelism = _cfg.Parallelism,
                Salt = Convert.ToBase64String(_salt),
            },
            WrappedKey = _wrapped,
            // canonical order: identical content on two devices serialises to identical bytes,
            // so folder-sync (iCloud/Drive) converges instead of ping-ponging re-uploads
            Records = _records.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, Json);
    }

    // ---- records ----

    public string Add(VaultItem item)
    {
        string id = Guid.NewGuid().ToString();
        _records.Add(EncryptRecord(id, item));
        return id;
    }

    public void Update(string id, VaultItem item)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (_records[i].Id == id && !_records[i].Deleted)
            {
                _records[i] = EncryptRecord(id, item);
                return;
            }
        }
        throw new KeyNotFoundException(id);
    }

    /// <summary>Insert-or-replace a record at a caller-chosen, stable id. Used for app-managed
    /// singletons (e.g. synced preferences) so both devices address the same record and the
    /// last-write-wins merge applies. Revives a tombstoned id if it was previously deleted.</summary>
    public void Put(string id, VaultItem item)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (_records[i].Id == id)
            {
                _records[i] = EncryptRecord(id, item);
                return;
            }
        }
        _records.Add(EncryptRecord(id, item));
    }

    /// <summary>Tombstone the record (kept for last-write-wins sync) and drop its payload.</summary>
    public void Delete(string id)
    {
        foreach (RecordDto rec in _records)
        {
            if (rec.Id == id)
            {
                rec.Deleted = true;
                rec.Nonce = "";
                rec.Ciphertext = "";
                rec.UpdatedAt = NowIso();
                return;
            }
        }
        throw new KeyNotFoundException(id);
    }

    public IReadOnlyList<VaultEntry> Items()
    {
        var list = new List<VaultEntry>();
        foreach (RecordDto rec in _records)
            if (!rec.Deleted)
                list.Add(new VaultEntry(rec.Id, DecryptRecord(rec), rec.UpdatedAt));
        return list;
    }

    public VaultItem Get(string id)
    {
        foreach (RecordDto rec in _records)
            if (rec.Id == id && !rec.Deleted)
                return DecryptRecord(rec);
        throw new KeyNotFoundException(id);
    }

    // ---- sync merge ----

    /// <summary>
    /// Merge another serialisation of the SAME vault (a copy synced from another device)
    /// into this one. For each record id, keep whichever side has the newer <c>UpdatedAt</c> —
    /// tombstones included, so deletions propagate too. Records stay encrypted the whole time
    /// (same DEK lineage), so no password is needed. The local key envelope is kept, so this
    /// vault still unlocks with the local master password afterwards. Refuses to merge a
    /// different vault (mismatched <see cref="VaultId"/>). Returns the number of records that
    /// were added or replaced from <paramref name="otherBlob"/>.
    /// </summary>
    public int MergeFrom(byte[] otherBlob)
    {
        VaultDocumentDto other = JsonSerializer.Deserialize<VaultDocumentDto>(otherBlob, Json)
                                 ?? throw new FormatException("empty vault blob");
        if (other.Format != Format)
            throw new NotSupportedException($"unsupported vault format: {other.Format}");
        if (!string.IsNullOrEmpty(other.VaultId) && !string.IsNullOrEmpty(_vaultId)
            && !string.Equals(other.VaultId, _vaultId, StringComparison.Ordinal))
            throw new VaultIntegrityException("refusing to merge a different vault");

        var byId = new Dictionary<string, RecordDto>(StringComparer.Ordinal);
        foreach (RecordDto r in _records) byId[r.Id] = r;

        int changed = 0;
        foreach (RecordDto r in other.Records)
        {
            if (!byId.TryGetValue(r.Id, out RecordDto? mine))
            {
                byId[r.Id] = r; changed++;                                   // new record from the other side
            }
            else if (string.CompareOrdinal(r.UpdatedAt, mine.UpdatedAt) > 0)
            {
                byId[r.Id] = r; changed++;                                   // the other side's copy is newer
            }
        }

        if (changed > 0)
        {
            _records.Clear();
            _records.AddRange(byId.Values);
        }
        return changed;
    }

    // ---- master password ----

    /// <summary>
    /// Verify the current password, then rotate the salt and re-wrap the SAME DEK
    /// under the new password. Records are untouched (the point of the KEK/DEK split).
    /// </summary>
    public void ChangeMasterPassword(string oldPassword, string newPassword)
    {
        _ = Unwrap(Crypto.DeriveKey(oldPassword, _salt, _cfg), _wrapped); // throws if wrong
        _salt = Crypto.RandomBytes(Crypto.SaltLen);
        _wrapped = Wrap(Crypto.DeriveKey(newPassword, _salt, _cfg), _dek);
    }

    // ---- key wrapping ----

    private static BlobDto Wrap(byte[] kek, byte[] dek)
    {
        byte[] nonce = Crypto.RandomBytes(Crypto.NonceLen);
        byte[] ct = Crypto.Seal(kek, nonce, dek, Crypto.AadVaultKey);
        return new BlobDto { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(ct) };
    }

    private static byte[] Unwrap(byte[] kek, BlobDto wrapped)
    {
        try
        {
            return Crypto.Open(kek,
                Convert.FromBase64String(wrapped.Nonce),
                Convert.FromBase64String(wrapped.Ciphertext),
                Crypto.AadVaultKey);
        }
        catch (CryptographicException)
        {
            throw new WrongMasterPasswordException();
        }
    }

    // ---- record encryption ----

    private RecordDto EncryptRecord(string id, VaultItem item)
    {
        byte[] nonce = Crypto.RandomBytes(Crypto.NonceLen);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        byte[] ct = Crypto.Seal(_dek, nonce, plaintext, Crypto.RecordAad(id));
        return new RecordDto
        {
            Id = id,
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ct),
            UpdatedAt = NowIso(),
            Deleted = false,
        };
    }

    private VaultItem DecryptRecord(RecordDto rec)
    {
        try
        {
            byte[] pt = Crypto.Open(_dek,
                Convert.FromBase64String(rec.Nonce),
                Convert.FromBase64String(rec.Ciphertext),
                Crypto.RecordAad(rec.Id));
            return JsonSerializer.Deserialize<VaultItem>(pt, Json)
                   ?? throw new VaultIntegrityException("record decoded to null");
        }
        catch (CryptographicException)
        {
            throw new VaultIntegrityException($"record {rec.Id} failed authentication");
        }
    }

    // ---- test/inspection helpers (no secrets exposed beyond ciphertext) ----

    /// <summary>Base64 ciphertext of a record, for tests that assert records are not re-encrypted.</summary>
    public string? RawCiphertextOf(string id) => _records.Find(r => r.Id == id)?.Ciphertext;

    private static string NowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
