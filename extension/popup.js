// IPasswrd toolbar popup: unlock the vault right here (the app window never opens).
const send = (msg) => new Promise((res) => {
  try { chrome.runtime.sendMessage(msg, (r) => res(chrome.runtime.lastError ? { ok: false, error: "bg_unreachable" } : (r || { ok: false }))); }
  catch (e) { res({ ok: false, error: String(e) }); }
});
const $ = (id) => document.getElementById(id);
function show(unlocked) {
  $("locked").style.display = unlocked ? "none" : "block";
  $("open").style.display = unlocked ? "block" : "none";
  if (!unlocked) setTimeout(() => $("pw").focus(), 60);
}
async function refresh() {
  const st = await send({ cmd: "status" });
  show(!!(st && st.ok && st.unlocked));
  if (!(st && st.ok)) { $("err").textContent = "Нет связи с приложением"; $("err").style.display = "block"; }
}
async function unlock() {
  const pw = $("pw").value;
  if (!pw) { $("pw").focus(); return; }
  const r = await send({ cmd: "unlock", password: pw });
  $("pw").value = "";
  const err = $("err");
  if (r && r.ok) { err.style.display = "none"; show(true); return; }
  err.style.display = "block";
  if (r && r.error === "wrong_password") err.textContent = r.attemptsLeft > 0 ? ("Неверный пароль. Осталось попыток: " + r.attemptsLeft) : "Неверный пароль. Следующая попытка заблокирует вход.";
  else if (r && r.error === "locked_out") err.textContent = "Слишком много попыток. Подождите " + (r.wait || "");
  else if (r && r.error === "no_vault") err.textContent = "Сейф ещё не создан — откройте приложение.";
  else err.textContent = "Нет связи с приложением";
  $("pw").focus();
}
$("unlock").addEventListener("click", unlock);
$("pw").addEventListener("keydown", (e) => { if (e.key === "Enter") unlock(); });
$("app").addEventListener("click", () => send({ cmd: "focus" }));
refresh();