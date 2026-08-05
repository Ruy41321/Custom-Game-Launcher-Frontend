using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Writing a game's devlog: new entries, edits, publishing, withdrawing and deleting.
///
/// A devlog entry is deliberately not a version's release notes. It may name a version or none
/// at all — "what we are working on this month" is a legitimate post — and it carries a
/// publication state of its own, so a draft can exist before the build it talks about does.
///
/// **Publishing and withdrawing are the same field**, because a note that went out by mistake
/// has to be able to come back. Re-publishing does not move the original date: that date is
/// when readers saw it, not when it was last edited, and this page says so rather than leaving
/// somebody to discover it.
/// </summary>
public sealed partial class GameDevlogViewModel : ViewModelBase
{
    private readonly ICatalogApi _catalog;
    private readonly IPublishingApi _publishing;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;

    private Game? _game;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private PendingDeletion? _pendingDeletion;

    [ObservableProperty]
    private PatchNote? _selected;

    [ObservableProperty]
    private bool _hasMore;

    // --- the form, used for both a new entry and an edit -----------------------------------

    [ObservableProperty]
    private string _entryTitle = string.Empty;

    [ObservableProperty]
    private string _entryBody = string.Empty;

    /// <summary>Null posts about the game rather than about a version, which is allowed.</summary>
    [ObservableProperty]
    private GameVersion? _entryVersion;

    [ObservableProperty]
    private bool _publishImmediately;

    public GameDevlogViewModel(
        ICatalogApi catalog,
        IPublishingApi publishing,
        IApiErrorPresenter errors,
        ILocalizationService localization)
    {
        _catalog = catalog;
        _publishing = publishing;
        _errors = errors;
        _localization = localization;
    }

    /// <summary>Every entry loaded so far, newest first, drafts included for their author.</summary>
    public ObservableCollection<PatchNote> Entries { get; } = [];

    /// <summary>The game's versions, so an entry can name one. Set by the dashboard.</summary>
    public ObservableCollection<GameVersion> Versions { get; } = [];

    public bool HasGame => _game is not null;

    public bool IsEditing => Selected is not null;

    public bool CanSave =>
        HasGame && !IsBusy && PendingDeletion is null
        && EntryTitle.Trim().Length > 0 && EntryBody.Trim().Length > 0;

    public async Task ShowAsync(
        Game? game,
        IEnumerable<GameVersion> versions,
        CancellationToken cancellationToken = default)
    {
        _game = game;
        ErrorMessage = null;
        StatusMessage = null;
        PendingDeletion = null;

        Versions.Clear();
        foreach (GameVersion version in versions)
        {
            Versions.Add(version);
        }

        ClearForm();
        Entries.Clear();

        if (game is not null)
        {
            await LoadPageAsync(cancellationToken).ConfigureAwait(true);
        }

        RaiseDerived();
    }

    /// <summary>
    /// Loads the next page. The page number is derived from how many entries are already shown,
    /// which is what makes "reload" and "show older" the same call and makes asking for the
    /// same page twice impossible — the same arrangement the player-facing devlog uses (D38).
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreAsync(CancellationToken cancellationToken) =>
        await LoadPageAsync(cancellationToken).ConfigureAwait(true);

