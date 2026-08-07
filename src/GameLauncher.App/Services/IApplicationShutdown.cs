using Avalonia.Controls.ApplicationLifetimes;

namespace GameLauncher.App.Services;

/// <summary>
/// Closing the application, behind an interface for one reason: a view model that called
/// <see cref="IClassicDesktopStyleApplicationLifetime.Shutdown"/> directly would be a view model
/// that ends the test host when a test presses its button. The same budget D32 spends on the
/// file picker, spent on the one other thing a test must not really do.
/// </summary>
public interface IApplicationShutdown
{
    void Shutdown();
}

/// <summary>
/// The real one. It resolves the lifetime at call time rather than holding it, like the pickers
/// beside it: the shell is built before the window exists.
/// </summary>
public sealed class ApplicationLifetimeShutdown(Avalonia.Application application)
    : IApplicationShutdown
{
    public void Shutdown()
    {
        if (application.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
