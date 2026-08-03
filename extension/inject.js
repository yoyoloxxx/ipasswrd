// IPasswrd WebAuthn provider (MAIN world).
//
// Overrides navigator.credentials.create/get so IPasswrd can act as a passkey
// authenticator backed by the encrypted vault. All crypto runs here in WebCrypto;
// only the private key (as a JWK) and metadata are stored — through the isolated
// content script → native host → app — inside the vault.
//
// Non-breaking by design: if the vault is locked, the site excludes ES256, or there
// is no matching IPasswrd passkey, we fall back to the browser's native authenticator
// (Windows Hello etc.) by calling the original method. IPasswrd augments, never blocks.
(() => {
  "use strict";
  if (window.__ipwAuthnLoaded) return;
  window.__ipwAuthnLoaded = true;

  const nav = navigator.credentials;
  if (!nav || !window.PublicKeyCredential) return;

  // Capture originals at document_start, before page scripts can tamper.
  const subtle = crypto.subtle;
  const getRandom = crypto.getRandomValues.bind(crypto);
  const origCreate = nav.create.bind(nav);
  const origGet = nav.get.bind(nav);
  const TE = new TextEncoder();

  // ---------- byte helpers ----------
  const toU8 = (v) => {
    if (v == null) return new Uint8Array(0);
    if (v instanceof Uint8Array) return v;
    if (v instanceof ArrayBuffer) return new Uint8Array(v);
    if (ArrayBuffer.isView(v)) return new Uint8Array(v.buffer, v.byteOffset, v.byteLength);
    if (Array.isArray(v)) return Uint8Array.from(v);
    return new Uint8Array(0);
  };
  const cat = (...arrs) => {
    let n = 0;
    for (const a of arrs) n += a.length;
    const out = new Uint8Array(n);
    let o = 0;
    for (const a of arrs) { out.set(a, o); o += a.length; }
    return out;
  };
  const u16 = (n) => new Uint8Array([(n >> 8) & 255, n & 255]);
  const u32 = (n) => new Uint8Array([(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]);

  const b64urlEnc = (bytes) => {
    let s = "";
    const b = toU8(bytes);
    for (let i = 0; i < b.length; i++) s += String.fromCharCode(b[i]);
    return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  };
  const b64urlDec = (str) => {
    str = String(str).replace(/-/g, "+").replace(/_/g, "/");
    while (str.length % 4) str += "=";
    const bin = atob(str);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  };
  const sha256 = async (data) =>
    new Uint8Array(await subtle.digest("SHA-256", typeof data === "string" ? TE.encode(data) : toU8(data)));

  // ---------- minimal CBOR encoder (only what WebAuthn needs) ----------
  function cborInt(n) {
    // major 0 for >=0, major 1 for <0; all our ints fit the 0..23 immediate range
    if (n >= 0) {
      if (n < 24) return new Uint8Array([n]);
      if (n < 256) return new Uint8Array([0x18, n]);
      return cat(new Uint8Array([0x19]), u16(n));
    }
    const m = -1 - n;
    if (m < 24) return new Uint8Array([0x20 | m]);
    if (m < 256) return new Uint8Array([0x38, m]);
    return cat(new Uint8Array([0x39]), u16(m));
  }
  function cborLen(major, len) {
    const M = major << 5;
    if (len < 24) return new Uint8Array([M | len]);
    if (len < 256) return new Uint8Array([M | 24, len]);
    if (len < 65536) return cat(new Uint8Array([M | 25]), u16(len));
    return cat(new Uint8Array([M | 26]), u32(len));
  }
  const cborBytes = (b) => cat(cborLen(2, b.length), b);
  const cborText = (s) => { const b = TE.encode(s); return cat(cborLen(3, b.length), b); };
  const cborMap = (n) => cborLen(5, n);

  // COSE_Key for an EC2 P-256 public key: {1:2, 3:-7, -1:1, -2:x, -3:y}
  function coseEc2(x, y) {
    return cat(
      cborMap(5),
      cborInt(1), cborInt(2),      // kty: EC2
      cborInt(3), cborInt(-7),     // alg: ES256
      cborInt(-1), cborInt(1),     // crv: P-256
      cborInt(-2), cborBytes(x),   // x
      cborInt(-3), cborBytes(y),   // y
    );
  }
  // attestationObject with "none" attestation
  function attObjNone(authData) {
    return cat(
      cborMap(3),
      cborText("fmt"), cborText("none"),
      cborText("attStmt"), cborMap(0),
      cborText("authData"), cborBytes(authData),
    );
  }

  // ---------- DER encoding for the assertion signature (raw r||s → DER) ----------
  function derInt(v) {
    let i = 0;
    while (i < v.length - 1 && v[i] === 0) i++;
    v = v.slice(i);
    if (v[0] & 0x80) v = cat(new Uint8Array([0]), v);
    return cat(new Uint8Array([0x02, v.length]), v);
  }
  function rawSigToDer(raw) {
    const r = derInt(raw.slice(0, 32));
    const s = derInt(raw.slice(32, 64));
    return cat(new Uint8Array([0x30, r.length + s.length]), r, s);
  }

  // ---------- bridge to the isolated content script ----------
  let seq = 0;
  function request(cmd, data) {
    return new Promise((resolve) => {
      const id = "ipw" + (++seq) + "_" + b64urlEnc(getRandom(new Uint8Array(6)));
      const timer = setTimeout(() => { window.removeEventListener("message", onMsg); resolve({ ok: false, error: "timeout" }); }, 60000);
      function onMsg(e) {
        const d = e.data;
        if (e.source !== window || !d || d.dir !== "ipw->page" || d.id !== id) return;
        clearTimeout(timer);
        window.removeEventListener("message", onMsg);
        resolve(d.res || { ok: false, error: "empty" });
      }
      window.addEventListener("message", onMsg);
      window.postMessage({ dir: "ipw->cs", id, cmd, data }, location.origin);
    });
  }

  const supportsEs256 = (params) => !params || !params.length || params.some((p) => p && p.alg === -7);

  // ---------- create (registration) ----------
  async function ipwCreate(options) {
    const pk = options && options.publicKey;
    if (!pk || !supportsEs256(pk.pubKeyCredParams)) return origCreate(options);

    const rpId = (pk.rp && pk.rp.id) || location.hostname;
    const status = await request("passkeyList", { rpId });   // also tells us whether the vault is unlocked
    if (!status || !status.ok || !status.unlocked) return origCreate(options);   // locked → native fallback

    const user = pk.user || {};
    const challenge = toU8(pk.challenge);
    const userId = toU8(user.id);

    const clientData = { type: "webauthn.create", challenge: b64urlEnc(challenge), origin: location.origin, crossOrigin: false };
    const clientDataJSON = TE.encode(JSON.stringify(clientData));

    const kp = await subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
    const jwk = await subtle.exportKey("jwk", kp.privateKey);
    const pub = await subtle.exportKey("jwk", kp.publicKey);
    const x = b64urlDec(pub.x), y = b64urlDec(pub.y);
    const spki = new Uint8Array(await subtle.exportKey("spki", kp.publicKey));

    const credId = getRandom(new Uint8Array(16));
    const rpIdHash = await sha256(rpId);
    const aaguid = new Uint8Array(16);   // zeroes — anonymous authenticator
    const attData = cat(aaguid, u16(credId.length), credId, coseEc2(x, y));
    const authData = cat(rpIdHash, new Uint8Array([0x45]), u32(0), attData);   // flags UP|UV|AT
    const attestationObject = attObjNone(authData);

    const save = await request("passkeySave", {
      rpId,
      credId: b64urlEnc(credId),
      userHandle: b64urlEnc(userId),
      userName: user.name || user.displayName || "",
      privJwk: JSON.stringify(jwk),
    });
    if (!save || !save.ok) return origCreate(options);   // couldn't persist → don't claim success

    return {
      id: b64urlEnc(credId),
      rawId: credId.buffer,
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        clientDataJSON: clientDataJSON.buffer,
        attestationObject: attestationObject.buffer,
        getAuthenticatorData: () => authData.buffer,
        getPublicKey: () => spki.buffer,
        getPublicKeyAlgorithm: () => -7,
        getTransports: () => ["internal"],
      },
      getClientExtensionResults: () => ({}),
    };
  }

  // ---------- get (authentication) ----------
  async function ipwGet(options) {
    const pk = options && options.publicKey;
    if (!pk) return origGet(options);
    if (options.mediation === "conditional") return origGet(options);   // autofill UI → let the browser drive

    const rpId = pk.rpId || location.hostname;
    const list = await request("passkeyList", { rpId });
    if (!list || !list.ok || !list.unlocked) return origGet(options);   // locked → native fallback

    let items = list.items || [];
    const allow = pk.allowCredentials || [];
    if (allow.length) {
      const ids = allow.map((c) => b64urlEnc(toU8(c.id)));
      items = items.filter((it) => ids.includes(it.credId));
    }
    if (!items.length) return origGet(options);   // no IPasswrd passkey here → native fallback

    const it = items[0];
    const credId = b64urlDec(it.credId);
    const challenge = toU8(pk.challenge);

    const clientData = { type: "webauthn.get", challenge: b64urlEnc(challenge), origin: location.origin, crossOrigin: false };
    const clientDataJSON = TE.encode(JSON.stringify(clientData));

    const rpIdHash = await sha256(rpId);
    const authData = cat(rpIdHash, new Uint8Array([0x05]), u32(0));   // flags UP|UV
    const clientHash = await sha256(clientDataJSON);

    const key = await subtle.importKey("jwk", JSON.parse(it.privJwk), { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
    const raw = new Uint8Array(await subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, cat(authData, clientHash)));
    const signature = rawSigToDer(raw);
    const userHandle = it.userHandle ? b64urlDec(it.userHandle) : null;

    return {
      id: it.credId,
      rawId: credId.buffer,
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        clientDataJSON: clientDataJSON.buffer,
        authenticatorData: authData.buffer,
        signature: signature.buffer,
        userHandle: userHandle ? userHandle.buffer : null,
      },
      getClientExtensionResults: () => ({}),
    };
  }

  // ---------- install overrides ----------
  try {
    nav.create = function (options) {
      try { return ipwCreate(options); }
      catch (e) { return origCreate(options); }
    };
    nav.get = function (options) {
      try { return ipwGet(options); }
      catch (e) { return origGet(options); }
    };
    // Advertise a platform authenticator so RPs offer the passkey path.
    PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable = () => Promise.resolve(true);
    if (!PublicKeyCredential.isConditionalMediationAvailable)
      PublicKeyCredential.isConditionalMediationAvailable = () => Promise.resolve(false);
  } catch (e) { /* leave native behaviour intact on any failure */ }
})();
