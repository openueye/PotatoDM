namespace PotatoDM.AppCore.Services;

/// <summary>
/// Lets the manager marshal job updates onto the UI thread without depending
/// on WPF. Tests pass an inline implementation.
/// </summary>
public interface IUiDispatcher
{
    bool IsCurrent { get; }

    void Post(Action action);

    /// <summary>Runs work on the UI thread and completes after it has finished.</summary>
    Task InvokeAsync(Func<Task> action);

    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}
