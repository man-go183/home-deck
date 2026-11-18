using System.Runtime.CompilerServices;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace HomeDeck.Controls;

/// <summary>
/// Mock-style checkbox: a custom-drawn 20px box (gold fill + dark check when
/// on). The native MAUI CheckBox enforces a ~120px minimum layout slot on
/// Windows no matter the WidthRequest, which pushed labels far right — this
/// box is exactly 20px. API mirrors CheckBox (IsChecked + CheckedChanged).
/// </summary>
public class HdCheckBox : ContentView
{
    public static readonly BindableProperty IsCheckedProperty =
        BindableProperty.Create(nameof(IsChecked), typeof(bool), typeof(HdCheckBox), false,
            propertyChanged: (b, _, _) => ((HdCheckBox)b).Sync());

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public event EventHandler<bool>? CheckedChanged;

    readonly Border _box;
    readonly Microsoft.Maui.Controls.Shapes.Path _tick;

    public HdCheckBox()
    {
        _tick = new Microsoft.Maui.Controls.Shapes.Path
        {
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M5 10l4 4 8-9"),
            Stroke = Color.FromArgb("#161311"),
            StrokeThickness = 2.5,
            Aspect = Stretch.Uniform,
            WidthRequest = 11,
            HeightRequest = 11,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            InputTransparent = true,
        };
        _box = new Border
        {
            WidthRequest = 20,
            HeightRequest = 20,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromRgba(0, 0, 0, 90),
            Stroke = Color.FromRgba(255, 255, 255, 90),
            StrokeThickness = 1.5,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = _tick,
        };
        Content = _box;
        VerticalOptions = LayoutOptions.Center;
        GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => IsChecked = !IsChecked),
        });
        Sync();
    }

    protected override void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(IsChecked))
        {
            Sync();
            CheckedChanged?.Invoke(this, IsChecked);
        }
    }

    void Sync()
    {
        if (_box == null) return;
        bool on = IsChecked;
        _tick.IsVisible = on;
        _box.BackgroundColor = on ? Color.FromArgb("#E8B34A") : Color.FromRgba(0, 0, 0, 90);
        _box.Stroke = on ? Color.FromArgb("#E8B34A") : Color.FromRgba(255, 255, 255, 90);
    }
}
