"use strict";
(() => {
  const token = location.hash.slice(1);
  const validToken = /^[A-Za-z0-9_-]{43}$/.test(token);
  const api = new URL("../api/", location.href);
  const element = id => document.getElementById(id);
  const views = ["login-view", "join-view", "done-view"];
  let bearer = null, account = "", preview = null, busy = false, generation = 0;
  const status = (text, tone = "error") => { element("status").textContent = text; element("status").dataset.tone = tone; };
  const show = id => { for (const view of views) element(view).hidden = view !== id; };
  const setBusy = value => {
    busy = value;
    for (const id of ["login-button", "join-button", "switch-button", "done-button"]) element(id).disabled = value || !validToken;
  };
  function expiryText(invitation) {
    // Never infer a no-expiry grant from a very distant date. The explicit
    // server flag and compatibility timestamp must agree; legacy finite
    // replies that omit the flag retain their original date presentation.
    const unlimited = invitation.neverExpires === true;
    const expiry = new Date(invitation.expiresAt);
    const maximum = Date.parse("9999-12-31T23:59:59.999Z");
    if ((invitation.neverExpires !== undefined && typeof invitation.neverExpires !== "boolean") ||
        !Number.isFinite(expiry.getTime()) || (unlimited ? expiry.getTime() !== maximum : expiry.getTime() === maximum))
      throw new Error("Sunucu geçerli bir davet süresi vermedi. Lütfen yeniden dene.");
    return unlimited ? "Sınırsız" : new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short" }).format(expiry);
  }
  async function request(route, body, authorization = bearer, keepalive = false) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), keepalive ? 4000 : 20000);
    try {
      const response = await fetch(new URL(route, api), {
        method: "POST", cache: "no-store", credentials: "omit", redirect: "error", referrerPolicy: "no-referrer",
        headers: { "Content-Type": "application/json", ...(authorization ? { Authorization: "Bearer " + authorization } : {}) },
        body: JSON.stringify(body), signal: controller.signal, keepalive
      });
      if (response.status === 204) return null;
      const data = await response.json();
      if (!response.ok) throw new Error(data.message || "İşlem tamamlanamadı. Lütfen yeniden dene.");
      return data;
    } catch (error) {
      if (error.name === "AbortError") throw new Error("Bağlantı zaman aşımına uğradı. Lütfen yeniden dene.");
      if (error instanceof TypeError) throw new Error("Sunucuya ulaşılamıyor. İnternet bağlantını kontrol et.");
      if (error instanceof SyntaxError) throw new Error("Sunucu geçerli bir yanıt vermedi. Lütfen yeniden dene.");
      throw error;
    } finally { clearTimeout(timeout); }
  }
  async function releaseSession() {
    const old = bearer; bearer = null;
    if (old) { try { await request("auth/logout", {}, old, true); } catch { /* Do not turn successful membership into an error. */ } }
  }
  async function reset() {
    if (busy) return;
    generation++; preview = null; status(""); show("login-view");
    setBusy(true); await releaseSession(); setBusy(false); element("password").value = ""; element("username").focus();
  }
  element("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    if (busy || !validToken) return;
    const current = ++generation; setBusy(true); status("Davet kontrol ediliyor…", "info");
    const password = element("password").value;
    element("password").value = "";
    try {
      const session = await request("auth/login", { email: element("username").value.trim(), password }, null);
      if (current !== generation) {
        // Leaving while login is in flight can create a bearer after pagehide's
        // cleanup ran. Revoke that exact late session, never a newer login's bearer.
        try { await request("auth/logout", {}, session.accessToken, true); } catch { /* Best-effort unload cleanup. */ }
        return;
      }
      bearer = session.accessToken; account = session.user.displayName;
      preview = await request("group-invites/preview", { token });
      if (current !== generation) return;
      element("group-title").textContent = preview.title;
      element("signed-in").textContent = account + " hesabıyla katılacaksın.";
      element("expires").textContent = expiryText(preview);
      element("join-button").firstChild.textContent = preview.alreadyMember ? "Sohbeti aç " : "Gruba katıl ";
      status(""); show("join-view");
    } catch (error) {
      if (current === generation) { status(error.message); await releaseSession(); preview = null; }
    } finally {
      if (current === generation) setBusy(false);
    }
  });
  element("join-button").addEventListener("click", async () => {
    if (busy || !bearer || !preview) return;
    const current = generation; setBusy(true); status("Katılım onaylanıyor…", "info");
    try {
      await request("group-invites/join", { token });
      if (current !== generation) return;
      element("done-title").textContent = preview.alreadyMember ? "Zaten bu gruptasın." : "Gruba katıldın.";
      element("done-description").textContent = "MTK Chat uygulamasını aç; “" + preview.title + "” sohbet listende görünecek.";
      status(""); show("done-view"); await releaseSession();
    } catch (error) { if (current === generation) status(error.message); }
    finally { if (current === generation) setBusy(false); }
  });
  element("switch-button").addEventListener("click", reset);
  element("done-button").addEventListener("click", reset);
  addEventListener("pagehide", () => { generation++; void releaseSession(); });
  addEventListener("pageshow", event => {
    if (!event.persisted) return;
    preview = null; status(""); show("login-view"); setBusy(false); element("password").value = "";
    if (!validToken) status("Davet bağlantısı eksik veya geçersiz. Grup yöneticisinden yeni bir bağlantı iste.");
  });
  setBusy(false);
  if (!validToken) status("Davet bağlantısı eksik veya geçersiz. Grup yöneticisinden yeni bir bağlantı iste.");
})();
