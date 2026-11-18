using HomeDeck.Models;
using HomeDeck.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace HomeDeck.Views;

public partial class LauncherPage : ContentPage
{
    readonly ProfileStore _store;
    readonly IServiceProvider _services;

    public LauncherPage(ProfileStore store, IServiceProvider services)
    {
        InitializeComponent();
        _store = store;
        _services = services;
        AskChk.CheckedChanged += (_, e) =>
        {
            _store.Prefs.AskEveryTime = e.Value;
            _store.SavePrefs();
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { Window.Title = "Home Deck"; } catch { }
        await _store.LoadAsync();
        AskChk.IsChecked = _store.Prefs.AskEveryTime;
        BuildCards();

        // Reopen last profile when "ask" is off.
        if (!_store.Prefs.AskEveryTime && !string.IsNullOrEmpty(_store.Prefs.LastProfileId))
        {
            var last = _store.Profiles.FirstOrDefault(p => p.Id == _store.Prefs.LastProfileId);
            if (last != null) await OpenProfile(last);
        }
    }

    void BuildCards()
    {
        CardRow.Children.Clear();
        foreach (var p in _store.Profiles)
            CardRow.Children.Add(MakeCard(p));
        CardRow.Children.Add(MakeAddCard());
    }

    View MakeCard(Profile p)
    {
        var bg = (Color)Application.Current!.Resources["HdCard"];
        var avatar = new Ellipse
        {
            WidthRequest = 64, HeightRequest = 64,
            Fill = new SolidColorBrush(Color.FromArgb(p.ColorHex)),
            HorizontalOptions = LayoutOptions.Center
        };
        var glyph = new Label
        {
            Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name[..1].ToUpper(),
            FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center
        };
        var shield = new Path
        {
            WidthRequest = 15, HeightRequest = 17, Aspect = Stretch.None,
            TranslationX = 1, TranslationY = 1,
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(
                "M6 0 L11 2.5 V7 C11 10.5 8.7 12.3 6 13.5 C3.3 12.3 1 10.5 1 7 V2.5 Z"),
            Fill = (Color)Application.Current.Resources["HdGold"],
            Stroke = (Color)Application.Current.Resources["HdGoldDeep"],
            StrokeThickness = 1,
            IsVisible = p.AdBlock,
            HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.End
        };
        // Face grid is exactly avatar-sized and centered, so End/End anchors
        // the shield to the avatar's corner (not the card's).
        var face = new Grid
        {
            WidthRequest = 64, HeightRequest = 64,
            HorizontalOptions = LayoutOptions.Center,
            Children = { avatar, glyph, shield }
        };

        var box = new VerticalStackLayout
        {
            Spacing = 6, Padding = new Thickness(12, 20, 12, 14),
            WidthRequest = 150, HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                face,
                new Label { Text = p.Name, FontSize = 14, FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation },
                new Label { Text = p.PageId, FontSize = 10,
                    TextColor = (Color)Application.Current.Resources["HdDim"],
                    HorizontalOptions = LayoutOptions.Center,
                    LineBreakMode = LineBreakMode.TailTruncation }
            }
        };
        var card = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = bg, Stroke = Color.FromRgba(255, 255, 255, 26),
            StrokeThickness = 1, Margin = new Thickness(6), Content = box
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await OpenProfile(p))
        });

        // Small edit affordance: long-press (mobile) / right-click-ish menu.
        var menu = new TapGestureRecognizer { Buttons = ButtonsMask.Secondary };
        menu.Tapped += async (_, _) => await EditProfile(p, card, shield);
        card.GestureRecognizers.Add(menu);
        var editBtn = new Button
        {
            Text = "Edit", FontSize = 11, Padding = new Thickness(10, 2, 10, 2),
            TextColor = (Color)Application.Current.Resources["HdDim"],
            BackgroundColor = Colors.Transparent, HorizontalOptions = LayoutOptions.Center
        };
        editBtn.Clicked += async (_, _) => await EditProfile(p, card, shield);
        box.Children.Add(editBtn);
        return card;
    }

    View MakeAddCard()
    {
        var inner = new VerticalStackLayout
        {
            Spacing = 8, Padding = new Thickness(12, 20, 12, 14),
            WidthRequest = 150, HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "+", FontSize = 34, TextColor = Colors.White,
                    HorizontalOptions = LayoutOptions.Center },
                new Label { Text = "New profile", FontSize = 13,
                    TextColor = (Color)Application.Current!.Resources["HdDim"],
                    HorizontalOptions = LayoutOptions.Center }
            }
        };
        var card = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Colors.Transparent,
            Stroke = Color.FromRgba(255, 255, 255, 60),
            StrokeThickness = 1,
            Margin = new Thickness(6), Content = inner, MinimumHeightRequest = 190
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await AddProfile())
        });
        return card;
    }

    async Task OpenProfile(Profile p)
    {
        _store.Prefs.LastProfileId = p.Id;
        _store.SavePrefs();
        var page = _services.GetRequiredService<BrowserPage>();
        page.BindProfile(p);
        await Navigation.PushAsync(page);
    }

    async Task AddProfile()
    {
        var name = await DisplayPromptAsync("New profile", "Name:", initialValue: "Untitled");
        if (name == null) return;
        var url = await DisplayPromptAsync("New profile", "Perchance page link:",
            initialValue: "https://perchance.org/");
        if (url == null) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        _store.Add(name.Trim().Length > 0 ? name.Trim() : "Untitled", url.Trim(), adBlock: true);
        BuildCards();
    }

    async Task EditProfile(Profile p, View card, View shieldDot)
    {
        var choice = await DisplayActionSheetAsync(p.Name, "Cancel", "Delete",
            "Rename", "Edit link", p.AdBlock ? "Turn ad-blocking off" : "Turn ad-blocking on");
        if (choice == "Rename")
        {
            var name = await DisplayPromptAsync("Rename", "Name:", initialValue: p.Name);
            if (name != null && name.Trim().Length > 0) { p.Name = name.Trim(); _store.SaveProfiles(); BuildCards(); }
        }
        else if (choice == "Edit link")
        {
            var url = await DisplayPromptAsync("Page link", "Perchance page link:", initialValue: p.Url);
            if (url != null && url.Trim().Length > 0)
            {
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                p.Url = url.Trim(); _store.SaveProfiles(); BuildCards();
            }
        }
        else if (choice == "Turn ad-blocking off" || choice == "Turn ad-blocking on")
        {
            p.AdBlock = !p.AdBlock;
            _store.SaveProfiles();
            shieldDot.IsVisible = p.AdBlock;
        }
        else if (choice == "Delete")
        {
            if (await DisplayAlertAsync("Delete?", $"Remove profile \"{p.Name}\"?", "Delete", "Cancel"))
            {
                _store.Remove(p);
                BuildCards();
            }
        }
    }

    void OnAskTap(object? sender, TappedEventArgs e) => AskChk.IsChecked = !AskChk.IsChecked;

    async void OnHelp(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("Using Home Deck",
            "1. Choose a profile to open its page.\n" +
            "2. Tap Edit on a card to rename / edit / delete it.\n" +
            "3. + adds a profile (ad-blocking on by default).\n" +
            "4. In the app, tap your profile in the header to switch.\n" +
            "5. Uncheck Ask every time to reopen your last profile.\n" +
            "6. The gold shield = ad & tracker blocking is on for that profile.",
            "Got it");
    }
}
