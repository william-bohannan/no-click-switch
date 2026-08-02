using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwitchedBar;

/// <summary>
/// Always-on-top top bar: one tab per open window.
/// Tabs are 5em wide × 2em tall; they wrap and the bar grows in height.
/// Bar is always 100% of the primary screen width.
/// Tab order is user-controlled via drag-and-drop and preserved across refreshes.
/// </summary>
public partial class MainWindow : Window
{
    // CSS-style em: 1em ≈ 16 device-independent pixels (DIP).
    private const double EmBase = 16.0;
    private const double TabWidthEm = 5.0;  // 80 DIP
    private const double TabHeightEm = 2.0; // 32 DIP
    private const double DragThreshold = 6.0;

    private static readonly DataFormat TabDragFormat = DataFormats.GetDataFormat("SwitchedBar.WindowEntry.Handle");

    private readonly ObservableCollection<WindowEntry> _tabs = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _activeTabTimer;
    private readonly SystemStatsReader _stats = new();
    private IntPtr _selfHwnd;
    private IntPtr _lastAppForeground;
    private bool _syncingAutoHideToggle;

    // Drag state
    private Point _dragStart;
    private WindowEntry? _dragEntry;
    private bool _dragInProgress;
    private bool _suppressClick;
    private Border? _dragHighlight;

    public double TabWidth { get; } = TabWidthEm * EmBase;
    public double TabHeight { get; } = TabHeightEm * EmBase;

