using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

public partial class FabItemView : UserControl
{
    /// <summary>A title too long for its one line ends at a whole word, then " …".</summary>
    public static TextTrimming TitleTrimming { get; } = new TextTrailingTrimming(" …", isWordBased: true);

    private FabItemViewModel? _shown;
    private bool _attached;

    public FabItemView()
    {
        InitializeComponent();
    }

    // The grid only creates tiles for rows on screen and reuses them while scrolling, so a tile's picture is
    // wanted exactly while it is attached and showing an item.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Track();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Track();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Track();
    }

    private void Track()
    {
        var item = _attached ? DataContext as FabItemViewModel : null;
        if (item == _shown)
            return;
        _shown?.HideThumbnail();
        _shown = item;
        _shown?.ShowThumbnail();
    }
}
