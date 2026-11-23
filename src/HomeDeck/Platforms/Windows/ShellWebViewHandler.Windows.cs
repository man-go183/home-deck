using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using HomeDeck;
using HomeDeck.Controls;
using HomeDeck.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace HomeDeck.Handlers;

/// <summary>Windows WebView2: host object + per-request ad-block interception.</summary>
public partial class ShellWebViewHandler
{
    AdBlockService? _ads;
    Microsoft.UI.Xaml.Controls.WebView2? _wv;
    Microsoft.Web.WebView2.Core.CoreWebView2? _core;
    string? _autoArmId;

    /// <summary>True when consumed: pick mode was armed and is now
    /// cancelled. Wired by the page (it owns the armed flag).</summary>
    public Func<bool>? PickEscPressed;

    /// <summary>Global pick-mode exit requested from any frame (Esc).
    /// Wired by the page to its shared cancel.</summary>
    public Action? PickCancelRequested;

    /// <summary>Fired (off the UI thread) after every cosmetic count pass,
    /// including the late one. Wired by the page to refresh open UI.</summary>
    public Action? CountsChanged;

    protected override void ConnectHandler(Microsoft.UI.Xaml.Controls.WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        _wv = platformView;
        _ads = MauiContext?.Services.GetService<AdBlockService>();
        _ = InitAsync(platformView, (ShellWebView)VirtualView);
    }

    protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.WebView2 platformView)
    {
        try { DisarmFrames(); } catch { }
        _core = null;
        if (_wv == platformView) _wv = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>Best-effort frame reach: same-origin recursion now, plus an
    /// auto-arm snippet so frames created while armed start themselves.
    /// Never throws; never fails the arm.</summary>
    private partial async Task ArmFramesAsync()
    {
        try { await EvalJsAsync(ArmFramesJs); } catch { }
        try
        {
            var core = _core;
            if (core == null) return;
            if (_autoArmId != null)
            {
                try { core.RemoveScriptToExecuteOnDocumentCreated(_autoArmId); } catch { }
                _autoArmId = null;
            }
            _autoArmId = await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoArmJs);
        }
        catch { }
    }

    partial void DisarmFrames()
    {
        try
        {
            var core = _core;
            var id = _autoArmId;
            _autoArmId = null;
            if (core != null && id != null) core.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch { }
    }

    /// <summary>No-op: WinUI exposes cursor only via the protected
    /// UIElement.ProtectedCursor (subclass-only), and WebView2 is not
    /// subclassed here — so there is no compile-safe way to set a wait
    /// cursor on the view. Kept as a method because the page calls it
    /// around arming; the single-trip arm is fast enough without one.</summary>
    public void SetWaitCursor(bool wait) { }

    /// <summary>null = engine ready, else "fail: …".</summary>
    private partial async Task<string?> EnsureEngineAsync()
    {
        var wv = _wv;
        if (wv == null) return "fail: view: no page view yet (open a profile first).";
        try { await wv.EnsureCoreWebView2Async(); return null; }
        catch (Exception e) { return "fail: engine: browser engine not ready (" + e.GetType().Name + ": " + Trunc(e.Message) + ")."; }
    }

    /// <summary>Raw script eval (bypasses the MAUI wrapper). Never throws:
    /// channel errors come back as THROW:/TIMEOUT.</summary>
    private partial async Task<string> EvalJsAsync(string js)
    {
        var wv = _wv;
        if (wv == null) return "THROW:NoView";
        try
        {
            var task = wv.ExecuteScriptAsync(js).AsTask();
            var winner = await Task.WhenAny(task, Task.Delay(10000));
            if (winner != task) return "TIMEOUT:eval exceeded 10s";
            return await task;
        }
        catch (Exception e) { return "THROW:" + e.GetType().Name + ":" + Trunc(e.Message); }
    }

    async Task InitAsync(Microsoft.UI.Xaml.Controls.WebView2 wv, ShellWebView view)
    {
        try
        {
            await wv.EnsureCoreWebView2Async();
            var core = wv.CoreWebView2!;
            _core = core;

            core.AddHostObjectToScript("HomeDeckNative",
                new HomeDeckHostObject(msg =>
                    MainThread.BeginInvokeOnMainThread(
                        () => { _ = wv.ExecuteScriptAsync($"window.__hdResolveMsg({msg})"); }), _ads,
                    () => { try { PickCancelRequested?.Invoke(); } catch { } }));

            core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            // Element picker reports (shell.js postMessage {type:"hd-pick"}).
            // Accepts the object directly; a JSON string gets unwrapped too
            // (WebMessageAsJson double-wraps posted strings — that silent
            // mismatch once ate every report).
            core.WebMessageReceived += (s, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.String)
                    {
                        try { using var inner = JsonDocument.Parse(root.GetString() ?? ""); root = inner.RootElement.Clone(); }
                        catch { _ads?.NoteReportError("report: unreadable pick message (not JSON)"); return; }
                    }
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("type", out var t) && t.GetString() == "hd-pick")
                        _ads?.ReportPickerPick(
                            root.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "",
                            root.TryGetProperty("selector", out var x) ? x.GetString() ?? "" : "",
                            root.TryGetProperty("action", out var a) ? a.GetString() ?? "hide" : "hide");
                    else if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("type", out var c) && c.GetString() == "hd-cancel")
                        try { PickCancelRequested?.Invoke(); } catch { }
                    else _ads?.NoteReportError("report: unexpected pick message shape");
                }
                catch (Exception ex) { try { _ads?.NoteReportError("report: bad pick message (" + ex.GetType().Name + ")"); } catch { } }
            };
            // Esc exits: the page's own Esc listener covers web focus; this
            // XAML-level fallback catches Esc when focus is on the shell
            // chrome. (This SDK's CoreWebView2 exposes neither
            // AcceleratorKeyPressed nor Controller, so no browser-level
            // hook — iframe-focus Esc stays a known gap, parked with the
            // document-created injection item.)
            try
            {
                wv.KeyDown += (s, e) =>
                {
                    try
                    {
                        if (e.Key == Windows.System.VirtualKey.Escape && (PickEscPressed?.Invoke() == true)) e.Handled = true;
                    }
                    catch { }
                };
            }
            catch { }
            core.WebResourceRequested += (s, e) =>
            {
                try
                {
                    if (!view.AdBlockEnabled || _ads == null) return;
                    var url = e.Request.Uri;
                    if (string.IsNullOrEmpty(url)) return;
                    if (_ads.ShouldBlock(url, view.PageHost, GuessType(url, e.Request.Headers)))
                    {
                        view.CountBlocked();
                        var env = (s as Microsoft.Web.WebView2.Core.CoreWebView2)?.Environment;
                        if (env != null)
                            e.Response = env.CreateWebResourceResponse(
                                new MemoryStream(Array.Empty<byte>()).AsRandomAccessStream(), 403, "Blocked", "Content-Type: text/plain");
                    }
                }
                catch { /* fail open */ }
            };

            using var st = await FileSystem.OpenAppPackageFileAsync("shell.js");
            using var r = new StreamReader(st);
            var js = await r.ReadToEndAsync();
            // Permanent document-created injection: every frame (including
            // cross-origin iframes the top-frame eval can't reach) gets the
            // bridge + pick module + Esc + remove-mode at creation. The
            // NavigationCompleted eval below stays as the DOM-ready top pass
            // (double injection is safe: every shell.js section self-guards).
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(js); } catch { }
            core.NavigationCompleted += async (_, _) =>
            {
                try { await wv.ExecuteScriptAsync(js); } catch { }
                try { await InjectCosmeticsAsync(wv, view); } catch { }
            };
        }
        catch { /* WebView2 missing: page area stays blank, shell still runs */ }
    }

    async Task InjectCosmeticsAsync(
        Microsoft.UI.Xaml.Controls.WebView2 wv, ShellWebView view)
    {
        var ads = _ads;
        if (ads == null || !view.AdBlockEnabled) return;
        var pageHost = view.PageHost;
        var css = ads.GetCosmeticCssForHost(pageHost);
        if (!string.IsNullOrEmpty(css))
            await wv.ExecuteScriptAsync("var s=document.createElement('style');s.textContent=" + JsonSerializer.Serialize(css) + ";document.documentElement.appendChild(s);");
        string[] classes, ids;
        try
        {
            var raw = await wv.ExecuteScriptAsync(AdBlockService.ClassSnapshotJs);
            AdBlockService.ParseClassSnapshot(raw, out classes, out ids);
        }
        catch { return; }
        if (classes.Length == 0 && ids.Length == 0) return;
        var css2 = ads.GetCosmeticCssForClasses(pageHost, string.Join(",", classes), string.Join(",", ids));
        if (!string.IsNullOrEmpty(css2) && css2 != css)
            await wv.ExecuteScriptAsync("var s=document.createElement('style');s.textContent=" + JsonSerializer.Serialize(css2) + ";document.documentElement.appendChild(s);");
        CountCosmeticHidden(wv, view, ads, css, css2, view.PageGen, CountsChanged);
    }

    /// <summary>Count page elements matching the injected selectors. One
    /// pass at load plus a late pass for slow pages (bounded); stale
    /// pages (navigated since) are skipped via the generation guard.</summary>
    static void CountCosmeticHidden(
        Microsoft.UI.Xaml.Controls.WebView2 wv, ShellWebView view, AdBlockService ads, string css, string css2,
        int gen, Action? onCounted)
    {
        _ = Task.Run(async () =>
        {
            async Task CountOnceAsync()
            {
                try
                {
                    var set = new HashSet<string>(AdBlockService.CosmeticSelectors(css));
                    foreach (var s in AdBlockService.CosmeticSelectors(css2)) set.Add(s);
                    if (view.PageGen != gen) return;
                    if (set.Count == 0) { view.SetCosmeticHidden(0); return; }
                    var raw = await wv.ExecuteScriptAsync("window.__hdCountMatches&&window.__hdCountMatches(" + JsonSerializer.Serialize(set.Take(300).ToArray()) + ")");
                    if (view.PageGen != gen) return;
                    if (int.TryParse(raw?.Trim('"'), out int n)) view.SetCosmeticHidden(n);
                }
                catch { }
                try { onCounted?.Invoke(); } catch { }
            }
            await CountOnceAsync();
            try { await Task.Delay(4000); } catch { }
            await CountOnceAsync();
        });
    }

    static string GuessType(string url, Microsoft.Web.WebView2.Core.CoreWebView2HttpRequestHeaders? headers)
    {
        try
        {
            if (headers != null && headers.Contains("X-Requested-With")) return "xmlhttprequest";
        }
        catch { }
        var u = url.ToLowerInvariant();
        if (u.Contains(".js")) return "script";
        if (u.Contains(".css")) return "stylesheet";
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" })
            if (u.Contains(ext)) return "image";
        foreach (var ext in new[] { ".woff", ".woff2", ".ttf", ".otf" })
            if (u.Contains(ext)) return "font";
        foreach (var ext in new[] { ".mp4", ".mp3", ".webm", ".ogg", ".m4a" })
            if (u.Contains(ext)) return "media";
        return "other";
    }
}
