#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace HomeDeck.Views;

/// <summary>
/// Windows title-bar integration: the page's back button + profile pill move
/// INTO the OS caption as a real TitleBar, so the caption row genuinely is our
/// header (single row — no framework row above, no in-page row below). Drag
/// works everywhere on the bar except the back button, the pill, and the
/// caption-button reserve on the right.
/// If anything here throws, the page simply keeps its normal look.
/// </summary>
public partial class BrowserPage
{
    MauiWinUIWindow? _win;
    Microsoft.Maui.Controls.TitleBar? _winBar;

    partial void SetupCustomTitleBar()
    {
        try
        {
            if (Window?.Handler?.PlatformView is not MauiWinUIWindow win) return;
            _win = win;
            var bar = win.AppWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            bar.ButtonBackgroundColor = transparent;
            bar.ButtonInactiveBackgroundColor = transparent;
            bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 242, 237, 232);
            bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(120, 242, 237, 232);
            EnsureWindowTitleBar();
            UpdateDragRects();
            win.SizeChanged -= OnWinSizeChanged;
            win.SizeChanged += OnWinSizeChanged;
        }
        catch { /* keep the normal title bar */ }
    }

    /// <summary>Build the real caption content once (back + pill, moved out of
    /// the in-page grid) and install it as the window's title bar, replacing
    /// MAUI's default icon + title + framework back. Android is untouched: it
    /// keeps hiding the grid and moving the same views into its toolbar.</summary>
    void EnsureWindowTitleBar()
    {
        try
        {
            if (Window == null) return;
            if (_winBar == null)
            {
                TitleBar.Children.Remove(BackBtn);
                TitleBar.Children.Remove(Pill);
                TitleBar.IsVisible = false;
                // Collapse the now-empty in-page row so no dead band sits
                // under the caption (the real bar lives in the title zone).
                if (TitleBar.Parent is Grid outer && outer.RowDefinitions.Count > 0)
                    outer.RowDefinitions[0].Height = Microsoft.Maui.Controls.GridLength.Auto;
                _winBar = new Microsoft.Maui.Controls.TitleBar
                {
                    HeightRequest = 40,
                    BackgroundColor = Color.FromArgb("#241F1A"),
                    LeadingView = BackBtn,
                    Content = Pill,
                    TrailingView = new BoxView
                    {
                        WidthRequest = 140,
                        Color = Colors.Transparent,
                    },
                };
            }
            Window.TitleBar = _winBar;
        }
        catch { }
    }

    partial void ResetTitleBar()
    {
        try
        {
            if (_win != null)
            {
                _win.SizeChanged -= OnWinSizeChanged;
                _win = null;
            }
            // Stay extended and keep an empty caption: the launcher sets its
            // own empty title bar on appearing. Never restore the framework
            // default here — that is what painted the extra strip + white
            // caption when navigating back.
            if (Window != null)
                Window.TitleBar = new Microsoft.Maui.Controls.TitleBar();
        }
        catch { }
    }

    void OnWinSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
        => UpdateDragRects();

    void UpdateDragRects()
    {
        try
        {
            if (_win == null || _winBar == null) return;
            if (_winBar.Handler?.PlatformView is not FrameworkElement root) return;
            double scale = root.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var content = (FrameworkElement)_win.Content;
            var bar = ToPx(_winBar, content, scale);
            if (bar.Width <= 0 || bar.Height <= 0) return;
            var back = ToPx(BackBtn, content, scale);
            var pill = ToPx(Pill, content, scale);

            // Right-side reserve for the OS caption buttons (~3x46 logical px).
            double reserve = 140 * scale;
            double reserveX = bar.X + bar.Width - reserve;

            var rects = new System.Collections.Generic.List<RectInt32>();
            AddRect(rects, bar.X, bar.Y, back.X - bar.X, bar.Height);
            AddRect(rects, back.X + back.Width, bar.Y,
                pill.X - (back.X + back.Width), bar.Height);
            AddRect(rects, pill.X + pill.Width, bar.Y,
                reserveX - (pill.X + pill.Width), bar.Height);
            _win.AppWindow.TitleBar.SetDragRectangles(rects.ToArray());
        }
        catch { /* window stays usable, just not draggable by the bar */ }
    }

    static Windows.Foundation.Rect ToPx(
        Microsoft.Maui.Controls.View view, FrameworkElement relativeTo, double scale)
    {
        if (view.Handler?.PlatformView is not FrameworkElement fe || fe.ActualWidth <= 0)
            return new Windows.Foundation.Rect();
        var t = fe.TransformToVisual(relativeTo);
        var r = t.TransformBounds(new Windows.Foundation.Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
        return new Windows.Foundation.Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
    }

    static void AddRect(System.Collections.Generic.List<RectInt32> rects,
        double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        rects.Add(new RectInt32((int)x, (int)y, (int)w, (int)h));
    }
}
#endif
