using Avalonia;
using Avalonia.Controls;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

/// <summary>
/// One row of the Fab grid. Tiles keep a fixed size; the space left over in the page's width is shared out between
/// them, so a full row always spans the page and a short last row lines up with the columns above. Keeps its tiles
/// and hands them new items when the row is reused (scrolling) or the results change (searching): re-binding a tile
/// is far cheaper than building one.
/// </summary>
public sealed class FabRowView : Panel
{
    public const double TileWidth = 236;

    /// <summary>A 16:9 picture (133) plus title, author, versions with status marks, and the buttons.</summary>
    public const double TileHeight = 133 + 118;

    /// <summary>The least space between tiles; any more the width allows is added to it.</summary>
    public const double MinGap = 14;

    /// <summary>Space between rows.</summary>
    public const double RowGap = 14;

    /// <summary>How many columns fit a page this wide.</summary>
    public static int ColumnsFor(double width) => Math.Max(1, (int)((width + MinGap) / (TileWidth + MinGap)));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var items = (DataContext as FabItemRow)?.Items ?? [];
        for (int i = 0; i < items.Count; i++)
        {
            if (i < Children.Count)
                Children[i].DataContext = items[i];
            else
                Children.Add(new FabItemView { DataContext = items[i] });
        }
        while (Children.Count > items.Count)
            Children.RemoveAt(Children.Count - 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(TileWidth, TileHeight));
        double width = double.IsInfinity(availableSize.Width) ? Columns * TileWidth + (Columns - 1) * MinGap : availableSize.Width;
        return new Size(width, TileHeight + RowGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = Columns;
        double gap = columns > 1 ? Math.Max(MinGap, (finalSize.Width - columns * TileWidth) / (columns - 1)) : 0;
        for (int i = 0; i < Children.Count; i++)
            Children[i].Arrange(new Rect(Math.Round(i * (TileWidth + gap)), 0, TileWidth, TileHeight));
        return finalSize;
    }

    /// <summary>The page's column count, not this row's tile count, so a short last row keeps the grid's spacing.</summary>
    private int Columns => Math.Max(1, (DataContext as FabItemRow)?.Columns ?? Children.Count);
}
