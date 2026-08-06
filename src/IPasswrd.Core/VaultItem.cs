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
    /// Passwords this record used to have, newest first. Maintained by <see cref="Vault.Update"/> —
    /// callers build items from a form and leave this alone.
    /// </summary>
    [JsonPropertyName("history")] public List<PasswordChange> History { get; set; } = new();
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
