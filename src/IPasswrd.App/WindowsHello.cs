using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace IPasswrd.App;

/// <summary>
/// Windows Hello (Microsoft Passport) helper for TPM-bound quick unlock.
///
/// The quick-unlock secret is derived from a signature produced by a Passport key
/// credential that lives in the machine's TPM and is released ONLY after the user
/// completes a Hello gesture (face / fingerprint / PIN). A Passport signature over a
/// fixed challenge is deterministic, so the same challenge always yields the same
/// bytes — from which we derive a stable AES wrapping key for the cached session key.
///
/// Why this matters: no process can reproduce that secret without the user physically
/// passing Hello, and the private key never leaves the TPM. That closes the hole where
/// any process running as the same Windows user could read the cached key off disk.
///
/// Everything degrades gracefully: if Hello is not enrolled/supported (older machine,
/// no TPM, portable non-packaged build where the API is unavailable), the caller falls
/// back to the software path and, ultimately, to the master password — which always works.
///
/// Note on first-time setup: the very first arming enrolls the Passport credential
/// (one Hello gesture) and then signs the challenge (a second gesture). Both are normal
/// system prompts; subsequent unlocks show a single gesture.
/// </summary>
internal static class WindowsHello
{
    // v2 so a future scheme change can rotate cleanly without colliding with an old key.
    private const string CredentialName = "IPasswrd.QuickUnlock.v2";

    /// <summary>Is Windows Hello usable here (supported by the OS and enrolled by the user)?</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>
    /// Sign <paramref name="challenge"/> with the Hello-gated credential, creating it on
    /// first use. Shows the Hello prompt. Returns the raw signature bytes (the secret we
    /// derive the wrap key from), or null on cancel / failure / unavailable.
    /// </summary>
    public static async Task<byte[]?> SignAsync(byte[] challenge)
    {
        try
        {
            KeyCredential? cred = await OpenOrCreateAsync();
            if (cred is null) return null;

            IBuffer buf = CryptographicBuffer.CreateFromByteArray(challenge);
            KeyCredentialOperationResult op = await cred.RequestSignAsync(buf);
            if (op.Status != KeyCredentialStatus.Success || op.Result is null) return null;

            CryptographicBuffer.CopyToByteArray(op.Result, out byte[] sig);
            return sig is { Length: > 0 } ? sig : null;
        }
        catch { return null; }
    }

    private static async Task<KeyCredential?> OpenOrCreateAsync()
    {
        try
        {
            KeyCredentialRetrievalResult open = await KeyCredentialManager.OpenAsync(CredentialName);
            if (open.Status == KeyCredentialStatus.Success) return open.Credential;

            // First use on this machine: enroll a fresh Passport credential (prompts Hello).
            KeyCredentialRetrievalResult made =
                await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.ReplaceExisting);
            return made.Status == KeyCredentialStatus.Success ? made.Credential : null;
        }
        catch { return null; }
    }

    /// <summary>Forget the Passport credential entirely (e.g. if quick unlock is turned off for good).</summary>
    public static async Task DeleteAsync()
    {
        try { await KeyCredentialManager.DeleteAsync(CredentialName); }
        catch { /* nothing to delete / not supported */ }
    }
}
