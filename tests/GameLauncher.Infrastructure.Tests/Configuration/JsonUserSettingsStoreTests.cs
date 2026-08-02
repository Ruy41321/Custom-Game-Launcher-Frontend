using GameLauncher.Core.Configuration;
using GameLauncher.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Configuration;

public sealed class JsonUserSettingsStoreTests
{
    private static JsonUserSettingsStore StoreFor(string path) =>
        new(path, NullLogger<JsonUserSettingsStore>.Instance);

    [Fact]
    public async Task AMissingFileYieldsTheDefaults()
    {
        using var directory = new TemporaryDirectory();
        using JsonUserSettingsStore store = StoreFor(directory.File("settings.json"));

        UserSettings settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(settings.Language);
        Assert.Null(settings.ThemeVariant);
        Assert.False(settings.SendCrashReports);
    }

    [Fact]
    public async Task SettingsSurviveARoundTrip()
    {
        using var directory = new TemporaryDirectory();
        using JsonUserSettingsStore store = StoreFor(directory.File("settings.json"));

        var written = new UserSettings
        {
            Language = "it",
            ThemeVariant = "light",
            InstallDirectory = "D:/Games",
            SendCrashReports = true,
            LaunchMinimized = true,
        };

        await store.SaveAsync(written, TestContext.Current.CancellationToken);
        UserSettings read = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(written, read);
    }

    [Fact]
    public async Task SavingCreatesTheDirectoryWhenItDoesNotExist()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "nested", "deeper", "settings.json");
        using JsonUserSettingsStore store = StoreFor(path);

        await store.SaveAsync(new UserSettings { Language = "fr" }, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
    }

    // Crash-safety: the write goes through a temp file that is then moved into place.
    [Fact]
    public async Task SavingLeavesNoTemporaryFileBehind()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        using JsonUserSettingsStore store = StoreFor(path);

        await store.SaveAsync(new UserSettings { Language = "en" }, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SavingTwiceOverwritesRatherThanFailing()
    {
        using var directory = new TemporaryDirectory();
        using JsonUserSettingsStore store = StoreFor(directory.File("settings.json"));

        await store.SaveAsync(new UserSettings { Language = "en" }, TestContext.Current.CancellationToken);
        await store.SaveAsync(new UserSettings { Language = "fr" }, TestContext.Current.CancellationToken);

        UserSettings read = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("fr", read.Language);
    }

    // Losing preferences is annoying; refusing to start because of them is unacceptable.
    [Fact]
    public async Task ACorruptFileFallsBackToTheDefaultsInsteadOfThrowing()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile("settings.json", "{ this is not json");
        using JsonUserSettingsStore store = StoreFor(path);

        UserSettings settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(settings.Language);
    }

    [Fact]
    public async Task ConcurrentSavesDoNotCorruptTheFile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        using JsonUserSettingsStore store = StoreFor(path);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            store.SaveAsync(
                new UserSettings { Language = index % 2 == 0 ? "en" : "it" },
                TestContext.Current.CancellationToken)));

        UserSettings read = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(read.Language is "en" or "it", $"unexpected language: {read.Language}");
    }
}
