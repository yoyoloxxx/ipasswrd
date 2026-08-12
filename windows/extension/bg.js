// IPasswrd background worker: relays content-script requests to the app
// and keeps the "credentials just submitted" stash across the post-login navigation.
//
// Two transports, tried in order:
//   1) native messaging (the normal route);
//   2) loopback HTTP to the app itself — used when an antivirus (e.g. Kaspersky)
//      blocks the browser from launching the native host executable.
// Whichever works is remembered for subsequent calls.

const HOST = "com.yoyoloxxx.ipasswrd";
const HTTP_BRIDGE = "http://127.0.0.1:38799/";

let transport = "native"; // "native" | "http" — sticky until it fails

// Per-session bearer for the HTTP fallback. The trusted native (pipe) path hands it to us in
// `status`; on the HTTP-only path we get it via a one-time `pair` the user approves in the app.
let bridgeToken = null;
try { chrome.storage.session.get("ipwToken").then((o) => { if (o && o.ipwToken) bridgeToken = o.ipwToken; }).catch(() => {}); } catch (e) {}
function rememberToken(resp) { if (resp && resp.token) { bridgeToken = resp.token; try { chrome.storage.session.set({ ipwToken: bridgeToken }); } catch (e) {} } }

function callViaHost(msg) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage(HOST, msg, (resp) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: "host_unreachable", detail: chrome.runtime.lastError.message });
        } else {
          rememberToken(resp);
          resolve(resp || { ok: false, error: "empty_response" });
        }
      });
    } catch (e) {
      resolve({ ok: false, error: "host_unreachable", detail: String(e) });
    }
  });
}

async function callViaHttp(msg) {
  try {
    const ctl = new AbortController();
    const t = setTimeout(() => ctl.abort(), 4000);
    const r = await fetch(HTTP_BRIDGE, {
      method: "POST",
      headers: { "content-type": "text/plain" },   // simple request — no preflight
      body: JSON.stringify(bridgeToken ? { ...msg, token: bridgeToken } : msg),
      signal: ctl.signal,
    });
    clearTimeout(t);
    if (!r.ok) return { ok: false, error: "host_unreachable", detail: "http_" + r.status };
    const j = await r.json();
    rememberToken(j);
    return j;
  } catch (e) {
    return { ok: false, error: "host_unreachable", detail: "http_fail" };
  }
}

async function httpCall(msg) {
  let h = await callViaHttp(msg);
  if (h && h.error === "unpaired") {                  // secret command over HTTP without a token yet
    const pr = await callViaHttp({ cmd: "pair" });    // prompts the app once; returns a token on allow
    if (pr && pr.ok && bridgeToken) h = await callViaHttp(msg);   // retry once, now paired
  }
  return h;
}

async function callNative(msg) {
  if (transport === "http") {
    const h = await httpCall(msg);
    if (!h || h.error === "host_unreachable") transport = "native"; // app restarted? retry the normal route
    else return h;
  }
  const n = await callViaHost(msg);
  if (!n || n.error !== "host_unreachable") return n;
  const h2 = await httpCall(msg);                    // native host blocked → loopback fallback
  if (h2 && h2.error !== "host_unreachable") { transport = "http"; return h2; }
  n.detail = (n.detail || "") + " | " + (h2 && h2.detail || "http?");   // both down — show both reasons
  return n;
}

const stashKey = (tabId) => "pending_" + tabId;

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    const tabId = sender.tab && sender.tab.id;

    if (msg && msg.cmd === "_stash") {
      // credentials captured at submit — keep for the page shown after navigation
      if (tabId != null) {
        await chrome.storage.session.set({ [stashKey(tabId)]: { ...msg.data, t: Date.now() } });
      }
      sendResponse({ ok: true });
      return;
    }

    if (msg && msg.cmd === "_takePending") {
      // the newly loaded page asks whether a save prompt is due
      if (tabId == null) { sendResponse({ ok: true, pending: null }); return; }
      const key = stashKey(tabId);
      const got = await chrome.storage.session.get(key);
      const pending = got[key] || null;
      await chrome.storage.session.remove(key);
      // stale stashes (tab reused much later) are not worth prompting about
      sendResponse({ ok: true, pending: pending && Date.now() - pending.t < 120000 ? pending : null });
      return;
    }

    // everything else goes to the app through the native host
    let resp = await callNative(msg);
    if (resp && resp.error === "host_unreachable") {          // host may be mid-start — one retry heals it
      await new Promise((r) => setTimeout(r, 500));
      resp = await callNative(msg);
    }
    sendResponse(resp);
  })();
  return true; // async sendResponse
});

// Toolbar button: open (and raise) the IPasswrd app.
chrome.action.onClicked.addListener(() => { callNative({ cmd: "focus" }); });

// Closing a tab drops its stash.
chrome.tabs.onRemoved.addListener((tabId) => {
  chrome.storage.session.remove(stashKey(tabId));
});

// Dev: Alt+Shift+R reloads the whole extension.
if (chrome.commands && chrome.commands.onCommand) {
  chrome.commands.onCommand.addListener((c) => { if (c === 'reload-ext') chrome.runtime.reload(); });
}
