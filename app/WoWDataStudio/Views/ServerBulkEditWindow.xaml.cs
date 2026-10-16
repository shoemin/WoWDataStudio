using System.Windows;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Bulk edit..." of server rows: set a column of every row of a list to a value, add to it or multiply it.</summary>
public partial class ServerBulkEditWindow : Window
{
    private readonly ServerDataViewModel _vm;
    private readonly WorldTable _table;
    private readonly IReadOnlyList<string> _keys;

    public ServerBulkEditWindow(ServerDataViewModel vm, WorldTable table, IReadOnlyList<string> keys)
    {
        InitializeComponent();
        _vm = vm;
        _table = table;
        _keys = keys;
        TitleText.Text = $"Bulk edit: {keys.Count} row{(keys.Count == 1 ? "" : "s")} of {table.Name}";
        ApplyButton.Content = $"Apply to {keys.Count} row{(keys.Count == 1 ? "" : "s")}";
        ColumnBox.ItemsSource = table.Columns.Where(c => !table.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        ColumnBox.SelectedIndex = 0;
    }

    public WorldColumn? Column
    {
        get => ColumnBox.SelectedItem as WorldColumn;
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
        var info = _vm.Service?.Info(_table.Name)?.Column(c.Name);
        ColumnHint.Text = c.ColumnType + (info?.Hint is { Length: > 0 } h ? ". " + h : ".") + (c.IsNumber ? "" : " Texts can only be set.");
        AddRadio.IsEnabled = MultiplyRadio.IsEnabled = c.IsNumber;
        if (!c.IsNumber) SetRadio.IsChecked = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var c = Column;
        if (c == null) return;
        var (ok, msg) = _vm.BulkEdit(_table.Name, _keys, c.Name, Operation, Value);
        ResultText.Text = msg;
        if (ok) DialogResult = true;
    }
}
