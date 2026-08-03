using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class LibraryViewModelTests
{
    private readonly ILibraryApi _library = Substitute.For<ILibraryApi>();
    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private LibraryViewModel CreateViewModel() =>
        new(_library, new ApiErrorPresenter(_localization));

    private void Returns(params string[] titles) =>
        _library.GetLibraryAsync(Arg.Any<CancellationToken>()).Returns(
            [.. titles.Select(title => new Game { Id = title, Slug = title, Title = title })]);

    [Fact]
    public async Task LoadingFillsTheList()
    {
        Returns("Orbital Drift", "Deep Cut");
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Games.Count);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void AnUnloadedLibraryIsNotAnEmptyOne()
    {
        Assert.False(CreateViewModel().IsEmpty);
    }

    [Fact]
    public async Task AnAccountWithNoGamesIsReportedAsEmpty()
    {
        Returns();
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public async Task ReloadingDoesNotDuplicateWhatIsAlreadyThere()
    {
        Returns("Orbital Drift");
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(model.Games);
    }

    [Fact]
    public async Task AFailedLoadShowsAMessageAndNotAnEmptyLibrary()
    {
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.DependencyFailure, "down"));
        LibraryViewModel model = CreateViewModel();

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_localization.Translate("Error.DependencyFailure"), model.ErrorMessage);
        Assert.False(model.IsEmpty);
    }

    // The server has confirmed the removal, so a second round trip would only make the list
    // flicker for information it already has.
    [Fact]
    public async Task RemovingTakesTheGameOutOfTheListWithoutReloading()
    {
        Returns("Orbital Drift", "Deep Cut");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Game removed = model.Games[0];

        await model.RemoveCommand.ExecuteAsync(removed);

        await _library.Received(1).RemoveAsync(removed.Id, Arg.Any<CancellationToken>());
        Assert.Single(model.Games);
        await _library.Received(1).GetLibraryAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedRemovalLeavesTheGameInPlace()
    {
        Returns("Orbital Drift");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        _library.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.NotFound, "gone"));
        await model.RemoveCommand.ExecuteAsync(model.Games[0]);

        Assert.Single(model.Games);
        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
    }

    [Fact]
    public async Task RemovingTheLastGameLeavesAnEmptyLibraryAndSaysSo()
    {
        Returns("Orbital Drift");
        LibraryViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await model.RemoveCommand.ExecuteAsync(model.Games[0]);

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void OpeningAGameAsksForItByItsIdentifier()
    {
        LibraryViewModel model = CreateViewModel();
        string? requested = null;
        model.GameSelected += (_, idOrSlug) => requested = idOrSlug;

        model.OpenGameCommand.Execute(new Game { Id = "g1", Slug = "orbital-drift" });

        Assert.Equal("orbital-drift", requested);
    }
}
