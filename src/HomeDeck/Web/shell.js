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

  var nextId = 1;
  var pending = {};
  var native = window.HomeDeckNative || window.chrome?.webview?.hostObjects?.HomeDeckNative;

  function callSync(name, ...args) {
    try {
      if (!native) return Promise.resolve({ ok: false, error: "no-bridge" });
      if (native[name]) {
        var r = native[name](...args);
        if (typeof r === "string") return Promise.resolve(JSON.parse(r));
        if (r && typeof r.then === "function") return r.then(x => typeof x === "string" ? JSON.parse(x) : x);
      }
      return Promise.resolve({ ok: false, error: "no-method: " + name });
    } catch (e) {
      return Promise.resolve({ ok: false, error: String(e && e.message || e) });
    }
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
    if (!native || !native[name]) return Promise.resolve({ ok: false, error: "no-bridge" });
    var id = nextId++;
    return new Promise(resolve => {
      pending[id] = resolve;
      try { native[name](id, ...args); }
      catch (e) { delete pending[id]; resolve({ ok: false, error: String(e) }); }
      setTimeout(() => { if (pending[id]) { delete pending[id]; resolve({ ok: false, error: "timeout" }); } }, 30000);
    });
  }

  window.HomeDeck = {
    version: "hd/1",
    getBattery: () => callSync("getBattery"),
    getNetwork: () => callSync("getNetwork"),
    getDevice: () => callSync("getDevice"),
    vibrate: (ms) => callSync("vibrate", ms | 0),
    getLocation: () => callSync("getLocation"),
    setBrightness: (level) => callSync("setBrightness", +level),
    shareText: (text, title) => callAsync("shareText", text || "", title || ""),
    clipboardWrite: (text) => callAsync("clipboardWrite", text || ""),
    clipboardRead: () => callAsync("clipboardRead"),
    notify: (title, body) => callAsync("notify", title || "", body || "")
  };

  window.dispatchEvent(new Event("homedeck:ready"));
})();

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

  function report(host, sel, action) {
    var payload = { type: "hd-pick", host: host, selector: sel, action: action };
    try {
      var n = window.HomeDeckNative ||
        (window.chrome && window.chrome.webview && window.chrome.webview.hostObjects && window.chrome.webview.hostObjects.HomeDeckNative);
      if (n) {
        if (action === "remove" && n.reportRemoved) { n.reportRemoved(host, sel); return true; }
        if (action !== "remove" && n.reportPicked) { n.reportPicked(host, sel); return true; }
      }
    } catch (e) {}
    try {
      // Post the OBJECT: WebMessageAsJson double-wraps a posted string,
      // which once made every report unparseable (and silent).
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(payload);
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

  function onKey(e) { if (e.key === "Escape") cancel(); }

  function onScroll() { if (active && locked) place(locked); }

  function doHide() {
    if (!locked) return;
    var sel = genSelector(locked);
    try { locked.style.setProperty("display", "none", "important"); } catch (e) {}
    report(window.location.hostname || "", sel, "hide");
    barLabel.textContent = "Hidden ✓";
    setTimeout(exit, 900);
  }

  function doRemove() {
    if (!locked) return;
    var sel = genSelector(locked);
    try { locked.remove(); } catch (e) {}
    report(window.location.hostname || "", sel, "remove");
    barLabel.textContent = "Deleted ✓";
    setTimeout(exit, 900);
  }

  function cancel() { exit(); }

  function exit() {
    active = false; locked = null;
    try {
      document.removeEventListener("click", onTap, true);
      document.removeEventListener("mouseover", onHover, true);
      document.removeEventListener("keydown", onKey, true);
      window.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("resize", onScroll, true);
      if (overlay && overlay.parentNode) overlay.parentNode.removeChild(overlay);
      if (bar && bar.parentNode) bar.parentNode.removeChild(bar);
    } catch (e) {}
    overlay = null; bar = null; barLabel = null;
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
    bar.style.cssText = "position:fixed;display:none;top:10px;left:50%;transform:translateX(-50%);z-index:2147483647;" +
      "align-items:center;gap:8px;max-width:92vw;padding:8px 10px;border-radius:10px;" +
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
      var host = window.location.hostname || "";
      if (!host) return;
      var n = window.HomeDeckNative ||
        (window.chrome && window.chrome.webview && window.chrome.webview.hostObjects && window.chrome.webview.hostObjects.HomeDeckNative);
      if (!n || !n.getRemoveSelectors) return;
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
