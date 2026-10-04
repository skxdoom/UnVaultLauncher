using Avalonia.Controls;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

public partial class FabLibraryView : UserControl
{
    private FabLibraryViewModel? _viewModel;

    public FabLibraryView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, _) => UpdateColumns();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.FilterChanged -= OnFilterChanged;
        _viewModel = DataContext as FabLibraryViewModel;
        if (_viewModel is not null)
            _viewModel.FilterChanged += OnFilterChanged;
        UpdateColumns();
    }

    /// <summary>As many columns as fit at the minimum tile width; the rows then stretch the tiles to fill the page.</summary>
    private void UpdateColumns()
    {
        if (_viewModel is not null && Page.Bounds.Width > 0)
            _viewModel.Columns = FabRowView.ColumnsFor(Page.Bounds.Width);
    }

    // New results start at the top, rather than wherever the previous list was scrolled to.
    private void OnFilterChanged(object? sender, EventArgs e) => Scroller.Offset = default;
}