    private async Task LoadPageAsync(CancellationToken cancellationToken)
    {
        if (_game is not { } game)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        RaiseDerived();

        try
        {
            int page = (Entries.Count / ICatalogApi.DefaultPatchNotePageSize) + 1;

            PagedResult<PatchNote> result = await _catalog
                .GetPatchNotesAsync(
                    game.Id, page, ICatalogApi.DefaultPatchNotePageSize, cancellationToken)
                .ConfigureAwait(true);

            foreach (PatchNote note in result.Items)
            {
                Entries.Add(note);
            }

            HasMore = Entries.Count < result.Total;
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    /// <summary>Loads an existing entry into the form. The same form writes and edits.</summary>
    [RelayCommand]
    private void Edit(PatchNote note)
    {
        Selected = note;
        EntryTitle = note.Title;
        EntryBody = note.BodyMarkdown;
        EntryVersion = Versions.FirstOrDefault(
            version => string.Equals(version.Id, note.VersionId, StringComparison.Ordinal));
        PublishImmediately = note.Published;
        ErrorMessage = null;
        StatusMessage = null;

        RaiseDerived();
    }

    [RelayCommand]
    private void NewEntry()
    {
        ClearForm();
        RaiseDerived();
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_game is not { } game || !CanSave)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        RaiseDerived();

        try
        {
            if (Selected is { } existing)
            {
                await _publishing.UpdatePatchNoteAsync(
                    existing.Id,
                    new PatchNoteChanges
                    {
                        Title = EntryTitle.Trim(),
                        BodyMarkdown = EntryBody.Trim(),
                        // An empty string detaches the entry from its version. Null would mean
                        // "leave it alone", which cannot express removing the link.
                        VersionId = EntryVersion?.Id ?? string.Empty,
                        Published = PublishImmediately,
                    },
                    cancellationToken).ConfigureAwait(true);

                StatusMessage = _localization.Translate("Publish.EntrySaved");
            }
            else
            {
                await _publishing.CreatePatchNoteAsync(
                    game.Id,
                    new CreatePatchNoteRequest
                    {
                        Title = EntryTitle.Trim(),
                        BodyMarkdown = EntryBody.Trim(),
                        VersionId = EntryVersion?.Id,
                        Publish = PublishImmediately,
                    },
                    cancellationToken).ConfigureAwait(true);

                StatusMessage = _localization.Translate("Publish.EntryCreated");
            }

            ClearForm();
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    /// <summary>
    /// Publishes or withdraws in one call, without going through the form. Re-publishing keeps
    /// the original date, which is the server's rule and the reason this is not a "post" button.
    /// </summary>
    [RelayCommand]
    private async Task TogglePublishedAsync(PatchNote note)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        RaiseDerived();

        try
        {
            await _publishing.UpdatePatchNoteAsync(
                note.Id,
                new PatchNoteChanges { Published = !note.Published },
                CancellationToken.None).ConfigureAwait(true);

            StatusMessage = _localization.Translate(
                note.Published ? "Publish.EntryWithdrawn" : "Publish.EntryPublished");

            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    /// <summary>
    /// Arms the deletion, naming the entry and saying that withdrawing is the reversible option.
    /// Somebody who wants a post to stop being visible almost always wants that instead.
    /// </summary>
    [RelayCommand]
    private void AskToDelete(PatchNote note)
    {
        ErrorMessage = null;
        StatusMessage = null;

        PendingDeletion = new PendingDeletion(
            _localization.Translate("Publish.ConfirmDeleteEntry", note.Title),
            cancellationToken => _publishing.DeletePatchNoteAsync(note.Id, cancellationToken));

        RaiseDerived();
    }

    [RelayCommand]
    private async Task ConfirmDeletionAsync(CancellationToken cancellationToken)
    {
        if (PendingDeletion is not { } deletion)
        {
            return;
        }

        PendingDeletion = null;
        IsBusy = true;
        RaiseDerived();

        try
        {
            await deletion.ConfirmAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = _localization.Translate("Publish.EntryDeleted");

            ClearForm();
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseDerived();
        }
    }

    [RelayCommand]
    private void CancelDeletion()
    {
        PendingDeletion = null;
        RaiseDerived();
    }

    /// <summary>
    /// Back to the first page. A write can change how the list is ordered and paginated, so
    /// keeping the pages already fetched and appending to them would show a stale prefix.
    /// </summary>
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        Entries.Clear();
        await LoadPageAsync(cancellationToken).ConfigureAwait(true);
    }

    private void ClearForm()
    {
        Selected = null;
        EntryTitle = string.Empty;
        EntryBody = string.Empty;
        EntryVersion = null;
        PublishImmediately = false;
    }

    partial void OnSelectedChanged(PatchNote? value) => RaiseDerived();

    partial void OnEntryTitleChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnEntryBodyChanged(string value) => OnPropertyChanged(nameof(CanSave));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(CanSave));
    }
}
