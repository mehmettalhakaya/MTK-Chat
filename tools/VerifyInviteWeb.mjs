// Isolated VM regression checks, NOT an actual browser/end-to-end test. No network,
// persistent credentials, cookies or real accounts are used. Run: node tools/VerifyInviteWeb.mjs
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const scriptFile = fileURLToPath(new URL("../src/MTKChat.Server/InviteWeb/invite.js", import.meta.url));
const htmlFile = fileURLToPath(new URL("../src/MTKChat.Server/InviteWeb/index.html", import.meta.url));
const source = await readFile(scriptFile, "utf8");
const html = await readFile(htmlFile, "utf8");
const syntheticToken = "A".repeat(43);
const session = { accessToken: "vm-only-session", user: { displayName: "VM user" } };
const group = { conversationId: "vm-only-group", title: "VM group", expiresAt: "2076-10-05T12:00:00Z", alreadyMember: false };
let checks = 0, passed = 0, failed = 0;
function check(condition, description) {
  checks++;
  if (!condition) throw new Error(description);
}
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};

function harness(fragment = syntheticToken) {
  const nodes = new Map(), calls = [], plans = new Map(), events = new Map(), timers = new Map(), unexpected = [];
  let nextTimer = 0, unsafeHtmlWrites = 0;
  for (const match of html.matchAll(/<[^>]+\bid="([^"]+)"[^>]*>/g)) {
    const handlers = new Map();
    const node = {
      hidden: /\bhidden\b/.test(match[0]), disabled: false, value: "", textContent: "", dataset: {}, firstChild: { textContent: "" }, focusCount: 0,
      focus() { this.focusCount++; },
      addEventListener(name, handler) { if (!handlers.has(name)) handlers.set(name, []); handlers.get(name).push(handler); },
      emit(name, details = {}) {
        const event = { preventDefault() { this.prevented = true; }, ...details };
        return Promise.all((handlers.get(name) ?? []).map(handler => handler(event)));
      }
    };
    Object.defineProperty(node, "innerHTML", { set() { unsafeHtmlWrites++; throw new Error("Unsafe HTML insertion attempted"); } });
    nodes.set(match[1], node);
  }
  const element = id => { if (!nodes.has(id)) throw new Error("Unknown DOM element used"); return nodes.get(id); };
  function plan(route, response = {}) { if (!plans.has(route)) plans.set(route, []); plans.get(route).push(response); }
  function hold(route) { const pending = deferred(); plan(route, { pending }); return pending; }
  const clock = {
    setTimeout(callback, duration) { const id = ++nextTimer; timers.set(id, { callback, duration }); return id; },
    clearTimeout(id) { timers.delete(id); },
    expire(duration) { for (const [id, timer] of [...timers]) if (timer.duration === duration) { timers.delete(id); timer.callback(); } }
  };
  async function mockedFetch(url, options) {
    const address = new URL(url), route = address.pathname.replace("/chat/api/", "");
    const call = { route, url: address, ...options, body: JSON.parse(options.body) }; calls.push(call);
    const planned = plans.get(route)?.shift();
    if (!planned) { unexpected.push(route); throw new Error("Unexpected mocked request"); }
    let response = planned;
    if (planned.pending) {
      const abort = () => planned.pending.reject(new DOMException("Mock timeout", "AbortError"));
      options.signal.addEventListener("abort", abort, { once: true });
      try { response = await planned.pending.promise; }
      finally { options.signal.removeEventListener("abort", abort); }
    }
    if (response.networkError) throw new TypeError("VM network failure");
    const status = response.status ?? 200;
    return { status, ok: status >= 200 && status < 300,
      async json() { if (response.invalidJson) throw new SyntaxError("VM invalid JSON"); return response.data ?? null; } };
  }
  const context = vm.createContext({
    URL, AbortController, DOMException, Date, Intl, Error, TypeError, SyntaxError,
    location: { hash: "#" + fragment, href: "https://mtkaya.me/chat/invite/#" + fragment },
    document: { getElementById: element }, fetch: mockedFetch,
    setTimeout: clock.setTimeout, clearTimeout: clock.clearTimeout,
    addEventListener(name, handler) { if (!events.has(name)) events.set(name, []); events.get(name).push(handler); }
  });
  vm.runInContext(source, context, { filename: scriptFile, timeout: 2000 });
  const test = {
    element, calls, plan, hold, clock,
    async event(name, details = {}) { for (const handler of events.get(name) ?? []) await handler(details); await flush(); },
    async login(preview = group, displayName = session.user.displayName) {
      plan("auth/login", { data: { ...session, user: { displayName } } }); plan("group-invites/preview", { data: preview });
      element("username").value = "  vm-user  "; element("password").value = "vm-password-not-real";
      await element("login-form").emit("submit");
    },
    clean() {
      check(unexpected.length === 0, "Every fetch must be accounted for by a local mock");
      check(unsafeHtmlWrites === 0, "Untrusted values must never be inserted with innerHTML");
      check(timers.size === 0, "Completed requests must clear all timeout handles");
    },
    view(id) { return !element(id).hidden; },
    count(route) { return calls.filter(call => call.route === route).length; }
  };
  return test;
}

