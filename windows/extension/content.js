// IPasswrd content script: in-field key badge + dropdown, autofill saved logins,
// offer to save new ones. Runs in an isolated world; page scripts cannot read its state.

(() => {
  "use strict";
  if (window.__ipasswrdLoaded) return;
  window.__ipasswrdLoaded = true;

  const BRASS = "#BC9F5C", INK = "#17191D", PAPER = "#F4F1E8";
  const TOP = window === window.top;
  const KEY_SVG = `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="${BRASS}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 2l-2 2m-7.6 7.6a5.5 5.5 0 1 1-7.78 7.78 5.5 5.5 0 0 1 7.78-7.78zm0 0L19 4m-3.5 3.5L18 10"/></svg>`;
  const TRAY_IMG = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACgAAAAoCAYAAACM/rhtAAAKOklEQVR42u1Ya3RU1RX+9j3nziOTyRNCAggEtKyCoAiKYJEElYJ0qSybKZCgPASKSl1alm1t5TIiFhVh2a5q04KAJRYnPlaXyKOAE3zhUrCIPFRE3pBAJMm8Z+69Z/dHJpAmVFDxsbr6/Zq55957vvvtfb69zwH+j/8xGIahnfMmZhbMTN/LL2hN7LsguabyZ72YcXre1nISABARR0MnZzY2HswlIv6mSDKDDMPQDMPQgkFDAsCmZROndeuS++nG5RUPA0DQGC5lG7X0SKhuXoa3w/2x8IlbQ6Gj4wCcYmYQEV8IUjVzh4salCgivwL86Xca4ECZeC2mmmyL68H8ebucA4CmU0duYmaONNVGmJkjjcefaB4Pyq/JjThQJlpfeKNqQm5N1aRL3qia3HNr5XS95XrlgrJstA1rmqQGQEZCdfMyszrdHwuf2GTDGuf1dj4FgL+qgoZhaH6/XwHA+mcrCpykjWfmm5VCP2bOJSJbF3TQttWz6H54QWnpZqv1M9SGLANANHRypqliq3JyujcwM31VcoFAmfD5qm3DGC5LLu52L4Fmu516gWUrpEwbSikAgBQavB4nPm+KvVLkSf20764+Fvn9DKD9AmhN6OuQCwaHy9LSzdbaJeW9MxxiudutXx2Nm7As2yIQMaBRq/iByczLdTtPNUZnXzfpuUVr/zDKESnyWu1yK71yBQD1dcltWDrheodDPK/rIq8xnLAIJIhItg0dQASCjMZSCqCpRHgCWJdsf99/ruh0UtcAKEGasDrXKq2uLtN8vmp70zPlUxwOWWkrlqZp26SROMezrEuNTMuuVcSjpSY6k1LUTkEOBAQR2QCss1WY9Fj7CQxDA/nZh2r7teUVD7ld+oOxhMlKsToXuRYNLVuBiAo1xpa8LJfr88Z4UrYj5/PZjYcO5bk7em9RrC4jxbomxV6VNFcT0V5m1toqGQiUCfL57YBR5nitp2tpZoajIhRO2gzWiEg7zwrGUmhkKT6i66JrUyR5SBPw0ZlJAsLn89mh+qNjXZmeJ3Wn96LWhcZMNkVTicSCzJzCh5kDgsinAHBLvr1YObYoz+293uPWhzWFE1ZLrp2vhxORLQVJ07QHWraSSfDBm6etqpOtQxdpOjHa6XK8JB1uREMnjzJhPTHiYB7mycrqrzu985oajjqIusxhZjl3bglKSzdbq5+aMNDjktVOXRQ3hBKWRiT5yywvgp2f7Zb1DbEFN0yper912lBLrT1x4oQnw6l2ZWZ36BaPNmxye9zlRN669AfIWLT+UZfTeZ9lWmgINVxWWNhrBwC8G5gyzla8hAieZNKy8CWUIyKAoKTGWjgSrfrxtBcrgoYhT/bdzWVlAUVETBwMSiottaJNtTdleHP+EY+GToVj0b6dOhXXMrO+bds2DBo0yASASOPxt9ze/KEa4n+Fxgv+aEyc2b0oc3YiacGyldLOM9+ayWlsWZayrZTWo1ev168t899RW3vgeFHR5dFWNQNym9dLaZX6gJxs2/Y7nToV13IwKInITI/p1dU+lUgk1niy9aEHPts7+e5f/Hry9g+2S1sRc3M8m42Xm82LQFDMYAaIAE0j8On/ZFuWKaSU4sEH7sNVYyZdkYzZezJdBQeZGwYBOU0tnnwmHFqaMtLZU3LG2/buXav5fNUmc8DeuGEdpk6fhcbGBpmbk2VLZmFaCk6HQNomAAAp00ZmhoQUGpIpG/GkDadDsC4FmJXwZOalFi2cH73+hhHuSFNDhq47CEQKjQByzigtBw4c2FyUGbvBSdKEHBIO13YiKqyrrKzUiWaYwI3JY2/9snv5BJ/v5Vc386ghXbS7xw/lBc/sENleHeWje2HO0//CLaXd8NGBJuhSQ/nonli/5Rj2Hw1jUJ8OGNyvA69at5/2HQmjrCT/mZHjH3uhd/8rnzp+ZF9FbqZntzPDI5w4eYyoKN66xEoAipmpvv6jGg7joCerQ/d4tKFq//6dFcXFl9YyB/LeXrV29m8Wv3PX6o17szJcDr6ptKfWMdeBi7tlYdW6fWgIpXDweBh9eubgn1uOoKhDBoJba7Hk5Y9RkOfCm9tP8O9nXUEDemcl+v9onDnrnpkey4w9Gos2pNyZ+W+7c3Mb25bb/2i3Wmymsb5upMfrXi0dbh2w6hY/sTi4/pXnBjM5i9/98DgSKcVTbulN5aOLEY6lkJmhY/6SHVj75mE8cMfl8N3QA9MeehNejwOfHg7jxmEX4S7fD3nCr9apjp26NqyZnzvMOfSVwQCWx6P1dt3Jz7oWFw+uZWYJwG5LDgBkIFAmiMjetPw2354N987Jv3jswp4Dxo6Z/8jC/o8vfnpcMmVB2Wx3zHNrE39STL6RPRBLWiAiSEG45rICvLfrJK7u1wHxpA1NI0ihweUU9tHaJnp/5wHz/tn3OIcMH1MTLvpBSEUaHyHN2iOEVlRYcMnS+vr68QAiZyPXEuJ0y6tsl9ubvX39vX/a9+Fak+sP9bvxms4pIaRemO8Wg/rmo7hzJqJxC832BSgGTEuBCEiZChoBkZjJHXPdasatvcWfXzqE/P53OseMGXMKsOZEGo+Olxk5ocNH9g3Jzy680pOVW+VU4R5EtCPdMHN7D2+Djcun9tdF6gOXQ2cGt/SESKZsJFPNCgGAUowsj47Vrx/B4qqdWDHvWlxU6LEfW75T9OiSjYpRXdaE3SWLho2elPnJJ1u29u499Cgzy3SzwQCwtbJSHzRjhvlFfnlawa2V0/VBM/5iskqUuz0unGpK2ESQLdamEZ0ml+4+YNmMwo4ZGNingN0uaTND3lPeJ6oU/3bArSueBFYAmJy2K0MjIqtVO0ctPvtFOO384WMfN1ssUZ5SzEIQhNZMSmgEovZlKpGy0ac42374rgFUkOeW8aT1umkmB5dMfPZJw4AWCAQEMwvDMLTmXdyZLp2I1Plsac8YdQkAP5hAu4RGdLoEfHGLZDmdUpqWHQvHkv4Rt618HAAHjeGy1L/ZAnxnq798tt/nVLCmZrMCACHVC6FoKiSEJpnB/4WYYrDK9rqkadlv2cq8asRtKx9jbu5AmsldGJwm6PdDBQJlonRi1RFbqQ0ZLgkQ2nbPrJgth0NqbqeuRWKphcF9h0qum7RqVzA4XBKBKb1dvFBoCTEZhkF9sFsws9q0bOIxTdNsMBQDDGIGQxGRzPG6ZCJp7U8lzVkjJle9ygyaC0OjUr+FbwBnTbKNyyomdczLWNYYSoAZEEKD0yEQT5gJMFfGk4mHRk2rPpXONfts/nXBFGQGVS8qc+XlOVeCmFMCU8Snh1eeRNcuui4mMHO2bau6eII3JBWvGHn73/a07ENKfdUWvg1sWFoxfdfqn/N7L0zl9UsqLm2t8M7AnZltN0itj8e+aTTnoMa3J5JWoikc/93Iqc/t4sOGVoMardS/2brU91Sk5SisBiXK5/Pb3+Z5IVVWTtd76tGJtqneHjXj7x+1PqMBQGk7ZHwfEGhzRPa9QdAYLvl8Dq+/A/wbaD43dtKWffIAAAAASUVORK5CYII=";

  // ---------- registrable domain (mirrors Dedup.cs — proper public-suffix rule) ----------
  // A "public suffix" is a level under which independent parties register names, so two names
  // below it are DIFFERENT sites. The set MUST include the private multi-tenant registries
  // (github.io, *.web.app, herokuapp.com, …); otherwise every tenant of such a host would
  // collapse to one "site" and IPasswrd could hand one tenant's saved password to another.
  // Longest matching suffix + one more label = the registrable domain. Kept in sync with
  // IPasswrd.Core.Dedup.PublicSuffixes; for full coverage swap in the official Public Suffix List.
  const PUBLIC_SUFFIXES = new Set([
    "co.uk","org.uk","gov.uk","ac.uk","me.uk","ltd.uk","plc.uk","net.uk","sch.uk","nhs.uk","police.uk","com.au",
    "net.au","org.au","edu.au","gov.au","id.au","asn.au","co.jp","or.jp","ne.jp","ac.jp","go.jp","gr.jp",
    "ed.jp","lg.jp","ad.jp","co.kr","or.kr","ne.kr","re.kr","pe.kr","go.kr","ac.kr","hs.kr","ms.kr","com.br",
    "net.br","org.br","gov.br","edu.br","art.br","blog.br","com.cn","net.cn","org.cn","gov.cn","edu.cn","ac.cn",
    "com.tw","net.tw","org.tw","idv.tw","gov.tw","edu.tw","com.hk","net.hk","org.hk","edu.hk","gov.hk","idv.hk",
    "com.sg","net.sg","org.sg","edu.sg","gov.sg","per.sg","com.my","net.my","org.my","gov.my","edu.my","co.in",
    "net.in","org.in","gen.in","firm.in","ind.in","gov.in","ac.in","edu.in","com.tr","net.tr","org.tr","gov.tr",
    "edu.tr","bel.tr","com.ua","net.ua","org.ua","in.ua","kiev.ua","com.ru","net.ru","org.ru","msk.ru","spb.ru",
    "com.mx","com.ar","com.co","net.co","nom.co","com.pe","com.ve","com.ec","com.uy","com.py","com.bo","com.cl",
    "co.il","org.il","net.il","ac.il","gov.il","muni.il","co.za","org.za","net.za","web.za","gov.za","ac.za",
    "co.nz","net.nz","org.nz","govt.nz","ac.nz","geek.nz","school.nz","co.th","in.th","ac.th","go.th","or.th",
    "net.th","com.vn","net.vn","org.vn","gov.vn","edu.vn","com.ph","net.ph","org.ph","gov.ph","com.pl","net.pl",
    "org.pl","gov.pl","waw.pl","edu.pl","com.pt","com.es","org.es","com.eg","com.sa","com.ng","com.gh","com.kw",
    "com.qa","com.bh","com.pk","com.bd","co.id","or.id","web.id","ac.id","go.id","my.id","biz.id","co.ke",
    "co.tz","co.ug","co.zw","co.mz","github.io","githubusercontent.com","gitlab.io","pages.dev","workers.dev",
    "r2.dev","web.app","firebaseapp.com","appspot.com","cloudfunctions.net","run.app","vercel.app","now.sh",
    "netlify.app","netlify.com","onrender.com","render.com","herokuapp.com","herokudns.com","fly.dev",
    "railway.app","up.railway.app","azurewebsites.net","azurestaticapps.net","cloudapp.net",
    "cloudapp.azure.com","trafficmanager.net","blob.core.windows.net","web.core.windows.net","azureedge.net",
    "amazonaws.com","s3.amazonaws.com","s3-website.amazonaws.com","elasticbeanstalk.com","cloudfront.net",
    "amplifyapp.com","awsapprunner.com","execute-api.amazonaws.com","blogspot.com","wordpress.com","tumblr.com",
    "weebly.com","wixsite.com","editorx.io","myshopify.com","squarespace.com","webflow.io","framer.app",
    "framer.website","framer.media","glitch.me","repl.co","replit.dev","replit.app","surge.sh","bubbleapps.io",
    "softr.app","translate.goog","googleusercontent.com","readthedocs.io","gitbook.io","notion.site",
    "super.site","carrd.co","substack.com","sharepoint.com","atlassian.net","zendesk.com","freshdesk.com",
    "myjetbrains.com","statuspage.io","pythonanywhere.com","codeberg.page","stackblitz.io","vercel.sh",
    "deno.dev",
  ]);
  function isPublicSuffix(d) {
    d = String(d || "").toLowerCase().replace(/\.$/, "");
    if (!d) return true;
    if (d.indexOf(".") < 0) return true;                 // bare TLD ("com", "io", "ru")
    return PUBLIC_SUFFIXES.has(d);
  }

  function baseDomain(host) {
    host = (host || "").toLowerCase().replace(/^www\./, "").replace(/\.$/, "");
    if (/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return host;
    const l = host.split(".").filter(Boolean);
    if (l.length <= 1) return host;
    let suffixLabels = 1;                                 // default: last label is the TLD
    for (let i = l.length - 1; i >= 1; i--)
      if (PUBLIC_SUFFIXES.has(l.slice(i).join("."))) suffixLabels = l.length - i;
    const take = suffixLabels + 1;
    if (take > l.length) return host;                    // host IS a public suffix → keep as-is
    return l.slice(l.length - take).join(".");
  }

  const send = (msg) => new Promise((res) => {
    try { chrome.runtime.sendMessage(msg, (r) => res(chrome.runtime.lastError ? { ok: false, error: "bg_unreachable" } : (r || { ok: false }))); }
    catch (e) { res({ ok: false, error: String(e) }); }
  });

  // ---------- WebAuthn relay (MAIN-world inject.js ⇄ vault) ----------
  // Crypto now lives in the app; we shuttle passkey list/create/sign between the page and the vault.
  //
  // TRUST BOUNDARY: the rpId→origin rule is enforced HERE, in the isolated world, because a hostile
  // page can post an "ipw->cs" message DIRECTLY (bypassing inject.js's check, which runs in the
  // page-tamperable MAIN world). Without this, evil.com could ask the vault for bank.com's passkey.
  function rpIdAllowed(rpId, host) {
    rpId = String(rpId || "").toLowerCase().replace(/\.$/, "");
    host = String(host || "").toLowerCase().replace(/\.$/, "");
    if (!rpId || !host) return false;
    // WebAuthn registrable-domain-suffix rule: rpId must be the page host or a registrable parent
    // of it, and NEVER a bare public suffix (github.io, co.uk, com) — else a page could claim an
    // rpId that spans every tenant of a shared suffix.
    if (isPublicSuffix(rpId)) return false;
    if (rpId === host) return true;
    return host.endsWith("." + rpId);
  }
  window.addEventListener("message", async (e) => {
    const d = e.data;
    if (e.source !== window || !d || d.dir !== "ipw->cs") return;
    let res;
    try {
      const rpId = (d.data && d.data.rpId) || "";
      if (!rpIdAllowed(rpId, location.hostname)) {
        res = { ok: false, error: "rpId_forbidden" };            // cross-origin request → refused at the boundary
      } else if (d.cmd === "passkeyList") {
        res = await send({ cmd: "passkeyList", rpId });
      } else if (d.cmd === "passkeyCreate") {
        res = await send({ cmd: "passkeyCreate", rpId, userHandle: (d.data && d.data.userHandle) || "", userName: (d.data && d.data.userName) || "" });
      } else if (d.cmd === "passkeySign") {
        res = await send({ cmd: "passkeySign", rpId, credId: (d.data && d.data.credId) || "", data: (d.data && d.data.data) || "" });
      } else {
        res = { ok: false, error: "unknown_cmd" };
      }
    } catch (err) { res = { ok: false, error: String(err) }; }
    window.postMessage({ dir: "ipw->page", id: d.id, res }, location.origin);
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

  // -> "password" | "login" | "card-*" | "otp" | "doc" | "id-*" | null
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
    if (ac.includes("one-time-code") || /one[- ]?time|\botp\b|\btotp\b|2fa|two[- ]?factor|verification code|authenticator|аутентификатор|проверочн(ый|ого) код|код подтвержден|код из (смс|sms)|одноразов/.test(t)) return "otp";

    if (type === "password") {
      if (/\bcvc\b|\bcvv\b/.test(t)) return "card-cvc";
      return "password";
    }
    // Личные данные. Идут ДО логина: поле "Имя получателя" в форме доставки — не логин,
    // хотя по тексту похоже. Почта и телефон, наоборот, остаются логином: на странице входа
    // это чаще именно он, а внутри заполнения личных данных они находятся своими правилами.
    // Словарь нарочно шире очевидного: в самописных формах подпись часто не привязана к полю
    // (<label> без for), и всё, что остаётся, — name="surname" или name="addr". Проверено на живой
    // странице: без этих слов значок не появлялся на «Фамилии» и «Улице».
    if (ac.includes("family-name") || ac.includes("given-name") || ac.includes("additional-name") ||
        ac === "name" || /(^|[^а-яё])фамили|(^|[^а-яё])отчеств|\bфио\b|получател|recipient|full ?name|first ?name|last ?name|surname|patronymic|middlename|lastname|firstname|\bfname\b|\blname\b|\bmname\b/.test(t)) return "id-name";
    // Без голого postal: «postal address» — это адрес, а не индекс.
    if (ac.includes("postal-code") || /индекс|postal ?code|post ?code|zip|postindex/.test(t)) return "id-zip";
    if (ac.includes("country") || /страна|country/.test(t)) return "id-country";
    if (ac.includes("address-level2") || /(^|[^а-яё])город|населённ|населенн|\bcity\b|town|locality/.test(t)) return "id-city";
    // «Адрес электронной почты» тоже адрес, но везти туда некуда. Отличаем по «электронн»
    // и mail, а не по «почт»: «Почтовый адрес» — это как раз куда везти.
    if ((ac.includes("street-address") || ac.includes("address-line") ||
         /улиц|(^|[^а-яё])адрес|street|address|\baddr\b|addr[1-2]?\b/.test(t)) &&
        // «адрес эл. почты» и «телефон или адрес…» — это поле ВХОДА, а не улица. Исключаем
        // электронную почту (в т.ч. сокращение «эл. почт») и телефон, оставляя «почтовый адрес».
        !/электронн|эл\.?\s?почт|e[-\s]?mail|\bmail\b|телефон|\bphone\b|\btel\b/.test(t)) return "id-street";

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
    // Ячейки кода (ряд одно-символьных инпутов) и системные one-time-code-поля не декорируем
    // бейджем — на idmsa.apple.com и подобных он рисовался в каждой клетке. Заполняет их
    // карточка «Код проверки» в углу страницы.
    const cells = new Set(otpCellGroup());
    for (const el of document.querySelectorAll("input")) {
      if (!visible(el)) continue;
      if (el.maxLength === 1 || cells.has(el)) continue;
      const k = classify(el);
      if (k === "otp" && (el.getAttribute("autocomplete") || "").toLowerCase().includes("one-time-code")) continue;
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
  // Личные данные заполняются целиком, как карта: человек выбирает, кем он представляется,
  // а не по какому полю кликнул. Поиск ограничен той же формой — на странице с формой входа
  // рядом ничего не должно перезаписаться.
  function fillIdentity(anchor, idn) {
    const scope = anchor.form || document;
    const fio = [idn.lastName, idn.firstName, idn.middleName].filter(Boolean).join(" ");

    for (const el of scope.querySelectorAll("input, textarea, select")) {
      if (!visible(el) || el.disabled || el.readOnly) continue;
      const ac = (el.getAttribute("autocomplete") || "").toLowerCase();
      const t = fieldText(el);
      const type = (el.type || "text").toLowerCase();

      if (ac.includes("family-name") || /(^|[^а-яё])фамили|last ?name|lastname|surname|\blname\b/.test(t)) { if (idn.lastName) setVal(el, idn.lastName); continue; }
      if (ac.includes("additional-name") || /отчеств|middle ?name|middlename|patronymic|\bmname\b/.test(t)) { if (idn.middleName) setVal(el, idn.middleName); continue; }
      // Собирательное поле — раньше «имени»: «Имя получателя» в русских магазинах ждёт ФИО целиком,
      // а не одно имя, и проверка на given-name перехватила бы его по слову «имя».
      if (ac === "name" || /\bфио\b|получател|recipient|full ?name/.test(t)) { if (fio) setVal(el, fio); continue; }
      if (ac.includes("given-name") || /(^|[^а-яё])имя([^а-яё]|$)|first ?name|firstname|\bfname\b/.test(t)) { if (idn.firstName) setVal(el, idn.firstName); continue; }
      if (type === "tel" || ac.includes("tel") || /телефон|phone|mobile/.test(t)) { if (idn.phone) setVal(el, idn.phone); continue; }

      // Индекс — РАНЬШЕ почты. «Почтовый индекс» содержит слово «почт», и правило для
      // электронной почты забирало его себе — в поле индекса приезжала почта. Наоборот
      // запретить «почтов» нельзя: «Почтовый ящик» — это как раз почта. Порядок решает оба
      // случая без списка исключений.
      if (ac.includes("postal-code") || /индекс|postal ?code|post ?code|zip|postindex/.test(t)) { if (idn.zip) setVal(el, idn.zip); continue; }

      // Почта — РАНЬШЕ адреса по той же причине: «Адрес электронной почты» — не адрес доставки.
      // Само слово «почт» слишком широкое: «почтовый адрес» — это куда везти.
      if (type === "email" || ac.includes("email") || /e[-\s]?mail|электронн\w* почт|почт[аеуы]([^а-яё]|$)/.test(t)) { if (idn.email) setVal(el, idn.email); continue; }

      if (ac.includes("country") || /страна|country/.test(t)) { if (idn.country) setVal(el, idn.country); continue; }
      if (ac.includes("address-level2") || /(^|[^а-яё])город|\bcity\b|town|locality/.test(t)) { if (idn.city) setVal(el, idn.city); continue; }
      if ((ac.includes("street-address") || ac.includes("address-line") || /улиц|(^|[^а-яё])адрес|street|address|\baddr\b|addr[1-2]?\b/.test(t)) &&
          !/электронн|e[-\s]?mail|\bmail\b/.test(t)) { if (idn.street) setVal(el, idn.street); continue; }
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
      .uform .ue{color:#E5484D;font-size:12px;display:none}
      .ocode{font:700 24px/1.25 Consolas,ui-monospace,monospace;letter-spacing:4px;color:${PAPER};margin:2px 0}
      .oit{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:7px 10px;
        margin:2px -6px;border-radius:8px;cursor:pointer}
      .oit:hover{background:${BRASS}22}
      .oit .c{font:700 15px Consolas,ui-monospace,monospace;letter-spacing:2px;color:${PAPER}}
      .oit .n{color:${PAPER}88;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}`;
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
  let codes = [];      // standalone authenticator codes matched to this site (live, from the app)
  let smsCodes = [];   // одноразовые коды из СМС, переданные с телефона (живут ~3 мин)
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
    if (!r || !r.ok) { try { console.log("[IPWNM]", location.hostname, `err=${r && r.error} ${r && r.detail || ""}`); } catch (e) {} }   // failures only, never credentials
    if (r && r.ok) { unlocked = !!r.unlocked; items = r.items || []; codes = r.codes || []; smsCodes = r.smsCodes || []; queried = true; }
    return r;
  }
  async function ensureList() {
    if (listCache) return listCache;
    const r = await send({ cmd: "list" });
    listCache = (r && r.ok) ? r : { cards: [], docs: [], ids: [] };
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
                : kind.startsWith("card") ? "card"
                : kind.startsWith("id-") ? "identity" : kind;   // account | card | otp | doc | identity

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
      p.appendChild(mkUnlockForm(() => { closeMenu(); filledOnce = false; autofill(); maybeOfferOtp(); openMenu(field, kind); }));
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
    } else if (group === "identity") {
      const L = await ensureList();
      const ids = (L && L.ids) || [];
      if (ids.length) for (const idn of ids) {
        const fio = [idn.lastName, idn.firstName, idn.middleName].filter(Boolean).join(" ");
        const where = [idn.city, idn.street].filter(Boolean).join(", ");
        add(`<div class="u">${esc(idn.title || fio || "Личные данные")}</div><div class="t">${esc(where || idn.phone || idn.email || "")}</div>`,
            () => fillIdentity(field, idn));
      }
      else add(`<div class="t">Нет личных данных в сейфе</div>`, null);
      openApp();
    } else if (group === "otp") {
      const withCode = items.filter((it) => it.totp).map((it) => ({ code: it.totp, name: it.username || it.title || "" }))
        .concat((codes || []).map((c) => ({ code: c.code, name: c.title || "" })));
      if (withCode.length) for (const it of withCode)
        add(`<div class="u">${esc(it.code)}</div><div class="t">${esc(it.name)}</div>`, () => fillOtp(field, it.code));
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

  // The username already committed on the page. Two-step logins (Google, etc.) show the chosen
  // account on the password step; we read it so the RIGHT password fills when a site has several logins.
  function knownUser() {
    const users = new Set(items.map((it) => (it.username || "").trim().toLowerCase()).filter(Boolean));
    if (!users.size) return "";
    for (const i of document.querySelectorAll("input")) {          // the picked email usually sits in a hidden identifier field
      const v = (i.value || "").trim().toLowerCase();
      if (v && users.has(v)) return v;
    }
    const txt = ((document.body && document.body.innerText) || "").toLowerCase();   // fallback: the selected-account chip text
    for (const u of users) if (u.length >= 5 && txt.includes(u)) return u;
    return "";
  }

  // ---------- autofill on load (silent) ----------
  async function autofill() {
    const pairs = findPairs();
    if (!pairs.length) return;
    if (!queried) await query();
    if (!unlocked || !items.length || filledOnce) return;

    const fillable = items.filter((it) => !it.related);           // never silently fill a redirect-only (menu-only) match
    let candidate = null;
    if (fillable.length === 1) candidate = fillable[0];
    else if (fillable.length > 1) {
      const ku = knownUser();                                      // several logins for this site → pick the one already chosen on the page
      if (ku) {
        const m = fillable.filter((it) => (it.username || "").trim().toLowerCase() === ku);
        if (m.length === 1) candidate = m[0];
      }
    }
    if (!candidate) return;

    const p = pairs[0];
    const empty = (!p.user || !p.user.value) && !p.pw.value;
    if (empty) { fill(p, candidate); filledOnce = true; }
  }

  // Card forms: the badge lives ONLY in the card-number field — one click fills the whole
  // form (number, expiry, CVC, holder); the small boxes stay clean.
  const badgeless = (k) => k === "card-cvc" || k === "card-holder" || k.startsWith("card-exp") ||
    k === "id-city" || k === "id-zip" || k === "id-country";

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
    c.appendChild(mkUnlockForm(() => { c.remove(); filledOnce = false; autofill(); maybeOfferOtp(); }));
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

  // ---------- 2FA: предложение заполнить код проверки ----------
  // На страницах двухэтапного входа (ячейки по одному символу или одно поле one-time-code)
  // показываем в правом верхнем углу карточку с кодом этого сайта и кнопкой «Заполнить».
  let otpOffer = null;        // показанная карточка
  let otpOfferDone = false;   // заполнено / «не сейчас» — на этой странице больше не предлагаем

  const fmtCode = (s) => String(s || "").replace(/^(\d{3})(\d{3})$/, "$1 $2");

  function otpCellGroup() {
    // Ячейка кода: maxlength=1 ЛИБО узкий инпут с ≤1 символом значения/плейсхолдера
    // (cs.money не ставит maxlength — там просто узкие поля с плейсхолдерами «1»…«6»).
    const cand = [...document.querySelectorAll("input")].filter((el) => {
      if (!visible(el)) return false;
      const t = (el.type || "text").toLowerCase();
      if (!["text", "tel", "number", "password"].includes(t)) return false;
      if (el.maxLength === 1) return true;
      const r = el.getBoundingClientRect();
      return r.width >= 18 && r.width <= 80 &&
             String(el.value || "").length <= 1 &&
             String(el.getAttribute("placeholder") || "").length <= 1;
    });
    if (cand.length < 4) return [];
    // Ряд из 4–8 одинаковых по ширине ячеек на одной строке — сигнатура поля кода.
    const rows = new Map();
    for (const el of cand) {
      const r = el.getBoundingClientRect();
      const key = Math.round(r.top / 12) + ":" + Math.round(r.width / 8);
      if (!rows.has(key)) rows.set(key, []);
      rows.get(key).push(el);
    }
    for (const group of rows.values())
      if (group.length >= 4 && group.length <= 8)
        return group.sort((a, b) => a.getBoundingClientRect().left - b.getBoundingClientRect().left);
    return [];
  }
  function otpSingleInput() {
    for (const el of document.querySelectorAll("input"))
      if (visible(el) && el.maxLength !== 1 && classify(el) === "otp") return el;
    return null;
  }
  function otpCandidates() {
    const seen = new Set(), out = [];
    const push = (code, name) => {
      if (!code) return;
      const k = name + " " + code;
      if (seen.has(k)) return;
      seen.add(k);
      out.push({ code, name });
    };
    for (const s of (smsCodes || [])) {
      const age = s.ageSec ?? 0;
      if (age > 90) continue;                     // старая СМС — почти наверняка от прошлой попытки входа
      const when = age < 15 ? "только что" : age < 60 ? age + " сек назад" : Math.round(age / 60) + " мин назад";
      push(s.code, "СМС · " + when);
    }
    for (const it of items) if (it.totp && !it.related) push(it.totp, it.username || it.title || "");
    for (const cd of (codes || [])) push(cd.code, cd.title || "");
    return out;
  }

  // ---------- СМС-коды: автоввод ----------
  // Пока открыта страница с полем кода, раз в 3 секунды спрашиваем у приложения, не пришла ли
  // НОВАЯ СМС (пересланная Быстрой командой с телефона). Пришла и вкладка на экране —
  // вписываем код сами; вкладка в фоне — показываем карточку. Коды, лежавшие в приложении
  // ЕЩЁ ДО открытия страницы, сами не вписываются — только через карточку (мало ли от какого они сайта).
  let smsWatch = null, smsWatchUntil = 0, smsSeen = new Set();
  const smsKey = (s) => s.code + "|" + (s.hint || "");

  function armSmsWatch() {
    if (smsWatch) return;
    smsSeen = new Set((smsCodes || []).map(smsKey));
    smsWatchUntil = Date.now() + 180000;
    smsWatch = setInterval(async () => {
      if (Date.now() > smsWatchUntil) { clearInterval(smsWatch); smsWatch = null; return; }
      if (!otpCellGroup().length && !otpSingleInput()) return;
      await query();
      const fresh = (smsCodes || []).filter((s) => !smsSeen.has(smsKey(s)));
      if (!fresh.length) return;
      for (const s of fresh) smsSeen.add(smsKey(s));
      if (!document.hidden && fillOtpEverywhere(fresh[0].code)) {
        toast("Код из СМС вставлен ✓");
        if (otpOffer) { otpOffer.remove(); otpOffer = null; }
        otpOfferDone = true;
        return;
      }
      otpOfferDone = false;                       // вкладка в фоне / не вписалось — предложим карточкой
      if (otpOffer) { otpOffer.remove(); otpOffer = null; }
      maybeOfferOtp();
    }, 3000);
  }

  function fillOtpEverywhere(code) {
    const digits = String(code || "").replace(/\D+/g, "") || String(code || "");
    const cells = otpCellGroup();
    if (cells.length) {
      cells.forEach((el, i) => { try { el.focus(); } catch (e) { /* ignore */ } setVal(el, digits[i] || ""); });
      const last = Math.min(digits.length, cells.length) - 1;
      if (last >= 0) { try { cells[last].focus(); } catch (e) { /* ignore */ } }
      return true;
    }
    const single = otpSingleInput();
    if (single) { setVal(single, digits); try { single.focus(); } catch (e) { /* ignore */ } return true; }
    return false;
  }

  async function maybeOfferOtp() {
    if (!TOP) return;
    if (!otpCellGroup().length && !otpSingleInput()) return;
    if (!queried) await query();
    armSmsWatch();                        // ждать свежие СМС-коды, пока поле кода на странице
    if (otpOfferDone || otpOffer) return;
    const cand = otpCandidates();         // СМС + сейф (СМС-коды есть и при заблокированном сейфе)
    if (!cand.length) {
      // Кодов нет совсем; если сейф заблокирован — предложим разблокировать
      // (ячейки кода без бейджей, другого пути к разблокировке тут нет).
      if (!unlocked && !lockBubbleShown) { lockBubbleShown = true; showLockedBubble(); }
      return;
    }

    const c = document.createElement("div");
    c.className = "card";
    c.innerHTML = `<div class="hd"><span class="key">⚿</span> IPasswrd · Код проверки</div><div class="sub"></div>`;
    const site = location.hostname.replace(/^www\./, "");
    c.querySelector(".sub").textContent = site + (cand.length === 1 && cand[0].name ? " · " + cand[0].name : "");

    const dismiss = () => { c.remove(); otpOffer = null; otpOfferDone = true; };
    const doFill = async (name) => {
      await query();   // пока карточка висела, код мог смениться — берём свежий
      const fresh = otpCandidates();
      const pick = fresh.find((x) => x.name === name) || fresh[0] || null;
      dismiss();
      if (!pick) return;
      if (fillOtpEverywhere(pick.code)) toast("Код вставлен ✓");
      else toast("Код: " + fmtCode(pick.code));
    };

    if (cand.length === 1) {
      const codeEl = document.createElement("div");
      codeEl.className = "ocode";
      codeEl.textContent = fmtCode(cand[0].code);
      c.appendChild(codeEl);
      const row = document.createElement("div");
      row.className = "row";
      row.innerHTML = `<button class="pri">Заполнить</button><button class="sec">Не сейчас</button>`;
      row.querySelector(".pri").addEventListener("click", () => doFill(cand[0].name));
      row.querySelector(".sec").addEventListener("click", dismiss);
      c.appendChild(row);
      const iv = setInterval(async () => {   // код живёт 30 с — держим цифры на карточке свежими
        if (!c.isConnected) { clearInterval(iv); return; }
        await query();
        const f = otpCandidates();
        const cur = f.find((x) => x.name === cand[0].name) || f[0];
        if (cur) codeEl.textContent = fmtCode(cur.code);
      }, 5000);
    } else {
      for (const it of cand) {
        const d = document.createElement("div");
        d.className = "oit";
        const cc = document.createElement("span"); cc.className = "c"; cc.textContent = fmtCode(it.code);
        const nn = document.createElement("span"); nn.className = "n"; nn.textContent = it.name;
        d.appendChild(cc); d.appendChild(nn);
        d.addEventListener("click", () => doFill(it.name));
        c.appendChild(d);
      }
      const row = document.createElement("div");
      row.className = "row";
      row.innerHTML = `<button class="sec">Не сейчас</button>`;
      row.querySelector(".sec").addEventListener("click", dismiss);
      c.appendChild(row);
    }

    root().appendChild(c);
    otpOffer = c;
  }

  // ---------- boot ----------
  function boot() { attachFieldHandlers(); autofill(); maybeOfferOtp(); }

  let debounce = null;
  new MutationObserver(() => {
    clearTimeout(debounce);
    debounce = setTimeout(() => { attachFieldHandlers(); autofill(); maybeOfferOtp(); }, 400);
  }).observe(document.documentElement, { childList: true, subtree: true });

  window.addEventListener("scroll", repositionAll, true);
  window.addEventListener("resize", repositionAll, true);
  setInterval(repositionAll, 250);

  boot();
  maybeOfferSave();
})();
