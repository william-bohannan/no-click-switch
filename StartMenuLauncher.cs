using System.Runtime.InteropServices;

namespace SwiztchBar;

/// <summary>Opens the Windows Start menu (simulates the Windows key).</summary>
internal static class StartMenuLauncher
{
    private const int InputKeyboard = 1;
    private const ushort VkLwin = 0x5B;
    private const uint KeyeventfKeyup = 0x0002;

    public static void Open()
    {
        var inputs = new Input[2];

        inputs[0].type = InputKeyboard;
        inputs[0].U.ki = new KeybdInput
        {
            wVk = VkLwin,
            dwFlags = 0,
        };

        inputs[1].type = InputKeyboard;
        inputs[1].U.ki = new KeybdInput
        {
            wVk = VkLwin,
            dwFlags = KeyeventfKeyup,
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
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
}
