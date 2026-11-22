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

    public HomeDeckHostObject(Action<string> post, AdBlockService? ads) { _post = post; _ads = ads; }

    /// <summary>Sync answer to page JS: remove-mode selectors for a host
    /// (JSON array). Called at page load by shell.js.</summary>
    public string getRemoveSelectors(string host)
    {
        try { return _ads?.GetRemoveSelectorsJson(host) ?? "[]"; }
        catch { return "[]"; }
    }

    public string getBattery() => DeviceBridge.GetBattery();
    public string getNetwork() => DeviceBridge.GetNetwork();
    public string getDevice() => DeviceBridge.GetDevice();
    public string vibrate(int millis) => DeviceBridge.Vibrate(millis);
    public string getLocation() => DeviceBridge.NotImplemented("location");
    public string bleScan() => DeviceBridge.NotImplemented("ble");
    public string setBrightness(float level) => DeviceBridge.NotImplemented("brightness");

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
}
