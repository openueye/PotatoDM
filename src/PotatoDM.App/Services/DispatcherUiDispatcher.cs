using System.Windows.Threading;
using PotatoDM.AppCore.Services;

namespace PotatoDM.App.Services;

public sealed class DispatcherUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public DispatcherUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.InvokeAsync(action);
        }
    }

    public Task InvokeAsync(Func<Task> action)
    {
        return _dispatcher.CheckAccess()
            ? action()
            : _dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        return _dispatcher.CheckAccess()
            ? action()
            : _dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
