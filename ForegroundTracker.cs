using System.Runtime.InteropServices;

namespace SwitchedBar;

/// <summary>Resolves the current foreground top-level window for tab highlighting.</summary>
internal static class ForegroundTracker
{
    private const uint GaRoot = 2;

    public static IntPtr GetForegroundRootWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return IntPtr.Zero;

        var root = GetAncestor(fg, GaRoot);
        return root != IntPtr.Zero ? root : fg;
    }

    /// <summary>
    /// True if <paramref name="tabHwnd"/> is the foreground window or an ancestor/owner of it.
    /// </summary>
    public static bool IsTabForForeground(IntPtr tabHwnd, IntPtr foregroundRoot)
    {
        if (tabHwnd == IntPtr.Zero || foregroundRoot == IntPtr.Zero)
            return false;

        if (tabHwnd == foregroundRoot)
            return true;

        // Foreground might be a child/owned dialog of the tab's main window.
        var walk = foregroundRoot;
        for (var i = 0; i < 8 && walk != IntPtr.Zero; i++)
        {
            if (walk == tabHwnd)
                return true;
            walk = GetWindow(walk, 4); // GW_OWNER
        }

        var rootOfTab = GetAncestor(tabHwnd, GaRoot);
        return rootOfTab != IntPtr.Zero && rootOfTab == foregroundRoot;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
