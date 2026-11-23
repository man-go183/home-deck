# Home Deck — MAUI shell (v0.1.0)

Thin native shell around Perchance pages. One profile = one page in a WebView.
Nothing else. Any Perchance page is compatible by definition.

Targets: **Android** (system WebView) + **Windows** (WebView2). No Electron, no Blazor.
iOS has no sideload story — out of scope for v1.

## Repo layout

- `src/HomeDeck/` — the MAUI project (`net8.0-android`, `net8.0-windows10.0.19041.0`).
  - `Models/` — `Profile` (`id, name, url, colorHex, adBlock`).
  - `Services/` — `ProfileStore` (Preferences-backed), `AdBlockService` (filter lists + matcher), `DeviceBridge` (JSON answers for the JS bridge).
  - `Controls/` — `ShellWebView` (profile-bound WebView).
  - `Handlers/` — shared partial; real behavior in `Platforms/Android|Windows/`.
  - `Views/` — `LauncherPage` (profile grid, ask-every-time, help), `BrowserPage` (shell bar, pill + switcher dropdown, per-profile blocker toggle, splash).
  - `Web/shell.js` — the `window.HomeDeck` contract (`hd/1`), injected into every page.
  - `profiles.default.json` — first-run seed (this homepage).
- `.github/workflows/` — `android.yml`, `windows.yml`. **Manual only** (`workflow_dispatch`): nothing builds on push.

## Mockup → code map

The approved mockup (profile cards, flip-to-edit, avatars, header pill + dropdown,
help + Advanced device list, per-profile shield) is implemented natively here,
not pixel-identical but behavior-identical: card grid + Edit sheet (rename / link /
ad-toggle / delete), ask-every-time checkbox, header pill with avatar + name +
gold shield badge (bottom-right of avatar, filled + dark outline) while the open
profile blocks, dropdown (other profiles → Manage → separator → blocker toggle),
launch splash, help dialog.

Deviations from the mockup (v1 scope): avatars are color + initial (no photo
upload / gradients yet); the launcher "?" is a text dialog; no snapshot-boot
(WebView HTTP cache + instant last-profile reopen instead).

## The JS bridge (hd/1)

Pages feature-detect: `if (window.HomeDeck) …`. Every call resolves
`{ok:true, data?…}` or `{ok:false, error}`. `not-implemented: x` = surface
exists, capability lands later.

| call | v1 status |
|---|---|
| `getBattery()` | ✅ level / state / source |
| `getNetwork()` | ✅ access + connection profiles |
| `getDevice()` | ✅ model / OS / bridge version |
| `vibrate(ms)` | ✅ |
| `shareText(text, title)` | ✅ system sheet |
| `clipboardWrite/read()` | ✅ |
| `notify(title, body)` | ✅ local, permission-gated (Android 13+ / iOS-style prompt) |
| `setBrightness(level)` | ✅ Android app-level only (restores on exit) |
| `getLocation()` | ✅ one-shot lat/lon/accuracy (runtime permission) |
| `bleScan(seconds)` | ✅ name/RSSI scan-only list (runtime permission; Win needs MSIX) |
| `getNotifications()` | ✅ read access (Android listener opt-in; Win UNL grant, MSIX) |
| `notificationAccess()` / `openNotificationSettings()` | ✅ enabled-check + settings deep-link |
| `getBrightness()` / `setBrightness(level)` | ✅ Android in-app window; Win WMI per-machine (fail-soft) |
| `listDir(path?)` / `fileInfo(path)` / `readFile(path)` | ✅ roots→listing→base64 (8 MB cap); page renders the browser UI |

`homedeck:ready` fires on `window` after injection.

## Ad blocking (Brave-style)

Same lists Brave uses — EasyList, EasyPrivacy, Brave Unbreak — downloaded on
first run, cached in app storage, refreshed when older than 7 days.
Enforcement is per-profile (`profile.adBlock`), at the request layer:
Android `ShouldInterceptRequest`, Windows WebView2 `WebResourceRequested`.
Matches return an empty 403; matcher errors fail open (page never breaks).

The v1 matcher (`AdBlockService.cs`) is C# covering the ABP subset those lists
rely on (`||domain^`, `|prefix`, `*`, `@@` exceptions, `$third-party`,
`$domain=`, resource types). Cosmetic rules are accepted and ignored.
**Upgrade path:** implement `IFilterEngine` with `adblock-rust` compiled as a
cdylib per ABI and DllImport it — no other file changes.

## Build

Local (needs .NET 8 SDK + MAUI workloads):
`dotnet build src/HomeDeck -f net8.0-android` /
`dotnet publish src/HomeDeck -f net8.0-windows10.0.19041.0 -c Release -p:WindowsPackageType=None`

CI: Actions tab → run `android-apk` (Debug = installable, signed with debug key;
use Release + your own keystore for anything public) or `windows-exe`
(unpackaged folder, zipped). Artifacts download from the run.

No secrets, no API keys, no backend. Release signing (Android keystore) is
deliberately not in CI yet — add it when the app goes public.

## v1 explicitly out

Snapshot-boot images, offline banner, file pickers beyond WebView defaults,
tray/autostart, SecureStorage tokens, camera/mic bridge methods,
avatar photo upload, icon/splash art (placeholder PNGs generated in-repo;
replace `Platforms/Windows/Assets/` + add `MauiIcon`/`MauiSplashScreen` items).
