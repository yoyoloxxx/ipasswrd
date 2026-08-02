// IPasswrd background worker: relays content-script requests to the native host
// and keeps the "credentials just submitted" stash across the post-login navigation.

const HOST = "com.yoyoloxxx.ipasswrd";

function callNative(msg) {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage(HOST, msg, (resp) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: "host_unreachable", detail: chrome.runtime.lastError.message });
        } else {
          resolve(resp || { ok: false, error: "empty_response" });
        }
      });
    } catch (e) {
      resolve({ ok: false, error: "host_unreachable", detail: String(e) });
    }
  });
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
