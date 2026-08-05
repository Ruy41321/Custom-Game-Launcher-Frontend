using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// One released version, rendered as a patch-note card. The server decides which versions are
/// visible, so an unpublished one only ever appears to somebody who could edit the game.
/// </summary>
public sealed class VersionCardViewModel(GameVersion version, ILocalizationService localization)
    : ViewModelBase
{
    public string Semver => version.Semver;

    public string Stage => localization.Translate("Stage." + version.Stage);

    /// <summary>Hidden for a released version; the badge is only interesting when it is not.</summary>
    public bool ShowStage => version.Stage != BuildStage.Release;

    public string ReleaseNotes => version.ReleaseNotes;

    public bool HasReleaseNotes => version.ReleaseNotes.Trim().Length > 0;

    public string PublishedOn => version.PublishedAt is { } published
        ? published.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
        : localization.Translate("Detail.Unpublished");
}

/// <summary>
/// One picture of the gallery. Its own object because a screenshot arrives on its own: the
/// page shows the frames it has and fills them in as the rest land.
/// </summary>
public sealed partial class ScreenshotViewModel(GameMedia media) : ViewModelBase
{
    [ObservableProperty]
    private Bitmap? _image;

    public string Url => media.Url;

    /// <summary>The publisher's description, which is what a screen reader reads out.</summary>
    public string AltText => media.AltText;

    public bool HasImage => Image is not null;

    public async Task LoadAsync(
        IImageProvider images, CancellationToken cancellationToken = default) =>
        Image = await images.GetAsync(media.Url, cancellationToken).ConfigureAwait(true);

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
}

/// <summary>
/// One devlog entry, as a card. The body is shown as the publisher typed it: rendering remote
/// Markdown would mean rendering remote markup, and a devlog is not worth that.
/// </summary>
public sealed partial class PatchNoteCardViewModel : ViewModelBase
{
    private readonly PatchNote _note;
    private readonly ILocalizationService _localization;

    public PatchNoteCardViewModel(
        PatchNote note, string versionLabel, ILocalizationService localization)
    {
        _note = note;
        _localization = localization;
        VersionLabel = versionLabel;
    }

    public string Title => _note.Title;

    public string Body => _note.BodyMarkdown;

    public string Author => _localization.Translate("Detail.DevlogBy", _note.Author.DisplayName);

    /// <summary>
    /// The semver of the version the entry names, when the detail response carried it. An
    /// entry about a version this account cannot see shows no badge rather than an id.
    /// </summary>
    public string VersionLabel { get; }

    public bool ShowVersion => VersionLabel.Length > 0;

    /// <summary>A draft says so: only its publisher is ever sent one.</summary>
    public string PublishedOn => _note.PublishedAt is { } published
        ? published.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
        : _localization.Translate("Detail.DevlogDraft");
}

/// <summary>
/// The game page: description, what is installable here, and the version history. Reused
/// across navigations rather than rebuilt, so <see cref="LoadAsync"/> resets everything it
/// does not overwrite.
/// </summary>
public sealed partial class GameDetailViewModel : ViewModelBase
{
    private readonly ICatalogApi _catalog;
    private readonly ILibraryApi _library;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;
    private readonly IRuntimePlatform _platform;
    private readonly IAuthenticationService _authentication;
    private readonly IInstallationService _installations;
    private readonly IInstallStore _installs;
    private readonly IGameLauncher _games;
    private readonly IImageProvider _images;
    private readonly TransferRateEstimator _rate;

    private CancellationTokenSource? _installCancellation;

    private int _devlogTotal;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private GameDetail? _detail;

    [ObservableProperty]
    private bool _inLibrary;

    [ObservableProperty]
    private InstalledGame? _installed;

    /// <summary>
    /// The banner if the publisher uploaded one, the cover otherwise, and nothing when there
    /// is neither: a banner is the wide picture a page like this is for.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _hero;

    /// <summary>Which screenshot is shown large. The first one, until somebody picks another.</summary>
    [ObservableProperty]
    private ScreenshotViewModel? _selectedScreenshot;

    [ObservableProperty]
    private bool _isDevlogBusy;

    /// <summary>
    /// Kept apart from <see cref="ErrorMessage"/> on purpose: a devlog that will not load must
    /// not replace a page from which a game can still be installed and played.
    /// </summary>
    [ObservableProperty]
    private string? _devlogError;

    /// <summary>Null when nothing is running; the last report otherwise.</summary>
    [ObservableProperty]
    private DownloadProgress? _progress;

