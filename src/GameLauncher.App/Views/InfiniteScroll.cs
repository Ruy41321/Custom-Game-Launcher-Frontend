using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace GameLauncher.App.Views;

/// <summary>
/// Turns scrolling near the bottom of a <see cref="ScrollViewer"/> into a command, and a list
/// that was replaced into a return to the top.
/// </summary>
/// <remarks>
/// An attached behaviour rather than a code-behind handler, because a code-behind with logic in
/// it is a rule no test can press (§2 of CLAUDE.md) — and because what it does is the same for
/// any list. It deliberately holds no policy: it fires the command whenever the viewport is
/// near the end, and whether that means anything is the view model's to decide. That is why
/// <c>LoadMoreCommand</c> refuses cheaply and silently rather than assuming it is called once
/// per page.
/// </remarks>
public static class InfiniteScroll
{
    /// <summary>
    /// How close to the bottom counts as the bottom. Roughly one card's height, so the next
    /// page is asked for while there is still something to read.
    /// </summary>
    private const double Threshold = 200;

    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, ICommand?>(
            "Command", typeof(InfiniteScroll));

    /// <summary>
    /// The collection being shown. Watched only for the reset a replacement raises: after a new
    /// search the offset would otherwise stay where the previous results were, which reads as a
    /// page that has scrolled itself somewhere for no reason.
    /// </summary>
    public static readonly AttachedProperty<INotifyCollectionChanged?> ItemsProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, INotifyCollectionChanged?>(
            "Items", typeof(InfiniteScroll));

    /// <summary>
    /// Which viewer is showing which collection. A dictionary rather than a closure so the
    /// handler can be removed by reference below; the entries live as long as the page does.
    /// </summary>
    private static readonly Dictionary<INotifyCollectionChanged, ScrollViewer> Viewers = [];

    static InfiniteScroll()
    {
        CommandProperty.Changed.AddClassHandler<ScrollViewer>(OnCommandChanged);
        ItemsProperty.Changed.AddClassHandler<ScrollViewer>(OnItemsChanged);
    }

    public static void SetCommand(ScrollViewer element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(ScrollViewer element) => element.GetValue(CommandProperty);

    public static void SetItems(ScrollViewer element, INotifyCollectionChanged? value) =>
        element.SetValue(ItemsProperty, value);

    public static INotifyCollectionChanged? GetItems(ScrollViewer element) =>
        element.GetValue(ItemsProperty);

    private static void OnCommandChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs args)
    {
        viewer.ScrollChanged -= OnScrollChanged;

        if (args.NewValue is ICommand)
        {
            viewer.ScrollChanged += OnScrollChanged;
        }
    }

    private static void OnItemsChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.OldValue is INotifyCollectionChanged previous)
        {
            previous.CollectionChanged -= OnItemsCollectionChanged;
        }

        if (args.NewValue is INotifyCollectionChanged current)
        {
            current.CollectionChanged += OnItemsCollectionChanged;
            Viewers[current] = viewer;
        }
    }

    private static void OnItemsCollectionChanged(
        object? sender, NotifyCollectionChangedEventArgs args)
    {
        // Only a reset, which is what clearing the list raises. An append must leave the offset
        // exactly where it is — moving it is the jump this whole feature exists not to cause.
        if (args.Action == NotifyCollectionChangedAction.Reset &&
            sender is INotifyCollectionChanged items &&
            Viewers.TryGetValue(items, out ScrollViewer? viewer))
        {
            viewer.Offset = viewer.Offset.WithY(0);
        }
    }

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is not ScrollViewer viewer || GetCommand(viewer) is not { } command)
        {
            return;
        }

        // A viewport taller than its content is already at the end, and asking there is how a
        // first page shorter than the window loads its second one.
        double remaining = viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height;
        if (remaining <= Threshold && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
