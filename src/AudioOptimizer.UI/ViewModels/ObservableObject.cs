namespace AudioOptimizer.UI.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// The smallest useful implementation of <see cref="INotifyPropertyChanged"/>. Deliberately hand-written: a
/// view-model framework is a dependency and a convention for six properties, and WPF's binding only needs the
/// event.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
