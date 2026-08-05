using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using GameLauncher.Core.Media;

namespace GameLauncher.App.Services;

/// <summary>A file the user chose, with its bytes already read.</summary>
public sealed record PickedFile(string Name, byte[] Content);

/// <summary>
/// Asks the user for one file and hands back its contents. Behind an interface for the same
/// reason <see cref="IFolderPicker"/> is (D32): a file dialog is the one step of the flow that
/// cannot be driven from a test, so it is the only thing behind one — everything after it,
/// including every rule about what may be uploaded, is exercised.
///
/// It returns **bytes and not a path** on purpose. A view model that received a path would have
/// to read the file, which is I/O in a view model and a second untestable step; here the dialog
/// and the read are the same operation and the same substitution replaces both.
/// </summary>
public interface IFilePicker
{
    /// <summary>Null when the user cancelled, which is not an error.</summary>
    Task<PickedFile?> PickAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The real dialog, over Avalonia's storage provider. Resolves the window at call time rather
/// than holding it, as the folder picker does and for the same reason.
/// </summary>
public sealed class StorageProviderFilePicker(Application application) : IFilePicker
{
    /// <summary>
    /// A dishonest or hostile file must not be read into memory without a bound. This is well
    /// above any deployment's media limit, and the real refusal happens afterwards against
    /// <see cref="MediaUploadRules"/> and the server's announced cap — this only stops the read
    /// itself from being unbounded.
    /// </summary>
    public const long MaxReadBytes = 64 * 1024 * 1024;

    public async Task<PickedFile?> PickAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken = default)
    {
        if (application.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { StorageProvider: { } storage })
        {
            return null;
        }

        IReadOnlyList<IStorageFile> chosen = await storage
            .OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                // A convenience for the person choosing, and nothing more: what is actually
                // accepted is decided by the bytes, here and again on the server.
                FileTypeFilter =
                [
                    new FilePickerFileType(title)
                    {
                        Patterns = [.. extensions.Select(extension => "*." + extension)],
                    },
                ],
            })
            .ConfigureAwait(true);

        if (chosen.Count == 0)
        {
            return null;
        }

        IStorageFile file = chosen[0];

        try
        {
            await using Stream contents = await file.OpenReadAsync().ConfigureAwait(true);
            using MemoryStream buffer = new();

            await CopyCappedAsync(contents, buffer, cancellationToken).ConfigureAwait(true);
            return new PickedFile(file.Name, buffer.ToArray());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is the same to the caller as no file chosen: there is
            // nothing to upload either way, and the picker is not the place to explain a disk.
            return null;
        }
    }

    private static async Task CopyCappedAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > MaxReadBytes)
            {
                throw new IOException("The chosen file is too large to read.");
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
