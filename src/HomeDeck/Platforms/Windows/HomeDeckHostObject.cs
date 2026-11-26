using System.Runtime.InteropServices;
using HomeDeck.Services;

namespace HomeDeck;

/// <summary>
/// Sync COM-visible surface for WebView2 AddHostObjectToScript.
/// All methods return JSON strings; async work posts back via
/// chrome.webview.postMessage — see Web/shell.js.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class HomeDeckHostObject
{
    readonly Action<string> _post;
    readonly AdBlockService? _ads;
    readonly Action _onCancel;
    readonly Func<string?>? _source;
    readonly Func<string?, string?, Task<string>>? _appPickSheet;
    readonly Func<string?, bool>? _tokenOk;

    /// <summary>Relay identity (audit relay): while set, Guard/SiteKey
    /// use this browser-stamped frame origin instead of any page-supplied
    /// host AND skip the native top-host override. Only the relay entry
    /// sets it, only with a valid nav token, only from an isTrusted
    /// frame message — so the value is trustworthy. AsyncLocal keeps
    /// concurrent relayed calls isolated. Never throws.</summary>
    readonly System.Threading.AsyncLocal<string?> _relayHost = new();

    public HomeDeckHostObject(Action<string> post, AdBlockService? ads, Action? onCancel = null, Func<string?>? sourceProvider = null, Func<string?, string?, Task<string>>? appPickSheet = null, Func<string?, bool>? tokenOk = null) { _post = post; _ads = ads; _onCancel = onCancel ?? (() => { }); _source = sourceProvider; _appPickSheet = appPickSheet; _tokenOk = tokenOk; }

    /// <summary>File gate with the native-observed caller URL: file://
    /// pages are keyed by their own page file (hash-bound), so a
    /// replaced homepage re-prompts. Page-supplied host is only the
    /// fallback identity for non-file callers. Relayed calls use the
    /// browser-stamped frame origin instead (top override skipped).
    /// Never throws.</summary>
    Task<bool> Guard(string? host, string op)
    {
        try
        {
            var rh = _relayHost.Value;
            if (rh != null) return HdFileGuard.EnsureAccessAsync(rh, op, null);
            return HdFileGuard.EnsureAccessAsync(host, op, _source?.Invoke());
        }
        catch { return Task.FromResult(false); }
    }

    /// <summary>Relay-aware site identity (see _relayHost). Never throws.</summary>
    string SiteKeyFor(string? host, out string? hash, out string display)
    {
        try
        {
            var rh = _relayHost.Value;
            if (rh != null) return HdFileGuard.SiteKey(rh, null, out hash, out display);
            return HdFileGuard.SiteKey(host, _source?.Invoke(), out hash, out display);
        }
        catch { hash = null; display = "This page"; return host ?? ""; }
    }

    /// <summary>Relay-aware host (see _relayHost). Never throws.</summary>
    string HostFor(string? host)
    {
        try { return _relayHost.Value ?? host ?? ""; } catch { return host ?? ""; }
    }

    /// <summary>Per-navigation token check (audit H1): every capability
    /// call carries the token native delivered top-frame-only. Null or
    /// stale = denied. Fail closed when unwired. Never throws.</summary>
    bool BadToken(string? tok)
    {
        try { var f = _tokenOk; return f == null || !f(tok); }
        catch { return true; }
    }

    void DenyToken(string id)
    {
        try { _post(JsonPair(id, "{\"ok\":false,\"error\":\"bad-token\"}")); } catch { }
    }

    string DenyTokenSync() => "{\"ok\":false,\"error\":\"bad-token\"}";

    /// <summary>User-picked app set: shows the native sheet (inventory
    /// stays native-side) and persists the site's picks. The site learns
    /// only picked IDs. Never throws; answers via postback.</summary>
    public void pickApps(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                var pk = SiteKeyFor(host, out _, out _);
                if (!HdFileGuard.RateOk(pk, "pickApps", true)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"rate-limited\"}")); return; }
                var sheet = _appPickSheet;
                if (sheet == null) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"no-sheet\"}")); return; }
                string json;
                try { json = await sheet(HostFor(host), _relayHost.Value != null ? null : _source?.Invoke()) ?? "{\"ok\":false,\"error\":\"cancelled\"}"; }
                catch (Exception e) { json = "{\"ok\":false,\"error\":" + Q(e.Message) + "}"; }
                _post(JsonPair(id, json));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Launch a picked app natively (real OS process). The id must
    /// be in the caller's picked set or this is denied — no arguments,
    /// no prompts at launch (the pick was the consent). Never throws.</summary>
    public void openApp(string id, string? tok, string? host, string? appId)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                var key = SiteKeyFor(host, out var hash, out _);
                if (!HdFileGuard.RateOk(key, "openApp", true)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"rate-limited\"}")); return; }
                appId = (appId ?? "").Trim();
                if (!HdFileGuard.IsAppPicked(key, hash, appId))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, await AppInventory.LaunchAsync(appId)));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>The site's own picked set with live running flags (one
    /// list for both — no separate task-list API). Unbound callers get
    /// an empty list. Never throws; answers via postback.</summary>
    public void pickedApps(string id, string? tok, string? host)
        => Task.Run(() =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                var key = SiteKeyFor(host, out var hash, out _);
                _post(JsonPair(id, AppInventory.PickedAppsJson(key, hash)));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Sync answer to page JS: remove-mode selectors for a host
    /// (JSON array). Called at page load by shell.js.</summary>
    public string getRemoveSelectors(string host)
    {
        try { return _ads?.GetRemoveSelectorsJson(HostFor(host)) ?? "[]"; }
        catch { return "[]"; }
    }

    /// <summary>Frame Esc exit: any frame (top or iframe) ends pick mode
    /// for the whole shell. Fire-and-forget.</summary>
    public void reportCancel()
    {
        try { _onCancel(); } catch { }
    }

    public void getBattery(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read device info")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, DeviceBridge.GetBattery()));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void getNetwork(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read device info")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, DeviceBridge.GetNetwork()));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void getDevice(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read device info")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, DeviceBridge.GetDevice()));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });
    /// <summary>Audit M6: vibration is a Device-group capability (gated
    /// like battery/network — consistency for all). Never throws.</summary>
    public void vibrate(string id, string? tok, string? host, int millis)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "use vibration")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, DeviceBridge.Vibrate(millis)));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void bleScan(string id, string? tok, string? host, int seconds)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "scan nearby devices")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                var seen = new System.Collections.Concurrent.ConcurrentDictionary<ulong, (string name, int rssi)>();
                var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode.Active
                };
                watcher.Received += (_, e) =>
                {
                    try
                    {
                        string name = "";
                        try { name = e.Advertisement.LocalName ?? ""; } catch { }
                        seen[e.BluetoothAddress] = (name, e.RawSignalStrengthInDBm);
                    }
                    catch { }
                };
                try { watcher.Start(); }
                catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); return; }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)));
                try { watcher.Stop(); } catch { }
                var list = new System.Collections.Generic.List<object>();
                foreach (var kv in seen)
                    list.Add(new { address = kv.Key.ToString("X"), name = kv.Value.name, rssi = kv.Value.rssi });
                _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = list })));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Audit L5: parity stubs — host is threaded like every other
    /// capability, so a future implementation inherits the gate shape. When
    /// implementing for real, add Guard(host, "scan nearby devices") first
    /// (like bleScan) — without it this would be ungated by construction.
    /// Until then: honest not-implemented, no phantom prompt. Never throws.</summary>
    public void wifiScan(string id, string? tok, string? host)
        => Task.Run(() =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                _post(JsonPair(id, DeviceBridge.NotImplemented("wifiScan")));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void bluetoothDevices(string id, string? tok, string? host, int _)
        => Task.Run(() =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                _post(JsonPair(id, DeviceBridge.NotImplemented("bluetoothDevices")));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Read access to toasts via UserNotificationListener. First
    /// call prompts the user (RequestAccessAsync, UI thread); the grant
    /// lives in Settings → Privacy → Notifications.</summary>
    public void getNotifications(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read notifications")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                var status = await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(
                    () => Windows.UI.Notifications.Management.UserNotificationListener.Current.RequestAccessAsync().AsTask());
                if (status != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                var notes = await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
                var list = new System.Collections.Generic.List<object>();
                foreach (var n in notes)
                {
                    if (list.Count >= 100) break;
                    string app = "", title = "", text = "";
                    try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                    try
                    {
                        var binding = n.Notification?.Visual?.GetBinding(Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                        if (binding != null)
                        {
                            var seq = binding.GetTextElements();
                            var parts = new System.Collections.Generic.List<string>();
                            foreach (var t in seq) { try { if (!string.IsNullOrEmpty(t?.Text)) parts.Add(t.Text); } catch { } }
                            if (parts.Count > 0) title = parts[0].Length > 200 ? parts[0][..200] : parts[0];
                            if (parts.Count > 1) text = string.Join(" ", parts.GetRange(1, parts.Count - 1)).Trim();
                            if (text.Length > 500) text = text[..500];
                        }
                    }
                    catch { }
                    string time = "";
                    try { time = n.CreationTime.UtcDateTime.ToString("o"); } catch { time = DateTime.UtcNow.ToString("o"); }
                    list.Add(new { app, title, text, time });
                }
                _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = list })));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public string notificationAccess(string? tok)
    {
        try
        {
            if (BadToken(tok)) return DenyTokenSync();
            var s = Windows.UI.Notifications.Management.UserNotificationListener.Current.GetAccessStatus();
            return "{\"ok\":true,\"data\":{\"enabled\":" + (s == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed ? "true" : "false") + "}}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + Q(e.Message) + "}"; }
    }

    public void openNotificationSettings(string id, string? tok)
        => Task.Run(() =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:privacy-notifications",
                    UseShellExecute = true
                });
                _post(JsonPair(id, "{\"ok\":true}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>System settings deep-links: the app cannot flip radios
    /// itself, so these open the OS page where the user flips the real
    /// toggle. Same delegation pattern as openNotificationSettings.</summary>
    public void openWifiSettings(string id, string? tok)
        => OpenSettingsPage(id, tok, "ms-settings:network-wifi");

    public void openBluetoothSettings(string id, string? tok)
        => OpenSettingsPage(id, tok, "ms-settings:bluetooth");

    void OpenSettingsPage(string id, string? tok, string uri)
        => Task.Run(() =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                // Audit L3x: settings deep-links have no host param — identity
                // comes from the native-observed top URL (relay-aware).
                var sk = HdFileGuard.SiteKey(HostFor(""), _relayHost.Value != null ? null : _source?.Invoke(), out _, out _);
                if (!HdFileGuard.RateOk(sk, "openSettings", true)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"rate-limited\"}")); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
                _post(JsonPair(id, "{\"ok\":true}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Windows brightness: WMI for internal displays, DDC/CI for
    /// external. Per-machine — desktops with basic monitors answer to
    /// neither API and get an honest not-implemented. Audit M6: Device
    /// group, gated like the rest. Never throws.</summary>
    public void setBrightness(string id, string? tok, string? host, float level)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "set brightness")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                int v = (int)(Math.Clamp(level, 0f, 1f) * 100);
                int applied = 0;
                using var searcher = new System.Management.ManagementObjectSearcher(
                    new System.Management.ManagementScope(@"\\.\root\wmi"),
                    new System.Management.ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
                foreach (System.Management.ManagementObject m in searcher.Get())
                {
                    try { m.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (uint)v }); applied++; }
                    catch { }
                }
                _post(JsonPair(id, applied == 0 ? DeviceBridge.NotImplemented("brightness") : "{\"ok\":true}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void getBrightness(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read brightness")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                using var searcher = new System.Management.ManagementObjectSearcher(
                    new System.Management.ManagementScope(@"\\.\root\wmi"),
                    new System.Management.ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
                foreach (System.Management.ManagementObject m in searcher.Get())
                {
                    try
                    {
                        var cur = m["CurrentBrightness"];
                        byte b = cur is byte[] arr && arr.Length > 0 ? arr[0] : Convert.ToByte(cur);
                        _post(JsonPair(id, "{\"ok\":true,\"data\":{\"level\":" + (b / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}"));
                        return;
                    }
                    catch { }
                }
                _post(JsonPair(id, DeviceBridge.NotImplemented("brightness")));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Scoped file access: every op below is gated by HdFileGuard —
    /// the path must sit under the app's HomeDeck root AND the calling site
    /// must hold a grant (native allow/deny popup on first use).
    /// Audit M5: fileRoot is split by caller — file-slot pages get the
    /// real path behind the same Guard gate as listDir (user's own page,
    /// trusted after prompt); https/blank callers get a blocked answer
    /// with no prompt (they never need the raw path; the Windows path
    /// carries the username). The block is intentional, not a bug.</summary>
    public void fileRoot(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                var key = HdFileGuard.SiteKey(host, _source?.Invoke(), out _, out _);
                if (!key.StartsWith("file|", StringComparison.Ordinal))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"blocked\"}")); return; }
                if (!await Guard(host, "list files"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, "{\"ok\":true,\"data\":{\"root\":" + Q(HdFileGuard.Root) + "}}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void listDir(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "list files"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                if (string.IsNullOrWhiteSpace(path))
                {
                    string root = HdFileGuard.Root;
                    _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ok = true,
                        data = new[] { new { name = "HomeDeck", path = root, isDir = true } }
                    })));
                    return;
                }
                if (!HdFileGuard.IsUnderRoot(path, out var full) || !System.IO.Directory.Exists(full))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"no-dir\"}")); return; }
                var list = new System.Collections.Generic.List<object>();
                int n = 0;
                foreach (var e in System.IO.Directory.EnumerateFileSystemEntries(full))
                {
                    if (n++ >= 2000) break;
                    try
                    {
                        bool isDir = System.IO.Directory.Exists(e);
                        long size = -1;
                        if (!isDir) { try { size = new System.IO.FileInfo(e).Length; } catch { continue; } }
                        list.Add(new { name = System.IO.Path.GetFileName(e) ?? e, path = e, isDir, size });
                    }
                    catch { }
                }
                _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = list })));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void fileInfo(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read file info"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                if (!HdFileGuard.IsUnderRoot(path, out var full))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"outside-root\"}")); return; }
                if (System.IO.Directory.Exists(full))
                { _post(JsonPair(id, "{\"ok\":true,\"data\":{\"name\":" + Q(System.IO.Path.GetFileName(full)) + ",\"isDir\":true,\"size\":-1}}")); return; }
                if (!System.IO.File.Exists(full)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"not-found\"}")); return; }
                var fi = new System.IO.FileInfo(full);
                _post(JsonPair(id, "{\"ok\":true,\"data\":{\"name\":" + Q(fi.Name) + ",\"isDir\":false,\"size\":" + fi.Length + "}}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void readFile(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read files"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                if (!HdFileGuard.IsUnderRoot(path, out var full) || !System.IO.File.Exists(full))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"not-found\"}")); return; }
                var fi = new System.IO.FileInfo(full);
                if (fi.Length > 8L * 1024 * 1024) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"too-big\"}")); return; }
                byte[] bytes = System.IO.File.ReadAllBytes(full);
                _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    data = new { name = fi.Name, size = (long)bytes.Length, base64 = Convert.ToBase64String(bytes) }
                })));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void writeFile(string id, string? tok, string? host, string? path, string? base64)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "write files"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                if (!HdFileGuard.IsUnderRoot(path, out var full))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"outside-root\"}")); return; }
                if (string.IsNullOrEmpty(base64) || base64.Length > 12L * 1024 * 1024)
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"too-big\"}")); return; }
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch { _post(JsonPair(id, "{\"ok\":false,\"error\":\"bad-base64\"}")); return; }
                if (bytes.Length > 8L * 1024 * 1024) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"too-big\"}")); return; }
                try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full) ?? HdFileGuard.Root); }
                catch { _post(JsonPair(id, "{\"ok\":false,\"error\":\"no-dir\"}")); return; }
                System.IO.File.WriteAllBytes(full, bytes);
                _post(JsonPair(id, "{\"ok\":true,\"data\":{\"size\":" + bytes.Length + "}}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void mkdir(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "create folders"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                if (!HdFileGuard.IsUnderRoot(path, out var full))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"outside-root\"}")); return; }
                System.IO.Directory.CreateDirectory(full);
                _post(JsonPair(id, "{\"ok\":true}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void deleteFile(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "delete files"))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                if (!HdFileGuard.IsUnderRoot(path, out var full))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"outside-root\"}")); return; }
                if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
                else if (System.IO.Directory.Exists(full)) System.IO.Directory.Delete(full, false);
                else { _post(JsonPair(id, "{\"ok\":false,\"error\":\"not-found\"}")); return; }
                _post(JsonPair(id, "{\"ok\":true}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void shareText(string id, string? tok, string? text, string? title)
        => Task.Run(async () =>
        {
            try { if (BadToken(tok)) { DenyToken(id); return; } } catch { DenyToken(id); return; }
            // Audit L3x: shareText has no host param — identity comes from the
            // native-observed top URL (relay-aware, like pickApps above).
            var sk = HdFileGuard.SiteKey(HostFor(""), _relayHost.Value != null ? null : _source?.Invoke(), out _, out _);
            if (!HdFileGuard.RateOk(sk, "shareText", true)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"rate-limited\"}")); return; }
            try { _post(JsonPair(id, await DeviceBridge.ShareTextAsync(text, title))); } catch { }
        });

    public void clipboardWrite(string id, string? tok, string? host, string? text)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "write the clipboard")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, await DeviceBridge.ClipboardWriteAsync(text)));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void clipboardRead(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "read the clipboard")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                _post(JsonPair(id, await DeviceBridge.ClipboardReadAsync()));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void notify(string id, string? tok, string? host, string? title, string? body)
        => Task.Run(async () =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (!await Guard(host, "send notifications")) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}")); return; }
                var center = Plugin.LocalNotification.LocalNotificationCenter.Current;
                if (!await center.RequestNotificationPermission())
                    _post(JsonPair(id, "{\"ok\":false,\"error\":\"denied\"}"));
                else
                {
                    await center.Show(new Plugin.LocalNotification.NotificationRequest
                    {
                        NotificationId = Random.Shared.Next(1, int.MaxValue),
                        Title = title ?? "Home Deck",
                        Description = body ?? ""
                    });
                    _post(JsonPair(id, "{\"ok\":true}"));
                }
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"" + e.Message + "\"}")); }
        });

    /// <summary>Subframe relay (audit relay): invoked ONLY by the top
    /// frame's shell.js listener (isTrusted frame message) with the nav
    /// token this closure holds. origin is browser-stamped and becomes
    /// the grant identity for this call (top-host override skipped via
    /// _relayHost); method allowlist excludes relay itself. Answers via
    /// the method's own postback under the same id. Never throws.</summary>
    public void relay(string id, string? tok, string? origin, string? method, string? argsJson)
        => Task.Run(() =>
        {
            try
            {
                if (BadToken(tok)) { DenyToken(id); return; }
                if (string.IsNullOrWhiteSpace(method) || method.Equals("relay", StringComparison.OrdinalIgnoreCase))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"bad-method\"}")); return; }
                System.Reflection.MethodInfo? mi = null;
                try { mi = typeof(HomeDeckHostObject).GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase); }
                catch { }
                if (mi == null) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q("no-method: " + method) + "}")); return; }
                var ps = mi.GetParameters();
                bool noArgs = ps.Length == 0;
                bool syncString = mi.ReturnType == typeof(string);
                bool asyncVoid = mi.ReturnType == typeof(void) && ps.Length >= 2 && ps[0].Name == "id" && ps[0].ParameterType == typeof(string)
                    && ps[1].Name == "tok" && ps[1].ParameterType == typeof(string);
                if (!noArgs && !asyncVoid && !(syncString && ps.Length >= 1 && ps[0].Name == "tok" && ps[0].ParameterType == typeof(string)))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"bad-signature\"}")); return; }
                string rhost;
                try
                {
                    if (string.IsNullOrEmpty(origin) || origin == "null") rhost = "";
                    else rhost = new Uri(origin).Host?.Trim().ToLowerInvariant() ?? "";
                }
                catch { rhost = ""; }
                System.Text.Json.JsonElement[] rargs;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(argsJson) ? "[]" : argsJson);
                    rargs = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? doc.RootElement.EnumerateArray().ToArray() : Array.Empty<System.Text.Json.JsonElement>();
                }
                catch { rargs = Array.Empty<System.Text.Json.JsonElement>(); }
                _relayHost.Value = rhost;
                try
                {
                    if (noArgs) { mi.Invoke(this, null); _post(JsonPair(id, "{\"ok\":true}")); return; }
                    if (syncString)
                    {
                        var margs = new object?[ps.Length];
                        margs[0] = tok;
                        for (int i = 1; i < ps.Length; i++)
                            margs[i] = CoerceRelay(ps[i].ParameterType, i - 1 < rargs.Length ? rargs[i - 1] : default);
                        var r = (string?)mi.Invoke(this, margs);
                        _post(JsonPair(id, string.IsNullOrEmpty(r) ? "{\"ok\":false,\"error\":\"empty\"}" : r));
                        return;
                    }
                    var vargs = new object?[ps.Length];
                    vargs[0] = id; vargs[1] = tok;
                    for (int i = 2; i < ps.Length; i++)
                        vargs[i] = CoerceRelay(ps[i].ParameterType, i - 2 < rargs.Length ? rargs[i - 2] : default);
                    mi.Invoke(this, vargs); // async results arrive via the method's own postback under the same id
                }
                finally { _relayHost.Value = null; }
            }
            catch (Exception e)
            {
                var inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(TruncRelay(inner.Message)) + "}"));
            }
        });

    /// <summary>JSON value to a bridge parameter type (covers the whole
    /// surface: string/int/float/bool). Never throws.</summary>
    static object? CoerceRelay(Type t, System.Text.Json.JsonElement el)
    {
        try
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.Undefined || el.ValueKind == System.Text.Json.JsonValueKind.Null)
                return t == typeof(string) ? null : (t.IsValueType ? Activator.CreateInstance(t) : null);
            if (t == typeof(string)) return el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : el.ToString();
            if (t == typeof(int))
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
                if (el.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(el.GetString(), out n)) return n;
                return 0;
            }
            if (t == typeof(float))
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetSingle(out var x)) return x;
                if (el.ValueKind == System.Text.Json.JsonValueKind.String && float.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) return x;
                return 0f;
            }
            if (t == typeof(bool))
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (el.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                if (el.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
                return false;
            }
        }
        catch { }
        try { return t.IsValueType ? Activator.CreateInstance(t) : null; } catch { return null; }
    }

    static string TruncRelay(string? s, int n = 160)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    static string JsonPair(string id, string json)
        => "{\"id\":" + id + ",\"result\":" + json + "}";

    static string Q(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
