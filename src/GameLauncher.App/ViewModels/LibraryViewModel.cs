using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// The account's games. This is membership, not installation: what is on this machine lives in
/// the local install store, and the game page is where it is shown and acted on.
/// </summary>
public sealed partial class LibraryViewModel(ILibraryApi library, IApiErrorPresenter errors)
    : ViewModelBase
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    private bool _hasLoaded;

    public event EventHandler<string>? GameSelected;

    public ObservableCollection<Game> Games { get; } = [];

    public bool IsEmpty => !IsBusy && Games.Count == 0 && ErrorMessage is null && _hasLoaded;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            IReadOnlyList<Game> games = await library
                .GetLibraryAsync(cancellationToken)
                .ConfigureAwait(true);

            Games.Clear();
            foreach (Game game in games)
            {
                Games.Add(game);
            }

            _hasLoaded = true;
        }
        catch (ApiException exception)
        {
            ErrorMessage = errors.Describe(exception);
            Games.Clear();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void OpenGame(Game? game)
    {
        if (game is not null)
        {
            GameSelected?.Invoke(this, game.Slug.Length > 0 ? game.Slug : game.Id);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(Game? game, CancellationToken cancellationToken)
    {
        if (game is null)
        {
            return;
        }

        try
        {
            await library.RemoveAsync(game.Id, cancellationToken).ConfigureAwait(true);

            // Removed locally rather than by reloading: the server has confirmed it, and a
            // second round trip would make the list flicker for no new information.
            Games.Remove(game);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (ApiException exception)
        {
            ErrorMessage = errors.Describe(exception);
        }
    }
}
