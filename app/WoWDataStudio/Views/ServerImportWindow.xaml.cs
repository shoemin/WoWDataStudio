using System.IO;
using System.Windows;
using WoWData.Core.ServerData;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The import preview of a CSV or JSON file into a server table: every changed, added and removed row, errors
/// and warnings, then "Apply import" (one undo step of the editor).</summary>
public partial class ServerImportWindow : Window
{
    private readonly ServerDataViewModel _vm;
    private readonly ServerEditor _ed;
    private readonly string _table;
    private readonly string _file;
    private readonly RowFilter? _scope;
    private ServerImportPlan? _plan;
    private readonly bool _ready;

    public ServerImportWindow(ServerDataViewModel vm, ServerEditor ed, string table, string file, RowFilter? scope)
    {
        InitializeComponent();
        _vm = vm;
        _ed = ed;
        _table = table;
        _file = file;
        _scope = scope;
        TitleText.Text = $"Import {Path.GetFileName(file)} into {table}";
        // "the whole list": only for a list in the details (its rows) or a small table
        RemoveBox.IsEnabled = scope != null || ed.Table(table).RowsApprox <= 5000;
        _ready = true;
        Plan();
    }

    public ServerImportPlan? CurrentPlan => _plan;

    public bool RemoveMissing
    {
        get => RemoveBox.IsChecked == true;
        set => RemoveBox.IsChecked = value;
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Plan();
    }

    private void Plan()
    {
        try
        {
            _plan = ServerExchange.Plan(_ed, _table, _file, RemoveMissing, _scope);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ServerDataException or System.Text.Json.JsonException)
        {
            _plan = new ServerImportPlan { Table = _ed.Table(_table), File = _file };
            _plan.Errors.Add("The file cannot be read: " + e.Message);
        }
        var p = _plan;
        SummaryText.Text = $"{p.RowsRead:N0} rows read. " + (p.IsEmpty ? "The file changes nothing." : p.Preview.Summary);
        ErrorBox.Visibility = p.Errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = string.Join("\n", p.Errors.Take(60)) + (p.Errors.Count > 60 ? $"\n... and {p.Errors.Count - 60} more." : "");
        WarnBox.Visibility = p.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = string.Join("\n", p.Warnings.Take(100));
        ApplyButton.IsEnabled = p.Errors.Count == 0 && !p.IsEmpty;
        Diff.Show(p.Preview, "Now (with your edits)", "From the file");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.Errors.Count > 0 || _plan.IsEmpty) return;
        _vm.AfterImport(ServerExchange.Apply(_ed, _plan));
        DialogResult = true;
    }
}
