namespace IPasswrd.Core;

/// <summary>Thrown when the master password fails to unwrap the vault key.</summary>
public sealed class WrongMasterPasswordException : Exception
{
    public WrongMasterPasswordException(string message = "Master password is incorrect.")
        : base(message) { }
}

/// <summary>Thrown when a record fails authentication (tampering or corruption).</summary>
public sealed class VaultIntegrityException : Exception
{
    public VaultIntegrityException(string message = "Vault data failed integrity verification.")
        : base(message) { }
}
