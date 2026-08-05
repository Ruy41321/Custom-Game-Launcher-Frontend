using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class GameDevlogViewModelTests
{
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly IPublishingApi _publishing = Substitute.For<IPublishingApi>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    private static readonly Game TheGame = new()
    {
        Id = "g1",
        Slug = "orbital-drift",
        Title = "Orbital Drift",
    };

    private static readonly GameVersion Version = new() { Id = "v1", Semver = "0.3.0" };

    public GameDevlogViewModelTests() =>
        // An unconfigured Task<T> member yields default(T) — a null PagedResult — and the page
        // dereferences it the moment it loads. Every test class that builds this has to arrange
        // it, whether or not the test is about the devlog.
        HasEntries();

    private void HasEntries(int total = 0, params PatchNote[] entries) =>
        _catalog.GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<PatchNote>
            {
                Items = entries,
                Total = total == 0 ? entries.Length : total,
                Limit = ICatalogApi.DefaultPatchNotePageSize,
            });

    private static PatchNote Entry(string id, string title, bool published = false) => new()
    {
        Id = id,
        GameId = "g1",
        Title = title,
        BodyMarkdown = "Something happened.",
        Published = published,
        PublishedAt = published ? DateTimeOffset.UnixEpoch : null,
    };

    private GameDevlogViewModel CreateViewModel() =>
        new(_catalog, _publishing, new ApiErrorPresenter(_localization), _localization);

    private async Task<GameDevlogViewModel> ShowingAsync()
    {
        GameDevlogViewModel model = CreateViewModel();
        await model.ShowAsync(TheGame, [Version], TestContext.Current.CancellationToken);
        return model;
    }

    // --- writing ---------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyFormCannotBeSaved()
    {
        GameDevlogViewModel model = await ShowingAsync();

        Assert.False(model.CanSave);

        model.EntryTitle = "Docking rework";
        Assert.False(model.CanSave);

        model.EntryBody = "It is better now.";
        Assert.True(model.CanSave);
    }

    // A draft can be written before the build it talks about exists — that is the whole reason
    // a patch note is not a version's release notes.
    [Fact]
    public async Task AnEntryCanBeWrittenAsADraftAboutNoVersionAtAll()
    {
        GameDevlogViewModel model = await ShowingAsync();

        model.EntryTitle = "What we are working on";
        model.EntryBody = "Docking, mostly.";
        model.PublishImmediately = false;

        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).CreatePatchNoteAsync(
            "g1",
            Arg.Is<CreatePatchNoteRequest>(request =>
                request!.Title == "What we are working on"
                && request.VersionId == null
                && !request.Publish),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEntryCanNameAVersion()
    {
        GameDevlogViewModel model = await ShowingAsync();

        model.EntryTitle = "0.3.0 is out";
        model.EntryBody = "Docking reworked.";
        model.EntryVersion = model.Versions[0];
        model.PublishImmediately = true;

        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).CreatePatchNoteAsync(
            "g1",
            Arg.Is<CreatePatchNoteRequest>(request =>
                request!.VersionId == "v1" && request.Publish),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheFormIsClearedAfterAnEntryIsWritten()
    {
        GameDevlogViewModel model = await ShowingAsync();

        model.EntryTitle = "Docking rework";
        model.EntryBody = "It is better now.";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, model.EntryTitle);
        Assert.Equal(string.Empty, model.EntryBody);
        Assert.False(model.IsEditing);
        Assert.Equal(_localization.Translate("Publish.EntryCreated"), model.StatusMessage);
    }

    // --- editing -----------------------------------------------------------------------------

    [Fact]
    public async Task EditingLoadsTheEntryIntoTheSameForm()
    {
        HasEntries(entries: Entry("n1", "Docking rework", published: true));
        GameDevlogViewModel model = await ShowingAsync();

        model.EditCommand.Execute(model.Entries[0]);

        Assert.True(model.IsEditing);
        Assert.Equal("Docking rework", model.EntryTitle);
        Assert.True(model.PublishImmediately);
    }

    [Fact]
    public async Task SavingAnEditPatchesRatherThanCreating()
    {
        HasEntries(entries: Entry("n1", "Docking rework"));
        GameDevlogViewModel model = await ShowingAsync();

        model.EditCommand.Execute(model.Entries[0]);
        model.EntryTitle = "Docking rework, again";
        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).UpdatePatchNoteAsync(
            "n1",
            Arg.Is<PatchNoteChanges>(changes => changes!.Title == "Docking rework, again"),
            Arg.Any<CancellationToken>());
        await _publishing.DidNotReceive().CreatePatchNoteAsync(
            Arg.Any<string>(),
            Arg.Any<CreatePatchNoteRequest>(),
            Arg.Any<CancellationToken>());
    }

    // Null means "leave it alone" on the wire, so removing a link has to be said with an empty
    // string — otherwise an entry can be attached to a version and never detached.
    [Fact]
    public async Task DetachingAnEntryFromItsVersionSendsAnEmptyVersionId()
    {
        HasEntries(entries: Entry("n1", "Docking rework") with { VersionId = "v1" });
        GameDevlogViewModel model = await ShowingAsync();

        model.EditCommand.Execute(model.Entries[0]);
        Assert.Equal("v1", model.EntryVersion?.Id);

        model.EntryVersion = null;
        await model.SaveCommand.ExecuteAsync(null);

        await _publishing.Received(1).UpdatePatchNoteAsync(
            "n1",
            Arg.Is<PatchNoteChanges>(changes => changes!.VersionId == string.Empty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartingANewEntryLeavesTheEditBehind()
    {
        HasEntries(entries: Entry("n1", "Docking rework"));
        GameDevlogViewModel model = await ShowingAsync();

        model.EditCommand.Execute(model.Entries[0]);
        model.NewEntryCommand.Execute(null);

        Assert.False(model.IsEditing);
        Assert.Equal(string.Empty, model.EntryTitle);
    }

    // --- publishing and withdrawing ------------------------------------------------------------

    [Fact]
    public async Task PublishingADraftIsOneCallOnTheSameField()
    {
        HasEntries(entries: Entry("n1", "Docking rework", published: false));
        GameDevlogViewModel model = await ShowingAsync();

        await model.TogglePublishedCommand.ExecuteAsync(model.Entries[0]);

        await _publishing.Received(1).UpdatePatchNoteAsync(
            "n1",
            Arg.Is<PatchNoteChanges>(changes => changes!.Published == true),
            Arg.Any<CancellationToken>());
        Assert.Equal(_localization.Translate("Publish.EntryPublished"), model.StatusMessage);
    }

    // A note that went out by mistake has to be able to come back, and the message says that
    // withdrawing is reversible rather than leaving somebody to guess.
    [Fact]
    public async Task WithdrawingIsTheSameCallAndSaysItIsReversible()
    {
        HasEntries(entries: Entry("n1", "Docking rework", published: true));
        GameDevlogViewModel model = await ShowingAsync();

        await model.TogglePublishedCommand.ExecuteAsync(model.Entries[0]);

        await _publishing.Received(1).UpdatePatchNoteAsync(
            "n1",
            Arg.Is<PatchNoteChanges>(changes => changes!.Published == false),
            Arg.Any<CancellationToken>());
        Assert.Equal(_localization.Translate("Publish.EntryWithdrawn"), model.StatusMessage);
    }

    // --- paging ------------------------------------------------------------------------------

    // The page number is derived from how many entries are shown, which makes a reload and a
    // "show older" the same call and makes asking for the same page twice impossible.
    [Fact]
    public async Task ShowingOlderAsksForTheNextPageAndNotTheFirstAgain()
    {
        PatchNote[] first = [.. Enumerable
            .Range(0, ICatalogApi.DefaultPatchNotePageSize)
            .Select(index => Entry("n" + index, "Entry " + index))];

        HasEntries(total: 25, entries: first);
        GameDevlogViewModel model = await ShowingAsync();

        Assert.True(model.HasMore);

        await model.LoadMoreCommand.ExecuteAsync(null);

        await _catalog.Received(1).GetPatchNotesAsync(
            "g1", 1, ICatalogApi.DefaultPatchNotePageSize, Arg.Any<CancellationToken>());
        await _catalog.Received(1).GetPatchNotesAsync(
            "g1", 2, ICatalogApi.DefaultPatchNotePageSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThereIsNothingOlderWhenEveryEntryIsAlreadyShown()
    {
        HasEntries(total: 1, entries: Entry("n1", "Docking rework"));
        GameDevlogViewModel model = await ShowingAsync();

        Assert.False(model.HasMore);
    }

    // A write can change the ordering and the pagination, so keeping the pages already fetched
    // and appending to them would leave a stale prefix on screen.
    [Fact]
    public async Task AWriteReloadsFromTheFirstPage()
    {
        HasEntries(total: 25, entries: Entry("n1", "Docking rework"));
        GameDevlogViewModel model = await ShowingAsync();

        await model.LoadMoreCommand.ExecuteAsync(null);
        _catalog.ClearReceivedCalls();

        model.EntryTitle = "New";
        model.EntryBody = "Body";
        await model.SaveCommand.ExecuteAsync(null);

        await _catalog.Received(1).GetPatchNotesAsync(
            "g1", 1, ICatalogApi.DefaultPatchNotePageSize, Arg.Any<CancellationToken>());
    }

    // --- deleting ------------------------------------------------------------------------------

    [Fact]
    public async Task AskingToDeleteSendsNothingAndOffersWithdrawingInstead()
    {
        HasEntries(entries: Entry("n1", "Docking rework", published: true));
        GameDevlogViewModel model = await ShowingAsync();

        model.AskToDeleteCommand.Execute(model.Entries[0]);

        Assert.NotNull(model.PendingDeletion);
        Assert.Contains(
            "Docking rework", model.PendingDeletion.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            "withdraw", model.PendingDeletion.Prompt, StringComparison.OrdinalIgnoreCase);

        await _publishing.DidNotReceive()
            .DeletePatchNoteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmingDeletesAndClearsTheForm()
    {
        HasEntries(entries: Entry("n1", "Docking rework"));
        GameDevlogViewModel model = await ShowingAsync();

        model.EditCommand.Execute(model.Entries[0]);
        model.AskToDeleteCommand.Execute(model.Entries[0]);
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        await _publishing.Received(1).DeletePatchNoteAsync("n1", Arg.Any<CancellationToken>());
        Assert.False(model.IsEditing);
        Assert.Equal(_localization.Translate("Publish.EntryDeleted"), model.StatusMessage);
    }

    [Fact]
    public async Task ChangingYourMindDeletesNothing()
    {
        HasEntries(entries: Entry("n1", "Docking rework"));
        GameDevlogViewModel model = await ShowingAsync();

        model.AskToDeleteCommand.Execute(model.Entries[0]);
        model.CancelDeletionCommand.Execute(null);

        Assert.Null(model.PendingDeletion);
        await _publishing.DidNotReceive()
            .DeletePatchNoteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- failures ------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusedWriteIsReportedThroughTheUsualPresenter()
    {
        GameDevlogViewModel model = await ShowingAsync();
        _publishing.CreatePatchNoteAsync(
                Arg.Any<string>(),
                Arg.Any<CreatePatchNoteRequest>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.NotFound, "no such game"));

        model.EntryTitle = "Docking rework";
        model.EntryBody = "It is better now.";
        await model.SaveCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.NotFound"), model.ErrorMessage);
    }

    [Fact]
    public async Task ADevlogThatWillNotLoadDoesNotEmptyTheForm()
    {
        _catalog.GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Network, "offline"));

        GameDevlogViewModel model = await ShowingAsync();

        Assert.NotNull(model.ErrorMessage);
        Assert.Empty(model.Entries);
        Assert.True(model.HasGame);
    }
}
