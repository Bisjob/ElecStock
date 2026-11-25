namespace ElecStock.Appli.WinUI;

/// <summary>WinUI entry point of the MAUI app.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
