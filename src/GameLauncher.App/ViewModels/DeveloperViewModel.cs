using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Publishing;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// The publisher's page: their own games including drafts, the versions and builds of the
/// selected one, the flow that turns a directory into a build people can install, and — through
/// three child view models — the game's own fields, its artwork and its devlog.
///
/// The children are children rather than pages of their own because a publisher works on **one
/// game at a time**: the selected game is the context all four share, and separate pages would
/// mean selecting it three times or inventing a shared navigation state that D17 does not have.
/// Tabs over child view models are binding, not navigation, so the one-way rule stays intact —
/// and three smaller view models give three readable test classes instead of one enormous one.
///
/// Every rule this page appears to enforce is enforced again server-side. What it does here is
/// avoid offering an action that would be refused (D8).
/// </summary>
public sealed partial class DeveloperViewModel : ViewModelBase, IAccountScopedPage
{
    private readonly ICatalogApi _catalog;
    private readonly IPublishingApi _publishing;
    private readonly IBuildPublisher _publisher;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IRuntimePlatform _platform;
    private readonly IFolderPicker _folders;

    private CancellationTokenSource? _publishCancellation;

    /// <summary>See <see cref="OnGameUpdated"/>: a saved edit is not a change of selection.</summary>
    private bool _suppressSelectionReload;

    [ObservableProperty]
    private PendingDeletion? _pendingDeletion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private Game? _selectedGame;

    [ObservableProperty]
    private GameDetail? _selected;

    [ObservableProperty]
    private VersionRowViewModel? _selectedVersion;

    [ObservableProperty]
    private PublishProgress? _progress;

    // --- the new-game form ---------------------------------------------------------------

    [ObservableProperty]
    private string _newGameTitle = string.Empty;

    [ObservableProperty]
    private string _newGameSummary = string.Empty;

    // --- the new-version form ------------------------------------------------------------

    [ObservableProperty]
    private string _newVersionSemver = string.Empty;

    [ObservableProperty]
    private BuildStage _newVersionStage = BuildStage.Beta;

    [ObservableProperty]
    private string _newVersionNotes = string.Empty;

    [ObservableProperty]
    private bool _publishVersionImmediately = true;

    // --- the publish form ----------------------------------------------------------------

    [ObservableProperty]
    private string _buildName = string.Empty;

    [ObservableProperty]
    private string _buildDirectory = string.Empty;

    [ObservableProperty]
    private string _entrypoint = string.Empty;

    [ObservableProperty]
    private string _launchArgs = string.Empty;

    public DeveloperViewModel(
        ICatalogApi catalog,
        IPublishingApi publishing,
        IBuildPublisher publisher,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IRuntimePlatform platform,
        IFolderPicker folders,
        GameEditorViewModel editor,
        GameMediaViewModel artwork,
        GameDevlogViewModel devlog)
    {
        _catalog = catalog;
        _publishing = publishing;
        _publisher = publisher;
        _errors = errors;
        _localization = localization;
        _platform = platform;
        _folders = folders;

        Editor = editor;
        Artwork = artwork;
        Devlog = devlog;

        // The editor is the one child that can change what the list above shows, so the list
        // hears about a save rather than being reloaded wholesale for a title edit.
        Editor.GameUpdated += OnGameUpdated;

        BuildPlatform = platform.Platform;
        Architecture = platform.Architecture;
    }

    /// <summary>
    /// Raised when the publisher asks to see the selected game the way a player sees it. The
    /// shell listens, exactly as it listens to Explore and the library (D17) — this page does
    /// not know the game page exists.
    /// </summary>
    public event EventHandler<string>? GameSelected;

    /// <summary>The selected game's own fields — title, summary, release date, visibility.</summary>
    public GameEditorViewModel Editor { get; }

    public GameMediaViewModel Artwork { get; }

    public GameDevlogViewModel Devlog { get; }

    public ObservableCollection<Game> Games { get; } = [];

