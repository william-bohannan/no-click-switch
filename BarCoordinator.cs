using System.Windows;
using System.Windows.Threading;

namespace NoClickSwitch;

/// <summary>
/// Owns bar windows (primary or per-monitor), tray icon, and global hotkeys.
/// </summary>
internal sealed class BarCoordinator
{
    public static BarCoordinator Instance { get; } = new();

    private readonly List<MainWindow> _bars = new();
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private bool _barsVisible = true;
    private MonitorBarMode _lastMonitorMode;
    private bool _started;

    public IReadOnlyList<MainWindow> Bars => _bars;

    public bool BarsVisible => _barsVisible && _bars.Any(b => b.IsVisible);

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _lastMonitorMode = AppSettingsStore.Instance.Current.MonitorMode;
        RebuildBars();

        _hotkeys = new HotkeyService();
        _hotkeys.JumpToTabIndex += OnJumpToTab;
        _hotkeys.Start();

        _tray = new TrayIconService(
            toggleBars: ToggleBarsVisible,
            openSettings: OpenSettings,
            barsVisible: () => _barsVisible,
            exit: ExitApp);
        ApplyTrayVisibility();

        AppSettingsStore.Instance.Changed += OnSettingsChanged;
        SystemEventsMonitor.DisplaySettingsChanged += OnDisplayChanged;
    }

    public void Shutdown()
    {
        AppSettingsStore.Instance.Changed -= OnSettingsChanged;
        SystemEventsMonitor.DisplaySettingsChanged -= OnDisplayChanged;
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        foreach (var bar in _bars)
            bar.AllowClose = true;
        CloseAllBars();
        SystemStatsReader.ShutdownShared();
    }

    public void OpenSettings()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            EnsureBarsVisible();
            var host = _bars.FirstOrDefault(b => b.IsVisible) ?? _bars.FirstOrDefault();
            host?.OpenSettingsWindow();
        });
    }

    public void ToggleBarsVisible()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _barsVisible = !_barsVisible;
            foreach (var bar in _bars)
            {
                if (_barsVisible)
                    bar.ShowBarFromTray();
                else
                    bar.HideBarFromTray();
            }
        });
    }

    public void EnsureBarsVisible()
    {
        if (_barsVisible)
            return;
        _barsVisible = true;
        foreach (var bar in _bars)
            bar.ShowBarFromTray();
    }

    private void OnJumpToTab(int index)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            EnsureBarsVisible();
            // Prefer the bar that contains the mouse cursor; else primary.
            var bar = FindBarUnderCursor() ?? _bars.FirstOrDefault(b => b.IsPrimaryBar) ?? _bars.FirstOrDefault();
            bar?.ActivateTabAtIndex(index);
        });
    }

    private MainWindow? FindBarUnderCursor()
    {
        try
        {
            var p = System.Windows.Forms.Control.MousePosition;
            foreach (var bar in _bars)
            {
                if (bar.Monitor.BoundsPx.Contains(p.X, p.Y))
                    return bar;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var s = AppSettingsStore.Instance.Current;
            ApplyTrayVisibility();
            _hotkeys?.ApplyFromSettings();

            if (s.MonitorMode != _lastMonitorMode)
            {
                _lastMonitorMode = s.MonitorMode;
                RebuildBars();
            }
        });
    }

    private void OnDisplayChanged()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (AppSettingsStore.Instance.Current.MonitorMode == MonitorBarMode.AllMonitors
                || _bars.Count != 1)
            {
                RebuildBars();
            }
            else
            {
                foreach (var bar in _bars)
                    bar.RelayoutForMonitor();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void ApplyTrayVisibility()
    {
        var show = AppSettingsStore.Instance.Current.ShowTrayIcon;
        _tray?.SetVisible(show);
    }

    private void RebuildBars()
    {
        CloseAllBars();

        var mode = AppSettingsStore.Instance.Current.MonitorMode;
        var monitors = MonitorInfo.GetAll();

        if (mode == MonitorBarMode.PrimaryOnly)
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            AddBar(primary, ownsTaskbar: true, isPrimaryBar: true);
        }
        else
        {
            var first = true;
            foreach (var m in monitors)
            {
                AddBar(m, ownsTaskbar: first && m.IsPrimary, isPrimaryBar: m.IsPrimary);
                first = false;
            }

            // Ensure exactly one bar owns taskbar auto-hide (primary if present).
            if (_bars.All(b => !b.OwnsTaskbarAutoHide) && _bars.Count > 0)
                _bars[0].OwnsTaskbarAutoHide = true;
        }

        if (_barsVisible)
        {
            foreach (var bar in _bars)
                bar.Show();
        }
    }

    private void AddBar(MonitorInfo monitor, bool ownsTaskbar, bool isPrimaryBar)
    {
        var bar = new MainWindow(monitor, ownsTaskbar, isPrimaryBar);
        _bars.Add(bar);
    }

    private void CloseAllBars()
    {
        foreach (var bar in _bars.ToList())
        {
            try
            {
                bar.AllowClose = true;
                bar.Close();
            }
            catch
            {
                // ignore
            }
        }

        _bars.Clear();
    }

    private void ExitApp()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Shutdown();
            Application.Current.Shutdown();
        });
    }
}

/// <summary>Thin wrapper so we can subscribe to display changes without WinForms leaks.</summary>
internal static class SystemEventsMonitor
{
    public static event Action? DisplaySettingsChanged;

    static SystemEventsMonitor()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
                DisplaySettingsChanged?.Invoke();
        }
        catch
        {
            // Non-interactive session, etc.
        }
    }
}
