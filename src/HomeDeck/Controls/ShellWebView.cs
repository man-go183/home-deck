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

    int _cosmeticHidden, _pickedHidden, _pageGen;

    /// <summary>Bumped on every new page (see ResetHiddenCount, called only
    /// from StartNavigation): lets late async count passes ignore pages
    /// that have since been superseded instead of writing stale numbers.</summary>
    public int PageGen => _pageGen;

    /// <summary>Elements concealed on this page: cosmetic matches (one
    /// count pass per load) + picked elements. Snapshot for UI.</summary>
    public int HiddenCount => _cosmeticHidden + _pickedHidden;

    public void SetCosmeticHidden(int n) => Interlocked.Exchange(ref _cosmeticHidden, Math.Max(0, n));

    public void AddPickedHidden() => Interlocked.Increment(ref _pickedHidden);

    /// <summary>Undo one AddPickedHidden (session pick removed). Floors at 0.</summary>
    public void RemovePickedHidden()
    {
        int cur, next;
        do
        {
            cur = _pickedHidden;
            if (cur <= 0) return;
            next = cur - 1;
        } while (Interlocked.CompareExchange(ref _pickedHidden, next, cur) != cur);
    }

    public void ResetHiddenCount()
    {
        Interlocked.Exchange(ref _cosmeticHidden, 0);
        Interlocked.Exchange(ref _pickedHidden, 0);
        Interlocked.Increment(ref _pageGen);
    }
}
