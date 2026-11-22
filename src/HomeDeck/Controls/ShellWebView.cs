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

    /// <summary>Requests blocked at the network layer on the current page load (snapshot for UI).</summary>
    public int BlockedCount => _blocked;

    public void CountBlocked() => Interlocked.Increment(ref _blocked);

    public void ResetBlockedCount() => Interlocked.Exchange(ref _blocked, 0);

    int _cosmeticHidden, _pickedHidden;

    /// <summary>Elements concealed on this page: cosmetic matches (one
    /// count pass per load) + picked elements. Snapshot for UI.</summary>
    public int HiddenCount => _cosmeticHidden + _pickedHidden;

    public void SetCosmeticHidden(int n) => Interlocked.Exchange(ref _cosmeticHidden, Math.Max(0, n));

    public void AddPickedHidden() => Interlocked.Increment(ref _pickedHidden);

    public void ResetHiddenCount()
    {
        Interlocked.Exchange(ref _cosmeticHidden, 0);
        Interlocked.Exchange(ref _pickedHidden, 0);
    }
}
