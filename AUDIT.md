# Home Deck — security audit

Living document. Updated as findings are discussed and fixed, so the list
never gets jumbled. Status per finding: `open` / `discussing` / `accepted`
/ `fixed(<commit>)` / `wontfix(reason)`.

- Started: 2026-11-26. Scope: app at `d0c512a` (Windows + Android shell).
- Method: static trace of the full trust chain as the user experiences it —
  page JS (`Web/shell.js`) → bridge (host object / JS interface / poll
  outbox) → gates (`HdFileGuard`) → storage (`Preferences`) → OS.
- No code was changed for the audit itself.

## Verdict

File handling is solid (scoped root, per-file hash-bound grants, native
prompts, size caps, validated launching). One high-severity hole (H1):
subframes and injected scripts can wield a top site's `https` grants.
Six mediums, five hygiene items. Details below.

## H1 — subframe / injected script can use the top site's grants [fixed(pending build)]

- What: for `https` pages, site identity is the hostname string the page
  itself sends (`pgHost()` = its own `location.hostname`). The
  native-observed URL (`_topUrl` / `CallerUrlAsync`) is only consulted for
  `file://` binding. And the bridge is reachable from every frame — Windows
  injects `shell.js` document-wide incl. cross-origin iframes; Android's
  `HomeDeckNative` interface object exists in all frames (even though
  `shell.js` only loads top-level, raw calls still work).
- Impact (user POV): you grant `example.com` "Always". Its ad/tracker
  iframe — or one stored-XSS payload — calls `readFile` / `clipboardRead`
  / `notify` / `openApp` with `host="example.com"` and gets everything
  with zero prompts. Same for the pick sheet (keyed by supplied host) and
  `openApp` (checks the spoofed key). Forged poll-outbox entries work the
  same way on Windows (`file://` top pages are exempt — their key comes
  from the native URL, so spoofing buys nothing there).
- NOT affected: `file://` slots (native-bound + hash-bound); the prompt UI
  itself (native, unfakeable); the `""` blank-origin bucket (one-shot
  only, never persisted); picker reports (session-only + need arming).
