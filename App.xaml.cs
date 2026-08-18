using System.Windows;

namespace NoClickSwitch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstance.TryEnter(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        // Bars, tray, and hotkeys are owned by the coordinator (not StartupUri).
        BarCoordinator.Instance.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { BarCoordinator.Instance.Shutdown(); } catch { /* ignore */ }
        SingleInstance.Dispose();
        base.OnExit(e);
    }
}
