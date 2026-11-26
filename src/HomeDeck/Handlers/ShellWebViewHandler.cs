using HomeDeck.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Storage;
using System.Linq;

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
            return await FinishArmAsync(InterpretArm(first));
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
        return await FinishArmAsync(InterpretArm(second));
    }

    /// <summary>Top frame armed: reach same-origin frames + register the
    /// auto-arm snippet (platform). Best-effort — never turns ok into fail.</summary>
    async Task<string> FinishArmAsync(string r)
    {
        if (r == "ok") await ArmFramesAsync();
        return r;
    }

    /// <summary>Probe + start + verify in a single page call.</summary>
    const string ArmJs = "(function(){try{if(!window.HomeDeckPick)return 'missing';window.HomeDeckPick.start();return window.HomeDeckPick.isActive()?'ok':'start-failed';}catch(e){return 'threw:'+((e&&e.message)||e);}})()";

    /// <summary>Best-effort same-origin frame arming (cross-origin frames
    /// throw per frame and are skipped — they join via the auto-arm
    /// snippet on their next load while armed). Never throws: channel
    /// errors come back as THROW:/TIMEOUT strings from EvalJsAsync.</summary>
    const string ArmFramesJs = "(function(){var n=0;try{function walk(w){try{if(w.HomeDeckPick&&w.HomeDeckPick.isActive&&!w.HomeDeckPick.isActive()){w.HomeDeckPick.start();n++;}}catch(e){}try{var f=w.frames;for(var i=0;i<f.length;i++){try{walk(f[i]);}catch(e){}}}catch(e){}}walk(window);}catch(e){}return 'frames:'+n;})()";

    /// <summary>Auto-start snippet for frames created while armed
    /// (document-created, runs before anything else in each new frame).</summary>
    const string AutoArmJs = "(function(){try{if(window.HomeDeckPick&&window.HomeDeckPick.isActive&&!window.HomeDeckPick.isActive())window.HomeDeckPick.start();}catch(e){}})();";

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
        try { DisarmFrames(); } catch { }
        try { _ = EvalJsAsync("window.HomeDeckPick&&window.HomeDeckPick.cancel()"); } catch { }
    }

    /// <summary>Per-step latest bridge-init failures, else null. The shield
    /// panel surfaces these so a dead bridge is diagnosable on device
    /// instead of a silent "waiting for bridge…".</summary>
    public string? BridgeInitError
    {
        get
        {
            try
            {
                lock (_initLock)
                {
                    if (_initErrors.Count == 0) return null;
                    return string.Join("\n", _initErrors.OrderBy(kv => kv.Key)
                        .Select(kv => "bridge init: " + kv.Key + " (" + kv.Value + ")"));
                }
            }
            catch { return null; }
        }
    }
    readonly Dictionary<string, string> _initErrors = new();
    readonly object _initLock = new();

    /// <summary>Record (or refresh) a step's failure; never throws.</summary>
    protected void NoteInitError(string step, Exception e)
        => NoteInitError(step, e.GetType().Name + ": " + Trunc(e.Message));

    /// <summary>Record (or refresh) a step's failure; never throws.</summary>
    protected void NoteInitError(string step, string detail)
    {
        try { lock (_initLock) { _initErrors[step] = detail; } } catch { }
    }

    /// <summary>Clear the recorded failure when its step later succeeds
    /// (e.g. the host object registers on a later navigation). Never throws.</summary>
    protected void ClearInitError(string step)
    {
        try { lock (_initLock) { _initErrors.Remove(step); } } catch { }
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

    /// <summary>Reach frames after a top-frame arm (platform).</summary>
    private partial Task ArmFramesAsync();

    /// <summary>Undo ArmFramesAsync (snippet removal etc). Sync fire-and-forget.</summary>
    partial void DisarmFrames();

    /// <summary>Native app-pick sheet: given the page-supplied host +
    /// native-observed caller URL, show the sheet and return the picked
    /// result JSON (ok/data or ok:false). Wired by the page.</summary>
    public Func<string?, string?, Task<string>>? AppPickSheet;

    /// <summary>Popup router: the page shows Cancel / Same tab /
    /// New window and returns 0 / 1 / 2. Wired by the page (it owns
    /// the prompt + the transient page push); platforms only capture
    /// the URL and honor 1 themselves (same-tab load). Null = block.</summary>
    public Func<string, Task<int>>? PopupRouter;

    /// <summary>Programmatic top navigation the policy must let through:
    /// the page sets this to the exact target before navigating itself
    /// (profile loads, retries, prompt-approved same-tab loads). Checked
    /// first and cleared on use, so page-driven travel can't borrow it.
    /// Never persisted. Audit M3.</summary>
    public string? ExpectedTopUrl;
}

/// <summary>Audit M3/M4 top-navigation + popup policy (shared by both
/// platforms and the page router). Profiles are pinned app slots: same
/// place travels silent, anywhere else asks, weird schemes never load.
/// The javascript:/data:/intent:/custom blocks below are intentional —
/// not a bug — there is no current intent: use (logged 2026-11-26), so a
/// future need reads as a deliberate revisit, not a hunt.</summary>
public static class TopNavPolicy
{
    public enum TopVerdict { Allow, Prompt, Block }

    /// <summary>http(s) + file only. Everything else never gets a prompt.
    /// Never throws.</summary>
    public static bool PopupAllowed(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var s = new Uri(url).Scheme.ToLowerInvariant();
            return s == "http" || s == "https" || s == "file";
        }
        catch { return false; }
    }

    /// <summary>Classify a top-frame travel. currentTop = native-observed
    /// current URL (null = first load, allow). expected = programmatic
    /// target set by the page itself. Same-site/same-file/about:blank =
    /// silent; cross-site http(s) and other-file = ask; everything else =
    /// block. Unparseable = block (fail closed). Never throws.</summary>
    public static TopVerdict Classify(string? currentTop, string? expected, string target)
    {
        try
        {
            var t = (target ?? "").Trim();
            if (t.Length == 0) return TopVerdict.Block;
            if (!string.IsNullOrEmpty(expected) && string.Equals(t, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                return TopVerdict.Allow;
            var tu = new Uri(t);
            var ts = tu.Scheme.ToLowerInvariant();
            if (ts == "about") return TopVerdict.Allow;
            if (ts != "http" && ts != "https" && ts != "file") return TopVerdict.Block;
            if (string.IsNullOrWhiteSpace(currentTop)) return TopVerdict.Allow;
            var cu = new Uri(currentTop);
            var cs = cu.Scheme.ToLowerInvariant();
            if (ts == "file" && cs == "file")
            {
                try
                {
                    var tf = System.IO.Path.GetFullPath(tu.LocalPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                    var cf = System.IO.Path.GetFullPath(cu.LocalPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                    return string.Equals(tf, cf, StringComparison.OrdinalIgnoreCase) ? TopVerdict.Allow : TopVerdict.Prompt;
                }
                catch { return TopVerdict.Prompt; }
            }
            if ((ts == "http" || ts == "https") && (cs == "http" || cs == "https"))
                return string.Equals(tu.Host, cu.Host, StringComparison.OrdinalIgnoreCase) ? TopVerdict.Allow : TopVerdict.Prompt;
            return TopVerdict.Prompt; // file<->https crossings always ask
        }
        catch { return TopVerdict.Block; }
    }
}
