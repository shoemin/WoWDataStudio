using System.Windows;
using System.Windows.Controls;
using WoWData.Core.Dbc;

namespace WoWDataStudio.Views;

/// <summary>The rows of a table comparison (changed / added / removed) and, for the selected row, every value before and
/// after. Used by the compare window, the save confirmation and the import preview.</summary>
public partial class DbcDiffView : UserControl
{
    public DbcDiffView()
    {
        InitializeComponent();
    }

    public void Show(DbcDiffResult diff, string beforeLabel = "Before (the game)", string afterLabel = "After (yours)")
    {
        RowList.ItemsSource = diff.Rows;
        if (CellList.View is GridView gv && gv.Columns.Count == 3)
        {
            gv.Columns[1].Header = beforeLabel;
            gv.Columns[2].Header = afterLabel;
        }
        CellList.ItemsSource = null;
        if (diff.Rows.Count > 0) RowList.SelectedIndex = 0;
    }

    private void RowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowList.SelectedItem is not DbcRowDiff r)
        {
            CellList.ItemsSource = null;
            return;
        }
        CellList.ItemsSource = r.Cells;
        CellTitle.Text = r.Kind switch
        {
            DbcRowChangeKind.Added => $"ID {r.Key} ({r.Label}) is a new row.",
            DbcRowChangeKind.Removed => $"ID {r.Key} ({r.Label}) is removed.",
            _ => $"{r.Label} (ID {r.Key}): {r.Cells.Count} value{(r.Cells.Count == 1 ? "" : "s")} changed.",
        };
    }
}