    public MainWindow()
    {
        InitializeComponent();

        TabsList.ItemsSource = _tabs;

        // Apply geometry as early as possible, then keep it locked to 100% width.
        SourceInitialized += (_, _) => PositionAsTopBar();
        DpiChanged += (_, _) => PositionAsTopBar();
        SizeChanged += (_, _) => EnsureFullWidth();

        // When the mouse enters the bar, claim foreground rights so tab hover can
        // activate real app windows without a prior click (and without taskbar peek).
        MouseEnter += (_, _) => EnsureBarForeground();
        PreviewMouseMove += (_, _) =>
        {
            if (!IsActive)
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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _selfHwnd = new WindowInteropHelper(this).Handle;
        PositionAsTopBar();
        RefreshTabs();

        // On open: hide the Windows taskbar so this bar owns the edge.
        try
        {
            TaskbarAutoHide.SetEnabled(true);
        }
        catch
        {
            // Best-effort; bar still works if shell API fails.
        }

        SyncTaskbarAutoHideToggle();
        UpdateClock();
        UpdateSystemStats();
        UpdateActiveTab();
        // After first layout pass, re-assert full width (SizeToContent can shrink it).
        Dispatcher.BeginInvoke(PositionAsTopBar, DispatcherPriority.Loaded);
        _refreshTimer.Start();
        _clockTimer.Start();
        _activeTabTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _clockTimer.Stop();
        _activeTabTimer.Stop();
        _stats.Dispose();
        // Closing Switched Bar restores the Windows taskbar (turns auto-hide off).
        try
        {
            TaskbarAutoHide.SetEnabled(false);
        }
        catch
        {
            // Best-effort; don't block shutdown if shell API fails.
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        // Taskbar-style: short time on top, short date underneath (culture-aware).
        ClockTimeText.Text = now.ToString("t");
        ClockDateText.Text = now.ToString("d");
        ClockBorder.ToolTip = now.ToString("F");
    }

    private static readonly SolidColorBrush UsageNormalBrush = CreateFrozenBrush(0x33, 0x33, 0x33);
    private static readonly SolidColorBrush UsageWarnBrush = CreateFrozenBrush(0xE6, 0x51, 0x00);
    private static readonly SolidColorBrush UsageCriticalBrush = CreateFrozenBrush(0xC6, 0x28, 0x28);

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
            Disk0Text.Foreground = UsageNormalBrush;
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
            CpuTempText.Foreground = UsageNormalBrush;
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

    private static Brush BrushForUsage(int percent)
    {
        if (percent >= 90)
            return UsageCriticalBrush;
        if (percent >= 75)
            return UsageWarnBrush;
        return UsageNormalBrush;
    }

    private static Brush BrushForTemp(int celsius)
    {
        if (celsius >= 90)
            return UsageCriticalBrush;
        if (celsius >= 75)
            return UsageWarnBrush;
        return UsageNormalBrush;
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
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; ignore if Explorer cannot start.
        }
    }

    /// <summary>
    /// Pin to top of primary screen at 100% width.
    /// Height comes from SizeToContent so wrapped rows expand the bar.
    /// </summary>
    private void PositionAsTopBar()
    {
        var workArea = SystemParameters.WorkArea;
        var fullWidth = SystemParameters.PrimaryScreenWidth;
        if (fullWidth <= 0)
            fullWidth = workArea.Width;

        Left = workArea.Left;
        Top = workArea.Top;

        MinWidth = fullWidth;
        MaxWidth = fullWidth;
        Width = fullWidth;
    }

    private void EnsureFullWidth()
    {
        var fullWidth = SystemParameters.PrimaryScreenWidth;
        if (fullWidth <= 0)
            fullWidth = SystemParameters.WorkArea.Width;

        if (Math.Abs(Width - fullWidth) > 0.5
            || Math.Abs(MinWidth - fullWidth) > 0.5
            || Math.Abs(MaxWidth - fullWidth) > 0.5)
        {
            MinWidth = fullWidth;
            MaxWidth = fullWidth;
            Width = fullWidth;
        }
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

        var live = WindowEnumerator.GetOpenWindows(_selfHwnd);
        var liveByHandle = new Dictionary<IntPtr, WindowEntry>();
        foreach (var w in live)
            liveByHandle[w.Handle] = w;

        // Remove closed windows (preserve relative order of the rest).
        for (var i = _tabs.Count - 1; i >= 0; i--)
        {
            if (!liveByHandle.ContainsKey(_tabs[i].Handle))
                _tabs.RemoveAt(i);
        }

        // Update title when it changes (keep same entry instance so IsActive bindings stick).
        foreach (var tab in _tabs)
        {
            if (!liveByHandle.TryGetValue(tab.Handle, out var fresh))
                continue;

            if (tab.Title != fresh.Title)
                tab.Title = fresh.Title;
        }

        // Append newly opened windows at the end (in enumerator order).
        var known = new HashSet<IntPtr>(_tabs.Select(t => t.Handle));
        foreach (var w in live)
        {
            if (known.Add(w.Handle))
                _tabs.Add(w);
        }

        UpdateActiveTab();
        Dispatcher.BeginInvoke(EnsureFullWidth, DispatcherPriority.Loaded);
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
        if (_syncingAutoHideToggle)
            return;

        var wantEnabled = TaskbarAutoHideToggle.IsChecked == true;
        TaskbarAutoHide.SetEnabled(wantEnabled);
        SyncTaskbarAutoHideToggle();
    }

    #region Drag and drop reordering

    private void EnsureBarForeground()
    {
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

        if (sender is not FrameworkElement { Tag: WindowEntry entry })
            return;

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

    /// <summary>
    /// Rectangle under this bar, full primary width, down to the work-area bottom.
    /// Values are device pixels for Win32 SetWindowPos.
    /// </summary>
    private (int X, int Y, int Width, int Height) GetFreeSpaceBelowBarInDevicePixels()
    {
        var workArea = SystemParameters.WorkArea;
        var fullWidth = SystemParameters.PrimaryScreenWidth;
        if (fullWidth <= 0)
            fullWidth = workArea.Width;

        var barHeight = ActualHeight > 0 ? ActualHeight : Height;
        if (barHeight <= 0)
            barHeight = TabHeight + 12;

        var dipLeft = Left;
        var dipTop = Top + barHeight;
        var dipWidth = fullWidth;
        var dipHeight = workArea.Bottom - dipTop;
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
        MenuVersion.Header = AppInstaller.VersionString;
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppInstaller.GitHubUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the GitHub page.\n\n{ex.Message}",
                "Switched Bar",
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
                "Installed for this user.\n\nSwitched Bar will start automatically when you sign in to Windows.",
                "Switched Bar",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Install failed.\n\n{ex.Message}",
                "Switched Bar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void MenuUninstall_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Uninstall Switched Bar for this user?\n\nThis removes auto-start and installed files.",
            "Switched Bar",
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
                    "Uninstall started. Switched Bar will close now.",
                    "Switched Bar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Close();
            }
            else
            {
                MessageBox.Show(
                    "Uninstalled. Auto-start and installed files have been removed.",
                    "Switched Bar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Uninstall failed.\n\n{ex.Message}",
                "Switched Bar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
