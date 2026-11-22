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

    /// <summary>True when consumed: pick mode was armed and is now
    /// cancelled. Wired by the page (it owns the armed flag).</summary>
    public Func<bool>? PickEscPressed;

    protected override void ConnectHandler(Microsoft.UI.Xaml.Controls.WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        _wv = platformView;
        _ads = MauiContext?.Services.GetService<AdBlockService>();
        _ = InitAsync(platformView, (ShellWebView)VirtualView);
    }

    protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.WebView2 platformView)
    {
        if (_wv == platformView) _wv = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>Wait cursor over the page while arming (WinUI has no
    /// MAUI-level cursor API). Restored by the caller in a finally.</summary>
    public void SetWaitCursor(bool wait)
    {
        try
        {
            var wv = _wv;
            if (wv == null) return;
            wv.ChangeCursor(Microsoft.UI.Input.InputSystemCursor.Create(
                wait ? Microsoft.UI.Input.InputSystemCursorShape.Wait : Microsoft.UI.Input.InputSystemCursorShape.Arrow));
        }
        catch { }
    }

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

            core.AddHostObjectToScript("HomeDeckNative",
                new HomeDeckHostObject(msg =>
                    MainThread.BeginInvokeOnMainThread(
                        () => { _ = wv.ExecuteScriptAsync($"window.__hdResolveMsg({msg})"); }), _ads));

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
                    else _ads?.NoteReportError("report: unexpected pick message shape");
                }
                catch (Exception ex) { try { _ads?.NoteReportError("report: bad pick message (" + ex.GetType().Name + ")"); } catch { } }
            };
            // Window-level Esc: fires even when page focus is buried in an
            // iframe (the page's own Esc listener never sees it then).
            // Consumed only while the page says pick mode is armed.
            core.AcceleratorKeyPressed += (s, e) =>
            {
                try
                {
                    if ((uint)e.VirtualKey == 0x1B && (PickEscPressed?.Invoke() == true)) e.Handled = true;
                }
                catch { }
            };
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
        CountCosmeticHidden(wv, view, ads, css, css2);
    }

    /// <summary>Count page elements matching the injected selectors (one
    /// pass per load; capped). Fire-and-forget: a 0 on any failure.</summary>
    static void CountCosmeticHidden(
        Microsoft.UI.Xaml.Controls.WebView2 wv, ShellWebView view, AdBlockService ads, string css, string css2)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var set = new HashSet<string>(AdBlockService.CosmeticSelectors(css));
                foreach (var s in AdBlockService.CosmeticSelectors(css2)) set.Add(s);
                if (set.Count == 0) { view.SetCosmeticHidden(0); return; }
                var raw = await wv.ExecuteScriptAsync("window.__hdCountMatches&&window.__hdCountMatches(" + JsonSerializer.Serialize(set.Take(300).ToArray()) + ")");
                if (int.TryParse(raw, out int n)) view.SetCosmeticHidden(n);
            }
            catch { }
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
