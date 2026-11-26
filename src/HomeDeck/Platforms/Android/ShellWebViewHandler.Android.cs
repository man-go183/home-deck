using System.Linq;
using System.Text.Json;
using Android.Webkit;
using HomeDeck;
using HomeDeck.Controls;
using HomeDeck.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;

namespace HomeDeck.Handlers;

/// <summary>Android WebView: JS bridge + per-request ad-block interception.</summary>
public partial class ShellWebViewHandler
{
    AdBlockService? _ads;
    Android.Webkit.WebView? _wv;

    /// <summary>Fired when any frame asks out of pick mode (Esc).
    /// Wired by the page to its shared cancel.</summary>
    public Action? PickCancelRequested;

    /// <summary>Fired (off the UI thread) after every cosmetic count pass,
    /// including the late one. Wired by the page to refresh open UI.</summary>
    public Action? CountsChanged;

    /// <summary>How this page is currently served (Advanced settings →
    /// Bridge status). Never throws.</summary>
    public string BridgeStatus()
    {
        try { return _wv == null ? "no view" : "JS interface"; }
        catch { return "unknown"; }
    }

    protected override void ConnectHandler(Android.Webkit.WebView platformView)
    {
        base.ConnectHandler(platformView);
        _wv = platformView;
        _ads = MauiContext?.Services.GetService<AdBlockService>();

        platformView.Settings!.JavaScriptEnabled = true;
        platformView.Settings.DomStorageEnabled = true;
        platformView.Settings.SetSupportMultipleWindows(true);
        platformView.Settings.JavaScriptCanOpenWindowsAutomatically = true;
        platformView.SetWebChromeClient(new ShellWebChromeClient(OnPopupUrl));
        platformView.AddJavascriptInterface(
            new HomeDeckJsBridge((ShellWebView)VirtualView, _ads ?? new AdBlockService(),
                () => { try { PickCancelRequested?.Invoke(); } catch { } }),
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
                    try { await InjectCosmeticsAsync(v); } catch { }
                };
        }
        catch { }
    }

    protected override void DisconnectHandler(Android.Webkit.WebView platformView)
    {
        try { platformView.RemoveJavascriptInterface("HomeDeckNative"); } catch { }
        if (_wv == platformView) _wv = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>null = engine ready, else "fail: …".</summary>
    private partial Task<string?> EnsureEngineAsync()
    {
        if (_wv == null) return Task.FromResult<string?>("fail: view: no page view yet (open a profile first).");
        return Task.FromResult<string?>(null);
    }

    /// <summary>Best-effort same-origin frame arming. No document-created
    /// API on Android, so existing cross-origin frames can't be reached —
    /// same boundary as the desktop build, minus the auto-arm snippet.</summary>
    private partial async Task ArmFramesAsync()
    {
        try { await EvalJsAsync(ArmFramesJs); } catch { }
    }

    partial void DisarmFrames() { /* nothing platform-held on Android */ }

    sealed class JsValue : Java.Lang.Object, IValueCallback
    {
        readonly TaskCompletionSource<string> _tcs;
        public JsValue(TaskCompletionSource<string> tcs) { _tcs = tcs; }
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            try { _tcs.TrySetResult(value?.ToString() ?? "null"); } catch { }
        }
    }

    /// <summary>Raw script eval (bypasses the MAUI wrapper). Never throws:
    /// channel errors come back as THROW:/TIMEOUT.</summary>
    private partial async Task<string> EvalJsAsync(string js)
    {
        var wv = _wv;
        if (wv == null) return "THROW:NoView";
        var tcs = new TaskCompletionSource<string>();
        try { wv.EvaluateJavascript(js, new JsValue(tcs)); }
        catch (Exception e) { return "THROW:" + e.GetType().Name + ":" + Trunc(e.Message); }
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (winner != tcs.Task) return "TIMEOUT:eval exceeded 10s";
        return await tcs.Task;
    }

    async Task InjectCosmeticsAsync(ShellWebView v)
    {
        var ads = _ads;
        if (ads == null || !v.AdBlockEnabled) return;
        var pageHost = v.PageHost;
        var css = ads.GetCosmeticCssForHost(pageHost);
        if (!string.IsNullOrEmpty(css))
            await v.EvaluateJavaScriptAsync("var s=document.createElement('style');s.textContent=" + JsonSerializer.Serialize(css) + ";document.documentElement.appendChild(s);");
        string[] classes, ids;
        try
        {
            var raw = await v.EvaluateJavaScriptAsync(AdBlockService.ClassSnapshotJs);
            AdBlockService.ParseClassSnapshot(raw, out classes, out ids);
        }
        catch { return; }
        if (classes.Length == 0 && ids.Length == 0) return;
        var css2 = ads.GetCosmeticCssForClasses(pageHost, string.Join(",", classes), string.Join(",", ids));
        if (!string.IsNullOrEmpty(css2) && css2 != css)
            await v.EvaluateJavaScriptAsync("var s=document.createElement('style');s.textContent=" + JsonSerializer.Serialize(css2) + ";document.documentElement.appendChild(s);");
        CountCosmeticHidden(v, ads, css, css2, v.PageGen, CountsChanged);
    }

    /// <summary>Count page elements matching the injected selectors. One
    /// pass at load plus a late pass for slow pages (bounded); stale
    /// pages (navigated since) are skipped via the generation guard.</summary>
    static void CountCosmeticHidden(ShellWebView v, AdBlockService ads, string css, string css2, int gen, Action? onCounted)
    {
        _ = Task.Run(async () =>
        {
            async Task CountOnceAsync()
            {
                try
                {
                    var set = new HashSet<string>(AdBlockService.CosmeticSelectors(css));
                    foreach (var s in AdBlockService.CosmeticSelectors(css2)) set.Add(s);
                    if (v.PageGen != gen) return;
                    if (set.Count == 0) { v.SetCosmeticHidden(0); return; }
                    var raw = await v.EvaluateJavaScriptAsync("window.__hdCountMatches&&window.__hdCountMatches(" + JsonSerializer.Serialize(set.Take(300).ToArray()) + ")");
                    if (v.PageGen != gen) return;
                    if (int.TryParse(raw?.Trim('"'), out int n)) v.SetCosmeticHidden(n);
                }
                catch { }
                try { onCounted?.Invoke(); } catch { }
            }
            await CountOnceAsync();
            try { await Task.Delay(4000); } catch { }
            await CountOnceAsync();
        });
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
                    {
                        _view.CountBlocked();
                        return new WebResourceResponse("text/plain", "utf-8", 403, "Blocked",
                            new Dictionary<string, string>(), new MemoryStream(Array.Empty<byte>()));
                    }
                }
            }
            catch { /* fail open */ }
            return base.ShouldInterceptRequest(view, request);
        }

        static string GuessType(string url, IDictionary<string, string>? headers)
        {
            try
            {
                if (headers != null && headers.Keys.Any(k => k.Equals("X-Requested-With", StringComparison.OrdinalIgnoreCase)))
                    return "xmlhttprequest";
            }
            catch { }
            var u = url.ToLowerInvariant();
            if (u.EndsWith(".js")) return "script";
            if (u.EndsWith(".css")) return "stylesheet";
            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") ||
                u.EndsWith(".gif") || u.EndsWith(".webp") || u.EndsWith(".svg")) return "image";
            if (u.EndsWith(".woff") || u.EndsWith(".woff2") || u.EndsWith(".ttf") || u.EndsWith(".otf")) return "font";
            if (u.EndsWith(".mp4") || u.EndsWith(".mp3") || u.EndsWith(".webm") || u.EndsWith(".ogg") || u.EndsWith(".m4a")) return "media";
            return "other";
        }
    }

    /// <summary>Popup interception: every new-window request is captured
    /// into a throwaway view whose first navigation reveals the URL, then
    /// routed (cancel / same tab / new shell) — ask every time, and the
    /// popup page itself never loads anywhere unprompted.</summary>
    sealed class ShellWebChromeClient : WebChromeClient
    {
        readonly Func<string, Task> _onUrl;
        public ShellWebChromeClient(Func<string, Task> onUrl) { _onUrl = onUrl; }

        public override bool OnCreateWindow(Android.Webkit.WebView? view, bool isDialog, bool isUserGesture, Android.OS.Message? resultMsg)
        {
            try
            {
                var ctx = view?.Context;
                if (ctx == null || resultMsg == null) return false;
                var dummy = new Android.Webkit.WebView(ctx);
                try { dummy.Settings!.JavaScriptEnabled = true; } catch { }
                dummy.SetWebViewClient(new PopupCaptureClient(_onUrl));
                if (resultMsg.Obj is Android.Webkit.WebView.WebViewTransport transport)
                {
                    transport.WebView = dummy;
                    resultMsg.SendToTarget();
                    return true;
                }
                try { dummy.Destroy(); } catch { }
                return false;
            }
            catch { return false; }
        }

        sealed class PopupCaptureClient : WebViewClient
        {
            readonly Func<string, Task> _onUrl;
            bool _fired;
            public PopupCaptureClient(Func<string, Task> onUrl) { _onUrl = onUrl; }
            public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? v, IWebResourceRequest? request)
            {
                try
                {
                    var url = request?.Url?.ToString();
                    if (!_fired && !string.IsNullOrEmpty(url))
                    {
                        _fired = true;
                        var cb = _onUrl;
                        _ = Task.Run(async () => { try { await cb(url); } catch { } });
                    }
                }
                catch { }
                try { v?.Destroy(); } catch { }
                return true;
            }
        }
    }

    /// <summary>Route an intercepted popup URL: the page prompts
    /// (cancel / same tab / new shell). Same-tab loads here; the
    /// new-shell push happens inside the page's router.</summary>
    Task OnPopupUrl(string url)
    {
        return Task.Run(async () =>
        {
            try
            {
                var r = PopupRouter;
                int c = r == null ? 0 : await r(url);
                if (c == 1)
                    MainThread.BeginInvokeOnMainThread(() => { try { _wv?.LoadUrl(url); } catch { } });
            }
            catch { }
        });
    }
}
