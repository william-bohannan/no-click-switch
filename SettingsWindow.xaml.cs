using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NoClickSwitch;

public partial class SettingsWindow : Window
{
    private static readonly string[] DefaultAccentSwatches =
    {
        "#2F7FD1", "#0078D4", "#107C10", "#D83B01",
        "#8764B8", "#E3008C", "#00B7C3", "#5C2D91",
        "#FFB900", "#E81123", "#333333", "#6B6B6B",
    };

    private readonly ObservableCollection<string> _statsOrder = new();
    private readonly ObservableCollection<ExcludeRow> _excludeRows = new();
    /// <summary>True while loading XAML / applying stored values — do not persist.</summary>
    private bool _loading = true;

    public SettingsWindow()
    {
        try
        {
            // _loading stays true through InitializeComponent: sliders coerce Value and fire
            // ValueChanged mid-parse, which must not call PersistFromUi (other controls are null).
            InitializeComponent();
            StatsOrderList.ItemsSource = _statsOrder;
            ExcludeListBox.ItemsSource = _excludeRows;
            BuildAccentSwatches();
            LoadFromStore();

            // Wire nav only after every page panel exists.
            NavList.SelectionChanged += NavList_SelectionChanged;
            // Defer first selection until layout is ready (avoids init re-entrancy).
            Loaded += (_, _) =>
            {
                try
                {
                    if (NavList.SelectedItem is null)
                        SelectStage("Mode");
                    else if (NavList.SelectedItem is ListBoxItem { Tag: string tag })
                        ShowPage(tag);
                }
                catch
                {
                    // ignore
                }
            };
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Select the stage ListBoxItem for <paramref name="tag"/> (skips section headers).</summary>
    private void SelectStage(string tag)
    {
        if (NavList is null)
        {
            ShowPage(tag);
            return;
        }

        foreach (var obj in NavList.Items)
        {
            if (obj is ListBoxItem item
                && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                NavList.SelectedItem = item;
                return;
            }
        }

        ShowPage(tag);
    }

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var hex in DefaultAccentSwatches)
        {
            var border = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 6),
                Background = (Brush)new BrushConverter().ConvertFromString(hex)!,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = hex,
                ToolTip = hex,
            };
            border.MouseLeftButtonUp += (_, _) =>
            {
                AccentHexBox.Text = hex;
                ApplyAccentPreview(hex);
                PersistFromUi();
            };
            AccentSwatches.Children.Add(border);
        }
    }

    private void LoadFromStore()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var s = AppSettingsStore.Instance.Snapshot();

            ModeStandard.IsChecked = s.Mode == BarMode.Standard;
            ModeCompact.IsChecked = s.Mode == BarMode.Compact;

            ThemeLight.IsChecked = s.Theme == ThemeMode.Light;
            ThemeDark.IsChecked = s.Theme == ThemeMode.Dark;
            ThemeSystem.IsChecked = s.Theme == ThemeMode.System;

            AccentHexBox.Text = s.AccentColor;
            ApplyAccentPreview(s.AccentColor);

            OpacitySlider.Value = s.Opacity;
            OpacityValueText.Text = $"{(int)Math.Round(s.Opacity * 100)}%";
            BlurSlider.Value = s.BlurStrength;
            BlurValueText.Text = $"{(int)Math.Round(s.BlurStrength)}";

            HoverSlider.Value = s.HoverDelayMs;
            HoverValueText.Text = FormatHover(s.HoverDelayMs);

            StatLoadCheck.IsChecked = s.ShowLoadStat;
            StatDiskCheck.IsChecked = s.ShowDiskStat;
            StatTempCheck.IsChecked = s.ShowTempStat;
            StatClockCheck.IsChecked = s.ShowClock;

            _statsOrder.Clear();
            foreach (var key in s.StatsOrder)
                _statsOrder.Add(key);

            TabWidthSlider.Value = s.TabWidthEm;
            TabWidthValueText.Text = FormatTabWidth(s.TabWidthEm);

            BarAutoHideCheck.IsChecked = s.BarAutoHide;

            _excludeRows.Clear();
            foreach (var rule in s.ExcludeList)
                _excludeRows.Add(ExcludeRow.FromRule(rule));

            HotkeysCheck.IsChecked = s.EnableTabHotkeys;
            WinNumberCheck.IsChecked = s.EnableWinNumberHotkeys;
            MonitorPrimary.IsChecked = s.MonitorMode == MonitorBarMode.PrimaryOnly;
            MonitorAll.IsChecked = s.MonitorMode == MonitorBarMode.AllMonitors;
            TrayCheck.IsChecked = s.ShowTrayIcon;

            FlameshotShowCheck.IsChecked = s.AddonFlameshotShowOnBar;
            RefreshFlameshotStatus();
        }
        finally
        {
            // Caller (ctor) clears _loading after full init; Reset keeps previous state.
            _loading = wasLoading;
        }
    }

    private void RefreshFlameshotStatus()
    {
        if (FlameshotStatusText is null || FlameshotInstallButton is null)
            return;

        FlameshotAddon.InvalidateCache();
        var path = FlameshotAddon.TryGetExePath();
        var installed = path is not null;
        FlameshotStatusText.Text = installed
            ? $"Status: Installed\n{path}"
            : "Status: Not installed";
        FlameshotInstallButton.Content = installed ? "Reinstall with winget" : "Install with winget";
        FlameshotInstallButton.IsEnabled = true;
        if (FlameshotUninstallButton is not null)
            FlameshotUninstallButton.IsEnabled = installed;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (NavList?.SelectedItem is not ListBoxItem item)
                return;

            // Skip section headers (no Tag).
            var tag = item.Tag as string;
            if (string.IsNullOrEmpty(tag))
            {
                if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is ListBoxItem prev)
                    NavList.SelectedItem = prev;
                return;
            }

            ShowPage(tag);
        }
        catch
        {
            // Never let nav selection take down the app.
        }
    }

    private void ShowPage(string tag)
    {
        SetPageVisible(PageMode, tag == "Mode");
        SetPageVisible(PageTheme, tag == "Theme");
        SetPageVisible(PageAppearance, tag == "Appearance");
        SetPageVisible(PageHover, tag == "Hover");
        SetPageVisible(PageStats, tag == "Stats");
        SetPageVisible(PageTabWidth, tag == "TabWidth");
        SetPageVisible(PageAutoHide, tag == "AutoHide");
        SetPageVisible(PageExclude, tag == "Exclude");
        SetPageVisible(PageKeyboard, tag == "Keyboard");
        SetPageVisible(PageMonitors, tag == "Monitors");
        SetPageVisible(PageAddons, tag == "Addons");

        if (tag == "Addons")
            RefreshFlameshotStatus();
    }

    private static void SetPageVisible(UIElement? page, bool visible)
    {
        if (page is null)
            return;
        page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        PersistFromUi();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText is null)
            return;
        OpacityValueText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        if (_loading)
            return;
        PersistFromUi();
    }

    private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurValueText is null)
            return;
        var v = (int)Math.Round(e.NewValue);
        BlurValueText.Text = v <= 0
            ? "0 (solid)"
            : v < 50
                ? $"{v} (Mica)"
                : $"{v} (Acrylic)";
        if (_loading)
            return;
        PersistFromUi();
    }

    private void HoverSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverValueText is null)
            return;
        HoverValueText.Text = FormatHover((int)Math.Round(e.NewValue));
        if (_loading)
            return;
        PersistFromUi();
    }

    private void TabWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TabWidthValueText is null)
            return;
        TabWidthValueText.Text = FormatTabWidth(e.NewValue);
        if (_loading)
            return;
        PersistFromUi();
    }

    private void AccentHexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        if (TryNormalizeHex(AccentHexBox.Text, out var hex))
        {
            AccentHexBox.Text = hex;
            ApplyAccentPreview(hex);
            PersistFromUi();
        }
        else
        {
            AccentHexBox.Text = AppSettingsStore.Instance.Current.AccentColor;
        }
    }

    private void AccentHexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        AccentHexBox_LostFocus(sender, e);
        e.Handled = true;
    }

    private void StatMoveUp_Click(object sender, RoutedEventArgs e)
    {
        var i = StatsOrderList.SelectedIndex;
        if (i <= 0)
            return;
        _statsOrder.Move(i, i - 1);
        StatsOrderList.SelectedIndex = i - 1;
        PersistFromUi();
    }

    private void StatMoveDown_Click(object sender, RoutedEventArgs e)
    {
        var i = StatsOrderList.SelectedIndex;
        if (i < 0 || i >= _statsOrder.Count - 1)
            return;
        _statsOrder.Move(i, i + 1);
        StatsOrderList.SelectedIndex = i + 1;
        PersistFromUi();
    }

    private void ExcludeAdd_Click(object sender, RoutedEventArgs e)
    {
        var pattern = ExcludePatternBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(pattern))
            return;

        var kind = ExcludeKindBox.SelectedIndex == 1
            ? ExcludeMatchKind.Process
            : ExcludeMatchKind.Title;

        _excludeRows.Add(new ExcludeRow
        {
            Kind = kind,
            Pattern = pattern,
        });
        ExcludePatternBox.Text = "";
        PersistFromUi();
    }

    private void ExcludeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludeListBox.SelectedItem is not ExcludeRow row)
            return;
        _excludeRows.Remove(row);
        PersistFromUi();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Reset all customization settings to defaults?",
            "Reset settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        AppSettingsStore.Instance.Replace(new AppSettings());
        LoadFromStore();
        StatusText.Text = "Defaults restored.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PersistFromUi()
    {
        if (_loading)
            return;

        // Defensive: events can fire before every control is assigned.
        if (ModeCompact is null || OpacitySlider is null || StatusText is null
            || HotkeysCheck is null || TrayCheck is null || MonitorAll is null)
            return;

        try
        {
            var current = AppSettingsStore.Instance.Current;
            var settings = new AppSettings
            {
                Mode = ModeCompact.IsChecked == true ? BarMode.Compact : BarMode.Standard,
                Theme = ThemeDark.IsChecked == true
                    ? ThemeMode.Dark
                    : ThemeLight.IsChecked == true
                        ? ThemeMode.Light
                        : ThemeMode.System,
                AccentColor = TryNormalizeHex(AccentHexBox?.Text, out var hex)
                    ? hex
                    : current.AccentColor,
                Opacity = OpacitySlider.Value,
                BlurStrength = BlurSlider?.Value ?? current.BlurStrength,
                HoverDelayMs = (int)Math.Round(HoverSlider?.Value ?? current.HoverDelayMs),
                TabWidthEm = TabWidthSlider?.Value ?? current.TabWidthEm,
                BarAutoHide = BarAutoHideCheck?.IsChecked == true,
                ShowLoadStat = StatLoadCheck?.IsChecked == true,
                ShowDiskStat = StatDiskCheck?.IsChecked == true,
                ShowTempStat = StatTempCheck?.IsChecked == true,
                ShowClock = StatClockCheck?.IsChecked == true,
                StatsOrder = _statsOrder.ToList(),
                ExcludeList = _excludeRows.Select(r => r.ToRule()).ToList(),
                // Preserve pins edited from tab context menus.
                PinnedProcesses = current.PinnedProcesses.ToList(),
                EnableTabHotkeys = HotkeysCheck.IsChecked == true,
                EnableWinNumberHotkeys = WinNumberCheck?.IsChecked == true,
                MonitorMode = MonitorAll.IsChecked == true
                    ? MonitorBarMode.AllMonitors
                    : MonitorBarMode.PrimaryOnly,
                ShowTrayIcon = TrayCheck.IsChecked == true,
                AddonFlameshotShowOnBar = FlameshotShowCheck is null || FlameshotShowCheck.IsChecked == true,
            };

            AppSettingsStore.Instance.Replace(settings);
            StatusText.Text = "Saved.";
        }
        catch
        {
            // Best-effort save — never crash the settings window.
        }
    }

    private void FlameshotInstall_Click(object sender, RoutedEventArgs e)
    {
        if (!FlameshotAddon.StartInstallWithWinget())
        {
            MessageBox.Show(
                this,
                "Could not start the install PowerShell window.\n\n" +
                "Install Flameshot manually:\n" +
                "  winget install Flameshot.Flameshot\n\n" +
                "Or see " + FlameshotAddon.DocsInstallUrl,
                "Install Flameshot",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = "Flameshot install started in PowerShell…";
        MessageBox.Show(
            this,
            "A PowerShell window was opened to install Flameshot via winget " +
            "(or Chocolatey if winget is not available).\n\n" +
            "When it finishes, click Refresh status here. " +
            "The Flameshot icon will appear to the right of Terminal if “Show icon on the bar” is checked.",
            "Install Flameshot",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void FlameshotUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!FlameshotAddon.IsInstalled)
        {
            RefreshFlameshotStatus();
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "Uninstall Flameshot from this PC?\n\n" +
            "This runs winget uninstall (or Chocolatey) in PowerShell.",
            "Uninstall Flameshot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        if (!FlameshotAddon.StartUninstallWithWinget())
        {
            MessageBox.Show(
                this,
                "Could not start the uninstall PowerShell window.\n\n" +
                "Uninstall manually:\n" +
                "  winget uninstall Flameshot.Flameshot\n\n" +
                "Or remove it from Windows Settings → Apps.",
                "Uninstall Flameshot",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = "Flameshot uninstall started in PowerShell…";
        MessageBox.Show(
            this,
            "A PowerShell window was opened to uninstall Flameshot.\n\n" +
            "When it finishes, click Refresh status. The bar icon will hide once Flameshot is gone.",
            "Uninstall Flameshot",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void FlameshotRefresh_Click(object sender, RoutedEventArgs e)
    {
        FlameshotAddon.InvalidateCache();
        RefreshFlameshotStatus();
        // Notify bars to re-evaluate addon chrome visibility.
        AppSettingsStore.Instance.Replace(AppSettingsStore.Instance.Snapshot());
        StatusText.Text = FlameshotAddon.IsInstalled ? "Flameshot detected." : "Flameshot still not found.";
    }

    private void ApplyAccentPreview(string hex)
    {
        try
        {
            AccentPreview.Background = (Brush)new BrushConverter().ConvertFromString(hex)!;
        }
        catch
        {
            // ignore invalid
        }
    }

    private static string FormatHover(int ms)
        => ms <= 0 ? "Instant (0 ms)" : $"{ms} ms";

    private static string FormatTabWidth(double em)
        => $"{em:0.#} em ({(int)Math.Round(em * 16)} px)";

    private static bool TryNormalizeHex(string? text, out string hex)
    {
        hex = "#2F7FD1";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;

        if (text.Length is not (7 or 9))
            return false;

        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            var ok = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!ok)
                return false;
        }

        hex = text.ToUpperInvariant();
        // Keep #RRGGBB form for storage.
        if (hex.Length == 9)
            hex = "#" + hex[3..]; // drop alpha if pasted
        return true;
    }

    private sealed class ExcludeRow
    {
        public ExcludeMatchKind Kind { get; set; }
        public string Pattern { get; set; } = "";
        public string Display => $"{Kind}: {Pattern}";

        public ExcludeRule ToRule() => new() { Kind = Kind, Pattern = Pattern };

        public static ExcludeRow FromRule(ExcludeRule r) => new()
        {
            Kind = r.Kind,
            Pattern = r.Pattern,
        };
    }
}
