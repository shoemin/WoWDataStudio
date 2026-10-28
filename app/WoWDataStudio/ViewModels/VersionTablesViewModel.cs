using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Versions;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A table of the read-only Data tables tab.</summary>
public sealed record VersionTableItem(string Name, int Fields, string Columns)
{
    public string Text => Name;
    public string Tip => $"{Name}: {Fields} fields, {Columns}";
}

/// <summary>
/// The Data tables tab while another version is open read only: its DB2 tables (names from the executable, columns
/// named where our layout file for the build knows them, else col_N), a row search, and the rows exported as CSV or
/// JSON to a folder you choose (the same as "wds dbc tables / rows / get / export --version"). Nothing can be saved:
/// "Save to my workspace" and "Copy to server..." are greyed out with the read-only reason.
/// </summary>
public sealed partial class VersionTablesViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private List<VersionTableItem> _all = new();
    private VersionTable? _table;

    public VersionTablesViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
    }

    public ObservableCollection<VersionTableItem> Tables { get; } = new();
    [ObservableProperty] private VersionTableItem? _selectedTable;
    [ObservableProperty] private string _tableFilter = "";
    [ObservableProperty] private DataView? _rows;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _tableNote = "";

    /// <summary>Why "Save to my workspace" and "Copy to server..." are greyed out.</summary>
    public string? WriteRefusal => _session.WriteRefusal;
    public string Header => $"Data tables of {_session.Profile?.VersionBuild} (read only)";
    public bool HasTable => _table != null;

    /// <summary>The table shown now (tests, screenshots).</summary>
    public VersionTable? Table => _table;
    /// <summary>The rows the search shows (indexes into <see cref="Table"/>).</summary>
    public List<int> Shown { get; private set; } = new();

    /// <summary>Lists the tables (once; later calls wait for the same load).</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private Task? _loading;

    private async Task LoadAsync()
    {
        var oc = _session.Other;
        if (oc == null) return;
        _all = await Task.Run(() =>
        {
            var layouts = WoWData.Core.Db2.Db2Layouts.ForBuild(_session.Profile?.Build ?? oc.Copy.Build);
            return oc.TableNames().Select(n => new VersionTableItem(n, oc.Catalog.Get(n)?.FieldCount ?? 0, layouts?.Get(n) != null ? "columns named by our layout file" : "raw columns (col_N)")).ToList();
        });
        ApplyTableFilter();
        Status = $"{_all.Count:N0} tables of {_session.Profile?.VersionBuild}. Click one to read it (a table is read when you open it). Read only.";
    }

    partial void OnTableFilterChanged(string value) => ApplyTableFilter();

    private void ApplyTableFilter()
    {
        var words = TableFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Tables.Clear();
        foreach (var t in _all.Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))) Tables.Add(t);
    }

    partial void OnSelectedTableChanged(VersionTableItem? value)
    {
        if (value != null) _ = SelectTableAsync(value.Name);
    }

    /// <summary>Reads a table (on a background thread) and shows its rows.</summary>
    public async Task SelectTableAsync(string name)
    {
        var oc = _session.Other;
        if (oc == null) return;
        Rows = null;
        _table = null;
        OnPropertyChanged(nameof(HasTable));
        TableNote = $"Reading {name}...";
        try
        {
            var build = _session.Profile?.Build;
            var t = await Task.Run(() => oc.TableView(name, build));
            if (t == null)
            {
                TableNote = $"{name} could not be read (not on this computer, or encrypted).";
                return;
            }
            _table = t;
            OnPropertyChanged(nameof(HasTable));
            TableNote = (t.IsRaw ? $"{name} has no known layout for {_session.Profile?.VersionBuild} yet: every field is a raw number (col_N)." : $"{name}: {t.RowCount:N0} rows, {t.Coverage}.")
                        + (t.Note.Length > 0 ? " " + t.Note + "." : "") + " Read only.";
            ApplySearch();
        }
        catch (Exception e) when (e is WoWData.Core.Db2.Db2FormatException or ClientFileException or IOException or InvalidDataException)
        {
            TableNote = $"{name} could not be read: {e.Message}";
        }
    }

    partial void OnSearchChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var t = _table;
        if (t == null) return;
        Shown = VersionTableExport.Find(t, Search);
        var dt = new DataTable(t.Name);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in t.Columns)
        {
            var key = c.Replace(".", "_");
            while (!used.Add(key)) key += "_";
            dt.Columns.Add(key, typeof(string)).Caption = c;
        }
        foreach (var i in Shown.Take(50_000)) dt.Rows.Add(t.RowAt(i).Select(v => (object)(v.IndexOf('\n') < 0 ? v : v.Replace("\r\n", "\n").Replace("\n", " / "))).ToArray());
        Rows = dt.DefaultView;
        Status = Shown.Count == t.RowCount ? $"{t.RowCount:N0} rows" : $"{Shown.Count:N0} of {t.RowCount:N0} rows" + (Shown.Count > 50_000 ? " (the first 50,000 are shown; Export saves all)" : "");
    }

    /// <summary>"Export rows...": the rows the search shows, as CSV or JSON, to a folder outside the client.</summary>
    [RelayCommand]
    private async Task Export()
    {
        var t = _table;
        if (t == null) return;
        var dir = Path.Combine(_session.Settings.DefaultExportDir, _session.Profile?.Id ?? "other", "Tables");
        Directory.CreateDirectory(dir);
        var file = Dialogs.PickSaveFile("Export rows", "CSV for spreadsheets (*.csv)|*.csv|JSON (*.json)|*.json", t.Name + ".csv", dir);
        if (file == null) return;
        if (await ExportToAsync(file)) _main.Toast($"Exported {Shown.Count:N0} rows of {t.Name} to {file}.");
    }

    /// <summary>Exports the shown rows (also used by the screenshots and tests); refuses the client's own folders.</summary>
    public async Task<bool> ExportToAsync(string file)
    {
        var t = _table;
        if (t == null) return false;
        try
        {
            WoWData.Core.M2.Modern.ModernModelExport.CheckOutsideClient(_session, file);
            var rows = Shown;
            await Task.Run(() => VersionTableExport.Write(t, rows, file));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Export rows", e.Message);
            return false;
        }
    }

    /// <summary>Never runs (the buttons are greyed out with <see cref="WriteRefusal"/>); kept so the buttons look like the 1.12 tab's.</summary>
    [RelayCommand]
    private void Save() { }

    [RelayCommand]
    private void CopyToServer() { }
}
