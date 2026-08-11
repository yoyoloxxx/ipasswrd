# IPasswrd

Free, cross-platform password manager. A full alternative to Kaspersky Password Manager, built on an
own end-to-end-encrypted vault so the same data works on Windows, iPhone and Android.

**Download for Windows:** https://yoyoloxxx.github.io/ipasswrd/ — installs in one click and updates itself.

**Status: the Windows app has shipped.** Encrypted vault (Argon2id + AES-256-GCM), full GUI, browser
autofill via the bundled extension (passwords, cards, forms, several accounts per site), passkeys,
built-in TOTP authenticator, breach check against Have I Been Pwned (k-anonymity), password generator,
Windows Hello quick unlock (TPM-backed), optional sync through the user's own iCloud Drive / Google
Drive (only the encrypted file ever leaves the device), in-memory key protection, anti-screenshot mode,
and automatic updates from GitHub Releases. Android and iOS apps are in progress on the same core.

## Repository layout

```
ipasswrd/
├─ core/       IPasswrd.Core — the encrypted-vault engine, shared by every platform
├─ windows/    Avalonia desktop app, native-messaging Host, browser extension, build scripts
├─ mobile/     Android + iOS — one .NET MAUI project (plus the iOS AutoFill extension)
├─ docs/       download page + privacy policy (GitHub Pages)
├─ tests/      xUnit suite for the security core
├─ tools/     dev CLI, import checker, packaging/screenshot scripts
└─ reference/  executable Python spec of the crypto scheme
```

## Architecture decisions

- **Own encrypted vault is the source of truth.** We hold the data ourselves, encrypted with a key only
  the user's master password can produce. Because the vault is our own format, the underlying OS does not
  matter, which is exactly what makes one codebase work on Windows, iPhone and Android (the model Bitwarden
  and Proton Pass use). Apple Passwords and Google Credential Manager are used for *import* and, on their
  own platforms, as a *mirror* so the OS-native autofill keeps working.
- **Stack: C# everywhere.** Avalonia on desktop, .NET MAUI on mobile, one plain-.NET core with no UI
  dependency underneath both.
- **Order: Windows first, mobile next** — on the same core.
- **Sync is transport-agnostic.** The vault serialises to one self-contained encrypted blob, so *how* it
  syncs (iCloud Drive today, Google Drive for Android) is a swappable decision. The core neither knows nor cares.

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

At runtime the unwrapped DEK is additionally shielded in memory (`CryptProtectMemory`) and wiped on lock.

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
dotnet build core/IPasswrd.Core
dotnet test  tests/IPasswrd.Core.Tests
```

Windows packaging lives in `windows/`: `publish-installer.cmd <version>` builds the Velopack installer
and update packages, `upload-release.cmd <version>` publishes them to GitHub Releases. Mobile CI builds
run from `.github/workflows/`.

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

Before the first public release the app also went through an adversarial security audit (red team);
everything found was fixed and re-verified. See `SECURITY.md`.

## Command line (dev)

A thin console front-end over the core (`tools/IPasswrd.Cli`), for exercising the vault and imports:

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

## Roadmap

1. Encrypted core + CLI + imports + TOTP + audit + generator. **Done.**
2. Windows app: GUI, browser autofill, passkeys, Windows Hello, sync, auto-updates. **Done — shipped.**
3. Android app (same core, Google Drive sync). **In progress.**
4. iOS app (same core, iCloud sync, AutoFill extension). **In progress.**
5. Shared-clipboard rework, then macOS.
