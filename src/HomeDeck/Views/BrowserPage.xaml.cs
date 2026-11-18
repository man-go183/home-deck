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
#if ANDROID
    bool _titleViewSet;
#endif

    public BrowserPage(ProfileStore store, AdBlockService ads)
    {
        InitializeComponent();
        _store = store;
        _ads = ads;

        Web.Navigating += (_, e) =>
        {
            try { Web.PageHost = new Uri(e.Url).Host; } catch { }
        };
        Web.Navigated += async (_, _) =>
        {
            try
            {
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
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
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
        PillName.Text = p.Name;
        PillAvaBox.Children.Clear();
        PillAvaBox.Children.Add(AvatarView.Build(p, 26));
        SplashAvaBox.Children.Clear();
        SplashAvaBox.Children.Add(AvatarView.Build(p, 76));
        SplashName.Text = p.Name;
        SyncShield(p);

        Web.AdBlockEnabled = p.AdBlock;
        try { Web.PageHost = new Uri(p.Url).Host; } catch { Web.PageHost = ""; }
        Web.Source = new UrlWebViewSource { Url = p.Url };
        Splash.IsVisible = true;

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
        foreach (var p in _store.Profiles.Where(p => p != _profile))
        {
            var b = new Button
            {
                Text = (p.AdBlock ? "◈ " : "") + p.Name,
                BackgroundColor = Colors.Transparent,
                TextColor = Colors.White, HorizontalOptions = LayoutOptions.Fill
            };
            var target = p;
            b.Clicked += (_, _) => { Drop.IsVisible = false; BindProfile(target); };
            DropList.Children.Add(b);
        }
        var manage = new Button
        {
            Text = "Manage profiles…", BackgroundColor = Colors.Transparent,
            TextColor = Colors.White, HorizontalOptions = LayoutOptions.Fill
        };
        manage.Clicked += async (_, _) => await Navigation.PopAsync();
        DropList.Children.Add(manage);

        if (_profile != null)
        {
            var row = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(10, 6) };
            var cb = new CheckBox { IsChecked = _profile.AdBlock, Color = (Color)Application.Current!.Resources["HdGold"] };
            var lab = new Label { Text = "Block ads & trackers", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center };
            cb.CheckedChanged += (_, e) =>
            {
                if (_profile == null) return;
                _profile.AdBlock = e.Value;
                Web.AdBlockEnabled = e.Value;
                SyncShield(_profile);
                _store.SaveProfiles();
            };
            row.Children.Add(cb); row.Children.Add(lab);
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => cb.IsChecked = !cb.IsChecked)
            });
            DropList.Children.Add(row);

            var open = new Button
            {
                Text = "Open in browser", BackgroundColor = Colors.Transparent,
                TextColor = Colors.White, HorizontalOptions = LayoutOptions.Fill
            };
            open.Clicked += async (_, _) =>
            {
                Drop.IsVisible = false;
                if (_profile != null)
                {
                    try { await Launcher.OpenAsync(_profile.Url); } catch { }
                }
            };
            DropList.Children.Add(open);
        }
    }
}
