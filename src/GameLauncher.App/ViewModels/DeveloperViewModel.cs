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
/// The publisher's page: their own games including drafts, the versions of the selected one,
/// and the flow that turns a directory into a build people can install.
///
/// Every rule this page appears to enforce is enforced again server-side. What it does here is
/// avoid offering an action that would be refused (D8).
/// </summary>
public sealed partial class DeveloperViewModel : ViewModelBase
{
    private readonly ICatalogApi _catalog;
    private readonly IPublishingApi _publishing;
    private readonly IBuildPublisher _publisher;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IRuntimePlatform _platform;
    private readonly IFolderPicker _folders;

    private CancellationTokenSource? _publishCancellation;

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
    private GameVersion? _selectedVersion;

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
        IFolderPicker folders)
    {
        _catalog = catalog;
        _publishing = publishing;
        _publisher = publisher;
        _errors = errors;
        _localization = localization;
        _platform = platform;
        _folders = folders;

        BuildPlatform = platform.Platform;
        Architecture = platform.Architecture;
    }

    public ObservableCollection<Game> Games { get; } = [];

    public ObservableCollection<GameVersion> Versions { get; } = [];

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

    public bool CanCreateGame => !IsBusy && !IsPublishing && NewGameTitle.Trim().Length > 0;

    public bool CanCreateVersion =>
        Selected is not null && !IsBusy && !IsPublishing && NewVersionSemver.Trim().Length > 0;

    public bool CanPublish =>
        SelectedVersion is not null
        && !IsPublishing
        && BuildDirectory.Trim().Length > 0
        && Entrypoint.Trim().Length > 0;

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

            Versions.Insert(0, created);
            SelectedVersion = created;
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
                    Directory = BuildDirectory.Trim(),
                    Entrypoint = Entrypoint.Trim(),
                    LaunchArgs = LaunchArgs.Trim(),
                },
                new Progress<PublishProgress>(report => Progress = report),
                cancellation.Token).ConfigureAwait(true);

            Builds.Insert(0, result.Build);
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
        if (value is not null)
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
            Versions.Clear();
            foreach (GameVersion version in detail.Versions)
            {
                Versions.Add(version);
            }

            Builds.Clear();
            foreach (GameBuild build in detail.Builds)
            {
                Builds.Add(build);
            }

            SelectedVersion = Versions.FirstOrDefault();
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

    partial void OnProgressChanged(PublishProgress? value) => RaiseDerived();

    partial void OnSelectedVersionChanged(GameVersion? value) => RaiseDerived();

    partial void OnNewGameTitleChanged(string value) => OnPropertyChanged(nameof(CanCreateGame));

    partial void OnNewVersionSemverChanged(string value) =>
        OnPropertyChanged(nameof(CanCreateVersion));

    partial void OnBuildDirectoryChanged(string value) => OnPropertyChanged(nameof(CanPublish));

    partial void OnEntrypointChanged(string value) => OnPropertyChanged(nameof(CanPublish));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsPublishing));
        OnPropertyChanged(nameof(CanCreateGame));
        OnPropertyChanged(nameof(CanCreateVersion));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(PublishPhaseText));
        OnPropertyChanged(nameof(PublishFraction));
        OnPropertyChanged(nameof(IsPublishProgressIndeterminate));
        OnPropertyChanged(nameof(PublishDetail));
    }
}
