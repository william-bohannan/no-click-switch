using System.Windows.Media;

namespace NoClickSwitch;

/// <summary>
/// Windows 11 Segoe Fluent Icons (fallback: Segoe MDL2 Assets on Windows 10).
/// Glyph codes: https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font
/// Preferred sizes: 16, 20, 24, 32, 40, 48, 64.
/// </summary>
internal static class FluentIcons
{
    /// <summary>
    /// Font stack: Fluent (Win11) then MDL2 (Win10). Common glyphs share the same PUA points.
    /// </summary>
    public static FontFamily FontFamily { get; } =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string GlobalNavButton = "\uE700";
    public const string Settings = "\uE713";
    public const string GoToStart = "\uE8FC";
    public const string Folder = "\uE8B7";
    public const string CommandPrompt = "\uE756";
    public const string ChevronDown = "\uE70D";
    public const string ChevronUp = "\uE70E";
    public const string Download = "\uE896";
    public const string Delete = "\uE74D";
    public const string ChromeClose = "\uE8BB";
    public const string OpenInNewWindow = "\uE8A7";
    public const string Globe = "\uE774";
    public const string Info = "\uE946";
    public const string FullScreen = "\uE740";
    public const string Color = "\uE790";
    public const string Brightness = "\uE706";
    public const string Stopwatch = "\uE916";
    public const string Diagnostic = "\uE9D9";
    public const string FontSize = "\uE8E9";
    public const string QuietHours = "\uE708";
    public const string Filter = "\uE71C";
}
