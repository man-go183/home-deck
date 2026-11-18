using Microsoft.Maui;
using Microsoft.Web.WebView2.Core;

namespace HomeDeck.WinUI;

public partial class App : MauiWinUIApplication
{
    /// <summary>Shared browser process, warmed once at launch (no visible page
    /// needed) and held for the app's lifetime. Each profile's engine init
    /// then reuses it instead of cold-starting. Fail-safe.</summary>
    static CoreWebView2Environment? s_browserEnv;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);
        _ = WarmBrowserAsync();
    }

    static async Task WarmBrowserAsync()
    {
        try { s_browserEnv = await CoreWebView2Environment.CreateAsync(); }
        catch { s_browserEnv = null; }
    }

    protected override MauiApp CreateMauiApp() => HomeDeck.MauiProgram.CreateMauiApp();
}
