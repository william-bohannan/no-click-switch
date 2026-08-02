using System.Windows.Media.Imaging;

namespace SwiztchBar;

/// <summary>One open top-level window shown as a single tab.</summary>
public sealed class WindowEntry
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public BitmapSource? Icon { get; init; }

    public override bool Equals(object? obj)
        => obj is WindowEntry other && Handle == other.Handle;

    public override int GetHashCode() => Handle.GetHashCode();
}
