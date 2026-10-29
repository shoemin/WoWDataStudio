using System.Data;
using System.Windows.Controls;

namespace WoWDataStudio.Views;

/// <summary>The read-only Data tables tab of another version (<see cref="ViewModels.VersionTablesViewModel"/>).</summary>
public partial class VersionTablesView : UserControl
{
    public VersionTablesView() => InitializeComponent();

    /// <summary>A column's header is the table's column name (the DataTable key has dots replaced).</summary>
    private void OnAutoColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (DataContext is ViewModels.VersionTablesViewModel { Rows.Table: { } t } && t.Columns[e.PropertyName] is { } c) e.Column.Header = c.Caption.Replace("_", "__");   // "__": a header shows "_" (one is an access key)
    }
}
