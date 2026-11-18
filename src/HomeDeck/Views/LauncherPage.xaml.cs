using HomeDeck.Controls;
using HomeDeck.Models;
using HomeDeck.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace HomeDeck.Views;

public partial class LauncherPage : ContentPage
{
    readonly ProfileStore _store;
    readonly IServiceProvider _services;
    readonly AdBlockService _ads;
    bool _adding;
    Profile? _avaEditing;

    public LauncherPage(ProfileStore store, IServiceProvider services, AdBlockService ads)
    {
        InitializeComponent();
#if WINDOWS
        // The OS draws the caption itself; the NavigationPage toolbar (plus
        // its large page-title header) would stack a 2nd + 3rd "Home Deck".
        NavigationPage.SetHasNavigationBar(this, false);
#endif
        _store = store;
        _services = services;
        _ads = ads;
        AskChk.CheckedChanged += (_, _) =>
        {
            _store.Prefs.AskEveryTime = AskChk.IsChecked;
            _store.SavePrefs();
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { Window.Title = "Home Deck"; } catch { }
#if WINDOWS
        // Launcher caption header (mock .wapp): gradient dot + "Home Deck",
        // left-aligned. No back, no pill — exactly the mock launcher state.
        // (Must stay a visible-but-real bar — an empty/invisible one gets no
        // native view and MAUI falls back to its default icon + title row.)
        try
        {
            Window.TitleBar = new Microsoft.Maui.Controls.TitleBar
            {
                BackgroundColor = Colors.Transparent,
                Content = new HorizontalStackLayout
                {
                    Spacing = 8,
                    Padding = new Thickness(8, 0, 0, 0),
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new BoxView
                        {
                            WidthRequest = 10,
                            HeightRequest = 10,
                            CornerRadius = 5,
                            VerticalOptions = LayoutOptions.Center,
                            Background = new LinearGradientBrush(
                                new GradientStopCollection
                                {
                                    new GradientStop(Color.FromArgb("#e8b34a"), 0f),
                                    new GradientStop(Color.FromArgb("#b3402e"), 1f),
                                },
                                new Point(0, 0), new Point(1, 1)),
                        },
                        new Label
                        {
                            Text = "Home Deck",
                            FontSize = 12,
                            CharacterSpacing = 0.5,
                            TextColor = Color.FromRgba(242, 237, 232, 128),
                            VerticalOptions = LayoutOptions.Center,
                        },
                    },
                },
            };
        }
        catch { }
#endif
        // Warm the ad-block lists at launch (not on first profile open), so
        // they're ready before any tap. Fail-safe inside; never blocks UI.
        _ = _ads.EnsureLoadedAsync();
        await _store.LoadAsync();
        AskChk.IsChecked = _store.Prefs.AskEveryTime;
        BuildCards();

        if (!_store.Prefs.AskEveryTime && !string.IsNullOrEmpty(_store.Prefs.LastProfileId))
        {
            var last = _store.Profiles.FirstOrDefault(p => p.Id == _store.Prefs.LastProfileId);
            if (last != null) await OpenProfile(last);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (HelpCard.IsVisible) { HideHelp(); return true; }
        if (AvaPop.IsVisible) { HideAvaPop(); return true; }
        return base.OnBackButtonPressed();
    }

    void BuildCards()
    {
        CardRow.Children.Clear();
        foreach (var p in _store.Profiles)
            CardRow.Children.Add(MakeCard(p));
        CardRow.Children.Add(_adding ? MakeFormCard() : MakeAddCard());
    }

    // ---------- cards ----------

    View MakeCard(Profile p)
    {
        var box = new VerticalStackLayout
        {
            Spacing = 6, Padding = new Thickness(12, 20, 12, 14),
            WidthRequest = 208, HorizontalOptions = LayoutOptions.Center
        };
        var face = new Grid { HeightRequest = 274 };
        box.Children.Add(face);

        // front face
        var front = new VerticalStackLayout
        {
            Spacing = 8, VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        var avaRow = new Grid
        {
            WidthRequest = 64, HeightRequest = 64,
            HorizontalOptions = LayoutOptions.Center
        };
        avaRow.Children.Add(AvatarView.Build(p, 64));
        avaRow.Children.Add(MakePenButton(() => Flip(face, true, p)));
        front.Children.Add(avaRow);
        front.Children.Add(new Label
        {
            Text = p.Name, FontSize = 14, FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        });
        front.Children.Add(new Label
        {
            Text = p.PageId, FontSize = 10,
            TextColor = (Color)Application.Current!.Resources["HdDim"],
            HorizontalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        });
        face.Children.Add(front);

        // edit face
        var edit = new VerticalStackLayout { Spacing = 8, IsVisible = false };
        var editAva = new Grid
        {
            WidthRequest = 64, HeightRequest = 64,
            HorizontalOptions = LayoutOptions.Center
        };
        RefreshEditAvatar(editAva, p);
        editAva.Children.Add(MakePenButton(() => OpenAvaPop(p)));
        edit.Children.Add(editAva);
        var nameEntry = new Entry
        {
            Text = p.Name, TextColor = Colors.White,
            Placeholder = "Name",
            PlaceholderColor = (Color)Application.Current.Resources["HdDim"],
            HorizontalOptions = LayoutOptions.Fill
        };
        var linkEntry = new Entry
        {
            Text = p.Url, TextColor = Colors.White, FontSize = 12,
            Placeholder = "Perchance page link",
            PlaceholderColor = (Color)Application.Current.Resources["HdDim"],
            HorizontalOptions = LayoutOptions.Fill
        };
        var warn = new Label
        {
            Text = "Not a Perchance page — it will still open, but may not behave like one.",
            TextColor = (Color)Application.Current.Resources["HdGold"],
            FontSize = 11, IsVisible = !AvatarView.IsPerchanceUrl(p.Url)
        };
        linkEntry.TextChanged += (_, _) =>
        {
            try { warn.IsVisible = !AvatarView.IsPerchanceUrl(linkEntry.Text ?? ""); }
            catch { }
        };
        var save = new Button
        {
            Text = "Save", BackgroundColor = (Color)Application.Current.Resources["HdGold"],
            TextColor = (Color)Application.Current.Resources["HdBg"], CornerRadius = 10
        };
        save.Clicked += (_, _) =>
        {
            var name = (nameEntry.Text ?? "").Trim();
            p.Name = name.Length > 0 ? name : "Untitled";
            var url = (linkEntry.Text ?? "").Trim();
            if (url.Length > 0 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (url.Length > 0) p.Url = url;
            _store.SaveProfiles();
            BuildCards();
        };
        var del = new Button
        {
            Text = "Delete", BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#E14B32")
        };
        del.Clicked += async (_, _) =>
        {
            if (await DisplayAlertAsync("Delete?", $"Remove profile \"{p.Name}\"?", "Delete", "Cancel"))
            {
                _store.Remove(p);
                BuildCards();
            }
        };
        edit.Children.Add(nameEntry);
        edit.Children.Add(linkEntry);
        edit.Children.Add(warn);
        edit.Children.Add(save);
        edit.Children.Add(del);
        face.Children.Add(edit);

        var card = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = (Color)Application.Current.Resources["HdCard"],
            Stroke = Color.FromRgba(255, 255, 255, 45),
            StrokeThickness = 1, Margin = new Thickness(6), Content = box
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await OpenProfile(p))
        });

        // shield toggle on the card's bottom-right corner (toggles, never navigates)
        var shieldBtn = MakeShieldButton(p, null);
        var outer = new Grid { card, shieldBtn };
        return outer;
    }

