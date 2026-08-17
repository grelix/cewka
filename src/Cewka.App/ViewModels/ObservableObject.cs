using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cewka.App.ViewModels;

/// <summary>
/// Minimal change notification. A full MVVM toolkit would be more machinery than this
/// application needs, and the audio engine in later stages raises its own events anyway.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(propertyName);
        return true;
    }
}
