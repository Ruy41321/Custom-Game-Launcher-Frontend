using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// One row of the publisher's version list.
///
/// It exists because the row has to say two things <see cref="GameVersion"/> cannot carry.
/// Which builds hang off it: a version is a wire record that knows nothing about builds — the
/// server sends the two lists side by side — so joining them is the page's job, and a view
/// model is where that join can be read by a test instead of inferred from a template. And
/// whether it is published, as something that <em>changes</em>: publishing a version is now a
/// button on this row, and the row it changes must be able to say so without the list being
/// rebuilt underneath the publisher's cursor.
/// </summary>
public sealed partial class VersionRowViewModel : ObservableObject
{
    public VersionRowViewModel(GameVersion version, IEnumerable<GameBuild> builds)
    {
        _version = version;
        _buildsSummary = SummarizeBuilds(builds);
    }

    [ObservableProperty]
    private GameVersion _version;

    /// <summary>
    /// The builds under this version, named. A build that was given a label shows the label;
    /// one that was not falls back to what tells it apart anyway — its platform and
    /// architecture. Empty when the version has no builds yet, which is a row that should say
    /// nothing rather than "none".
    /// </summary>
    [ObservableProperty]
    private string _buildsSummary;

    public string Id => Version.Id;

    public string Semver => Version.Semver;

    public BuildStage Stage => Version.Stage;

    public bool Published => Version.Published;

    public bool HasBuilds => BuildsSummary.Length > 0;

    /// <summary>Recomputes the summary in place, so a publish does not rebuild the list.</summary>
    public void RefreshBuilds(IEnumerable<GameBuild> builds) => BuildsSummary =
        SummarizeBuilds(builds);

    private static string SummarizeBuilds(IEnumerable<GameBuild> builds) => string.Join(
        " · ",
        builds.Select(build => build.Name.Length > 0
            ? build.Name
            : $"{build.Platform} {build.Architecture}"));

    partial void OnVersionChanged(GameVersion value)
    {
        OnPropertyChanged(nameof(Semver));
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(Published));
    }

    partial void OnBuildsSummaryChanged(string value) => OnPropertyChanged(nameof(HasBuilds));
}
