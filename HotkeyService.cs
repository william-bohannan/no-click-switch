using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NoClickSwitch;

/// <summary>
/// Global hotkeys via RegisterHotKey. Default: Ctrl+Alt+1..0 → tab index.
/// Optional Win+1..0 (often swallowed by the shell taskbar).
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    // VK_0..VK_9
    private static readonly int[] DigitVks = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30 };

    private HwndSource? _source;
    private readonly List<int> _registeredIds = new();

    /// <summary>Raised with 0-based tab index (0 = first tab, 9 = tenth).</summary>
    public event Action<int>? JumpToTabIndex;

    public void Start()
    {
        if (_source is not null)
            return;

        var p = new HwndSourceParameters("NoClickSwitch.Hotkeys")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        ApplyFromSettings();
    }

    public void ApplyFromSettings()
    {
        UnregisterAll();
        var s = AppSettingsStore.Instance.Current;
        if (!s.EnableTabHotkeys || _source is null)
            return;

        var hwnd = _source.Handle;
        // Ctrl+Alt+digit — reliable; does not fight the shell.
        RegisterDigits(hwnd, ModControl | ModAlt | ModNoRepeat, idBase: 100);

        if (s.EnableWinNumberHotkeys)
            RegisterDigits(hwnd, ModWin | ModNoRepeat, idBase: 200);
    }

    private void RegisterDigits(IntPtr hwnd, uint modifiers, int idBase)
    {
        for (var i = 0; i < DigitVks.Length; i++)
        {
            var id = idBase + i;
            if (RegisterHotKey(hwnd, id, modifiers, (uint)DigitVks[i]))
                _registeredIds.Add(id);
        }
    }

    private void UnregisterAll()
    {
        if (_source is null)
            return;
        var hwnd = _source.Handle;
        foreach (var id in _registeredIds)
            _ = UnregisterHotKey(hwnd, id);
        _registeredIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
            return IntPtr.Zero;

        var id = wParam.ToInt32();
        var index = id % 100; // 0..9
        if (index is >= 0 and <= 9)
        {
            JumpToTabIndex?.Invoke(index);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