    static async void Flip(Grid face, bool toEdit, Profile _)
    {
        try
        {
            await face.ScaleXTo(0.02, 110, Easing.CubicIn);
            ((View)face.Children[0]).IsVisible = !toEdit;
            ((View)face.Children[1]).IsVisible = toEdit;
            await face.ScaleXTo(1, 110, Easing.CubicOut);
        }
        catch
        {
            ((View)face.Children[0]).IsVisible = !toEdit;
            ((View)face.Children[1]).IsVisible = toEdit;
            face.ScaleX = 1;
        }
    }

    static View MakePenButton(Action onTap)
    {
        var path = new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(
                "M4 20l1.2-4.2L16.7 4.3a2.1 2.1 0 0 1 3 3L8.2 18.8z M14.7 6.3l3 3"),
            Fill = new SolidColorBrush(Colors.White),
            Aspect = Stretch.Uniform, WidthRequest = 11, HeightRequest = 11,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };
        var circ = new Border
        {
            WidthRequest = 22, HeightRequest = 22,
            StrokeShape = new RoundRectangle { CornerRadius = 11 },
            BackgroundColor = Color.FromRgba(0, 0, 0, 150),
            Stroke = (Color)Application.Current!.Resources["HdDim"],
            StrokeThickness = 1,
            HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.End,
            TranslationX = 2, TranslationY = 2, Content = path
        };
        circ.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => onTap())
        });
        return circ;
    }

    View MakeShieldButton(Profile p, Action? refreshed)
    {
        var gold = (Color)Application.Current!.Resources["HdGold"];
        var shield = new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(AvatarView.ShieldData),
            Aspect = Stretch.Uniform, WidthRequest = 17, HeightRequest = 17,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };
        var slash = new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(AvatarView.SlashData),
            Aspect = Stretch.Uniform, WidthRequest = 17, HeightRequest = 17,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            Stroke = (Color)Application.Current.Resources["HdDim"], StrokeThickness = 2,
            InputTransparent = true
        };
        void Sync()
        {
            shield.Fill = new SolidColorBrush(p.AdBlock ? gold : Color.FromArgb("#6E675F"));
            shield.Stroke = new SolidColorBrush(p.AdBlock
                ? (Color)Application.Current.Resources["HdGoldDeep"]
                : Color.FromArgb("#4A453F"));
            shield.StrokeThickness = 1;
            slash.IsVisible = !p.AdBlock;
        }
        Sync();
        var btn = new Grid
        {
            WidthRequest = 26, HeightRequest = 26,
            HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 10, 10),
            Children = { shield, slash }
        };
        btn.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                p.AdBlock = !p.AdBlock;
                _store.SaveProfiles();
                Sync();
                refreshed?.Invoke();
            })
        });
        return btn;
    }

    static void RefreshEditAvatar(Grid host, Profile p)
    {
        host.Children.Clear();
        // pen button is re-added by the caller after this call
        host.Children.Add(AvatarView.Build(p, 64));
    }

    View MakeAddCard()
    {
        var inner = new VerticalStackLayout
        {
            Spacing = 8, Padding = new Thickness(12, 20, 12, 14),
            WidthRequest = 208, HorizontalOptions = LayoutOptions.Center,
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
            StrokeThickness = 1, MinimumHeightRequest = 274,
            Margin = new Thickness(6), Content = inner
        };
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => { _adding = true; BuildCards(); })
        });
        return card;
    }

    View MakeFormCard()
    {
        var dim = (Color)Application.Current!.Resources["HdDim"];
        var nameEntry = new Entry
        {
            Placeholder = "Name", TextColor = Colors.White, PlaceholderColor = dim
        };
        var linkEntry = new Entry
        {
            Placeholder = "Perchance page link", TextColor = Colors.White,
            PlaceholderColor = dim, FontSize = 12
        };
        var hint = new Label
        {
            Text = "Your Perchance page link — e.g. perchance.org/home-deck",
            TextColor = dim, FontSize = 11
        };
        var warn = new Label
        {
            Text = "Not a Perchance page — it will still open, but may not behave like one.",
            TextColor = (Color)Application.Current.Resources["HdGold"],
            FontSize = 11, IsVisible = false
        };
        linkEntry.TextChanged += (_, _) =>
        {
            try
            {
                var t = linkEntry.Text ?? "";
                warn.IsVisible = t.Length > 0 && !AvatarView.IsPerchanceUrl(t);
            }
            catch { }
        };
        var blockRow = new HorizontalStackLayout { Spacing = 8 };
        var blockChk = new CheckBox
        {
            IsChecked = true, Color = (Color)Application.Current.Resources["HdGold"],
            VerticalOptions = LayoutOptions.Center
        };
        var blockLab = new Label
        {
            Text = "Block ads & trackers", TextColor = Colors.White,
            FontSize = 12, VerticalOptions = LayoutOptions.Center
        };
        blockLab.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => blockChk.IsChecked = !blockChk.IsChecked)
        });
        blockRow.Children.Add(blockChk);
        blockRow.Children.Add(blockLab);
        var add = new Button
        {
            Text = "Add", BackgroundColor = (Color)Application.Current.Resources["HdGold"],
            TextColor = (Color)Application.Current.Resources["HdBg"], CornerRadius = 10
        };
        add.Clicked += (_, _) =>
        {
            var name = (nameEntry.Text ?? "").Trim();
            var url = (linkEntry.Text ?? "").Trim();
            if (url.Length > 0 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (url.Length == 0) url = "https://perchance.org/";
            _store.Add(name.Length > 0 ? name : "Untitled", url, blockChk.IsChecked);
            _adding = false;
            BuildCards();
        };
        var cancel = new Button
        {
            Text = "Cancel", BackgroundColor = Colors.Transparent,
            TextColor = (Color)Application.Current.Resources["HdDim"]
        };
        cancel.Clicked += (_, _) => { _adding = false; BuildCards(); };
        var box = new VerticalStackLayout
        {
            Spacing = 8, Padding = new Thickness(12, 20, 12, 14),
            WidthRequest = 208, HorizontalOptions = LayoutOptions.Center,
            Children = { nameEntry, linkEntry, hint, warn, blockRow, add, cancel }
        };
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = (Color)Application.Current.Resources["HdCard"],
            Stroke = Color.FromRgba(255, 255, 255, 45),
            StrokeThickness = 1, Margin = new Thickness(6), Content = box
        };
    }

    async Task OpenProfile(Profile p)
    {
        _store.Prefs.LastProfileId = p.Id;
        _store.SavePrefs();
        var page = _services.GetRequiredService<BrowserPage>();
        page.BindProfile(p);
        await Navigation.PushAsync(page);
    }

    void OnAskTap(object? sender, TappedEventArgs e) => AskChk.IsChecked = !AskChk.IsChecked;

    // ---------- avatar popup ----------

    void OpenAvaPop(Profile p)
    {
        _avaEditing = p;
        RebuildAvaPop();
        AvaDim.IsVisible = true;
        AvaPop.IsVisible = true;
    }

    void HideAvaPop()
    {
        AvaPop.IsVisible = false;
        AvaDim.IsVisible = false;
        _avaEditing = null;
    }

    void RebuildAvaPop()
    {
        var p = _avaEditing;
        if (p == null) return;
        AvaPreviewBox.Children.Clear();
        AvaPreviewBox.Children.Add(AvatarView.Build(p, 64));
        AvaRemove.IsVisible = p.AvatarKind == "file";
        AvaDots.Children.Clear();
        // default dot (profile color)
        AvaDots.Children.Add(MakePresetDot(
            new SolidColorBrush(AvatarView.SafeColor(p.ColorHex)),
            selected: p.AvatarKind == "default",
            onTap: () =>
            {
                p.AvatarKind = "default"; p.AvatarValue = "";
                _store.SaveProfiles(); RebuildAvaPop(); BuildCards();
            }));
        for (int i = 0; i < AvatarView.PresetCount; i++)
        {
            int idx = i;
            AvaDots.Children.Add(MakePresetDot(
                AvatarView.PresetBrush(idx),
                selected: p.AvatarKind == "gradient" && p.AvatarValue == idx.ToString(),
                onTap: () =>
                {
                    p.AvatarKind = "gradient"; p.AvatarValue = idx.ToString();
                    _store.SaveProfiles(); RebuildAvaPop(); BuildCards();
                }));
        }
    }

    static View MakePresetDot(Brush fill, bool selected, Action onTap)
    {
        var dot = new Ellipse { WidthRequest = 30, HeightRequest = 30, Fill = fill };
        var ring = new Border
        {
            WidthRequest = 38, HeightRequest = 38, Padding = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 19 },
            BackgroundColor = Colors.Transparent,
            Stroke = selected ? (Color)Application.Current!.Resources["HdGold"] : Colors.Transparent,
            StrokeThickness = 2, Content = dot, Margin = new Thickness(3)
        };
        ring.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => onTap())
        });
        return ring;
    }

    async void OnAvaUpload(object? sender, EventArgs e)
    {
        var p = _avaEditing;
        if (p == null) return;
        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync();
            if (photo == null) return;
            var dir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "avatars");
            Directory.CreateDirectory(dir);
            var dest = System.IO.Path.Combine(dir, p.Id + ".jpg");
            using var src = await photo.OpenReadAsync();
            using var dst = File.Create(dest);
            await src.CopyToAsync(dst);
            p.AvatarKind = "file";
            p.AvatarValue = p.Id + ".jpg";
            _store.SaveProfiles();
            RebuildAvaPop();
            BuildCards();
        }
        catch { /* picker cancelled or unavailable — stay as-is */ }
    }

    void OnAvaRemove(object? sender, EventArgs e)
    {
        var p = _avaEditing;
        if (p == null) return;
        try
        {
            var path = System.IO.Path.Combine(FileSystem.AppDataDirectory, "avatars", p.AvatarValue);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        p.AvatarKind = "default";
        p.AvatarValue = "";
        _store.SaveProfiles();
        RebuildAvaPop();
        BuildCards();
    }

    void OnAvaDoneTap(object? sender, TappedEventArgs e) => HideAvaPop();

    void OnAvaDoneBtn(object? sender, EventArgs e) => HideAvaPop();

    // ---------- help ----------

    void OnHelp(object? sender, EventArgs e)
    {
        HelpDim.IsVisible = true;
        HelpCard.IsVisible = true;
    }

    void HideHelp()
    {
        HelpCard.IsVisible = false;
        HelpDim.IsVisible = false;
    }

    void OnHelpClose(object? sender, EventArgs e) => HideHelp();

    void OnAdvTap(object? sender, TappedEventArgs e)
    {
        AdvList.IsVisible = !AdvList.IsVisible;
        AdvHead.Text = AdvList.IsVisible
            ? "Advanced: device features a page can request –"
            : "Advanced: device features a page can request ›";
    }
}
