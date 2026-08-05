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

    private readonly IFilePicker _files = Substitute.For<IFilePicker>();
    private readonly IServerCapabilityProvider _capabilities =
        Substitute.For<IServerCapabilityProvider>();

    public DeveloperViewModelTests()
    {
        // Arranged in the constructor rather than in the factory: NSubstitute's last stub wins,
        // so a test arranging one of these before building the model would get the factory's
        // answer instead, and the failure would read as the view model ignoring the server.
        _capabilities.GetAsync(Arg.Any<CancellationToken>())
            .Returns(ServerCapabilities.Fallback);

        _catalog.GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<PatchNote>());
    }

    private DeveloperViewModel CreateViewModel()
    {
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(GamePlatform.Windows);
        runtime.Architecture.Returns(BuildArchitecture.X64);

        var errors = new ApiErrorPresenter(_localization);

        return new DeveloperViewModel(
            _catalog,
            _publishing,
            _publisher,
            errors,
            _localization,
            runtime,
            _folders,
            new GameEditorViewModel(_publishing, errors, _localization),
            new GameMediaViewModel(
                _catalog, _publishing, _capabilities, errors, _localization, _files),
            new GameDevlogViewModel(_catalog, _publishing, errors, _localization));
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

    // --- deleting a build or a version -------------------------------------------------------

    private static GameVersion Version(string id, string semver) =>
        new() { Id = id, Semver = semver };

    private static GameBuild Build(
        string id, string versionId, GamePlatform platform = GamePlatform.Windows) =>
        new()
        {
            Id = id,
            VersionId = versionId,
            Platform = platform,
            Architecture = BuildArchitecture.X64,
            Status = BuildStatus.Ready,
        };

    private async Task<DeveloperViewModel> WithSelectedGameAsync(
        IReadOnlyList<GameVersion> versions, IReadOnlyList<GameBuild> builds)
    {
        Game game = GameNamed("Orbital Drift");
        OwnsGames(game);
        Detail(new GameDetail { Game = game, Versions = versions, Builds = builds });

        DeveloperViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.SelectedGame = game;

        // OnSelectedGameChanged starts the load without awaiting it, and there is no
        // SynchronizationContext here, so the continuation has to be given a turn.
        await Task.Yield();
        return model;
    }

    // Nothing may be sent on the first click: that is the whole point of arming a deletion.
    [Fact]
    public async Task AskingToDeleteABuildSendsNothingUntilItIsConfirmed()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")], [Build("b1", "v1")]);

        model.AskToDeleteBuildCommand.Execute(model.Builds[0]);

        Assert.True(model.HasPendingDeletion);
        await _publishing.DidNotReceive()
            .DeleteBuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The prompt has to name which build, or four rows of "Ready" are indistinguishable.
    [Fact]
    public async Task ThePromptForABuildNamesTheVersionThePlatformAndTheArchitecture()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")], [Build("b1", "v1")]);

        model.AskToDeleteBuildCommand.Execute(model.Builds[0]);

        string prompt = model.PendingDeletion!.Prompt;
        Assert.Contains("0.3.0", prompt, StringComparison.Ordinal);
        Assert.Contains("Windows", prompt, StringComparison.Ordinal);
        Assert.Contains("X64", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmingRemovesTheBuildFromTheListAndSaysWhatItGaveBack()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")], [Build("b1", "v1")]);

        model.AskToDeleteBuildCommand.Execute(model.Builds[0]);
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        await _publishing.Received(1).DeleteBuildAsync("b1", Arg.Any<CancellationToken>());
        Assert.Empty(model.Builds);
        Assert.False(model.HasPendingDeletion);
        Assert.Equal(_localization.Translate("Publish.BuildDeleted"), model.StatusMessage);
    }

    [Fact]
    public async Task ChangingYourMindSendsNothingAndClearsThePrompt()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")], [Build("b1", "v1")]);

        model.AskToDeleteBuildCommand.Execute(model.Builds[0]);
        model.CancelDeletionCommand.Execute(null);

        Assert.False(model.HasPendingDeletion);
        Assert.Single(model.Builds);
        await _publishing.DidNotReceive()
            .DeleteBuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A version is a container, and somebody clicking a row labelled "0.3.0" has no way to know
    // how much goes with it unless the prompt says so.
    [Fact]
    public async Task ThePromptForAVersionSaysHowManyBuildsGoWithIt()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")],
            [Build("b1", "v1"), Build("b2", "v1", GamePlatform.Linux)]);

        model.AskToDeleteVersionCommand.Execute(model.Versions[0]);

        Assert.Contains("0.3.0", model.PendingDeletion!.Prompt, StringComparison.Ordinal);
        Assert.Contains("2", model.PendingDeletion.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingAVersionTakesItsBuildsOutOfTheListToo()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0"), Version("v2", "0.4.0")],
            [Build("b1", "v1"), Build("b2", "v2")]);

        model.AskToDeleteVersionCommand.Execute(
            model.Versions.First(version => version.Id == "v1"));
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        await _publishing.Received(1)
            .DeleteVersionAsync("Orbital Drift", "v1", Arg.Any<CancellationToken>());
        Assert.Single(model.Versions);
        Assert.Single(model.Builds);
        Assert.Equal("b2", model.Builds[0].Id);
    }

    // A resource the caller may not see is a 404 server-side, and it must not be presented as a
    // permissions problem — the server refuses to confirm the thing exists, and so does this.
    [Fact]
    public async Task ARefusedDeletionIsReportedAsUnavailableAndLeavesTheListAlone()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")], [Build("b1", "v1")]);

        _publishing.DeleteBuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.NotFound, "no such build"));

        model.AskToDeleteBuildCommand.Execute(model.Builds[0]);
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
        Assert.Single(model.Builds);
    }

    // --- the children ------------------------------------------------------------------------

    [Fact]
    public async Task SelectingAGameFillsTheEditorTheArtworkAndTheDevlog()
    {
        DeveloperViewModel model = await WithSelectedGameAsync(
            [Version("v1", "0.3.0")], [Build("b1", "v1")]);

        Assert.True(model.Editor.HasGame);
        Assert.True(model.Artwork.HasGame);
        Assert.True(model.Devlog.HasGame);
        Assert.Equal("Orbital Drift", model.Editor.Title);

        // The devlog needs the versions so an entry can name one.
        Assert.Single(model.Devlog.Versions);
    }

    // Saving an edit is not the publisher picking another game, so it must not refetch the
    // detail, reload three children and wipe the message they have not read yet.
    [Fact]
    public async Task SavingAnEditUpdatesTheListWithoutReloadingTheGame()
    {
        DeveloperViewModel model = await WithSelectedGameAsync([], []);

        _publishing.UpdateGameAsync(
                Arg.Any<string>(), Arg.Any<GameChanges>(), Arg.Any<CancellationToken>())
            .Returns(GameNamed("Orbital Drift") with { Title = "Orbital Drift II" });

        _catalog.ClearReceivedCalls();

        model.Editor.Title = "Orbital Drift II";
        await model.Editor.SaveCommand.ExecuteAsync(null);
        await Task.Yield();

        Assert.Equal("Orbital Drift II", model.Games[0].Title);
        await _catalog.DidNotReceive()
            .GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
