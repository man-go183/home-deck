namespace HomeDeck.Controls;

/// <summary>
/// WebView bound to one profile. The platform handlers attach the JS bridge
/// (window.HomeDeck) and enforce that profile's ad-block setting.
/// </summary>
public class ShellWebView : WebView
{
    public static readonly BindableProperty AdBlockEnabledProperty =
        BindableProperty.Create(nameof(AdBlockEnabled), typeof(bool), typeof(ShellWebView), true);

    public bool AdBlockEnabled
    {
        get => (bool)GetValue(AdBlockEnabledProperty);
        set => SetValue(AdBlockEnabledProperty, value);
    }

    /// <summary>Host of the top-level page (for third-party checks).</summary>
    public string PageHost { get; set; } = "";
}
