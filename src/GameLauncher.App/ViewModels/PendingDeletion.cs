namespace GameLauncher.App.ViewModels;

/// <summary>
/// A deletion the user has been asked about and has not yet confirmed.
///
/// Deleting a build, a version, a picture or a devlog entry is irreversible — the server has no
/// undo route, and the collector eventually reclaims the bytes — so none of them happens on one
/// click. <see cref="Prompt"/> is the sentence that says **what disappears**, not merely that
/// something will: "this version and its 3 builds" is actionable and "are you sure?" is not.
///
/// It is held as *state on the view model* rather than shown through a dialog service. A dialog
/// would be a second thing behind an interface that no test can drive, and D32 spends that
/// budget on the file picker alone. As state, a test arms the deletion, reads exactly what the
/// user is being told, and then confirms or cancels — so the wording is covered too, which is
/// the part that actually protects somebody's build.
/// </summary>
public sealed record PendingDeletion(string Prompt, Func<CancellationToken, Task> ConfirmAsync);
