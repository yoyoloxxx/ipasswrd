# IPasswrd

Free, cross-platform password manager. A full alternative to Kaspersky Password Manager, built on an
own end-to-end-encrypted vault so the same data works on Windows, iPhone and Android.

**Status: encrypted core + a full-featured CLI**, all unit-tested (42 tests) and verified end-to-end on
Windows: an envelope-encrypted vault, import (Chrome / Edge / Yandex / Kaspersky), TOTP 2FA codes
(RFC 6238), a local weak/reused password audit, and a CSPRNG generator. No GUI yet; the Avalonia app and
cross-device sync are next. The clickable UI prototype lives in [`prototype/`](prototype/).

## Architecture decisions

- **Own encrypted vault is the source of truth.** We hold the data ourselves, encrypted with a key only
  the user's master password can produce. Because the vault is our own format, the underlying OS does not
  matter, which is exactly what makes one codebase work on Windows, iPhone and Android (the model Bitwarden
  and Proton Pass use). Apple Passwords and Google Credential Manager are used for *import* and, on their
  own platforms, as a *mirror* so the OS-native autofill keeps working.
- **Stack: C# + Avalonia.** Avalonia targets desktop and mobile (iOS/Android) from one C#/XAML codebase,
  so the mobile goal does not force a rewrite. The core here is plain .NET with no UI dependency.
