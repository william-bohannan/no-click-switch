using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NoClickSwitch;

/// <summary>
/// Writes a .lnk with an explicit AppUserModelID so Windows 11 Start Search
/// lists the app under Apps (file-path AppIDs are omitted from Search).
/// </summary>
internal static class ShellShortcut
{
    public static void Create(
        string lnkPath,
        string targetPath,
        string workingDirectory,
        string description,
        string appUserModelId,
        string displayName)
    {
        var dir = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            link.SetShowCmd(1);

            var store = (IPropertyStore)link;
            SetString(store, PkeyAppUserModelId, appUserModelId, required: true);
            SetString(store, PkeyRelaunchCommand, $"\"{targetPath}\"", required: false);
            SetString(store, PkeyRelaunchIconResource, $"{targetPath},0", required: false);
            SetString(store, PkeyRelaunchDisplayName, displayName, required: false);
            SetString(store, PkeyKeywords, "NCS;NoClickSwitch;No Click Switch", required: false);
            var hr = store.Commit();
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            if (File.Exists(lnkPath))
                File.Delete(lnkPath);

            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }

        NotifyShellOfShortcut(lnkPath);
    }

    public static void NotifyAssociationChanged()
        => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    private static void NotifyShellOfShortcut(string lnkPath)
    {
        var ptr = Marshal.StringToHGlobalUni(lnkPath);
        try
        {
            SHChangeNotify(ShcneCreate, ShcnfPathW | ShcnfFlush, ptr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value, bool required)
    {
        var pv = PropVariant.FromString(value);
        try
        {
            var hr = store.SetValue(ref key, ref pv);
            if (hr < 0 && required)
                Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            pv.Dispose();
        }
    }

    // PKEY_AppUserModel_ID
    private static readonly PropertyKey PkeyAppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    // PKEY_AppUserModel_RelaunchCommand / Icon / DisplayName
    private static readonly PropertyKey PkeyRelaunchCommand = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 2);
    private static readonly PropertyKey PkeyRelaunchIconResource = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);
    private static readonly PropertyKey PkeyRelaunchDisplayName = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 4);

    // System.Keywords (PIDSI_KEYWORDS)
    private static readonly PropertyKey PkeyKeywords = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 5);

    private const uint ShcneCreate = 0x00000002;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant : IDisposable
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pointerValue;
        public IntPtr extra;

        private const ushort VtLpwstr = 31;

        public static PropVariant FromString(string value)
            => new()
            {
                vt = VtLpwstr,
                pointerValue = Marshal.StringToCoTaskMemUni(value),
            };

        public void Dispose()
        {
            PropVariantClear(ref this);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }
}
