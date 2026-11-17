using HomeDeck.Controls;
using HomeDeck.Handlers;
using HomeDeck.Services;
using HomeDeck.Views;

namespace HomeDeck;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(h =>
            {
                h.AddHandler<ShellWebView, ShellWebViewHandler>();
            });

        builder.Services.AddSingleton<ProfileStore>();
        builder.Services.AddSingleton<AdBlockService>();
        builder.Services.AddTransient<LauncherPage>();
        builder.Services.AddTransient<BrowserPage>();

        return builder.Build();
    }
}
