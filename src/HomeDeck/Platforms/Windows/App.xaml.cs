using Microsoft.Maui;

namespace HomeDeck.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => HomeDeck.MauiProgram.CreateMauiApp();
}
