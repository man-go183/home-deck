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

    public HomeDeckHostObject(Action<string> post, AdBlockService? ads, Action? onCancel = null) { _post = post; _ads = ads; _onCancel = onCancel ?? (() => { }); }

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

    public string getBattery() => DeviceBridge.GetBattery();
    public string getNetwork() => DeviceBridge.GetNetwork();
    public string getDevice() => DeviceBridge.GetDevice();
    public string vibrate(int millis) => DeviceBridge.Vibrate(millis);

    public void getLocation(string id)
        => Task.Run(async () => _post(JsonPair(id, await DeviceBridge.GetLocationAsync())));

    public void bleScan(string id, int seconds)
        => Task.Run(async () =>
        {
            try
            {
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
    public void getNotifications(string id)
        => Task.Run(async () =>
        {
            try
            {
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

    public string notificationAccess()
    {
        try
        {
            var s = Windows.UI.Notifications.Management.UserNotificationListener.Current.GetAccessStatus();
            return "{\"ok\":true,\"data\":{\"enabled\":" + (s == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed ? "true" : "false") + "}}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + Q(e.Message) + "}"; }
    }

    public void openNotificationSettings(string id)
        => Task.Run(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:privacy-notifications",
                    UseShellExecute = true
                });
                _post(JsonPair(id, "{\"ok\":true}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    /// <summary>Windows brightness: WMI for internal displays, DDC/CI for
    /// external. Per-machine — desktops with basic monitors answer to
    /// neither API and get an honest not-implemented.</summary>
    public string setBrightness(float level)
    {
        try
        {
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
            if (applied == 0) return DeviceBridge.NotImplemented("brightness");
            return "{\"ok\":true}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + Q(e.Message) + "}"; }
    }

    public string getBrightness()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"\\.\root\wmi"),
                new System.Management.ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (System.Management.ManagementObject m in searcher.Get())
            {
                try
                {
                    var cur = m["CurrentBrightness"];
                    byte b = cur is byte[] arr && arr.Length > 0 ? arr[0] : Convert.ToByte(cur);
                    return "{\"ok\":true,\"data\":{\"level\":" + (b / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
                }
                catch { }
            }
            return DeviceBridge.NotImplemented("brightness");
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + Q(e.Message) + "}"; }
    }

    public void listDir(string id, string? path)
        => Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var roots = System.IO.DriveInfo.GetDrives();
                    var items = new System.Collections.Generic.List<object>();
                    foreach (var d in roots)
                    {
                        try { items.Add(new { name = d.Name, path = d.RootDirectory.FullName, isDir = true }); }
                        catch { }
                    }
                    _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = items })));
                    return;
                }
                if (!System.IO.Directory.Exists(path)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"no-dir\"}")); return; }
                var list = new System.Collections.Generic.List<object>();
                int n = 0;
                foreach (var e in System.IO.Directory.EnumerateFileSystemEntries(path))
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

    public void fileInfo(string id, string? path)
        => Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"no-path\"}")); return; }
                if (System.IO.Directory.Exists(path))
                { _post(JsonPair(id, "{\"ok\":true,\"data\":{\"name\":\"" + System.IO.Path.GetFileName(path) + "\",\"isDir\":true,\"size\":-1}}")); return; }
                if (!System.IO.File.Exists(path)) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"not-found\"}")); return; }
                var fi = new System.IO.FileInfo(path);
                _post(JsonPair(id, "{\"ok\":true,\"data\":{\"name\":\"" + fi.Name + "\",\"isDir\":false,\"size\":" + fi.Length + "}}"));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void readFile(string id, string? path)
        => Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                { _post(JsonPair(id, "{\"ok\":false,\"error\":\"not-found\"}")); return; }
                var fi = new System.IO.FileInfo(path);
                if (fi.Length > 8L * 1024 * 1024) { _post(JsonPair(id, "{\"ok\":false,\"error\":\"too-big\"}")); return; }
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                _post(JsonPair(id, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    data = new { name = fi.Name, size = (long)bytes.Length, base64 = Convert.ToBase64String(bytes) }
                })));
            }
            catch (Exception e) { _post(JsonPair(id, "{\"ok\":false,\"error\":" + Q(e.Message) + "}")); }
        });

    public void shareText(string id, string? text, string? title)
        => Task.Run(async () => _post(JsonPair(id, await DeviceBridge.ShareTextAsync(text, title))));

    public void clipboardWrite(string id, string? text)
        => Task.Run(async () => _post(JsonPair(id, await DeviceBridge.ClipboardWriteAsync(text))));

    public void clipboardRead(string id)
        => Task.Run(async () => _post(JsonPair(id, await DeviceBridge.ClipboardReadAsync())));

    public void notify(string id, string? title, string? body)
        => Task.Run(async () =>
        {
            try
            {
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
