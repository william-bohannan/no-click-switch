using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NoClickSwitch;

/// <summary>System backdrop material preference.</summary>
internal enum BackdropKind
{
    /// <summary>Solid / no DWM material.</summary>
    None,

    /// <summary>Windows 11 Mica (desktop-tinted, soft).</summary>
    Mica,

    /// <summary>Windows 11 Acrylic or Windows 10 acrylic blur.</summary>
    Acrylic,
}

/// <summary>
/// Applies Windows 11 Mica / Acrylic (DWM system backdrop) or Windows 10 acrylic blur.
/// Falls back to a transparent client so a semi-transparent WPF brush can show the desktop.
/// </summary>
internal static class WindowBackdrop
{
    // DwmSetWindowAttribute
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38; // Win11 22523+
    private const int DwmwaMicaEffect = 1029;       // early Win11

    private const int DwmsbtAuto = 0;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;     // Mica
    private const int DwmsbtTransientWindow = 3; // Acrylic
    private const int DwmsbtTabbedWindow = 4;    // Tabbed Mica

    // SetWindowCompositionAttribute (Win10 acrylic)
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentDisabled = 0;
    private const int WcaAccentPolicy = 19;

    public static bool IsWindows11OrGreater { get; } = GetIsWindows11OrGreater();

    /// <summary>
    /// Chooses material from blur strength: 0 = none, 1–49 = Mica, 50–100 = Acrylic.
    /// </summary>
    public static BackdropKind KindFromBlur(double blurStrength)
    {
        if (blurStrength <= 0.5)
            return BackdropKind.None;
        if (blurStrength < 50)
            return BackdropKind.Mica;
        return BackdropKind.Acrylic;
    }

    /// <summary>
    /// Prepares the HWND for backdrop (transparent composition target + frame extend)
    /// and enables the requested material. Call after the window handle exists.
    /// </summary>
    public static void Apply(Window window, BackdropKind kind, bool darkMode, Color tintAbgrHint)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            // Let DWM paint behind WPF content.
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is not null)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var dark = darkMode ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            if (kind == BackdropKind.None)
            {
                DisableBackdrop(hwnd);
                return;
            }

            // -1 margins: extend glass/frame into the entire client area.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            if (IsWindows11OrGreater && TrySetSystemBackdrop(hwnd, kind))
            {
                // Clear any Win10 accent policy left over from a previous session.
                SetWin10Accent(hwnd, enabled: false, tintAbgr: 0);
                return;
            }

            // Windows 10 (or Win11 if system backdrop failed): acrylic / blur via composition.
            // Alpha in high byte of ABGR; stronger blur setting → slightly stronger tint alpha.
            var abgr = ToAbgr(tintAbgrHint);
            SetWin10Accent(hwnd, enabled: true, tintAbgr: abgr);
        }
        catch
        {
            // Best-effort — bar still works with a plain brush.
        }
    }

    private static bool TrySetSystemBackdrop(IntPtr hwnd, BackdropKind kind)
    {
        var type = kind switch
        {
            BackdropKind.Mica => DwmsbtMainWindow,
            BackdropKind.Acrylic => DwmsbtTransientWindow,
            _ => DwmsbtNone,
        };

        // Prefer public SYSTEMBACKDROP_TYPE (38).
        if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) == 0)
            return true;

        // Early Win11: boolean Mica only.
        if (kind == BackdropKind.Mica)
        {
            var mica = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaMicaEffect, ref mica, sizeof(int)) == 0)
                return true;
        }

        return false;
    }

    private static void DisableBackdrop(IntPtr hwnd)
    {
        var none = DwmsbtNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
        var micaOff = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaMicaEffect, ref micaOff, sizeof(int));

        // Reset extended frame so solid backgrounds look correct.
        var margins = new Margins();
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        SetWin10Accent(hwnd, enabled: false, tintAbgr: 0);
    }

    private static void SetWin10Accent(IntPtr hwnd, bool enabled, uint tintAbgr)
    {
        var accent = new AccentPolicy
        {
            AccentState = enabled ? AccentEnableAcrylicBlurBehind : AccentDisabled,
            AccentFlags = enabled ? 2 : 0, // draw all borders / gradient
            GradientColor = tintAbgr,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var accentPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = accentPtr,
                SizeOfData = size,
            };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    /// <summary>Pack color as Win10 acrylic ABGR (alpha in high byte).</summary>
    public static uint ToAbgr(Color c)
        => ((uint)c.A << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;

    private static bool GetIsWindows11OrGreater()
    {
        try
        {
            // Environment.OSVersion is reliable on net8+ with proper app manifest / runtime.
            var v = Environment.OSVersion.Version;
            // Win11 is 10.0.22000+
            return v.Major >= 10 && v.Build >= 22000;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);
}
