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
