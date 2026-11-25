using ElecStock.Appli.Services;

namespace ElecStock.Appli;

public partial class App : Application
{
    readonly ServerConnection _server;

    public App(ServerConnection server)
    {
        _server = server;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage(_server))
        {
            Title = "ElecStock",
            Width = 1280,
            Height = 860,
        };
}
