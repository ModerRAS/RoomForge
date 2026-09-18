namespace AudioOptimizer.UI.ViewModels;

using System.Windows.Input;

/// <summary>
/// The smallest thing that satisfies <see cref="ICommand"/> for a WPF binding. Hand-written on purpose: a command
/// framework is a dependency and a convention for six buttons, and this shape is what M4's optimizer panel reuses
/// rather than inventing a second mechanism. <see cref="CanExecuteChanged"/> is raised explicitly by the owner
/// when its state changes, because nothing here knows what the owner's state is.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
