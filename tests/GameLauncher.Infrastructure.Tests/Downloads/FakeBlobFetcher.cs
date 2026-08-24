using System.Text;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Models;

namespace GameLauncher.Infrastructure.Tests.Downloads;

/// <summary>
/// Writes whatever the test says the file server holds. It deliberately does not check the
/// hash — that is <see cref="GameLauncher.Infrastructure.Downloads.BlobFetcher"/>'s job and has
/// its own tests — which is what lets a test hand the installer bytes that do not match the
/// plan and see what it does with them.
/// </summary>
internal sealed class FakeBlobFetcher : IBlobFetcher
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Fetched { get; } = [];

    public void Holds(string sha256, string content) =>
        _blobs[sha256] = Encoding.UTF8.GetBytes(content);

    public async Task FetchAsync(
        PlannedFile file,
        string destinationPath,
        IProgress<long>? transferred = null,
        CancellationToken cancellationToken = default)
    {
        lock (Fetched)
        {
            Fetched.Add(file.Sha256);
        }

        byte[] content = _blobs.TryGetValue(file.Sha256, out byte[]? bytes)
            ? bytes
            : throw new InvalidOperationException(
                $"The test did not say what blob {file.Sha256} contains.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, content, cancellationToken)
            .ConfigureAwait(false);

        transferred?.Report(file.Size);
    }
}
