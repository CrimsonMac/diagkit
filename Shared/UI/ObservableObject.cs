using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DiagKit.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Command whose CanExecuteChanged is raised on the thread that created it (the UI thread).</summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute(parameter);
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (_context != null && SynchronizationContext.Current != _context)
        {
            _context.Post(_ => CanExecuteChanged?.Invoke(this, EventArgs.Empty), null);
        }
        else
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Async command that disables itself while running and reports failures instead of crashing.</summary>
public sealed class AsyncCommand : ICommand
{
    /// <summary>Where failures of commands without their own error handler go. Each program sets it at startup.</summary>
    public static Action<Exception> UnhandledError { get; set; } = ex =>
        System.Windows.MessageBox.Show(ex.Message, "DiagKit", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private bool _running;

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute(), onError)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            if (_onError != null)
            {
                _onError(ex);
            }
            else
            {
                UnhandledError(ex);
            }
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (_context != null && SynchronizationContext.Current != _context)
        {
            _context.Post(_ => CanExecuteChanged?.Invoke(this, EventArgs.Empty), null);
        }
        else
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
