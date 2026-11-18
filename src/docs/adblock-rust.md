# Home Deck — real ad-block engine spec (Brave's `adblock-rust`)

Status: PROPOSED — no code changes until approved.
Order: this first, title-bar header second.

## 0. What was wrong

`AdBlockService` currently matches with a hand-written C# ABP *subset*
(`AbpEngine`/`Rule`, ~130 lines). It silently drops every rule whose syntax
it doesn't implement, ignores all cosmetic rules, and guesses resource types
from file extensions. Against the real EasyList + EasyPrivacy that means
trackers walk through while the toggle glows gold. This spec replaces the
matcher with Brave's actual engine. The subset matcher is deleted, not kept
as a fallback.

## 1. Goal

Per-profile ad & tracker blocking enforced at the WebView request layer on
Windows and Android, using:

- Engine: **Brave `adblock-rust`, crate `adblock`, pinned to 0.13.6**
  (latest stable as of 2026-11-18; repo `brave/adblock-rust`).
  Pinned via `Cargo.lock` committed in-repo — a future 0.14 with API drift
  cannot silently change the build.
- Lists (unchanged): EasyList, EasyPrivacy, Brave Unbreak — same three URLs
  as today, same 7-day on-device cache.
- Full ABP syntax + cosmetic (element-hide) filtering, not just network rules.

Non-goals: scriptlet injection (phase 2), iOS (no iOS target), changing the
toggle/shield UI (header work covers the badge separately).

## 2. Architecture (what stays, what changes)

KEPT as-is:

- `AdBlockService` list download + 7-day file cache + `IFilterEngine`
  interface shape (`ShouldBlock(url, pageHost, resourceType)`) + fail-open
  behavior (any error → allow the request, never break a page).
- Enforcement hooks: Android `ShouldInterceptRequest` → 403 empty response;
  Windows WebView2 `WebResourceRequested` override. Only the verdict source
  changes, not the plumbing.
- Per-profile `AdBlockEnabled` gating.

CHANGED:

- `AbpEngine`/`Rule` (~130 lines) deleted. New `RustFilterEngine :
  IFilterEngine` backed by P/Invoke into a native library.
- Resource-type mapping extended from extension-guessing to the engine's
  request types (script, image, stylesheet, fetch/xhr, subdocument, font,
  media, websocket, other; main-frame/document never blocked).
- Cosmetic filtering added: on navigation complete, query the engine for the
  page host's element-hide rules and inject them (extends the existing
  `shell.js` bootstrap, which is already injected on every navigation).

## 3. Native wrapper (`src/native/hdblock/` — new, committed, rebuildable)

A tiny Rust crate (not the engine itself — it *depends on* `adblock 0.13.6`)
exposing a C ABI the C# side P/Invokes. Minimal surface:

- `hdblock_new() -> handle` — create an empty engine.
- `hdblock_add_rules(handle, rules_ptr, rules_len)` — feed concatenated
  filter-list text (UTF-8). Returns count of parsed rules (for logging).
- `hdblock_should_block(handle, url, host, third_party, req_type) -> int`
  — 1 = block, 0 = allow. `req_type` is a small int enum mirroring the
  engine's request types.
- `hdblock_cosmetic_css(handle, host) -> char*` — newline-joined element-hide
  CSS for the host (caller frees via `hdblock_free_string`).
- `hdblock_destroy(handle)`.

Threading: matching happens on WebView threads. The wrapper guards the
engine handle with a mutex (matching is microseconds; contention is nil).
No callbacks into managed code, no async across the boundary — every call
is synchronous and infallible (errors return "allow").

Build matrix (all in CI, manual runs only as today):

| Target | Runner | Toolchain | Output |
|---|---|---|---|
| Windows x64 | `windows-latest` | `rustup` + `x86_64-pc-windows-msvc` (host target, no cross) | `hdblock.dll` beside the unpackaged exe |
| Android arm64 | `ubuntu-latest` | `rustup` + `aarch64-linux-android`, NDK clang as linker (`ANDROID_NDK_HOME` is preinstalled on the runner) | `libhdblock.so` in `arm64-v8a` |
| Android x64 (emulator) | same | `x86_64-linux-android` target | `libhdblock.so` in `x86_64` |

