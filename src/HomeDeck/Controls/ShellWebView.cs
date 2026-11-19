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

    int _blocked;

    /// <summary>Requests blocked on the current page load (snapshot for UI).</summary>
    public int BlockedCount => _blocked;

    public void CountBlocked() => Interlocked.Increment(ref _blocked);

    public void ResetBlockedCount() => Interlocked.Exchange(ref _blocked, 0);
}
