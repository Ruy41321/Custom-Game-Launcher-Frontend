using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Base for every view model. Change notification comes from the CommunityToolkit source
/// generators — <c>[ObservableProperty]</c> and <c>[RelayCommand]</c> — so no view model
/// implements <c>INotifyPropertyChanged</c> by hand.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Captured where the view model is built, which in the running application is the UI
    /// thread. Avalonia installs a context there; a test has none, and that absence is what
    /// makes <see cref="OnUiThread"/> run inline instead of on the thread pool.
    /// </summary>
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    /// <summary>
    /// Raises a change where the bindings can hear it. Needed for anything a background thread
    /// reports — a process exiting, a transfer finishing — because a binding updated off the
    /// UI thread is a crash that only happens on a user's machine.
    /// </summary>
    protected void OnUiThread(Action action)
    {
        if (_uiContext is null || _uiContext == SynchronizationContext.Current)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }
}