async function run(name, callback) {
  try { await callback(); passed++; console.log("PASS " + name); }
  catch (error) { failed++; console.error("FAIL " + name + ": " + error.message); }
}

await run("invalid fragment is inert", async () => {
  for (const token of ["", "short", "A".repeat(42), "A".repeat(43) + "=", "<script>bad</script>"]) {
    const h = harness(token);
    check(h.element("status").textContent.includes("geçersiz"), "Invalid links must display a clear status");
    for (const id of ["login-button", "join-button", "switch-button", "done-button"]) check(h.element(id).disabled, "Invalid links must disable actions");
    await h.element("login-form").emit("submit"); await h.element("join-button").emit("click");
    check(h.calls.length === 0, "Invalid links must not make requests");
    await h.event("pageshow", { persisted: true }); check(h.element("status").textContent.includes("geçersiz"), "BFcache must preserve invalid-link feedback");
    h.clean();
  }
});

await run("login previews without automatic membership and uses hardened fetch", async () => {
  const h = harness(); await h.login();
  check(h.view("join-view") && !h.view("done-view"), "Successful login must only show confirmation");
  check(h.count("auth/login") === 1 && h.count("group-invites/preview") === 1 && h.count("group-invites/join") === 0, "Authentication must not automatically join");
  check(h.calls[0].body.email === "vm-user" && !h.calls[0].headers.Authorization, "Login must trim identity and omit bearer");
  check(h.calls[1].headers.Authorization === "Bearer " + session.accessToken, "Preview must use authenticated account");
  check(h.calls[1].body.token === syntheticToken && h.calls[1].url.search === "" && h.calls[1].url.hash === "", "Token must only be sent in POST body, not request URL");
  for (const call of h.calls) {
    check(call.url.origin === "https://mtkaya.me" && call.method === "POST", "Fetch must use the expected same-origin POST route");
    check(call.cache === "no-store" && call.credentials === "omit" && call.redirect === "error" && call.referrerPolicy === "no-referrer", "Fetch privacy properties must be preserved");
  }
  check(h.element("password").value === "", "Login must clear password DOM value");
  check(h.element("group-title").textContent === group.title && h.element("expires").textContent.includes("2076"), "Preview must show group and expiry");
  h.clean();
});