- **Order: Windows first, mobile later** — on the same core.
- **Sync is transport-agnostic.** The vault serialises to one self-contained JSON blob, so *how* it syncs
  (encrypted file in the user's iCloud/Google Drive, or a managed realtime backend for near-instant sync)
  is a later, swappable decision. The core neither knows nor cares.

## The vault scheme

Envelope encryption with a key-encryption-key / data-encryption-key split:

```
master password ──Argon2id(salt, params)──▶  KEK (key-encryption key)
random 32-byte DEK (data-encryption key)
wrappedKey  = AES-256-GCM(KEK, DEK,  aad = "ipasswrd/vault-key/v1")
each record = AES-256-GCM(DEK, json(item), aad = "ipasswrd/record/v1/" + id)
```

Why this shape:

- **KEK/DEK split** — changing the master password only re-wraps the tiny DEK; records are never
  re-encrypted. Fast, and it means the master password never directly touches record data.
- **AAD binds every record to its id** — an attacker who reorders or swaps ciphertexts in the file gets
  authentication failures, not silently mixed-up entries.
- **AES-256-GCM** is authenticated: any tampering (or a wrong master password) is detected, never
  silently accepted.

### Cryptographic choices

| Concern | Choice | Notes |
|---|---|---|
| Key derivation | **Argon2id**, m=64 MiB, t=3, p=1 | Mobile-safe strong baseline, ≈ Bitwarden's default, well above OWASP's 19 MiB minimum. Params are stored in the vault header, so they can be raised later and the vault re-derived. |
| Authenticated encryption | **AES-256-GCM** | Built into .NET on every platform, hardware-accelerated. Random 96-bit nonce per encryption. |
| Argon2id library | `Konscious.Security.Cryptography.Argon2` | Pure-managed, so it works on iOS/Android with no native dependency. |
| Randomness | `RandomNumberGenerator` (CSPRNG) | For salts, nonces and the DEK. |

## Build & test

Requires the .NET 10 SDK (LTS).

```bash
# from the repo root
dotnet test                      # builds everything and runs the security-core test suite

# or per project
dotnet build src/IPasswrd.Core
dotnet test  tests/IPasswrd.Core.Tests
```

> On this dev machine the SDK is installed user-local at `%LOCALAPPDATA%\Microsoft\dotnet` (the machine had only a runtime). If your terminal's `dotnet` says "No .NET SDKs were found", prepend that folder to PATH for the session (`set PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%`) or call that `dotnet.exe` by full path. To run the built `ipasswrd.exe` directly, set `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet` so the app finds the runtime.

## Verification

The security scheme is pinned by a test suite that asserts one property each: round-trip, wrong-password
rejection, tamper detection on records and on the wrapped key, master-password change re-wrapping the key
only, record-to-id binding, per-vault salt, per-record nonce, and delete-tombstoning.

The same suite exists twice:

- `tests/IPasswrd.Core.Tests/VaultTests.cs` — xUnit, against the real C# core (`dotnet test`).
- [`reference/`](reference/) — an executable Python spec of the identical construction (same Argon2id +
  AES-256-GCM, same KEK/DEK split, same AAD). It was run green (9/9) to prove the scheme independently of
  the C# build:

  ```bash
  cd reference && python3 test_vault_reference.py
  ```

Keeping both honest is deliberate: the Python file is the human-readable spec, the C# file is the product.

## Command line (dev)

A thin console front-end over the core, for exercising the vault and (next) importing:

```bash
ipasswrd init                 # create a vault (asks for a master password, twice)
ipasswrd add [card|note|doc]  # add an item (default: account; prompts the right fields)
ipasswrd list                 # list entries
ipasswrd get  <id|title>      # show an entry (--show reveals password / CVC / TOTP secret)
ipasswrd del  <id|title>      # delete an entry
ipasswrd passwd               # change the master password
ipasswrd gen  [length]        # generate a strong password
ipasswrd totp <id|title>      # current 2FA (TOTP) code for an entry
ipasswrd audit                # weak / reused password report (runs locally)
ipasswrd import <file>        # import Chrome / Edge / Yandex / Kaspersky (--format to force)
```

Vault file: `%LOCALAPPDATA%\IPasswrd\vault.ipvault` (override with the `IPASSWRD_VAULT` env var).

## Threat model (what this does and does not protect)

- **Protects:** the vault at rest and in the (untrusted) sync channel. Without the master password the file
  is opaque; tampering is always detected.
- **Does not protect against:** a compromised device (keylogger, malware, someone with the vault already
  unlocked). Like every password manager, IPasswrd trusts the device while unlocked.
- **Known trade-off:** storing a login and its 2FA code (TOTP) in the same vault is a convenience choice
  (as Apple and 1Password do). A future setting may allow keeping 2FA separate.

## Layout

```
IPasswrd/
├─ src/IPasswrd.Core/          # the encrypted-vault engine + services
│  ├─ Vault.cs                 # Create / Unlock / Add / Get / Update / Delete / ChangeMasterPassword / Serialize
│  ├─ Crypto.cs                # Argon2id + AES-256-GCM primitives, KdfConfig
│  ├─ Totp.cs                  # TOTP / HOTP verification codes (RFC 6238 / 4226)
│  ├─ Generator.cs             # CSPRNG password generator
│  ├─ SecurityAudit.cs         # local weak / reused password analysis
│  ├─ Import/                  # Chrome/Edge/Yandex CSV + Kaspersky text parsers
│  ├─ VaultItem.cs             # decrypted item model
│  ├─ VaultDocument.cs         # on-disk JSON DTOs
│  └─ Exceptions.cs
├─ src/IPasswrd.Cli/           # console front-end over the core (dev tool)
├─ tests/IPasswrd.Core.Tests/  # xUnit suite (42 tests: crypto, import, TOTP, audit, generator)
├─ reference/                  # executable Python spec of the same scheme
└─ prototype/                  # clickable HTML UI prototype
```

## Next steps

1. Vault persistence + a CLI to exercise the core end-to-end. **Done** (`src/IPasswrd.Cli`, verified end-to-end on Windows).
2. Import: CSV from Chrome / Edge / Yandex / Kaspersky. **Done.** (Apple Passwords import next.)
3. TOTP 2FA, local security audit, CSPRNG generator. **Done.**
4. Sync layer (decide: encrypted file in cloud vs. managed realtime backend).
5. Avalonia UI on Windows, ported from the prototype's design tokens.
