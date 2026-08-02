using System.Runtime.InteropServices;
using System.Text;

namespace SwitchedBar;

/// <summary>
/// Opens / closes / toggles the Windows Start menu.
/// Clicking our bar steals focus and can dismiss Start before Click runs, so
/// callers should snapshot <see cref="IsOpen"/> on mouse-down, then call
/// <see cref="Toggle(bool)"/> with that snapshot.
/// </summary>
internal static class StartMenuLauncher
{
    private const int InputKeyboard = 1;
    private const ushort VkLwin = 0x5B;
    private const ushort VkEscape = 0x1B;
    private const uint KeyeventfKeyup = 0x0002;

    /// <summary>True if the Start launcher is currently visible.</summary>
    public static bool IsOpen()
    {
        if (TryIsLauncherVisibleViaCom(out var visible))
            return visible;

        return IsStartWindowVisibleFallback();
    }

    /// <summary>
    /// Toggle Start using the open-state captured <em>before</em> our button
    /// stole focus (pass the value from PreviewMouseLeftButtonDown).
    /// </summary>
    public static void Toggle(bool wasOpenBeforeClick)
    {
        if (wasOpenBeforeClick)
        {
            // Click may already have dismissed Start; Esc is a safe no-op if closed.
            // Do NOT send Win here — that would reopen it.
            Close();
        }
        else
        {
            Open();
        }
    }

    public static void Open() => SendKey(VkLwin);

    public static void Close()
    {
        // Prefer Esc (closes Start/Search without reopening).
        SendKey(VkEscape);

        // If still open (rare), Win toggles it shut.
        if (IsOpen())
            SendKey(VkLwin);
    }

    private static void SendKey(ushort vk)
    {
        var inputs = new Input[2];

        inputs[0].type = InputKeyboard;
        inputs[0].U.ki = new KeybdInput
        {
            wVk = vk,
            dwFlags = 0,
        };

        inputs[1].type = InputKeyboard;
        inputs[1].U.ki = new KeybdInput
        {
            wVk = vk,
            dwFlags = KeyeventfKeyup,
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    /// <summary>
    /// Official shell API: IAppVisibility.IsLauncherVisible.
    /// </summary>
    private static bool TryIsLauncherVisibleViaCom(out bool visible)
    {
        visible = false;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7E5FE3D9-985F-4908-91F9-EE19F9FD1514"));
            if (type is null)
                return false;

            var obj = Activator.CreateInstance(type);
            if (obj is null)
                return false;

            var appVisibility = (IAppVisibility)obj;
            // BOOL is a 4-byte Win32 value.
            var hr = appVisibility.IsLauncherVisible(out var isVisible);
            if (hr != 0)
                return false;

            visible = isVisible != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fallback: look for known Start / launcher host windows.
    /// </summary>
    private static bool IsStartWindowVisibleFallback()
    {
        var found = false;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            var className = GetClassName(hWnd);
            var title = GetWindowTitle(hWnd);

            // Windows 10/11 Start / Search hosts (varies by build).
            if (className is "Windows.UI.Core.CoreWindow"
                && title is "Start" or "Search" or "SearchUI")
            {
                found = true;
                return false;
            }

            if (className is "ImmersiveLauncher" or "Windows.UI.Input.InputSite.WindowClass")
            {
                // Immersive launcher is Start when visible with a non-empty rect.
                if (GetWindowRect(hWnd, out var rc)
                    && rc.Right > rc.Left
                    && rc.Bottom > rc.Top
                    && (title is "Start" or "" or "Search"))
                {
                    // Many InputSite windows exist; require Start title when present.
                    if (className == "ImmersiveLauncher" || title is "Start" or "Search")
                    {
                        found = true;
                        return false;
                    }
                }
            }

            // Win11 XAML Start host sometimes uses this class with Start-related title.
            if (className.Contains("XamlExplorerHost", StringComparison.OrdinalIgnoreCase)
                && title.Contains("Start", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0)
            return string.Empty;
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [ComImport]
    [Guid("2246EA2D-CAEA-4444-A3C4-6DE827E44313")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppVisibility
    {
        // HRESULT GetAppVisibilityOnMonitor(HMONITOR, MONITOR_APP_VISIBILITY*);
        [PreserveSig]
        int GetAppVisibilityOnMonitor(IntPtr hMonitor, out int pMode);

        // HRESULT IsLauncherVisible(BOOL*);
        [PreserveSig]
        int IsLauncherVisible(out int pfVisible);

        // Advise / Unadvise omitted — not needed for a one-shot query.
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput ki;
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public HardwareInput hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
}
