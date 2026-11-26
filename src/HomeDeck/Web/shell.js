/* HomeDeck bridge bootstrap (hd/1) — injected by the shell into every page.
 *
 * Pages should feature-detect, never assume:
 *   if (window.HomeDeck) { const b = await window.HomeDeck.getBattery(); ... }
 *
 * Every call resolves {ok:true, data?...} or {ok:false, error}.
 * error "not-implemented: x" means this shell version has the surface
 * but not the capability yet.
 */
(function () {
  if (window.HomeDeck) return;
  // Top frame only (audit H1): subframes get no bridge. The equality
  // check itself is allowed cross-origin; sandboxed frames without
  // top access fall through the catch.
  try { if (window.self !== window.top) return; } catch (e) { return; }

  var nextId = 1;
  var pending = {};
  // Per-navigation token (audit H1): native delivers it top-frame-only
  // after each navigation. No call is emitted before it arrives; every
  // emitted call carries it first; native rejects anything else.
  var hdToken = null;
  var preToken = [];
  function flushPreToken() {
    var q = preToken; preToken = [];
    for (var i = 0; i < q.length; i++) {
      (function (c) {
        try {
          var r = c.exec(hdToken);
          if (r && typeof r.then === "function") r.then(c.resolve, function (e) { c.resolve({ ok: false, error: String(e && e.message || e) }); });
          else c.resolve(r);
        } catch (e) { try { c.resolve({ ok: false, error: String(e && e.message || e) }); } catch (e2) {} }
      })(q[i]);
    }
  }
  window.__hdTokenArrived = function (t) {
    try {
      if (typeof t !== "string" || t.length < 16) return;
      hdToken = t;
      flushPreToken();
    } catch (e) {}
  };
  function withToken(name, args, exec) {
    if (hdToken) { try { return exec(hdToken); } catch (e) { return Promise.resolve({ ok: false, error: String(e && e.message || e) }); } }
    return new Promise(function (resolve) {
      var item = { name: name, args: args, exec: exec, resolve: resolve };
      preToken.push(item);
      setTimeout(function () { var i = preToken.indexOf(item); if (i >= 0) { preToken.splice(i, 1); resolve({ ok: false, error: "timeout" }); } }, 30000);
    });
  }
  // Native proxy resolution: prefer a SYNC proxy (window.HomeDeckNative,
  // else chrome.webview.hostObjects.sync.*). The async promise-proxy
  // (chrome.webview.hostObjects.*) cannot run this object's sync methods,
  // so it is only a last resort, used with promise handling. Re-resolved
  // per call until a sync proxy sticks: on Windows the host object can
  // arrive after this script runs (registration retries on navigation).
  var native = null; // {p, sync} or null
  function getNative() {
    try {
      if (window.HomeDeckNative) native = { p: window.HomeDeckNative, sync: true };
      else if (window.chrome?.webview?.hostObjects?.sync?.HomeDeckNative)
        native = { p: window.chrome.webview.hostObjects.sync.HomeDeckNative, sync: true };
      else if (!native || !native.sync) {
        if (window.chrome?.webview?.hostObjects?.HomeDeckNative)
          native = { p: window.chrome.webview.hostObjects.HomeDeckNative, sync: false };
        else native = null;
      }
    } catch (e) { native = null; }
    return native;
  }

  // No native proxy: queue for the shell's poll drain instead of failing.
  // The entry resolves via __hdResolveMsg like a normal async call.
  // (args are already token-stamped by the caller.)
  function enqueueCallInner(name, args) {
    var id = nextId++;
    return new Promise(function (resolve) {
      pending[id] = resolve;
      try {
        window.__hdOutbox = window.__hdOutbox || [];
        window.__hdOutbox.push({ id: id, method: name, args: args || [] });
      } catch (e) { delete pending[id]; resolve({ ok: false, error: String(e && e.message || e) }); return; }
      setTimeout(function () { if (pending[id]) { delete pending[id]; resolve({ ok: false, error: "timeout" }); } }, 30000);
    });
  }

  function enqueueCall(name, args) {
    return withToken(name, args, function (tok) { return enqueueCallInner(name, [tok].concat(args || [])); });
  }

  function callSync(name, ...args) {
    return withToken(name, args, function (tok) {
    try {
      var n = getNative();
      if (!n) return enqueueCallInner(name, [tok].concat(args));
      if (!n.p[name]) return Promise.resolve({ ok: false, error: "no-method: " + name });
      if (n.sync) {
        var r = n.p[name](tok, ...args);
        if (typeof r === "string") return Promise.resolve(JSON.parse(r));
        if (r && typeof r.then === "function") return r.then(x => typeof x === "string" ? JSON.parse(x) : x);
        return Promise.resolve({ ok: false, error: "no-method: " + name });
      }
      return Promise.resolve(n.p[name](tok, ...args)).then(
        x => { try { return typeof x === "string" ? JSON.parse(x) : x; } catch (e) { return { ok: false, error: String(e) }; } },
        e => ({ ok: false, error: String(e && e.message || e) }));
    } catch (e) {
      return Promise.resolve({ ok: false, error: String(e && e.message || e) });
    }
    });
  }

  // Android resolves callbacks via window.__hdResolve(id, json string).
  window.__hdResolve = function (id, json) {
    var cb = pending[id];
    if (!cb) return;
    delete pending[id];
    try { cb(typeof json === "string" ? JSON.parse(json) : json); }
    catch (e) { cb({ ok: false, error: String(e) }); }
  };

  // Windows resolves callbacks via window.__hdResolveMsg({id, result}).
  window.__hdResolveMsg = function (msg) {
    var m = typeof msg === "string" ? JSON.parse(msg) : msg;
    window.__hdResolve(m.id, m.result);
  };

  function callAsync(name, ...args) {
    return withToken(name, args, function (tok) {
    var n = getNative();
    if (!n) return enqueueCallInner(name, [tok].concat(args));
    try { if (!n.p[name]) return Promise.resolve({ ok: false, error: "no-method: " + name }); }
    catch (e) { return Promise.resolve({ ok: false, error: String(e && e.message || e) }); }
    var id = nextId++;
    return new Promise(resolve => {
      pending[id] = resolve;
      try {
        var invoked = n.p[name](id, tok, ...args);
        // Async proxy: a rejected invocation means dispatch failed — fail
        // fast instead of waiting out the 30s callback timeout. (The real
        // result still arrives via __hdResolveMsg, as with sync proxies.)
        if (!n.sync && invoked && typeof invoked.then === "function")
          invoked.then(null, e => { if (pending[id]) { delete pending[id]; resolve({ ok: false, error: String(e && e.message || e) }); } });
      }
      catch (e) { delete pending[id]; resolve({ ok: false, error: String(e) }); }
      setTimeout(() => { if (pending[id]) { delete pending[id]; resolve({ ok: false, error: "timeout" }); } }, 30000);
    });
    });
  }

  // file:// homepages have no hostname: they act as "local" so the
  // one-time site grant can persist instead of asking on every call.
  function pgHost() { try { if (window.location.protocol === "file:") return "local"; return window.location.hostname || ""; } catch (e) { return ""; } }

  window.HomeDeck = {
    version: "hd/1",
    getBattery: () => callAsync("getBattery", pgHost()),
    getNetwork: () => callAsync("getNetwork", pgHost()),
    getDevice: () => callAsync("getDevice", pgHost()),
    vibrate: (ms) => callSync("vibrate", ms | 0),
    bleScan: (seconds) => callAsync("bleScan", pgHost(), Math.max(1, Math.min(30, seconds | 0 || 8))),
    getNotifications: () => callAsync("getNotifications", pgHost()),
    notificationAccess: () => callSync("notificationAccess"),
    openNotificationSettings: () => callAsync("openNotificationSettings"),
    getBrightness: () => callSync("getBrightness"),
    setBrightness: (level) => callSync("setBrightness", +level),
    openWifiSettings: () => callAsync("openWifiSettings"),
    openBluetoothSettings: () => callAsync("openBluetoothSettings"),
    fileRoot: () => callSync("fileRoot"),
    listDir: (path) => callAsync("listDir", pgHost(), path || ""),
    fileInfo: (path) => callAsync("fileInfo", pgHost(), path || ""),
    readFile: (path) => callAsync("readFile", pgHost(), path || ""),
    writeFile: (path, base64) => callAsync("writeFile", pgHost(), path || "", base64 || ""),
    deleteFile: (path) => callAsync("deleteFile", pgHost(), path || ""),
    mkdir: (path) => callAsync("mkdir", pgHost(), path || ""),
    pickApps: () => callAsync("pickApps", pgHost()),
    openApp: (id) => callAsync("openApp", pgHost(), id || ""),
    pickedApps: () => callAsync("pickedApps", pgHost()),
    wifiScan: () => callAsync("wifiScan"),
    bluetoothDevices: (inquiry) => callAsync("bluetoothDevices", inquiry ? 1 : 0),
    shareText: (text, title) => callAsync("shareText", text || "", title || ""),
    clipboardWrite: (text) => callAsync("clipboardWrite", pgHost(), text || ""),
    clipboardRead: () => callAsync("clipboardRead", pgHost()),
    notify: (title, body) => callAsync("notify", pgHost(), title || "", body || "")
  };

  window.dispatchEvent(new Event("homedeck:ready"));
})();

