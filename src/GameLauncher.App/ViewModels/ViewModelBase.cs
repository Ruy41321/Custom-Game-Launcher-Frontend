using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Base for every view model. Change notification comes from the CommunityToolkit source
/// generators — <c>[ObservableProperty]</c> and <c>[RelayCommand]</c> — so no view model
/// implements <c>INotifyPropertyChanged</c> by hand.
/// </summary>
public abstract class ViewModelBase : ObservableObject;
