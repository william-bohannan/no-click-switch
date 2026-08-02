using System.Drawing;
using Forms = System.Windows.Forms;

namespace NoClickSwitch;

/// <summary>System tray icon with Show/Hide bar, Settings, Exit.</summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Action _toggleBars;
    private readonly Action _openSettings;
    private readonly Func<bool> _barsVisible;
    private readonly Action _exit;

    public TrayIconService(
        Action toggleBars,
        Action openSettings,
        Func<bool> barsVisible,
        Action exit)
    {
        _toggleBars = toggleBars;
        _openSettings = openSettings;
        _barsVisible = barsVisible;
        _exit = exit;

        _icon = new Forms.NotifyIcon
        {
            Text = $"{AppInstaller.DisplayName} ({AppInstaller.ShortName})",
            Visible = false,
            Icon = LoadIcon(),
        };

        _icon.DoubleClick += (_, _) =>
        {
            if (!_barsVisible())
                _toggleBars();
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenu(menu);
        _icon.ContextMenuStrip = menu;
    }

    public void SetVisible(bool visible) => _icon.Visible = visible;

    private void RebuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var shown = _barsVisible();
        var toggle = new Forms.ToolStripMenuItem(shown ? "Hide bar" : "Show bar");
        toggle.Click += (_, _) => _toggleBars();
        menu.Items.Add(toggle);

        var settings = new Forms.ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => _openSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exit = new Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => _exit();
        menu.Items.Add(exit);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/NoClickSwitch.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo is not null)
            {
                using var s = streamInfo.Stream;
                // NotifyIcon needs its own copy of the icon handle.
                return new Icon(s);
            }
        }
        catch
        {
            // fall through
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
