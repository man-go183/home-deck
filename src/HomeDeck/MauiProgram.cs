using HomeDeck.Controls;
using HomeDeck.Handlers;
using HomeDeck.Services;
using HomeDeck.Views;
using Microsoft.Extensions.DependencyInjection;

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
