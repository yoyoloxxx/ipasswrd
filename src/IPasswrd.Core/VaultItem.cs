using System.Text.Json.Serialization;

namespace IPasswrd.Core;

/// <summary>
/// The decrypted content of one vault record. This is what the app works with;
/// on disk / in transit it only ever exists as AES-256-GCM ciphertext.
/// </summary>
public sealed class VaultItem
{
    /// <summary>account | card | document | note | passkey</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "account";

    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>Free-form typed fields, e.g. username/password/url/totp, or card number/expiry/cvc.</summary>
    [JsonPropertyName("fields")] public Dictionary<string, string> Fields { get; set; } = new();

    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    [JsonPropertyName("favorite")] public bool Favorite { get; set; }

    /// <summary>
    /// Optional grouping, empty means ungrouped. Flat on purpose: a tree looks tidier in a
    /// screenshot and turns into busywork the moment you have to decide where something lives.
    /// </summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";

    /// <summary>
    /// Passwords this record used to have, newest first. Maintained by <see cref="Vault.Update"/> —
    /// callers build items from a form and leave this alone.
    /// </summary>
    [JsonPropertyName("history")] public List<PasswordChange> History { get; set; } = new();

    /// <summary>Scans and files kept with the record — passport photos, contracts, licence pictures.</summary>
    [JsonPropertyName("attachments")] public List<Attachment> Attachments { get; set; } = new();

    /// <summary>
    /// Anything a newer build wrote that this one does not know about. Without it, opening a
    /// record in an older app and saving it would silently drop whatever the newer app added —
    /// the same quiet data loss the vault format guards against at the file level.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// A file stored inside the record. It rides in the same AES-GCM ciphertext as the rest of the
/// item, so it needs no separate key, no separate file and no separate sync path — which is
/// exactly why the payload is capped: the whole vault travels as one blob on every save.
/// </summary>
public sealed class Attachment
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>e.g. image/jpeg, application/pdf.</summary>
    [JsonPropertyName("mime")] public string Mime { get; set; } = "";

    /// <summary>Base64 of the file itself.</summary>
    [JsonPropertyName("data")] public string Data { get; set; } = "";

    /// <summary>ISO-8601 UTC.</summary>
    [JsonPropertyName("addedAt")] public string AddedAt { get; set; } = "";

    /// <summary>Size of the decoded file, for showing "1,2 МБ" without decoding it first.</summary>
    [JsonPropertyName("bytes")] public int Bytes { get; set; }
}

/// <summary>One superseded password and the moment it stopped being the current one.</summary>
public sealed class PasswordChange
{
    [JsonPropertyName("password")] public string Password { get; set; } = "";

    /// <summary>ISO-8601 UTC.</summary>
    [JsonPropertyName("replacedAt")] public string ReplacedAt { get; set; } = "";
}

/// <summary>A decrypted record together with its stable id and last-modified stamp.</summary>
/// <remarks>UpdatedAt (ISO-8601 UTC) is kept in the clear to enable last-write-wins sync later.</remarks>
public sealed record VaultEntry(string Id, VaultItem Item, string UpdatedAt);
