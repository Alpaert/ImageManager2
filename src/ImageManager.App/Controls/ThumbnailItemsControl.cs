using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ImageManager.App.Controls;

/// <summary>Reuses a thumbnail's template controls along with its item container.</summary>
public sealed class ThumbnailItemsControl : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new ThumbnailContainer();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is not ThumbnailContainer thumbnail)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            return;
        }

        var template = ItemTemplate;
        if (!ReferenceEquals(thumbnail.BuiltTemplate, template))
        {
            thumbnail.Child = template?.Build(item);
            thumbnail.BuiltTemplate = template;
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is not ThumbnailContainer thumbnail)
        {
            base.ClearContainerForItemOverride(container);
            return;
        }

        // The panel has already detached the container. Avalonia closes the
        // card's context menu and AsyncThumbnailImage releases its bitmap on detach.
        // Null the inherited item while retaining the constructed template.
        thumbnail.DataContext = null;
    }

    private sealed class ThumbnailContainer : Decorator
    {
        public IDataTemplate? BuiltTemplate { get; set; }
    }
}