    public ObservableCollection<VersionRowViewModel> Versions { get; } = [];

    public ObservableCollection<GameBuild> Builds { get; } = [];

    public IReadOnlyList<GameVisibility> Visibilities { get; } = Enum.GetValues<GameVisibility>();

    public IReadOnlyList<BuildStage> Stages { get; } = Enum.GetValues<BuildStage>();

    public IReadOnlyList<GamePlatform> Platforms { get; } = Enum.GetValues<GamePlatform>();

    public IReadOnlyList<BuildArchitecture> Architectures { get; } =
        Enum.GetValues<BuildArchitecture>();

    /// <summary>
    /// Defaults to the machine doing the publishing, which is what it is nearly always
    /// building for — and being wrong here produces a build nobody can install.
    /// </summary>
    [ObservableProperty]
    private GamePlatform _buildPlatform;

    [ObservableProperty]
    private BuildArchitecture _architecture;

    [ObservableProperty]
    private GameVisibility _newGameVisibility = GameVisibility.Draft;

    public bool IsPublishing => Progress is not null;

    public bool HasPendingDeletion => PendingDeletion is not null;

    public bool CanCreateGame => !IsBusy && !IsPublishing && NewGameTitle.Trim().Length > 0;

    public bool CanCreateVersion =>
        Selected is not null && !IsBusy && !IsPublishing && NewVersionSemver.Trim().Length > 0;

    public bool CanPublish =>
        SelectedVersion is not null
        && !IsPublishing
        && BuildDirectory.Trim().Length > 0
        && Entrypoint.Trim().Length > 0;

    /// <summary>
    /// Available for a draft too, which is the case worth stating: the server serves a game's
    /// detail to whoever may edit it whatever its visibility, so a publisher can read their own
    /// unreleased page. Nothing else on this launcher can.
    /// </summary>
    public bool CanOpenGamePage => SelectedGame is not null;

    public string PublishPhaseText => Progress is { } report
        ? _localization.Translate("Publish." + report.Phase)
        : string.Empty;

    public double PublishFraction => Progress?.Fraction ?? 0;

    public bool IsPublishProgressIndeterminate => Progress?.Phase
        is PublishPhase.Packaging or PublishPhase.Negotiating or PublishPhase.Finalizing;

    public string PublishDetail => Progress is { Phase: PublishPhase.Uploading } report
        ? _localization.Translate(
            "Download.Progress",
            ByteSize.Format(report.UploadedBytes, CultureInfo.CurrentCulture),
            ByteSize.Format(report.TotalBytes, CultureInfo.CurrentCulture))
        : Progress is { Phase: PublishPhase.Packaging } packaging && packaging.TotalFiles > 0
            ? _localization.Translate(
                "Download.Progress",
                packaging.FilesHashed.ToString(CultureInfo.CurrentCulture),
                packaging.TotalFiles.ToString(CultureInfo.CurrentCulture))
            : string.Empty;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            PagedResult<Game> mine = await _catalog
                .GetMyGamesAsync(
                    new GameQuery { Sort = GameSort.Recent, PageSize = GameQuery.MaxPageSize },
                    cancellationToken)
                .ConfigureAwait(true);

            Games.Clear();
            foreach (Game game in mine.Items)
            {
                Games.Add(game);
            }
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
    private async Task CreateGameAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            Game created = await _publishing.CreateGameAsync(
                new CreateGameRequest
                {
                    Title = NewGameTitle.Trim(),
                    Summary = NewGameSummary.Trim(),
                    Visibility = NewGameVisibility,
                },
                cancellationToken).ConfigureAwait(true);

