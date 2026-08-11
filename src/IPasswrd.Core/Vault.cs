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
    /// <summary>Baseline layout. Every build ever shipped can read it.</summary>
    private const int FormatBase = 1;

    /// <summary>Adds the recovery envelope. Older builds refuse it loudly instead of
    /// silently dropping the field on their next save — losing a recovery code without
    /// anyone noticing is exactly the failure this whole feature exists to prevent.</summary>
    private const int FormatRecovery = 2;

    /// <summary>Records may carry attachments. Same reasoning: a build that cannot show a
    /// passport scan must not be the one that quietly deletes it.</summary>
    private const int FormatAttachments = 3;

    private KdfConfig _cfg;
    private byte[] _salt;
    private readonly byte[] _dek;
    private BlobDto _wrapped;
    private readonly List<RecordDto> _records;
    private string _vaultId;
    private RecoveryDto? _recovery;
    private string _recoveryRevokedAt;
    private string _masterChangedAt;
    private bool? _hasAttachments;   // null = not worked out yet

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    private Vault(KdfConfig cfg, byte[] salt, byte[] dek, BlobDto wrapped, List<RecordDto> records,
                  string vaultId, RecoveryDto? recovery = null, string recoveryRevokedAt = "",
                  string masterChangedAt = "")
    {
        _cfg = cfg;
        _salt = salt;
        _dek = (byte[])dek.Clone();   // own copy: shielding scrambles the buffer in place
        _dekShielded = MemProt.Shield(_dek);
        _dekAlive = true;
        _wrapped = wrapped;
        _records = records;
        _vaultId = vaultId;
        _recovery = recovery;
        _recoveryRevokedAt = recoveryRevokedAt;
        _masterChangedAt = masterChangedAt;
    }

    /// <summary>Deserialise and reject layouts this build does not understand.</summary>
    private static VaultDocumentDto Parse(byte[] blob)
    {
        VaultDocumentDto doc = JsonSerializer.Deserialize<VaultDocumentDto>(blob, Json)
                               ?? throw new FormatException("empty vault blob");
        if (doc.Format is < FormatBase or > FormatAttachments)
            throw new NotSupportedException($"unsupported vault format: {doc.Format}");
        return doc;
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
        byte[] kek = Crypto.DeriveKey(masterPassword, salt, cfg);
        BlobDto wrapped = Wrap(kek, dek, Crypto.AadVaultKey);
        CryptographicOperations.ZeroMemory(kek);
        var v = new Vault(cfg, salt, dek, wrapped, new List<RecordDto>(), Guid.NewGuid().ToString(),
                          masterChangedAt: NowIsoFine());   // creation is the first "change"
        CryptographicOperations.ZeroMemory(dek);   // the vault holds its own (shielded) copy now
        return v;
    }

    /// <summary>Unlock a serialised vault. Throws <see cref="WrongMasterPasswordException"/> on a bad password.</summary>
    public static Vault Unlock(byte[] blob, string masterPassword)
    {
        VaultDocumentDto doc = Parse(blob);

        var cfg = new KdfConfig(doc.Kdf.MemoryKiB, doc.Kdf.Iterations, doc.Kdf.Parallelism).Capped();
        byte[] salt = Convert.FromBase64String(doc.Kdf.Salt);
        byte[] kek = Crypto.DeriveKey(masterPassword, salt, cfg);
        byte[] dek = Unwrap(kek, doc.WrappedKey);   // throws WrongMasterPasswordException
        string vaultId = string.IsNullOrEmpty(doc.VaultId) ? Guid.NewGuid().ToString() : doc.VaultId;
        var v = new Vault(cfg, salt, dek, doc.WrappedKey, doc.Records, vaultId, doc.Recovery, doc.RecoveryRevokedAt,
                          doc.MasterChangedAt);
        CryptographicOperations.ZeroMemory(dek);   // the vault holds its own (shielded) copy now
        CryptographicOperations.ZeroMemory(kek);
        return v;
    }

    /// <summary>Copy of the session key (DEK) for OS-protected quick unlock. Handle with care.</summary>
    public byte[] ExportSessionKey() => WithDek(static d => (byte[])d.Clone());

    /// <summary>Best-effort scrub of the session key (DEK) from memory. Called on lock, before the
    /// vault object is dropped. The DEK is the key to every record, so wiping it the instant the vault
    /// locks shrinks what a memory scraper can recover from a locked session toward nothing.</summary>
    public void Wipe()
    {
        lock (_dekLock)
        {
            CryptographicOperations.ZeroMemory(_dek);
            _dekShielded = false;   // nothing left worth unshielding
            _dekAlive = false;      // any later WithDek/ExportSessionKey must FAIL, never hand out the zeroed buffer
        }
    }

    // ---- in-memory key protection ----
    //
    // Between uses the DEK sits in RAM scrambled by CryptProtectMemory (keyed per-process by
    // the kernel) and is raw for only the microseconds a record is actually being sealed or
    // opened. A memory dump of the running process therefore almost never contains the usable
    // key. Fully transparent to callers; on a platform without crypt32, or if the call ever
    // fails, the vault simply runs unshielded exactly as before — function over shielding.

    private readonly object _dekLock = new();
    private bool _dekShielded;
    private bool _dekAlive;   // false after Wipe(): the key is gone, not merely unshielded

    /// <summary>Run <paramref name="use"/> with the raw DEK, unshielding just around the call.
    /// Re-entrant: a nested call finds the key already raw and leaves re-shielding to the outer frame.
    /// <paramref name="use"/> must not stash the array — it is scrambled again on return.
    /// After <see cref="Wipe"/> this THROWS rather than exposing the zeroed buffer, so a stale caller
    /// (e.g. a background relay racing a lock) can never seal data under an all-zero key.</summary>
    private T WithDek<T>(Func<byte[], T> use)
    {
        lock (_dekLock)
        {
            if (!_dekAlive) throw new InvalidOperationException("vault session key has been wiped (vault is locked)");
            bool wasShielded = _dekShielded;
            if (wasShielded)
            {
                if (!MemProt.Unshield(_dek)) throw new CryptographicException("session key unshield failed");
                _dekShielded = false;
            }
            try { return use(_dek); }
            finally { if (wasShielded) _dekShielded = MemProt.Shield(_dek); }
        }
    }

    /// <summary>CryptProtectMemory / CryptUnprotectMemory, same-process scope. In-place on a
    /// 16-byte-multiple buffer (the 32-byte DEK qualifies). Best-effort by design.</summary>
    private static class MemProt
    {
        private const uint SameProcess = 0;   // CRYPTPROTECTMEMORY_SAME_PROCESS

        [System.Runtime.InteropServices.DllImport("crypt32.dll")]
        private static extern bool CryptProtectMemory(byte[] pData, uint cbData, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("crypt32.dll")]
        private static extern bool CryptUnprotectMemory(byte[] pData, uint cbData, uint dwFlags);

        public static bool Shield(byte[] buf)
        {
            if (!OperatingSystem.IsWindows() || buf.Length == 0 || (buf.Length & 15) != 0) return false;
            try { return CryptProtectMemory(buf, (uint)buf.Length, SameProcess); }
            catch { return false; }
        }

        public static bool Unshield(byte[] buf)
        {
            try { return CryptUnprotectMemory(buf, (uint)buf.Length, SameProcess); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Reopen a serialised vault with a cached session key, skipping the KDF (quick unlock).
    /// The key is verified against the first live record; a wrong key throws
    /// <see cref="WrongMasterPasswordException"/>. An empty vault is accepted as-is.
    /// </summary>
    public static Vault UnlockWithSessionKey(byte[] blob, byte[] dek)
    {
        VaultDocumentDto doc = Parse(blob);

        var cfg = new KdfConfig(doc.Kdf.MemoryKiB, doc.Kdf.Iterations, doc.Kdf.Parallelism).Capped();
        byte[] salt = Convert.FromBase64String(doc.Kdf.Salt);
        string vaultId = string.IsNullOrEmpty(doc.VaultId) ? Guid.NewGuid().ToString() : doc.VaultId;
        // NB: the caller keeps ownership of `dek` here (quick-unlock caches it OS-protected);
        // the ctor clones, so shielding never scrambles the caller's buffer.
        var v = new Vault(cfg, salt, dek, doc.WrappedKey, doc.Records, vaultId, doc.Recovery, doc.RecoveryRevokedAt,
                          doc.MasterChangedAt);

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
            // climb only as far as the contents demand: a vault that uses neither feature
            // keeps opening in every build ever shipped
            Format = FormatNeeded(),
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
            Recovery = _recovery,
            RecoveryRevokedAt = _recoveryRevokedAt,
            MasterChangedAt = _masterChangedAt,
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

    /// <summary>
    /// Replace a record. If the password actually changed, the old one is kept in the item's
    /// history first — losing the previous password on a change is how people get locked out
    /// of sites that did not really accept the new one.
    /// </summary>
    public void Update(string id, VaultItem item)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (_records[i].Id == id && !_records[i].Deleted)
            {
                _records[i] = EncryptRecord(id, CarryHistory(DecryptRecord(_records[i]), item));
                return;
            }
        }
        throw new KeyNotFoundException(id);
    }

    /// <summary>Field name the history logic watches. Matches the convention used by Import and the browser bridge.</summary>
    public const string PasswordField = "password";

    /// <summary>How many superseded passwords a record keeps. Old enough ones stop being useful and are just more secrets at rest.</summary>
    public const int MaxPasswordHistory = 20;

    /// <summary>Forget every previous password of one record. The current password is untouched.</summary>
    public void ClearPasswordHistory(string id)
    {
        for (int i = 0; i < _records.Count; i++)
        {
            if (_records[i].Id == id && !_records[i].Deleted)
            {
                VaultItem item = DecryptRecord(_records[i]);
                if (item.History.Count == 0) return;
                item.History.Clear();
                _records[i] = EncryptRecord(id, item);
                return;
            }
        }
        throw new KeyNotFoundException(id);
    }

    private static VaultItem CarryHistory(VaultItem previous, VaultItem next)
    {
        // The caller rebuilds the item from a form and has no reason to know about history,
        // so it arrives empty. Carry the old list forward instead of letting an ordinary edit
        // erase it — clearing is a deliberate act, see ClearPasswordHistory.
        if (next.History.Count == 0 && previous.History.Count > 0)
            next.History = new List<PasswordChange>(previous.History);

        previous.Fields.TryGetValue(PasswordField, out string? was);
        next.Fields.TryGetValue(PasswordField, out string? now);

        // Only a genuine replacement counts. Filling in a password for the first time, or
        // clearing the field, must not push a blank entry into the list.
        if (!string.IsNullOrEmpty(was) && !string.IsNullOrEmpty(now)
            && !string.Equals(was, now, StringComparison.Ordinal))
        {
            next.History.Insert(0, new PasswordChange { Password = was, ReplacedAt = NowIso() });
            if (next.History.Count > MaxPasswordHistory)
                next.History.RemoveRange(MaxPasswordHistory, next.History.Count - MaxPasswordHistory);
        }
        return next;
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
                _hasAttachments = null;   // the last scan may have just gone with it
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

    /// <summary>
    /// Те же записи, что и <see cref="Items"/>, но по одной и без накопления списка.
    ///
    /// Расширение автозаполнения iOS живёт в жёстком лимите памяти, а ему нужны только
    /// логин с паролем. С появлением вложений «расшифровать всё сразу» стало означать
    /// «держать в памяти все сканы паспорта разом» — и быть убитым посреди подстановки
    /// пароля. Здесь жива ровно одна расшифрованная запись.
    /// </summary>
    public IEnumerable<VaultEntry> Stream()
    {
        foreach (RecordDto rec in _records)
            if (!rec.Deleted)
                yield return new VaultEntry(rec.Id, DecryptRecord(rec), rec.UpdatedAt);
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
    /// (same DEK lineage), so no password is needed. The master-password envelope follows its
    /// own last-write-wins rule (newest <c>masterChangedAt</c> wins): a password changed on one
    /// device replaces the wrapping — never the DEK — everywhere else, so after this call the
    /// vault may require the OTHER side's master password at the next unlock. Files without a
    /// stamp keep the local envelope, exactly as every build behaved before the stamp existed.
    /// Refuses to merge a different vault (mismatched <see cref="VaultId"/>). Returns the number
    /// of records that were added or replaced from <paramref name="otherBlob"/> (an adopted
    /// envelope is not counted — compare <see cref="MasterPasswordChangedAt"/> to detect it).
    /// </summary>
    public int MergeFrom(byte[] otherBlob)
    {
        VaultDocumentDto other = Parse(otherBlob);
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
            _hasAttachments = null;
        }

        // The recovery envelope is not a record, so it needs its own last-write-wins rule.
        // Without one, a device that has never seen the code would quietly drop it on its
        // next save and the user would find out only when they needed it.
        if (string.CompareOrdinal(RecoveryStamp(other.Recovery, other.RecoveryRevokedAt),
                                  RecoveryStamp(_recovery, _recoveryRevokedAt)) > 0)
        {
            _recovery = other.Recovery;
            _recoveryRevokedAt = other.RecoveryRevokedAt;
        }

        // The master-password envelope converges the same way. Historically every device kept
        // its own envelope forever, so a password changed on the PC lived only there while the
        // phone silently stayed on the old one — records in sync, passwords diverged, no error
        // anywhere. Newest change wins now. Adopting swaps only the wrapping around the SAME
        // DEK: the open session, every record and the recovery envelope are untouched. A
        // mangled envelope (damaged base64/salt) is skipped — records still merge, and a
        // broken wrapping must never replace a working one.
        if (string.CompareOrdinal(other.MasterChangedAt, _masterChangedAt) > 0)
        {
            try
            {
                byte[] salt = Convert.FromBase64String(other.Kdf.Salt);
                byte[] ct = Convert.FromBase64String(other.WrappedKey.Ciphertext);
                byte[] nonce = Convert.FromBase64String(other.WrappedKey.Nonce);
                if (salt.Length == Crypto.SaltLen && nonce.Length == Crypto.NonceLen && ct.Length >= Crypto.TagLen)
                {
                    _cfg = new KdfConfig(other.Kdf.MemoryKiB, other.Kdf.Iterations, other.Kdf.Parallelism).Capped();
                    _salt = salt;
                    _wrapped = other.WrappedKey;
                    _masterChangedAt = other.MasterChangedAt;
                }
            }
            catch (FormatException) { /* damaged envelope on the other side — keep ours */ }
        }

        return changed;
    }

    /// <summary>
    /// Clear-text ISO stamp of the last master-password change ("" for files that predate the
    /// stamp). Callers snapshot it around <see cref="MergeFrom"/> to notice that the envelope
    /// was adopted from another device and the next unlock will want that device's password.
    /// </summary>
    public string MasterPasswordChangedAt => _masterChangedAt;

    /// <summary>
    /// The same stamp straight from a serialised blob, no password needed — for the unlock
    /// screen to explain a failed attempt ("the master password was changed on …"). The date
    /// is already visible metadata in the file, like the recovery envelope's presence.
    /// </summary>
    public static string MasterPasswordChangedAtOf(byte[] blob)
    {
        try { return Parse(blob).MasterChangedAt; }
        catch { return ""; }
    }

    // ---- master password ----

    /// <summary>
    /// Verify the current password, then rotate the salt and re-wrap the SAME DEK
    /// under the new password. Records are untouched (the point of the KEK/DEK split).
    /// </summary>
    public void ChangeMasterPassword(string oldPassword, string newPassword)
    {
        byte[] oldKek = Crypto.DeriveKey(oldPassword, _salt, _cfg);
        byte[] check = Unwrap(oldKek, _wrapped); // throws if wrong
        CryptographicOperations.ZeroMemory(oldKek);
        CryptographicOperations.ZeroMemory(check);
        _salt = Crypto.RandomBytes(Crypto.SaltLen);
        byte[] kek = Crypto.DeriveKey(newPassword, _salt, _cfg);   // slow KDF outside the key lock
        _wrapped = WithDek(d => Wrap(kek, d, Crypto.AadVaultKey));
        CryptographicOperations.ZeroMemory(kek);
        _masterChangedAt = NowIsoFine();   // this envelope now outranks every other device's
    }

    /// <summary>
    /// Set a new master password WITHOUT proving the old one. Only for the recovery flow.
    /// It grants nothing new — reaching this method already requires an unlocked vault,
    /// i.e. the DEK — but every other path should go through <see cref="ChangeMasterPassword"/>
    /// so that a forgotten password cannot be replaced by someone who merely walked up to
    /// an unlocked screen.
    /// </summary>
    public void ResetMasterPassword(string newPassword)
    {
        _cfg = _cfg.Sanitized();   // recovery reset must never carry over a downgraded KDF from a tampered header
        _salt = Crypto.RandomBytes(Crypto.SaltLen);
        byte[] kek = Crypto.DeriveKey(newPassword, _salt, _cfg);   // slow KDF outside the key lock
        _wrapped = WithDek(d => Wrap(kek, d, Crypto.AadVaultKey));
        CryptographicOperations.ZeroMemory(kek);
        _masterChangedAt = NowIsoFine();   // recovery sets a new password: same rule as a change
    }

    // ---- recovery code ----

    /// <summary>True when a recovery code has been issued and not revoked.</summary>
    public bool HasRecoveryCode => _recovery is not null;

    /// <summary>
    /// Does this vault file carry a recovery envelope? Answerable without the password —
    /// the unlock screen needs it to decide whether to offer "forgot master password",
    /// and the envelope's presence is already visible in the file either way.
    /// </summary>
    public static bool IsRecoveryAvailable(byte[] blob)
    {
        try { return Parse(blob).Recovery is not null; }
        catch { return false; }
    }

    /// <summary>When the current recovery code was issued (ISO-8601 UTC), or null if there is none.</summary>
    public string? RecoveryCodeIssuedAt => _recovery?.CreatedAt;

    /// <summary>
    /// Issue a recovery code: a second, independent envelope around the SAME DEK, locked by
    /// 125 random bits instead of the master password. Any previous code stops working.
    ///
    /// The returned string is the only copy that will ever exist — nothing derived from it is
    /// kept in the clear, so if the user does not write it down it is gone. Note the trade-off
    /// worth telling them about: from here on, the vault file has a second door, and whoever
    /// holds this code plus the file can open it.
    /// </summary>
    public string EnableRecovery()
    {
        string display = RecoveryCode.Generate();
        string canonical = RecoveryCode.Normalize(display)!;   // freshly generated: always valid

        byte[] salt = Crypto.RandomBytes(Crypto.SaltLen);
        byte[] rek = Crypto.DeriveKey(canonical, salt, _cfg);

        _recovery = new RecoveryDto
        {
            Kdf = KdfDtoOf(_cfg, salt),
            WrappedKey = WithDek(d => Wrap(rek, d, Crypto.AadRecoveryKey)),
            CreatedAt = NowIso(),
        };
        CryptographicOperations.ZeroMemory(rek);
        _recoveryRevokedAt = "";
        return display;
    }

    /// <summary>
    /// Revoke the recovery code — the written-down copy stops opening this vault. The
    /// revocation is stamped so that a device still carrying the old envelope does not
    /// resurrect it on the next sync merge.
    /// </summary>
    public void DisableRecovery()
    {
        if (_recovery is null) return;
        _recovery = null;
        _recoveryRevokedAt = NowIso();
    }

    /// <summary>
    /// Open a vault with its recovery code, for when the master password is lost.
    ///
    /// The vault comes back unlocked but still wrapped by the forgotten password: the caller
    /// MUST follow with <see cref="ResetMasterPassword"/> before serialising, otherwise the
    /// saved file still needs a password nobody knows.
    /// </summary>
    /// <exception cref="RecoveryNotEnabledException">No code was ever issued for this vault.</exception>
    /// <exception cref="WrongRecoveryCodeException">The code is malformed or simply wrong.</exception>
    public static Vault UnlockWithRecoveryCode(byte[] blob, string recoveryCode)
    {
        VaultDocumentDto doc = Parse(blob);
        RecoveryDto rec = doc.Recovery ?? throw new RecoveryNotEnabledException();

        string canonical = RecoveryCode.Normalize(recoveryCode) ?? throw new WrongRecoveryCodeException();

        var rcfg = new KdfConfig(rec.Kdf.MemoryKiB, rec.Kdf.Iterations, rec.Kdf.Parallelism).Capped();
        byte[] rek = Crypto.DeriveKey(canonical, Convert.FromBase64String(rec.Kdf.Salt), rcfg);
        byte[] dek = TryUnwrap(rek, rec.WrappedKey, Crypto.AadRecoveryKey)
                     ?? throw new WrongRecoveryCodeException();

        var cfg = new KdfConfig(doc.Kdf.MemoryKiB, doc.Kdf.Iterations, doc.Kdf.Parallelism).Capped();
        byte[] salt = Convert.FromBase64String(doc.Kdf.Salt);
        string vaultId = string.IsNullOrEmpty(doc.VaultId) ? Guid.NewGuid().ToString() : doc.VaultId;
        var v = new Vault(cfg, salt, dek, doc.WrappedKey, doc.Records, vaultId, doc.Recovery, doc.RecoveryRevokedAt,
                          doc.MasterChangedAt);
        CryptographicOperations.ZeroMemory(dek);   // the vault holds its own (shielded) copy now
        CryptographicOperations.ZeroMemory(rek);
        return v;
    }

    // ---- key wrapping ----

    private static BlobDto Wrap(byte[] kek, byte[] dek, byte[] aad)
    {
        byte[] nonce = Crypto.RandomBytes(Crypto.NonceLen);
        byte[] ct = Crypto.Seal(kek, nonce, dek, aad);
        return new BlobDto { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(ct) };
    }

    /// <summary>Unwrap, or null on a wrong key / mangled blob. Callers name the failure.</summary>
    private static byte[]? TryUnwrap(byte[] kek, BlobDto wrapped, byte[] aad)
    {
        try
        {
            return Crypto.Open(kek,
                Convert.FromBase64String(wrapped.Nonce),
                Convert.FromBase64String(wrapped.Ciphertext),
                aad);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }   // base64 damaged by a bad sync/edit
    }

    private static byte[] Unwrap(byte[] kek, BlobDto wrapped) =>
        TryUnwrap(kek, wrapped, Crypto.AadVaultKey) ?? throw new WrongMasterPasswordException();

    /// <summary>Lowest layout version that can represent what this vault currently holds.</summary>
    private int FormatNeeded()
    {
        if (HasAnyAttachment()) return FormatAttachments;
        if (_recovery is not null || _recoveryRevokedAt.Length > 0) return FormatRecovery;
        return FormatBase;
    }

    /// <summary>
    /// Does any live record carry a file? Answering means decrypting every record, so the
    /// result is cached and invalidated whenever a record is written.
    /// </summary>
    private bool HasAnyAttachment()
    {
        if (_hasAttachments is bool known) return known;
        bool found = false;
        foreach (RecordDto rec in _records)
        {
            if (rec.Deleted) continue;
            try { if (DecryptRecord(rec).Attachments.Count > 0) { found = true; break; } }
            catch (VaultIntegrityException) { /* a damaged record cannot be asked; ignore */ }
        }
        _hasAttachments = found;
        return found;
    }

    private static string RecoveryStamp(RecoveryDto? rec, string revokedAt) =>
        rec is not null ? rec.CreatedAt : revokedAt;

    private static KdfDto KdfDtoOf(KdfConfig cfg, byte[] salt) => new()
    {
        Algorithm = "argon2id",
        MemoryKiB = cfg.MemoryKiB,
        Iterations = cfg.Iterations,
        Parallelism = cfg.Parallelism,
        Salt = Convert.ToBase64String(salt),
    };

    // ---- record encryption ----

    /// <summary>
    /// Ceiling for one stored file. The whole vault travels as a single blob on every save, so an
    /// unbounded attachment would turn each keystroke into a multi-megabyte upload. Pictures are
    /// meant to be downscaled by the caller before they get here.
    /// </summary>
    public const int MaxAttachmentBytes = 2 * 1024 * 1024;

    /// <summary>Ceiling per record, for the same reason.</summary>
    public const int MaxAttachmentsPerItem = 10;

    private static void GuardAttachments(VaultItem item)
    {
        if (item.Attachments.Count == 0) return;
        if (item.Attachments.Count > MaxAttachmentsPerItem)
            throw new AttachmentTooLargeException($"Не больше {MaxAttachmentsPerItem} вложений в одной записи.");

        foreach (Attachment a in item.Attachments)
        {
            // Trust the payload, not the declared size: Bytes is a display convenience and a
            // hand-edited vault could disagree with it.
            int actual;
            try { actual = Convert.FromBase64String(a.Data).Length; }
            catch (FormatException) { throw new AttachmentTooLargeException($"Вложение «{a.Name}» повреждено."); }

            if (actual > MaxAttachmentBytes)
                throw new AttachmentTooLargeException(
                    $"Вложение «{a.Name}» — {actual / 1024} КБ, предел {MaxAttachmentBytes / 1024} КБ.");
        }
    }

    private RecordDto EncryptRecord(string id, VaultItem item)
    {
        GuardAttachments(item);
        _hasAttachments = null;   // contents changed: the format decision has to be made again
        item.Type = ItemTypes.Normalize(item.Type);   // одно написание типа на все устройства
        ItemFolders.Normalize(item);                  // список папок — истина, старый ключ — зеркало первой

        byte[] nonce = Crypto.RandomBytes(Crypto.NonceLen);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        byte[] ct = WithDek(d => Crypto.Seal(d, nonce, plaintext, Crypto.RecordAad(id)));
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
            byte[] pt = WithDek(d => Crypto.Open(d,
                Convert.FromBase64String(rec.Nonce),
                Convert.FromBase64String(rec.Ciphertext),
                Crypto.RecordAad(rec.Id)));
            VaultItem item = JsonSerializer.Deserialize<VaultItem>(pt, Json)
                             ?? throw new VaultIntegrityException("record decoded to null");
            // Запись могла приехать с устройства, которое называло тип по-своему — см. ItemTypes.
            item.Type = ItemTypes.Normalize(item.Type);
            ItemFolders.Normalize(item);   // и со старым одиночным ключом папки — см. ItemFolders
            return item;
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

    /// <summary>
    /// Envelope stamp with sub-second precision: two devices changing the master password in
    /// the same second must still order deterministically. One fixed length and layout, so the
    /// ordinal string compare in <see cref="MergeFrom"/> is a correct time compare.
    /// </summary>
    private static string NowIsoFine() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
