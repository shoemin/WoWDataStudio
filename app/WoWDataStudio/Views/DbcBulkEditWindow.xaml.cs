using System.Windows;
using WoWData.Core.Dbc;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Bulk edit...": set a column of the selected rows to a value, add to it or multiply it.</summary>
public partial class DbcBulkEditWindow : Window
{
    private readonly DbcEditorViewModel _vm;
    private readonly IReadOnlyList<DbcRow> _rows;

    public DbcBulkEditWindow(DbcEditorViewModel vm, IReadOnlyList<DbcRow> rows)
    {
        InitializeComponent();
        _vm = vm;
        _rows = rows;
        TitleText.Text = $"Bulk edit: {rows.Count} row{(rows.Count == 1 ? "" : "s")} of {vm.Table?.Name}";
        ApplyButton.Content = $"Apply to {rows.Count} row{(rows.Count == 1 ? "" : "s")}";
        ColumnBox.ItemsSource = vm.Table?.Layout.Columns.Where(c => !c.IsKey).ToList();
        ColumnBox.SelectedIndex = 0;
    }

    public DbcColumn? Column
    {
        get => ColumnBox.SelectedItem as DbcColumn;
        set => ColumnBox.SelectedItem = value;
    }

    public BulkOperation Operation
    {
        get => AddRadio.IsChecked == true ? BulkOperation.Add : MultiplyRadio.IsChecked == true ? BulkOperation.Multiply : BulkOperation.Set;
        set
        {
            SetRadio.IsChecked = value == BulkOperation.Set;
            AddRadio.IsChecked = value == BulkOperation.Add;
            MultiplyRadio.IsChecked = value == BulkOperation.Multiply;
        }
    }

    public string Value
    {
        get => ValueBox.Text;
        set => ValueBox.Text = value;
    }

    private void Column_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var c = Column;
        if (c == null) return;
        ColumnHint.Text = $"{c.TypeText}." + (c.Hint.Length > 0 ? " " + c.Hint : "") + (c.IsString ? " Texts can only be set." : "") +
                          (c.Storage == DbcStorage.Loc ? $" (The {DbcTable.LocaleNames[_vm.Locale]} text; the other languages are kept.)" : "");
        AddRadio.IsEnabled = MultiplyRadio.IsEnabled = c.IsNumber;
        if (!c.IsNumber) SetRadio.IsChecked = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var c = Column;
        if (c == null) return;
        var (ok, msg) = _vm.BulkEdit(c, Operation, Value, _rows);
        ResultText.Text = msg;
        if (ok) DialogResult = true;
    }
}
