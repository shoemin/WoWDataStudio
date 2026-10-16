using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WoWData.Core.Dbc;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The Data tables tab. The grid's columns are made here for each table (one per layout column, with its filter
/// box in the header); sorting and editing go through the view model.</summary>
public partial class DbcEditorView : UserControl
{
    private DbcEditorViewModel? _vm;
    private bool _buildingColumns;

    public DbcEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.ColumnsChanged -= BuildColumns;
                _vm.ScrollRequested -= ScrollTo;
            }
            _vm = e.NewValue as DbcEditorViewModel;
            if (_vm == null) return;
            _vm.ColumnsChanged += BuildColumns;
            _vm.ScrollRequested += ScrollTo;
            _vm.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName == nameof(DbcEditorViewModel.SelectedTable) && _vm.SelectedTable != null)
                    Dispatcher.BeginInvoke(() => TableList.ScrollIntoView(_vm.SelectedTable), System.Windows.Threading.DispatcherPriority.Background);
                if (pe.PropertyName == nameof(DbcEditorViewModel.SelectedRow)) DetailsScroll.ScrollToTop();
            };
            var view = CollectionViewSource.GetDefaultView(_vm.Tables);
            if (view.GroupDescriptions.Count == 0) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DbcTableItem.Category)));
            TableList.ItemsSource = view;
            BuildColumns();
        };
    }

    /// <summary>The grid (screenshots).</summary>
    public DataGrid DataGrid => Grid;

    /// <summary>Rebuild the columns (their filter boxes show the view model's filters).</summary>
    public void Refilter() => BuildColumns();

    /// <summary>Scroll the details panel to a value (screenshots, and after a refused edit).</summary>
    public void ShowField(string name)
    {
        if (_vm == null) return;
        var f = _vm.Fields.FirstOrDefault(x => x.Name == name);
        if (f == null || FieldList.ItemContainerGenerator.ContainerFromItem(f) is not FrameworkElement el) return;
        el.BringIntoView(new Rect(0, -60, Math.Max(1, el.ActualWidth), el.ActualHeight + 120));
    }

    private void ScrollTo(DbcRowView row)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Grid.ScrollIntoView(row);
            Grid.SelectedItem = row;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BuildColumns()
    {
        if (_vm == null) return;
        _buildingColumns = true;
        try
        {
            Grid.Columns.Clear();
            foreach (var gc in _vm.GridColumns)
            {
                var c = gc.Column;
                var col = new DataGridTemplateColumn
                {
                    Header = Header(gc),
                    CellTemplate = CellTemplateFor(gc.Index, false, c),
                    CellEditingTemplate = CellTemplateFor(gc.Index, true, c),
                    SortMemberPath = gc.Index.ToString(),
                    CanUserSort = true,
                    Width = new DataGridLength(ColumnWidth(c)),
                    IsReadOnly = false,
                };
                if (_vm.SortColumn == gc.Index) col.SortDirection = _vm.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
                Grid.Columns.Add(col);
            }
            // the row's name (its label column) right after the ID, both kept in view while scrolling sideways
            var label = _vm.Table?.Layout.LabelColumn;
            var key = _vm.Table?.Layout.KeyColumn;
            int frozen = 0;
            if (key != null && key.Index < Grid.Columns.Count)
            {
                Grid.Columns[key.Index].DisplayIndex = 0;
                frozen = 1;
            }
            if (label != null && label.Index < Grid.Columns.Count && Grid.Columns.Count > 3)
            {
                Grid.Columns[label.Index].DisplayIndex = frozen;
                frozen++;
            }
            Grid.FrozenColumnCount = frozen;
            // few columns: the last one takes the rest of the width
            if (Grid.Columns.Count is > 0 and <= 4) Grid.Columns.OrderBy(c => c.DisplayIndex).Last().Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }
        finally
        {
            _buildingColumns = false;
        }
    }

    private static double ColumnWidth(DbcColumn c) => c.IsKey ? 70
        : c.Storage == DbcStorage.Loc ? 220
        : c.IsString ? (c.Semantic == DbcSemantic.Plain ? 170 : 240)
        : c.ForeignKey != null || c.Values != null ? 150
        : c.IsFloat ? 90 : 80;

    private FrameworkElement Header(DbcGridColumn gc)
    {
        var name = new TextBlock { Text = gc.Header, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!gc.Column.Verified)
        {
            name.FontStyle = FontStyles.Italic;
            name.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3));
        }
        var box = new TextBox { Text = _vm!.ColumnFilter(gc.Index), FontSize = 11, Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(0, 2, 0, 0), MinWidth = 40, Tag = gc.Index };
        box.TextChanged += (s, _) =>
        {
            if (!_buildingColumns && s is TextBox t && t.Tag is int i) _vm?.SetColumnFilter(i, t.Text);
        };
        box.ToolTip = "Filter this column: text, or for numbers 5, >5, <10, 5-10, !0.";
        var panel = new StackPanel { ToolTip = gc.Tip, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(name);
        panel.Children.Add(box);
        return panel;
    }

    private static DataTemplate CellTemplateFor(int index, bool edit, DbcColumn c)
    {
        var t = new DataTemplate();
        if (edit)
        {
            var box = new FrameworkElementFactory(typeof(TextBox));
            box.SetBinding(TextBox.TextProperty, new Binding($"Edit[{index}]") { Mode = BindingMode.OneWay });
            box.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
            box.SetValue(TextBox.PaddingProperty, new Thickness(2, 0, 2, 0));
            t.VisualTree = box;
        }
        else
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding($"Display[{index}]") { Mode = BindingMode.OneWay });
            tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            tb.SetValue(TextBlock.MarginProperty, new Thickness(3, 0, 3, 0));
            tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            if (c.IsNumber && c.ForeignKey == null && c.Values == null) tb.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            t.VisualTree = tb;
        }
        return t;
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_vm == null || !int.TryParse(e.Column.SortMemberPath, out int idx)) return;
        _vm.Sort(idx);
        foreach (var c in Grid.Columns) c.SortDirection = null;
        if (_vm.SortColumn == idx) e.Column.SortDirection = _vm.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
    }

    private void Grid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (FindChild<TextBox>(e.EditingElement) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (_vm == null || e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not DbcRowView row || !int.TryParse(e.Column.SortMemberPath, out int idx)) return;
        var box = FindChild<TextBox>(e.EditingElement);
        if (box == null) return;
        var text = box.Text;
        // the change is made after the grid left the edit mode (the cell then shows the new value, or the old one)
        Dispatcher.BeginInvoke(() =>
        {
            if (!_vm.TryEditCell(row, idx, text)) row.Refresh();
        });
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm != null) _vm.SelectedRowsFromView(Grid.SelectedItems.OfType<DbcRowView>().ToList());
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is { } m)
        {
            m.PlacementTarget = ExportButton;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.DataContext = DataContext;
            m.IsOpen = true;
        }
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        if (box.AcceptsReturn && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        CommitField(box);
        e.Handled = true;
    }

    private void Field_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitField(box);
    }

    private static void CommitField(TextBox box)
    {
        if (box.DataContext is not DbcFieldView f) return;
        if (f.Text == box.Text) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        f.CommitCommand.Execute(null);
    }

    private static T? FindChild<T>(DependencyObject? d) where T : DependencyObject
    {
        if (d == null) return null;
        if (d is T t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }
}
