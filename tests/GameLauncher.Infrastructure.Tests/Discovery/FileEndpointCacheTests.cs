using GameLauncher.Infrastructure.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Discovery;

public sealed class FileEndpointCacheTests
{
    private static FileEndpointCache Cache(TemporaryDirectory directory) =>
        new(directory.Path, NullLogger<FileEndpointCache>.Instance);

    [Fact]
    public async Task StoresAndReadsBackTheEnvelopeUnchanged()
    {
        using TemporaryDirectory directory = new();
        using FileEndpointCache cache = Cache(directory);

        const string envelope = """{"payload":"eyJhIjoxfQ==","signature":"MEUCIQ=="}""";
        await cache.WriteAsync(
            "game-launcher-api", "production", envelope, TestContext.Current.CancellationToken);

        string? stored = await cache.ReadAsync(
            "game-launcher-api", "production", TestContext.Current.CancellationToken);

        // Byte for byte: what is stored is what the signature covers.
        Assert.Equal(envelope, stored);
    }

    [Fact]
    public async Task ReadsNothingWhenNothingWasStored()
    {
        using TemporaryDirectory directory = new();
        using FileEndpointCache cache = Cache(directory);

        Assert.Null(await cache.ReadAsync(
            "game-launcher-api", "production", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KeepsEnvironmentsApart()
    {
        using TemporaryDirectory directory = new();
        using FileEndpointCache cache = Cache(directory);

        await cache.WriteAsync(
            "game-launcher-api", "production", "production-envelope",
            TestContext.Current.CancellationToken);
        await cache.WriteAsync(
            "game-launcher-api", "staging", "staging-envelope",
            TestContext.Current.CancellationToken);

        Assert.Equal("production-envelope", await cache.ReadAsync(
            "game-launcher-api", "production", TestContext.Current.CancellationToken));
        Assert.Equal("staging-envelope", await cache.ReadAsync(
            "game-launcher-api", "staging", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplacesWhatWasThere()
    {
        using TemporaryDirectory directory = new();
        using FileEndpointCache cache = Cache(directory);

        await cache.WriteAsync(
            "game-launcher-api", "production", "first", TestContext.Current.CancellationToken);
        await cache.WriteAsync(
            "game-launcher-api", "production", "second", TestContext.Current.CancellationToken);

        Assert.Equal("second", await cache.ReadAsync(
            "game-launcher-api", "production", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The key and the environment come from a configuration file, so they are input, and a
    /// path built from input is a path worth checking.
    /// </summary>
    [Theory]
    [InlineData("../escape", "production")]
    [InlineData("game-launcher-api", "../escape")]
    [InlineData("", "production")]
    [InlineData("game-launcher-api", "")]
    public async Task WritesNothingForANameThatCouldEscapeTheDirectory(string key, string environment)
    {
        using TemporaryDirectory directory = new();
        using FileEndpointCache cache = Cache(directory);

        await cache.WriteAsync(key, environment, "envelope", TestContext.Current.CancellationToken);

        Assert.Null(await cache.ReadAsync(key, environment, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CreatesItsDirectoryOnDemand()
    {
        using TemporaryDirectory directory = new();
        string nested = Path.Combine(directory.Path, "does-not-exist-yet");
        using FileEndpointCache cache = new(nested, NullLogger<FileEndpointCache>.Instance);

        await cache.WriteAsync(
            "game-launcher-api", "production", "envelope", TestContext.Current.CancellationToken);

        Assert.Equal("envelope", await cache.ReadAsync(
            "game-launcher-api", "production", TestContext.Current.CancellationToken));
    }
}