/* Poll-transport outbox (no native proxy — e.g. Windows file://): calls
 * land here when getNative() finds nothing; the shell drains the queue
 * with script eval and posts answers to __hdResolveMsg. Top frame only
 * (audit H1): subframes neither create nor drain a queue. Re-injection
 * safe: the queue is never wiped, the drainer just re-attached. */
var __hdIsTop = false;
try { __hdIsTop = window.self === window.top; } catch (e) {}
if (__hdIsTop) {
window.__hdOutbox = window.__hdOutbox || [];
window.__hdTakeOutbox = function () {
  try {
    var q = window.__hdOutbox || [];
    window.__hdOutbox = [];
    return JSON.stringify(q);
  } catch (e) { try { window.__hdOutbox = []; } catch (e2) {} return "[]"; }
};
}

/* Hidden-element count for the shell's counter (one pass per load). */
window.__hdCountMatches = function (list) {
  var n = 0;
  try {
    if (!Array.isArray(list)) return 0;
    for (var i = 0; i < list.length && i < 300; i++) {
      try { n += document.querySelectorAll(list[i]).length; } catch (e) {}
    }
  } catch (e) {}
  return n;
};

/* Element picker (hd/1 pick). window.HomeDeckPick.start() arms it:
 * tap/click an element to lock the gold striped highlight, then Hide
 * (hides instantly + reports host+selector to the shell for a saved
 * per-site rule) or Cancel. Esc cancels. Main document only, v1. */