await run("unlimited preview displays its flag without a fabricated expiry date", async () => {
  const h = harness();
  await h.login({ ...group, expiresAt: "9999-12-31T23:59:59.9999999+00:00", neverExpires: true });
  check(h.view("join-view") && h.element("expires").textContent === "Sınırsız", "Explicit unlimited preview must show Sınırsız, never a year-9999 date");
  check(h.count("group-invites/join") === 0 && !h.element("join-button").disabled, "Unlimited validity must not cause automatic membership or skip explicit confirmation");
  h.plan("group-invites/join", { data: {} }); h.plan("auth/logout", { status: 204 });
  await h.element("join-button").emit("click");
  check(h.view("done-view") && h.count("group-invites/join") === 1, "Unlimited membership still uses the normal authenticated separate join and logout flow");
  check(h.calls.every(call => call.url.search === "" && call.url.hash === ""), "Unlimited invitation must not move the token into request URLs");
  h.clean();
});

await run("inconsistent unlimited flags cannot expose a confirmation", async () => {
  for (const preview of [
    { ...group, neverExpires: true },
    { ...group, neverExpires: "true" },
    { ...group, expiresAt: "9999-12-31T23:59:59.9999999+00:00", neverExpires: false },
    { ...group, expiresAt: "9999-12-31T23:59:59.9999999+00:00" },
    { ...group, expiresAt: "not-a-date", neverExpires: true }
  ]) {
    const h = harness(); h.plan("auth/logout", { status: 204 }); await h.login(preview);
    check(h.view("login-view") && !h.view("join-view") && h.element("status").textContent.includes("davet süresi"), "Malformed flag/timestamp pairs must fail closed with a localized message");
    await h.element("join-button").emit("click");
    check(h.count("group-invites/join") === 0 && h.count("auth/logout") === 1, "Rejected preview must release its session without joining");
    h.clean();
  }
  const finite = harness(); await finite.login({ ...group, neverExpires: false });
  check(finite.element("expires").textContent.includes("2076") && !finite.element("expires").textContent.includes("Sınırsız"), "Explicit finite false must retain its actual localized expiry");
  finite.clean();
});

await run("explicit join then logout and no repeat join after completion", async () => {
  const h = harness(); await h.login(); h.plan("group-invites/join", { data: {} }); h.plan("auth/logout", { status: 204 });
  await h.element("join-button").emit("click");
  check(h.view("done-view") && h.element("done-title").textContent === "Gruba katıldın.", "Explicit membership must display success");
  check(h.calls.map(call => call.route).join(",") === "auth/login,group-invites/preview,group-invites/join,auth/logout", "Logout must follow successful explicit join");
  const logout = h.calls.at(-1); check(logout.keepalive && logout.headers.Authorization === "Bearer " + session.accessToken, "Logout must release the original session with keepalive");
  await h.element("join-button").emit("click"); check(h.count("group-invites/join") === 1, "Completed session must not send another join");
  await h.element("done-button").emit("click"); check(h.view("login-view") && h.element("username").focusCount === 1, "Done action must reset to focused login"); h.clean();
});

await run("existing member confirmation and switch-account logout", async () => {
  const h = harness(); await h.login({ ...group, alreadyMember: true });
  check(h.element("join-button").firstChild.textContent === "Sohbeti aç ", "Existing membership must use reopen label");
  h.plan("auth/logout", { status: 204 }); await h.element("switch-button").emit("click");
  check(h.view("login-view") && h.count("auth/logout") === 1 && h.count("group-invites/join") === 0, "Switch account must logout without joining");
  await h.element("join-button").emit("click"); check(h.count("group-invites/join") === 0, "Reset must clear bearer and preview");
  await h.login({ ...group, alreadyMember: true }); h.plan("group-invites/join", { data: {} }); h.plan("auth/logout", { status: 204 });
  await h.element("join-button").emit("click"); check(h.element("done-title").textContent === "Zaten bu gruptasın.", "Already-member completion must use the correct description"); h.clean();
});

