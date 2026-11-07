using System.Security.Cryptography;

namespace IPasswrd.Core;

/// <summary>
/// Picks which sealed vault blob to unlock when a remote copy is also available —
/// without destroying local records first.
///
/// The old mobile path overwrote the local file with the cloud blob before unlock
/// ("pull-first"). That made a password change on another device unlock cleanly, but
/// it also wiped any offline edits that had not yet synced. Windows already does the
/// safer thing: unlock what is on disk, then <see cref="Vault.MergeFrom"/> the peer.
/// This helper is the shared decision for that model.
///
/// When the remote master-password envelope is newer we unlock the remote blob first
/// (so the new password works immediately) and merge the local blob afterwards —
/// same DEK, so offline record edits still win by <c>updatedAt</c>.
/// </summary>
public static class VaultUnlockSource
{
    /// <summary>Outcome of comparing a local vault file with a pulled remote copy.</summary>
    /// <param name="UnlockBlob">Bytes to pass to <see cref="Vault.Unlock"/> / recovery unlock.</param>
    /// <param name="OtherBlobToMergeAfterUnlock">
    /// Peer to <see cref="Vault.MergeFrom"/> after a successful unlock, or null when there is
    /// nothing to merge (identical content, missing peer, or foreign lineage).
    /// </param>
    /// <param name="RemoteIsForeign">
    /// True when both sides declare a non-empty <c>vaultId</c> and they differ — do not merge;
    /// leave the local file alone.
    /// </param>
    public sealed record Decision(
        byte[] UnlockBlob,
        byte[]? OtherBlobToMergeAfterUnlock,
        bool RemoteIsForeign);

    /// <summary>
    /// Decide which blob to unlock. Never implies "overwrite local with remote before unlock".
    /// Throws when neither side has bytes.
    /// </summary>
    public static Decision Decide(byte[]? local, byte[]? remote)
    {
        bool hasLocal = local is { Length: > 0 };
        bool hasRemote = remote is { Length: > 0 };
        if (!hasLocal && !hasRemote)
            throw new ArgumentException("no vault blob on either side", nameof(local));

        if (!hasLocal) return new Decision(remote!, OtherBlobToMergeAfterUnlock: null, RemoteIsForeign: false);
        if (!hasRemote) return new Decision(local!, OtherBlobToMergeAfterUnlock: null, RemoteIsForeign: false);

        // Byte-identical — unlock local, nothing to merge.
        byte[] localHash = SHA256.HashData(local!);
        byte[] remoteHash = SHA256.HashData(remote!);
        bool same = CryptographicOperations.FixedTimeEquals(localHash, remoteHash);
        CryptographicOperations.ZeroMemory(localHash);
        CryptographicOperations.ZeroMemory(remoteHash);
        if (same) return new Decision(local!, OtherBlobToMergeAfterUnlock: null, RemoteIsForeign: false);

        string localId = Vault.VaultIdOf(local!);
        string remoteId = Vault.VaultIdOf(remote!);
        if (localId.Length > 0 && remoteId.Length > 0
            && !string.Equals(localId, remoteId, StringComparison.Ordinal))
        {
            // Different lineage sitting in the cloud — keep local, refuse to merge.
            return new Decision(local!, OtherBlobToMergeAfterUnlock: null, RemoteIsForeign: true);
        }

        // Same lineage (or a legacy file missing vaultId). Prefer the newer master-password
        // envelope for the unlock attempt so a password changed on another device opens on
        // the first try; the other side is merged afterwards and keeps offline record edits.
        string localStamp = Vault.MasterPasswordChangedAtOf(local!);
        string remoteStamp = Vault.MasterPasswordChangedAtOf(remote!);
        if (string.CompareOrdinal(remoteStamp, localStamp) > 0)
            return new Decision(remote!, OtherBlobToMergeAfterUnlock: local!, RemoteIsForeign: false);

        return new Decision(local!, OtherBlobToMergeAfterUnlock: remote!, RemoteIsForeign: false);
    }
}
