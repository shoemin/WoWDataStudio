using System.Data;
using System.Windows.Controls;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>0.14.0 (experimental): the Other versions tab (see OtherVersionsViewModel).</summary>
public partial class OtherVersionsView : UserControl
{
    public OtherVersionsView() => InitializeComponent();

    /// <summary>For the screenshots: show an inner tab (0 files, 1 tables, 2 archives, 3 compare, 4 updates).</summary>
    public void ShowInnerTab(int index) => InnerTabs.SelectedIndex = index;

    /// <summary>For the screenshots: select the updates of the list that match (the picks of Preview import).</summary>
    public System.Collections.IList SelectUpdates(Func<UpdateRow, bool> pick)
    {
        UpdateGrid.SelectedItems.Clear();
        foreach (var r in UpdateGrid.Items.OfType<UpdateRow>().Where(pick)) UpdateGrid.SelectedItems.Add(r);
        return UpdateGrid.SelectedItems;
    }

    /// <summary>The generated columns show the table's own column names (their captions: a binding path cannot hold a dot).</summary>
    private void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid { ItemsSource: DataView dv } && dv.Table!.Columns[e.PropertyName] is { } col) e.Column.Header = col.Caption;
    }

    /// <summary>Picks changed: Import picked updates needs a new preview.</summary>
    private void UpdateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpdateGrid.DataContext is UpdateCatalogViewModel vm) vm.SelectionChanged(UpdateGrid.SelectedItems);
    }
}