await run("server values and error text are inert textContent", async () => {
  const dangerous = '<img src=x onerror="throw 1">';
  const h = harness(); await h.login({ ...group, title: dangerous }, dangerous);
  check(h.element("group-title").textContent === dangerous && h.element("signed-in").textContent.startsWith(dangerous), "Group/account strings must remain literal text");
  h.plan("group-invites/join", { status: 400, data: { message: dangerous } }); await h.element("join-button").emit("click");
  check(h.element("status").textContent === dangerous && h.view("join-view"), "Server error strings must remain literal and not falsely report success");
  h.plan("group-invites/join", { data: {} }); h.plan("auth/logout", { status: 204 }); await h.element("join-button").emit("click");
  check(h.element("done-description").textContent.includes(dangerous), "Done description must also remain literal text"); h.clean();
});

await run("network, malformed JSON, login 400 and preview expiry recover cleanly", async () => {
  for (const response of [{ networkError: true }, { invalidJson: true }, { status: 400, data: { message: "VM invalid credentials" } }]) {
    const h = harness(); h.plan("auth/login", response); h.element("password").value = "vm-password-not-real";
    await h.element("login-form").emit("submit");
    check(h.view("login-view") && h.element("status").textContent !== "", "Failed login must show a recoverable error");
    check(h.element("password").value === "" && !h.element("login-button").disabled, "Failed login must clear password and reenable actions");
    check(h.count("group-invites/preview") === 0 && h.count("group-invites/join") === 0, "Failed login must not call invitation actions"); h.clean();
  }
  const h = harness(); h.plan("auth/login", { data: session }); h.plan("group-invites/preview", { status: 410, data: { message: "VM expired invitation" } }); h.plan("auth/logout", { status: 204 });
  await h.element("login-form").emit("submit");
  check(h.view("login-view") && h.element("status").textContent === "VM expired invitation", "Expired preview must show server error without joining");
  check(h.count("auth/logout") === 1 && h.count("group-invites/join") === 0, "Failed preview must release authenticated session"); h.clean();
});

await run("join expiry permits account switch and logout failure does not erase success", async () => {
  const h = harness(); await h.login(); h.plan("group-invites/join", { status: 410, data: { message: "VM expired invitation" } });
  await h.element("join-button").emit("click"); check(h.view("join-view") && !h.element("join-button").disabled, "Failed join must remain recoverable and reenable actions");
  h.plan("auth/logout", { status: 204 }); await h.element("switch-button").emit("click"); check(h.view("login-view"), "User must be able to switch after failed join"); h.clean();
  const success = harness(); await success.login(); success.plan("group-invites/join", { data: {} }); success.plan("auth/logout", { networkError: true });
  await success.element("join-button").emit("click"); check(success.view("done-view") && success.element("status").textContent === "", "Logout transport failure must not turn successful membership into an error"); success.clean();
});

await run("double clicks cannot duplicate login or join", async () => {
  const h = harness(); const pendingLogin = h.hold("auth/login"); h.plan("group-invites/preview", { data: group });
  h.element("password").value = "vm-submitted-password";
  const firstLogin = h.element("login-form").emit("submit");
  check(h.element("password").value === "", "Submitted password must clear immediately, not after the network response");
  await flush(); await h.element("login-form").emit("submit");
  check(h.count("auth/login") === 1 && h.element("login-button").disabled, "Pending login must reject duplicate submit");
  pendingLogin.resolve({ data: session }); await firstLogin;
  const pendingJoin = h.hold("group-invites/join"); h.plan("auth/logout", { status: 204 });
  const firstJoin = h.element("join-button").emit("click"); await flush(); await h.element("join-button").emit("click");
  check(h.count("group-invites/join") === 1 && h.element("join-button").disabled, "Pending join must reject duplicate click");
  pendingJoin.resolve({ data: {} }); await firstJoin; h.clean();
});

await run("request timeout is localized and clears busy state", async () => {
  const h = harness(); h.hold("auth/login"); const login = h.element("login-form").emit("submit"); await flush(); h.clock.expire(20000); await login;
  check(h.element("status").textContent.includes("zaman aşımına"), "Abort timeout must show localized text");
  check(!h.element("login-button").disabled && h.view("login-view"), "Timeout must not lock the page"); h.clean();
  const logout = harness(); await logout.login(); logout.plan("group-invites/join", { data: {} }); logout.hold("auth/logout");
  const joining = logout.element("join-button").emit("click"); await flush(); logout.clock.expire(4000); await joining;
  check(logout.view("done-view") && !logout.element("done-button").disabled, "Keepalive logout timeout must preserve successful membership and release busy state"); logout.clean();
});

