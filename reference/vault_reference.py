"""
IPasswrd vault — reference implementation of the security core.

Purpose: this Python module is an executable specification of the vault's
cryptographic scheme. It uses the SAME primitives the C# core uses
(Argon2id for key derivation, AES-256-GCM for authenticated encryption),
so running its test suite green proves the construction is correct. The C#
implementation in ../core/IPasswrd.Core mirrors this exact construction
(same primitives, same KEK/DEK split, same AAD binding).

Scheme (envelope encryption, KEK/DEK split):

    master password ──Argon2id(salt, params)──▶ KEK (key-encryption key)
    random 32-byte DEK (data-encryption key)
    wrappedKey = AES-256-GCM(KEK, DEK, aad="ipasswrd/vault-key/v1")
    each record = AES-256-GCM(DEK, json(item), aad="ipasswrd/record/v1/"+id)

Why the split: changing the master password only re-wraps the DEK (one tiny
operation) instead of re-encrypting every record. AAD binding of each record
to its id stops an attacker swapping ciphertexts between records. The wrapped
key is cached on the unlocked vault, so saving never needs the password again.
Transport (file in cloud, managed backend, ...) is out of scope: the vault
serialises to a self-contained JSON blob, so any transport works.
"""
from __future__ import annotations

import base64
import json
import os
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone

from cryptography.hazmat.primitives.kdf.argon2 import Argon2id
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.exceptions import InvalidTag

FORMAT_VERSION = 1
KEY_LEN = 32          # 256-bit keys
NONCE_LEN = 12        # AES-GCM standard nonce
SALT_LEN = 16

AAD_VAULT_KEY = b"ipasswrd/vault-key/v1"
AAD_RECORD_PREFIX = b"ipasswrd/record/v1/"

# Argon2id cost. Stored in the vault header so it can be raised later and the
# vault re-derived. 64 MiB / t=3 / p=1 is a mobile-safe strong baseline
# (matches Bitwarden's Argon2 default, well above OWASP's 19 MiB minimum).
DEFAULT_KDF = dict(memory_kib=65536, iterations=3, parallelism=1)


class WrongMasterPassword(Exception):
    """Raised when the master password fails to unwrap the vault key."""


class VaultIntegrityError(Exception):
    """Raised when a record fails authentication (tampering / corruption)."""


def _b64e(b: bytes) -> str:
    return base64.b64encode(b).decode("ascii")


def _b64d(s: str) -> bytes:
    return base64.b64decode(s.encode("ascii"))


def _now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def _canonical(obj) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


# ---------------------------------------------------------------------------

@dataclass
class KdfParams:
    algorithm: str = "argon2id"
    memory_kib: int = DEFAULT_KDF["memory_kib"]
    iterations: int = DEFAULT_KDF["iterations"]
    parallelism: int = DEFAULT_KDF["parallelism"]
    salt: bytes = b""

    def derive(self, password: str) -> bytes:
        return Argon2id(
            salt=self.salt,
            length=KEY_LEN,
            iterations=self.iterations,
            lanes=self.parallelism,
            memory_cost=self.memory_kib,
        ).derive(password.encode("utf-8"))

    def to_json(self) -> dict:
        return dict(algorithm=self.algorithm, memoryKiB=self.memory_kib,
                    iterations=self.iterations, parallelism=self.parallelism, salt=_b64e(self.salt))

    @staticmethod
    def from_json(d: dict) -> "KdfParams":
        return KdfParams(algorithm=d["algorithm"], memory_kib=d["memoryKiB"],
                         iterations=d["iterations"], parallelism=d["parallelism"], salt=_b64d(d["salt"]))

    @staticmethod
    def fresh(memory_kib=None, iterations=None, parallelism=None) -> "KdfParams":
        c = DEFAULT_KDF
        return KdfParams(salt=os.urandom(SALT_LEN),
                         memory_kib=memory_kib or c["memory_kib"],
                         iterations=iterations or c["iterations"],
                         parallelism=parallelism or c["parallelism"])


@dataclass
class VaultItem:
    """The decrypted content of one record."""
    type: str                       # account | card | document | note | passkey
    title: str
    fields: dict = field(default_factory=dict)
    notes: str = ""
    favorite: bool = False

    def to_json(self) -> dict:
        return dict(type=self.type, title=self.title, fields=self.fields,
                    notes=self.notes, favorite=self.favorite)

    @staticmethod
    def from_json(d: dict) -> "VaultItem":
        return VaultItem(type=d["type"], title=d["title"], fields=d.get("fields", {}),
                         notes=d.get("notes", ""), favorite=d.get("favorite", False))


# ---------------------------------------------------------------------------

