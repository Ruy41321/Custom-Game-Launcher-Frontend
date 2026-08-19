using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Text;

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
/// One devlog entry, as a card that opens.
///
/// It used to be a block of raw text, which is what made a devlog of three posts look like one
/// unreadable wall: the body is Markdown, the publisher wrote it as Markdown, and it arrived on
/// screen with the asterisks still in it. It is parsed now (see <see cref="MarkdownParser"/> for
/// why that does not reopen what D38 closed), and a card that is not open shows one line of it,
/// so a list of entries is a list rather than a scroll.
/// </summary>
public sealed partial class PatchNoteCardViewModel : ViewModelBase
{
    private readonly PatchNote _note;
    private readonly ILocalizationService _localization;

    /// <summary>
    /// Whether the body is shown. The newest entry arrives open, because a devlog whose every
    /// card is shut is a page that looks like it failed to load.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    public PatchNoteCardViewModel(
        PatchNote note, string versionLabel, ILocalizationService localization)
    {
        _note = note;
        _localization = localization;
        VersionLabel = versionLabel;
    }

    public string Title => _note.Title;

    public string Body => _note.BodyMarkdown;

    /// <summary>
    /// The first paragraph, as plain text, for a card that is shut. Taken from the parsed
    /// document rather than from the raw string so that a post opening with a heading or a
    /// fenced block does not preview as <c>## Ciao a tutti</c>.
    /// </summary>
    public string Preview
    {
        get
        {
            MarkdownBlock? first = MarkdownParser
                .Parse(_note.BodyMarkdown)
                .FirstOrDefault(block => block.Kind != MarkdownBlockKind.Code);

            return first is null
                ? string.Empty
                : string.Concat(first.Spans.Select(span => span.Text));
        }
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

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
public sealed partial class GameDetailViewModel : ViewModelBase, IAccountScopedPage
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
    private readonly IVideoPlayback _playback;
    private readonly IFileBrowser _files;
    private readonly IFolderPicker _folders;
    private readonly IUserSettingsStore _settings;
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
    private MediaCardViewModel? _selectedScreenshot;

    /// <summary>The video being played, or the last one that was. Null until somebody presses play.</summary>
    [ObservableProperty]
    private MediaCardViewModel? _playingVideo;

    /// <summary>
    /// Why nothing is playing, when somebody asked for it. Kept apart from
    /// <see cref="ErrorMessage"/> for the reason <see cref="DevlogError"/> is: a trailer that
    /// will not play must not replace a page a game can still be installed from.
    /// </summary>
    [ObservableProperty]
    private string? _videoError;

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
        IVideoPlayback playback,
        IFileBrowser files,
        IFolderPicker folders,
        IUserSettingsStore settings,
        TimeProvider time)
    {
        _images = images;
        _playback = playback;
        _files = files;
        _folders = folders;
        _settings = settings;
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

    public ObservableCollection<MediaCardViewModel> Screenshots { get; } = [];

    /// <summary>
    /// The videos, in the order the publisher arranged them. A list of their own rather than
    /// rows in the screenshot strip: one is played and the other is decoded, and the two have
    /// nothing in common on screen beyond belonging to the same game.
    /// </summary>
    public ObservableCollection<MediaCardViewModel> Videos { get; } = [];

    public ObservableCollection<PatchNoteCardViewModel> Devlog { get; } = [];

    public bool HasHero => Hero is not null;

    public bool HasScreenshots => Screenshots.Count > 0;

    public bool HasVideos => Videos.Count > 0;

    /// <summary>
    /// Whether this machine can play anything. False is ordinary rather than broken — there is
    /// no libvlc package for Linux, so a machine without VLC installed lands here — and the
    /// page says so instead of offering a button that does nothing.
    /// </summary>
    public bool CanPlayVideo => _playback.IsAvailable;

    /// <summary>
    /// What a <c>VideoView</c> binds to, and <b>null until something is playing</b> — which is
    /// load-bearing twice over. The view puts the player in a <c>ContentControl</c>, so a null
    /// here means no native child window is created at all: opening a game page costs nothing,
    /// and a machine that cannot host one is not asked to. And reading this property is what
    /// loads libvlc, so a page with no trailer on it never pays for ~100 MB of native library.
    /// </summary>
    public object? VideoPlayer => IsPlayingVideo ? _playback.Player : null;

    public bool IsPlayingVideo => PlayingVideo is not null;

    /// <summary>
    /// Whether to say that this machine cannot play. Both halves, and in this order: the
    /// short-circuit is what keeps a game page with no videos from initialising a native
    /// library to answer a question nobody on that page is asking.
    /// </summary>
    public bool ShowVideoUnavailable => HasVideos && !_playback.IsAvailable;

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

    /// <summary>
    /// An update is not optional. A player who starts an old build talks to a server, saves
    /// into a format or joins a session the new one changed, and every one of those failures
    /// arrives later and looks like the game being broken rather than like a skipped update.
    /// The button is not disabled but absent, and the sentence below says why — a greyed-out
    /// Play with no explanation is the same dead end with worse manners.
    /// </summary>
    public bool CanPlay => IsInstalled && !HasUpdate && !IsWorking && !IsRunning;

    /// <summary>Shown exactly where the Play button would have been.</summary>
    public bool MustUpdateBeforePlaying => HasUpdate && !IsWorking;

    /// <summary>
    /// Leaving the library while the files are still on this disk would leave an install the
    /// account no longer owns: it could not be updated, could not be repaired, and nothing on
    /// the page would say why. Uninstalling first is the order that works, so the button is
    /// not offered until then.
    /// </summary>
    public bool CanRemoveFromLibrary => InLibrary && Installed is null;

    /// <summary>The install directory, if there is one to show.</summary>
    public bool CanOpenFolder =>
        Installed is { } install && install.InstallDirectory.Length > 0;

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
        // Loading another game stops the previous one's trailer. Nothing else would: the page
        // object outlives the game it is showing.
        StopVideo();
        VideoError = null;
        DevlogError = null;
        _devlogTotal = 0;
        Versions.Clear();
        Screenshots.Clear();
        Videos.Clear();
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
            Screenshots.Add(new MediaCardViewModel(shot));
        }

        // No LoadAsync for these: a video has no thumbnail to fetch, and MediaCardViewModel
        // knows to skip the decoder rather than every caller having to remember.
        foreach (GameMedia video in detail.Videos)
        {
            Videos.Add(new MediaCardViewModel(video));
        }

        SelectedScreenshot = Screenshots.FirstOrDefault();
        RaiseDerived();

        foreach (MediaCardViewModel screenshot in Screenshots.ToList())
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
                PatchNoteCardViewModel card = new(note, VersionLabelFor(note), _localization);

                // Only the newest, and only on the first page: an older entry fetched by
                // pressing "show older" opening itself would push the list under the pointer.
                card.IsExpanded = Devlog.Count == 0;
                Devlog.Add(card);
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
    private void ShowScreenshot(MediaCardViewModel? screenshot)
    {
        if (screenshot is not null)
        {
            SelectedScreenshot = screenshot;
        }
    }

    /// <summary>
    /// Starts a trailer. Everything about this is a state machine except the picture itself,
    /// which is the one thing no test here can see — so what is pinned by tests is: nothing
    /// plays on a machine that cannot play, a refusal says so instead of failing silently, and
    /// a second press replaces the first video rather than stacking two.
    /// </summary>
    [RelayCommand]
    private void PlayVideo(MediaCardViewModel? video)
    {
        if (video is null || !video.IsVideo)
        {
            return;
        }

        VideoError = null;

        if (!_playback.IsAvailable)
        {
            VideoError = _localization.Translate("Detail.VideoUnavailable");
            RaiseVideoDerived();
            return;
        }

        if (!_playback.Play(video.Url))
        {
            PlayingVideo = null;
            VideoError = _localization.Translate("Detail.VideoFailed");
            RaiseVideoDerived();
            return;
        }

        PlayingVideo = video;
        RaiseVideoDerived();
    }

    /// <summary>
    /// Stops whatever is playing. Called by the button, and by everything that takes the page
    /// away — navigating back, loading another game, losing the account — because a trailer
    /// still playing over the next screen is the failure this exists to prevent.
    /// </summary>
    [RelayCommand]
    private void StopVideo()
    {
        _playback.StopPlayback();
        PlayingVideo = null;
        RaiseVideoDerived();
    }

    [RelayCommand]
    private void Back()
    {
        // Leaving the page stops the sound. A trailer still playing over the library is the
        // failure this line exists to prevent, and nothing else takes this page down.
        StopVideo();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

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

    /// <summary>
    /// Shows the install directory in the desktop's file manager. Saves, screenshots, logs and
    /// mods are all in there and none of them is anywhere the launcher would think to look, so
    /// the way to that folder is worth a button rather than a support answer.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        if (Installed is not { } install)
        {
            return;
        }

        ErrorMessage = null;

        if (!_files.Reveal(install.InstallDirectory))
        {
            ErrorMessage = _localization.Translate("Detail.OpenFolderFailed");
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

        // Asked before anything is planned or written, and only for a game that is not here
        // yet: an update goes where the game already lives, and asking again would invite
        // somebody to answer with a second directory the first install is not in.
        string? root = null;
        if (Installed is null && await ShouldAskWhereToInstallAsync().ConfigureAwait(true))
        {
            root = await _folders
                .PickAsync(_localization.Translate("Detail.ChooseInstallDirectory", Title))
                .ConfigureAwait(true);

            // Cancelling the question cancels the install. Falling back to the default here
            // would install the game somewhere the player has just declined to confirm.
            if (root is null)
            {
                return;
            }
        }

        _rate.Reset();
        Progress = new DownloadProgress { Phase = InstallPhase.Planning };
        RaiseDerived();

        using CancellationTokenSource cancellation = new();
        _installCancellation = cancellation;

        try
        {
            InstallResult result = await _installations.InstallAsync(
                new InstallRequest
                {
                    Game = Detail.Game,
                    Version = version,
                    Build = build,
                    InstallRoot = root,
                },
                new Progress<DownloadProgress>(OnProgress),
                cancellation.Token).ConfigureAwait(true);

            Installed = result.Install;
            StatusMessage = _localization.Translate("Detail.InstallComplete");

            // Installing a game is deciding to own it, and finding it missing from the library
            // afterwards is the kind of gap people report as a bug. It runs after the install
            // rather than before, so a download that never finished leaves no entitlement
            // nobody asked for; and its own failure goes to ErrorMessage without touching
            // StatusMessage, because the files really are on this disk either way.
            await AddToLibraryAfterInstallAsync(cancellation.Token).ConfigureAwait(true);
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
    /// The preference is read at the moment it is needed rather than held from page load: the
    /// settings page writes it, and a game page opened before that would otherwise keep asking
    /// — or keep not asking — until it was navigated away from.
    /// </summary>
    private async Task<bool> ShouldAskWhereToInstallAsync()
    {
        UserSettings settings = await _settings.LoadAsync().ConfigureAwait(true);
        return settings.AskWhereToInstall;
    }

    /// <summary>
    /// Adds a freshly installed game to the library, if it is not already there. Adding one
    /// twice is not an error the server minds, but there is no reason to spend the round trip.
    /// </summary>
    private async Task AddToLibraryAfterInstallAsync(CancellationToken cancellationToken)
    {
        if (InLibrary || Detail is null)
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

    /// <summary>Whether the game can leave the library depends on both halves of that.</summary>
    partial void OnInLibraryChanged(bool value) =>
        OnPropertyChanged(nameof(CanRemoveFromLibrary));

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

    private void RaiseVideoDerived()
    {
        OnPropertyChanged(nameof(IsPlayingVideo));
        OnPropertyChanged(nameof(VideoPlayer));
        OnPropertyChanged(nameof(CanPlayVideo));
        OnPropertyChanged(nameof(ShowVideoUnavailable));
    }

    private void RaiseDevlogDerived()
    {
        OnPropertyChanged(nameof(DevlogIsEmpty));
        OnPropertyChanged(nameof(HasMoreDevlog));
        LoadMoreDevlogCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One game, fetched for one account, and half of what is on the page is about whether
    /// *that* account owns it — the library button, the download permission, the versions the
    /// server chose to show. None of it is worth keeping for the next person.
    ///
    /// An install in flight is cancelled rather than left running: it is downloading with
    /// credentials that no longer exist, so its next signed URL would be refused, and a
    /// transfer that fails halfway with nobody watching is worse than one stopped on purpose.
    /// What is already on the disk stays there, and the recovery pass at the next start is
    /// what decides whether it is an install or a mess (see <c>IInstallationService</c>).
    /// </summary>
    public void ResetForAccountChange()
    {
        _installCancellation?.Cancel();

        Detail = null;
        Installed = null;
        InLibrary = false;
        Hero = null;
        SelectedScreenshot = null;
        StopVideo();
        VideoError = null;
        Progress = null;
        IsBusy = false;
        IsDevlogBusy = false;
        ErrorMessage = null;
        StatusMessage = null;
        DevlogError = null;
        LaunchOptions = string.Empty;
        _devlogTotal = 0;

        Versions.Clear();
        Screenshots.Clear();
        Videos.Clear();
        Devlog.Clear();

        RaiseDerived();
        RaiseDevlogDerived();
        RaiseVideoDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasHero));
        OnPropertyChanged(nameof(HasScreenshots));
        OnPropertyChanged(nameof(HasVideos));
        OnPropertyChanged(nameof(CanPlayVideo));
        OnPropertyChanged(nameof(ShowVideoUnavailable));
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
        OnPropertyChanged(nameof(MustUpdateBeforePlaying));
        OnPropertyChanged(nameof(CanRemoveFromLibrary));
        OnPropertyChanged(nameof(CanOpenFolder));
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
