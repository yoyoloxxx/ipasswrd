using System.Text.Json.Serialization;

namespace IPasswrd.Core;

// Serialisation DTOs for the self-contained vault blob. Keys match the reference
// spec (reference/vault_reference.py) so both implementations describe the same format.

internal sealed class VaultDocumentDto
{
    [JsonPropertyName("format")] public int Format { get; set; }
    [JsonPropertyName("vaultId")] public string VaultId { get; set; } = "";  // clear-text lineage id, for safe sync merges
    [JsonPropertyName("kdf")] public KdfDto Kdf { get; set; } = new();
    [JsonPropertyName("wrappedKey")] public BlobDto WrappedKey { get; set; } = new();

    /// <summary>Second envelope around the same DEK, locked by the recovery code. Absent = no code issued.</summary>
    [JsonPropertyName("recovery")] public RecoveryDto? Recovery { get; set; }

    /// <summary>Clear-text ISO stamp of the last revocation, so a merge can tell "revoked" from "never had one".</summary>
    [JsonPropertyName("recoveryRevokedAt")] public string RecoveryRevokedAt { get; set; } = "";

    /// <summary>
    /// Clear-text ISO stamp of the last master-password change (creation counts as the first one).
    /// Sync uses it to converge the password envelope by last-write-wins — see Vault.MergeFrom.
    /// "" on files written by builds that predate the field; those merge exactly as before.
    /// </summary>
    [JsonPropertyName("masterChangedAt")] public string MasterChangedAt { get; set; } = "";

    /// <summary>
    /// Base64 HMAC-SHA256 over the vault's authenticated metadata — the key envelope plus every
    /// record's id / updatedAt / deleted / nonce / ciphertext — keyed by a subkey derived from the
    /// DEK. It stops a storage-level attacker (a cloud or account that can rewrite the synced file
    /// but does not know the master password) from rolling a record back to an old password, forging
    /// a deletion, or swapping the key envelope to lock the owner out. null on files written by
    /// builds that predate the field; those are treated as unauthenticated. See Vault.MergeFrom.
    /// </summary>
    [JsonPropertyName("mac")] public string? Mac { get; set; }

    [JsonPropertyName("records")] public List<RecordDto> Records { get; set; } = new();
}

/// <summary>
/// The recovery envelope. Its own salt and its own AAD, so it is cryptographically
/// independent of the master-password envelope even though both wrap the same DEK.
/// </summary>
internal sealed class RecoveryDto
{
    [JsonPropertyName("kdf")] public KdfDto Kdf { get; set; } = new();
    [JsonPropertyName("wrappedKey")] public BlobDto WrappedKey { get; set; } = new();
    /// <summary>Clear-text ISO stamp: shown in Settings, and used to converge on sync.</summary>
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
}

internal sealed class KdfDto
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "argon2id";
    [JsonPropertyName("memoryKiB")] public int MemoryKiB { get; set; }
    [JsonPropertyName("iterations")] public int Iterations { get; set; }
    [JsonPropertyName("parallelism")] public int Parallelism { get; set; }
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";   // base64
}

internal sealed class BlobDto
{
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";        // base64, 12 bytes
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = ""; // base64, cipher||tag
}

internal sealed class RecordDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
}
