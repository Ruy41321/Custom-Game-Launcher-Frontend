using System.Text.RegularExpressions;

namespace GameLauncher.Core.Tests.Conventions;

/// <summary>
/// Guards the rule that no user-visible string may be written directly into a view.
/// Localization only works if it is impossible to bypass, and a code review will not catch
/// every <c>Text="Install"</c> that slips in.
/// </summary>
public sealed partial class NoHardcodedUiStringsTests
{
    /// <summary>Attributes whose value is rendered to the user.</summary>
    private static readonly string[] UserVisibleAttributes =
    [
        "Text", "Content", "Title", "Watermark", "Header", "PlaceholderText", "ToolTip.Tip",
    ];

    [Fact]
    public void NoViewContainsALiteralUserVisibleString()
    {
        string sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        string[] views = Directory.GetFiles(sourceRoot, "*.axaml", SearchOption.AllDirectories);

        Assert.NotEmpty(views);

        List<string> violations = [];

        foreach (string view in views)
        {
            string[] lines = File.ReadAllLines(view);
            for (int index = 0; index < lines.Length; index++)
            {
                foreach (Match match in LiteralAttributeValue().Matches(lines[index]))
                {
                    string attribute = match.Groups["attribute"].Value;
                    string value = match.Groups["value"].Value;

                    if (!UserVisibleAttributes.Contains(attribute, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    // A markup extension or binding is exactly what we want to see here.
                    if (value.StartsWith('{') || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    violations.Add(
                        $"{Path.GetFileName(view)}:{index + 1}  {attribute}=\"{value}\"");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "User-visible strings must go through {loc:Tr} or a view model property:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [GeneratedRegex(@"(?<attribute>[\w.]+)\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex LiteralAttributeValue();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameLauncher.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
