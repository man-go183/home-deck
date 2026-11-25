using Android.Webkit;
using HomeDeck.Controls;
using HomeDeck.Handlers;
using HomeDeck.Services;
using Java.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace HomeDeck;

/// <summary>
/// Synchronous JS-callable surface (JS interfaces must return primitives —
/// always JSON strings). Async work uses the callback pattern: native calls
/// window.__hdResolve(id, json) when done. See Web/shell.js.
/// </summary>
class HomeDeckJsBridge : Java.Lang.Object
{
    readonly WeakReference<ShellWebView> _view;
    readonly AdBlockService _ads;
    readonly Action _onCancel;

    public HomeDeckJsBridge(ShellWebView view, AdBlockService ads, Action? onCancel = null)
    {
        _view = new WeakReference<ShellWebView>(view);
        _ads = ads;
        _onCancel = onCancel ?? (() => { });
    }

    void Resolve(string id, string json)
    {
        if (_view.TryGetTarget(out var v))
            MainThread.BeginInvokeOnMainThread(() =>
                v.EvaluateJavaScriptAsync($"window.__hdResolve({id},{JsonQuote(json)})"));
    }

    static string JsonQuote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Native-observed top page URL (UI-thread hop: bridge
    /// methods run on a Java thread). Binds file:// grants to the
    /// actual calling page — never trust the page-supplied host.
    /// Null = unknown, caller falls back to the legacy host key.</summary>
    async Task<string?> CallerUrlAsync()
    {
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    if (_view.TryGetTarget(out var v) && v.Source is Microsoft.Maui.Controls.UrlWebViewSource u)
                        return u.Url;
                    return null;
                }
                catch { return null; }
            });
        }
        catch { return null; }
    }

    /// <summary>File gate with the native-observed caller URL. Never throws.</summary>
    async Task<bool> GuardAsync(string? host, string op)
    {
        try { return await HdFileGuard.EnsureAccessAsync(host, op, await CallerUrlAsync()); }
        catch { return false; }
    }

    /// <summary>User-picked app set: native sheet, site learns picked IDs
    /// only. JavascriptInterface thread; the sheet marshals UI itself.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("pickApps")]
    public void PickApps(string id, string? host)
        => Task.Run(async () =>
        {
            try
            {
                ShellWebViewHandler? hh = null;
                try { if (_view.TryGetTarget(out var v)) hh = v.Handler as ShellWebViewHandler; } catch { }
                var sheet = hh?.AppPickSheet;
                if (sheet == null) { Resolve(id, "{\"ok\":false,\"error\":\"no-sheet\"}"); return; }
                string json;
                try { json = await sheet(host, await CallerUrlAsync()) ?? "{\"ok\":false,\"error\":\"cancelled\"}"; }
                catch (Exception e) { json = "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"; }
                Resolve(id, json);
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    /// <summary>Launch a picked app natively. Denied unless the id is in
    /// the caller's picked set — no arguments, no launch-time prompt.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("openApp")]
    public void OpenApp(string id, string? host, string? appId)
        => Task.Run(async () =>
        {
            try
            {
                var key = HdFileGuard.SiteKey(host, await CallerUrlAsync(), out var hash, out _);
                appId = (appId ?? "").Trim();
                if (!HdFileGuard.IsAppPicked(key, hash, appId))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, await AppInventory.LaunchAsync(appId));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    /// <summary>The site's own picked set with live running flags (one
    /// list for both — no separate task-list API). Unbound callers get
    /// an empty list (Android: always, running-state dropped there).</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("pickedApps")]
    public void PickedApps(string id, string? host)
        => Task.Run(() =>
        {
            try
            {
                var key = HdFileGuard.SiteKey(host, await CallerUrlAsync(), out var hash, out _);
                Resolve(id, AppInventory.PickedAppsJson(key, hash));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getBattery")] public string GetBattery() => DeviceBridge.GetBattery();
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getNetwork")] public string GetNetwork() => DeviceBridge.GetNetwork();
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getDevice")] public string GetDevice() => DeviceBridge.GetDevice();
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("vibrate")] public string Vibrate(int millis) => DeviceBridge.Vibrate(millis);

    /// <summary>Element picker reports (fire-and-forget): page JS calls
    /// HomeDeckNative.reportPicked/reportRemoved(host, selector). Runs on a
    /// Java bridge thread — never touches the view; AdBlockService
    /// marshals onward.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("reportPicked")]
    public void ReportPicked(string host, string selector)
    {
        try { _ads.ReportPickerPick(host, selector, "hide"); } catch { }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("reportRemoved")]
    public void ReportRemoved(string host, string selector)
    {
        try { _ads.ReportPickerPick(host, selector, "remove"); } catch { }
    }

    /// <summary>Frame Esc exit: any frame ends pick mode for the whole shell.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("reportCancel")]
    public void ReportCancel()
    {
        try { _onCancel(); } catch { }
    }

    /// <summary>Sync answer to page JS: remove-mode selectors for a host
    /// (JSON array). Called at page load by shell.js.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getRemoveSelectors")]
    public string GetRemoveSelectors(string host)
    {
        try { return _ads.GetRemoveSelectorsJson(host); } catch { return "[]"; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("shareText")]
    public void ShareText(string id, string? text, string? title)
        => Task.Run(async () => Resolve(id, await DeviceBridge.ShareTextAsync(text, title)));

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("clipboardWrite")]
    public void ClipboardWrite(string id, string? text)
        => Task.Run(async () => Resolve(id, await DeviceBridge.ClipboardWriteAsync(text)));

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("clipboardRead")]
    public void ClipboardRead(string id)
        => Task.Run(async () => Resolve(id, await DeviceBridge.ClipboardReadAsync()));

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("notify")]
    public void Notify(string id, string? title, string? body)
        => Task.Run(async () =>
        {
            try
            {
                var center = Plugin.LocalNotification.LocalNotificationCenter.Current;
                if (!await center.RequestNotificationPermission())
                    Resolve(id, "{\"ok\":false,\"error\":\"denied\"}");
                else
                {
                    await center.Show(new Plugin.LocalNotification.NotificationRequest
                    {
                        NotificationId = Random.Shared.Next(1, int.MaxValue),
                        Title = title ?? "Home Deck",
                        Description = body ?? ""
                    });
                    Resolve(id, "{\"ok\":true}");
                }
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    // Custom runtime permissions (MAUI BasePlatformPermission plumbing).
    class BtScanPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            OperatingSystem.IsAndroidVersionAtLeast(31)
                ? new[] { ("android.permission.BLUETOOTH_SCAN", true), ("android.permission.BLUETOOTH_CONNECT", true) }
                : new[] { ("android.permission.ACCESS_FINE_LOCATION", true) };
    }

    class MediaReadPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            OperatingSystem.IsAndroidVersionAtLeast(33)
                ? new[] { ("android.permission.READ_MEDIA_IMAGES", true), ("android.permission.READ_MEDIA_VIDEO", true), ("android.permission.READ_MEDIA_AUDIO", true) }
                : new[] { ("android.permission.READ_EXTERNAL_STORAGE", true) };
    }

    // Note: the pre-31 scan fallback asks for location, which the manifest
    // no longer declares (getLocation was dropped) — those old devices get
    // a clean denied from bleScan.
    class HdScanCallback : Android.Bluetooth.LE.ScanCallback
    {
        readonly Dictionary<string, (string name, int rssi)> _seen;
        public HdScanCallback(Dictionary<string, (string, int)> seen) { _seen = seen; }
        public override void OnScanResult(Android.Bluetooth.LE.ScanCallbackType callbackType, Android.Bluetooth.LE.ScanResult? result)
        {
            try
            {
                if (result?.Device == null) return;
                string addr = result.Device.Address ?? "?";
                string name;
                try { name = result.Device.Name ?? result.ScanRecord?.DeviceName ?? ""; }
                catch { name = ""; }
                lock (_seen) { _seen[addr] = (name, result.Rssi); }
            }
            catch { }
        }
        public override void OnScanFailed(Android.Bluetooth.LE.ScanFailure errorCode) { }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("bleScan")]
    public void BleScan(string id, int seconds)
        => Task.Run(async () =>
        {
            try
            {
                var st = await Permissions.RequestAsync<BtScanPermission>();
                if (st != PermissionStatus.Granted) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                var adapter = Android.Bluetooth.BluetoothAdapter.DefaultAdapter;
                if (adapter == null || !adapter.IsEnabled) { Resolve(id, "{\"ok\":false,\"error\":\"ble-off\"}"); return; }
                var scanner = adapter.BluetoothLeScanner;
                if (scanner == null) { Resolve(id, "{\"ok\":false,\"error\":\"no-scanner\"}"); return; }
                var seen = new Dictionary<string, (string name, int rssi)>();
                var cb = new HdScanCallback(seen);
                try { scanner.StartScan(cb); }
                catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); return; }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)));
                try { scanner.StopScan(cb); } catch { }
                var list = new List<object>();
                lock (seen)
                {
                    foreach (var kv in seen)
                        list.Add(new { address = kv.Key, name = kv.Value.name, rssi = kv.Value.rssi });
                }
                Resolve(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = list }));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("notificationAccess")]
    public string NotificationAccess()
    {
        try
        {
            var activity = Platform.CurrentActivity;
            if (activity == null) return "{\"ok\":false,\"error\":\"no-activity\"}";
            return "{\"ok\":true,\"data\":{\"enabled\":" + (HdNotificationService.IsEnabled(activity) ? "true" : "false") + "}}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("openNotificationSettings")]
    public void OpenNotificationSettings(string id)
        => Task.Run(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity == null) { Resolve(id, "{\"ok\":false,\"error\":\"no-activity\"}"); return; }
                var intent = new Android.Content.Intent(Android.Provider.Settings.ActionNotificationListenerSettings);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                activity.StartActivity(intent);
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("openWifiSettings")]
    public void OpenWifiSettings(string id)
        => Task.Run(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity == null) { Resolve(id, "{\"ok\":false,\"error\":\"no-activity\"}"); return; }
                var intent = new Android.Content.Intent(Android.Provider.Settings.ActionWifiSettings);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                activity.StartActivity(intent);
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("openBluetoothSettings")]
    public void OpenBluetoothSettings(string id)
        => Task.Run(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity == null) { Resolve(id, "{\"ok\":false,\"error\":\"no-activity\"}"); return; }
                var intent = new Android.Content.Intent(Android.Provider.Settings.ActionBluetoothSettings);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                activity.StartActivity(intent);
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getNotifications")]
    public void GetNotifications(string id)
        => Task.Run(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity != null && !HdNotificationService.IsEnabled(activity))
                {
                    Resolve(id, "{\"ok\":false,\"error\":\"listener-disabled\"}");
                    return;
                }
                var list = HdNotificationService.Active();
                Resolve(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = list }));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("setBrightness")]
    public string SetBrightness(float level)
    {
        try
        {
            var activity = Platform.CurrentActivity;
            if (activity?.Window == null) return DeviceBridge.NotImplemented("brightness");
            var attrs = activity.Window.Attributes!;
            attrs.ScreenBrightness = Math.Clamp(level, 0f, 1f);
            activity.Window.Attributes = attrs;
            return "{\"ok\":true}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getBrightness")]
    public string GetBrightness()
    {
        try
        {
            var activity = Platform.CurrentActivity;
            float v = activity?.Window?.Attributes?.ScreenBrightness ?? -1f;
            return "{\"ok\":true,\"data\":{\"level\":" + v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"; }
    }

    static string? PublicDir(string type)
    {
        try { return Android.OS.Environment.GetExternalStoragePublicDirectory(type)?.AbsolutePath; }
        catch { return null; }
    }

    static string JsonRaw(string? v)
        => System.Text.Json.JsonSerializer.Serialize(v ?? "");

    static bool UnderRoot(string? path, out string full)
    {
        full = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string canon;
            try { canon = new Java.IO.File(path).CanonicalPath ?? ""; }
            catch { return false; }
            return HdFileGuard.IsUnderRoot(canon, out full);
        }
        catch { return false; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("fileRoot")]
    public string FileRoot()
    {
        try { return "{\"ok\":true,\"data\":{\"root\":" + JsonRaw(HdFileGuard.Root) + "}}"; }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + JsonRaw(e.Message) + "}"; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("listDir")]
    public void ListDir(string id, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!await GuardAsync(host, "list files"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (string.IsNullOrWhiteSpace(path))
                {
                    string root = HdFileGuard.Root;
                    Resolve(id, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ok = true,
                        data = new[] { new { name = "HomeDeck", path = root, isDir = true } }
                    }));
                    return;
                }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                try { await Permissions.RequestAsync<MediaReadPermission>(); } catch { }
                var dir = new Java.IO.File(full);
                if (!dir.Exists() || !dir.IsDirectory) { Resolve(id, "{\"ok\":false,\"error\":\"no-dir\"}"); return; }
                var files = dir.ListFiles() ?? Array.Empty<Java.IO.File>();
                var list = new List<object>();
                foreach (var f in files.Take(2000))
                {
                    bool isDir;
                    long len;
                    try { isDir = f.IsDirectory; len = isDir ? -1 : f.Length(); }
                    catch { continue; }
                    list.Add(new { name = f.Name ?? "", path = f.AbsolutePath ?? "", isDir, size = len });
                }
                Resolve(id, System.Text.Json.JsonSerializer.Serialize(new { ok = true, data = list }));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("fileInfo")]
    public void FileInfo(string id, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!await GuardAsync(host, "read file info"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                var f = new Java.IO.File(full);
                if (!f.Exists()) { Resolve(id, "{\"ok\":false,\"error\":\"not-found\"}"); return; }
                bool isDir = f.IsDirectory;
                Resolve(id, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    data = new { name = f.Name ?? "", path = f.AbsolutePath ?? "", isDir, size = isDir ? -1L : f.Length() }
                }));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("readFile")]
    public void ReadFile(string id, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!await GuardAsync(host, "read files"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                var f = new Java.IO.File(full);
                if (!f.Exists() || f.IsDirectory) { Resolve(id, "{\"ok\":false,\"error\":\"not-found\"}"); return; }
                if (f.Length() > 8L * 1024 * 1024) { Resolve(id, "{\"ok\":false,\"error\":\"too-big\"}"); return; }
                byte[] bytes = System.IO.File.ReadAllBytes(f.AbsolutePath);
                Resolve(id, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    data = new { name = f.Name ?? "", size = (long)bytes.Length, base64 = Convert.ToBase64String(bytes) }
                }));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("writeFile")]
    public void WriteFile(string id, string? host, string? path, string? base64)
        => Task.Run(async () =>
        {
            try
            {
                if (!await GuardAsync(host, "write files"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                if (string.IsNullOrEmpty(base64) || base64.Length > 12L * 1024 * 1024)
                { Resolve(id, "{\"ok\":false,\"error\":\"too-big\"}"); return; }
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch { Resolve(id, "{\"ok\":false,\"error\":\"bad-base64\"}"); return; }
                if (bytes.Length > 8L * 1024 * 1024) { Resolve(id, "{\"ok\":false,\"error\":\"too-big\"}"); return; }
                try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full) ?? HdFileGuard.Root); }
                catch { Resolve(id, "{\"ok\":false,\"error\":\"no-dir\"}"); return; }
                System.IO.File.WriteAllBytes(full, bytes);
                Resolve(id, "{\"ok\":true,\"data\":{\"size\":" + bytes.Length + "}}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("mkdir")]
    public void Mkdir(string id, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!await GuardAsync(host, "create folders"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                System.IO.Directory.CreateDirectory(full);
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("deleteFile")]
    public void DeleteFile(string id, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!await GuardAsync(host, "delete files"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
                else if (System.IO.Directory.Exists(full) && System.IO.Directory.GetFileSystemEntries(full).Length == 0)
                    System.IO.Directory.Delete(full);
                else { Resolve(id, "{\"ok\":false,\"error\":\"not-found\"}"); return; }
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });
}
