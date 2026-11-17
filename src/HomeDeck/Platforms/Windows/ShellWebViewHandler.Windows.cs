using HomeDeck.Controls;
using HomeDeck.Handlers;
using HomeDeck.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace HomeDeck;

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
                    if (_ads.ShouldBlock(url, view.PageHost, GuessType(url)))
                    {
                        var env = (s as Microsoft.Web.WebView2.Core.CoreWebView2)?.Environment;
                        if (env != null)
                            e.Response = env.CreateWebResourceResponse(
                                new MemoryStream(Array.Empty<byte>()), 403, "Blocked", "Content-Type: text/plain");
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
            };
        }
        catch { /* WebView2 missing: page area stays blank, shell still runs */ }
    }

    static string GuessType(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains(".js")) return "script";
        if (u.Contains(".css")) return "stylesheet";
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" })
            if (u.Contains(ext)) return "image";
        return "other";
    }
}
