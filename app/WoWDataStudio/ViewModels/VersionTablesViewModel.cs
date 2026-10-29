using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Versions;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A table of the read-only Data tables tab.</summary>
public sealed record VersionTableItem(string Name, int Fields, string Columns, TableNaming? Naming = null)
{
    public string Text => Name;
    public string Tip => $"{Name}: {Fields} fields, {Columns}" + (Naming is { Fallback.Length: > 0 } n ? $" (not WoWDBDefs: {n.Fallback})" : "");
}

/// <summary>
/// The Data tables tab while another version is open read only: its DB2 tables (names from the executable, columns
/// named from the WoWDBDefs folder you chose in Settings, else where our layout file for the build knows them, else
/// col_N; M5: the source is shown per table), a row search, and the rows exported as CSV or
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

    /// <summary>M5: where the column names come from, under the header: the WoWDBDefs folder (with its licence) or our
    /// measured layout file.</summary>
    public string NamesSource => _session.Other?.Dbd is { } d
        ? $"Column names: {d.Attribution} (read on this computer, never copied); else our measured layout file; else col_N."
        : "Column names: our measured layout file for this build, else col_N. A WoWDBDefs folder (Settings, optional) names the other tables.";
    public bool HasTable => _table != null;

    /// <summary>The table shown now (tests, screenshots).</summary>
    public VersionTable? Table => _table;
    /// <summary>The rows the search shows (indexes into <see cref="Table"/>).</summary>
    public List<int> Shown { get; private set; } = new();

    /// <summary>Lists the tables (once; later calls wait for the same load).</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>M5: lists the tables again (the WoWDBDefs folder changed in Settings) and reads the shown table again.</summary>
    public async Task ReloadAsync()
    {
        _loading = null;
        OnPropertyChanged(nameof(NamesSource));
        await EnsureLoadedAsync();
        if (_table != null) await SelectTableAsync(_table.Name);
    }

    private Task? _loading;

    private async Task LoadAsync()
    {
        var oc = _session.Other;
        if (oc == null) return;
        var build = _session.Profile?.Build ?? oc.Copy.Build;
        _all = await Task.Run(() => oc.TableNames().Select(n =>
        {
            var naming = oc.ExpectedNaming(n, build);
            return new VersionTableItem(n, oc.Catalog.Get(n)?.FieldCount ?? 0, naming.Source switch
            {
                ColumnNameSource.WoWDBDefs => "column names: WoWDBDefs",
                ColumnNameSource.Measured => "column names: measured (our layout file)",
                _ => "raw columns (col_N)",
            }, naming);
        }).ToList());
        ApplyTableFilter();
        int dbd = _all.Count(t => t.Naming?.Source == ColumnNameSource.WoWDBDefs), ours = _all.Count(t => t.Naming?.Source == ColumnNameSource.Measured);
        Status = $"{_all.Count:N0} tables of {_session.Profile?.VersionBuild}" + (oc.Dbd != null ? $" ({dbd:N0} named by WoWDBDefs, {ours:N0} by our layout file)" : $" ({ours:N0} named by our layout file)") +
                 ". Click one to read it (a table is read when you open it). Read only.";
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
                        + (t.Note.Length > 0 ? " " + t.Note + "." : "") + " Read only." + NamingNote(t);
            ApplySearch();
        }
        catch (Exception e) when (e is WoWData.Core.Db2.Db2FormatException or ClientFileException or IOException or InvalidDataException)
        {
            TableNote = $"{name} could not be read: {e.Message}";
        }
    }

    /// <summary>" Column names: WoWDBDefs." / " Column names: measured (our layout file); not WoWDBDefs: ...".</summary>
    private static string NamingNote(VersionTable t) => t.Naming.Source == ColumnNameSource.None && t.Naming.Fallback.Length == 0 ? "" : $" Column names: {t.Naming.Long}.";

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
