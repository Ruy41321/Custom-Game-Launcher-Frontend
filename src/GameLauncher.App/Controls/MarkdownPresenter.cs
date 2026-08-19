using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GameLauncher.Core.Text;

namespace GameLauncher.App.Controls;

/// <summary>
/// Renders a devlog body. The parsing is <see cref="MarkdownParser"/>'s, in Core and covered by
/// tests; this is the half that needs Avalonia, and it holds no rules of its own — the same
/// split as <c>IImageProvider</c> in D37.
///
/// A panel rather than a templated control because what it produces is a variable number of
/// children of several types, which is what a panel is.
/// </summary>
public sealed class MarkdownPresenter : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPresenter, string?>(nameof(Markdown));

    private static readonly double[] HeadingSizes = [20, 17, 15, 14, 13, 13];

    public MarkdownPresenter() => Spacing = 8;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Children.Clear();

        foreach (MarkdownBlock block in MarkdownParser.Parse(Markdown ?? string.Empty))
        {
            Children.Add(Render(block));
        }
    }

    private static Control Render(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => Heading(block),
        MarkdownBlockKind.BulletItem => Bullet(block),
        MarkdownBlockKind.Code => Code(block),
        _ => Body(block),
    };

    private static TextBlock Heading(MarkdownBlock block)
    {
        TextBlock heading = Body(block);
        heading.FontSize = HeadingSizes[Math.Clamp(block.Level, 1, HeadingSizes.Length) - 1];
        heading.FontWeight = FontWeight.SemiBold;
        heading.Margin = new Thickness(0, 6, 0, 0);
        return heading;
    }

    /// <summary>
    /// A bullet and its text in two columns, so a wrapped item lines up under itself rather
    /// than under the dot.
    /// </summary>
    private static StackPanel Bullet(MarkdownBlock block)
    {
        TextBlock text = Body(block);

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 0, 0),
        };

        row.Children.Add(new TextBlock
        {
            Text = "•",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });

        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// Kept on one line each and allowed to scroll sideways: wrapping a code block is what
    /// makes somebody's stack trace unreadable.
    /// </summary>
    private static Border Code(MarkdownBlock block) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = block.Spans.Count > 0 ? block.Spans[0].Text : string.Empty,
                FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
                FontSize = 12,
            },
        },
    };

    private static TextBlock Body(MarkdownBlock block)
    {
        TextBlock text = new() { TextWrapping = TextWrapping.Wrap };

        foreach (MarkdownSpan span in block.Spans)
        {
            text.Inlines?.Add(Inline(span));
        }

        return text;
    }

    private static Run Inline(MarkdownSpan span)
    {
        Run run = new(span.Text);

        if (span.Style.HasFlag(MarkdownStyle.Bold))
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (span.Style.HasFlag(MarkdownStyle.Italic))
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (span.Style.HasFlag(MarkdownStyle.Code))
        {
            run.FontFamily = FontFamily.Parse("Consolas, Menlo, monospace");
        }

        return run;
    }
}
