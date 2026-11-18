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

    protected override void ConnectHandler(Microsoft.UI.Xaml.Controls.WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        _ads = MauiContext?.Services.GetService<AdBlockService>();
        _ = InitAsync(platformView, (ShellWebView)VirtualView);
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
                        () => { _ = wv.ExecuteScriptAsync($"window.__hdResolveMsg({msg})"); })));

            core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) =>
            {
                try
                {
                    if (!view.AdBlockEnabled || _ads == null) return;
                    var url = e.Request.Uri;
                    if (string.IsNullOrEmpty(url)) return;
                    if (_ads.ShouldBlock(url, view.PageHost, GuessType(url, e.Request.Headers)))
                    {
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
                try
                {
                    var css = _ads?.GetCosmeticCssForHost(view.PageHost) ?? "";
                    if (!string.IsNullOrEmpty(css))
                        await wv.ExecuteScriptAsync("var s=document.createElement('style');s.textContent=" + JsonSerializer.Serialize(css) + ";document.documentElement.appendChild(s);");
                }
                catch { }
            };
        }
        catch { /* WebView2 missing: page area stays blank, shell still runs */ }
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
