using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Networking;

namespace HomeDeck.Services;

/// <summary>
/// Platform-agnostic device answers, serialized for the JS bridge.
/// Every method is fail-safe: on any error it returns a JSON error object,
/// never throws across the native boundary.
/// </summary>
public static class DeviceBridge
{
    static string Ok(object? data = null)
        => JsonSerializer.Serialize(new { ok = true, data });
    static string Fail(string reason)
        => JsonSerializer.Serialize(new { ok = false, error = reason });

    public static string GetBattery()
    {
        try
        {
            var b = Battery.Default;
            return Ok(new
            {
                level = b.ChargeLevel,
                state = b.State.ToString(),
                source = b.PowerSource.ToString()
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static string GetNetwork()
    {
        try
        {
            var c = Connectivity.Current;
            return Ok(new
            {
                access = c.NetworkAccess.ToString(),
                profiles = c.ConnectionProfiles.Select(p => p.ToString()).ToArray()
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static string GetDevice()
    {
        try
        {
            var d = DeviceInfo.Current;
            return Ok(new
            {
                model = d.Model,
                manufacturer = d.Manufacturer,
                name = d.Name,
                platform = d.Platform.ToString(),
                version = d.VersionString,
                idiom = d.Idiom.ToString(),
                bridge = "hd/1"
            });
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static string Vibrate(int millis)
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(Math.Clamp(millis, 1, 5000)));
            return Ok();
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static async Task<string> ShareTextAsync(string? text, string? title)
    {
        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Text = text ?? "",
                Title = title ?? "Share"
            });
            return Ok();
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static async Task<string> ClipboardWriteAsync(string? text)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(text ?? "");
            return Ok();
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    public static async Task<string> ClipboardReadAsync()
    {
        try { return Ok(new { text = await Clipboard.Default.GetTextAsync() ?? "" }); }
        catch (Exception e) { return Fail(e.Message); }
    }

    // v1 stubs: part of the hd/1 surface, implemented in a later version.
    // Pages must feature-detect via the ok:false + "not-implemented" shape.
    public static string NotImplemented(string what) => Fail("not-implemented: " + what);
}
