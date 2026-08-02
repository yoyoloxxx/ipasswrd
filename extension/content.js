// IPasswrd content script: in-field key badge + dropdown, autofill saved logins,
// offer to save new ones. Runs in an isolated world; page scripts cannot read its state.

(() => {
  "use strict";
  if (window.__ipasswrdLoaded) return;
  window.__ipasswrdLoaded = true;

  const BRASS = "#BC9F5C", INK = "#17191D", PAPER = "#F4F1E8";
  const TOP = window === window.top;
  const KEY_SVG = `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="${BRASS}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 2l-2 2m-7.6 7.6a5.5 5.5 0 1 1-7.78 7.78 5.5 5.5 0 0 1 7.78-7.78zm0 0L19 4m-3.5 3.5L18 10"/></svg>`;
  const TRAY_IMG = "data:image/png;base64,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";

  // ---------- registrable domain (mirrors Dedup.cs) ----------
  const TWO_LEVEL = new Set(["co.uk","org.uk","gov.uk","ac.uk","me.uk","com.br","com.au","net.au","org.au",
    "co.jp","or.jp","co.kr","com.tr","co.in","com.ua","net.ua","org.ua","net.ru","org.ru",
    "co.il","com.mx","co.nz","com.tw","com.sg","com.cn","com.hk","com.pl","com.pt","com.es",
    "com.ar","co.za","com.my","co.th","com.vn","com.ph","com.co"]);

  function baseDomain(host) {
    host = (host || "").toLowerCase().replace(/^www\./, "").replace(/\.$/, "");
    if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return host;
    const l = host.split(".").filter(Boolean);
    if (l.length <= 2) return host;
    const lastTwo = l.slice(-2).join(".");
    return TWO_LEVEL.has(lastTwo) ? l.slice(-3).join(".") : lastTwo;
  }

  const send = (msg) => new Promise((res) => {
    try { chrome.runtime.sendMessage(msg, (r) => res(chrome.runtime.lastError ? { ok: false, error: "bg_unreachable" } : (r || { ok: false }))); }
    catch (e) { res({ ok: false, error: String(e) }); }
  });

  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

  // ---------- field discovery / classification ----------
  const visible = (el) => {
    if (!el || !el.getBoundingClientRect) return false;
    const r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) return false;
    const s = getComputedStyle(el);
    return s.visibility !== "hidden" && s.display !== "none";
  };

  function fieldText(el) {
    let t = [el.name, el.id, el.getAttribute("placeholder"), el.getAttribute("aria-label"),
             el.getAttribute("autocomplete"), el.getAttribute("inputmode")].filter(Boolean).join(" ");
    try {
      if (el.labels && el.labels.length) for (const l of el.labels) t += " " + (l.textContent || "");
      else if (el.id) { const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l) t += " " + l.textContent; }
    } catch (e) { /* ignore */ }
    return t.toLowerCase();
  }

  // -> "password" | "login" | "card-number" | "card-cvc" | "card-exp" | "card-holder" | "otp" | "doc" | null
  function classify(el) {
    if (!el || el.tagName !== "INPUT") return null;
    const type = (el.type || "text").toLowerCase();
    if (["hidden","checkbox","radio","submit","button","file","range","color","image","reset"].includes(type)) return null;
    const ac = (el.getAttribute("autocomplete") || "").toLowerCase();
    const t = fieldText(el);

    if (ac.includes("cc-number") || /card ?number|cardnum|numberofcard|\bpan\b|номер карты/.test(t)) return "card-number";
    if (ac.includes("cc-csc") || /\bcvc\b|\bcvv\b|cvc2|cvv2|security ?code|код безопасн|защитный код/.test(t)) return "card-cvc";
    if (ac.includes("cc-exp-month")) return "card-exp-month";
    if (ac.includes("cc-exp-year")) return "card-exp-year";
    if (ac.includes("cc-exp") || /exp(iry|iration)?|мм ?\/ ?гг|срок действ|valid ?thru/.test(t)) {
      if (/мм ?\/ ?гг|mm ?\/ ?yy/.test(t)) return "card-exp";   // combined "ММ/ГГ" field — NOT a month field
      if (/month|месяц|(^|[^a-z])mm([^a-z]|$)|(^|[^а-яё])мм([^а-яё]|$)/.test(t)) return "card-exp-month";
      if (/year|год|(^|[^a-z])yy(yy)?([^a-z]|$)|(^|[^а-яё])гг(гг)?([^а-яё]|$)/.test(t)) return "card-exp-year";
      return "card-exp";
    }
    if (ac.includes("cc-name") || /cardholder|card ?holder|имя на карте|владелец|держатель|(^|[^a-z])holder([^a-z]|$)/.test(t)) return "card-holder";
    if (ac.includes("one-time-code") || /one[- ]?time|\botp\b|\btotp\b|2fa|two[- ]?factor|verification code|проверочн(ый|ого) код|код из (смс|sms)|одноразов/.test(t)) return "otp";

    if (type === "password") {
      if (/\bcvc\b|\bcvv\b/.test(t)) return "card-cvc";
      return "password";
    }
    if (type === "email" || type === "tel" || ac.includes("username") || ac.includes("email") ||
        /\b(user(name)?|login|e[-\s]?mail|phone|tel|account|identifier)\b/.test(t) ||
        /(почт[аеы]|логин|телефон)/.test(t)) return "login";   // Cyrillic: no \b — JS word boundaries are Latin-only
    if (/passport|документ|udost|снилс|\bsnils\b|\binn\b|\bинн\b|серия и номер|номер документа|driver.?s? licen|водительск/.test(t)) return "doc";
    return null;
  }

  function findPairs() {
    const pws = [...document.querySelectorAll("input[type=password]")].filter(visible);
    return pws.map((pw) => {
      const scope = pw.form || document;
      let user = null;
      for (const i of scope.querySelectorAll("input")) {
        if (i === pw || !visible(i)) continue;
        const t = (i.type || "text").toLowerCase();
        if (!["text", "email", "tel"].includes(t)) continue;
        if (i.compareDocumentPosition(pw) & Node.DOCUMENT_POSITION_FOLLOWING) user = i;
      }
      return { user, pw };
    });
  }

  function pairFor(field) {
    for (const p of findPairs()) { if (p.user === field || p.pw === field) return p; }
    return { user: classify(field) === "login" ? field : null, pw: field.type === "password" ? field : null };
  }

  function candidateFields() {
    const map = new Map();
    for (const el of document.querySelectorAll("input")) {
      if (!visible(el)) continue;
      const k = classify(el);
      if (k) map.set(el, k);
    }
    for (const p of findPairs()) {
      if (p.pw && classify(p.pw) === "card-cvc") continue;   // CVC is type=password too — not a login pair
      if (p.user && visible(p.user) && !map.has(p.user)) map.set(p.user, "login");
      if (p.pw && visible(p.pw) && !map.has(p.pw)) map.set(p.pw, "password");
    }
    return map;
  }

  // ---------- fill ----------
  function setVal(el, v) {
    if (!el) return;
    const d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value");
    d.set.call(el, v);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }
  function fill(pair, item) {
    if (pair.user && item.username) setVal(pair.user, item.username);
    if (pair.pw) setVal(pair.pw, item.password);
    lastTyped = { u: item.username || lastTyped.u, p: item.password || lastTyped.p };
  }
  function fillCard(anchor, card) {
    const scope = anchor.form || document;
    const exp = String(card.expiry || "");
    const m = exp.match(/(\d{1,2})\s*[\/.\-]?\s*(\d{2,4})/);
    const mm = m ? m[1].padStart(2, "0") : "";
    let yy = m ? m[2] : "";
    if (yy.length === 4) yy = yy.slice(-2);
    const yr = (el) => (el.maxLength === 4 ? "20" + yy : yy);   // "2031" for 4-char year boxes
    const expFields = [];
    for (const el of scope.querySelectorAll("input")) {
      if (!visible(el)) continue;
      const k = classify(el);
      if (k === "card-number" && card.number) setVal(el, card.number);
      else if (k === "card-cvc" && card.cvc) setVal(el, card.cvc);
      else if (k === "card-holder" && card.holder) setVal(el, card.holder);
      else if (k === "card-exp-month" && mm) setVal(el, mm);
      else if (k === "card-exp-year" && yy) setVal(el, yr(el));
      else if (k === "card-exp") expFields.push(el);
      else if (!k) {
        // Bare "MM" / "YY" / "ГГГГ" boxes next to a card number carry no "expiry" wording at all —
        // inside an explicit card fill it is safe to recognise them by their own tokens.
        const t2 = fieldText(el);
        if (mm && /(^|[^a-z])mm([^a-z]|$)|месяц|(^|[^а-яё])мм([^а-яё]|$)|month/.test(t2)) setVal(el, mm);
        else if (yy && /(^|[^a-z])yy(yy)?([^a-z]|$)|год|(^|[^а-яё])гг(гг)?([^а-яё]|$)|year/.test(t2)) setVal(el, yr(el));
      }
    }
    if (expFields.length >= 2) {          // split MM / YY inputs (YooMoney-style)
      if (mm) setVal(expFields[0], mm);
      if (yy) setVal(expFields[1], yr(expFields[1]));
    } else if (expFields.length === 1) {
      setVal(expFields[0], mm && yy ? mm + "/" + yy : exp);
    }
  }
  function fillDoc(anchor, doc) { setVal(anchor, doc.number); }
  function fillOtp(anchor, code) { setVal(anchor, code); }

  function genPassword(len) {
    len = len || 18;
    const U = "ABCDEFGHJKLMNPQRSTUVWXYZ", L = "abcdefghijkmnopqrstuvwxyz", D = "23456789", S = "!@#$%^&*-_=+";
    const all = U + L + D + S, a = new Uint32Array(len);
    crypto.getRandomValues(a);
    let out = "";
    for (let i = 0; i < len; i++) out += all[a[i] % all.length];
    return out;
  }

  // ---------- shadow-dom UI ----------
  let uiHost = null, uiRoot = null;
  function root() {
    if (uiRoot) return uiRoot;
    uiHost = document.createElement("div");
    uiHost.style.cssText = "all:initial; position:fixed; z-index:2147483647; top:0; left:0;";
    uiRoot = uiHost.attachShadow({ mode: "closed" });
    const style = document.createElement("style");
    style.textContent = `
      .ipwbadge{position:fixed;display:flex;align-items:center;justify-content:center;width:20px;height:20px;
        border-radius:6px;background:${INK}ee;border:1px solid ${BRASS}66;cursor:pointer;
        box-shadow:0 2px 6px rgba(0,0,0,.35);transition:background .12s,border-color .12s}
      .ipwbadge:hover{background:${INK};border-color:${BRASS}}
      .card{position:fixed;top:16px;right:16px;width:320px;background:${INK};color:${PAPER};
        border:1px solid ${BRASS}55;border-radius:14px;box-shadow:0 12px 40px rgba(0,0,0,.45);
        font:13px/1.45 'Segoe UI',system-ui,sans-serif;padding:14px 16px;}
      .hd{display:flex;align-items:center;gap:8px;font-weight:600;font-size:13.5px;margin-bottom:8px;}
      .key{color:${BRASS};font-size:15px}
      .sub{color:${PAPER}aa;font-size:12px;margin-bottom:10px;word-break:break-all}
      label.opt{display:flex;gap:8px;align-items:flex-start;margin:6px 0;cursor:pointer;font-size:12.5px}
      label.opt input{accent-color:${BRASS};margin-top:2px}
      .row{display:flex;gap:8px;margin-top:12px}
      button{all:initial;font:600 12.5px 'Segoe UI',system-ui,sans-serif;border-radius:8px;
        padding:7px 14px;cursor:pointer;text-align:center}
      .pri{background:${BRASS};color:${INK}} .pri:hover{background:#CDB06C}
      .sec{background:transparent;color:${PAPER}cc;border:1px solid ${PAPER}33} .sec:hover{color:${PAPER}}
      .pick{position:fixed;background:${INK};border:1px solid ${BRASS}55;border-radius:10px;
        box-shadow:0 10px 30px rgba(0,0,0,.4);font:12.5px 'Segoe UI',system-ui,sans-serif;
        min-width:210px;max-width:320px;overflow:hidden;padding-bottom:2px}
      .mh{display:flex;align-items:center;gap:6px;padding:8px 12px 7px;color:${BRASS};font-weight:700;
        font-size:10.5px;letter-spacing:.5px;text-transform:uppercase;border-bottom:1px solid ${BRASS}22}
      .pick .it{padding:8px 12px;cursor:pointer;color:${PAPER}}
      .pick .it:hover{background:${BRASS}22}
      .pick .it.act{color:${BRASS}}
      .pick .it.info{cursor:default;color:${PAPER}77}
      .pick .it.info:hover{background:transparent}
      .pick .u{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
      .pick .t{color:${PAPER}77;font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
      .toast{position:fixed;top:16px;right:16px;background:${INK};color:${PAPER};border:1px solid ${BRASS}55;
        border-radius:10px;padding:10px 14px;font:600 12.5px 'Segoe UI',system-ui,sans-serif}
      .ok{color:${BRASS}}
      .uform{padding:10px 12px 12px;display:flex;flex-direction:column;gap:8px;min-width:230px}
      .uform .ut{font-weight:600}
      .uform input{all:initial;font:13px 'Segoe UI',system-ui,sans-serif;color:${PAPER};background:${INK};
        border:1px solid ${BRASS}55;border-radius:8px;padding:8px 10px;-webkit-text-security:disc}
      .uform input:focus{border-color:${BRASS}}
      .uform .ue{color:#E5484D;font-size:12px;display:none}`;
    uiRoot.appendChild(style);
    document.documentElement.appendChild(uiHost);
    return uiRoot;
  }
  function clearUi(cls) { if (uiRoot) [...uiRoot.querySelectorAll("." + cls)].forEach((n) => n.remove()); }

  function toast(text) {
    const t = document.createElement("div");
    t.className = "toast";
    t.innerHTML = `<span class="key">⚿</span> <span class="ok">${esc(text)}</span>`;
    root().appendChild(t);
    setTimeout(() => t.remove(), 2600);
  }

  // ---------- state ----------
  let items = [];
  let unlocked = false;
  let queried = false;
  let filledOnce = false;
  let lastTyped = { u: "", p: "" };
  let lastStashed = "";
  let lockBubbleShown = false;
  let listCache = null;
  const tracked = new Map();   // field -> { badge, kind }
  let menuField = null;

  async function query() {
    const r = await send({ cmd: "credentials", url: location.href });
    if (r && r.ok) { unlocked = !!r.unlocked; items = r.items || []; queried = true; }
    return r;
  }
  async function ensureList() {
    if (listCache) return listCache;
    const r = await send({ cmd: "list" });
    listCache = (r && r.ok) ? r : { cards: [], docs: [] };
    return listCache;
  }

  // ---------- in-field badge ----------
  function ensureBadge(field, kind) {
    const rec = tracked.get(field);
    if (rec) { rec.kind = kind; return; }
    const b = document.createElement("div");
    b.className = "ipwbadge";
    b.innerHTML = '<img src="' + TRAY_IMG + '" style="width:15px;height:15px;display:block">';
    b.addEventListener("mousedown", (e) => {
      e.preventDefault(); e.stopPropagation();
      const cur = tracked.get(field);
      openMenu(field, cur ? cur.kind : kind);
    });
    root().appendChild(b);
    tracked.set(field, { badge: b, kind });
  }
  function repositionAll() {
    for (const [field, rec] of tracked) {
      if (!field.isConnected) { rec.badge.remove(); tracked.delete(field); continue; }
      if (!visible(field)) { rec.badge.style.display = "none"; continue; }
      const r = field.getBoundingClientRect();
      const size = Math.min(20, Math.max(14, Math.round(r.height - 8)));
      rec.badge.style.display = "flex";
      rec.badge.style.width = rec.badge.style.height = size + "px";
      rec.badge.style.left = Math.round(r.right - size - 6) + "px";
      rec.badge.style.top = Math.round(r.top + (r.height - size) / 2) + "px";
    }
  }

  function positionMenu(p, field) {
    const r = field.getBoundingClientRect();
    const vw = document.documentElement.clientWidth;
    const w = p.offsetWidth || 260;
    let left = Math.round(r.right - w);
    left = Math.max(8, Math.min(left, vw - w - 8));
    p.style.left = left + "px";
    p.style.top = Math.round(r.bottom - 2) + "px";
  }

  function closeMenu() {
    clearUi("pick");
    menuField = null;
    document.removeEventListener("mousedown", onOutside, true);
    window.removeEventListener("scroll", onScrollClose, true);
  }
  function onOutside(e) {
    if (e.composedPath && e.composedPath().includes(uiHost)) return;   // click inside our UI
    closeMenu();
  }
  function onScrollClose() { closeMenu(); }

  function armUnlockRefresh() {
    window.addEventListener("focus", async function h() {
      const r = await query();
      if (r && r.unlocked) { window.removeEventListener("focus", h); listCache = null; filledOnce = false; autofill(); }
    });
  }

  // Inline master-password unlock (Kaspersky-style): the password goes bg -> native host ->
  // the app's CurrentUserOnly pipe; the APP WINDOW NEVER OPENS, nothing is stored or logged.
  function mkUnlockForm(onDone) {
    const w = document.createElement("div");
    w.className = "uform";
    w.innerHTML = `<div class="ut">Сейф заблокирован</div>
      <input type="password" placeholder="Мастер-пароль" autocomplete="off">
      <div class="ue"></div>
      <div class="row" style="margin-top:2px"><button class="pri">Разблокировать</button></div>`;
    const inp = w.querySelector("input"), err = w.querySelector(".ue");
    const showErr = (m) => { err.textContent = m; err.style.display = "block"; };
    async function submit() {
      const pw = inp.value;
      if (!pw) { inp.focus(); return; }
      const r = await send({ cmd: "unlock", password: pw });
      inp.value = "";
      if (r && r.ok) { unlocked = true; queried = false; onDone && onDone(); return; }
      if (r && r.error === "wrong_password")
        showErr(r.attemptsLeft > 0 ? `Неверный пароль. Осталось попыток: ${r.attemptsLeft}` : "Неверный пароль. Следующая попытка заблокирует вход.");
      else if (r && r.error === "locked_out") showErr(`Слишком много попыток. Подождите ${r.wait || ""}`);
      else if (r && r.error === "no_vault") showErr("Сейф ещё не создан — откройте приложение.");
      else showErr("Нет связи с приложением.");
      inp.focus();
    }
    w.querySelector(".pri").addEventListener("click", submit);
    inp.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") { e.preventDefault(); submit(); } });
    setTimeout(() => { try { inp.focus(); } catch (e) { /* ignore */ } }, 30);
    return w;
  }

  async function openMenu(field, kind) {
    if (menuField === field) { closeMenu(); return; }   // toggle
    closeMenu();
    const qr = await query();

    const group = (kind === "login" || kind === "password") ? "account"
                : kind.startsWith("card") ? "card" : kind;   // account | card | otp | doc

    const p = document.createElement("div");
    p.className = "pick";
    const head = document.createElement("div");
    head.className = "mh";
    head.innerHTML = `${KEY_SVG}<span>IPasswrd</span>`;
    p.appendChild(head);

    const add = (html, onClick, cls) => {
      const d = document.createElement("div");
      d.className = "it" + (cls ? " " + cls : "") + (onClick ? "" : " info");
      d.innerHTML = html;
      if (onClick) d.addEventListener("mousedown", (e) => { e.preventDefault(); onClick(); closeMenu(); });
      p.appendChild(d);
    };
    const openApp = () => add(`<div class="t">Открыть IPasswrd…</div>`, () => send({ cmd: "focus" }), "act");

    if (!qr || !qr.ok) {
      add(`<div class="u">Нет связи с приложением</div><div class="t">Нажмите, чтобы попробовать снова</div>`,
          () => { setTimeout(() => openMenu(field, kind), 150); }, "act");
    } else if (!unlocked) {
      p.appendChild(mkUnlockForm(() => { closeMenu(); filledOnce = false; autofill(); openMenu(field, kind); }));
    } else if (group === "account") {
      if (items.length) {
        const hostOf = (u) => { try { return new URL(u && u.includes("://") ? u : "https://" + (u || "")).hostname.replace(/^www\./, ""); } catch (e) { return u || ""; } };
        const multi = new Set(items.map((it) => hostOf(it.url))).size > 1;
        for (const it of items)
          add(`<div class="u">${esc(it.username || "(без логина)")}</div><div class="t">${esc(multi ? hostOf(it.url) : (it.title || ""))}</div>`, () => fill(pairFor(field), it));
      }
      else add(`<div class="t">Нет сохранённых логинов для этого сайта</div>`, null);
      if (kind === "password")
        add(`<div class="u">⚙ Сгенерировать пароль</div>`, () => { const pw = genPassword(); const pr = pairFor(field); if (pr.pw) setVal(pr.pw, pw); else setVal(field, pw); lastTyped.p = pw; }, "act");
      openApp();
    } else if (group === "card") {
      const L = await ensureList();
      const cards = (L && L.cards) || [];
      if (cards.length) for (const crd of cards)
        add(`<div class="u">${esc(crd.title || "Карта")}</div><div class="t">•••• ${esc((crd.number || "").replace(/\s/g, "").slice(-4))}</div>`, () => fillCard(field, crd));
      else add(`<div class="t">Нет карт в сейфе</div>`, null);
      openApp();
    } else if (group === "doc") {
      const L = await ensureList();
      const docs = (L && L.docs) || [];
      if (docs.length) for (const dc of docs)
        add(`<div class="u">${esc(dc.title || "Документ")}</div><div class="t">${esc(dc.number || "")}</div>`, () => fillDoc(field, dc));
      else add(`<div class="t">Нет документов в сейфе</div>`, null);
      openApp();
    } else if (group === "otp") {
      const withCode = items.filter((it) => it.totp);
      if (withCode.length) for (const it of withCode)
        add(`<div class="u">${esc(it.totp)}</div><div class="t">${esc(it.title || it.username || "")}</div>`, () => fillOtp(field, it.totp));
      else add(`<div class="t">Нет кодов проверки для этого сайта</div>`, null);
      openApp();
    }

    root().appendChild(p);
    positionMenu(p, field);
    menuField = field;
    setTimeout(() => {
      document.addEventListener("mousedown", onOutside, true);
      window.addEventListener("scroll", onScrollClose, true);
    }, 0);
  }

  // ---------- autofill on load (silent, single match) ----------
  async function autofill() {
    const pairs = findPairs();
    if (!pairs.length) return;
    if (!queried) await query();
    if (!unlocked || !items.length || filledOnce) return;
    if (items.length === 1) {
      const p = pairs[0];
      const empty = (!p.user || !p.user.value) && !p.pw.value;
      if (empty) { fill(p, items[0]); filledOnce = true; }
    }
  }

  // Card forms: the badge lives ONLY in the card-number field — one click fills the whole
  // form (number, expiry, CVC, holder); the small boxes stay clean.
  const badgeless = (k) => k === "card-cvc" || k === "card-holder" || k.startsWith("card-exp");

  function attachFieldHandlers() {
    const map = candidateFields();
    for (const [el, kind] of map) {
      if (badgeless(kind)) { const r = tracked.get(el); if (r) { r.badge.remove(); tracked.delete(el); } }
      else ensureBadge(el, kind);
      if (!el.__ipw) {
        el.__ipw = true;
        el.addEventListener("focus", () => onFieldFocus(el, tracked.get(el) ? tracked.get(el).kind : kind));
        el.addEventListener("input", () => { const pr = pairFor(el); lastTyped = { u: pr.user ? pr.user.value : lastTyped.u, p: pr.pw ? pr.pw.value : lastTyped.p }; });
        el.addEventListener("keydown", (e) => { if (e.key === "Enter") setTimeout(captureSubmit, 0); });
      }
    }
    repositionAll();
  }

  async function onFieldFocus(field, kind) {
    await query();
    if (!unlocked) { if (!lockBubbleShown) { lockBubbleShown = true; showLockedBubble(); } return; }
    if ((kind === "login" || kind === "password") && items.length) openMenu(field, kind);   // Kaspersky-style: open when matches exist
  }

  function showLockedBubble() {
    const c = document.createElement("div");
    c.className = "card";
    c.innerHTML = `<div class="hd"><span class="key">⚿</span> IPasswrd</div>`;
    c.appendChild(mkUnlockForm(() => { c.remove(); filledOnce = false; autofill(); }));
    const later = document.createElement("div");
    later.className = "sub";
    later.style.cssText = "margin:6px 0 0;cursor:pointer;text-align:right";
    later.textContent = "Позже";
    later.addEventListener("click", () => c.remove());
    c.appendChild(later);
    root().appendChild(c);
  }

  // ---------- capture at submit ----------
  function captureSubmit() {
    const u = (lastTyped.u || "").trim(), p = lastTyped.p || "";
    if (!p) return;
    const combo = u + " " + p;
    if (combo === lastStashed) return;
    if (unlocked && items.some((it) => (it.username || "").trim().toLowerCase() === u.toLowerCase() && it.password === p)) return;
    lastStashed = combo;
    send({ cmd: "_stash", data: { url: location.href, username: u, password: p } });
  }

  document.addEventListener("submit", () => setTimeout(captureSubmit, 0), true);
  document.addEventListener("click", (e) => {
    const b = e.target && e.target.closest && e.target.closest("button, input[type=submit], [role=button]");
    if (b && lastTyped.p) setTimeout(captureSubmit, 0);
  }, true);
  window.addEventListener("beforeunload", captureSubmit);

  // ---------- save bubble on the page after login ----------
  async function maybeOfferSave() {
    if (!TOP) return;
    const r = await send({ cmd: "_takePending" });
    const pending = r && r.pending;
    if (!pending || !pending.password) return;

    const q = await send({ cmd: "credentials", url: pending.url });
    let mode = "new";
    if (q && q.ok && q.unlocked) {
      const u = (pending.username || "").trim().toLowerCase();
      const same = (q.items || []).find((it) => (it.username || "").trim().toLowerCase() === u);
      if (same && same.password === pending.password) return;
      if (same) mode = "update";
    }

    const host = new URL(pending.url).hostname;
    const base = baseDomain(host);
    const exactShown = host.replace(/^www\./, "");
    const askScope = base !== exactShown;

    const c = document.createElement("div");
    c.className = "card";
    const title = mode === "update" ? "Обновить пароль?" : "Сохранить пароль?";
    const btn = mode === "update" ? "Обновить" : "Сохранить";
    c.innerHTML = `
      <div class="hd"><span class="key">⚿</span> IPasswrd · ${title}</div>
      <div class="sub"></div>
      ${askScope ? `
      <label class="opt"><input type="radio" name="sc" value="base" checked>
        <span>Сохранить для <b>${esc(base)}</b> — подойдёт для всех адресов сайта</span></label>
      <label class="opt"><input type="radio" name="sc" value="exact">
        <span>Только точный адрес <b>${esc(exactShown)}</b></span></label>` : ""}
      <div class="row"><button class="pri">${btn}</button><button class="sec">Не сейчас</button></div>`;
    c.querySelector(".sub").textContent = (pending.username ? pending.username + " · " : "") + exactShown;
    c.querySelector(".sec").addEventListener("click", () => c.remove());
    c.querySelector(".pri").addEventListener("click", async () => {
      const scope = askScope ? c.querySelector("input[name=sc]:checked").value : "base";
      c.querySelector(".pri").textContent = "…";
      const res = await trySave(pending, scope);
      c.remove();
      if (res && res.ok) toast(res.action === "updated" ? "Пароль обновлён ✓" : "Сохранено в IPasswrd ✓");
    });
    root().appendChild(c);
    setTimeout(() => { if (c.isConnected) c.remove(); }, 45000);
  }

  async function trySave(pending, scope) {
    const msg = { cmd: "save", url: pending.url, username: pending.username, password: pending.password, scope };
    let res = await send(msg);
    if (res && res.error === "locked") {
      await send({ cmd: "focus" });
      for (let i = 0; i < 30; i++) {
        await new Promise((r) => setTimeout(r, 2000));
        const st = await send({ cmd: "status" });
        if (st && st.unlocked) { res = await send(msg); break; }
      }
    }
    return res;
  }

  // ---------- boot ----------
  function boot() { attachFieldHandlers(); autofill(); }

  let debounce = null;
  new MutationObserver(() => {
    clearTimeout(debounce);
    debounce = setTimeout(() => { attachFieldHandlers(); autofill(); }, 400);
  }).observe(document.documentElement, { childList: true, subtree: true });

  window.addEventListener("scroll", repositionAll, true);
  window.addEventListener("resize", repositionAll, true);
  setInterval(repositionAll, 250);

  boot();
  maybeOfferSave();
})();
