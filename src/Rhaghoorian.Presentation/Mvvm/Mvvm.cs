using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Rhaghoorian.Presentation.Services;

namespace Rhaghoorian.Presentation.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Comando assíncrono: impede execução reentrante (duplo clique) e entrega qualquer exceção ao tratador central,
/// de modo que nenhuma falha de I/O derrube o aplicativo nem exponha detalhes técnicos.
/// </summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly IErrorHandler? _errors;
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    private AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute, IErrorHandler? errors)
    {
        _execute = execute; _canExecute = canExecute; _errors = errors;
    }

    public static AsyncCommand Create(Func<Task> execute, IErrorHandler? errors = null, Func<bool>? canExecute = null) =>
        new(_ => execute(), canExecute is null ? null : _ => canExecute(), errors);

    public static AsyncCommand Create<T>(Func<T, Task> execute, IErrorHandler? errors = null, Func<T, bool>? canExecute = null) =>
        new(p => execute((T)p!), canExecute is null ? null : p => p is T t && canExecute(t), errors);

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _execute(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (_errors is not null) { await _errors.HandleAsync(ex); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
