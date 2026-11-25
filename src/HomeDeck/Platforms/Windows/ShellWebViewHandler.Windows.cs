using System.Globalization;
using System.Linq;
using System.Reflection;
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
    bool _hostRegistered;
    Action? _navHostRetry;
    bool _retryPending;
    int _hostAttempts;
    HomeDeckHostObject? _bridge;
    CancellationTokenSource? _pollCts;

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
        _hostRegistered = false;
        _navHostRetry = null;
        _bridge = null;
        try { _pollCts?.Cancel(); } catch { }
        _pollCts = null;
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
        // Engine first (thread-safe). A failure here used to vanish into
        // the outer catch and leave pages with no host object and no
        // injected bridge — silent "waiting for bridge…" on device.
        try { await wv.EnsureCoreWebView2Async(); }
        catch (Exception e) { NoteInitError("engine", e); return; }

        // shell.js is plain asset IO: read before hopping threads.
        string? js = null;
        try
        {
            using var st = await FileSystem.OpenAppPackageFileAsync("shell.js");
            using var r = new StreamReader(st);
            js = await r.ReadToEndAsync();
        }
        catch (Exception e) { NoteInitError("shell.js", e); }

        // CoreWebView2 registration must run on the UI thread that owns
        // the core: the awaits above can resume on a worker, and
        // AddHostObjectToScript throws off-thread — which used to abort the
        // whole init (no host object, no injection, no ad-block).
        try { await MainThread.InvokeOnMainThreadAsync(() => RegisterCoreAsync(wv, view, js)); }
        catch (Exception e) { NoteInitError("register", e); }
    }

    /// <summary>UI-thread CoreWebView2 wiring. Every step is isolated: one
    /// failure is recorded in BridgeInitError and the rest still run, so a
    /// dead host object can no longer take the script injection (or the
    /// ad-block filter) down with it.</summary>
    async Task RegisterCoreAsync(Microsoft.UI.Xaml.Controls.WebView2 wv, ShellWebView view, string? js)
    {
        var core = wv.CoreWebView2;
        if (core == null) { NoteInitError("engine", "no CoreWebView2"); return; }
        _core = core;

        // Dispatch target for the poll transport (and the host object if it
        // ever registers): the same surface, unregistered. Postbacks ride
        // the proven native->page eval, never the broken page->native side.
        _bridge = new HomeDeckHostObject(msg =>
            MainThread.BeginInvokeOnMainThread(
                () => { _ = wv.ExecuteScriptAsync($"window.__hdResolveMsg({msg})"); }), _ads,
            () => { try { PickCancelRequested?.Invoke(); } catch { } });

        // The host object can throw ERROR_INVALID_STATE while the browser
        // side isn't ready yet, so registration retries on every navigation
        // (plus one delayed retry) until it sticks. Pages already loaded
        // pick it up with no reload via shell.js's lazy native resolution.
        try { TryRegisterHost(); } catch (Exception e) { NoteInitError("host-object", e); }
        _navHostRetry = () => { try { if (!_hostRegistered) TryRegisterHost(); } catch { } };

        try { core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All); }
        catch (Exception e) { NoteInitError("filter", e); }

        // Element picker reports (shell.js postMessage {type:"hd-pick"}).
        // Accepts the object directly; a JSON string gets unwrapped too
        // (WebMessageAsJson double-wraps posted strings — that silent
        // mismatch once ate every report).
        try
        {
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
        }
        catch (Exception e) { NoteInitError("reports", e); }
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
        try
        {
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
        }
        catch (Exception e) { NoteInitError("adblock", e); }

        if (js == null) return;
        // Permanent document-created injection: every frame (including
        // cross-origin iframes the top-frame eval can't reach) gets the
        // bridge + pick module + Esc + remove-mode at creation. The
        // NavigationCompleted eval below stays as the DOM-ready top pass
        // (double injection is safe: every shell.js section self-guards).
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(js); } catch (Exception e) { NoteInitError("doc-created", e); }
        try
        {
            core.NavigationCompleted += async (_, _) =>
            {
                try { _navHostRetry?.Invoke(); } catch { }
                try { if (!_hostRegistered) _ = DelayedHostRetryAsync(); } catch { }
                try { await wv.ExecuteScriptAsync(js); } catch { }
                try { await InjectCosmeticsAsync(wv, view); } catch { }
            };
        }
        catch (Exception e) { NoteInitError("nav-hook", e); }

        // Poll transport: the page queues bridge calls when no native proxy
        // exists; the drain below runs them on _bridge and posts answers
        // back via eval. Only the proven native->page direction is used.
        StartPollLoop(wv);
    }

    /// <summary>Register the sync host object on the live core. Every
    /// attempt is counted and the latest error kept per step, so the shield
    /// panel shows exactly what is failing and how often it retried.</summary>
    bool TryRegisterHost()
    {
        var wv = _wv;
        var core = _core;
        var bridge = _bridge;
        if (wv == null || core == null || bridge == null) { NoteInitError("host-object", "no view/core/bridge"); return false; }
        _hostAttempts++;
        var attempt = _hostAttempts;
        try
        {
            core.AddHostObjectToScript("HomeDeckNative", bridge);
            try { _hostRegistered = true; ClearInitError("host-object"); } catch { }
            return true;
        }
        catch (Exception e) { NoteInitError("host-object", "try " + attempt + ": " + e.GetType().Name + ": " + Trunc(e.Message)); return false; }
    }

    /// <summary>One delayed re-attempt per navigation: the browser side is
    /// sometimes ready a couple of seconds after NavigationCompleted.</summary>
    async Task DelayedHostRetryAsync()
    {
        if (_retryPending || _hostRegistered) return;
        _retryPending = true;
        try
        {
            await Task.Delay(2500);
            if (_hostRegistered) return;
            try { await MainThread.InvokeOnMainThreadAsync(() => TryRegisterHost()); }
            catch (Exception e) { NoteInitError("host-object", e); }
        }
        catch { }
        finally { try { _retryPending = false; } catch { } }
    }

    /// <summary>Poll loop for the outbox transport. One eval per 500ms per
    /// page (trivial cost); eval runs on the UI thread because background
    /// WebView2 calls fail silently. Never throws; the loop dies only with
    /// the view (disconnect cancels it).</summary>
    void StartPollLoop(Microsoft.UI.Xaml.Controls.WebView2 wv)
    {
        try { _pollCts?.Cancel(); } catch { }
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _ = Task.Run(() => PollLoopAsync(wv, cts.Token));
    }

    async Task PollLoopAsync(Microsoft.UI.Xaml.Controls.WebView2 wv, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct); } catch { return; }
            if (ct.IsCancellationRequested) return;
            var bridge = _bridge;
            if (bridge == null) continue;
            string? raw = null;
            try { raw = await MainThread.InvokeOnMainThreadAsync(() => wv.ExecuteScriptAsync("window.__hdTakeOutbox&&window.__hdTakeOutbox()").AsTask()); }
            catch { continue; }
            if (string.IsNullOrEmpty(raw)) continue;
            List<OutboxCall>? calls = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    try { using var inner = JsonDocument.Parse(root.GetString() ?? "[]"); calls = ParseOutboxCalls(inner.RootElement); }
                    catch { continue; }
                }
                else calls = ParseOutboxCalls(root);
            }
            catch { continue; }
            if (calls == null || calls.Count == 0) continue;
            foreach (var call in calls)
            {
                if (ct.IsCancellationRequested) return;
                try { DispatchOutboxCall(wv, bridge, call); } catch { }
            }
        }
    }

    sealed class OutboxCall
    {
        public int Id;
        public string Method = "";
        public readonly List<JsonElement> Args = new();
    }

    static List<OutboxCall> ParseOutboxCalls(JsonElement root)
    {
        var list = new List<OutboxCall>();
        try
        {
            if (root.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in root.EnumerateArray())
            {
                try
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    if (!el.TryGetProperty("id", out var idp) || !el.TryGetProperty("method", out var mp)) continue;
                    var c = new OutboxCall { Id = idp.GetInt32(), Method = mp.GetString() ?? "" };
                    if (el.TryGetProperty("args", out var ap) && ap.ValueKind == JsonValueKind.Array)
                        foreach (var a in ap.EnumerateArray()) c.Args.Add(a.Clone());
                    if (c.Method.Length > 0) list.Add(c);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Run one queued bridge call on the unregistered surface.
    /// void + id-first methods answer via their own postback; sync string
    /// methods and the no-arg reportCancel answer inline here.</summary>
    void DispatchOutboxCall(Microsoft.UI.Xaml.Controls.WebView2 wv, HomeDeckHostObject bridge, OutboxCall call)
    {
        void Post(string json)
        {
            try
            {
                var msg = "{\"id\":" + call.Id + ",\"result\":" + json + "}";
                MainThread.BeginInvokeOnMainThread(() => { try { _ = wv.ExecuteScriptAsync($"window.__hdResolveMsg({msg})"); } catch { } });
            }
            catch { }
        }
        MethodInfo? mi = null;
        try { mi = typeof(HomeDeckHostObject).GetMethod(call.Method, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase); }
        catch { }
        if (mi == null) { Post("{\"ok\":false,\"error\":" + JsonSerializer.Serialize("no-method: " + call.Method) + "}"); return; }
        var ps = mi.GetParameters();
        try
        {
            if (mi.ReturnType == typeof(void))
            {
                var margs = new object?[ps.Length];
                int ai = 0;
                if (ps.Length > 0 && ps[0].Name == "id" && ps[0].ParameterType == typeof(string))
                { margs[0] = call.Id.ToString(); ai = 1; }
                for (int i = ai; i < ps.Length; i++)
                    margs[i] = CoerceArg(ps[i].ParameterType, i - ai < call.Args.Count ? call.Args[i - ai] : default);
                mi.Invoke(bridge, margs); // async results arrive via the bridge's own postback
                if (ps.Length == 0) Post("{\"ok\":true}");
            }
            else if (mi.ReturnType == typeof(string))
            {
                var margs = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    margs[i] = CoerceArg(ps[i].ParameterType, i < call.Args.Count ? call.Args[i] : default);
                var r = (string?)mi.Invoke(bridge, margs);
                Post(string.IsNullOrEmpty(r) ? "{\"ok\":false,\"error\":\"empty\"}" : r);
            }
            else Post("{\"ok\":false,\"error\":" + JsonSerializer.Serialize("bad-signature: " + call.Method) + "}");
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
            Post("{\"ok\":false,\"error\":" + JsonSerializer.Serialize(Trunc(inner.Message)) + "}");
        }
    }

    /// <summary>JSON value to a bridge parameter type (string/int/float/bool
    /// cover the whole surface). Never throws; missing values become the
    /// type default (null for string).</summary>
    static object? CoerceArg(Type t, JsonElement el)
    {
        try
        {
            if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null)
                return t == typeof(string) ? null : (t.IsValueType ? Activator.CreateInstance(t) : null);
            if (t == typeof(string)) return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            if (t == typeof(int))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n)) return n;
                return 0;
            }
            if (t == typeof(float))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var x)) return x;
                if (el.ValueKind == JsonValueKind.String && float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return x;
                return 0f;
            }
            if (t == typeof(bool))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
                return false;
            }
        }
        catch { }
        try { return t.IsValueType ? Activator.CreateInstance(t) : null; } catch { return null; }
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
