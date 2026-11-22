using HomeDeck.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Storage;

namespace HomeDeck.Handlers;

/// <summary>Platform behavior lives in the Platforms/{Android,Windows} partials.</summary>
public partial class ShellWebViewHandler : WebViewHandler
{
    /// <summary>Arm the element picker, reporting every step. Returns "ok"
    /// or "fail: &lt;step&gt;: &lt;detail&gt;" — never throws, so the page
    /// can show the exact failure instead of failing silent. One page
    /// round-trip when the script is present, two when it must be injected.</summary>
    public async Task<string> StartPickerModeAsync()
    {
        var ready = await EnsureEngineAsync();
        if (ready != null) return ready;
        var first = await EvalJsAsync(ArmJs);
        if (IsChanErr(first))
            return "fail: js-channel: the page did not answer (" + first + "). Scripts are not executing in this page.";
        if (!first.Contains("missing"))
            return InterpretArm(first);
        var (text, readErr) = await ReadShellJsAsync();
        if (text == null)
            return "fail: package: could not read the pick script from the app package (" + readErr + "). Reinstall the app.";
        var inj = await EvalJsAsync(text);
        if (IsChanErr(inj))
            return "fail: inject: the page refused the script (" + inj + "). The page may block inline scripts.";
        var second = await EvalJsAsync(ArmJs);
        if (IsChanErr(second))
            return "fail: js-channel: the page stopped answering after injection (" + second + ").";
        if (second.Contains("missing"))
            return "fail: inject: script ran but the pick module is still missing.";
        return InterpretArm(second);
    }

    /// <summary>Probe + start + verify in a single page call.</summary>
    const string ArmJs = "(function(){try{if(!window.HomeDeckPick)return 'missing';window.HomeDeckPick.start();return window.HomeDeckPick.isActive()?'ok':'start-failed';}catch(e){return 'threw:'+((e&&e.message)||e);}})()";

    static string InterpretArm(string r)
    {
        if (r.Contains("ok")) return "ok";
        if (r.Contains("start-failed")) return "fail: verify: start() ran but pick mode did not engage.";
        if (r.Contains("threw:")) return "fail: start: arming threw (" + Trunc(r, 200) + "). The page may have redefined HomeDeckPick.";
        return "fail: verify: unexpected answer (" + Trunc(r, 120) + ").";
    }

    /// <summary>Fire-and-forget pick-mode exit (back button, Esc).</summary>
    public void CancelPickerMode()
    {
        try { _ = EvalJsAsync("window.HomeDeckPick&&window.HomeDeckPick.cancel()"); } catch { }
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
