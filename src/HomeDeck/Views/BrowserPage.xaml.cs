using HomeDeck.Controls;
using HomeDeck.Handlers;
using HomeDeck.Models;
using HomeDeck.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;

namespace HomeDeck.Views;

public partial class BrowserPage : ContentPage
{
    readonly ProfileStore _store;
    readonly AdBlockService _ads;
    Profile? _profile;
    CancellationTokenSource? _loadCts;
    bool _appeared;
    string? _pendingUrl;
    bool _navStarted;
    bool _updating;
    bool _lastUpdateFailed;
    bool _pickArmed;
    /// <summary>Session-only picks (user: no persistence for now). Live
    /// hide/delete + count + panel list while the page is open; cleared
    /// on every navigation, never written to prefs.</summary>
    readonly List<PickerRule> _sessionPicks = new();
#if ANDROID
    bool _titleViewSet;
#endif

    bool _transient;
    Label? _blockSub;
    string? _openPerm;
    string? _openApp;
    public BrowserPage(ProfileStore store, AdBlockService ads)
    {
        InitializeComponent();
#if WINDOWS
        // The in-page TitleBar grid IS the app header (it extends into the OS
        // caption). The NavigationPage toolbar + large header would duplicate
        // the title and the back button — hide them. Android keeps its native
        // toolbar: back + pill move there as the TitleView (see OnAppearing).
        NavigationPage.SetHasNavigationBar(this, false);
        // Also hide MAUI's own caption back button: our ‹ covers back duty
        // (WebView history first, then pop). Without this the framework draws
        // a second back chevron in the caption area on Windows.
        NavigationPage.SetHasBackButton(this, false);
#endif
        _store = store;
        _ads = ads;
        _ads.OnPickerReported = OnPickerReported;
        Web.HandlerChanged += (_, _) =>
        {
            // Window-level Esc (fires even with focus buried in an iframe).
            if (Web.Handler is ShellWebViewHandler hh)
            {
#if WINDOWS
                // Windows-only: other platforms' handler partial has no key path.
                hh.PickEscPressed = OnPickEsc;
#endif
                hh.CountsChanged = OnCountsChanged;
                hh.PickCancelRequested = () => CancelPickMode();
                hh.PopupRouter = RoutePopupAsync;
                hh.AppPickSheet = PickAppsSheetAsync;
            }
        };

        Web.Navigating += (_, e) =>
        {
            _navStarted = true;
            try { Web.PageHost = new Uri(e.Url).Host; } catch { }
        };
        Web.Navigated += async (_, e) =>
        {
            CancelWatchdog();
            SplashStatus.IsVisible = false;
            try
            {
                if (e.Result == WebNavigationResult.Failure)
                {
                    ShowSplashError("Couldn't load this page.");
                    return;
                }
                await Splash.FadeToAsync(0, 200);
                Splash.IsVisible = false;
                Splash.Opacity = 1;
            }
            catch { Splash.IsVisible = false; }
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        if (!_titleViewSet)
        {
            _titleViewSet = true;
            // The in-page bar is hidden; back + pill move into the native toolbar.
            TitleBar.IsVisible = false;
            var root = new HorizontalStackLayout
            {
                Spacing = 8,
                VerticalOptions = LayoutOptions.Center,
                Children = { BackBtn, Pill, ReloadBtn }
            };
            NavigationPage.SetHasBackButton(this, false);
            NavigationPage.SetTitleView(this, root);
        }
#endif
        SetupCustomTitleBar();
        _appeared = true;
        if (_pendingUrl != null)
        {
            var url = _pendingUrl;
            _pendingUrl = null;
            var p = _profile;
            if (p != null && TargetUrl(p) == url)
                StartNavigation(p);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _appeared = false;
        CancelWatchdog();
        ResetTitleBar();
    }

    // Implemented in Platforms/Windows/BrowserPage.Windows.cs. On other
    // platforms these declarations vanish at compile time (no-op).
    partial void SetupCustomTitleBar();
    partial void ResetTitleBar();

    public void BindProfile(Profile p)
    {
        _profile = p;
        Drop.IsVisible = false;
        CancelWatchdog();
        HideSplashError();
        PillName.Text = p.Name;
        PillAvaBox.Children.Clear();
        PillAvaBox.Children.Add(AvatarView.Build(p, 26));
        SplashAvaBox.Children.Clear();
        SplashAvaBox.Children.Add(AvatarView.Build(p, 76));
        SplashName.Text = p.Name;
        SyncShield(p);

        Web.AdBlockEnabled = p.AdBlock;
        try { Web.PageHost = new Uri(TargetUrl(p)).Host; } catch { Web.PageHost = ""; }
        if (_appeared)
            StartNavigation(p);
        else
        {
            // Page isn't on screen yet: starting the load now races WebView
            // setup and the first navigation gets dropped (the stuck splash).
            // Park the URL; OnAppearing starts the load once we're live.
            _pendingUrl = TargetUrl(p);
            Splash.IsVisible = true;
        }

        try { Window.Title = $"{p.Name} – Home Deck"; } catch { }

        if (!_transient) { _store.Prefs.LastProfileId = p.Id; _store.SavePrefs(); }
        _ads.RefreshDays = _store.Prefs.ListRefreshDays;
        PushListsToAds();
        _ = _ads.EnsureLoadedAsync();
    }

    /// <summary>Push the user's list config (prefs) into the loader.</summary>
    void PushListsToAds()
        => _ads.ApplyConfig(_store.Prefs.DisabledLists, _store.Prefs.CustomLists, _store.Prefs.PickerRules);

    /// <summary>Picked element reported by page JS: count it and list it
    /// for this session only (no prefs, no engine reload). The element is
    /// already hidden/deleted live in the page by the picker overlay.
    /// Late reports for a previous page (host mismatch) are dropped.</summary>
    void OnPickerReported(string host, string selector, string mode)
    {
        try
        {
            // Frame-aware gate: reports arrive from any armed frame (top or
            // iframe, each with its own host), so gate on armed state, not
            // on matching the top page host. Stale reports can't outlive a
            // navigation: StartNavigation always disarms first.
            if (!_pickArmed)
            {
                _ads.NoteReportError($"report: dropped (picker not armed; from '{host}')");
                if (AdsPanel.IsVisible) RebuildAdsPanel();
                return;
            }
            if (!_sessionPicks.Any(r => r.Host == host && r.Selector == selector))
            {
                if (_sessionPicks.Count >= 500) return;
                _sessionPicks.Add(new PickerRule { Host = host, Selector = selector, Mode = mode });
                Web.AddPickedHidden();
                _ads.ClearReportError();
            }
            if (AdsPanel.IsVisible) RebuildAdsPanel();
        }
        catch (Exception e) { try { _ads.NoteReportError("report: page save threw (" + e.GetType().Name + ")"); } catch { } }
    }

    /// <summary>Cosmetic count pass finished (late passes included):
    /// refresh the open panel so it never shows a stale zero. The handler
    /// fires off the UI thread, so marshal here.</summary>
    void OnCountsChanged()
    {
        try { MainThread.BeginInvokeOnMainThread(() => { try { if (AdsPanel.IsVisible) RebuildAdsPanel(); } catch { } try { if (Drop.IsVisible && _blockSub != null) _blockSub.Text = BlockerSub(); } catch { } }); } catch { }
    }

    void RemovePickerRuleAsync(string host, string selector)
    {
        if (_updating) return;
        int n = _sessionPicks.RemoveAll(r => r.Host == host && r.Selector == selector);
        for (int i = 0; i < n; i++) Web.RemovePickedHidden();
        RebuildAdsPanel();
    }

    void SyncShield(Profile p)
    {
        PillShield.IsVisible = p.AdBlock;
        PillSlash.IsVisible = !p.AdBlock;
    }

    /// <summary>What this profile actually loads: its imported file slot
    /// (file://, fresh every navigation) or the URL. Falls back to the URL
    /// when File mode has no file yet — the caller shows the missing state.</summary>
    static string TargetUrl(Profile p)
    {
        try
        {
            if (p.UseLocal)
            {
                var f = HdFileGuard.HomepagePath(p.Id);
                if (File.Exists(f)) return new Uri(f).AbsoluteUri;
            }
        }
        catch { }
        return p.Url;
    }

    /// <summary>Native app-pick sheet provider for the bridge: native-only
    /// inventory + modal multi-select + per-site persist. Returns the
    /// picked result JSON (ok/data or ok:false). Runs on a bridge thread;
    /// the modal marshals its own UI. Never throws.</summary>
    async Task<string> PickAppsSheetAsync(string? host, string? callerUrl)
    {
        try
        {
            var siteKey = HdFileGuard.SiteKey(host, callerUrl, out var siteHash, out _);
            if (string.IsNullOrEmpty(siteKey))
                return "{\"ok\":false,\"error\":\"denied\"}";
            var apps = await AppInventory.GetInstalledAsync();
            var current = HdFileGuard.PickedAppIds(siteKey, siteHash);
            var picked = await AppPickPage.PickAsync(Navigation, apps, current);
            if (picked == null) return "{\"ok\":false,\"error\":\"cancelled\"}";
            HdFileGuard.SetAppPicks(siteKey, picked, siteHash);
            return AppInventory.PickedAppsJson(siteKey, siteHash);
        }
        catch (Exception e)
        {
            try { return "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(e.Message) + "}"; }
            catch { return "{\"ok\":false,\"error\":\"failed\"}"; }
        }
    }

    /// <summary>Transient popup page: loads the URL like a profile but is
    /// never stored and never becomes LastProfileId (see the guard in
    /// BindProfile). Back pops it; popups from popups nest the same way.</summary>
    public void BindPopup(string url)
    {
        _transient = true;
        string name;
        try
        {
            var u = new Uri(url);
            name = u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(u.LocalPath) ?? "Popup"
                : u.Host ?? "Popup";
        }
        catch { name = "Popup"; }
        if (string.IsNullOrEmpty(name)) name = "Popup";
        BindProfile(new Profile { Id = "popup-" + Guid.NewGuid().ToString("N"), Name = name, Url = url, UseLocal = false, AdBlock = _profile?.AdBlock ?? true });
    }

    /// <summary>Popup router (ask every time): Cancel / Same tab /
    /// New window. Runs on any thread; the prompt + push marshal to
    /// the UI thread. "New window" pushes a transient page itself and
    /// returns 2 (the platform then does nothing more); 1 means the
    /// platform loads it same-tab; anything else is cancel. Never throws.</summary>
    async Task<int> RoutePopupAsync(string url)
    {
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var opener = _profile?.Name;
                    if (string.IsNullOrEmpty(opener)) opener = string.IsNullOrEmpty(Web.PageHost) ? "This page" : Web.PageHost;
                    var choice = await DisplayActionSheetAsync($"{opener} wants to open {PopupDestName(url)}", "Cancel", null, "Same tab", "New window");
                    if (choice == "Same tab") return 1;
                    if (choice == "New window")
                    {
                        try
                        {
                            var page = new BrowserPage(_store, _ads);
                            page.BindPopup(url);
                            await Navigation.PushAsync(page);
                        }
                        catch { return 0; }
                        return 2;
                    }
                    return 0;
                }
                catch { return 0; }
            });
        }
        catch { return 0; }
    }

    static string PopupDestName(string url)
    {
        try
        {
            var u = new Uri(url);
            if (u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var n = Path.GetFileName(u.LocalPath);
                return string.IsNullOrEmpty(n) ? "a local file" : n;
            }
            if (!string.IsNullOrEmpty(u.Host)) return u.Host;
        }
        catch { }
        var s = (url ?? "").Trim();
        return s.Length <= 60 ? s : s.Substring(0, 60) + "…";
    }

    /// <summary>Begin (or re-begin) loading a profile URL. Only ever called
    /// while the page is on screen, so the WebView is ready to navigate.</summary>
    void StartNavigation(Profile p)
    {
        // Disarm first: kills the overlay everywhere, removes the auto-arm
        // snippet (new documents must not self-arm), then resets + navigates.
        CancelPickMode();
        var target = TargetUrl(p);
        if (p.UseLocal && target == p.Url)
        {
            // Slot empty: friendly error instead of a dead load. No
            // watchdog/kick — there is nothing in flight to watch.
            Splash.IsVisible = true;
            ShowSplashError("This profile's homepage file is missing — flip its card to Edit and Import one.");
            return;
        }
        Web.Source = new UrlWebViewSource { Url = target };
        Web.ResetBlockedCount();
        Web.ResetHiddenCount();
        _sessionPicks.Clear();
        Splash.IsVisible = true;
        _navStarted = false;
        SplashStatus.Text = "Loading…";
        SplashStatus.IsVisible = true;
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _ = WatchdogAsync(cts.Token, p);
        _ = KickMonitorAsync(cts.Token, p);
    }

    /// <summary>Proof-of-start monitor: appearing on screen doesn't mean the
    /// WebView is ready, and a too-early navigation is silently dropped. If no
    /// Navigating fires within a few seconds, re-set the URL to kick it.
    /// Silent, bounded, superseded by the next load.</summary>
    async Task KickMonitorAsync(CancellationToken token, Profile p)
    {
        try
        {
            for (int kick = 0; kick < 3; kick++)
            {
                await Task.Delay(3000, token);
                if (token.IsCancellationRequested) return;
                if (_navStarted) return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        if (!_navStarted && _profile == p)
                        {
                            SplashStatus.Text = "Retrying…";
                            SplashStatus.IsVisible = true;
                            Web.Source = null;
                            Web.Source = new UrlWebViewSource { Url = TargetUrl(p) };
                        }
                    }
                    catch { }
                });
            }
        }
        catch { } // superseded by a newer load
    }

    /// <summary>Splash watchdog: a stuck load becomes an error with actions
    /// instead of an eternal spinner. Superseded by the next BindProfile.</summary>
    void CancelWatchdog()
    {
        try { _loadCts?.Cancel(); } catch { }
        _loadCts = null;
    }

    async Task WatchdogAsync(CancellationToken token, Profile p)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), token); }
        catch { return; } // superseded by a newer load
        if (token.IsCancellationRequested) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Splash.IsVisible && !SplashError.IsVisible)
                    ShowSplashError($"Still loading {p.Name} — the page hasn't responded. Check your connection, then try again.");
            }
            catch { }
        });
    }

    void ShowSplashError(string msg)
    {
        try
        {
            SplashStatus.IsVisible = false;
            SplashError.Text = msg;
            SplashError.IsVisible = true;
            SplashRetry.IsVisible = true;
        }
        catch { }
    }

    void HideSplashError()
    {
        try
        {
            SplashError.IsVisible = false;
            SplashRetry.IsVisible = false;
        }
        catch { }
    }

    void OnSplashRetry(object? sender, EventArgs e)
    {
        var p = _profile;
        if (p == null) return;
        Web.Source = null; // force a fresh navigation even for the same URL
        BindProfile(p);
    }

    static string Glyph(Profile p)
        => string.IsNullOrEmpty(p.Name) ? "?" : p.Name[..1].ToUpper();

    protected override bool OnBackButtonPressed()
    {
        if (_pickArmed)
        {
            CancelPickMode();
            return true;
        }
        if (AdsPanel.IsVisible) { CloseAdsPanel(); return true; }
        if (Drop.IsVisible) { Drop.IsVisible = false; return true; }
        if (Web.CanGoBack) { Web.GoBack(); return true; }
        return base.OnBackButtonPressed();
    }

    async void OnBack(object? sender, EventArgs e)
    {
        if (Web.CanGoBack) Web.GoBack();
        else await Navigation.PopAsync();
    }

    void OnReload(object? sender, TappedEventArgs e)
    {
        try { Web.Reload(); } catch { }
    }

    void OnPillTap(object? sender, TappedEventArgs e)
    {
        if (Drop.IsVisible) { Drop.IsVisible = false; return; }
        RebuildDrop();
        Drop.IsVisible = true;
    }

    void RebuildDrop()
    {
        DropList.Children.Clear();
        var dim = (Color)Application.Current!.Resources["HdDim"];
        var others = _store.Profiles.Where(p => p != _profile).ToList();
        // Profile switcher: capped at ~4 rows, then scrolls.
        var rows = new VerticalStackLayout { Spacing = 2 };
        if (others.Count == 0)
        {
            rows.Children.Add(new Label
            {
                Text = "No other profiles",
                FontSize = 13,
                TextColor = dim,
                Padding = new Thickness(10, 8),
            });
        }
        foreach (var p in others)
        {
            var target = p;
            var row = new HorizontalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(10, 8),
                Children =
                {
                    AvatarView.Build(p, 26),
                    new Label
                    {
                        Text = p.Name,
                        FontSize = 13,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White,
                        VerticalOptions = LayoutOptions.Center,
                    },
                },
            };
            AddRowTap(row, () => { Drop.IsVisible = false; BindProfile(target); });
            rows.Children.Add(row);
        }
        DropList.Children.Add(new ScrollView { Content = rows, MaximumHeightRequest = 180 });
        DropList.Children.Add(MenuButton("Manage profiles\u2026", async () => await Navigation.PopAsync()));
        DropList.Children.Add(MenuDivider());

        if (_profile != null)
        {
            var p = _profile;
            DropList.Children.Add(MenuButton("Open in browser  \u2197", async () =>
            {
                Drop.IsVisible = false;
                try { await Launcher.OpenAsync(TargetUrl(p)); } catch { }
            }));
            DropList.Children.Add(MenuDivider());

            var cb = new HdCheckBox { IsChecked = p.AdBlock, VerticalOptions = LayoutOptions.Center };
            var texts = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand };
            texts.Children.Add(new Label { Text = "Block ads & trackers", TextColor = Colors.White, FontSize = 13 });
            _blockSub = new Label { Text = BlockerSub(), FontSize = 11, TextColor = Color.FromRgba(242, 237, 232, 110) };
            texts.Children.Add(_blockSub);
            cb.CheckedChanged += (_, on) =>
            {
                if (_profile == null) return;
                _profile.AdBlock = on;
                Web.AdBlockEnabled = on;
                SyncShield(_profile);
                _store.SaveProfiles();
                try { if (_blockSub != null) _blockSub.Text = BlockerSub(); } catch { }
            };
            texts.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => cb.IsChecked = !cb.IsChecked) });
            var brow = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 8) };
            brow.Children.Add(cb);
            brow.Children.Add(texts);
            AddHoverBg(brow);
            DropList.Children.Add(brow);
            DropList.Children.Add(MenuDivider());

            DropList.Children.Add(MenuButton("Hide element\u2026", async () => await ArmPickerAsync()));
            DropList.Children.Add(MenuDivider());

            DropList.Children.Add(MenuButton("Advanced settings\u2026", async () => { Drop.IsVisible = false; OpenAdsPanel(); await Task.CompletedTask; }));
        }
    }

    string BlockerSub()
    {
        try
        {
            if (_profile == null || !_profile.AdBlock) return "Off";
            return $"On \u2014 Blocked {Web.BlockedCount} \u00b7 Hidden {Web.HiddenCount} on-page";
        }
        catch { return ""; }
    }

    /// <summary>Arm the element picker with a wait cursor (Windows) and an
    /// exact failure dialog. Shared by the menu entry and the panel button.</summary>
    async Task ArmPickerAsync()
    {
        Drop.IsVisible = false;
        if (AdsPanel.IsVisible) CloseAdsPanel();
        if (_profile == null) return;
#if WINDOWS
        if (Web.Handler is ShellWebViewHandler cur0) cur0.SetWaitCursor(true);
#endif
        string res = "fail: shell: no platform view (shell not attached)";
        try
        {
            _pickArmed = false;
            if (Web.Handler is ShellWebViewHandler h) { res = await h.StartPickerModeAsync(); _pickArmed = res == "ok"; }
        }
        catch (Exception e) { _pickArmed = false; res = "fail: shell: arm call crashed (" + e.GetType().Name + ": " + e.Message + ")"; }
#if WINDOWS
        try { if (Web.Handler is ShellWebViewHandler cur1) cur1.SetWaitCursor(false); } catch { }
#endif
        if (res != "ok") { PickCancelBtn.IsVisible = false; await SayPickAsync(res); }
        else PickCancelBtn.IsVisible = true;
    }

    /// <summary>Shared pick-mode exit: native overlay button, system back,
    /// and window-level Esc all land here. Never depends on the page.</summary>
    void CancelPickMode()
    {
        _pickArmed = false;
        PickCancelBtn.IsVisible = false;
        try { if (Web.Handler is ShellWebViewHandler h) h.CancelPickerMode(); } catch { }
    }

    void OnPickCancel(object? sender, EventArgs e) => CancelPickMode();

    /// <summary>Window-level Esc: consumed only while pick mode is armed
    /// (the page's own Esc listener never fires with focus in an iframe).</summary>
    bool OnPickEsc()
    {
        if (!_pickArmed) return false;
        CancelPickMode();
        return true;
    }

    /// <summary>Picker arm/report failure dialog. DisplayAlert itself is
    /// wrapped: if the dialog can't show, there is no deeper fallback.</summary>
    async Task SayPickAsync(string msg)
    {
        try { await DisplayAlert("Element picker", msg, "OK"); } catch { }
    }

    /// <summary>Mock-style hover fill for menu rows (transparent buttons show
    /// no native hover on Windows, so paint it explicitly).</summary>
    static void AddHoverBg(View row)
    {
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => row.BackgroundColor = Color.FromRgba(255, 255, 255, 20);
        hover.PointerExited += (_, _) => row.BackgroundColor = Colors.Transparent;
        row.GestureRecognizers.Add(hover);
    }

    void OnAdsDimTap(object? sender, TappedEventArgs e) => CloseAdsPanel();

    void OpenAdsPanel()
    {
        if (_profile == null) return;
        Drop.IsVisible = false;
        _lastUpdateFailed = false;
        RebuildAdsPanel();
        AdsDim.IsVisible = true;
        AdsPanel.IsVisible = true;
    }

    void CloseAdsPanel()
    {
        AdsPanel.IsVisible = false;
        AdsDim.IsVisible = false;
    }

    /// <summary>Adblock settings panel body: status toggle + count, filter
    /// lists, Update now, refresh cadence, Done. Rebuilt on open,
    /// after cadence/toggle changes, and after updates.</summary>
    void RebuildAdsPanel()
    {
        var p = _profile;
        AdsPanelBody.Children.Clear();
        if (p == null) { CloseAdsPanel(); return; }
        var dim = (Color)Application.Current!.Resources["HdDim"];
        var gold = (Color)Application.Current.Resources["HdGold"];
        var bg = (Color)Application.Current.Resources["HdBg"];
        AdsPanelBody.Children.Add(new Label
        {
            Text = "Advanced settings",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
        });

        // ---------- AD BLOCKING ----------
        AddSection("AD BLOCKING", dim);
        var tcb = new HdCheckBox { IsChecked = p.AdBlock, VerticalOptions = LayoutOptions.Center };
        var tlab = new Label
        {
            Text = "Block ads & trackers",
            TextColor = Colors.White,
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.StartAndExpand,
        };
        tlab.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => tcb.IsChecked = !tcb.IsChecked)
        });
        tcb.CheckedChanged += (_, on) =>
        {
            p.AdBlock = on;
            Web.AdBlockEnabled = on;
            SyncShield(p);
            _store.SaveProfiles();
        };
        var trow = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 8) };
        trow.Children.Add(tcb);
        trow.Children.Add(tlab);
        AddHoverBg(trow);
        AdsPanelBody.Children.Add(trow);
        AdsPanelBody.Children.Add(new Label
        {
            Text = $"Blocked {Web.BlockedCount} network \u00b7 Hidden {Web.HiddenCount} on-page",
            FontSize = 11,
            TextColor = Color.FromRgba(242, 237, 232, 110),
            Padding = new Thickness(10, 0, 10, 4),
        });
        AdsPanelBody.Children.Add(new Label
        {
            Text = _ads.Ready ? $"Engine ready \u00b7 {_ads.LoadedRuleCount:N0} rules" : "Engine not ready \u2014 blocking off",
            FontSize = 11,
            TextColor = dim,
            Padding = new Thickness(10, 0, 10, 4),
        });
        AdsPanelBody.Children.Add(MenuDivider());
        AdsPanelBody.Children.Add(new Label
        {
            Text = "FILTER LISTS",
            FontSize = 11,
            TextColor = dim,
            Padding = new Thickness(10, 4, 10, 0),
        });
        var rows = new VerticalStackLayout { Spacing = 2 };
        foreach (var ls in _ads.GetListStatus())
            rows.Children.Add(AdsListRow(ls, dim));
        var listScroll = new ScrollView
        {
            Content = rows,
            HeightRequest = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
        };
        AdsPanelBody.Children.Add(new Border
        {
            Stroke = Color.FromRgba(255, 255, 255, 45),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(4),
            Content = listScroll,
        });
        var upd = new Button
        {
            Text = _updating ? "Updating\u2026" : "Update now",
            BackgroundColor = gold,
            TextColor = bg,
            CornerRadius = 10,
            IsEnabled = !_updating,
        };
        upd.Clicked += async (_, _) => await UpdateListsAsync();
        AdsPanelBody.Children.Add(upd);
        AdsPanelBody.Children.Add(new Label
        {
            Text = "Some lists couldn\u2019t download \u2014 kept the current blocking.",
            FontSize = 12,
            TextColor = Color.FromArgb("#E14B32"),
            IsVisible = _lastUpdateFailed,
        });
        AdsPanelBody.Children.Add(new Label
        {
            Text = "CUSTOM LISTS",
            FontSize = 11,
            TextColor = dim,
            Padding = new Thickness(10, 8, 10, 0),
        });
        var nameEntry = new Entry
        {
            Placeholder = "Name (e.g. my-filters)",
            TextColor = Colors.White,
            PlaceholderColor = dim,
            FontSize = 13,
            Margin = new Thickness(10, 2, 10, 0),
        };
        var urlEntry = new Entry
        {
            Placeholder = "https://\u2026/list.txt",
            TextColor = Colors.White,
            PlaceholderColor = dim,
            FontSize = 13,
            Keyboard = Keyboard.Url,
            Margin = new Thickness(10, 2, 10, 0),
        };
        var addErr = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#E14B32"),
            IsVisible = false,
            Padding = new Thickness(10, 2, 10, 0),
        };
        var addBtn = new Button
        {
            Text = "Add list",
            FontSize = 13,
            CornerRadius = 8,
            Padding = new Thickness(14, 7),
            BackgroundColor = Colors.Transparent,
            TextColor = dim,
            BorderWidth = 1,
            BorderColor = dim,
            Margin = new Thickness(10, 6, 10, 0),
        };
        addBtn.Clicked += async (_, _) => await AddCustomAsync(nameEntry.Text ?? "", urlEntry.Text ?? "", addErr);
        AdsPanelBody.Children.Add(nameEntry);
        AdsPanelBody.Children.Add(urlEntry);
        AdsPanelBody.Children.Add(addErr);
        AdsPanelBody.Children.Add(addBtn);
        AdsPanelBody.Children.Add(new Label
        {
            Text = "Refresh lists",
            FontSize = 13,
            TextColor = Colors.White,
            Padding = new Thickness(10, 8, 10, 0),
        });
        var crow = new HorizontalStackLayout { Spacing = 7, HorizontalOptions = LayoutOptions.Center };
        foreach (var (label, days) in new[] { ("Daily", 1), ("Weekly", 7), ("Monthly", 30) })
        {
            bool sel = _store.Prefs.ListRefreshDays == days;
            var b = new Button
            {
                Text = label,
                FontSize = 13,
                CornerRadius = 8,
                Padding = new Thickness(14, 7),
                BackgroundColor = sel ? gold : Colors.Transparent,
                TextColor = sel ? bg : dim,
                BorderWidth = sel ? 0 : 1,
                BorderColor = dim,
            };
            int d = days;
            b.Clicked += (_, _) =>
            {
                _store.Prefs.ListRefreshDays = d;
                _store.SavePrefs();
                _ads.RefreshDays = d;
                RebuildAdsPanel();
            };
            crow.Children.Add(b);
        }
        AdsPanelBody.Children.Add(crow);

        // ---------- HIDDEN ELEMENTS ----------
        AddSection("HIDDEN ELEMENTS", dim);
        var picks = _sessionPicks;
        if (!string.IsNullOrEmpty(_ads.LastReportError))
        {
            AdsPanelBody.Children.Add(new Label
            {
                Text = "Last save failed: " + _ads.LastReportError,
                FontSize = 12,
                TextColor = Color.FromArgb("#E14B32"),
                Padding = new Thickness(10, 2, 10, 0),
            });
        }
        if (picks.Count == 0)
        {
            AdsPanelBody.Children.Add(new Label
            {
                Text = "Nothing picked yet \u2014 Hide element\u2026 in the menu.",
                FontSize = 11,
                TextColor = dim,
                Padding = new Thickness(10, 2, 10, 0),
            });
        }
        else
        {
            foreach (var r in picks)
            {
                string host = r.Host, selector = r.Selector;
                string shown = selector.Length > 80 ? selector.Substring(0, 80) + "\u2026" : selector;
                if (r.Mode == "remove") shown += " \u00b7 removed";
                var texts = new VerticalStackLayout
                {
                    Spacing = 0,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.StartAndExpand,
                };
                texts.Children.Add(new Label { Text = host, FontSize = 13, TextColor = Colors.White });
                texts.Children.Add(new Label { Text = shown, FontSize = 11, TextColor = dim });
                var rm = new Button
                {
                    Text = "\u2715",
                    FontSize = 12,
                    TextColor = dim,
                    BackgroundColor = Colors.Transparent,
                    BorderWidth = 0,
                    Padding = new Thickness(8, 2),
                    VerticalOptions = LayoutOptions.Center,
                };
                rm.Clicked += async (_, _) =>
                {
                    bool go = await DisplayAlert("Remove rule?",
                        $"Stop hiding this on {host}? It will reappear after you reload the page.", "Remove", "Keep");
                    if (go) RemovePickerRuleAsync(host, selector);
                };
                var prow = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
                prow.Children.Add(texts);
                prow.Children.Add(rm);
                AddHoverBg(prow);
                AdsPanelBody.Children.Add(prow);
            }
        }
        var pickBtn = new Button
        {
            Text = "\uFF0B Pick an element",
            FontSize = 13,
            CornerRadius = 8,
            Padding = new Thickness(14, 7),
            BackgroundColor = Colors.Transparent,
            TextColor = dim,
            BorderWidth = 1,
            BorderColor = dim,
            Margin = new Thickness(10, 6, 10, 0),
        };
        pickBtn.Clicked += async (_, _) => await ArmPickerAsync();
        AdsPanelBody.Children.Add(pickBtn);

        // ---------- PERMISSIONS ----------
        AddSection("PERMISSIONS", dim);
        string curKey = CurrentSiteKey(out string? _);
        foreach (var perm in HdFileGuard.Perms.All)
            AddPermRow(perm, curKey, dim);
        AdsPanelBody.Children.Add(GroupLabel("SYSTEM", dim));
        var sysText = new Label { Text = "System notifications: \u2026", FontSize = 12, TextColor = dim, Padding = new Thickness(10, 2, 10, 0) };
        AdsPanelBody.Children.Add(sysText);
        var sysBtn = new Button
        {
            Text = "Open notification settings",
            FontSize = 13,
            CornerRadius = 8,
            Padding = new Thickness(14, 7),
            BackgroundColor = Colors.Transparent,
            TextColor = dim,
            BorderWidth = 1,
            BorderColor = dim,
            Margin = new Thickness(10, 6, 10, 0),
        };
        sysBtn.Clicked += async (_, _) => { try { await Web.EvaluateJavaScriptAsync("HomeDeck.openNotificationSettings()"); } catch { } };
        AdsPanelBody.Children.Add(sysBtn);
        _ = Task.Run(async () =>
        {
            try
            {
                var raw = await Web.EvaluateJavaScriptAsync("HomeDeck.notificationAccess()");
                string? inner = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw ?? "");
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                        inner = doc.RootElement.GetString();
                }
                catch { }
                bool on = inner != null && inner.Contains("\\"enabled\\":true");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try { sysText.Text = "System notifications: " + (on ? "on" : "off \u2014 turn on in Settings, then pages can ask"); } catch { }
                });
            }
            catch { }
        });

        // ---------- BRIDGE ----------
        AddSection("BRIDGE", dim);
        string transport = "\u2014";
        string? initErr = null;
        try
        {
            if (Web.Handler is ShellWebViewHandler hb)
            {
                transport = hb.BridgeStatus();
                initErr = hb.BridgeInitError;
            }
        }
        catch { }
        AdsPanelBody.Children.Add(new Label
        {
            Text = "Transport: " + transport,
            FontSize = 12,
            TextColor = Colors.White,
            Padding = new Thickness(10, 2, 10, 0),
        });
        if (!string.IsNullOrEmpty(initErr))
        {
            AdsPanelBody.Children.Add(new Label
            {
                Text = "Bridge: " + initErr,
                FontSize = 12,
                TextColor = Color.FromArgb("#E14B32"),
                Padding = new Thickness(10, 2, 10, 0),
            });
        }

        // ---------- APPS ----------
        AddSection("APPS", dim);
        AddAppSection(curKey, dim);

        var done = new Button
        {
            Text = "Done",
            BackgroundColor = gold,
            TextColor = bg,
            CornerRadius = 10,
        };
        done.Clicked += (_, _) => CloseAdsPanel();
        AdsPanelBody.Children.Add(done);
    }

    void AddSection(string text, Color dim)
    {
        AdsPanelBody.Children.Add(MenuDivider());
        AdsPanelBody.Children.Add(new Label
        {
            Text = text,
            FontSize = 11,
            TextColor = dim,
            Padding = new Thickness(10, 8, 10, 0),
        });
    }

    Label GroupLabel(string text, Color dim) => new()
    {
        Text = text,
        FontSize = 11,
        TextColor = dim,
        Padding = new Thickness(10, 6, 10, 0),
    };

    /// Current profile's site key (permissions + apps resolve through it).
    string CurrentSiteKey(out string? siteHash)
    {
        siteHash = null;
        try
        {
            string? h = (_profile != null && _profile.UseLocal) ? "local" : Web.PageHost;
            string? u = (Web.Source as UrlWebViewSource)?.Url;
            return HdFileGuard.SiteKey(h, u, out siteHash, out _);
        }
        catch { return ""; }
    }

    void AddPermRow((string Id, string Title, string Sub) perm, string curKey, Color dim)
    {
        int n = HdFileGuard.PermAllowedCount(perm.Id);
        bool open = _openPerm == perm.Id;
        var texts = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand };
        texts.Children.Add(new Label { Text = perm.Title, TextColor = Colors.White, FontSize = 13 });
        texts.Children.Add(new Label { Text = $"{perm.Sub} \u00b7 {n} site{(n == 1 ? "" : "s")}", FontSize = 11, TextColor = dim });
        var chev = new Label { Text = open ? "\u2304" : "\u203a", FontSize = 16, TextColor = dim, VerticalOptions = LayoutOptions.Center, Padding = new Thickness(12, 2) };
        var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
        row.Children.Add(texts);
        row.Children.Add(chev);
        var id = perm.Id;
        AddRowTap(row, () => { _openPerm = open ? null : id; RebuildAdsPanel(); });
        AdsPanelBody.Children.Add(row);
        if (!open) return;
        var sites = HdFileGuard.PermSites(id);
        var box = new VerticalStackLayout { Spacing = 2 };
        bool anyMine = curKey.Length > 0 && sites.Any(s => s.SiteKey == curKey);
        if (anyMine) box.Children.Add(GroupLabel("THIS PROFILE", dim));
        foreach (var s in sites.Where(s => s.SiteKey == curKey && curKey.Length > 0))
            box.Children.Add(PermSiteRow(id, s, dim));
        if (anyMine && sites.Any(s => s.SiteKey != curKey)) box.Children.Add(GroupLabel("OTHER SITES", dim));
        foreach (var s in sites.Where(s => s.SiteKey != curKey || curKey.Length == 0))
            box.Children.Add(PermSiteRow(id, s, dim));
        if (sites.Count == 0)
            box.Children.Add(new Label { Text = "No sites yet \u2014 first use asks.", FontSize = 12, TextColor = dim, Padding = new Thickness(10, 2, 10, 4) });
        AdsPanelBody.Children.Add(new ScrollView { Content = box, MaximumHeightRequest = 170 });
    }

    View PermSiteRow(string permId, HdFileGuard.SitePerm s, Color dim)
    {
        var texts = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand };
        texts.Children.Add(new Label { Text = s.Display, TextColor = Colors.White, FontSize = 13 });
        texts.Children.Add(new Label { Text = s.Allowed ? "Allowed" : "Off", FontSize = 11, TextColor = s.Allowed ? dim : Color.FromArgb("#E14B32") });
        var cb = new HdCheckBox { IsChecked = s.Allowed, VerticalOptions = LayoutOptions.Center };
        cb.CheckedChanged += (_, on) => { HdFileGuard.SetPermOff(s.SiteKey, permId, !on); RebuildAdsPanel(); };
        texts.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => cb.IsChecked = !cb.IsChecked) });
        var rm = new Button
        {
            Text = "\u2715",
            FontSize = 12,
            TextColor = dim,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(8, 2),
            VerticalOptions = LayoutOptions.Center,
        };
        rm.Clicked += async (_, _) =>
        {
            bool go = await DisplayAlert("Revoke permission?", $"Forget this site\u2019s {permId} grant? Next use asks again.", "Revoke", "Keep");
            if (go) { HdFileGuard.DeletePermEntry(s.SiteKey, permId); RebuildAdsPanel(); }
        };
        var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
        row.Children.Add(texts);
        row.Children.Add(cb);
        row.Children.Add(rm);
        AddHoverBg(row);
        return row;
    }

    void AddAppSection(string curKey, Color dim)
    {
        var all = HdFileGuard.AllPickedApps();
        Dictionary<string, bool> running = new();
        try { running = AppInventory.RunningFlags(all.Select(a => a.Id)); } catch { }
        foreach (var a in all)
        {
            var sites = HdFileGuard.AppSites(a.Id);
            bool open = _openApp == a.Id;
            string runMark = running.TryGetValue(a.Id, out var r) && r ? " \u00b7 running" : "";
            var texts = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand };
            texts.Children.Add(new Label { Text = a.Name, TextColor = Colors.White, FontSize = 13 });
            texts.Children.Add(new Label { Text = $"{sites.Count} site{(sites.Count == 1 ? "" : "s")}{runMark}", FontSize = 11, TextColor = dim });
            var chev = new Label { Text = open ? "\u2304" : "\u203a", FontSize = 16, TextColor = dim, VerticalOptions = LayoutOptions.Center, Padding = new Thickness(12, 2) };
            var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
            row.Children.Add(texts);
            row.Children.Add(chev);
            var aid = a.Id;
            AddRowTap(row, () => { _openApp = open ? null : aid; RebuildAdsPanel(); });
            AdsPanelBody.Children.Add(row);
            if (!open) continue;
            var box = new VerticalStackLayout { Spacing = 2 };
            bool anyMine = curKey.Length > 0 && sites.Any(s => s.SiteKey == curKey);
            if (anyMine) box.Children.Add(GroupLabel("THIS PROFILE", dim));
            foreach (var s in sites.Where(s => s.SiteKey == curKey && curKey.Length > 0))
                box.Children.Add(AppSiteRow(aid, s, dim));
            if (anyMine && sites.Any(s => s.SiteKey != curKey)) box.Children.Add(GroupLabel("OTHER SITES", dim));
            foreach (var s in sites.Where(s => s.SiteKey != curKey || curKey.Length == 0))
                box.Children.Add(AppSiteRow(aid, s, dim));
            AdsPanelBody.Children.Add(new ScrollView { Content = box, MaximumHeightRequest = 170 });
        }
        if (all.Count == 0)
            AdsPanelBody.Children.Add(new Label { Text = "Nothing picked yet \u2014 Choose\u2026 opens the app picker.", FontSize = 12, TextColor = dim, Padding = new Thickness(10, 2, 10, 4) });
        var choose = new Button
        {
            Text = "Choose\u2026",
            FontSize = 13,
            CornerRadius = 8,
            Padding = new Thickness(14, 7),
            BackgroundColor = Colors.Transparent,
            TextColor = dim,
            BorderWidth = 1,
            BorderColor = dim,
            Margin = new Thickness(10, 6, 10, 0),
        };
        choose.Clicked += async (_, _) =>
        {
            try
            {
                string? h = (_profile != null && _profile.UseLocal) ? "local" : Web.PageHost;
                string? u = (Web.Source as UrlWebViewSource)?.Url;
                await PickAppsSheetAsync(h, u);
            }
            catch { }
            RebuildAdsPanel();
        };
        AdsPanelBody.Children.Add(choose);
        var clear = new Button
        {
            Text = "Clear this profile\u2019s apps",
            FontSize = 13,
            CornerRadius = 8,
            Padding = new Thickness(14, 7),
            BackgroundColor = Colors.Transparent,
            TextColor = dim,
            BorderWidth = 1,
            BorderColor = dim,
            Margin = new Thickness(10, 6, 10, 0),
        };
        clear.Clicked += async (_, _) =>
        {
            try
            {
                string ck = CurrentSiteKey(out string? ch);
                if (string.IsNullOrEmpty(ck)) return;
                bool go = await DisplayAlert("Clear picked apps?", "Forget this profile\u2019s picked apps?", "Clear", "Keep");
                if (go) HdFileGuard.SetAppPicks(ck, new List<HdFileGuard.AppRef>(), ch);
            }
            catch { }
            RebuildAdsPanel();
        };
        AdsPanelBody.Children.Add(clear);
    }

    View AppSiteRow(string appId, HdFileGuard.AppSite s, Color dim)
    {
        var texts = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand };
        texts.Children.Add(new Label { Text = s.Display, TextColor = Colors.White, FontSize = 13 });
        texts.Children.Add(new Label { Text = s.Enabled ? "Allowed" : "Off", FontSize = 11, TextColor = s.Enabled ? dim : Color.FromArgb("#E14B32") });
        var cb = new HdCheckBox { IsChecked = s.Enabled, VerticalOptions = LayoutOptions.Center };
        cb.CheckedChanged += (_, on) => { HdFileGuard.SetAppOff(s.SiteKey, null, appId, !on, true); RebuildAdsPanel(); };
        texts.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => cb.IsChecked = !cb.IsChecked) });
        var rm = new Button
        {
            Text = "\u2715",
            FontSize = 12,
            TextColor = dim,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            Padding = new Thickness(8, 2),
            VerticalOptions = LayoutOptions.Center,
        };
        rm.Clicked += async (_, _) =>
        {
            bool go = await DisplayAlert("Unpick app?", $"Stop letting this site open {s.Display}?", "Remove", "Keep");
            if (go) { HdFileGuard.RemoveAppPick(s.SiteKey, appId); RebuildAdsPanel(); }
        };
        var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
        row.Children.Add(texts);
        row.Children.Add(cb);
        row.Children.Add(rm);
        AddHoverBg(row);
        return row;
    }

    View AdsListRow(AdBlockService.ListStatus s, Color dim)
    {
        var cb = new HdCheckBox { IsChecked = s.Enabled };
        var texts = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.StartAndExpand,
        };
        texts.Children.Add(new Label { Text = s.Name, FontSize = 13, TextColor = Colors.White });
        string sub = !s.Enabled ? "Off"
            : !s.HasCache ? "Not downloaded yet"
            : s.UpdatedUtc == null ? "Saved copy, age unknown"
            : $"Updated {RelAge(s.UpdatedUtc.Value)} · {FmtSize(s.Bytes)}";
        texts.Children.Add(new Label
        {
            Text = sub,
            FontSize = 11,
            TextColor = s.Enabled && !s.HasCache ? Color.FromArgb("#E14B32") : dim,
        });
        // Text taps toggle the box (split zones: no row-level toggle, nested
        // taps would fire twice — same pattern as the ad row above).
        var tap = new TapGestureRecognizer { Command = new Command(() => cb.IsChecked = !cb.IsChecked) };
        texts.GestureRecognizers.Add(tap);
        cb.CheckedChanged += async (_, on) => await ToggleListAsync(s.Name, s.IsCustom, on);
        var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
        row.Children.Add(cb);
        row.Children.Add(texts);
        if (s.IsCustom)
        {
            var rm = new Button
            {
                Text = "✕",
                FontSize = 12,
                TextColor = dim,
                BackgroundColor = Colors.Transparent,
                BorderWidth = 0,
                Padding = new Thickness(8, 2),
                VerticalOptions = LayoutOptions.Center,
            };
            string name = s.Name;
            rm.Clicked += async (_, _) =>
            {
                bool go = await DisplayAlert("Remove list?",
                    $"Remove \"{name}\" and delete its downloaded copy?", "Remove", "Keep");
                if (go) await RemoveCustomAsync(name);
            };
            row.Children.Add(rm);
        }
        AddHoverBg(row);
        return row;
    }

    async Task ToggleListAsync(string name, bool isCustom, bool on)
    {
        if (_updating) return;
        if (isCustom)
        {
            var c = _store.Prefs.CustomLists.FirstOrDefault(x => x.Name == name);
            if (c == null) return;
            c.Enabled = on;
        }
        else if (on)
            _store.Prefs.DisabledLists.Remove(name);
        else if (!_store.Prefs.DisabledLists.Contains(name))
            _store.Prefs.DisabledLists.Add(name);
        _store.SavePrefs();
        PushListsToAds();
        if (on)
            await UpdateListsAsync(); // newly enabled: fetch what's missing, same as Update now
        else
        {
            await _ads.ReloadFromCacheAsync(); // instant: drop its rules, no network
            RebuildAdsPanel();
        }
    }

    async Task RemoveCustomAsync(string name)
    {
        if (_updating) return;
        _store.Prefs.CustomLists.RemoveAll(x => x.Name == name);
        _store.SavePrefs();
        PushListsToAds();
        _ads.DeleteCacheFor(AdBlockService.CacheKey(name));
        await _ads.ReloadFromCacheAsync();
        RebuildAdsPanel();
    }

    async Task AddCustomAsync(string name, string url, Label err)
    {
        if (_updating) return;
        err.IsVisible = false;
        name = name.Trim();
        url = url.Trim();
        string? fail = ValidateCustom(name, url);
        if (fail != null) { err.Text = fail; err.IsVisible = true; return; }
        _store.Prefs.CustomLists.Add(new CustomList { Name = name, Url = url, Enabled = true });
        _store.SavePrefs();
        PushListsToAds();
        await UpdateListsAsync(); // downloads the new list, rebuilds, sets the failure flag
    }

    string? ValidateCustom(string name, string url)
    {
        if (name.Length == 0) return "Give the list a name.";
        var key = AdBlockService.CacheKey(name);
        if (_ads.GetListStatus().Any(s => AdBlockService.CacheKey(s.Name) == key))
            return "A list with that name already exists.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return "URL must start with http:// or https://.";
        return null;
    }

    static string RelAge(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays} days ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }

    static string FmtSize(long bytes)
        => bytes < 1024 * 1024 ? $"{bytes / 1024} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";

    async Task UpdateListsAsync()
    {
        if (_updating) return;
        _updating = true;
        _lastUpdateFailed = false;
        _ads.RefreshDays = _store.Prefs.ListRefreshDays;
        RebuildAdsPanel();
        try { await _ads.EnsureLoadedAsync(force: true); }
        catch { }
        _updating = false;
        try { _lastUpdateFailed = _ads.GetListStatus().Any(s => s.Enabled && !s.HasCache); }
        catch { _lastUpdateFailed = true; }
        RebuildAdsPanel();
    }

    /// <summary>De-boxed menu button (native button borders show through on
    /// Windows, so kill them explicitly). Slim mock-style padding.</summary>
    static Button MenuButton(string text, Func<Task> onTap)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 13,
            Padding = new Thickness(10, 8),
            BorderWidth = 0,
            BackgroundColor = Colors.Transparent,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Fill,
        };
        b.Clicked += async (_, _) => await onTap();
        AddHoverBg(b);
        return b;
    }

    /// <summary>Mock .wsep: hairline divider between menu sections.</summary>
    static BoxView MenuDivider() => new()
    {
        HeightRequest = 1,
        Color = Color.FromRgba(255, 255, 255, 0.08),
        Margin = new Thickness(4, 5),
        HorizontalOptions = LayoutOptions.Fill,
    };

    /// <summary>Borderless tap row with mock-style hover feedback.</summary>
    static void AddRowTap(HorizontalStackLayout row, Action onTap)
    {
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        AddHoverBg(row);
    }
}
