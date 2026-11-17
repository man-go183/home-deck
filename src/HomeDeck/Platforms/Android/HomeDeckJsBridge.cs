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

    public HomeDeckJsBridge(ShellWebView view, AdBlockService ads)
    {
        _view = new WeakReference<ShellWebView>(view);
        _ads = ads;
    }

    void Resolve(string id, string json)
    {
        if (_view.TryGetTarget(out var v))
            MainThread.BeginInvokeOnMainThread(() =>
                v.EvaluateJavaScriptAsync($"window.__hdResolve({id},{JsonQuote(json)})"));
    }

    static string JsonQuote(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    [JavascriptInterface, Export("getBattery")] public string GetBattery() => DeviceBridge.GetBattery();
    [JavascriptInterface, Export("getNetwork")] public string GetNetwork() => DeviceBridge.GetNetwork();
    [JavascriptInterface, Export("getDevice")] public string GetDevice() => DeviceBridge.GetDevice();
    [JavascriptInterface, Export("vibrate")] public string Vibrate(int millis) => DeviceBridge.Vibrate(millis);

    [JavascriptInterface, Export("shareText")]
    public void ShareText(string id, string? text, string? title)
        => Task.Run(async () => Resolve(id, await DeviceBridge.ShareTextAsync(text, title)));

    [JavascriptInterface, Export("clipboardWrite")]
    public void ClipboardWrite(string id, string? text)
        => Task.Run(async () => Resolve(id, await DeviceBridge.ClipboardWriteAsync(text)));

    [JavascriptInterface, Export("clipboardRead")]
    public void ClipboardRead(string id)
        => Task.Run(async () => Resolve(id, await DeviceBridge.ClipboardReadAsync()));

    [JavascriptInterface, Export("notify")]
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

    // v1 stubs (surface exists so pages can feature-detect; implemented later)
    [JavascriptInterface, Export("getLocation")] public string GetLocation() => DeviceBridge.NotImplemented("location");
    [JavascriptInterface, Export("bleScan")] public string BleScan() => DeviceBridge.NotImplemented("ble");
    [JavascriptInterface, Export("setBrightness")]
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
}
