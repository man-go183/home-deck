using HomeDeck.Models;
using HomeDeck.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;

namespace HomeDeck.Views;

public partial class BrowserPage : ContentPage
{
    readonly ProfileStore _store;
    readonly AdBlockService _ads;
    readonly IServiceProvider _services;
    Profile? _profile;

    public BrowserPage(ProfileStore store, AdBlockService ads, IServiceProvider services)
    {
        InitializeComponent();
        _store = store;
        _ads = ads;
        _services = services;

        Web.Navigating += (_, e) =>
        {
            try { Web.PageHost = new Uri(e.Url).Host; } catch { }
        };
        Web.Navigated += async (_, _) =>
        {
            try
            {
                await Splash.FadeTo(0, 200);
                Splash.IsVisible = false;
                Splash.Opacity = 1;
            }
            catch { Splash.IsVisible = false; }
        };
    }

    public void BindProfile(Profile p)
    {
        _profile = p;
        Drop.IsVisible = false;
        PillName.Text = p.Name;
        PillGlyph.Text = Glyph(p);
        SplashName.Text = p.Name;
        SplashGlyph.Text = Glyph(p);
        var color = Color.FromArgb(p.ColorHex);
        ((SolidColorBrush)PillAva.Fill!).Color = color;
        ((SolidColorBrush)SplashAva.Fill!).Color = color;
        PillShield.IsVisible = p.AdBlock;

        Web.AdBlockEnabled = p.AdBlock;
        try { Web.PageHost = new Uri(p.Url).Host; } catch { Web.PageHost = ""; }
        Web.Source = new UrlWebViewSource { Url = p.Url };
        Splash.IsVisible = true;

        _store.Prefs.LastProfileId = p.Id;
        _store.SavePrefs();
        _ = _ads.EnsureLoadedAsync();
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
                PillShield.IsVisible = e.Value;
                _store.SaveProfiles();
            };
            row.Children.Add(cb); row.Children.Add(lab);
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => cb.IsChecked = !cb.IsChecked)
            });
            DropList.Children.Add(row);
        }
    }

    async void OnMenu(object? sender, EventArgs e)
    {
        var choice = await DisplayActionSheet("Home Deck", "Cancel", null,
            "Switch profile", "Open in browser", "Manage profiles");
        if (choice == "Switch profile") { RebuildDrop(); Drop.IsVisible = true; }
        else if (choice == "Open in browser" && _profile != null)
        {
            try { await Launcher.OpenAsync(_profile.Url); } catch { }
        }
        else if (choice == "Manage profiles") await Navigation.PopAsync();
    }
}