    /// <summary>What just finished — an install, a removal, a verification.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Edited in the box, saved on the button. Kept separate from
    /// <see cref="InstalledGame.LaunchOptions"/> so a half-typed argument is never what the
    /// game is started with.
    /// </summary>
    [ObservableProperty]
    private string _launchOptions = string.Empty;

    public GameDetailViewModel(
        ICatalogApi catalog,
        ILibraryApi library,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IRuntimePlatform platform,
        IAuthenticationService authentication,
        IInstallationService installations,
        IInstallStore installs,
        IGameLauncher games,
        IImageProvider images,
        TimeProvider time)
    {
        _images = images;
        _catalog = catalog;
        _library = library;
        _errors = errors;
        _localization = localization;
        _platform = platform;
        _authentication = authentication;
        _installations = installations;
        _installs = installs;
        _games = games;
        _rate = new TransferRateEstimator(time);

        // The process exits on a thread that is not the UI's, and the page only has to know
        // that its own game stopped.
        _games.GameExited += (_, args) => OnUiThread(() =>
        {
            if (args.GameId == Detail?.Game.Id)
            {
                RaiseDerived();
            }
        });
    }

    public event EventHandler? BackRequested;

    public ObservableCollection<VersionCardViewModel> Versions { get; } = [];

    public ObservableCollection<ScreenshotViewModel> Screenshots { get; } = [];

    public ObservableCollection<PatchNoteCardViewModel> Devlog { get; } = [];

    public bool HasHero => Hero is not null;

    public bool HasScreenshots => Screenshots.Count > 0;

    /// <summary>True once the first page has come back and there was nothing in it.</summary>
    public bool DevlogIsEmpty =>
        !IsDevlogBusy && Devlog.Count == 0 && DevlogError is null && _devlogTotal == 0;

    /// <summary>
    /// The server said how many entries there are, so "more" is a fact rather than a guess at
    /// whether the last page was full.
    /// </summary>
    public bool HasMoreDevlog => !IsDevlogBusy && Devlog.Count < _devlogTotal;

    public string Title => Detail?.Game.Title ?? string.Empty;

    public string Summary => Detail?.Game.Summary ?? string.Empty;

    public string Description => Detail?.Game.Description ?? string.Empty;

    public string PublisherName => Detail?.Game.Publisher.DisplayName ?? string.Empty;

    public string ReleaseDate => Detail?.Game.ReleaseDate is { } date
        ? date.ToString("d", CultureInfo.CurrentCulture)
        : _localization.Translate("Detail.Unreleased");

    /// <summary>What this machine could install, or null when the publisher shipped nothing for it.</summary>
    public GameBuild? InstallableBuild =>
        Detail?.BuildFor(_platform.Platform, _platform.Architecture);

    public bool HasInstallableBuild => InstallableBuild is not null;

    /// <summary>
    /// Advisory: the button is hidden when the account cannot download, but the server checks
    /// the same permission again on the request that would follow.
    /// </summary>
    public bool CanDownload =>
        HasInstallableBuild && _authentication.HasPermission(Permissions.GameDownload);

