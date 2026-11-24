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
        BootLog("ctor: managed entry reached");
        try { BootLog("package: " + Windows.ApplicationModel.Package.Current.Id.FullName); }
        catch (Exception e) { BootLog("package?: " + e.GetType().Name + " " + e.Message); }
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, a) =>
                BootLog("FATAL unhandled: " + a.ExceptionObject);
        }
        catch (Exception e) { BootLog("hook?: " + e.Message); }
        this.InitializeComponent();
        BootLog("ctor: InitializeComponent ok");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        BootLog("OnLaunched: start");
        try
        {
            base.OnLaunched(args);
            BootLog("OnLaunched: base ok");
        }
        catch (Exception e) { BootLog("OnLaunched FATAL: " + e); throw; }
        _ = WarmBrowserAsync();
    }

    static async Task WarmBrowserAsync()
    {
        try { s_browserEnv = await CoreWebView2Environment.CreateAsync(); }
        catch { s_browserEnv = null; }
    }

    protected override MauiApp CreateMauiApp()
    {
        BootLog("CreateMauiApp: start");
        try
        {
            var app = HomeDeck.MauiProgram.CreateMauiApp();
            BootLog("CreateMauiApp: ok");
            return app;
        }
        catch (Exception e) { BootLog("CreateMauiApp FATAL: " + e); throw; }
    }

    /// <summary>DIAGNOSTIC boot log (remove once the silent-activation issue
    /// is found): %TEMP%\homedeck-boot.log. If the file never appears, managed
    /// code never ran and the kill is in native bootstrap/activation.</summary>
    public static void BootLog(object msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "homedeck-boot.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
        }
        catch { }
    }
}
