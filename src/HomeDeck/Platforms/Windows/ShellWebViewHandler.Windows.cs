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

    /// <summary>null = engine ready, else "fail: …".</summary>
    partial async Task<string?> EnsureEngineAsync()
    {
        var wv = _wv;
        if (wv == null) return "fail: view: no page view yet (open a profile first).";
        try { await wv.EnsureCoreWebView2Async(); return null; }
        catch (Exception e) { return "fail: engine: browser engine not ready (" + e.GetType().Name + ": " + Trunc(e.Message) + ")."; }
    }

    /// <summary>Raw script eval (bypasses the MAUI wrapper). Never throws:
    /// channel errors come back as THROW:/TIMEOUT.</summary>
    partial async Task<string> EvalJsAsync(string js)
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
            core.WebMessageReceived += (s, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t) && t.GetString() == "hd-pick")
                        _ads?.ReportPickerPick(
                            root.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "",
                            root.TryGetProperty("selector", out var x) ? x.GetString() ?? "" : "",
                            root.TryGetProperty("action", out var a) ? a.GetString() ?? "hide" : "hide");
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
