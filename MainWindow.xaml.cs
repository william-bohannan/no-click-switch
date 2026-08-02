using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NoClickSwitch;

/// <summary>
/// Always-on-top top bar: one tab per open window on a given monitor.
/// Tabs wrap and the bar grows in height; width matches the monitor work area.
/// Tab order is user-controlled via drag-and-drop; pinned favorites stay first.
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    // CSS-style em: 1em ≈ 16 device-independent pixels (DIP).
    private const double EmBase = 16.0;
    private const double DefaultTabWidthEm = 5.0;  // 80 DIP
    private const double TabHeightEm = 2.25; // 36 DIP — room for 12px type + padding
    private const double DragThreshold = 6.0;
    private const double BarCollapsedHeight = 3.0;
    private const double HeightAnimMs = 220;

    private bool _heightSyncScheduled;
    private bool _heightAnimBusy;
    private double _heightAnimTarget = double.NaN;
    private double _lastAppliedHeight = double.NaN;

    private static readonly DataFormat TabDragFormat = DataFormats.GetDataFormat("NoClickSwitch.WindowEntry.Handle");

    private readonly ObservableCollection<WindowEntry> _tabs = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _activeTabTimer;
    private readonly DispatcherTimer _hoverDelayTimer;
    private readonly DispatcherTimer _barHideTimer;
    // Shared across all bars — LibreHardwareMonitor must only Open() once per process.
    private readonly SystemStatsReader _stats = SystemStatsReader.Shared;
    private IntPtr _selfHwnd;
    private IntPtr _lastAppForeground;
    private bool _syncingAutoHideToggle;
    private bool _barCollapsed;
    private WindowEntry? _pendingHoverEntry;
    private SettingsWindow? _settingsWindow;
    private MonitorInfo _monitor;
    /// <summary>Display this bar is docked to.</summary>
    public MonitorInfo Monitor => _monitor;

    /// <summary>Only one bar should drive Windows taskbar auto-hide.</summary>
    public bool OwnsTaskbarAutoHide { get; set; }

    public bool IsPrimaryBar { get; }

    /// <summary>When false, Close is treated as hide (tray). Coordinator sets true on exit.</summary>
    public bool AllowClose { get; set; }

    // Drag state
    private Point _dragStart;
    private WindowEntry? _dragEntry;
    private bool _dragInProgress;
    private bool _suppressClick;
    private Border? _dragHighlight;

    private double _tabWidth = DefaultTabWidthEm * EmBase;
    private double _tabHeight = TabHeightEm * EmBase;
    private double _tabItemGap = 4.0;

    public double TabWidth
    {
        get => _tabWidth;
        private set
        {
            if (Math.Abs(_tabWidth - value) < 0.1)
                return;
            _tabWidth = value;
            OnPropertyChanged();
        }
    }

    public double TabHeight
    {
        get => _tabHeight;
        private set
        {
            if (Math.Abs(_tabHeight - value) < 0.1)
                return;
            _tabHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabRowStride));
        }
    }

    /// <summary>Exact gap (DIP) between neighboring tabs and between wrap rows.</summary>
    public double TabItemGap
    {
        get => _tabItemGap;
        private set
        {
            // Keep integer DIPs so layout rounding cannot make 3px look like 2/4.
            var gap = Math.Max(0, Math.Round(value));
            if (Math.Abs(_tabItemGap - gap) < 0.1)
                return;
            _tabItemGap = gap;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabItemMargin));
            OnPropertyChanged(nameof(TabRowStride));
        }
    }

    /// <summary>
    /// Per-item margin: gap only on right + bottom so adjacent items never double-up
    /// half-margins (which round unevenly and look unequal).
    /// </summary>
    public Thickness TabItemMargin => new(0, 0, _tabItemGap, _tabItemGap);

    /// <summary>WrapPanel row stride = tab height + vertical gap.</summary>
    public double TabRowStride => _tabHeight + _tabItemGap;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public MainWindow()
        : this(MonitorInfo.GetPrimary(), ownsTaskbarAutoHide: true, isPrimaryBar: true)
    {
    }

    public MainWindow(MonitorInfo monitor, bool ownsTaskbarAutoHide, bool isPrimaryBar)
    {
        _monitor = monitor;
        OwnsTaskbarAutoHide = ownsTaskbarAutoHide;
        IsPrimaryBar = isPrimaryBar;

        InitializeComponent();
        DataContext = this;
        ShowInTaskbar = isPrimaryBar; // secondary bars stay off the taskbar

        TabsList.ItemsSource = _tabs;

        // Apply geometry as early as possible, then keep it locked to monitor width.
        // Height is driven manually so we can ease grow/shrink when rows wrap.
        SourceInitialized += (_, _) =>
        {
            PositionAsTopBar();
            SyncBarHeight(animate: false);
        };
        DpiChanged += (_, _) =>
        {
            PositionAsTopBar();
            ScheduleBarHeightSync(animate: false);
        };
        SizeChanged += (_, e) =>
        {
            EnsureFullWidth();
            // Width change can reflow wrap rows → new desired height.
            if (e.WidthChanged && !_heightAnimBusy)
                ScheduleBarHeightSync(animate: true);
        };
        Closing += MainWindow_Closing;

        // When the mouse enters the bar, claim foreground rights so tab hover can
        // activate real app windows without a prior click (and without taskbar peek).
        // Skip while Start is open (or just opened) — reclaiming FG dismisses Start.
        PreviewMouseMove += (_, _) =>
        {
            if (!IsActive && !StartMenuLauncher.ShouldSuppressForegroundSteal && !_barCollapsed)
                EnsureBarForeground();
        };

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshTabs();
            SyncTaskbarAutoHideToggle();
        };

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            UpdateSystemStats();
        };

        // Track foreground window often enough that the active tab feels live.
        _activeTabTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _activeTabTimer.Tick += (_, _) => UpdateActiveTab();

        _hoverDelayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1),
        };
        _hoverDelayTimer.Tick += HoverDelayTimer_Tick;

        _barHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _barHideTimer.Tick += (_, _) =>
        {
            _barHideTimer.Stop();
            if (AppSettingsStore.Instance.Current.BarAutoHide && !IsMouseOver && _settingsWindow is null or { IsVisible: false })
                CollapseBar();
        };

        AppSettingsStore.Instance.Changed += (_, _) =>
            Dispatcher.Invoke(ApplySettings);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _selfHwnd = new WindowInteropHelper(this).Handle;
        ApplySettings();
        PositionAsTopBar();
        RefreshTabs();

        // On open: hide the Windows taskbar so this bar owns the edge (primary bar only).
        if (OwnsTaskbarAutoHide)
        {
            try
            {
                TaskbarAutoHide.SetEnabled(true);
            }
            catch
            {
                // Best-effort; bar still works if shell API fails.
            }
        }

        SyncTaskbarAutoHideToggle();
        UpdateClock();
        UpdateSystemStats();
        UpdateActiveTab();
        // After first layout pass, re-assert full width and height (and re-seed demo tabs).
        Dispatcher.BeginInvoke(() =>
        {
            PositionAsTopBar();
            SyncBarHeight(animate: false);
        }, DispatcherPriority.Loaded);
        _refreshTimer.Start();
        _clockTimer.Start();
        _activeTabTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _clockTimer.Stop();
        _activeTabTimer.Stop();
        _hoverDelayTimer.Stop();
        _barHideTimer.Stop();
        StopHeightAnimation();
        // Do not dispose shared stats here (other bars may still be sampling).
        _settingsWindow?.Close();
        // Closing restores the Windows taskbar (primary bar only).
        if (OwnsTaskbarAutoHide)
        {
            try
            {
                TaskbarAutoHide.SetEnabled(false);
            }
            catch
            {
                // Best-effort; don't block shutdown if shell API fails.
            }
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Tray mode: close button / Alt+F4 hides instead of exiting (unless coordinator allows).
        if (!AllowClose && AppSettingsStore.Instance.Current.ShowTrayIcon)
        {
            e.Cancel = true;
            HideBarFromTray();
        }
    }

    public void HideBarFromTray() => Hide();

    public void ShowBarFromTray()
    {
        Show();
        ExpandBar();
        PositionAsTopBar();
        ScheduleBarHeightSync(animate: true);
    }

    public void RelayoutForMonitor()
    {
        _monitor = MonitorInfo.GetAll().FirstOrDefault(m =>
                       string.Equals(m.DeviceName, _monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                   ?? (IsPrimaryBar ? MonitorInfo.GetPrimary() : _monitor);
        PositionAsTopBar();
        RefreshTabs();
        ScheduleBarHeightSync(animate: true);
    }

    /// <summary>Hotkey: activate tab at 0-based index.</summary>
    public void ActivateTabAtIndex(int index)
    {
        if (index < 0 || index >= _tabs.Count)
            return;
        ActivateTab(_tabs[index]);
    }

    public void OpenSettingsWindow()
    {
        MenuSettings_Click(this, new RoutedEventArgs());
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _barHideTimer.Stop();
        if (_barCollapsed)
            ExpandBar();

        if (!StartMenuLauncher.ShouldSuppressForegroundSteal)
            EnsureBarForeground();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        CancelPendingHover();
        if (!AppSettingsStore.Instance.Current.BarAutoHide)
            return;

        // Don't hide while settings (or a menu) may be in use above the bar.
        if (_settingsWindow is { IsVisible: true })
            return;

        _barHideTimer.Stop();
        _barHideTimer.Start();
    }

    #region Settings application

    private void ApplySettings()
    {
        var s = AppSettingsStore.Instance.Current;

        // Geometry — compact tightens chrome; tab row gap stays readable when wrapping.
        TabWidth = s.TabWidthEm * EmBase;
        TabHeight = TabHeightEm * EmBase;

        var chrome = s.Mode == BarMode.Compact ? 4.0 : 8.0;
        // Outer padding around the tab strip (space above first row / below last row).
        var tabsPad = s.Mode == BarMode.Compact ? 4.0 : 6.0;
        // Integer DIP between neighbors only (right + bottom on each item).
        const double tabGap = 4.0;
        LeftChrome.Margin = new Thickness(chrome, chrome, 0, 0);
        RightChrome.Margin = new Thickness(0, chrome, chrome, 0);
        TabItemGap = tabGap;
        // Item margins add `tabGap` after the last row/column. Shrink the strip's bottom/right
        // padding by that amount so outer space above == below (and left == right).
        var outerEnd = Math.Max(0, tabsPad - tabGap);
        TabsList.Margin = new Thickness(tabsPad, tabsPad, outerEnd, outerEnd);

        // Theme + accent + opacity/blur → background
        ApplyTheme(s);

        // Stats visibility + order
        ApplyStatsLayout(s);

        // Optional chrome addons (Flameshot, …)
        RefreshAddonButtons();

        // Bar auto-hide: expand if turned off
        if (!s.BarAutoHide && _barCollapsed)
            ExpandBar();

        PositionAsTopBar();
        EnsureFullWidth();
        RefreshTabs();
        ScheduleBarHeightSync(animate: true);
    }

    /// <summary>Show/hide addon chrome buttons based on install state + settings.</summary>
    public void RefreshAddonButtons()
    {
        if (FlameshotButton is null)
            return;

        FlameshotButton.Visibility = FlameshotAddon.ShouldShowOnBar
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyTheme(AppSettings s)
    {
        var dark = ResolveDark(s.Theme);
        var accent = ParseColor(s.AccentColor, Color.FromRgb(0x2F, 0x7F, 0xD1));

        // Backdrop: Blur 0 = solid (or flat alpha if Opacity < 1);
        //           1–49  = Mica on Win11 (acrylic blur on Win10);
        //           50–100 = Acrylic.
        // Opacity controls tint strength. Text stays sharp (never fades whole window).
        var kind = WindowBackdrop.KindFromBlur(s.BlurStrength);
        var frosted = kind != BackdropKind.None;
        var translucent = frosted || s.Opacity < 0.995;

        byte tintAlpha;
        if (frosted)
        {
            // Material does the frost; wash should stay light enough to see desktop texture.
            var wash = 0.22 + s.Opacity * 0.40;
            wash *= 1.0 - (s.BlurStrength / 100.0) * 0.28;
            tintAlpha = (byte)Math.Clamp((int)Math.Round(255 * Math.Clamp(wash, 0.14, 0.75)), 36, 190);
        }
        else if (translucent)
        {
            tintAlpha = (byte)Math.Clamp((int)Math.Round(255 * s.Opacity), 90, 255);
        }
        else
        {
            tintAlpha = 255;
        }

        Color bg;
        Color tabBg, tabBorder, tabActiveBg, tabActiveBorder, title, titleActive, chrome, statLabel, clockTime, clockDate;
        if (dark)
        {
            bg = Color.FromArgb(tintAlpha, 0x20, 0x20, 0x20);
            tabBg = Color.FromArgb(frosted ? (byte)0xB0 : (byte)0xFF, 0x3A, 0x3A, 0x3A);
            tabBorder = Color.FromRgb(0x55, 0x55, 0x55);
            tabActiveBg = Color.FromArgb(frosted ? (byte)0xC8 : (byte)0xFF, 0x4A, 0x4A, 0x4A);
            tabActiveBorder = Color.FromRgb(0x77, 0x77, 0x77);
            title = Color.FromRgb(0xDD, 0xDD, 0xDD);
            titleActive = Color.FromRgb(0xFF, 0xFF, 0xFF);
            chrome = Color.FromRgb(0xB0, 0xB0, 0xB0);
            statLabel = Color.FromRgb(0x9A, 0x9A, 0x9A);
            clockTime = Color.FromRgb(0xF0, 0xF0, 0xF0);
            clockDate = Color.FromRgb(0xB0, 0xB0, 0xB0);
        }
        else
        {
            bg = Color.FromArgb(tintAlpha, 0xF3, 0xF3, 0xF3);
            tabBg = Color.FromArgb(frosted ? (byte)0xB8 : (byte)0xFF, 0xE8, 0xE8, 0xE8);
            tabBorder = Color.FromRgb(0xC8, 0xC8, 0xC8);
            tabActiveBg = Color.FromArgb(frosted ? (byte)0xD0 : (byte)0xFF, 0xD4, 0xD4, 0xD4);
            tabActiveBorder = Color.FromRgb(0x8E, 0x8E, 0x8E);
            title = Color.FromRgb(0x44, 0x44, 0x44);
            titleActive = Color.FromRgb(0x22, 0x22, 0x22);
            chrome = Color.FromRgb(0x6B, 0x6B, 0x6B);
            statLabel = Color.FromRgb(0x6B, 0x6B, 0x6B);
            clockTime = Color.FromRgb(0x1A, 0x1A, 0x1A);
            clockDate = Color.FromRgb(0x55, 0x55, 0x55);
        }

        // Hover tint from accent
        var hoverBg = Lerp(dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xFF, 0xFF, 0xFF), accent, dark ? 0.35 : 0.22);
        var hoverBorder = accent;

        // Keep Window.Opacity at 1 so glyphs stay sharp; transparency is in the brush + DWM.
        Opacity = 1.0;

        // Win10 acrylic ABGR tint (alpha in high byte).
        var acrylicTint = Color.FromArgb(
            (byte)Math.Clamp((int)(s.Opacity * 160 + 50), 50, 210),
            dark ? (byte)0x1A : (byte)0xF3,
            dark ? (byte)0x1A : (byte)0xF3,
            dark ? (byte)0x1A : (byte)0xF3);

        if (IsLoaded || _selfHwnd != IntPtr.Zero)
        {
            WindowBackdrop.Apply(this, kind, dark, acrylicTint);

            // Opacity-only (no blur): still punch a transparent composition target so alpha washes show the desktop.
            if (kind == BackdropKind.None && translucent)
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    var source = HwndSource.FromHwnd(hwnd);
                    if (source?.CompositionTarget is not null)
                        source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }
                catch
                {
                    // ignore
                }
            }
        }

        Background = new SolidColorBrush(bg);
        SetBrush("TabBgBrush", tabBg);
        SetBrush("TabBorderBrush", tabBorder);
        SetBrush("TabActiveBgBrush", tabActiveBg);
        SetBrush("TabActiveBorderBrush", tabActiveBorder);
        SetBrush("TabHoverBgBrush", hoverBg);
        SetBrush("TabHoverBorderBrush", hoverBorder);
        SetBrush("TabTitleBrush", title);
        SetBrush("TabTitleActiveBrush", titleActive);
        SetBrush("ChromeIconBrush", chrome);
        SetBrush("StatLabelBrush", statLabel);
        SetBrush("ClockTimeBrush", clockTime);
        SetBrush("ClockDateBrush", clockDate);

        ClockTimeText.Foreground = new SolidColorBrush(clockTime);
        ClockDateText.Foreground = new SolidColorBrush(clockDate);
        _usageNormalBrush = CreateFrozenBrush(title.R, title.G, title.B);
        // Chrome icons use DynamicResource ChromeIconBrush (Segoe Fluent Icons TextBlocks).
        ApplyStatLabelColor(statLabel);
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    private void ApplyStatLabelColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        foreach (var tb in FindVisualChildren<TextBlock>(StatsHost))
        {
            // Only the tiny labels (CPU/MEM/etc.), not the value texts we recolor by usage.
            if (tb.FontSize <= 8.5)
                tb.Foreground = brush;
        }
    }

    private void ApplyStatsLayout(AppSettings s)
    {
        LoadStat.Visibility = s.ShowLoadStat ? Visibility.Visible : Visibility.Collapsed;
        DiskStat.Visibility = s.ShowDiskStat ? Visibility.Visible : Visibility.Collapsed;
        TempStat.Visibility = s.ShowTempStat ? Visibility.Visible : Visibility.Collapsed;
        ClockBorder.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;

        // Reorder Load / Disk / Temp inside StatsHost.
        var map = new Dictionary<string, UIElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Load"] = LoadStat,
            ["Disk"] = DiskStat,
            ["Temp"] = TempStat,
        };

        StatsHost.Children.Clear();
        foreach (var key in s.StatsOrder)
        {
            if (map.TryGetValue(key, out var el))
                StatsHost.Children.Add(el);
        }

        // Any missing keys (shouldn't happen after Clamp) — append remaining.
        foreach (var kv in map)
        {
            if (!StatsHost.Children.Contains(kv.Value))
                StatsHost.Children.Add(kv.Value);
        }
    }

    private static bool ResolveDark(ThemeMode theme)
    {
        if (theme == ThemeMode.Dark)
            return true;
        if (theme == ThemeMode.Light)
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
                return i == 0;
        }
        catch
        {
            // fall through
        }

        return false;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            return c;
        }
        catch
        {
            return fallback;
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private void CollapseBar()
    {
        if (_barCollapsed)
            return;
        _barCollapsed = true;
        RootPanel.Visibility = Visibility.Collapsed;
        ScheduleBarHeightSync(animate: true);
    }

    private void ExpandBar()
    {
        if (!_barCollapsed)
            return;
        _barCollapsed = false;
        RootPanel.Visibility = Visibility.Visible;
        // Measure after visibility flip, then ease open.
        ScheduleBarHeightSync(animate: true);
    }

    #region Smooth bar height

    /// <summary>Coalesce rapid layout changes (tab refresh, wrap reflow) into one height sync.</summary>
    private void ScheduleBarHeightSync(bool animate = true)
    {
        if (_heightSyncScheduled)
            return;

        _heightSyncScheduled = true;
        var useAnim = animate;
        Dispatcher.BeginInvoke(() =>
        {
            _heightSyncScheduled = false;
            SyncBarHeight(useAnim);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Measure content height at full bar width and ease the window to that height.
    /// Replaces SizeToContent so multi-row wrap doesn't jump.
    /// </summary>
    private void SyncBarHeight(bool animate)
    {
        if (!IsLoaded && _selfHwnd == IntPtr.Zero)
            return;

        var target = MeasureDesiredBarHeight();
        var current = ActualHeight > 1 ? ActualHeight : (double.IsNaN(Height) || Height <= 0 ? target : Height);

        if (!double.IsNaN(_lastAppliedHeight)
            && Math.Abs(_lastAppliedHeight - target) < 0.5
            && Math.Abs(current - target) < 0.5
            && !_heightAnimBusy)
        {
            return;
        }

        if (!animate || !IsLoaded || Math.Abs(current - target) < 0.5)
        {
            StopHeightAnimation();
            Height = target;
            _lastAppliedHeight = target;
            return;
        }

        AnimateBarHeight(current, target);
    }

    private double MeasureDesiredBarHeight()
    {
        if (_barCollapsed || RootPanel.Visibility != Visibility.Visible)
            return BarCollapsedHeight;

        var w = ActualWidth > 1
            ? ActualWidth
            : (Width > 1 ? Width : SystemParameters.PrimaryScreenWidth);
        if (w <= 1)
            w = SystemParameters.WorkArea.Width;

        // Unconstrained height measure so WrapPanel reports full multi-row size.
        RootPanel.Measure(new Size(w, double.PositiveInfinity));
        var h = RootPanel.DesiredSize.Height;

        // Minimum: one tab row + chrome padding.
        var min = TabHeight + TabsList.Margin.Top + TabsList.Margin.Bottom + 4;
        if (h < min)
            h = min;

        return Math.Ceiling(h);
    }

    private void AnimateBarHeight(double from, double to)
    {
        StopHeightAnimation();

        // Duration scales slightly with distance so big jumps aren't sluggish.
        var ms = Math.Clamp(HeightAnimMs + Math.Abs(to - from) * 0.6, 160, 320);

        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };

        _heightAnimBusy = true;
        _heightAnimTarget = to;
        _lastAppliedHeight = to;

        EventHandler? onDone = null;
        onDone = (_, _) =>
        {
            anim.Completed -= onDone;
            // Ignore completion if a newer animation superseded this one.
            if (Math.Abs(_heightAnimTarget - to) > 0.1)
                return;

            StopHeightAnimation();
            Height = to;
            _lastAppliedHeight = to;
            _heightAnimBusy = false;

            // Content may have reflowed during the animation (e.g. demo tabs).
            var again = MeasureDesiredBarHeight();
            if (Math.Abs(again - to) > 1.0)
                ScheduleBarHeightSync(animate: true);
        };
        anim.Completed += onDone;

        Height = from;
        BeginAnimation(HeightProperty, anim);
    }

    private void StopHeightAnimation()
    {
        BeginAnimation(HeightProperty, null);
        _heightAnimBusy = false;
    }

    #endregion

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
            yield break;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
                yield return t;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    #endregion

    private void UpdateClock()
    {
        var now = DateTime.Now;
        // Taskbar-style: short time on top, short date underneath (culture-aware).
        ClockTimeText.Text = now.ToString("t");
        ClockDateText.Text = now.ToString("d");
        ClockBorder.ToolTip = now.ToString("F");
    }

    private static readonly SolidColorBrush UsageWarnBrush = CreateFrozenBrush(0xE6, 0x51, 0x00);
    private static readonly SolidColorBrush UsageCriticalBrush = CreateFrozenBrush(0xC6, 0x28, 0x28);
    private SolidColorBrush _usageNormalBrush = CreateFrozenBrush(0x33, 0x33, 0x33);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void UpdateSystemStats()
    {
        _stats.Sample();

        // Stack 1: CPU / MEM
        CpuText.Text = $"{_stats.CpuPercent}%";
        CpuText.Foreground = BrushForUsage(_stats.CpuPercent);
        MemText.Text = $"{_stats.MemPercent}%";
        MemText.Foreground = BrushForUsage(_stats.MemPercent);
        LoadStat.ToolTip = $"{_stats.CpuToolTip}\n{_stats.MemToolTip}";

        // Stack 2: up to two fixed disks
        var d0 = _stats.Disk0;
        var d1 = _stats.Disk1;

        if (d0 is not null)
        {
            Disk0Row.Visibility = Visibility.Visible;
            Disk0Label.Text = d0.Value.Letter.TrimEnd(':');
            Disk0Text.Text = $"{d0.Value.UsedPercent}%";
            Disk0Text.Foreground = BrushForUsage(d0.Value.UsedPercent);
        }
        else
        {
            Disk0Label.Text = "—";
            Disk0Text.Text = "--%";
            Disk0Text.Foreground = _usageNormalBrush;
        }

        if (d1 is not null)
        {
            Disk1Row.Visibility = Visibility.Visible;
            Disk1Label.Text = d1.Value.Letter.TrimEnd(':');
            Disk1Text.Text = $"{d1.Value.UsedPercent}%";
            Disk1Text.Foreground = BrushForUsage(d1.Value.UsedPercent);
            DiskStat.ToolTip = d0 is not null
                ? $"{d0.Value.ToolTip}\n\n{d1.Value.ToolTip}"
                : d1.Value.ToolTip;
        }
        else
        {
            Disk1Row.Visibility = Visibility.Collapsed;
            DiskStat.ToolTip = d0 is not null ? d0.Value.ToolTip : "Disk";
        }

        // Stack 3: temps (GPU row only if present)
        if (_stats.CpuTempC is int cpuT)
        {
            CpuTempText.Text = $"{cpuT}°";
            CpuTempText.Foreground = BrushForTemp(cpuT);
        }
        else
        {
            CpuTempText.Text = "--°";
            CpuTempText.Foreground = _usageNormalBrush;
        }

        if (_stats.GpuTempC is int gpuT)
        {
            GpuTempRow.Visibility = Visibility.Visible;
            GpuTempText.Text = $"{gpuT}°";
            GpuTempText.Foreground = BrushForTemp(gpuT);
            TempStat.ToolTip = $"{_stats.CpuTempToolTip}\n{_stats.GpuTempToolTip}";
        }
        else
        {
            GpuTempRow.Visibility = Visibility.Collapsed;
            TempStat.ToolTip = _stats.CpuTempToolTip;
        }
    }

    private Brush BrushForUsage(int percent)
    {
        if (percent >= 90)
            return UsageCriticalBrush;
        if (percent >= 75)
            return UsageWarnBrush;
        return _usageNormalBrush;
    }

    private Brush BrushForTemp(int celsius)
    {
        if (celsius >= 90)
            return UsageCriticalBrush;
        if (celsius >= 75)
            return UsageWarnBrush;
        return _usageNormalBrush;
    }

    // Captured on mouse-down before our click dismisses an open Start menu.
    private bool _startWasOpenOnMouseDown;

    private void StartButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Must read before focus moves to us (that can close Start immediately).
        _startWasOpenOnMouseDown = StartMenuLauncher.IsOpen();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle: open if it was closed; close (and don't reopen) if it was open.
        StartMenuLauncher.Toggle(_startWasOpenOnMouseDown);
    }

    private void ExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+click elevates (UAC); plain click opens a normal Explorer window.
        // Elevated explorer uses /separate so it does not replace the desktop shell.
        LaunchShellApp(
            "explorer.exe",
            asAdmin: IsCtrlDown(),
            arguments: IsCtrlDown() ? "/separate" : null);
    }

    private void TerminalButton_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+click elevates (UAC); plain click opens a normal Terminal window.
        // Prefer Windows Terminal (wt.exe); fall back to PowerShell / cmd.
        var asAdmin = IsCtrlDown();
        if (LaunchShellApp("wt.exe", asAdmin))
            return;
        if (LaunchShellApp("powershell.exe", asAdmin))
            return;
        LaunchShellApp("cmd.exe", asAdmin);
    }

    private void FlameshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (FlameshotAddon.LaunchGui())
            return;

        MessageBox.Show(
            "Flameshot was not found. Install it from Settings → Addons.",
            AppInstaller.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        RefreshAddonButtons();
    }

    private static bool IsCtrlDown() =>
        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    /// <summary>
    /// Starts a shell-associated executable. When <paramref name="asAdmin"/> is true,
    /// uses the "runas" verb (UAC). Returns false if the process could not start.
    /// </summary>
    private static bool LaunchShellApp(string fileName, bool asAdmin, string? arguments = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };
            if (!string.IsNullOrEmpty(arguments))
                psi.Arguments = arguments;
            if (asAdmin)
                psi.Verb = "runas";

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            // Best-effort: missing app, UAC cancel, or start failure.
            return false;
        }
    }

    /// <summary>
    /// Pin to top of this monitor's work area at 100% of that monitor's width.
    /// Height is animated separately when wrap rows change (<see cref="SyncBarHeight"/>).
    /// </summary>
    private void PositionAsTopBar()
    {
        var work = GetWorkAreaDip();
        Left = work.Left;
        Top = work.Top;

        MinWidth = work.Width;
        MaxWidth = work.Width;
        Width = work.Width;
    }

    private void EnsureFullWidth()
    {
        var fullWidth = GetWorkAreaDip().Width;
        if (fullWidth <= 0)
            return;

        if (Math.Abs(Width - fullWidth) > 0.5
            || Math.Abs(MinWidth - fullWidth) > 0.5
            || Math.Abs(MaxWidth - fullWidth) > 0.5)
        {
            MinWidth = fullWidth;
            MaxWidth = fullWidth;
            Width = fullWidth;
        }
    }

    private Rect GetWorkAreaDip()
    {
        // Prefer live DIP conversion from this window's DPI once the source exists.
        try
        {
            var source = PresentationSource.FromVisual(this);
            var toDip = source?.CompositionTarget?.TransformFromDevice;
            if (toDip is { } m)
            {
                var px = _monitor.WorkAreaPx;
                var tl = m.Transform(new Point(px.X, px.Y));
                var br = m.Transform(new Point(px.X + px.Width, px.Y + px.Height));
                return new Rect(tl, br);
            }
        }
        catch
        {
            // fall through
        }

        return _monitor.WorkAreaDip;
    }

    /// <summary>
    /// Merge live windows into the ordered tab list without resetting user order.
    /// Closed windows are removed; new windows are appended; titles/icons update in place.
    /// </summary>
    private void RefreshTabs()
    {
        // Don't rebuild the list mid-drag.
        if (_dragInProgress)
            return;

        var settings = AppSettingsStore.Instance.Current;
        var exclude = settings.ExcludeList;
        // Primary-only: all windows. All-monitors: filter to this display.
        MonitorInfo? filterMonitor = settings.MonitorMode == MonitorBarMode.AllMonitors
            ? _monitor
            : null;
        var live = WindowEnumerator.GetOpenWindows(_selfHwnd, exclude, filterMonitor);
        var liveByHandle = new Dictionary<IntPtr, WindowEntry>();
        foreach (var w in live)
        {
            w.IsPinned = settings.IsProcessPinned(w.ProcessName);
            liveByHandle[w.Handle] = w;
        }

        // Remove closed windows (preserve relative order of the rest).
        for (var i = _tabs.Count - 1; i >= 0; i--)
        {
            if (!liveByHandle.ContainsKey(_tabs[i].Handle))
                _tabs.RemoveAt(i);
        }

        // Update title / pin when it changes (keep same entry instance so bindings stick).
        foreach (var tab in _tabs)
        {
            if (!liveByHandle.TryGetValue(tab.Handle, out var fresh))
                continue;

            if (tab.Title != fresh.Title)
                tab.Title = fresh.Title;
            if (!ReferenceEquals(tab.Icon, fresh.Icon) && fresh.Icon is not null)
                tab.Icon = fresh.Icon;
            if (tab.ProcessName != fresh.ProcessName)
                tab.ProcessName = fresh.ProcessName;
            tab.IsPinned = fresh.IsPinned;
        }

        // Append newly opened windows at the end (in enumerator order).
        var known = new HashSet<IntPtr>(_tabs.Select(t => t.Handle));
        foreach (var w in live)
        {
            if (known.Add(w.Handle))
                _tabs.Add(w);
        }

        ApplyPinnedSort(settings);

        UpdateActiveTab();
        Dispatcher.BeginInvoke(() =>
        {
            EnsureFullWidth();
            ScheduleBarHeightSync(animate: true);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Pinned tabs first (settings order), then unpinned (user order preserved).</summary>
    private void ApplyPinnedSort(AppSettings settings)
    {
        var pinnedOrder = settings.PinnedProcesses;
        if (pinnedOrder.Count == 0 && _tabs.All(t => !t.IsPinned))
            return;

        int PinRank(WindowEntry e)
        {
            if (!e.IsPinned || string.IsNullOrEmpty(e.ProcessName))
                return int.MaxValue;
            var name = e.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? e.ProcessName[..^4]
                : e.ProcessName;
            var idx = pinnedOrder.FindIndex(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : int.MaxValue - 1;
        }

        var sorted = _tabs
            .Select((e, i) => (e, i, rank: PinRank(e)))
            .OrderBy(x => x.rank)
            .ThenBy(x => x.i)
            .Select(x => x.e)
            .ToList();

        // Rebuild only if order changed.
        var changed = sorted.Count != _tabs.Count;
        if (!changed)
        {
            for (var i = 0; i < sorted.Count; i++)
            {
                if (!ReferenceEquals(sorted[i], _tabs[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return;

        _tabs.Clear();
        foreach (var t in sorted)
            _tabs.Add(t);
    }

    /// <summary>
    /// Marks the tab for the current foreground app. When our bar is focused,
    /// keeps highlighting the last real app window.
    /// </summary>
    private void UpdateActiveTab()
    {
        var fg = ForegroundTracker.GetForegroundRootWindow();

        // Ignore our own bar (and its popups) so hover/click on the bar
        // doesn't clear the active app highlight.
        if (fg != IntPtr.Zero && fg != _selfHwnd && !IsOwnedByUs(fg))
            _lastAppForeground = fg;

        var activeHwnd = _lastAppForeground;
        foreach (var tab in _tabs)
            tab.IsActive = ForegroundTracker.IsTabForForeground(tab.Handle, activeHwnd);
    }

    private bool IsOwnedByUs(IntPtr hWnd)
    {
        if (_selfHwnd == IntPtr.Zero || hWnd == IntPtr.Zero)
            return false;
        if (hWnd == _selfHwnd)
            return true;

        // Walk owner chain a few steps (GW_OWNER = 4).
        var walk = hWnd;
        for (var i = 0; i < 6 && walk != IntPtr.Zero; i++)
        {
            if (walk == _selfHwnd)
                return true;
            walk = GetWindow(walk, 4);
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private void SyncTaskbarAutoHideToggle()
    {
        _syncingAutoHideToggle = true;
        try
        {
            if (!OwnsTaskbarAutoHide)
            {
                TaskbarAutoHideToggle.Visibility = Visibility.Collapsed;
                return;
            }

            TaskbarAutoHideToggle.Visibility = Visibility.Visible;
            var enabled = TaskbarAutoHide.IsEnabled;
            TaskbarAutoHideToggle.IsChecked = enabled;
            TaskbarAutoHideToggle.ToolTip = enabled
                ? "Taskbar auto-hide: ON (click to turn off)"
                : "Taskbar auto-hide: OFF (click to turn on)";
        }
        finally
        {
            _syncingAutoHideToggle = false;
        }
    }

    private void TaskbarAutoHideToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoHideToggle || !OwnsTaskbarAutoHide)
            return;

        var wantEnabled = TaskbarAutoHideToggle.IsChecked == true;
        TaskbarAutoHide.SetEnabled(wantEnabled);
        SyncTaskbarAutoHideToggle();
    }

    #region Drag and drop reordering

    private void EnsureBarForeground()
    {
        if (StartMenuLauncher.ShouldSuppressForegroundSteal)
            return;

        if (_selfHwnd == IntPtr.Zero)
            _selfHwnd = new WindowInteropHelper(this).Handle;

        if (_selfHwnd == IntPtr.Zero)
            return;

        // Take foreground without requiring a click first.
        WindowActivator.ForceOurWindowForeground(_selfHwnd);
        if (!IsActive)
            Activate();
    }

    private void Tab_MouseEnter(object sender, MouseEventArgs e)
    {
        // Hover preview: bring that window to the front (no resize). Skip while dragging.
        if (_dragInProgress)
            return;

        // Don't yank focus away from an open Start menu.
        if (StartMenuLauncher.ShouldSuppressForegroundSteal)
            return;

        if (sender is not FrameworkElement { Tag: WindowEntry entry })
            return;

        var delayMs = AppSettingsStore.Instance.Current.HoverDelayMs;
        if (delayMs <= 0)
        {
            ActivateOnHover(entry);
            return;
        }

        // Debounce: wait until the pointer rests on this tab.
        _pendingHoverEntry = entry;
        _hoverDelayTimer.Stop();
        _hoverDelayTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _hoverDelayTimer.Start();
    }

    private void Tab_MouseLeave(object sender, MouseEventArgs e)
    {
        // Leaving a tab cancels a pending delayed activation.
        if (sender is FrameworkElement { Tag: WindowEntry entry }
            && ReferenceEquals(_pendingHoverEntry, entry))
        {
            CancelPendingHover();
        }
    }

    private void HoverDelayTimer_Tick(object? sender, EventArgs e)
    {
        _hoverDelayTimer.Stop();
        var entry = _pendingHoverEntry;
        _pendingHoverEntry = null;
        if (entry is null || _dragInProgress)
            return;
        if (StartMenuLauncher.ShouldSuppressForegroundSteal)
            return;
        ActivateOnHover(entry);
    }

    private void CancelPendingHover()
    {
        _hoverDelayTimer.Stop();
        _pendingHoverEntry = null;
    }

    private void ActivateOnHover(WindowEntry entry)
    {
        // 1) Claim foreground from whatever app is active (may be Explorer/shell).
        EnsureBarForeground();

        // 2) Hand foreground to the real app window (not a taskbar button flash).
        WindowActivator.BringToFront(entry.Handle);
        _lastAppForeground = entry.Handle;
        UpdateActiveTab();

        // 3) Shell sometimes peeks/shows the taskbar on failed activation; re-assert.
        ReassertAutoHideIfNeeded();
    }

    private void ReassertAutoHideIfNeeded()
    {
        // Keep auto-hide on when the toggle says it should be (default after open).
        if (TaskbarAutoHideToggle.IsChecked != true)
            return;

        try
        {
            if (!TaskbarAutoHide.IsEnabled)
                TaskbarAutoHide.SetEnabled(true);
        }
        catch
        {
            // Best-effort.
        }
    }

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowEntry entry })
            return;

        _dragStart = e.GetPosition(null);
        _dragEntry = entry;
        _suppressClick = false;
        _dragInProgress = false;
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragEntry is null)
            return;

        if (sender is not FrameworkElement element)
            return;

        var pos = e.GetPosition(null);
        if ((pos - _dragStart).Length < DragThreshold)
            return;

        _dragInProgress = true;
        _suppressClick = true;

        var data = new DataObject(TabDragFormat.Name, _dragEntry.Handle.ToInt64());
        try
        {
            DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDragHighlight();
            _dragInProgress = false;
            _dragEntry = null;
            // Keep _suppressClick true so the release after drag does not activate.
        }
    }

    private void Tab_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressClick)
        {
            _suppressClick = false;
            _dragEntry = null;
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowEntry entry })
        {
            _dragEntry = null;
            return;
        }

        _dragEntry = null;
        ActivateTab(entry);
        e.Handled = true;
    }

    private void TabsList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabDragFormat.Name))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TabsList_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedHandle(e, out var handle))
            return;

        var insertIndex = GetInsertIndex(e.GetPosition(TabsList));
        MoveTab(handle, insertIndex);
        ClearDragHighlight();
        e.Handled = true;
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabDragFormat.Name))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Tab_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border && e.Data.GetDataPresent(TabDragFormat.Name))
            SetDragHighlight(border);
    }

    private void Tab_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border && ReferenceEquals(_dragHighlight, border))
            ClearDragHighlight();
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement target || !TryGetDraggedHandle(e, out var handle))
            return;

        var targetEntry = target.Tag as WindowEntry;
        if (targetEntry is null)
            return;

        var targetIndex = IndexOfHandle(targetEntry.Handle);
        if (targetIndex < 0)
            return;

        // Insert before or after depending on which half of the target tab we're over.
        var pos = e.GetPosition(target);
        var insertIndex = pos.X < target.ActualWidth / 2 ? targetIndex : targetIndex + 1;

        MoveTab(handle, insertIndex);
        ClearDragHighlight();
        e.Handled = true;
    }

    private void TabsList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        // Default pointer on tabs; only switch to move cursor while a drag is active.
        if (e.Effects.HasFlag(DragDropEffects.Move))
        {
            Mouse.SetCursor(Cursors.SizeAll);
            e.UseDefaultCursors = false;
            e.Handled = true;
        }
    }

    private static bool TryGetDraggedHandle(DragEventArgs e, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (!e.Data.GetDataPresent(TabDragFormat.Name))
            return false;

        var raw = e.Data.GetData(TabDragFormat.Name);
        if (raw is long l)
        {
            handle = new IntPtr(l);
            return handle != IntPtr.Zero;
        }

        if (raw is int i)
        {
            handle = new IntPtr(i);
            return handle != IntPtr.Zero;
        }

        return false;
    }

    private int IndexOfHandle(IntPtr handle)
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Handle == handle)
                return i;
        }

        return -1;
    }

    private void MoveTab(IntPtr handle, int insertIndex)
    {
        var from = IndexOfHandle(handle);
        if (from < 0)
            return;

        insertIndex = Math.Clamp(insertIndex, 0, _tabs.Count);

        // When moving forward, account for the removal shifting indices down.
        if (insertIndex > from)
            insertIndex--;

        if (insertIndex == from)
            return;

        _tabs.Move(from, insertIndex);
    }

    /// <summary>
    /// Best-effort insert index for drops on empty space of the wrap panel.
    /// </summary>
    private int GetInsertIndex(Point positionInTabsList)
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (TabsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            // Container is ContentPresenter; visual tab is the child border.
            var tab = FindDescendantBorder(container) ?? container;
            Point origin;
            try
            {
                origin = tab.TransformToAncestor(TabsList).Transform(new Point(0, 0));
            }
            catch
            {
                continue;
            }

            var rect = new Rect(origin, tab.RenderSize);
            if (!rect.Contains(positionInTabsList))
                continue;

            return positionInTabsList.X < rect.Left + rect.Width / 2 ? i : i + 1;
        }

        // Not over any tab: append, or insert before first tab that starts below/after the point.
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (TabsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var tab = FindDescendantBorder(container) ?? container;
            Point origin;
            try
            {
                origin = tab.TransformToAncestor(TabsList).Transform(new Point(0, 0));
            }
            catch
            {
                continue;
            }

            var midY = origin.Y + tab.RenderSize.Height / 2;
            var midX = origin.X + tab.RenderSize.Width / 2;

            // Same row (roughly) and drop point is left of tab center → insert here.
            if (Math.Abs(positionInTabsList.Y - midY) <= tab.RenderSize.Height
                && positionInTabsList.X < midX)
            {
                return i;
            }

            // Drop point is above this tab's row → insert here.
            if (positionInTabsList.Y < origin.Y)
                return i;
        }

        return _tabs.Count;
    }

    private static Border? FindDescendantBorder(DependencyObject root)
    {
        if (root is Border b)
            return b;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendantBorder(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private void SetDragHighlight(Border border)
    {
        if (ReferenceEquals(_dragHighlight, border))
            return;

        ClearDragHighlight();
        _dragHighlight = border;
        // Local values for drag target; cleared so Style hover works again after.
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD1));
        border.BorderThickness = new Thickness(2);
        border.Background = new SolidColorBrush(Color.FromRgb(0xD0, 0xE8, 0xFF));
    }

    private void ClearDragHighlight()
    {
        if (_dragHighlight is null)
            return;

        // Remove local values so Style (including hover) takes effect again.
        _dragHighlight.ClearValue(Border.BackgroundProperty);
        _dragHighlight.ClearValue(Border.BorderBrushProperty);
        _dragHighlight.ClearValue(Border.BorderThicknessProperty);
        _dragHighlight = null;
    }

    #endregion

    private void ActivateTab(WindowEntry entry)
    {
        var free = GetFreeSpaceBelowBarInDevicePixels();
        WindowActivator.ActivateAndFit(
            entry.Handle,
            free.X,
            free.Y,
            free.Width,
            free.Height);
        _lastAppForeground = entry.Handle;
        UpdateActiveTab();
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTabEntry(sender, out var entry))
            return;


        if (entry is null || entry.Handle == IntPtr.Zero)
            return;

        var handle = entry.Handle;
        WindowActivator.CloseWindow(handle);

        if (_lastAppForeground == handle)
            _lastAppForeground = IntPtr.Zero;

        // WM_CLOSE is async; remove the tab as soon as the window is actually gone
        // (including after a save prompt). Don't wait for the slow refresh timer.
        _ = WatchAndRemoveClosedTabAsync(handle);
    }

    /// <summary>
    /// Poll until <paramref name="handle"/> is destroyed, then drop its tab immediately.
    /// Stops early if the window is still alive after a long wait (e.g. user cancelled close).
    /// </summary>
    private async Task WatchAndRemoveClosedTabAsync(IntPtr handle)
    {
        // First chance after the UI message queue processes the close.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (TryRemoveTabIfClosed(handle))
            return;

        // Fast poll while the window is shutting down (or waiting on a dialog).
        const int pollMs = 50;
        const int maxMs = 120_000;
        for (var waited = 0; waited < maxMs; waited += pollMs)
        {
            await Task.Delay(pollMs).ConfigureAwait(true);
            if (TryRemoveTabIfClosed(handle))
                return;
        }
    }

    /// <summary>
    /// If the HWND is gone (or no longer a listed top-level window), remove its tab.
    /// Returns true when the tab was removed or was already absent.
    /// </summary>
    private bool TryRemoveTabIfClosed(IntPtr handle)
    {
        if (WindowActivator.IsAlive(handle))
        {
            // Still alive: keep tab (save dialogs, close cancelled, slow apps).
            return false;
        }

        for (var i = _tabs.Count - 1; i >= 0; i--)
        {
            if (_tabs[i].Handle == handle)
                _tabs.RemoveAt(i);
        }

        UpdateActiveTab();
        return true;
    }

    /// <summary>
    /// Rectangle under this bar, full primary width, down to the work-area bottom.
    /// Values are device pixels for Win32 SetWindowPos.
    /// </summary>
    private (int X, int Y, int Width, int Height) GetFreeSpaceBelowBarInDevicePixels()
    {
        var work = GetWorkAreaDip();

        var barHeight = ActualHeight > 0 ? ActualHeight : Height;
        if (barHeight <= 0)
            barHeight = TabHeight + 12;

        var dipLeft = Left;
        var dipTop = Top + barHeight;
        var dipWidth = work.Width;
        var dipHeight = work.Bottom - dipTop;
        if (dipHeight < 1)
            dipHeight = 1;

        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;

        var topLeft = toDevice.Transform(new Point(dipLeft, dipTop));
        var bottomRight = toDevice.Transform(new Point(dipLeft + dipWidth, dipTop + dipHeight));

        var x = (int)Math.Round(topLeft.X);
        var y = (int)Math.Round(topLeft.Y);
        var w = (int)Math.Round(bottomRight.X - topLeft.X);
        var h = (int)Math.Round(bottomRight.Y - topLeft.Y);

        return (x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (MenuButton.ContextMenu is null)
            return;

        RefreshAppMenu();
        MenuButton.ContextMenu.PlacementTarget = MenuButton;
        MenuButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MenuButton.ContextMenu.IsOpen = true;
    }

    private void AppMenu_Opened(object sender, RoutedEventArgs e)
    {
        RefreshAppMenu();
    }

    private void RefreshAppMenu()
    {
        var installed = AppInstaller.IsInstalled;
        MenuInstall.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        MenuUninstall.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        MenuAppName.Header = $"{AppInstaller.DisplayName} ({AppInstaller.ShortName})";
        MenuVersion.Header = $"Version {AppInstaller.VersionString}";
        MenuGitHub.Header = "GitHub";
        MenuGitHub.ToolTip = AppInstaller.GitHubUrl;
        MenuWebsite.Header = "Website";
        MenuWebsite.ToolTip = AppInstaller.WebsiteUrl;
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        if (AppSettingsStore.Instance.Current.ShowTrayIcon)
        {
            // Hide all bars; app stays in the tray.
            if (BarCoordinator.Instance.BarsVisible)
                BarCoordinator.Instance.ToggleBarsVisible();
            return;
        }

        // No tray: exit the whole app.
        AllowClose = true;
        Application.Current.Shutdown();
    }

    private void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BarCoordinator.Instance.EnsureBarsVisible();

            // Reuse a single settings window across bars if one is already open.
            if (Application.Current is not null)
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (w is SettingsWindow existing && existing.IsVisible)
                    {
                        existing.Activate();
                        return;
                    }
                }
            }

            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            // Keep bar expanded while configuring.
            ExpandBar();

            // Do not set Owner to a topmost bar — that can break activation / lifetime.
            _settingsWindow = new SettingsWindow
            {
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                // Resume bar auto-hide behavior after settings closes.
                if (AppSettingsStore.Instance.Current.BarAutoHide && !IsMouseOver)
                {
                    _barHideTimer.Stop();
                    _barHideTimer.Start();
                }
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            _settingsWindow = null;
            MessageBox.Show(
                $"Could not open Settings.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                AppInstaller.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void TabMinimize_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTabEntry(sender, out var entry))
            return;
        WindowActivator.MinimizeWindow(entry.Handle);
    }

    private void TabPin_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTabEntry(sender, out var entry))
            return;

        var proc = entry.ProcessName;
        if (string.IsNullOrWhiteSpace(proc))
            proc = WindowExclude.TryGetProcessNamePublic(entry.Handle);
        if (string.IsNullOrWhiteSpace(proc))
        {
            MessageBox.Show(
                "Could not determine the process name for this window.",
                AppInstaller.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var key = proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? proc[..^4] : proc;
        AppSettingsStore.Instance.Update(s =>
        {
            var list = s.PinnedProcesses;
            var idx = list.FindIndex(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                list.RemoveAt(idx);
            else
                list.Add(key);
        });
        // Settings Changed will refresh; also force a local refresh for snappy pin toggle.
        RefreshTabs();
    }

    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var entry = menu.PlacementTarget is FrameworkElement fe
            ? fe.Tag as WindowEntry ?? fe.DataContext as WindowEntry
            : null;

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MenuPin") is { } pinItem
            && entry is not null)
        {
            pinItem.Header = entry.IsPinned ? "Unpin" : "Pin";
        }
    }

    private static bool TryGetTabEntry(object sender, out WindowEntry entry)
    {
        entry = null!;
        if (sender is not MenuItem menuItem)
            return false;

        entry = menuItem.DataContext as WindowEntry
            ?? (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target }
                ? target.Tag as WindowEntry ?? target.DataContext as WindowEntry
                : null)!;
        return entry is not null;
    }

    private void MenuGitHub_Click(object sender, RoutedEventArgs e)
        => OpenExternalUrl(AppInstaller.GitHubUrl, "GitHub page");

    private void MenuWebsite_Click(object sender, RoutedEventArgs e)
        => OpenExternalUrl(AppInstaller.WebsiteUrl, "website");

    private static void OpenExternalUrl(string url, string label)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the {label}.\n\n{ex.Message}",
                AppInstaller.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MenuInstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppInstaller.Install();
            MessageBox.Show(
                $"Installed for this user.\n\n{AppInstaller.DisplayName} ({AppInstaller.ShortName}) will start automatically when you sign in to Windows.",
                AppInstaller.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Install failed.\n\n{ex.Message}",
                AppInstaller.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void MenuUninstall_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            $"Uninstall {AppInstaller.DisplayName} ({AppInstaller.ShortName}) for this user?\n\nThis removes auto-start and installed files.",
            AppInstaller.DisplayName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            var runningFromInstall = AppInstaller.IsRunningFromInstallLocation();
            AppInstaller.Uninstall();

            if (runningFromInstall)
            {
                MessageBox.Show(
                    $"Uninstall started. {AppInstaller.DisplayName} will close now.",
                    AppInstaller.DisplayName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
            }
            else
            {
                MessageBox.Show(
                    "Uninstalled. Auto-start and installed files have been removed.",
                    AppInstaller.DisplayName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Uninstall failed.\n\n{ex.Message}",
                AppInstaller.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
