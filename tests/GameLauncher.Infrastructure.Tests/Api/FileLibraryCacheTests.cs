using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Api;

/// <summary>
/// The library as last seen. Everything here is about a cache that must never be able to
/// break the page it exists to fill: a miss, a corrupt file and a directory that cannot be
/// written are all ordinary answers.
/// </summary>
public sealed class FileLibraryCacheTests
{
    private const string Account = "7e679742-05d0-497f-baeb-be590790a5e0";

    private static FileLibraryCache CacheIn(TemporaryDirectory directory) =>
        new(directory.Path, NullLogger<FileLibraryCache>.Instance);

    private static Game GameNamed(string id, string title) =>
        new() { Id = id, Slug = id, Title = title, CoverUrl = "https://media.example/" + id };

    [Fact]
    public async Task WhatWasStoredIsWhatComesBack()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);

        await cache.WriteAsync(
            Account,
            [GameNamed("g1", "Makhia"), GameNamed("g2", "Orbit")],
            TestContext.Current.CancellationToken);

        IReadOnlyList<Game> remembered = await cache.ReadAsync(
            Account, TestContext.Current.CancellationToken);

        Assert.Equal(2, remembered.Count);
        Assert.Equal("Makhia", remembered[0].Title);
        Assert.Equal("https://media.example/g2", remembered[1].CoverUrl);
    }

    /// <summary>Two people share a machine and neither is owed the other's list.</summary>
    [Fact]
    public async Task OneAccountNeverReadsAnother()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);

        await cache.WriteAsync(
            Account, [GameNamed("g1", "Makhia")], TestContext.Current.CancellationToken);

        Assert.Empty(await cache.ReadAsync(
            "somebody-else", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NothingStoredIsAnEmptyListRatherThanAFailure()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);

        Assert.Empty(await cache.ReadAsync(Account, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A file this launcher can no longer parse is a cache miss. The page then falls back to
    /// the install rows, which is worse than the cached list and much better than an error.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeReadIsAMiss()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);
        await cache.WriteAsync(
            Account, [GameNamed("g1", "Makhia")], TestContext.Current.CancellationToken);

        foreach (string path in Directory.GetFiles(directory.Path, "*.json"))
        {
            await File.WriteAllTextAsync(
                path, "{ not json", TestContext.Current.CancellationToken);
        }

        Assert.Empty(await cache.ReadAsync(Account, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheStoredListIsReplacedRatherThanAddedTo()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);

        await cache.WriteAsync(
            Account,
            [GameNamed("g1", "Makhia"), GameNamed("g2", "Orbit")],
            TestContext.Current.CancellationToken);

        await cache.WriteAsync(
            Account, [GameNamed("g2", "Orbit")], TestContext.Current.CancellationToken);

        Game single = Assert.Single(
            await cache.ReadAsync(Account, TestContext.Current.CancellationToken));
        Assert.Equal("g2", single.Id);
    }

    [Fact]
    public async Task ClearingForgetsTheList()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);
        await cache.WriteAsync(
            Account, [GameNamed("g1", "Makhia")], TestContext.Current.CancellationToken);

        await cache.ClearAsync(Account, TestContext.Current.CancellationToken);

        Assert.Empty(await cache.ReadAsync(Account, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The signed-out launcher. There is no list to keep and nowhere to keep it, and asking
    /// must not create a file named after nobody.
    /// </summary>
    [Fact]
    public async Task NoAccountMeansNoFile()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);

        await cache.WriteAsync(
            string.Empty, [GameNamed("g1", "Makhia")], TestContext.Current.CancellationToken);

        Assert.Empty(await cache.ReadAsync(
            string.Empty, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.Exists(directory.Path)
            ? Directory.GetFiles(directory.Path)
            : []);
    }

    /// <summary>The id itself never appears in a directory listing.</summary>
    [Fact]
    public async Task TheFileIsNotNamedAfterTheAccount()
    {
        using TemporaryDirectory directory = new();
        using FileLibraryCache cache = CacheIn(directory);

        await cache.WriteAsync(
            Account, [GameNamed("g1", "Makhia")], TestContext.Current.CancellationToken);

        string name = Path.GetFileName(Assert.Single(Directory.GetFiles(directory.Path)));
        Assert.DoesNotContain(Account, name, StringComparison.Ordinal);
    }
}
