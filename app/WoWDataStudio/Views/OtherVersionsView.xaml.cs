using System.Data;
using System.Windows.Controls;

namespace WoWDataStudio.Views;

/// <summary>0.14.0 (experimental): the Other versions tab (see OtherVersionsViewModel).</summary>
public partial class OtherVersionsView : UserControl
{
    public OtherVersionsView() => InitializeComponent();

    /// <summary>For the screenshots: show an inner tab (0 files, 1 tables, 2 archives, 3 compare).</summary>
    public void ShowInnerTab(int index) => InnerTabs.SelectedIndex = index;

    /// <summary>The generated columns show the table's own column names (their captions: a binding path cannot hold a dot).</summary>
    private void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid { ItemsSource: DataView dv } && dv.Table!.Columns[e.PropertyName] is { } col) e.Column.Header = col.Caption;
    }
}
