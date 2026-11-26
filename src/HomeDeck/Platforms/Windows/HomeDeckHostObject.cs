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

    public HomeDeckHostObject(Action<string> post, AdBlockService? ads, Action? onCancel = null, Func<string?>? sourceProvider = null, Func<string?, string?, Task<string>>? appPickSheet = null, Func<string?, bool>? tokenOk = null) { _post = post; _ads = ads; _onCancel = onCancel ?? (() => { }); _source = sourceProvider; _appPickSheet = appPickSheet; _tokenOk = tokenOk; }

    /// <summary>File gate with the native-observed caller URL: file://
    /// pages are keyed by their own page file (hash-bound), so a
    /// replaced homepage re-prompts. Page-supplied host is only the
    /// fallback identity for non-file callers. Never throws.</summary>
    Task<bool> Guard(string? host, string op)
    {
        try { return HdFileGuard.EnsureAccessAsync(host, op, _source?.Invoke()); }
        catch { return Task.FromResult(false); }
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
                var sheet = _appPickSheet;
                if (sheet == null) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"no-sheet\"}")); return; }
                string json;
                try { json = await sheet(host, _source?.Invoke()) ?? "{\"ok\":false,\"error\":\"cancelled\"}"; }
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
                var key = HdFileGuard.SiteKey(host, _source?.Invoke(), out var hash, out _);
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
                var key = HdFileGuard.SiteKey(host, _source?.Invoke(), out var hash, out _);
                _post(JsonPair(id, AppInventory.PickedAppsJson(key, hash)));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Sync answer to page JS: remove-mode selectors for a host
    /// (JSON array). Called at page load by shell.js.</summary>
    public string getRemoveSelectors(string host)
    {
        try { return _ads?.GetRemoveSelectorsJson(host) ?? "[]"; }
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

    static string JsonPair(string id, string json)
        => "{\"id\":" + id + ",\"result\":" + json + "}";

    static string Q(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
