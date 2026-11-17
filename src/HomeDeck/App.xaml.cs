using Microsoft.Extensions.DependencyInjection;

namespace HomeDeck;

public partial class App : Application
{
    readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new NavigationPage(_services.GetRequiredService<Views.LauncherPage>()));
}
