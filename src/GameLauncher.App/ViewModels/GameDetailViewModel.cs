using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly TransferRateEstimator _rate;

    private CancellationTokenSource? _installCancellation;

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

    /// <summary>Null when nothing is running; the last report otherwise.</summary>
    [ObservableProperty]
    private DownloadProgress? _progress;

    /// <summary>What just finished — an install, a removal, a verification.</summary>
    [ObservableProperty]
    private string? _statusMessage;

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
        TimeProvider time)
    {
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
        Versions.Clear();
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

    partial void OnInstalledChanged(InstalledGame? value) => RaiseDerived();

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

    private void RaiseDerived()
    {
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
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(ProgressDetail));
    }

}