await run("pagehide logs out and BFcache restores unauthenticated login", async () => {
  const h = harness(); await h.login(); h.plan("auth/logout", { status: 204 }); await h.event("pagehide");
  check(h.count("auth/logout") === 1 && h.calls.at(-1).keepalive, "Pagehide must release existing session");
  h.element("password").value = "vm-draft"; await h.event("pageshow", { persisted: true });
  check(h.view("login-view") && h.element("password").value === "" && !h.element("login-button").disabled, "BFcache must reset preview/password/busy state");
  await h.element("join-button").emit("click"); check(h.count("group-invites/join") === 0, "BFcache must not retain authorization to join"); h.clean();
});

await run("late preview after pagehide cannot repaint restored page", async () => {
  const h = harness(); h.plan("auth/login", { data: session }); const pending = h.hold("group-invites/preview");
  const login = h.element("login-form").emit("submit"); await flush(); check(h.count("group-invites/preview") === 1, "Fixture must reach pending preview");
  h.plan("auth/logout", { status: 204 }); await h.event("pagehide"); await h.event("pageshow", { persisted: true });
  pending.resolve({ data: group }); await login;
  check(h.view("login-view") && h.count("group-invites/join") === 0, "Generation guard must discard stale preview after restore"); h.clean();
});

await run("late authentication after pagehide must release its newly minted session", async () => {
  const h = harness(); const pending = h.hold("auth/login"); const login = h.element("login-form").emit("submit"); await flush();
  await h.event("pagehide"); await h.event("pageshow", { persisted: true }); h.plan("auth/logout", { status: 204 });
  h.element("password").value = "vm-new-password-draft";
  pending.resolve({ data: session }); await login; await flush();
  check(h.view("login-view") && h.count("group-invites/preview") === 0, "Stale login must not authenticate restored page or preview");
  check(h.element("password").value === "vm-new-password-draft", "Stale login finally must not erase the restored page's new password draft");
  check(h.count("auth/logout") === 1, "A stale login response must logout its newly created session instead of leaking it"); h.clean();
});

await run("stale authentication cleanup must not logout a newer active account", async () => {
  const h = harness(); const pending = h.hold("auth/login"); const firstLogin = h.element("login-form").emit("submit"); await flush();
  await h.event("pagehide"); await h.event("pageshow", { persisted: true });
  const newerSession = { accessToken: "vm-newer-session", user: { displayName: "VM newer user" } };
  h.plan("auth/login", { data: newerSession }); h.plan("group-invites/preview", { data: group }); await h.element("login-form").emit("submit");
  check(h.view("join-view"), "Fixture must authenticate a newer account while old login is pending");
  h.plan("auth/logout", { status: 204 }); pending.resolve({ data: session }); await firstLogin; await flush();
  const staleLogout = h.calls.filter(call => call.route === "auth/logout")[0];
  check(staleLogout?.headers.Authorization === "Bearer " + session.accessToken, "Cleanup must target the stale returned token, not the newer bearer");
  check(h.view("join-view"), "Stale cleanup must leave newer confirmation visible");
  h.plan("group-invites/join", { data: {} }); h.plan("auth/logout", { status: 204 }); await h.element("join-button").emit("click");
  const joined = h.calls.find(call => call.route === "group-invites/join");
  check(joined?.headers.Authorization === "Bearer " + newerSession.accessToken, "Newer account must remain authorized for its explicit join"); h.clean();
});

console.log(`Invite VM regression: ${passed} passed, ${failed} failed, ${checks} assertions. No real browser, network or account was used.`);
if (failed) process.exitCode = 1;
