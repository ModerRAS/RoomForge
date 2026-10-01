namespace AudioOptimizer.UI.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioOptimizer.UI.Localization;

/// <summary>
/// The smallest useful implementation of <see cref="INotifyPropertyChanged"/>. Deliberately hand-written: a
/// view-model framework is a dependency and a convention for six properties, and WPF's binding only needs the
/// event.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    protected ObservableObject()
    {
        UiText.Changed += (_, _) =>
        {
            OnCultureChanged();
            Raise(null);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Rewrite cached sentences after the language changes. Computed text refreshes from <see cref="Raise"/>.</summary>
    protected virtual void OnCultureChanged()
    {
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
