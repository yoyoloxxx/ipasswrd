"""
Executable spec for the IPasswrd vault security core.
Run: python3 test_vault_reference.py   (exits non-zero on any failure)

Each test asserts one security property. Green here == the scheme is correct;
the C# core mirrors the same construction.
"""
import json
import sys

from vault_reference import (
    Vault, VaultItem, WrongMasterPassword, VaultIntegrityError,
)

PW = "correct horse battery staple"
# smaller Argon2id cost so the suite runs fast; real vault uses 64 MiB
FAST = dict(memory_kib=8192, iterations=1, parallelism=1)

_passed = 0


def check(name, fn):
    global _passed
    try:
        fn()
    except AssertionError as e:
        print(f"  FAIL  {name}: {e}")
        raise
    except Exception as e:  # noqa
        print(f"  ERROR {name}: {type(e).__name__}: {e}")
        raise
    _passed += 1
    print(f"  ok    {name}")


def sample():
    return VaultItem(type="account", title="Т-Банк",
                     fields={"username": "gleb.hse@gmail.com", "password": "k4!Vr#92mQzL&wPn",
                             "url": "tbank.ru", "totp": "otpauth://..."},
                     notes="основной аккаунт", favorite=True)


# 1. round-trip: create -> add -> serialize -> unlock -> read identical
def t_round_trip():
    v = Vault.create(PW, FAST)
    rid = v.add(sample())
    blob = v.serialize()
    v2 = Vault.unlock(blob, PW)
    item = v2.get(rid)
    assert item.title == "Т-Банк"
    assert item.fields["password"] == "k4!Vr#92mQzL&wPn"
    assert item.favorite is True


# 2. wrong master password -> WrongMasterPassword, no data leaked
def t_wrong_password():
    v = Vault.create(PW, FAST)
    v.add(sample())
    blob = v.serialize()
    try:
        Vault.unlock(blob, "wrong password")
    except WrongMasterPassword:
        return
    assert False, "unlock must fail with a wrong password"


# 3. tampering with a record ciphertext is detected (AES-GCM auth)
def t_tamper_record():
    v = Vault.create(PW, FAST)
    rid = v.add(sample())
    blob = bytearray(v.serialize())
    doc = json.loads(bytes(blob).decode())
    ct = bytearray(__import__("base64").b64decode(doc["records"][0]["ciphertext"]))
    ct[5] ^= 0x01  # flip one bit
    doc["records"][0]["ciphertext"] = __import__("base64").b64encode(bytes(ct)).decode()
    v2 = Vault.unlock(json.dumps(doc).encode(), PW)
    try:
        v2.get(rid)
    except VaultIntegrityError:
        return
    assert False, "tampered record must fail authentication"


# 4. tampering with the wrapped key is detected -> looks like wrong password
def t_tamper_wrapped_key():
    v = Vault.create(PW, FAST)
    blob = v.serialize()
    doc = json.loads(blob.decode())
    ct = bytearray(__import__("base64").b64decode(doc["wrappedKey"]["ciphertext"]))
    ct[0] ^= 0x80
    doc["wrappedKey"]["ciphertext"] = __import__("base64").b64encode(bytes(ct)).decode()
    try:
        Vault.unlock(json.dumps(doc).encode(), PW)
    except WrongMasterPassword:
        return
    assert False, "tampered wrapped key must fail to unlock"


# 5. change master password: old fails, new works, records NOT re-encrypted
def t_change_master_password():
    v = Vault.create(PW, FAST)
    rid = v.add(sample())
    ct_before = v.raw_records[0]["ciphertext"]
    dek_before = v.dek

    NEW = "new stronger passphrase 2026"
    v.change_master_password(PW, NEW)
    blob = v.serialize()

    # records untouched: same ciphertext bytes, same DEK
    assert v.raw_records[0]["ciphertext"] == ct_before, "records must not be re-encrypted"
    assert v.dek == dek_before, "DEK must survive a password change"

    # old password no longer unlocks
    try:
        Vault.unlock(blob, PW)
        assert False, "old password must stop working"
    except WrongMasterPassword:
        pass
    # new password unlocks and data is intact
    v2 = Vault.unlock(blob, NEW)
    assert v2.get(rid).fields["username"] == "gleb.hse@gmail.com"

    # a wrong *old* password is rejected by change_master_password
    v3 = Vault.unlock(blob, NEW)
    try:
        v3.change_master_password("not the old one", "whatever")
        assert False, "wrong current password must be rejected"
    except WrongMasterPassword:
        pass


# 6. record<->id binding: swapping two records' ciphertext breaks decryption
def t_record_id_binding():
    v = Vault.create(PW, FAST)
    r1 = v.add(VaultItem("account", "A", {"password": "aaa"}))
    r2 = v.add(VaultItem("account", "B", {"password": "bbb"}))
    recs = v.raw_records
    recs[0]["ciphertext"], recs[1]["ciphertext"] = recs[1]["ciphertext"], recs[0]["ciphertext"]
    recs[0]["nonce"], recs[1]["nonce"] = recs[1]["nonce"], recs[0]["nonce"]
    for rid in (r1, r2):
        try:
            v.get(rid)
            assert False, "swapped ciphertext must not decrypt under a different id"
        except VaultIntegrityError:
            pass


# 7. salt matters: same password, different salt -> different KEK/wrappedKey
def t_salt_effective():
    a = Vault.create(PW, FAST).serialize()
    b = Vault.create(PW, FAST).serialize()
    da, db = json.loads(a.decode()), json.loads(b.decode())
    assert da["kdf"]["salt"] != db["kdf"]["salt"], "each vault must get a fresh salt"
    assert da["wrappedKey"]["ciphertext"] != db["wrappedKey"]["ciphertext"]


# 8. nonce randomness: encrypting the same item twice -> different ciphertext
def t_nonce_randomness():
    v = Vault.create(PW, FAST)
    r1 = v.add(sample())
    r2 = v.add(sample())
    recs = {r["id"]: r for r in v.raw_records}
    assert recs[r1]["ciphertext"] != recs[r2]["ciphertext"], "identical plaintext must not yield identical ciphertext"
    assert recs[r1]["nonce"] != recs[r2]["nonce"]


# 9. delete tombstones the record and drops the payload
def t_delete_tombstone():
    v = Vault.create(PW, FAST)
    rid = v.add(sample())
    v.delete(rid)
    assert all(i[0] != rid for i in v.items()), "deleted item must not appear"
    rec = next(r for r in v.raw_records if r["id"] == rid)
    assert rec["deleted"] is True and rec["ciphertext"] == "", "payload must be dropped on delete"


TESTS = [
    ("round-trip create/unlock/read", t_round_trip),
    ("wrong master password rejected", t_wrong_password),
    ("record tampering detected", t_tamper_record),
    ("wrapped-key tampering detected", t_tamper_wrapped_key),
    ("master-password change re-wraps only the key", t_change_master_password),
    ("record bound to its id (no swap)", t_record_id_binding),
    ("per-vault salt is effective", t_salt_effective),
    ("per-record nonce is random", t_nonce_randomness),
    ("delete tombstones + drops payload", t_delete_tombstone),
]

if __name__ == "__main__":
    print("IPasswrd vault — security core spec\n")
    for name, fn in TESTS:
        check(name, fn)
    print(f"\n{_passed}/{len(TESTS)} properties verified.")
    sys.exit(0 if _passed == len(TESTS) else 1)