armeabi-v7a (32-bit ARM) is OUT of scope: arm64 covers all devices shipped
in the last ~6 years. If a v7a device ever matters, the app fail-opens
(blocking off, everything loads) — never crashes. The C# side treats a
missing native library as "not ready," same as lists-not-downloaded.

`Cargo.toml` + `Cargo.lock` are committed; the Rust toolchain version is
pinned in the workflow (e.g. `rustup toolchain install 1.8x` — exact
version fixed at implementation time to whatever is stable then).

## 4. C# integration

- `RustFilterEngine : IFilterEngine`, constructed once in the
  `AdBlockService` singleton after lists load; `DllImport("hdblock")`
  (Windows resolves `hdblock.dll` next to the exe; Android resolves
  `libhdblock.so` from the APK's lib dir).
- `EnsureLoadedAsync` unchanged apart from feeding the concatenated rules
  text into `hdblock_add_rules` instead of `new AbpEngine(...)`.
- If the native library is absent or any call throws: fail open + log count.
  Blocking silently degrading to OFF is specified behavior, never a crash.
- Cosmetic: `BrowserPage` (both platforms) after navigation completes calls
  `GetCosmeticCss(pageHost)` and injects it before/after `shell.js`.
  Scriptlets (`+js(...)`) are explicitly NOT executed in phase 1 — logged
  and skipped.

## 5. Filter-list management (unchanged, restated for the record)

Same three URLs, same 7-day refresh, same `AppDataDirectory` cache files.
No bundled snapshot in v1 (keeps the APK/exe small; first launch fetches on
network — blocking is off until the first successful load, which is the
existing `Ready` behavior). Phase 2 (explicitly deferred, 2026-11-18):
bundled list snapshot for offline first-run, refresh-policy tuning,
manual "update lists" UI with per-list ages, custom user-added list URLs.

## 6. Verification (the proof — part of the deliverable, not an afterthought)

1. **Rust-side vectors (run in CI):** `cargo test` with pinned
   block/allow/cosmetic vectors covering anchors, exceptions (`@@`),
   `$third-party`, `$domain=`, and all resource types. Fails the build if
   the engine binding misbehaves.
2. **Request counts on-device:** debug counter (logcat / debug output) of
   blocked vs allowed requests per page. Test pages: 2–3 heavy real-world
   pages + one controlled page exercising `@@` exceptions.
3. **No-break checks:** with blocking ON, first-party content, logins, and
   video on the test pages must work; `@@` exception rules must visibly
   allow their targets; toggle OFF must load everything (baseline).
4. **Leak check:** a page with a known tracker that the subset matcher
   missed must now be blocked (proves the engine swap did something real).
5. **Both artifacts green:** `windows-exe` + `android-apk` publish, native
   libs present in outputs (zip listing / APK lib listing inspected).

Acceptance: all five pass. "Toggle looks right" is not acceptance.

## 7. CI cost (stated upfront — free minutes are finite)

Added per Android run: rustup + 2 cross targets (~1–2 min, partly
cacheable), NDK link (preinstalled, ~0), Rust release build of a small
crate (~3–6 min cold). Added per Windows run: rustup + host build (~2–4
min). Mitigation: `Swatinem/rust-cache` (or equivalent) to reuse the Rust
target dir between runs; workflows stay `workflow_dispatch`-only so nothing
runs uninvited. Estimated +5–8 min per platform run versus today.

## 8. Risks

- `adblock` 0.14 API drift → neutralized by the pin + lockfile.
- NDK path changes on runner images → build reads `ANDROID_NDK_HOME`, fails
  loudly at the setup step (fast fail, cheap) rather than mid-compile.
- `.so`/`.dll` size (~2–4 MB each) → acceptable inside a 64 MB app.
- No iOS → nothing to do; if iOS is ever added, content-blocker JSON export
  from the same engine is the path (not in scope).

## 9. Rollout (code changes, for approval next)

1. Add `src/native/hdblock/` (Cargo project + lockfile).
2. Add `RustFilterEngine`, wire into `AdBlockService`, extend resource-type
   mapping, add cosmetic injection to both platform handlers.
3. Delete `AbpEngine`/`Rule`.
4. Workflow edits: Rust setup + native builds on both files; native outputs
   packaged (exe dir / APK lib dirs).
5. Run verification §6; report counts, not vibes.
