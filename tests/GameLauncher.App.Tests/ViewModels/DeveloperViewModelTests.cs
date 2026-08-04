using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Publishing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class DeveloperViewModelTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IPublishingApi _publishing = Substitute.For<IPublishingApi>();
    private readonly IBuildPublisher _publisher = Substitute.For<IBuildPublisher>();
    private readonly IFolderPicker _folders = Substitute.For<IFolderPicker>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    private DeveloperViewModel CreateViewModel()
    {
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(GamePlatform.Windows);
        runtime.Architecture.Returns(BuildArchitecture.X64);

        return new DeveloperViewModel(
            _catalog,
            _publishing,
            _publisher,
            new ApiErrorPresenter(_localization),
            _localization,
            runtime,
            _folders);
    }

    private void OwnsGames(params Game[] games) =>
        _catalog.GetMyGamesAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game> { Items = games, Total = games.Length });

    private static Game GameNamed(
        string title, GameVisibility visibility = GameVisibility.Draft) => new()
        {
            Id = title,
            Slug = title.ToLowerInvariant(),
            Title = title,
            Visibility = visibility,
        };

    private void Detail(GameDetail detail) =>
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(detail);

    [Fact]
    public async Task ThePageListsThePublishersOwnGamesIncludingDrafts()
    {
        OwnsGames(GameNamed("Orbital Drift"), GameNamed("Deep Cut", GameVisibility.Public));

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Games.Count);
        Assert.Contains(model.Games, game => game.Visibility == GameVisibility.Draft);
    }

    [Fact]
    public async Task CreatingAGameAddsItToTheListAndSelectsIt()
    {
        OwnsGames();
        _publishing.CreateGameAsync(Arg.Any<CreateGameRequest>(), Arg.Any<CancellationToken>())
            .Returns(GameNamed("Orbital Drift"));
        Detail(new GameDetail { Game = GameNamed("Orbital Drift") });

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.CanCreateGame);
        model.NewGameTitle = "Orbital Drift";
        Assert.True(model.CanCreateGame);

        await model.CreateGameCommand.ExecuteAsync(null);

        Assert.Single(model.Games);
        Assert.Equal("Orbital Drift", model.SelectedGame?.Title);
        Assert.Empty(model.NewGameTitle);
        Assert.Contains("orbital drift", model.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingAGameShowsItsVersionsAndBuilds()
    {
        OwnsGames(GameNamed("Orbital Drift"));
        Detail(new GameDetail
        {
            Game = GameNamed("Orbital Drift"),
            Versions = [new GameVersion { Id = "v1", Semver = "0.1.0" }],
            Builds = [new GameBuild { Id = "b1", VersionId = "v1", Status = BuildStatus.Ready }],
        });

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.SelectedGame = model.Games[0];

        // The selection kicks off a load that is not awaited by the setter.
        await Task.Yield();

        Assert.Single(model.Versions);
        Assert.Single(model.Builds);
        Assert.Equal("v1", model.SelectedVersion?.Id);
    }

    // A build belongs to a version, so there is nothing to publish to until one is chosen.
    [Fact]
    public void PublishingIsNotOfferedWithoutAVersionADirectoryAndAnExecutable()
    {
        DeveloperViewModel model = CreateViewModel();

        Assert.False(model.CanPublish);

        model.BuildDirectory = "/builds/orbital-drift";
        model.Entrypoint = "Game.exe";
        Assert.False(model.CanPublish);

        model.SelectedVersion = new GameVersion { Id = "v1", Semver = "0.1.0" };
        Assert.True(model.CanPublish);
    }

    // A build almost always has one obvious executable, and typing its name again is a chance
    // to get it wrong in a way that only shows up after the upload.
    [Fact]
    public async Task ChoosingADirectoryWithOneExecutableFillsInTheEntrypoint()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "Game.exe"),
            "the executable",
            TestContext.Current.CancellationToken);

        _folders.PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(directory.Path);

        DeveloperViewModel model = CreateViewModel();
        await model.ChooseDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(directory.Path, model.BuildDirectory);
        Assert.Equal("Game.exe", model.Entrypoint);
    }

    [Fact]
    public async Task TwoExecutablesLeaveTheChoiceToThePublisher()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "Game.exe"), "a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "Editor.exe"), "b", TestContext.Current.CancellationToken);

        _folders.PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(directory.Path);

        DeveloperViewModel model = CreateViewModel();
        await model.ChooseDirectoryCommand.ExecuteAsync(null);

        Assert.Empty(model.Entrypoint);
    }

    [Fact]
    public async Task CancellingTheDialogChangesNothing()
    {
        _folders.PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        DeveloperViewModel model = CreateViewModel();
        model.BuildDirectory = "/kept";
        await model.ChooseDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("/kept", model.BuildDirectory);
    }

    [Fact]
    public async Task PublishingSendsWhatTheFormSaysAndReportsWhatItCost()
    {
        OwnsGames(GameNamed("Orbital Drift"));
        Detail(new GameDetail
        {
            Game = GameNamed("Orbital Drift"),
            Versions = [new GameVersion { Id = "v1", Semver = "0.1.0" }],
        });

        PublishRequest? asked = null;
        _publisher
            .PublishAsync(
                Arg.Do<PublishRequest>(request => asked = request),
                Arg.Any<IProgress<PublishProgress>>(),
                Arg.Any<CancellationToken>())
            .Returns(new PublishResult
            {
                Build = new GameBuild { Id = "b1", Status = BuildStatus.Ready },
                UploadedBytes = 1_048_576,
                BlobsUploaded = 1,
                BlobsAlreadyPresent = 3,
            });

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.SelectedGame = model.Games[0];
        await Task.Yield();

        model.BuildDirectory = "/builds/orbital-drift";
        model.Entrypoint = "Game.exe";
        model.LaunchArgs = "--fullscreen";
        model.BuildPlatform = GamePlatform.Linux;

        await model.PublishCommand.ExecuteAsync(null);

        Assert.Equal("v1", asked?.VersionId);
        Assert.Equal("Game.exe", asked?.Entrypoint);
        Assert.Equal("--fullscreen", asked?.LaunchArgs);
        Assert.Equal(GamePlatform.Linux, asked?.Platform);

        Assert.Single(model.Builds);
        Assert.Null(model.Progress);
        Assert.Contains("1 MB", model.StatusMessage!, StringComparison.Ordinal);
        Assert.Contains("3", model.StatusMessage!, StringComparison.Ordinal);
    }

    // A packaging failure is local and specific, and saying which rule was broken is the
    // difference between fixing it and guessing.
    [Fact]
    public async Task APackagingFailureSaysWhichRuleWasBroken()
    {
        OwnsGames(GameNamed("Orbital Drift"));
        Detail(new GameDetail
        {
            Game = GameNamed("Orbital Drift"),
            Versions = [new GameVersion { Id = "v1", Semver = "0.1.0" }],
        });

        _publisher
            .PublishAsync(
                Arg.Any<PublishRequest>(),
                Arg.Any<IProgress<PublishProgress>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new PublishingException(
                PublishFailure.EntrypointMissing, "Game.exe is not one of the files."));

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.SelectedGame = model.Games[0];
        await Task.Yield();

        model.BuildDirectory = "/builds";
        model.Entrypoint = "Game.exe";
        await model.PublishCommand.ExecuteAsync(null);

        Assert.Contains(
            "The executable is not one of the files being published.",
            model.ErrorMessage!,
            StringComparison.Ordinal);
        Assert.Null(model.Progress);
    }

    [Fact]
    public async Task ARefusedCreationIsReportedThroughTheUsualPresenter()
    {
        OwnsGames();
        _publishing.CreateGameAsync(Arg.Any<CreateGameRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Conflict, "slug taken"));

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.NewGameTitle = "Orbital Drift";

        await model.CreateGameCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.Conflict"), model.ErrorMessage);
        Assert.Empty(model.Games);
    }

    [Fact]
    public void ThePlatformDefaultsToTheMachineDoingThePublishing()
    {
        DeveloperViewModel model = CreateViewModel();

        Assert.Equal(GamePlatform.Windows, model.BuildPlatform);
        Assert.Equal(BuildArchitecture.X64, model.Architecture);
    }
}
