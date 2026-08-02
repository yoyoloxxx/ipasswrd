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
    [JsonPropertyName("records")] public List<RecordDto> Records { get; set; } = new();
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
