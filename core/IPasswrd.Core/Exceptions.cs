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

/// <summary>Thrown when the recovery code fails to unwrap the vault key (or is malformed).</summary>
public sealed class WrongRecoveryCodeException : Exception
{
    public WrongRecoveryCodeException(string message = "Recovery code is incorrect.")
        : base(message) { }
}

/// <summary>Thrown when a vault is opened by recovery code but no code was ever issued for it.</summary>
public sealed class RecoveryNotEnabledException : Exception
{
    public RecoveryNotEnabledException(string message = "This vault has no recovery code.")
        : base(message) { }
}

/// <summary>Thrown when an attachment (or a record's worth of them) exceeds what the vault will carry.</summary>
public sealed class AttachmentTooLargeException : Exception
{
    public AttachmentTooLargeException(string message) : base(message) { }
}
