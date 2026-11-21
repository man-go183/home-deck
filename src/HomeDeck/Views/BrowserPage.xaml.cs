using HomeDeck.Controls;
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
#if ANDROID
    bool _titleViewSet;
#endif

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
                Children = { BackBtn, Pill }
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
            if (p != null && p.Url == url)
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
        try { Web.PageHost = new Uri(p.Url).Host; } catch { Web.PageHost = ""; }
        if (_appeared)
            StartNavigation(p);
        else
        {
            // Page isn't on screen yet: starting the load now races WebView
            // setup and the first navigation gets dropped (the stuck splash).
            // Park the URL; OnAppearing starts the load once we're live.
            _pendingUrl = p.Url;
            Splash.IsVisible = true;
        }

        try { Window.Title = $"{p.Name} – Home Deck"; } catch { }

        _store.Prefs.LastProfileId = p.Id;
        _store.SavePrefs();
        _ = _ads.EnsureLoadedAsync();
    }

    void SyncShield(Profile p)
    {
        PillShield.IsVisible = p.AdBlock;
        PillSlash.IsVisible = !p.AdBlock;
    }

    /// <summary>Begin (or re-begin) loading a profile URL. Only ever called
    /// while the page is on screen, so the WebView is ready to navigate.</summary>
    void StartNavigation(Profile p)
    {
        Web.Source = new UrlWebViewSource { Url = p.Url };
        Web.ResetBlockedCount();
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
                            Web.Source = new UrlWebViewSource { Url = p.Url };
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
        if (Web.CanGoBack) { Web.GoBack(); return true; }
        return base.OnBackButtonPressed();
    }

    async void OnBack(object? sender, EventArgs e)
    {
        if (Web.CanGoBack) Web.GoBack();
        else await Navigation.PopAsync();
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
        var others = _store.Profiles.Where(p => p != _profile).ToList();
        if (others.Count == 0)
        {
            // Mock state with a single profile: dimmed, non-clickable row.
            DropList.Children.Add(new Label
            {
                Text = "No other profiles",
                FontSize = 13,
                TextColor = (Color)Application.Current!.Resources["HdDim"],
                Padding = new Thickness(10, 8),
            });
        }
        foreach (var p in others)
        {
            // Mock profile row: avatar + name (no ◈ prefix — the pill
            // shield already shows state), borderless with hover feedback.
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
            DropList.Children.Add(row);
        }
        DropList.Children.Add(MenuButton("Manage profiles…", async () => await Navigation.PopAsync()));
        DropList.Children.Add(MenuDivider());

        if (_profile != null)
        {
            var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 8) };
            var cb = new HdCheckBox { IsChecked = _profile.AdBlock, VerticalOptions = LayoutOptions.Center };
            var lab = new Label { Text = "Block ads & trackers", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center };
            cb.CheckedChanged += (_, on) =>
            {
                if (_profile == null) return;
                _profile.AdBlock = on;
                Web.AdBlockEnabled = on;
                SyncShield(_profile);
                _store.SaveProfiles();
            };
            row.Children.Add(cb); row.Children.Add(lab);
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => cb.IsChecked = !cb.IsChecked)
            });
            AddHoverBg(row);
            DropList.Children.Add(row);
            if (_profile.AdBlock)
            {
                // Subtitle-style proof row: smaller + fainter than menu text.
                DropList.Children.Add(new Label
                {
                    Text = $"Blocked {Web.BlockedCount} on this page",
                    FontSize = 11,
                    TextColor = Color.FromRgba(242, 237, 232, 110),
                    Padding = new Thickness(10, 0, 10, 4),
                });
            }
            DropList.Children.Add(MenuDivider());

            DropList.Children.Add(MenuButton("Open in browser  ↗", async () =>
            {
                Drop.IsVisible = false;
                if (_profile != null)
                {
                    try { await Launcher.OpenAsync(_profile.Url); } catch { }
                }
            }));
        }
    }

    /// <summary>Mock-style hover fill for menu rows (transparent buttons show
    /// no native hover on Windows, so paint it explicitly).</summary>
    static void AddHoverBg(VisualElement row)
    {
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => row.BackgroundColor = Color.FromRgba(255, 255, 255, 20);
        hover.PointerExited += (_, _) => row.BackgroundColor = Colors.Transparent;
        row.GestureRecognizers.Add(hover);
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
