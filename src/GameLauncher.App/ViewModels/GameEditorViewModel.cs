using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Editing a game that already exists. <c>IPublishingApi.UpdateGameAsync</c> had been written
/// and tested since milestone 8 and no screen called it, so a game created as a draft could
/// only be published from outside the launcher.
///
/// The one thing this page really has to get right is that **an absent field means "leave it
/// alone"**: it sends only what the publisher actually changed, so opening the tab and pressing
/// save cannot rewrite a description with the value a text box happened to hold.
/// </summary>
public sealed partial class GameEditorViewModel : ViewModelBase
{
    private readonly IPublishingApi _publishing;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;

    /// <summary>What the server last said the game was. Every change is measured against it.</summary>
    private Game? _original;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _releaseDate;

    [ObservableProperty]
    private GameVisibility _visibility;

    public GameEditorViewModel(
        IPublishingApi publishing,
        IApiErrorPresenter errors,
        ILocalizationService localization)
    {
        _publishing = publishing;
        _errors = errors;
        _localization = localization;
    }

    /// <summary>Raised when a save changed the game, so the list above can refresh its row.</summary>
    public event EventHandler<Game>? GameUpdated;

    public IReadOnlyList<GameVisibility> Visibilities { get; } = Enum.GetValues<GameVisibility>();

    public bool HasGame => _original is not null;

    public bool CanSave => HasGame && !IsBusy && Title.Trim().Length > 0 && HasChanges;

    /// <summary>
    /// Whether anything differs from what the server last said. Also what keeps the save button
    /// honest: a page with nothing to save should not offer to save.
    /// </summary>
    public bool HasChanges => _original is { } original
        && (!string.Equals(Title.Trim(), original.Title, StringComparison.Ordinal)
            || !string.Equals(Summary.Trim(), original.Summary, StringComparison.Ordinal)
            || !string.Equals(Description.Trim(), original.Description, StringComparison.Ordinal)
            || DateOnlyOrNull() != original.ReleaseDate
            || Visibility != original.Visibility);

    /// <summary>Fills the form from the game the dashboard has selected.</summary>
    public void Show(Game? game)
    {
        _original = game;
        ErrorMessage = null;
        StatusMessage = null;

        Title = game?.Title ?? string.Empty;
        Summary = game?.Summary ?? string.Empty;
        Description = game?.Description ?? string.Empty;
        ReleaseDate = game?.ReleaseDate is { } date
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
        Visibility = game?.Visibility ?? GameVisibility.Draft;

        RaiseDerived();
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_original is not { } original || !HasChanges)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        RaiseDerived();

        try
        {
            Game updated = await _publishing.UpdateGameAsync(
                original.Id, ChangesAgainst(original), cancellationToken).ConfigureAwait(true);

            // Reseeding from the response rather than from the form is what makes a value the
            // server normalised — a trimmed title, a derived slug — the one the page then shows.
            Show(updated);
            StatusMessage = _localization.Translate("Publish.GameUpdated");
            GameUpdated?.Invoke(this, updated);
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
    /// Only what differs. Null is absence on the wire, so a field left alone is a field the
    /// server does not touch — which is the difference between editing a summary and rewriting
    /// a description with whatever was on screen.
    /// </summary>
    private GameChanges ChangesAgainst(Game original) => new()
    {
        Title = Different(Title, original.Title),
        Summary = Different(Summary, original.Summary),
        Description = Different(Description, original.Description),
        ReleaseDate = DateOnlyOrNull() != original.ReleaseDate ? DateOnlyOrNull() : null,
        Visibility = Visibility != original.Visibility ? Visibility : null,
    };

    private static string? Different(string edited, string original) =>
        string.Equals(edited.Trim(), original, StringComparison.Ordinal) ? null : edited.Trim();

    private DateOnly? DateOnlyOrNull() =>
        ReleaseDate is { } value ? DateOnly.FromDateTime(value.Date) : null;

    partial void OnTitleChanged(string value) => RaiseDerived();

    partial void OnSummaryChanged(string value) => RaiseDerived();

    partial void OnDescriptionChanged(string value) => RaiseDerived();

    partial void OnReleaseDateChanged(DateTimeOffset? value) => RaiseDerived();

    partial void OnVisibilityChanged(GameVisibility value) => RaiseDerived();

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanSave));
    }
}
