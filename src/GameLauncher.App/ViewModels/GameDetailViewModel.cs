using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
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

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private GameDetail? _detail;

    [ObservableProperty]
    private bool _inLibrary;

    public GameDetailViewModel(
        ICatalogApi catalog,
        ILibraryApi library,
        IApiErrorPresenter errors,
        ILocalizationService localization,
        IRuntimePlatform platform,
        IAuthenticationService authentication)
    {
        _catalog = catalog;
        _library = library;
        _errors = errors;
        _localization = localization;
        _platform = platform;
        _authentication = authentication;
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
        ? FormatBytes(build.TotalSizeBytes, CultureInfo.CurrentCulture)
        : string.Empty;

    public async Task LoadAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = null;
        Detail = null;
        Versions.Clear();
        RaiseDerived();

        try
        {
            GameDetail detail = await _catalog
                .GetGameAsync(idOrSlug, cancellationToken)
                .ConfigureAwait(true);

            Detail = detail;
            InLibrary = detail.InLibrary;

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

    partial void OnDetailChanged(GameDetail? value) => RaiseDerived();

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
    }

    /// <summary>
    /// Sizes are shown in the powers of 1024 that a file manager uses, because a user
    /// comparing "4.7 GB" against their free disk space is comparing against that number.
    /// </summary>
    internal static string FormatBytes(long bytes, IFormatProvider? culture = null)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // The decimal separator follows the user's culture — a size is a number they read.
        return string.Create(
            culture ?? CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
