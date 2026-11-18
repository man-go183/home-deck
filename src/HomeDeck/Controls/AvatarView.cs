using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using HomeDeck.Models;

namespace HomeDeck.Controls;

/// <summary>
/// Profile avatar, shared by launcher cards, pill, splash and popups.
/// No letters, ever: default is the person glyph on the profile color,
/// presets are gradients, uploads are round-cropped photos.
/// </summary>
public static class AvatarView
{
    // (start, end[, mid]) hex stops, 135-degree linears. Entries 4-5 stand
    // in for the mockup's conic presets — MAUI has no conic brush, so they
    // use the same stops as linears (documented approximation).
    static readonly string[][] Presets =
    {
        new[] { "#f6a35c", "#e14b32" },
        new[] { "#3ec6e0", "#2f6da3" },
        new[] { "#9a6bff", "#5a2ee0" },
        new[] { "#7bc47f", "#2e7d4f" },
        new[] { "#f6a35c", "#e14b32", "#7c3fa3" },
        new[] { "#3ec6e0", "#7bc47f", "#f6a35c" },
    };

    public static int PresetCount => Presets.Length;

    public static LinearGradientBrush PresetBrush(int i)
    {
        var stops = Presets[(i % Presets.Length + Presets.Length) % Presets.Length];
        var c = new GradientStopCollection();
        for (int k = 0; k < stops.Length; k++)
            c.Add(new GradientStop(Color.FromArgb(stops[k]), (float)k / (stops.Length - 1)));
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = c
        };
    }

    static Geometry? s_person;
    public static Geometry PersonGeometry => s_person ??= new GeometryGroup
    {
        Children =
        {
            new EllipseGeometry { Center = new Point(12, 8), RadiusX = 4.2, RadiusY = 4.2 },
            (Geometry)new PathGeometryConverter().ConvertFromInvariantString(
                "M3.5 21c.8-4.4 4.3-7 8.5-7s7.7 2.6 8.5 7z")
        }
    };

    /// <summary>Shield badge path (mockup asset, 24x24 box).</summary>
    public const string ShieldData =
        "M12 3l7 2.8v5.1c0 4.6-3 7.9-7 9.6-4-1.7-7-5-7-9.6V5.8z";

    /// <summary>Slash shown across the shield when blocking is off.</summary>
    public const string SlashData = "M5 5l14 14";

    public static Color GlyphColor { get; } = Color.FromArgb("#F2EDE8");

    /// <summary>Build a size×size avatar view for a profile. Never throws.</summary>
    public static View Build(Profile p, double size)
    {
        try
        {
            if (p.AvatarKind == "file" && !string.IsNullOrEmpty(p.AvatarValue))
            {
                var path = Path.Combine(FileSystem.AppDataDirectory, "avatars", p.AvatarValue);
                if (File.Exists(path))
                {
                    var img = new Image
                    {
                        Source = ImageSource.FromFile(path),
                        Aspect = Aspect.AspectFill,
                        WidthRequest = size, HeightRequest = size
                    };
                    img.Clip = new EllipseGeometry
                    {
                        Center = new Point(size / 2, size / 2),
                        RadiusX = size / 2, RadiusY = size / 2
                    };
                    return img;
                }
            }
            var g = new Grid { WidthRequest = size, HeightRequest = size };
            var bg = new Ellipse { WidthRequest = size, HeightRequest = size };
            if (p.AvatarKind == "gradient" && int.TryParse(p.AvatarValue, out int gi))
                bg.Fill = PresetBrush(gi);
            else
                bg.Fill = new SolidColorBrush(SafeColor(p.ColorHex));
            g.Children.Add(bg);
            g.Children.Add(new Path
            {
                Data = PersonGeometry,
                Fill = new SolidColorBrush(GlyphColor),
                Aspect = Stretch.Uniform,
                WidthRequest = size * 0.62, HeightRequest = size * 0.62,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            });
            return g;
        }
        catch { return new Ellipse { WidthRequest = size, HeightRequest = size }; }
    }

    public static Color SafeColor(string hex)
    {
        try { return Color.FromArgb(hex); }
        catch { return Color.FromArgb("#B3402E"); }
    }

    public static bool IsPerchanceUrl(string url)
    {
        try
        {
            var h = new Uri(url).Host.ToLowerInvariant();
            return h == "perchance.org" || h.EndsWith(".perchance.org");
        }
        catch { return false; }
    }
}
