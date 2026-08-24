using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GameLauncher.App.ViewModels;

namespace GameLauncher.App;

/// <summary>
/// Maps a view model to its view by convention: <c>…ViewModels.FooViewModel</c> resolves to
/// <c>…Views.Foo</c>. Views therefore never need to be wired up one by one.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param)
    {
        if (param is null)
        {
            return new TextBlock { Text = "No view model." };
        }

        string name = param.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", string.Empty, StringComparison.Ordinal);

        Type? type = Type.GetType(name);
        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = $"View not found: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
