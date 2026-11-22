using HomeDeck.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Storage;

namespace HomeDeck.Handlers;

/// <summary>Platform behavior lives in the Platforms/{Android,Windows} partials.</summary>
public partial class ShellWebViewHandler : WebViewHandler
{
    /// <summary>Arm the element picker, reporting every step. Returns "ok"
    /// or "fail: &lt;step&gt;: &lt;detail&gt;" — never throws, so the page
    /// can show the exact failure instead of failing silent.</summary>
    public async Task<string> StartPickerModeAsync()
    {
        var ready = await EnsureEngineAsync();
        if (ready != null) return ready;
        var probe = await EvalJsAsync("typeof window.HomeDeckPick");
        if (IsChanErr(probe))
            return "fail: js-channel: the page did not answer the probe (" + probe + "). Scripts are not executing in this page.";
        if (!probe.Contains("undefined"))
            return await ArmAsync();
        var (text, readErr) = await ReadShellJsAsync();
        if (text == null)
            return "fail: package: could not read the pick script from the app package (" + readErr + "). Reinstall the app.";
        var inj = await EvalJsAsync(text);
        if (IsChanErr(inj))
            return "fail: inject: the page refused the script (" + inj + "). The page may block inline scripts.";
        var probe2 = await EvalJsAsync("typeof window.HomeDeckPick");
        if (probe2.Contains("undefined") || IsChanErr(probe2))
            return "fail: inject: script ran but the pick module is still missing (" + probe2 + ").";
        return await ArmAsync();
    }

    async Task<string> ArmAsync()
    {
        var s = await EvalJsAsync("window.HomeDeckPick.start()");
        if (IsChanErr(s))
            return "fail: start: arming threw (" + s + "). The page may have redefined HomeDeckPick.";
        var v = await EvalJsAsync("window.HomeDeckPick.isActive()");
        if (v.Contains("true")) return "ok";
        return "fail: verify: start() ran but pick mode did not engage (isActive=" + v + ").";
    }

    static bool IsChanErr(string s) => s.StartsWith("THROW") || s == "TIMEOUT";

    static string Trunc(string? s, int n = 160)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

    async Task<(string? text, string? error)> ReadShellJsAsync()
    {
        try
        {
            using var st = await FileSystem.OpenAppPackageFileAsync("shell.js");
            using var r = new StreamReader(st);
            return (await r.ReadToEndAsync(), null);
        }
        catch (Exception e) { return (null, e.GetType().Name + ": " + Trunc(e.Message)); }
    }

    // Per-platform primitives (never throw; channel errors as THROW:/TIMEOUT).
    private partial Task<string?> EnsureEngineAsync();
    private partial Task<string> EvalJsAsync(string js);
}
