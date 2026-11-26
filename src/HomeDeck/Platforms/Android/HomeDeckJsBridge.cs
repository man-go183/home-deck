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

    /// <summary>Per-navigation bridge token (audit H1): set by the
    /// handler on every navigation (top-frame-only delivery), required
    /// on every capability call. Memory-only. Never throws.</summary>
    volatile string? _token;

    internal void SetToken(string? t)
    {
        try { _token = t; } catch { }
    }

    bool TokenOk(string? tok)
    {
        try
        {
            if (string.IsNullOrEmpty(tok)) return false;
            var cur = _token;
            if (string.IsNullOrEmpty(cur)) return false;
            var x = System.Text.Encoding.UTF8.GetBytes(tok);
            var y = System.Text.Encoding.UTF8.GetBytes(cur);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
        }
        catch { return false; }
    }

    void DenyToken(string id)
    {
        try { Resolve(id, "{\"ok\":false,\"error\":\"bad-token\"}"); } catch { }
    }

    string DenyTokenSync() => "{\"ok\":false,\"error\":\"bad-token\"}";

    /// <summary>Relay identity (audit relay): while set, Guard/SiteKey
    /// use this browser-stamped frame origin instead of any page-supplied
    /// host AND skip the native top-host override. Only the relay entry
    /// sets it, only with a valid nav token, only from an isTrusted
    /// frame message. AsyncLocal keeps concurrent calls isolated.</summary>
    readonly System.Threading.AsyncLocal<string?> _relayHost = new();

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

    /// <summary>File gate with the native-observed caller URL. Relayed
    /// calls use the browser-stamped frame origin instead (top override
    /// skipped). Never throws.</summary>
    async Task<bool> GuardAsync(string? host, string op)
    {
        try
        {
            var rh = _relayHost.Value;
            if (rh != null) return await HdFileGuard.EnsureAccessAsync(rh, op, null);
            return await HdFileGuard.EnsureAccessAsync(host, op, await CallerUrlAsync());
        }
        catch { return false; }
    }

    /// <summary>Relay-aware site identity (see _relayHost). Never throws.</summary>
    async Task<(string key, string? hash)> SiteKeyForAsync(string? host)
    {
        try
        {
            var rh = _relayHost.Value;
            if (rh != null) { var k = HdFileGuard.SiteKey(rh, null, out var h2, out _); return (k, h2); }
            var key = HdFileGuard.SiteKey(host, await CallerUrlAsync(), out var hash, out _);
            return (key, hash);
        }
        catch { return (host ?? "", null); }
    }

    /// <summary>Relay-aware host (see _relayHost). Never throws.</summary>
    string HostFor(string? host)
    {
        try { return _relayHost.Value ?? host ?? ""; } catch { return host ?? ""; }
    }

    /// <summary>User-picked app set: native sheet, site learns picked IDs
    /// only. JavascriptInterface thread; the sheet marshals UI itself.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("pickApps")]
    public void PickApps(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var (pk, _) = await SiteKeyForAsync(host);
                if (!HdFileGuard.RateOk(pk, "pickApps", true)) { Resolve(id, "{\"ok\":false,\"error\":\"rate-limited\"}"); return; }
                ShellWebViewHandler? hh = null;
                try { if (_view.TryGetTarget(out var v)) hh = v.Handler as ShellWebViewHandler; } catch { }
                var sheet = hh?.AppPickSheet;
                if (sheet == null) { Resolve(id, "{\"ok\":false,\"error\":\"no-sheet\"}"); return; }
                string json;
                try { json = await sheet(HostFor(host), _relayHost.Value != null ? null : await CallerUrlAsync()) ?? "{\"ok\":false,\"error\":\"cancelled\"}"; }
                catch (Exception e) { json = "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"; }
                Resolve(id, json);
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    /// <summary>Launch a picked app natively. Denied unless the id is in
    /// the caller's picked set — no arguments, no launch-time prompt.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("openApp")]
    public void OpenApp(string id, string? tok, string? host, string? appId)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var (key, hash) = await SiteKeyForAsync(host);
                if (!HdFileGuard.RateOk(key, "openApp", true)) { Resolve(id, "{\"ok\":false,\"error\":\"rate-limited\"}"); return; }
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
    public void PickedApps(string id, string? tok, string? host)
        => Task.Run(() =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var (key2, hash2) = await SiteKeyForAsync(host);
                Resolve(id, AppInventory.PickedAppsJson(key2, hash2));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getBattery")] public void GetBattery(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "read device info")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, DeviceBridge.GetBattery());
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getNetwork")] public void GetNetwork(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "read device info")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, DeviceBridge.GetNetwork());
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getDevice")] public void GetDevice(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "read device info")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, DeviceBridge.GetDevice());
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });
    /// <summary>Audit M6: vibration is a Device-group capability (gated
    /// like battery/network — consistency for all).</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("vibrate")]
    public void Vibrate(string id, string? tok, string? host, int millis)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "use vibration")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, DeviceBridge.Vibrate(millis));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

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
        try { return _ads.GetRemoveSelectorsJson(HostFor(host)); } catch { return "[]"; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("shareText")]
    public void ShareText(string id, string? tok, string? text, string? title)
        => Task.Run(async () =>
        {
            try { if (!TokenOk(tok)) { DenyToken(id); return; } } catch { DenyToken(id); return; }
            // Audit L3x: shareText has no host param — identity comes from the
            // native-observed top URL (relay-aware, like PickApps above).
            var sk = HdFileGuard.SiteKey(HostFor(""), _relayHost.Value != null ? null : await CallerUrlAsync(), out _, out _);
            if (!HdFileGuard.RateOk(sk, "shareText", true)) { Resolve(id, "{\"ok\":false,\"error\":\"rate-limited\"}"); return; }
            try { Resolve(id, await DeviceBridge.ShareTextAsync(text, title)); } catch { }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("clipboardWrite")]
    public void ClipboardWrite(string id, string? tok, string? host, string? text)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "write the clipboard")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, await DeviceBridge.ClipboardWriteAsync(text));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("clipboardRead")]
    public void ClipboardRead(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "read the clipboard")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, await DeviceBridge.ClipboardReadAsync());
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("notify")]
    public void Notify(string id, string? tok, string? host, string? title, string? body)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "send notifications")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
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
    public void BleScan(string id, string? tok, string? host, int seconds)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "scan nearby devices")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
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

    /// <summary>Audit L5: parity stubs — host is threaded like every other
    /// capability, so a future implementation inherits the gate shape. When
    /// implementing for real, add GuardAsync(host, "scan nearby devices")
    /// first (like BleScan) — without it this would be ungated by
    /// construction. Until then: honest not-implemented, no phantom
    /// prompt. Never throws.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("wifiScan")]
    public void WifiScan(string id, string? tok, string? host)
        => Task.Run(() =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                Resolve(id, DeviceBridge.NotImplemented("wifiScan"));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("bluetoothDevices")]
    public void BluetoothDevices(string id, string? tok, string? host, int inquiry)
        => Task.Run(() =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                Resolve(id, DeviceBridge.NotImplemented("bluetoothDevices"));
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("notificationAccess")]
    public string NotificationAccess(string? tok)
    {
        try
        {
            if (!TokenOk(tok)) return DenyTokenSync();
            var activity = Platform.CurrentActivity;
            if (activity == null) return "{\"ok\":false,\"error\":\"no-activity\"}";
            return "{\"ok\":true,\"data\":{\"enabled\":" + (HdNotificationService.IsEnabled(activity) ? "true" : "false") + "}}";
        }
        catch (Exception e) { return "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"; }
    }

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("openNotificationSettings")]
    public void OpenNotificationSettings(string id, string? tok)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var sk = HdFileGuard.SiteKey(HostFor(""), _relayHost.Value != null ? null : await CallerUrlAsync(), out _, out _);
                if (!HdFileGuard.RateOk(sk, "openSettings", true)) { Resolve(id, "{\"ok\":false,\"error\":\"rate-limited\"}"); return; }
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
    public void OpenWifiSettings(string id, string? tok)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var sk = HdFileGuard.SiteKey(HostFor(""), _relayHost.Value != null ? null : await CallerUrlAsync(), out _, out _);
                if (!HdFileGuard.RateOk(sk, "openSettings", true)) { Resolve(id, "{\"ok\":false,\"error\":\"rate-limited\"}"); return; }
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
    public void OpenBluetoothSettings(string id, string? tok)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var sk = HdFileGuard.SiteKey(HostFor(""), _relayHost.Value != null ? null : await CallerUrlAsync(), out _, out _);
                if (!HdFileGuard.RateOk(sk, "openSettings", true)) { Resolve(id, "{\"ok\":false,\"error\":\"rate-limited\"}"); return; }
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
    public void GetNotifications(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "read notifications")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
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

    /// <summary>Audit M6: brightness is a Device-group capability, gated
    /// like the rest. Never throws.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("setBrightness")]
    public void SetBrightness(string id, string? tok, string? host, float level)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "set brightness")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                var activity = Platform.CurrentActivity;
                if (activity?.Window == null) { Resolve(id, DeviceBridge.NotImplemented("brightness")); return; }
                var attrs = activity.Window.Attributes!;
                attrs.ScreenBrightness = Math.Clamp(level, 0f, 1f);
                activity.Window.Attributes = attrs;
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("getBrightness")]
    public void GetBrightness(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "read brightness")) { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                var activity = Platform.CurrentActivity;
                float v = activity?.Window?.Attributes?.ScreenBrightness ?? -1f;
                Resolve(id, "{\"ok\":true,\"data\":{\"level\":" + v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

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

    /// <summary>Audit M5: fileRoot is split by caller — file-slot pages
    /// get the real path behind the same Guard gate as listDir (user's
    /// own page, trusted after prompt); https/blank callers get a
    /// blocked answer with no prompt. Intentional, not a bug.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("fileRoot")]
    public void FileRoot(string id, string? tok, string? host)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                var key = HdFileGuard.SiteKey(host, await CallerUrlAsync(), out _, out _);
                if (!key.StartsWith("file|", StringComparison.Ordinal))
                { Resolve(id, "{\"ok\":false,\"error\":\"blocked\"}"); return; }
                if (!await GuardAsync(host, "list files"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                Resolve(id, "{\"ok\":true,\"data\":{\"root\":" + JsonRaw(HdFileGuard.Root) + "}}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonRaw(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("listDir")]
    public void ListDir(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
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
    public void FileInfo(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
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
    public void ReadFile(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
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
    public void WriteFile(string id, string? tok, string? host, string? path, string? base64)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
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
    public void Mkdir(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (!await GuardAsync(host, "create folders"))
                { Resolve(id, "{\"ok\":false,\"error\":\"denied\"}"); return; }
                if (!UnderRoot(path, out var full)) { Resolve(id, "{\"ok\":false,\"error\":\"outside-root\"}"); return; }
                System.IO.Directory.CreateDirectory(full);
                Resolve(id, "{\"ok\":true}");
            }
            catch (Exception e) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(e.Message) + "}"); }
        });

    [Android.Webkit.JavascriptInterface, Java.Interop.Export("deleteFile")]
    public void DeleteFile(string id, string? tok, string? host, string? path)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
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

    /// <summary>Subframe relay (audit relay): invoked ONLY by the top
    /// frame's shell.js listener (isTrusted frame message) with the nav
    /// token this closure holds. origin is browser-stamped and becomes
    /// the grant identity for this call (top-host override skipped via
    /// _relayHost); method allowlist excludes relay itself. Answers via
    /// the method's own Resolve under the same id. Never throws.</summary>
    [Android.Webkit.JavascriptInterface, Java.Interop.Export("relay")]
    public void Relay(string id, string? tok, string? origin, string? method, string? argsJson)
        => Task.Run(async () =>
        {
            try
            {
                if (!TokenOk(tok)) { DenyToken(id); return; }
                if (string.IsNullOrWhiteSpace(method) || method.Equals("relay", StringComparison.OrdinalIgnoreCase))
                { Resolve(id, "{\"ok\":false,\"error\":\"bad-method\"}"); return; }
                System.Reflection.MethodInfo? mi = null;
                try { mi = typeof(HomeDeckJsBridge).GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase); }
                catch { }
                if (mi == null) { Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote("no-method: " + method) + "}"); return; }
                var ps = mi.GetParameters();
                bool noArgs = ps.Length == 0;
                bool syncString = mi.ReturnType == typeof(string);
                bool asyncVoid = mi.ReturnType == typeof(void) && ps.Length >= 2 && ps[0].Name == "id" && ps[0].ParameterType == typeof(string)
                    && ps[1].Name == "tok" && ps[1].ParameterType == typeof(string);
                if (!noArgs && !asyncVoid && !(syncString && ps.Length >= 1 && ps[0].Name == "tok" && ps[0].ParameterType == typeof(string)))
                { Resolve(id, "{\"ok\":false,\"error\":\"bad-signature\"}"); return; }
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
                    if (noArgs) { mi.Invoke(this, null); Resolve(id, "{\"ok\":true}"); return; }
                    if (syncString)
                    {
                        var margs = new object?[ps.Length];
                        margs[0] = tok;
                        for (int i = 1; i < ps.Length; i++)
                            margs[i] = CoerceRelay(ps[i].ParameterType, i - 1 < rargs.Length ? rargs[i - 1] : default);
                        var r = (string?)mi.Invoke(this, margs);
                        Resolve(id, string.IsNullOrEmpty(r) ? "{\"ok\":false,\"error\":\"empty\"}" : r);
                        return;
                    }
                    var vargs = new object?[ps.Length];
                    vargs[0] = id; vargs[1] = tok;
                    for (int i = 2; i < ps.Length; i++)
                        vargs[i] = CoerceRelay(ps[i].ParameterType, i - 2 < rargs.Length ? rargs[i - 2] : default);
                    mi.Invoke(this, vargs); // async results arrive via the method's own Resolve under the same id
                }
                finally { _relayHost.Value = null; }
            }
            catch (Exception e)
            {
                var inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Resolve(id, "{\"ok\":false,\"error\":" + JsonQuote(inner.Message ?? "failed") + "}");
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
}
