using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Publishing;
using GameLauncher.Infrastructure.Publishing;
using GameLauncher.Infrastructure.Tests.Progressing;

namespace GameLauncher.Infrastructure.Tests.Publishing;

public sealed class DirectoryBuildPackagerTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FixedCapabilities _capabilities = new();

    private readonly DirectoryBuildPackager _packager;

    public DirectoryBuildPackagerTests() => _packager = new DirectoryBuildPackager(_capabilities);

    public void Dispose() => _directory.Dispose();

    private string Write(string relativePath, string content)
    {
        string full = Path.Combine(
            _directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static string Sha(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task EveryFileIsHashedAndNamedRelativeToTheBuildRoot()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "an asset");
        Write("data/nested/more.dat", "another asset");

        BuildPackage package = await _packager.PackageAsync(
            _directory.Path, "Game.exe", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Game.exe", "data/nested/more.dat", "data/pak"],
            package.Files.Select(file => file.Path).Order(StringComparer.Ordinal));

        PackagedFile executable = package.Files.Single(file => file.Path == "Game.exe");
        Assert.Equal(Sha("the executable"), executable.Sha256);
        Assert.Equal("the executable".Length, executable.Size);
        Assert.Equal("Game.exe", package.Entrypoint);
    }

    // Blobs are the unit of the transfer: two files with identical content are one upload, and
    // declaring them twice would overstate what publishing is about to cost.
    [Fact]
    public async Task IdenticalFilesAreOneBlobAndTwoEntries()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "shared content");
        Write("assets/pak", "shared content");

        BuildPackage package = await _packager.PackageAsync(
            _directory.Path, "Game.exe", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, package.Files.Count);
        Assert.Equal(2, package.DistinctBlobs.Count);
        Assert.Equal(
            "the executable".Length + ("shared content".Length * 2), package.TotalBytes);
    }

    [Fact]
    public async Task TheManifestIsSortedByPathAndCarriesNoSizes()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "an asset");

        BuildPackage package = await _packager.PackageAsync(
            _directory.Path, "Game.exe", cancellationToken: TestContext.Current.CancellationToken);

        ManifestSubmission manifest = package.ToManifest("--fullscreen");

        Assert.Equal(
            ["Game.exe", "data/pak"], manifest.Files.Select(file => file.Path));
        Assert.Equal("Game.exe", manifest.Entrypoint);
        Assert.Equal("--fullscreen", manifest.LaunchArgs);
    }

    [Fact]
    public async Task AnEmptyDirectoryIsNotABuild()
    {
        PublishingException exception = await Assert.ThrowsAsync<PublishingException>(() =>
            _packager.PackageAsync(
                _directory.Path, "Game.exe",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PublishFailure.NothingToPublish, exception.Reason);
    }

    // The server refuses a manifest whose entrypoint it cannot find, and learning that after
    // the upload would be an expensive way to learn it.
    [Fact]
    public async Task AnEntrypointThatIsNotBeingPublishedIsRefusedBeforeAnythingTravels()
    {
        Write("data/pak", "an asset");

        PublishingException exception = await Assert.ThrowsAsync<PublishingException>(() =>
            _packager.PackageAsync(
                _directory.Path, "Game.exe",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PublishFailure.EntrypointMissing, exception.Reason);
    }

    [Fact]
    public async Task TheEntrypointIsAcceptedInEitherSeparator()
    {
        Write("bin/Game.exe", "the executable");

        BuildPackage package = await _packager.PackageAsync(
            _directory.Path,
            "bin\\Game.exe",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("bin/Game.exe", package.Entrypoint);
    }

    [Fact]
    public async Task ProgressCountsUpToEveryFile()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "an asset");

        List<PackagingProgress> reports = [];
        await _packager.PackageAsync(
            _directory.Path,
            "Game.exe",
            new ImmediateProgress<PackagingProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, reports.Count);
        Assert.Equal(2, reports[^1].FilesHashed);
        Assert.Equal(2, reports[^1].TotalFiles);
        Assert.Equal("the executable".Length + "an asset".Length, reports[^1].BytesHashed);
    }
}