(function () {
  if (window.HomeDeckPick) return;
  var GOLD = "#E8B34A";
  var active = false, locked = null, overlay = null, bar = null, barLabel = null;

  function cssEsc(s) {
    try { return CSS.escape(String(s)); }
    catch (e) { return String(s).replace(/([^a-zA-Z0-9_-])/g, "\\$1"); }
  }

  function unique(sel) {
    try { return document.querySelectorAll(sel).length === 1; }
    catch (e) { return false; }
  }

  // Shortest unique selector: #id, then tag+classes up 3 levels, then
  // :nth-of-type fallback. Verified with querySelectorAll at each step.
  function genSelector(el) {
    if (!el || el === document.documentElement) return "body";
    if (el.id && unique("#" + cssEsc(el.id))) return "#" + cssEsc(el.id);
    var parts = [], cur = el, depth = 0;
    while (cur && cur !== document.body && cur !== document.documentElement && depth < 4) {
      var tag = cur.tagName.toLowerCase();
      var cls = (typeof cur.className === "string" ? cur.className : "").trim().split(/\s+/).filter(Boolean).slice(0, 2).map(cssEsc);
      parts.unshift(tag + (cls.length ? "." + cls.join(".") : ""));
      if (unique(parts.join(" > "))) return parts.join(" > ").slice(0, 500);
      var idx = 1, sib = cur.previousElementSibling, total = 1, q = cur.parentElement ? cur.parentElement.firstElementChild : null;
      while (sib) { if (sib.tagName === cur.tagName) idx++; sib = sib.previousElementSibling; }
      while (q) { if (q.tagName === cur.tagName) total++; q = q.nextElementSibling; }
      if (total > 1) {
        parts[0] = tag + ":nth-of-type(" + idx + ")" + (cls.length ? "." + cls.join(".") : "");
        if (unique(parts.join(" > "))) return parts.join(" > ").slice(0, 500);
      }
      cur = cur.parentElement; depth++;
    }
    parts.unshift("body");
    return parts.join(" > ").slice(0, 500);
  }

  function shortLabel(el) {
    var t = el.tagName.toLowerCase();
    if (el.id) t += "#" + el.id;
    else if (typeof el.className === "string" && el.className.trim()) t += "." + el.className.trim().split(/\s+/)[0];
    return t.slice(0, 40);
  }

  function inChrome(n) { return !!(n && n.closest && n.closest("[data-hd-pick]")); }

  function place(el) {
    if (!overlay || !el || !el.getBoundingClientRect) return;
    var r = el.getBoundingClientRect();
    overlay.style.display = "block";
    overlay.style.left = (r.left - 2) + "px";
    overlay.style.top = (r.top - 2) + "px";
    overlay.style.width = (r.width + 4) + "px";
    overlay.style.height = (r.height + 4) + "px";
  }

  function showBar(el) {
    barLabel.textContent = shortLabel(el);
    bar.style.display = "flex";
  }

  function hideBar() { if (bar) bar.style.display = "none"; }

  // Same convention as the bridge's pgHost(): file:// pages have no
  // hostname, so they report and match rules as "local" instead of "".
  // (The shell rejects empty hosts — "" used to die as "bad host".)
  function pageHost() { try { if (window.location.protocol === "file:") return "local"; return window.location.hostname || ""; } catch (e) { return ""; } }

  function report(host, sel, action) {
    var payload = { type: "hd-pick", host: host, selector: sel, action: action };
    try {
      // Real object only (Android interface, or the Windows sync proxy):
      // the async promise-proxy fabricates truthy lookups for methods that
      // don't exist natively (reportPicked/reportRemoved are Android-only),
      // and such a call vanishes with an unhandled rejection while this
      // function wrongly reports success — so the message below never sends.
      var n = window.HomeDeckNative;
      var m = action === "remove" ? (n && n.reportRemoved) : (n && n.reportPicked);
      if (typeof m === "function") { m.call(n, host, sel); return true; }
    } catch (e) {}
    try {
      // String form: this SDK's postMessage is string-only (an object arg
      // throws and dies silent in the catch below). The shell unwraps
      // a JSON string before reading the payload.
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(JSON.stringify(payload));
        return true;
      }
    } catch (e2) {}
    return false;
  }

  function onHover(e) {
    if (!active || locked || inChrome(e.target)) return;
    var el = e.target;
    if (el === document.documentElement || el === document.body) { if (overlay) overlay.style.display = "none"; return; }
    place(el);
  }

  function onTap(e) {
    if (!active || inChrome(e.target)) return;
    e.preventDefault();
    e.stopPropagation();
    var el = e.target;
    if (el === document.documentElement || el === document.body) {
      // Tapped empty page: clear the lock, keep picking.
      locked = null;
      if (overlay) overlay.style.display = "none";
      hideBar();
      return;
    }
    locked = el;
    place(el);
    showBar(el);
  }

  function onKey(e) {
    if (e.key !== "Escape") return;
    // Global exit first: the shell cancels pick mode everywhere (this
    // frame's Esc otherwise never reaches other frames or the shell).
    try {
      // Same rule as report(): real object with a verified method only.
      // (reportCancel exists natively, so the old async-proxy call happened
      // to work — but it also duplicated the hd-cancel message below.)
      var n = window.HomeDeckNative;
      if (n && typeof n.reportCancel === "function") { try { n.reportCancel.call(n); } catch (e0) {} }
    } catch (e1) {}
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage)
        window.chrome.webview.postMessage(JSON.stringify({ type: "hd-cancel" }));
    } catch (e2) {}
    cancel();
  }

  function onScroll() { if (active && locked) place(locked); }

  // Draggable bar: grab empty bar space (not the buttons) to move it,
  // so elements behind it can be reached. Sits below the native X.
  var dragPt = null;

  function onDragStart(e) {
    if (!active || !bar) return;
    if (e.target && e.target.closest && e.target.closest("button")) return;
    dragPt = { x: e.clientX, y: e.clientY };
    try { e.preventDefault(); } catch (e2) {}
  }

  function onDragMove(e) {
    if (!dragPt || !bar) return;
    try {
      var r = bar.getBoundingClientRect();
      bar.style.transform = "none";
      bar.style.left = (r.left + e.clientX - dragPt.x) + "px";
      bar.style.top = (r.top + e.clientY - dragPt.y) + "px";
      dragPt = { x: e.clientX, y: e.clientY };
    } catch (e2) {}
  }

  function onDragUp() { dragPt = null; }

  // Picking is over page-side (bar auto-exits after Hide/Delete): tell
  // the app too, so it disarms and hides the native pill. Host path only
  // via the real object with a verified method (same rule as report());
  // otherwise the hd-cancel message, which the shell maps to its shared
  // cancel — same as the native ✕ and frame-Esc paths.
  function notifyDone() {
    try {
      var n = window.HomeDeckNative;
      if (n && typeof n.reportCancel === "function") { try { n.reportCancel.call(n); } catch (e0) {} return; }
    } catch (e1) {}
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage)
        window.chrome.webview.postMessage(JSON.stringify({ type: "hd-cancel" }));
    } catch (e2) {}
  }

  function doHide() {
    if (!locked) return;
    var sel = genSelector(locked);
    try { locked.style.setProperty("display", "none", "important"); } catch (e) {}
    var hid = report(pageHost(), sel, "hide");
    barLabel.textContent = hid ? "Hidden ✓" : "Hidden — NOT saved (no bridge)";
    setTimeout(function () { exit(); notifyDone(); }, hid ? 900 : 2500);
  }

  function doRemove() {
    if (!locked) return;
    var sel = genSelector(locked);
    try { locked.remove(); } catch (e) {}
    var rem = report(pageHost(), sel, "remove");
    barLabel.textContent = rem ? "Deleted ✓" : "Deleted — NOT saved (no bridge)";
    setTimeout(function () { exit(); notifyDone(); }, rem ? 900 : 2500);
  }

  function cancel() { exit(); }

  function exit() {
    active = false; locked = null;
    try {
      document.removeEventListener("click", onTap, true);
      document.removeEventListener("mouseover", onHover, true);
      document.removeEventListener("keydown", onKey, true);
      document.removeEventListener("pointermove", onDragMove, true);
      document.removeEventListener("pointerup", onDragUp, true);
      window.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("resize", onScroll, true);
      if (overlay && overlay.parentNode) overlay.parentNode.removeChild(overlay);
      if (bar && bar.parentNode) bar.parentNode.removeChild(bar);
    } catch (e) {}
    overlay = null; bar = null; barLabel = null; dragPt = null;
  }

  function start() {
    if (active) return;
    active = true; locked = null;
    overlay = document.createElement("div");
    overlay.setAttribute("data-hd-pick", "1");
    overlay.style.cssText = "position:fixed;display:none;pointer-events:none;z-index:2147483646;" +
      "border:2px solid " + GOLD + ";box-sizing:border-box;" +
      "background:repeating-linear-gradient(45deg,rgba(232,179,74,0.40) 0 10px,rgba(232,179,74,0.10) 10px 20px);";
    bar = document.createElement("div");
    bar.setAttribute("data-hd-pick", "1");
    bar.style.cssText = "position:fixed;display:none;top:64px;left:50%;transform:translateX(-50%);z-index:2147483647;" +
      "align-items:center;gap:8px;max-width:92vw;padding:8px 10px;border-radius:10px;touch-action:none;" +
      "background:#161311;border:1px solid " + GOLD + ";color:#fff;" +
      "font:13px -apple-system,Segoe UI,Roboto,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.5);";
    barLabel = document.createElement("span");
    barLabel.style.cssText = "overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:44vw;";
    var hide = document.createElement("button");
    hide.textContent = "Hide";
    hide.style.cssText = "border:none;border-radius:8px;padding:6px 14px;background:" + GOLD + ";color:#161311;font-weight:700;font-size:13px;";
    hide.addEventListener("click", function (e) { e.stopPropagation(); doHide(); });
    var del = document.createElement("button");
    del.textContent = "Delete";
    del.style.cssText = "border:1px solid " + GOLD + ";border-radius:8px;padding:6px 14px;background:transparent;color:" + GOLD + ";font-size:13px;";
    del.addEventListener("click", function (e) { e.stopPropagation(); doRemove(); });
    var no = document.createElement("button");
    no.textContent = "Cancel";
    no.style.cssText = "border:1px solid #8a8272;border-radius:8px;padding:6px 14px;background:transparent;color:#fff;font-size:13px;";
    no.addEventListener("click", function (e) { e.stopPropagation(); cancel(); });
    bar.appendChild(barLabel); bar.appendChild(hide); bar.appendChild(del); bar.appendChild(no);
    document.documentElement.appendChild(overlay);
    document.documentElement.appendChild(bar);
    document.addEventListener("click", onTap, true);
    document.addEventListener("mouseover", onHover, true);
    document.addEventListener("keydown", onKey, true);
    bar.addEventListener("pointerdown", onDragStart);
    document.addEventListener("pointermove", onDragMove, true);
    document.addEventListener("pointerup", onDragUp, true);
    window.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", onScroll, true);
  }

  window.HomeDeckPick = {
    start: start,
    cancel: cancel,
    isActive: function () { return active; }
  };

  // Remove-mode rules: delete matching nodes on every load. Selector lists
  // come from the shell (per-host). Runs at injection + once late for
  // slow-building pages. Main document only, v1.
  function applyRemovals() {
    try {
      var host = pageHost();
      if (!host) return;
      // Real object only (same rule as report()): the async proxy would
      // answer a promise here and JSON.parse would throw — caught, but
      // pointless. File pages simply skip until a sync proxy exists.
      var n = window.HomeDeckNative;
      if (!n || typeof n.getRemoveSelectors !== "function") return;
      var list = JSON.parse(n.getRemoveSelectors(host) || "[]");
      if (!Array.isArray(list)) return;
      list.forEach(function (sel) {
        try {
          var els = document.querySelectorAll(sel);
          for (var i = 0; i < els.length; i++) {
            if (!els[i].closest("[data-hd-pick]")) els[i].remove();
          }
        } catch (e) {}
      });
    } catch (e) {}
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", function () { applyRemovals(); setTimeout(applyRemovals, 2000); });
  } else {
    applyRemovals();
    setTimeout(applyRemovals, 2000);
  }
})();