    public string DownloadSize => InstallableBuild is { } build
        ? ByteSize.Format(build.TotalSizeBytes, CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>A finished install of some build — not necessarily the newest one.</summary>
    public bool IsInstalled => Installed?.State == InstallState.Installed;

    public bool IsBroken => Installed?.State == InstallState.Broken;

    /// <summary>An install that this machine's newest build would replace.</summary>
    public bool HasUpdate =>
        IsInstalled && InstallableBuild is { } build && !Installed!.Is(build.Id);

    public bool IsWorking => Progress is not null;

    public bool CanInstall => CanDownload && !IsWorking && !IsInstalled && !IsBroken;

    public bool CanUpdate => CanDownload && !IsWorking && (HasUpdate || IsBroken);

    public bool CanUninstall => Installed is not null && !IsWorking && !IsRunning;

    public bool CanVerify => IsInstalled && !IsWorking && !IsRunning;

    /// <summary>Started by this launcher and not yet seen to exit.</summary>
    public bool IsRunning => Installed is not null && _games.IsRunning(Installed.GameId);

    public bool CanPlay => IsInstalled && !IsWorking && !IsRunning;

    /// <summary>
    /// The arguments the build itself carries, shown so a player can see what their own are
    /// being added to. Read-only: they are the publisher's, and an update rewrites them.
    /// </summary>
    public string BuildLaunchArgs => Installed?.LaunchArgs ?? string.Empty;

    public string LaunchOptionsHint => _localization.Translate(
        "Detail.LaunchOptionsHint",
        BuildLaunchArgs.Length > 0
            ? BuildLaunchArgs
            : _localization.Translate("Detail.LaunchOptionsNone"));

    public bool CanEditLaunchOptions => Installed is not null && !IsWorking;

    /// <summary>True while the box says something the stored row does not.</summary>
    public bool LaunchOptionsChanged =>
        Installed is not null
        && !string.Equals(LaunchOptions.Trim(), Installed.LaunchOptions, StringComparison.Ordinal);

    public string InstalledVersion => Installed is { } install
        ? _localization.Translate("Detail.InstalledVersion", install.VersionSemver)
        : string.Empty;

    public double ProgressFraction => Progress?.Fraction ?? 0;

    /// <summary>
    /// The phases that have no byte count of their own. A bar filling up during a step that
    /// is not transferring anything is a bar that is lying.
    /// </summary>
    public bool IsProgressIndeterminate => Progress?.Phase
        is InstallPhase.Planning or InstallPhase.CheckingSpace or InstallPhase.Verifying;

    public string PhaseText => Progress is { } report
        ? _localization.Translate("Download." + report.Phase)
        : string.Empty;

    /// <summary>
    /// "1.2 GB of 4.7 GB — 3.4 MB/s — 2 min left", with each part left out when it would be a
    /// guess: a countdown that says four hours and then twelve seconds is worse than none.
    /// </summary>
    public string ProgressDetail
    {
        get
        {
            if (Progress is not { Phase: InstallPhase.Downloading } report
                || report.TotalBytes <= 0)
            {
                return string.Empty;
            }

            List<string> parts =
            [
                _localization.Translate(
                    "Download.Progress",
                    ByteSize.Format(report.TransferredBytes, CultureInfo.CurrentCulture),
                    ByteSize.Format(report.TotalBytes, CultureInfo.CurrentCulture)),
            ];

            if (_rate.BytesPerSecond > 0)
            {
                parts.Add(ByteSize.FormatRate(_rate.BytesPerSecond, CultureInfo.CurrentCulture));
            }

            if (_rate.Remaining(report.TransferredBytes, report.TotalBytes) is { } remaining)
            {
                parts.Add(_localization.Translate(
                    "Download.Remaining", FormatDuration(remaining)));
            }

            return string.Join(" — ", parts);
        }
    }

    public async Task LoadAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        Detail = null;
        Installed = null;
        Progress = null;
        Hero = null;
        SelectedScreenshot = null;
        DevlogError = null;
        _devlogTotal = 0;
        Versions.Clear();
        Screenshots.Clear();
        Devlog.Clear();
        RaiseDerived();

        try
        {
            GameDetail detail = await _catalog
                .GetGameAsync(idOrSlug, cancellationToken)
                .ConfigureAwait(true);

            Detail = detail;
            InLibrary = detail.InLibrary;
            Installed = await _installs
                .FindAsync(detail.Game.Id, cancellationToken)
                .ConfigureAwait(true);

            foreach (GameVersion version in detail.Versions)
            {
                Versions.Add(new VersionCardViewModel(version, _localization));
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

        if (Detail is null)
        {
            return;
        }

        // Both after the page is on screen. Neither the artwork nor the devlog is what the
        // page is for, and a game must be installable before its screenshots have arrived.
        await LoadArtworkAsync(cancellationToken).ConfigureAwait(true);
        await LoadDevlogAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// The next page of the devlog, appended. Paged rather than fetched whole because a game
    /// three years old has a devlog nobody wants delivered in one response.
    /// </summary>
    [RelayCommand]
    private Task LoadMoreDevlogAsync(CancellationToken cancellationToken) =>
        HasMoreDevlog ? LoadDevlogAsync(cancellationToken) : Task.CompletedTask;

    private async Task LoadArtworkAsync(CancellationToken cancellationToken)
    {
        if (Detail is not { } detail)
        {
            return;
        }

        GameMedia? banner = detail.Artwork(MediaKind.Banner) ?? detail.Artwork(MediaKind.Cover);
        Hero = banner is null
            ? null
            : await _images.GetAsync(banner.Url, cancellationToken).ConfigureAwait(true);

        foreach (GameMedia shot in detail.Screenshots)
        {
            Screenshots.Add(new ScreenshotViewModel(shot));
        }

        SelectedScreenshot = Screenshots.FirstOrDefault();
        RaiseDerived();

        foreach (ScreenshotViewModel screenshot in Screenshots.ToList())
        {
            await screenshot.LoadAsync(_images, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task LoadDevlogAsync(CancellationToken cancellationToken)
    {
        if (Detail is not { } detail)
        {
            return;
        }

        IsDevlogBusy = true;
        DevlogError = null;
        RaiseDevlogDerived();

        try
        {
            // The page number follows from what is already shown, so a reload and a "show
            // more" are the same call and neither can ask for a page twice.
            int page = (Devlog.Count / ICatalogApi.DefaultPatchNotePageSize) + 1;

            PagedResult<PatchNote> result = await _catalog
                .GetPatchNotesAsync(detail.Game.Id, page, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            _devlogTotal = result.Total;
            foreach (PatchNote note in result.Items)
            {
                Devlog.Add(new PatchNoteCardViewModel(note, VersionLabelFor(note), _localization));
            }
        }
        catch (ApiException exception)
        {
            DevlogError = _errors.Describe(exception);
        }
        finally
        {
            IsDevlogBusy = false;
            RaiseDevlogDerived();
        }
    }

    /// <summary>
    /// The semver of the version an entry names, when the detail response carried that version.
    /// It may not: a note can point at a version this account is not allowed to see.
    /// </summary>
    private string VersionLabelFor(PatchNote note) =>
        note.HasVersion && Detail is { } detail
            ? detail.Versions.FirstOrDefault(version => version.Id == note.VersionId)?.Semver
                ?? string.Empty
            : string.Empty;

    [RelayCommand]
    private void ShowScreenshot(ScreenshotViewModel? screenshot)
    {
        if (screenshot is not null)
        {
            SelectedScreenshot = screenshot;
        }
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task AddToLibraryAsync(CancellationToken cancellationToken)
    {
        if (Detail is null)
        {
            return;
        }

        try
        {
            await _library.AddAsync(Detail.Game.Id, cancellationToken).ConfigureAwait(true);
            InLibrary = true;
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
    }

    [RelayCommand]
    private async Task RemoveFromLibraryAsync(CancellationToken cancellationToken)
    {
        if (Detail is null)
        {
            return;
        }

        try
        {
            await _library.RemoveAsync(Detail.Game.Id, cancellationToken).ConfigureAwait(true);
            InLibrary = false;
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
    }

    /// <summary>
    /// Installs, updates or repairs — one command, because from here they are the same request
    /// and the server works out which of the three it is.
    /// </summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Detail is null || InstallableBuild is not { } build)
        {
            return;
        }

        GameVersion? version = Detail.Versions.FirstOrDefault(v => v.Id == build.VersionId);
        if (version is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        _rate.Reset();
        Progress = new DownloadProgress { Phase = InstallPhase.Planning };
        RaiseDerived();

        using CancellationTokenSource cancellation = new();
        _installCancellation = cancellation;

        try
        {
            InstallResult result = await _installations.InstallAsync(
                new InstallRequest { Game = Detail.Game, Version = version, Build = build },
                new Progress<DownloadProgress>(OnProgress),
                cancellation.Token).ConfigureAwait(true);

            Installed = result.Install;
            StatusMessage = _localization.Translate("Detail.InstallComplete");
        }
        catch (Exception exception) when (
            exception is ApiException or InsufficientDiskSpaceException
                or OperationCanceledException)
        {
            ErrorMessage = _errors.Describe(exception);

            // Whatever the failure was, the row is the authority on what is on disk now — a
            // cancelled update leaves an install that is no longer the build it was.
            Installed = await ReloadInstalledAsync().ConfigureAwait(true);
        }
        finally
        {
            _installCancellation = null;
            Progress = null;
            RaiseDerived();
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCancellation?.Cancel();

    /// <summary>
    /// Saved on demand rather than on every keystroke: the row is what the next launch reads,
    /// and half a typed argument is not something to start a game with.
    /// </summary>
    [RelayCommand]
    private async Task SaveLaunchOptionsAsync(CancellationToken cancellationToken)
    {
        if (Installed is null)
        {
            return;
        }

        ErrorMessage = null;

        InstalledGame updated = Installed with { LaunchOptions = LaunchOptions.Trim() };
        await _installs.SaveAsync(updated, cancellationToken).ConfigureAwait(true);

        Installed = updated;
        StatusMessage = _localization.Translate("Detail.LaunchOptionsSaved");
    }

    [RelayCommand]
    private async Task PlayAsync(CancellationToken cancellationToken)
    {
        if (Installed is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await _games.LaunchAsync(Installed.GameId, cancellationToken).ConfigureAwait(true);

            // Re-read rather than patch the field: starting a game writes LastPlayedAt, and
            // the row on disk is the authority on what the launcher believes.
            Installed = await ReloadInstalledAsync().ConfigureAwait(true);
        }
        catch (GameLaunchException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            RaiseDerived();
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(CancellationToken cancellationToken)
    {
        if (Installed is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            UninstallResult result = await _installations
                .UninstallAsync(Installed.GameId, cancellationToken)
                .ConfigureAwait(true);

            Installed = null;
            StatusMessage = _localization.Translate(
                "Detail.Uninstalled",
                ByteSize.Format(result.FreedBytes, CultureInfo.CurrentCulture));
        }
        catch (Exception exception) when (exception is ApiException or IOException)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            RaiseDerived();
        }
    }

    [RelayCommand]
    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        if (Installed is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        Progress = new DownloadProgress { Phase = InstallPhase.Verifying };
        RaiseDerived();

        try
        {
            IntegrityReport report = await _installations
                .VerifyAsync(Installed.GameId, cancellationToken)
                .ConfigureAwait(true);

            StatusMessage = report.Intact
                ? _localization.Translate("Detail.VerifyIntact")
                : _localization.Translate(
                    "Detail.VerifyBroken",
                    report.Missing.Count.ToString(CultureInfo.CurrentCulture),
                    report.Corrupt.Count.ToString(CultureInfo.CurrentCulture));

            Installed = await ReloadInstalledAsync().ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            Progress = null;
            RaiseDerived();
        }
    }

    /// <summary>
    /// The terminal report is not a state of its own: the buttons come back instead, and
    /// <see cref="StatusMessage"/> says what happened.
    /// </summary>
    private void OnProgress(DownloadProgress report) =>
        Progress = report.Phase == InstallPhase.Done ? null : report;

    private async Task<InstalledGame?> ReloadInstalledAsync() =>
        Detail is null ? null : await _installs.FindAsync(Detail.Game.Id).ConfigureAwait(true);

    /// <summary>Minutes and seconds, because an hour of precision nobody asked for is noise.</summary>
    internal static string FormatDuration(TimeSpan remaining) => remaining.TotalHours >= 1
        ? string.Create(CultureInfo.CurrentCulture, $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m")
        : remaining.TotalMinutes >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)remaining.TotalMinutes}m {remaining.Seconds:00}s")
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)remaining.TotalSeconds)}s");

    partial void OnDetailChanged(GameDetail? value) => RaiseDerived();

    partial void OnHeroChanged(Bitmap? value) => OnPropertyChanged(nameof(HasHero));

    partial void OnInstalledChanged(InstalledGame? value)
    {
        // The box follows the row whenever the row changes underneath it — a fresh install, a
        // reload, an update — because the row is what the next launch will actually read.
        LaunchOptions = value?.LaunchOptions ?? string.Empty;
        RaiseDerived();
    }

    partial void OnLaunchOptionsChanged(string value) =>
        OnPropertyChanged(nameof(LaunchOptionsChanged));

    /// <summary>
    /// Every progress report funnels through the property, so the rate is fed from one place
    /// whether the report came from an install or from a test setting the state directly.
    /// </summary>
    partial void OnProgressChanged(DownloadProgress? value)
    {
        if (value is { Phase: InstallPhase.Downloading })
        {
            _rate.Observe(value.TransferredBytes);
        }

        RaiseDerived();
    }

    private void RaiseDevlogDerived()
    {
        OnPropertyChanged(nameof(DevlogIsEmpty));
        OnPropertyChanged(nameof(HasMoreDevlog));
        LoadMoreDevlogCommand.NotifyCanExecuteChanged();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasHero));
        OnPropertyChanged(nameof(HasScreenshots));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(PublisherName));
        OnPropertyChanged(nameof(ReleaseDate));
        OnPropertyChanged(nameof(InstallableBuild));
        OnPropertyChanged(nameof(HasInstallableBuild));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadSize));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(BuildLaunchArgs));
        OnPropertyChanged(nameof(LaunchOptionsHint));
        OnPropertyChanged(nameof(CanEditLaunchOptions));
        OnPropertyChanged(nameof(LaunchOptionsChanged));
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(ProgressDetail));
    }

}