- Fix options:
  1. Top-frame-only bridge: expose `HomeDeck` only when
     `window === window.top`. Kills the iframe vector. Cheap. Does NOT stop
     a top-frame XSS payload (nothing can — that *is* the site's code).
  2. Per-navigation secret token, delivered by top-frame-only eval after
     load, required as the first arg of every bridge call. Kills iframe +
     outbox-forgery vectors. More work (touches `shell.js`, both bridges,
     both handlers).
  3. Frame-attributed identity from native side — not available in
     WebView2 host objects or Android JS interfaces. Not viable.
  4. Bind https key + prompt name to the NATIVE-observed top host
     (`_topUrl` / `CallerUrlAsync`, already plumbed) instead of the
     page-supplied string. Kills spoofing AND prompt-identity lies in one
     move, cheaper than 2. Residual: subframes of a granted page still
     share its grant (same top host) — same residual as cookies-era
     mashups; option 1 remains the second layer for that.
- Recommendation: option 1 now (small, immediate), option 2 as the
  follow-up if the threat model includes hostile page content beyond
  iframes. Residual either way: top-frame XSS inherits the origin's
  grants by design (unfixable at this layer).
- User's reframing (2026-11-26): ambient third parties don't call these
  APIs (browsers don't have them), so the drive-by ad-iframe case is
  ~zero today. The realistic model is targeted: hostile content the user
  deliberately loads (imported homepage, HomeDeck-aware page), or a
  compromised widget inside a granted HomeDeck-aware page. BUT spoofing
  still matters in the targeted model — see prompt-identity note below.
- Source-is-public correction (2026-11-26): the repo is on GitHub, so the
  raw interface (`HomeDeckNative` + method names + outbox shape) is no
  secret — any iframe author who looks can bypass a `shell.js`-only
  top-frame guard with direct calls. That promotes option 2 from
  optional to must: only a per-navigation token (delivered top-frame-only,
  required on every call) closes the knowledgeable-iframe residual.
  Accepted batch: 4 (native-host key+prompt) + 1 (top-frame guard) + 2
  (token). Residual after all three: top-frame XSS only.
- Prompt-identity lie (part of H1): prompts display the page-supplied
  `host`. An iframe in `evil.com` can pass `host="bank.com"`, so the
  prompt reads "bank.com wants to…" and the grant lands under `bank.com`.
  The grant then works for the attacker (they keep passing the same
  string). Option 4 closes this: the prompt always names the true top
  page, and the key follows it.
- Files: `Web/shell.js` (`pgHost`, outbox), `Platforms/Windows/
  HomeDeckHostObject.cs` (`Guard`, `SiteKey` call sites),
  `Platforms/Windows/ShellWebViewHandler.Windows.cs` (doc-created
  injection, drain), `Platforms/Android/HomeDeckJsBridge.cs`,
  `Platforms/Android/ShellWebViewHandler.Android.cs` (interface + inject).

## Mediums [all open]

- M1 — blank-origin bucket shares one identity. Sandboxed iframes,
  `data:` pages, `about:blank` all report host `""` ("This page").
  Persistence is already off for these, but the prompt never says *which*
  page is asking. Fix: name the top host in the prompt, or deny `""`.
- M2 — pill never shows the real URL. After any link/redirect the pill
  keeps the profile name; nothing on screen says where you are. Fix: show
  current host in the pill; consider confirming cross-origin navigations.
- M3 — no top-level navigation policy. No `NavigationStarting` (Windows)
  / `shouldOverrideUrlLoading` (Android main client): pages can drive the
  top frame to `file:` / `intent:` / `data:` unconfirmed. With M2, silent.
  Fix: confirm or block non-http(s) top navigations.
- M4 — popup URLs accept any scheme. `RoutePopupAsync` offers same-tab /
  new-window for the raw URL; `javascript:` executes in the granted page
  after one prompt tap. Fix: only route `http(s)` (+ the `file` flow).
- M5 — `fileRoot()` needs no permission. Any page learns the full scoped
  path unprompted — on Windows it contains your username. Fix: gate like
  `listDir`, or return an opaque handle.
- M6 — brightness + vibrate need no permission. Any page can set
  brightness 0 (black-screen scare) with zero consent; battery/network/
  device are gated, brightness was missed (also needs `pgHost` threaded
  through `shell.js`). Fix: move into the Device group.

## Hygiene [all open]

- L1 — grants/profiles/prefs are unencrypted `Preferences`, and
  `allowBackup="true"` ships them to Google Drive backup. No app lock:
  unlocked device = full access. Consider backup-exclusion for
  `hd.grants`/`hd.profiles`, optional PIN.
- L2 — custom filter-list download has no size cap (`ReadAsStringAsync`
  unbounded on a user-pasted URL) → OOM. Cap ~10 MB (HTML rejection
  already exists).
- L3 — `openApp` has no rate limit: granted page can launch a picked app
  in a loop. Small cooldown.
- L4 — "read notifications" = up to 100 full notification bodies. By
  design; just know what the toggle means.
- L5 — dead surface: `wifiScan`/`bluetoothDevices` exist in `shell.js`
  *without* a host parameter and currently no-op. If ever implemented
  natively they'd be ungated by construction. Remove or thread host now.

## Checked and fine (do not re-open without new evidence)

- `file://` grant hash-binding + re-prompt on replace; scoped-root
  canonical checks; 8 MB read/write caps; 2000-entry `listDir` cap.
- Native prompt UI (unfakeable, fail-closed); `""`-host persistence off.
- Launch validation (`.exe` must exist + fully qualified; AUMID charset;
  Android package intents); no launch args cross the bridge.
- Picker reports: session-only, need arming, host/selector length caps.
- `IsUnderRoot` symlink/ToCTOU: no symlink primitive crosses the bridge;
  residual risk accepted.
- Profile-URL entry neutralizes schemes (`javascript:`/`data:` get an
  `https://` prefix and die harmlessly). Popup path (M4) is the exception.
- Adblock: matcher errors fail open intentionally; cosmetics bounded.

## Decision log (append-only)

- 2026-11-26: audit filed. H1 opened for discussion; everything else open.
- 2026-11-26: H1 refined per user — ambient drive-by risk ~zero (sites
  don't call unknown APIs); model is targeted content + compromised
  widgets. Added option 4 (native-host key+prompt binding) and the
  prompt-identity lie sub-finding. Options on the table: 1, 2, 4.
- 2026-11-26: user pointed out the source is public, so raw-interface
  bypass of a shell.js-only guard is trivially discoverable — option 2
  promoted to must. Accepted H1 batch: 4+1+2.
- 2026-11-26: H1 batch implemented as one commit (6 files): (4) native
  https-host binding in `SiteKey`/`EnsureAccessAsync`; (1) top-frame-only
  `HomeDeck` + outbox in `shell.js`; (2) per-navigation token minted in
  both handlers, delivered top-frame-only, required as first arg on all
  capability methods + outbox drain. `getRemoveSelectors`/`reportCancel`
  intentionally ungated (no capability). Residual: top-frame XSS only.
