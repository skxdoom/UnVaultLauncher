using Avalonia;
using Avalonia.Controls;
using Unvault.App.ViewModels;

namespace Unvault.App.Views;

public partial class FabItemView : UserControl
{
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
