using System.Reflection;
using System.Runtime.InteropServices;

namespace NoClickSwitch;

/// <summary>
/// App identity and version. Distribution is Microsoft Store (MSIX).
/// </summary>
internal static class AppInstaller
{
    /// <summary>Executable / settings-folder name (no spaces).</summary>
    public const string AppName = "NoClickSwitch";

    /// <summary>Short product name.</summary>
    public const string ShortName = "NCS";

    /// <summary>Full product display name.</summary>
    public const string DisplayName = "No Click Switch";

    /// <summary>
    /// Unpackaged identity for <c>dotnet run</c>. Store / MSIX supplies its own AUMID.
    /// </summary>
    public const string AppUserModelId = "william-bohannan.NoClickSwitch";

    public const string GitHubUrl = "https://github.com/william-bohannan/no-click-switch";
    public const string WebsiteUrl = "https://noclickswitch.com";

    public static string VersionString
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }

            var v = asm.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Bind this process to <see cref="AppUserModelId"/> before any HWND exists.
    /// Skip when packaged — the MSIX identity already supplies the AUMID.
    /// </summary>
    public static void BindProcessAppUserModelId()
    {
        if (AppIdentity.IsPackaged)
            return;

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch
        {
            // best-effort
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
