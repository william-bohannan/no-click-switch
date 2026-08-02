using System.Windows;

namespace NoClickSwitch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Bars, tray, and hotkeys are owned by the coordinator (not StartupUri).
        BarCoordinator.Instance.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        BarCoordinator.Instance.Shutdown();
        base.OnExit(e);
    }
}
