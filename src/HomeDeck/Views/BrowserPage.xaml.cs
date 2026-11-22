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
    bool _updating;
    bool _lastUpdateFailed;
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
        _ads.RefreshDays = _store.Prefs.ListRefreshDays;
        PushListsToAds();
        _ = _ads.EnsureLoadedAsync();
    }

    /// <summary>Push the user's list config (prefs) into the loader.</summary>
    void PushListsToAds()
        => _ads.ApplyConfig(_store.Prefs.DisabledLists, _store.Prefs.CustomLists);

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
            var lab = new Label { Text = "Block ads & trackers", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand };
            cb.CheckedChanged += (_, on) =>
            {
                if (_profile == null) return;
                _profile.AdBlock = on;
                Web.AdBlockEnabled = on;
                SyncShield(_profile);
                _store.SaveProfiles();
            };
            row.Children.Add(cb); row.Children.Add(lab);
            // Split tap zones (nested taps both fire): cb + label toggle via
            // the label's own tap below; the chevron opens the settings panel.
            // (No row-level toggle — it would misfire into the chevron.)
            lab.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => cb.IsChecked = !cb.IsChecked)
            });
            var chev = new Label
            {
                Text = "›",
                FontSize = 18,
                TextColor = (Color)Application.Current!.Resources["HdDim"],
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(12, 2),
            };
            chev.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => OpenAdsPanel())
            });
            row.Children.Add(chev);
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
            Text = "Ad blocking",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
        });
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
            Text = $"Blocked {Web.BlockedCount} on this page",
            FontSize = 11,
            TextColor = Color.FromRgba(242, 237, 232, 110),
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
        foreach (var s in _ads.GetListStatus())
            rows.Children.Add(AdsListRow(s, dim));
        // Locked list region: fixed height, own scroll. The list can grow
        // (custom lists later) without pushing Update/cadence/Done off-panel.
        // The outer panel scroll remains as the overflow safety net.
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
            Placeholder = "https://…/list.txt",
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
        var upd = new Button
        {
            Text = _updating ? "Updating…" : "Update now",
            BackgroundColor = gold,
            TextColor = bg,
            CornerRadius = 10,
            IsEnabled = !_updating,
        };
        upd.Clicked += async (_, _) => await UpdateListsAsync();
        AdsPanelBody.Children.Add(upd);
        AdsPanelBody.Children.Add(new Label
        {
            Text = "Some lists couldn't download — kept the current blocking.",
            FontSize = 12,
            TextColor = Color.FromArgb("#E14B32"),
            IsVisible = _lastUpdateFailed,
        });
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
