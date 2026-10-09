using Avalonia.Controls;
using Avalonia.Threading;
using UnVault.App.Services;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

public partial class FabLibraryView : UserControl
{
    /// <summary>Screens' worth of scrolling after which the memory it took is worth handing back.</summary>
    private const double ScreensBeforeRelief = 5;

    private FabLibraryViewModel? _viewModel;
    private readonly DispatcherTimer _scrollSettled = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private double _scrolledSinceRelief;

    public FabLibraryView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, _) => UpdateColumns();

        // A long scroll loads and drops many pictures. Once it stops, the memory that took goes back to Windows; not
        // during it, nor after a short one, as handing it back briefly pauses the app.
        Scroller.ScrollChanged += (_, e) =>
        {
            _scrolledSinceRelief += Math.Abs(e.OffsetDelta.Y);
            _scrollSettled.Stop();
            _scrollSettled.Start();
        };
        _scrollSettled.Tick += (_, _) =>
        {
            _scrollSettled.Stop();
            if (_scrolledSinceRelief < ScreensBeforeRelief * Scroller.Viewport.Height)
                return;
            _scrolledSinceRelief = 0;
            MemoryRelief.Release();
        };
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
