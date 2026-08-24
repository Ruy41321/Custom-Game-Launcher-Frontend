using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace GameLauncher.App.Services;

/// <summary>
/// Asks the user for a directory. Behind an interface so the publish view model can be
/// exercised without a window — a file dialog is the one thing in the flow that cannot be
/// driven from a test.
/// </summary>
public interface IFolderPicker
{
    /// <summary>Null when the user cancelled, which is not an error.</summary>
    Task<string?> PickAsync(string title, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real dialog, over Avalonia's storage provider. It resolves the window at call time
/// rather than holding it: the shell is built before the window exists, and a view model that
/// captured a null top level would fail the first time somebody clicked.
/// </summary>
public sealed class StorageProviderFolderPicker(Application application) : IFolderPicker
{
    public async Task<string?> PickAsync(
        string title, CancellationToken cancellationToken = default)
    {
        if (application.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { StorageProvider: { } storage })
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> chosen = await storage
            .OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            })
            .ConfigureAwait(true);

        return chosen.Count == 0 ? null : chosen[0].Path.LocalPath;
    }
}
