using Android.Webkit;
using HomeDeck.Controls;
using HomeDeck;
using HomeDeck.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace HomeDeck.Handlers;

/// <summary>Android WebView: JS bridge + per-request ad-block interception.</summary>
public partial class ShellWebViewHandler
{
    AdBlockService? _ads;

    protected override void ConnectHandler(Android.Webkit.WebView platformView)
    {
        base.ConnectHandler(platformView);
        _ads = MauiContext?.Services.GetService<AdBlockService>();

        platformView.Settings!.JavaScriptEnabled = true;
        platformView.Settings.DomStorageEnabled = true;
        platformView.AddJavascriptInterface(
            new HomeDeckJsBridge((ShellWebView)VirtualView, _ads ?? new AdBlockService()),
            "HomeDeckNative");
        platformView.SetWebViewClient(new ShellWebViewClient((ShellWebView)VirtualView, _ads));

        _ = InjectShellJsAsync();
    }

    async Task InjectShellJsAsync()
    {
        try
        {
            using var s = await FileSystem.OpenAppPackageFileAsync("shell.js");
            using var r = new StreamReader(s);
            var js = await r.ReadToEndAsync();
            if (VirtualView is ShellWebView v)
                v.Navigated += async (_, _) =>
                {
                    try { await v.EvaluateJavaScriptAsync(js); } catch { }
                };
        }
        catch { }
    }

    protected override void DisconnectHandler(Android.Webkit.WebView platformView)
    {
        try { platformView.RemoveJavascriptInterface("HomeDeckNative"); } catch { }
        base.DisconnectHandler(platformView);
    }

    sealed class ShellWebViewClient : WebViewClient
    {
        readonly ShellWebView _view;
        readonly AdBlockService? _ads;

        public ShellWebViewClient(ShellWebView view, AdBlockService? ads)
        {
            _view = view;
            _ads = ads;
        }

        public override WebResourceResponse? ShouldInterceptRequest(Android.Webkit.WebView? view, IWebResourceRequest? request)
        {
            try
            {
                if (_view.AdBlockEnabled && _ads != null && request?.Url != null)
                {
                    var url = request.Url!.ToString()!;
                    var reqHeaders = request.RequestHeaders;
                    string type = GuessType(url, reqHeaders);
                    if (_ads.ShouldBlock(url, _view.PageHost, type))
                        return new WebResourceResponse("text/plain", "utf-8", 403, "Blocked",
                            new Dictionary<string, string>(), new MemoryStream(Array.Empty<byte>()));
                }
            }
            catch { /* fail open */ }
            return base.ShouldInterceptRequest(view, request);
        }

        static string GuessType(string url, IDictionary<string, string>? headers)
        {
            var u = url.ToLowerInvariant();
            if (u.EndsWith(".js")) return "script";
            if (u.EndsWith(".css")) return "stylesheet";
            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") ||
                u.EndsWith(".gif") || u.EndsWith(".webp") || u.EndsWith(".svg")) return "image";
            if (u.EndsWith(".woff") || u.EndsWith(".woff2") || u.EndsWith(".ttf")) return "font";
            if (u.EndsWith(".mp4") || u.EndsWith(".mp3") || u.EndsWith(".webm")) return "media";
            return "other";
        }
    }
}