class Vault:
    """An unlocked vault held in memory. Never persists the DEK in the clear."""

    def __init__(self, kdf: KdfParams, dek: bytes, wrapped_key: dict, records: list[dict]):
        self._kdf = kdf
        self._dek = dek
        self._wrapped = wrapped_key          # {"nonce":.., "ciphertext":..}
        self._records = records              # list of encrypted record dicts

    # ---- lifecycle ----

    @staticmethod
    def create(master_password: str, kdf_cfg: dict | None = None) -> "Vault":
        cfg = kdf_cfg or {}
        kdf = KdfParams.fresh(**cfg)
        dek = os.urandom(KEY_LEN)
        wrapped = Vault._wrap(kdf.derive(master_password), dek)
        return Vault(kdf, dek, wrapped, [])

    @staticmethod
    def unlock(blob: bytes, master_password: str) -> "Vault":
        doc = json.loads(blob.decode("utf-8"))
        if doc.get("format") != FORMAT_VERSION:
            raise ValueError(f"unsupported vault format: {doc.get('format')}")
        kdf = KdfParams.from_json(doc["kdf"])
        kek = kdf.derive(master_password)
        dek = Vault._unwrap(kek, doc["wrappedKey"])   # raises WrongMasterPassword
        return Vault(kdf, dek, doc["wrappedKey"], list(doc["records"]))

    def serialize(self) -> bytes:
        """Self-contained JSON blob. No password needed: the wrapped key is cached."""
        doc = dict(format=FORMAT_VERSION, kdf=self._kdf.to_json(),
                   wrappedKey=self._wrapped, records=self._records)
        return _canonical(doc)

    # ---- records ----

    def add(self, item: VaultItem) -> str:
        rid = str(uuid.uuid4())
        self._records.append(self._encrypt_record(rid, item))
        return rid

    def update(self, rid: str, item: VaultItem) -> None:
        for i, rec in enumerate(self._records):
            if rec["id"] == rid and not rec.get("deleted"):
                self._records[i] = self._encrypt_record(rid, item)
                return
        raise KeyError(rid)

    def delete(self, rid: str) -> None:
        # Tombstone (kept for last-write-wins sync later), payload removed.
        for rec in self._records:
            if rec["id"] == rid:
                rec.update(deleted=True, nonce="", ciphertext="", updatedAt=_now_iso())
                return
        raise KeyError(rid)

    def items(self) -> list[tuple[str, VaultItem]]:
        return [(r["id"], self._decrypt_record(r)) for r in self._records if not r.get("deleted")]

    def get(self, rid: str) -> VaultItem:
        for rec in self._records:
            if rec["id"] == rid and not rec.get("deleted"):
                return self._decrypt_record(rec)
        raise KeyError(rid)

    # ---- master password ----

    def change_master_password(self, old_password: str, new_password: str) -> None:
        # Prove the caller knows the old secret, then rotate salt and re-wrap the
        # SAME dek under the new KEK. Records are untouched (the point of KEK/DEK).
        try:
            Vault._unwrap(self._kdf.derive(old_password), self._wrapped)
        except WrongMasterPassword:
            raise WrongMasterPassword("current master password is incorrect")
        self._kdf = KdfParams.fresh(self._kdf.memory_kib, self._kdf.iterations, self._kdf.parallelism)
        self._wrapped = Vault._wrap(self._kdf.derive(new_password), self._dek)

    # ---- key wrapping ----

    @staticmethod
    def _wrap(kek: bytes, dek: bytes) -> dict:
        nonce = os.urandom(NONCE_LEN)
        ct = AESGCM(kek).encrypt(nonce, dek, AAD_VAULT_KEY)
        return dict(nonce=_b64e(nonce), ciphertext=_b64e(ct))

    @staticmethod
    def _unwrap(kek: bytes, wrapped: dict) -> bytes:
        try:
            return AESGCM(kek).decrypt(_b64d(wrapped["nonce"]), _b64d(wrapped["ciphertext"]), AAD_VAULT_KEY)
        except InvalidTag:
            raise WrongMasterPassword("master password is incorrect")

    # ---- record encryption ----

    def _encrypt_record(self, rid: str, item: VaultItem) -> dict:
        nonce = os.urandom(NONCE_LEN)
        aad = AAD_RECORD_PREFIX + rid.encode("ascii")
        ct = AESGCM(self._dek).encrypt(nonce, _canonical(item.to_json()), aad)
        return dict(id=rid, nonce=_b64e(nonce), ciphertext=_b64e(ct), updatedAt=_now_iso(), deleted=False)

    def _decrypt_record(self, rec: dict) -> VaultItem:
        aad = AAD_RECORD_PREFIX + rec["id"].encode("ascii")
        try:
            pt = AESGCM(self._dek).decrypt(_b64d(rec["nonce"]), _b64d(rec["ciphertext"]), aad)
        except InvalidTag:
            raise VaultIntegrityError(f"record {rec['id']} failed authentication")
        return VaultItem.from_json(json.loads(pt.decode("utf-8")))

    # ---- test helpers ----
    @property
    def raw_records(self) -> list[dict]:
        return self._records

    @property
    def dek(self) -> bytes:
        return self._dek