            Games.Insert(0, created);
            SelectedGame = created;
            NewGameTitle = string.Empty;
            NewGameSummary = string.Empty;
            StatusMessage = _localization.Translate("Publish.GameCreated", created.Slug);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            RaiseDerived();
        }
    }

    [RelayCommand]
    private async Task CreateVersionAsync(CancellationToken cancellationToken)
    {
        if (Selected is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            GameVersion created = await _publishing.CreateVersionAsync(
                Selected.Game.Id,
                new CreateVersionRequest
                {
                    Semver = NewVersionSemver.Trim(),
                    Stage = NewVersionStage,
                    ReleaseNotes = NewVersionNotes.Trim(),
                    Publish = PublishVersionImmediately,
                },
                cancellationToken).ConfigureAwait(true);

            VersionRowViewModel row = new(created, []);
            Versions.Insert(0, row);
            SelectedVersion = row;
            NewVersionSemver = string.Empty;
            NewVersionNotes = string.Empty;
            StatusMessage = _localization.Translate("Publish.VersionCreated", created.Semver);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            RaiseDerived();
        }
    }

    [RelayCommand]
    private async Task ChooseDirectoryAsync()
    {
        string? chosen = await _folders
            .PickAsync(_localization.Translate("Publish.ChooseDirectory"))
            .ConfigureAwait(true);

        if (chosen is null)
        {
            return;
        }

        BuildDirectory = chosen;

        // A build almost always has one obvious executable, and typing its name again is a
        // chance to get it wrong in a way that only shows up after the upload.
        if (Entrypoint.Length == 0)
        {
            Entrypoint = GuessEntrypoint(chosen) ?? string.Empty;
        }

        RaiseDerived();
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (Selected is null || SelectedVersion is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        Progress = new PublishProgress { Phase = PublishPhase.Packaging };
        RaiseDerived();

        using CancellationTokenSource cancellation = new();
        _publishCancellation = cancellation;

        try
        {
            PublishResult result = await _publisher.PublishAsync(
                new PublishRequest
                {
                    GameIdOrSlug = Selected.Game.Id,
                    VersionId = SelectedVersion.Id,
                    Platform = BuildPlatform,
                    Architecture = Architecture,
                    Name = BuildName.Trim(),
                    Directory = BuildDirectory.Trim(),
                    Entrypoint = Entrypoint.Trim(),
                    LaunchArgs = LaunchArgs.Trim(),
                },
                new Progress<PublishProgress>(report => Progress = report),
                cancellation.Token).ConfigureAwait(true);

            Builds.Insert(0, result.Build);
            BuildName = string.Empty;

            // The row above has just gained a build, and it is the thing the publisher is
            // looking at. Refreshed in place rather than by rebuilding the list, which would
            // drop the selection they are in the middle of using.
            VersionRowFor(result.Build.VersionId)?.RefreshBuilds(BuildsOf(result.Build.VersionId));
            StatusMessage = _localization.Translate(
                "Publish.BuildReady",
                ByteSize.Format(result.UploadedBytes, CultureInfo.CurrentCulture),
                result.BlobsAlreadyPresent.ToString(CultureInfo.CurrentCulture));
        }
        catch (Exception exception) when (
            exception is ApiException or PublishingException or OperationCanceledException)
        {
            ErrorMessage = Describe(exception);
        }
        finally
        {
            _publishCancellation = null;
            Progress = null;
            RaiseDerived();
        }
    }

    [RelayCommand]
    private void CancelPublish() => _publishCancellation?.Cancel();

    /// <summary>
    /// Opens the selected game's own page — the one a player lands on — so that a publisher can
    /// see what they have made rather than inferring it from the boxes they filled in. The
    /// dashboard shows fields and lists; whether the banner is the right way round, whether the
    /// summary reads as a sentence and whether the screenshots are in a sensible order are all
    /// questions only that page answers.
    ///
    /// The slug is preferred over the id because it is what a URL and a support message would
    /// carry, and the id is the fallback for a game whose slug the server has not derived yet.
    /// </summary>
    [RelayCommand]
    private void OpenGamePage()
    {
        if (SelectedGame is not { } game)
        {
            return;
        }

        GameSelected?.Invoke(this, game.Slug.Length > 0 ? game.Slug : game.Id);
    }

    /// <summary>
    /// Publishes a version that was created without "publish it now", or withdraws one that was.
    ///
    /// This is a command and not a checkbox because it is a request to the server that can be
    /// refused, and a checkbox that silently springs back is worse than a button that reports
    /// why. Publishing is safe to press twice — the server keeps the original date — and
    /// withdrawing is offered because it is the reversible thing standing next to Delete: a
    /// version published by mistake can be taken back out of sight without taking its builds,
    /// its uploads and its devlog links with it.
    ///
    /// No confirmation prompt (D43 is about deletions): both directions are undone by pressing
    /// the other button, and nothing is destroyed either way.
    /// </summary>
    [RelayCommand]
    private async Task SetVersionPublishedAsync(VersionRowViewModel row)
    {
        if (Selected is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;
        RaiseDerived();

        bool publishing = !row.Published;

        try
        {
            GameVersion updated = await _publishing.UpdateVersionAsync(
                Selected.Game.Id,
                row.Id,
                new VersionChanges { Published = publishing }).ConfigureAwait(true);

            row.Version = updated;
            StatusMessage = _localization.Translate(
                publishing ? "Publish.VersionPublished" : "Publish.VersionWithdrawn",
                updated.Semver);
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

    /// <summary>The builds hanging off one version, in the order the server listed them.</summary>
    private IEnumerable<GameBuild> BuildsOf(string versionId) => Builds
        .Where(build => string.Equals(build.VersionId, versionId, StringComparison.Ordinal));

    /// <summary>
    /// A packaging failure is local and specific, and saying which rule was broken is the
    /// difference between fixing it and guessing.
    /// </summary>
    private string Describe(Exception exception) => exception switch
    {
        PublishingException publishing =>
            _localization.Translate("Publish." + publishing.Reason) + " " + publishing.Message,
        _ => _errors.Describe(exception),
    };

    private static string? GuessEntrypoint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string[] candidates = [.. Directory.EnumerateFiles(directory, "*.exe")];
        if (candidates.Length == 1)
        {
            return Path.GetFileName(candidates[0]);
        }

        return null;
    }

    partial void OnSelectedGameChanged(Game? value)
    {
        OnPropertyChanged(nameof(CanOpenGamePage));

        if (value is not null && !_suppressSelectionReload)
        {
            _ = LoadSelectedAsync(value);
        }
    }

    private async Task LoadSelectedAsync(Game game)
    {
        ErrorMessage = null;

        try
        {
            GameDetail detail = await _catalog.GetGameAsync(game.Id).ConfigureAwait(true);

            Selected = detail;

            Builds.Clear();
            foreach (GameBuild build in detail.Builds)
            {
                Builds.Add(build);
            }

            // Builds first: a row is built from its own builds, and the list it reads has to be
            // the new game's rather than the previous one's.
            Versions.Clear();
            foreach (GameVersion version in detail.Versions)
            {
                Versions.Add(new VersionRowViewModel(version, BuildsOf(version.Id)));
            }

            SelectedVersion = Versions.FirstOrDefault();

            Editor.Show(detail.Game);
            await Artwork.ShowAsync(detail.Game).ConfigureAwait(true);
            await Devlog.ShowAsync(detail.Game, detail.Versions).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            RaiseDerived();
        }
    }

    // --- deleting a build or a version -----------------------------------------------------

    /// <summary>
    /// Arms the deletion of one build. The prompt names the platform, the architecture and the
    /// version it belongs to, because a publisher looking at four rows of "Ready" needs to know
    /// which one is about to go.
    /// </summary>
    [RelayCommand]
    private void AskToDeleteBuild(GameBuild build)
    {
        ErrorMessage = null;
        StatusMessage = null;

        string version = VersionRowFor(build.VersionId)?.Semver ?? build.VersionId;

        PendingDeletion = new PendingDeletion(
            _localization.Translate(
                "Publish.ConfirmDeleteBuild",
                version,
                build.Platform.ToString(),
                build.Architecture.ToString()),
            async cancellationToken =>
            {
                await _publishing.DeleteBuildAsync(build.Id, cancellationToken)
                    .ConfigureAwait(true);

                Builds.Remove(build);
                VersionRowFor(build.VersionId)?.RefreshBuilds(BuildsOf(build.VersionId));
                StatusMessage = _localization.Translate("Publish.BuildDeleted");
            });

        RaiseDerived();
    }

    /// <summary>
    /// Arms the deletion of a version. The prompt says **how many builds go with it**: a
    /// version is a container, and deleting one takes everything under it — which is exactly
    /// the part somebody clicking a row labelled "0.3.0" would not otherwise be told.
    /// </summary>
    [RelayCommand]
    private void AskToDeleteVersion(VersionRowViewModel row)
    {
        if (Selected is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        int builds = BuildsOf(row.Id).Count();

        PendingDeletion = new PendingDeletion(
            _localization.Translate(
                "Publish.ConfirmDeleteVersion",
                row.Semver,
                builds.ToString(CultureInfo.CurrentCulture)),
            async cancellationToken =>
            {
                await _publishing.DeleteVersionAsync(
                    Selected.Game.Id, row.Id, cancellationToken).ConfigureAwait(true);

                Versions.Remove(row);
                foreach (GameBuild build in BuildsOf(row.Id).ToList())
                {
                    Builds.Remove(build);
                }

                SelectedVersion = Versions.FirstOrDefault();
                StatusMessage = _localization.Translate("Publish.VersionDeleted");
            });

        RaiseDerived();
    }

    /// <summary>
    /// Arms the deletion of a whole game.
    ///
    /// The prompt has to carry the part the other two do not: the server allows this **even
    /// while other people hold the game in their library**, so nothing refuses it on their
    /// behalf. What they installed keeps working and their updates stop, and a publisher who
    /// only wants the title to stop being visible wants <c>draft</c> instead — which the prompt
    /// names, because it is the reversible thing somebody in this position usually meant.
    /// </summary>
    [RelayCommand]
    private void AskToDeleteGame(Game game)
    {
        ErrorMessage = null;
        StatusMessage = null;

        int versions = Selected is { } detail
            && string.Equals(detail.Game.Id, game.Id, StringComparison.Ordinal)
                ? Versions.Count
                : 0;

        PendingDeletion = new PendingDeletion(
            _localization.Translate(
                "Publish.ConfirmDeleteGame",
                game.Title,
                versions.ToString(CultureInfo.CurrentCulture)),
            async cancellationToken =>
            {
                await _publishing.DeleteGameAsync(game.Id, cancellationToken).ConfigureAwait(true);

                Games.Remove(game);

                // Clearing the selection rather than picking the next row: the children below
                // are all showing a game that no longer exists, and letting the list choose a
                // replacement would silently point a publisher at somebody's other title.
                if (Selected is { } shown
                    && string.Equals(shown.Game.Id, game.Id, StringComparison.Ordinal))
                {
                    ClearSelection();
                }

                StatusMessage = _localization.Translate("Publish.GameDeleted", game.Title);
            });

        RaiseDerived();
    }

    /// <summary>
    /// Puts the page back to "no game selected". <see cref="_suppressSelectionReload"/> is held
    /// while it happens: assigning <see cref="SelectedGame"/> normally means a publisher picked
    /// a game, and null would otherwise start a load of nothing.
    /// </summary>
    private void ClearSelection()
    {
        _suppressSelectionReload = true;
        try
        {
            SelectedGame = null;
        }
        finally
        {
            _suppressSelectionReload = false;
        }

        Selected = null;
        SelectedVersion = null;
        Versions.Clear();
        Builds.Clear();

        Editor.Show(null);

        // The three tabs are one selection: leaving the artwork and the devlog of a game that
        // is no longer selected — deleted, in the caller above — is how a publisher ends up
        // editing the alt text of a picture belonging to a game that is gone.
        Artwork.Clear();
        Devlog.Clear();
    }

    /// <summary>
    /// The page this was found on: after a sign-out and a sign-in the dashboard was still
    /// showing the previous account's game, selection and all — every field of it fetched with
    /// a token that no longer exists. <see cref="ClearSelection"/> was already the right shape
    /// and nothing called it when the session changed.
    ///
    /// The list goes with the selection, and so do the three forms. A half-typed new game and
    /// a build directory chosen on the previous account's behalf are that account's work: the
    /// next person's dashboard opens empty, as it does on a launcher that has just started.
    /// The platform and architecture go back to what this machine is, which is where the
    /// constructor puts them, because those describe the computer rather than the publisher.
    /// </summary>
    public void ResetForAccountChange()
    {
        _publishCancellation?.Cancel();

        ClearSelection();
        Games.Clear();

        PendingDeletion = null;
        Progress = null;
        IsBusy = false;
        ErrorMessage = null;
        StatusMessage = null;

        NewGameTitle = string.Empty;
        NewGameSummary = string.Empty;
        NewGameVisibility = GameVisibility.Draft;

        NewVersionSemver = string.Empty;
        NewVersionStage = BuildStage.Beta;
        NewVersionNotes = string.Empty;
        PublishVersionImmediately = true;

        BuildName = string.Empty;
        BuildDirectory = string.Empty;
        Entrypoint = string.Empty;
        LaunchArgs = string.Empty;
        BuildPlatform = _platform.Platform;
        Architecture = _platform.Architecture;

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
    /// Replaces the row in the list rather than reloading it. An edit that changed a title has
    /// to be visible above, and refetching every game the publisher owns to learn one field
    /// would make saving a summary cost a page load.
    ///
    /// The reload is suppressed while the row is swapped: assigning <see cref="SelectedGame"/>
    /// is what normally means "the publisher picked another game", and letting a save mean that
    /// would refetch the detail, reload three children, and wipe the "saved" message the
    /// publisher has not read yet.
    /// </summary>
    private void OnGameUpdated(object? sender, Game updated)
    {
        Game? row = Games.FirstOrDefault(game =>
            string.Equals(game.Id, updated.Id, StringComparison.Ordinal));

        _suppressSelectionReload = true;
        try
        {
            if (row is not null)
            {
                Games[Games.IndexOf(row)] = updated;
                SelectedGame = updated;
            }

            if (Selected is { } detail)
            {
                Selected = detail with { Game = updated };
            }
        }
        finally
        {
            _suppressSelectionReload = false;
        }
    }

    private VersionRowViewModel? VersionRowFor(string versionId) => Versions
        .FirstOrDefault(row => string.Equals(row.Id, versionId, StringComparison.Ordinal));

    partial void OnProgressChanged(PublishProgress? value) => RaiseDerived();

    partial void OnSelectedVersionChanged(VersionRowViewModel? value) => RaiseDerived();

    partial void OnNewGameTitleChanged(string value) => OnPropertyChanged(nameof(CanCreateGame));

    partial void OnNewVersionSemverChanged(string value) =>
        OnPropertyChanged(nameof(CanCreateVersion));

    partial void OnBuildDirectoryChanged(string value) => OnPropertyChanged(nameof(CanPublish));

    partial void OnEntrypointChanged(string value) => OnPropertyChanged(nameof(CanPublish));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasPendingDeletion));
        OnPropertyChanged(nameof(IsPublishing));
        OnPropertyChanged(nameof(CanCreateGame));
        OnPropertyChanged(nameof(CanCreateVersion));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanOpenGamePage));
        OnPropertyChanged(nameof(PublishPhaseText));
        OnPropertyChanged(nameof(PublishFraction));
        OnPropertyChanged(nameof(IsPublishProgressIndeterminate));
        OnPropertyChanged(nameof(PublishDetail));
    }
}
