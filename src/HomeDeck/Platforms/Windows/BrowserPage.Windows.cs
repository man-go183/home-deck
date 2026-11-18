#if WINDOWS
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace HomeDeck.Views;

/// <summary>
/// Windows title-bar integration: the in-page bar row (TitleBar grid) becomes
/// the OS title-bar content. Drag works everywhere on the bar except the back
/// button, the pill, and the caption-button reserve on the right.
/// If anything here throws, the page simply keeps its normal look.
/// </summary>
public partial class BrowserPage
{
    MauiWinUIWindow? _win;

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
            UpdateDragRects();
            win.SizeChanged -= OnWinSizeChanged;
            win.SizeChanged += OnWinSizeChanged;
            ReplaceDefaultCaptionContent();
        }
        catch { /* keep the normal title bar */ }
    }

    /// <summary>Swap MAUI's default caption content (icon + window title +
    /// framework back button) for an empty bar. The in-page TitleBar grid is
    /// the real header and already sits in the caption area; caption buttons
    /// stay system-drawn and dragging keeps working via our drag rects.
    /// NOTE: the bar must stay VISIBLE-but-empty — an invisible title bar
    /// gets no native view and MAUI silently keeps showing its default.</summary>
    void ReplaceDefaultCaptionContent()
    {
        try
        {
            if (Window != null)
                Window.TitleBar = new Microsoft.Maui.Controls.TitleBar();
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
            if (_win == null) return;
            if (TitleBar.Handler?.PlatformView is not FrameworkElement root) return;
            double scale = root.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var content = (FrameworkElement)_win.Content;
            var bar = ToPx(TitleBar, content, scale);
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
