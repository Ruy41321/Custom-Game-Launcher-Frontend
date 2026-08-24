using Avalonia.Controls;

namespace GameLauncher.App.Views;

/// <summary>
/// Code-behind holds no logic by design: everything lives in
/// <see cref="ViewModels.MainWindowViewModel"/>, which is testable without a UI.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
