namespace GameLauncher.App.Tests;

/// <summary>
/// A directory under the system temp path that deletes itself at end of scope. The same helper
/// Infrastructure.Tests has: a test project cannot reference another test project, and putting
/// it in shipped code to share it would be worse.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "launcher-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
