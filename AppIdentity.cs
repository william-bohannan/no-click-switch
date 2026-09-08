using System.Runtime.InteropServices;
using System.Text;

namespace NoClickSwitch;

/// <summary>
/// Detects MSIX / Microsoft Store package identity at runtime.
/// Unpackaged <c>dotnet run</c> keeps the GitHub-style install tools;
/// the Store package is the shipping Windows build.
/// </summary>
internal static class AppIdentity
{
    /// <summary>True when running inside an MSIX package (Store or sideload).</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    private const int AppModelErrorNoPackage = 15700;

    private static bool DetectPackaged()
    {
        try
        {
            var length = 0;
            var rc = GetCurrentPackageFullName(ref length, null);
            return rc != AppModelErrorNoPackage;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);
}
