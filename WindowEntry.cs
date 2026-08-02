using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace NoClickSwitch;

/// <summary>One open top-level window shown as a single tab.</summary>
public sealed class WindowEntry : INotifyPropertyChanged
{
    private bool _isActive;
    private string _title = "";
    private BitmapSource? _icon;

    public required IntPtr Handle { get; init; }

    public required string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;
            _title = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True when this tab's window is the foreground (or last app) window.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override bool Equals(object? obj)
        => obj is WindowEntry other && Handle == other.Handle;

    public override int GetHashCode() => Handle.GetHashCode();
}
