using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using GameLauncher.App.Localization;
using GameLauncher.App.Views;
using GameLauncher.Core.Localization;

namespace GameLauncher.Views.Tests;

/// <summary>
/// One question, asked of every page: does it build at all?
///
/// It exists because the answer was no. <c>Settings.axaml</c> passed a <c>{loc:Tr}</c> — which
/// is a binding, not a string (D3) — as a <c>StringFormat</c>, so constructing the page threw
/// an <see cref="InvalidCastException"/>, the shell's <c>ContentControl</c> swallowed it, and
/// the whole page rendered as an empty rectangle. Nothing failed: the launcher ran, the suite
/// was green, and every setting on that page was simply invisible until somebody looked at the
/// window. This is the cheapest test that would have said so.
/// </summary>
public sealed class ViewConstructionTests
{
    public static TheoryData<Type> EveryView() =>
    [
        typeof(Login),
        typeof(Explore),
        typeof(Library),
        typeof(GameDetail),
        typeof(Developer),
        typeof(Settings),
        typeof(ChangePassword),
        typeof(MainWindow),
    ];

    [Theory]
    [MemberData(nameof(EveryView))]
    public void EveryViewCanBeBuilt(Type view)
    {
        AvaloniaFixture.EnsureStarted();

        object? built = Activator.CreateInstance(view);

        Assert.IsAssignableFrom<Control>(built);
    }
}

/// <summary>
/// Avalonia is process-wide and must be set up exactly once, before any UI type is touched.
/// The real <see cref="GameLauncher.App.App"/> is used rather than a stand-in, because the
/// styles a view resolves against are part of what is being tested.
/// </summary>
internal static class AvaloniaFixture
{
    private static readonly Lock Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            AppBuilder.Configure<GameLauncher.App.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                .SetupWithoutStarting();

            // `{loc:Tr}` reads the source the running app initialises at start-up (D3); without
            // it every localized property on every page would throw for a reason that has
            // nothing to do with the page.
            LocalizationSource.Initialize(new ResourceManagerLocalizationService("en"));

            _started = true;
        }
    }
}
