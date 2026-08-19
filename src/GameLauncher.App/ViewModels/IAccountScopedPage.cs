namespace GameLauncher.App.ViewModels;

/// <summary>
/// A page whose contents belong to one account. The shell owns its pages for the lifetime of
/// the window, so nothing on them is thrown away when they stop being shown — which is exactly
/// right while one person is signed in and exactly wrong the moment somebody else signs in:
/// the dashboard was still showing the previous account's game, and the library and the game
/// page hold an account's state in the same way.
///
/// The reset is one rule for every page rather than a fix on the page where it was noticed,
/// and it draws the line at the *account's* data: what a page fetched with the account's token
/// goes, and what belongs to this machine — the install directory, the theme, the language,
/// whether crash reports are sent — stays, because none of it changes when somebody else signs
/// in on the same computer.
/// </summary>
public interface IAccountScopedPage
{
    /// <summary>
    /// Puts the page back to the state it had before anybody signed in. Called by the shell
    /// when the account changes, which includes signing out — never on a token rotation, which
    /// raises the same event with the same account (see <c>MainWindowViewModel</c>).
    ///
    /// Synchronous and never a request: it is the removal of what the previous account's
    /// requests brought back, and it runs while nobody is signed in.
    /// </summary>
    void ResetForAccountChange();
}
