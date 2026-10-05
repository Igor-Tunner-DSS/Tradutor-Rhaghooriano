using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.App;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly IStartupCoordinator _startup;

    public App(AppShell shell, IStartupCoordinator startup)
    {
        InitializeComponent();
        _shell = shell;
        _startup = startup;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell) { Title = "Rhagho'oriano", MinimumWidth = 900, MinimumHeight = 640 };
        // Abertura: SQLite → dados locais → conexão → sessão → sincronização (ver StartupCoordinator).
        window.Created += (_, _) => _ = _startup.EnsureReadyAsync();
        return window;
    }
}
