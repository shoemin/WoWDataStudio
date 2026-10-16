using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Dbc;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A table in the list of the Data tables tab.</summary>
public sealed partial class DbcTableItem : ObservableObject
{
    public DbcTableItem(DbcTableInfo info) => Info = info;

    [ObservableProperty] private DbcTableInfo _info;
    public string Name => Info.Name;
    public DbcLayout? Layout => Info.Layout;
    public string Category => Layout?.Category ?? "Other";
    public string About => Layout?.About ?? "";
    public bool CanOpen => Layout != null && !Info.DeletedInGame;
    public bool IsModified => Info.InWorkspace;
    public string ServerBadge => Info.DeletedInGame ? "Server only" : Layout?.ServerUse switch
    {
        DbcServerUse.ServerFile => "Server too",
        DbcServerUse.ServerDatabase => "Server: database",
        _ => "Client only",
    };
    public string CoverageText => Info.DeletedInGame ? "deleted by " + Info.Sources[0].Name : Layout?.CoverageText ?? "no known layout";
    public string Tip => Info.DeletedInGame
        ? $"{Name}.dbc: {Info.SourceText}"
        : $"{About}\n\n{Info.SourceText}{(Info.OverrideText.Length > 0 ? "\n" + Info.OverrideText : "")}\n{Layout?.ServerText}\n{CoverageText}";

    partial void OnInfoChanged(DbcTableInfo value)
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>A column of the grid.</summary>
public sealed class DbcGridColumn
{
    public DbcGridColumn(DbcColumn c) => Column = c;
    public DbcColumn Column { get; }
    public int Index => Column.Index;
    public string Header => Column.Name;
    public string Tip => $"{Column.Name}: {Column.TypeText}" + (Column.Hint.Length > 0 ? "\n" + Column.Hint : "") +
                         (!Column.Verified ? "\nNot verified: the meaning of this column is a guess or unknown." : "") +
                         "\nFilter: type text, or for numbers 5, >5, <10, 5-10, !0.";
}

/// <summary>Cell texts of a row for the grid: Display = what is shown (with names), Edit = the raw value typed.</summary>
public sealed class DbcCellTexts
{
    private readonly DbcRowView _row;
    private readonly bool _edit;

    public DbcCellTexts(DbcRowView row, bool edit)
    {
        _row = row;
        _edit = edit;
    }

    public string this[int col] => _row.Vm.CellText(_row.Row, col, _edit);
}

/// <summary>A row of the grid.</summary>
public sealed class DbcRowView : ObservableObject
{
    public DbcRowView(DbcEditorViewModel vm, DbcRow row)
    {
        Vm = vm;
        Row = row;
        Display = new DbcCellTexts(this, false);
        Edit = new DbcCellTexts(this, true);
    }

    public DbcEditorViewModel Vm { get; }
    public DbcRow Row { get; }
    public DbcCellTexts Display { get; }
    public DbcCellTexts Edit { get; }
    public bool IsChanged => Vm.IsRowChanged(Row);

    /// <summary>Redraw the row (after an edit).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Edit));
        OnPropertyChanged(nameof(IsChanged));
    }
}

public sealed record DbcChoice(long Value, string Text)
{
    public override string ToString() => Text;
}

public sealed partial class DbcFlagItem : ObservableObject
{
    private readonly DbcFieldView _field;
    public DbcFlagItem(DbcFieldView field, long bit, string name, bool on)
    {
        _field = field;
        Bit = bit;
        Name = name;
        _isOn = on;
    }

    public long Bit { get; }
    public string Name { get; }
    public string Text => $"{Name} (0x{Bit:X})";
    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _field.FlagToggled(this);
}

/// <summary>One column of the selected row in the details panel (an editor of its value, its hint, its link).</summary>
public sealed partial class DbcFieldView : ObservableObject
{
    private readonly DbcEditorViewModel _vm;
    private bool _loading;

    public DbcFieldView(DbcEditorViewModel vm, DbcColumn c)
    {
        _vm = vm;
        Column = c;
    }

    public DbcColumn Column { get; }
    public string Name => Column.Name;
    public string TypeText => Column.TypeText;
    public string Hint => Column.Hint;
    public bool Verified => Column.Verified;
    public string Tip => $"{Column.Name}: {Column.TypeText} (file field {Column.Field})" + (Column.Hint.Length > 0 ? "\n" + Column.Hint : "") +
                         (Column.ServerRef != null ? $"\nThe value is a {Column.ServerRef} entry of the server's database." : "") +
                         (!Column.Verified ? "\nNot verified: the meaning of this column is a guess or unknown; the value is shown as stored." : "");
    public bool IsText => Column.IsString;
    public bool IsLoc => Column.Storage == DbcStorage.Loc;
    public bool IsEnum => Column.Semantic == DbcSemantic.Enum && Column.Values != null;
    public bool IsFlags => Column.Semantic == DbcSemantic.Flags && Column.Values != null;
    public bool IsBool => Column.Semantic == DbcSemantic.Bool;
    public bool IsPlainBox => !IsEnum && !IsBool;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _extra = "";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private DbcChoice? _choice;
    [ObservableProperty] private bool _hasLink;
    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private bool _linkMissing;
    public string? LinkTable => Column.ForeignKey;
    public long LinkId { get; private set; }
    public List<DbcChoice> Choices { get; } = new();
    public ObservableCollection<DbcFlagItem> Flags { get; } = new();
    public List<string> OtherLocales { get; } = new();
    public bool HasOtherLocales => OtherLocales.Count > 0;
    public List<string> Files { get; } = new();
    public bool HasFiles => Files.Count > 0;

    internal void Load(DbcTable t, DbcRow r, int locale, DbcService svc, DbcTableLookup lookup)
    {
        _loading = true;
        try
        {
            Text = t.Format(r, Column, locale);
            Problem = "";
            Raw = Column.IsNumber ? t.Raw(r, Column.Field) : 0;
            if (Column.IsNumber)
            {
                long v = t.Number(r, Column);
                if (Column.Values != null) Extra = Column.Values.Describe(Column.Storage == DbcStorage.Int32 ? v : t.Raw(r, Column.Field));
                if (Column.Semantic == DbcSemantic.Flags) Extra = $"0x{t.Raw(r, Column.Field):X8}" + (Extra.Length > 0 ? " · " + Extra : "");
                if (IsBool) BoolValue = t.Raw(r, Column.Field) != 0;
                if (IsEnum)
                {
                    Choices.Clear();
                    foreach (var (val, name) in Column.Values!.Values) Choices.Add(new DbcChoice(val, $"{val} · {name}"));
                    if (Choices.All(c => c.Value != v)) Choices.Insert(0, new DbcChoice(v, $"{v} (not a known value)"));
                    Choice = Choices.First(c => c.Value == v);
                }
                if (IsFlags)
                {
                    Flags.Clear();
                    uint raw = t.Raw(r, Column.Field);
                    foreach (var (bit, name) in Column.Values!.Values.Where(x => x.Value != 0))
                        Flags.Add(new DbcFlagItem(this, bit, name, (raw & (uint)bit) == (uint)bit));
                }
                if (Column.ForeignKey != null && !DbcEditorViewModel.IsNoLink(lookup(Column.ForeignKey), v, t.Raw(r, Column.Field)))
                {
                    LinkId = v;
                    var label = svc.LinkLabel(Column.ForeignKey, v, lookup, locale);
                    HasLink = true;
                    LinkMissing = label == null;
                    LinkText = label == null ? $"{Column.ForeignKey} #{v} does not exist" : $"→ {Column.ForeignKey} #{v}: {label}";
                }
                else
                {
                    HasLink = false;
                    LinkText = "";
                }
            }
            if (IsLoc)
            {
                OtherLocales.Clear();
                for (int l = 0; l < DbcFile.Locales; l++)
                    if (l != locale && t.Text(r, Column, l) is { Length: > 0 } s) OtherLocales.Add($"{DbcTable.LocaleNames[l]}: {s}");
                OnPropertyChanged(nameof(HasOtherLocales));
            }
            if (Column.IsString && Column.Semantic != DbcSemantic.Plain)
            {
                Files.Clear();
                Files.AddRange(svc.FilesOf(t, r, Column, locale));
                OnPropertyChanged(nameof(HasFiles));
                if (Text.Length > 0 && Files.Count == 0) Extra = "No such file in the game.";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void Commit() => _vm.EditField(this, Text);

    partial void OnBoolValueChanged(bool value)
    {
        if (!_loading) _vm.EditField(this, value ? "1" : "0");
    }

    partial void OnChoiceChanged(DbcChoice? value)
    {
        if (!_loading && value != null) _vm.EditField(this, value.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The raw value of a number column (for the flag check boxes).</summary>
    public uint Raw { get; private set; }

    internal void FlagToggled(DbcFlagItem f)
    {
        if (_loading) return;
        uint named = 0, raw = 0;
        foreach (var x in Flags)
        {
            named |= (uint)x.Bit;
            if (x.IsOn) raw |= (uint)x.Bit;
        }
        raw |= Raw & ~named;   // the bits without a name are kept
        _vm.EditField(this, Column.Storage == DbcStorage.Int32 ? unchecked((int)raw).ToString(CultureInfo.InvariantCulture) : raw.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void FollowLink()
    {
        if (HasLink && !LinkMissing && LinkTable != null) _ = _vm.NavigateAsync(LinkTable, LinkId, true);
    }

    [RelayCommand]
    private void ShowFile(string? path)
    {
        if (path != null) _vm.ShowPreview(path);
    }
}

/// <summary>
/// The Data tables tab (0.7.0, the DBC editor): every DBC of the game in a list, a table in a fast grid with search,
/// column filters and sorting, the selected row in the details panel (links, flags, choices, languages, files, "Used by"),
/// editing with validation and undo / redo, adding / duplicating / deleting rows, bulk edits, CSV / JSON export and import
/// with a preview, saving into the workspace (My changes, Compare, Test in game, release patch) and "Copy to server" for
/// the tables the server reads too.
/// </summary>
public sealed partial class DbcEditorViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private readonly BrowserViewModel _browser;
    private readonly DispatcherTimer _filterTimer;
    private int _filterToken, _usedByToken;
    private string[]? _rowText;
    private int _rowTextVersion = -1;
    private DbcTable? _rowTextFor;
    private HashSet<DbcRow> _changed = new();
    private readonly Stack<(string Table, long? Id)> _history = new();
    private readonly Dictionary<int, string> _columnFilters = new();

    public DbcEditorViewModel(AppSession session, MainViewModel main, BrowserViewModel browser)
    {
        _session = session;
        _main = main;
        _browser = browser;
        Preview = new TextureViewerViewModel(session);
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _ = ApplyFilterAsync();
        };
        _locale = Math.Clamp(session.Settings.DbcLocale ?? session.GameLocale, 0, DbcFile.Locales - 1);
        if (session.Workspace != null) session.Workspace.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(RefreshTableStates);
    }

    public DbcService? Service => _session.Dbc;
    public TextureViewerViewModel Preview { get; }

    // ------------------------------------------------------------------ the table list

    public ObservableCollection<DbcTableItem> AllTables { get; } = new();
    public ObservableCollection<DbcTableItem> Tables { get; } = new();
    [ObservableProperty] private string _tableFilter = "";
    [ObservableProperty] private bool _onlyModifiedTables;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _listText = "";

    partial void OnTableFilterChanged(string value) => FilterTables();
    partial void OnOnlyModifiedTablesChanged(bool value) => FilterTables();

    public async Task EnsureLoadedAsync()
    {
        if (IsLoaded || Service == null) return;
        var svc = Service;
        var list = await Task.Run(svc.List);
        AllTables.Clear();
        foreach (var i in list.OrderBy(i => i.Layout?.Category ?? "~", StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            AllTables.Add(new DbcTableItem(i));
        IsLoaded = true;
        FilterTables();
        if (SelectedTable == null)
        {
            var last = _session.Settings.DbcLastTable;
            var pick = Tables.FirstOrDefault(t => string.Equals(t.Name, last, StringComparison.OrdinalIgnoreCase) && t.CanOpen) ?? Tables.FirstOrDefault(t => t.Name == "Spell");
            if (pick != null) await SelectTableAsync(pick);
        }
    }

    private void FilterTables()
    {
        var words = TableFilter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = SelectedTable;
        Tables.Clear();
        foreach (var t in AllTables)
        {
            if (OnlyModifiedTables && !t.IsModified) continue;
            var hay = (t.Name + " " + t.Category + " " + t.About).ToLowerInvariant();
            if (words.All(hay.Contains)) Tables.Add(t);
        }
        ListText = $"{Tables.Count} of {AllTables.Count} tables";
        if (keep != null && Tables.Contains(keep)) OnPropertyChanged(nameof(SelectedTable));
    }

    private void RefreshTableStates()
    {
        if (Service == null || !IsLoaded) return;
        foreach (var t in AllTables) t.Info = Service.Info(t.Name);
        if (Table == null) return;
        UpdateTableTexts();
        // the open table was changed elsewhere (reverted in My changes, saved by an import...): load it again when it has
        // no unsaved changes, else say so
        if (WorkspaceStamp(Table.Name) == _loadedStamp) return;
        if (Editor?.IsDirty == true)
            SetStatus($"{Table.Name}.dbc changed in your workspace since you opened it. \"Discard changes\" loads that version; saving keeps yours.", true);
        else
        {
            var keep = SelectedRow != null ? Table.Key(SelectedRow.Row) : null;
            var name = Table.Name;
            _ = LoadTableAsync(name).ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (keep != null) GoTo(keep.Value);
            }), TaskScheduler.Default);
        }
    }

    private string _loadedStamp = "";

    private string WorkspaceStamp(string table) => Service?.Workspace?.Get(DbcService.PathOf(table))?.WorkspaceSha256 ?? "game";

    private DbcTableItem? _selectedTable;

    /// <summary>The table shown. Switching away from unsaved changes asks first.</summary>
    public DbcTableItem? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedTable)) return;
            _ = SelectTableAsync(value);
        }
    }

    public async Task<bool> SelectTableAsync(DbcTableItem item, long? selectId = null)
    {
        if (!ReferenceEquals(item, _selectedTable))
        {
            if (!ConfirmLeave())
            {
                OnPropertyChanged(nameof(SelectedTable));
                return false;
            }
            if (!item.CanOpen)
            {
                _selectedTable = item;
                OnPropertyChanged(nameof(SelectedTable));
                ClearTable(item.Info.DeletedInGame ? $"{item.Name}.dbc: {item.Info.SourceText}" : $"{item.Name}.dbc has no known layout.");
                return false;
            }
            _selectedTable = item;
            OnPropertyChanged(nameof(SelectedTable));
            await LoadTableAsync(item.Name);
        }
        if (selectId != null) GoTo(selectId.Value);
        return true;
    }

    /// <summary>True when there are unsaved changes that the user wants to keep editing (Cancel).</summary>
    public bool ConfirmLeave()
    {
        if (Editor == null || !Editor.IsDirty || ScreenshotRunner.Running) return true;
        int pick = Dialogs.Show("Unsaved changes", $"{Table!.Name}.dbc has changes that are not saved yet.\n\n\"Save to my workspace\" keeps them (then they are in My changes); \"Discard\" throws them away.",
            DialogKind.Question, new[] { "Save to my workspace", "Discard my changes", "Cancel" });
        if (pick == 0) return SaveCore(interactive: false);
        return pick == 1;
    }

    // ------------------------------------------------------------------ the open table

    [ObservableProperty] private DbcTable? _table;
    [ObservableProperty] private DbcEditor? _editor;
    [ObservableProperty] private IReadOnlyList<DbcGridColumn> _gridColumns = Array.Empty<DbcGridColumn>();
    [ObservableProperty] private IReadOnlyList<DbcRowView> _rows = Array.Empty<DbcRowView>();
    [ObservableProperty] private string _rowCountText = "";
    [ObservableProperty] private string _tableTitle = "";
    [ObservableProperty] private string _tableAbout = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _overrideText = "";
    [ObservableProperty] private string _serverBadge = "";
    [ObservableProperty] private string _serverText = "";
    [ObservableProperty] private string _coverageText = "";
    [ObservableProperty] private string _evidenceText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _goToIdText = "";
    [ObservableProperty] private DbcRowView? _selectedRow;
    [ObservableProperty] private string _sortText = "";

    /// <summary>The grid's selected rows (set by the view).</summary>
    public List<DbcRowView> SelectedRows { get; private set; } = new();

    public string SelectionText => SelectedRows.Count > 1 ? $"{SelectedRows.Count} rows selected" : "";

    public void SelectedRowsFromView(List<DbcRowView> rows)
    {
        SelectedRows = rows;
        OnPropertyChanged(nameof(SelectionText));
    }

    /// <summary>Raised when the grid must rebuild its columns (another table) or scroll to the selected row.</summary>
    public event Action? ColumnsChanged;
    public event Action<DbcRowView>? ScrollRequested;

    public bool HasTable => Table != null;
    public bool IsDirty => Editor?.IsDirty == true;
    public string DirtyText => Editor == null ? "" : Editor.IsDirty ? "Unsaved changes" : Table != null && Service?.Workspace?.IsModified(DbcService.PathOf(Table.Name)) == true ? "Saved in your workspace" : "Same as the game";
    public bool CanUndo => Editor?.CanUndo == true;
    public bool CanRedo => Editor?.CanRedo == true;
    public string UndoText => Editor?.UndoLabel is { } l ? $"Undo: {l}" : "Nothing to undo";
    public string RedoText => Editor?.RedoLabel is { } l ? $"Redo: {l}" : "Nothing to redo";
    public bool HasKey => Table?.Layout.HasKey == true;
    public bool CanCopyToServer => Table?.Layout.ServerUse == DbcServerUse.ServerFile;

    /// <summary>0.9.0: the server keeps this data in its database (Spell: spell_template ...): the Server data tab edits it.</summary>
    public bool IsServerDatabaseTable => Table?.Layout.ServerUse == DbcServerUse.ServerDatabase && Table.Layout.ServerTable != null;

    [RelayCommand]
    private async Task OpenServerTable()
    {
        if (Table?.Layout.ServerTable is { } st) await _browser.ShowServerTableAsync(st, SelectedRow != null ? Table.Key(SelectedRow.Row) : null);
    }
    public DbcServerUse ServerUse => Table?.Layout.ServerUse ?? DbcServerUse.ClientOnly;
    public bool ServerUsesDatabase => ServerUse == DbcServerUse.ServerDatabase;

    // languages
    public IReadOnlyList<string> LocaleNames => DbcTable.LocaleNames;
    private int _locale;

    public int Locale
    {
        get => _locale;
        set
        {
            value = Math.Clamp(value, 0, DbcFile.Locales - 1);
            if (_locale == value) return;
            _locale = value;
            if (_edit != null) _edit.Locale = value;
            _session.Settings.DbcLocale = value;
            _main.SaveSettings();
            OnPropertyChanged();
            _rowText = null;
            foreach (var r in Rows) r.Refresh();
            LoadDetails();
        }
    }

    private void ClearTable(string message)
    {
        _edit = null;
        Table = null;
        Editor = null;
        GridColumns = Array.Empty<DbcGridColumn>();
        Rows = Array.Empty<DbcRowView>();
        Fields.Clear();
        Message = message;
        TableTitle = SelectedTable?.Name + ".dbc";
        TableAbout = "";
        ColumnsChanged?.Invoke();
        NotifyEditState();
    }

    public async Task LoadTableAsync(string name)
    {
        var svc = Service;
        if (svc == null) return;
        IsLoading = true;
        Message = "";
        try
        {
            var stamp = WorkspaceStamp(name);
            int locale = _locale;
            var edit = await Task.Run(() => new DbcEditSession(svc, name, locale));
            var t = edit.Table;
            if (Editor != null) Editor.Changed -= OnEditorChanged;
            _edit = edit;
            Table = t;
            Editor = edit.Editor;
            Editor.Changed += OnEditorChanged;
            _gameTable = edit.GameTable;
            _loadedStamp = stamp;
            SetStatus("", false);
            _columnFilters.Clear();
            SearchText = "";
            _sortColumn = -1;
            SortText = "";
            _rowText = null;
            GridColumns = t.Layout.Columns.Select(c => new DbcGridColumn(c)).ToList();
            UpdateTableTexts();
            _session.Settings.DbcLastTable = name;
            _main.SaveSettings();
            _changed = await Task.Run(ChangedRows);
            ColumnsChanged?.Invoke();
            await ApplyFilterAsync();
            SelectedRow = Rows.FirstOrDefault();
            NotifyEditState();
        }
        catch (Exception e) when (e is DbcException or IOException)
        {
            ClearTable("This table cannot be opened: " + e.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private DbcTable? _gameTable;

    /// <summary>0.8.0: the open table as a Core edit session (shared with "wds dbc ...").</summary>
    private DbcEditSession? _edit;
    public DbcEditSession? EditSession => _edit;

    private void UpdateTableTexts()
    {
        var t = Table;
        if (t == null || Service == null) return;
        var info = Service.Info(t.Name);
        TableTitle = $"{t.Name}.dbc";
        TableAbout = t.Layout.About;
        SourceText = info.SourceText;
        OverrideText = info.OverrideText;
        ServerBadge = t.Layout.ServerText;
        ServerText = t.Layout.ServerExplanation;
        CoverageText = t.Layout.CoverageText + (t.Layout.UnverifiedColumns > 0 ? $" ({t.Layout.UnverifiedColumns} not verified: shown as stored, with a hint)" : "");
        EvidenceText = t.Layout.Evidence;
        OnPropertyChanged(nameof(CanCopyToServer));
        OnPropertyChanged(nameof(IsServerDatabaseTable));
        OnPropertyChanged(nameof(ServerUse));
        OnPropertyChanged(nameof(ServerUsesDatabase));
        OnPropertyChanged(nameof(DirtyText));
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        _rowText = null;
        NotifyEditState();
        _ = UpdateChangedRowsAsync();
    }

    private int _changedToken;

    /// <summary>The rows that differ from the game's file, found in the background (a big table takes a moment).</summary>
    private async Task UpdateChangedRowsAsync()
    {
        int token = ++_changedToken;
        var set = await Task.Run(ChangedRows);
        if (token != _changedToken) return;
        _changed = set;
        foreach (var r in Rows) r.Refresh();
        if (SelectedRow != null) DetailSubtitle = SubtitleOf(SelectedRow.Row);
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(HasTable));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DirtyText));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(HasKey));
    }

    /// <summary>The rows that differ from the game's file (marked in the grid).</summary>
    private HashSet<DbcRow> ChangedRows() => _edit?.ChangedRows() ?? new HashSet<DbcRow>();

    public bool IsRowChanged(DbcRow r) => _changed.Contains(r);

    private string SubtitleOf(DbcRow r)
    {
        var t = Table!;
        return $"{t.Name} · " + (t.Key(r) is { } k ? $"ID {k}" : $"row {t.Rows.IndexOf(r) + 1}") + (IsRowChanged(r) ? " · changed (not like the game's file)" : "");
    }

    /// <summary>0 and -1 mean "no link", unless the linked table has such a row (Map 0 is Eastern Kingdoms).</summary>
    public static bool IsNoLink(DbcTable? target, long v, uint raw) => DbcEditSession.IsNoLink(target, v, raw);

    public DbcTable? Lookup(string name) => _edit != null ? _edit.Lookup(name) : Service?.Lookup(name);

    // ------------------------------------------------------------------ cells

    /// <summary>The text of a cell: shown (with the name of a choice or the label of a link) or for editing (the raw value).</summary>
    public string CellText(DbcRow r, int col, bool edit)
    {
        var t = Table;
        if (t == null || _edit == null || col >= t.Layout.Columns.Count) return "";
        return _edit.CellText(r, t.Layout.Columns[col], edit);
    }

    /// <summary>An edit typed in the grid. Returns false (and says why in the status line) when the value is refused.</summary>
    public bool TryEditCell(DbcRowView rv, int col, string text)
    {
        var t = Table;
        if (t == null || Editor == null) return false;
        var c = t.Layout.Columns[col];
        if (text == t.Format(rv.Row, c, _locale)) return true;
        return Edit(rv.Row, c, text);
    }

    internal void EditField(DbcFieldView f, string text)
    {
        var r = SelectedRow?.Row;
        if (r == null || Table == null) return;
        if (text == Table.Format(r, f.Column, _locale)) return;
        if (!Edit(r, f.Column, text))
        {
            f.Problem = Status;
            return;
        }
    }

    private bool Edit(DbcRow r, DbcColumn c, string text)
    {
        var res = _edit!.Edit(r, c, text);
        SetStatus(res.Message, !res.Ok);
        if (!res.Ok) return false;
        RefreshRow(r);
        return true;
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }

    private void RefreshRow(DbcRow r)
    {
        foreach (var rv in Rows)
            if (ReferenceEquals(rv.Row, r)) rv.Refresh();
        if (ReferenceEquals(SelectedRow?.Row, r)) LoadDetails();
    }

    // ------------------------------------------------------------------ search, filters, sorting

    partial void OnSearchTextChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>A column filter typed in a column header ("" = none).</summary>
    public void SetColumnFilter(int col, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) _columnFilters.Remove(col);
        else _columnFilters[col] = text.Trim();
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    public string ColumnFilter(int col) => _columnFilters.GetValueOrDefault(col) ?? "";

    public bool HasFilters => _columnFilters.Count > 0 || SearchText.Length > 0;

    [RelayCommand]
    private void ClearFilters()
    {
        _columnFilters.Clear();
        SearchText = "";
        ColumnsChanged?.Invoke();
        _ = ApplyFilterAsync();
    }

    private int _sortColumn = -1;
    private bool _sortAscending = true;

    /// <summary>Sort by a column (again: the other direction; a third time: file order).</summary>
    public void Sort(int col)
    {
        if (_sortColumn == col)
        {
            if (_sortAscending) _sortAscending = false;
            else _sortColumn = -1;
        }
        else
        {
            _sortColumn = col;
            _sortAscending = true;
        }
        SortText = _sortColumn < 0 || Table == null ? "" : $"Sorted by {Table.Layout.Columns[_sortColumn].Name} ({(_sortAscending ? "ascending" : "descending")})";
        _ = ApplyFilterAsync();
    }

    public int SortColumn => _sortColumn;
    public bool SortAscending => _sortAscending;

    public Task ApplyFilterNow() => ApplyFilterAsync();

    private async Task ApplyFilterAsync()
    {
        var t = Table;
        if (t == null) return;
        int token = ++_filterToken;
        var words = SearchText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filters = _columnFilters.Select(kv => (Col: t.Layout.Columns[kv.Key], F: DbcFilter.Parse(kv.Value))).ToList();
        int locale = _locale;
        int sortCol = _sortColumn;
        bool asc = _sortAscending;
        var rows = t.Rows.ToList();
        string[]? text = null;
        if (words.Length > 0)
        {
            if (_rowText == null || _rowTextFor != t || _rowTextVersion != t.Version || _rowText.Length != rows.Count)
            {
                IsLoading = true;
                var built = await Task.Run(() => rows.Select(r => DbcEditSession.RowSearchText(t, r, locale)).ToArray());
                if (token != _filterToken) return;
                _rowText = built;
                _rowTextFor = t;
                _rowTextVersion = t.Version;
                IsLoading = false;
            }
            text = _rowText;
        }
        var sortColumn = sortCol >= 0 ? t.Layout.Columns[sortCol] : null;
        var search = SearchText;
        var result = await Task.Run(() => DbcEditSession.Query(t, locale, search, filters, sortColumn, asc, text));
        if (token != _filterToken) return;
        var keep = SelectedRow?.Row;
        var views = result.Select(i => new DbcRowView(this, rows[i])).ToList();
        Rows = views;
        RowCountText = views.Count == t.Rows.Count ? $"{t.Rows.Count:N0} rows" : $"{views.Count:N0} of {t.Rows.Count:N0} rows";
        OnPropertyChanged(nameof(HasFilters));
        if (keep != null) SelectedRow = views.FirstOrDefault(v => ReferenceEquals(v.Row, keep));
    }

    // ------------------------------------------------------------------ go to ID, navigation

    [RelayCommand]
    private void GoToId()
    {
        if (!long.TryParse(GoToIdText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            SetStatus("Type the ID of a row (a whole number).", true);
            return;
        }
        GoTo(id);
    }

    /// <summary>Select the row with this ID (clearing filters that hide it).</summary>
    public bool GoTo(long id)
    {
        var t = Table;
        var r = t?.Find(id);
        if (t == null || r == null)
        {
            SetStatus($"{Table?.Name ?? "This table"} has no row with ID {id}.", true);
            return false;
        }
        var rv = Rows.FirstOrDefault(v => ReferenceEquals(v.Row, r));
        if (rv == null)
        {
            _columnFilters.Clear();
            SearchText = "";
            _filterTimer.Stop();
            ColumnsChanged?.Invoke();
            ApplyFilterAsync().ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var v = Rows.FirstOrDefault(x => ReferenceEquals(x.Row, r));
                if (v != null)
                {
                    SelectedRow = v;
                    ScrollRequested?.Invoke(v);
                }
            }), TaskScheduler.Default);
            return true;
        }
        SelectedRow = rv;
        ScrollRequested?.Invoke(rv);
        SetStatus($"ID {id}: {t.Label(r, _locale)}", false);
        return true;
    }

    /// <summary>Open a table and select a row (a followed link: Back returns).</summary>
    public async Task<bool> NavigateAsync(string table, long id, bool remember)
    {
        var item = AllTables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        if (item == null) return false;
        if (remember && Table != null) _history.Push((Table.Name, SelectedRow != null ? Table.Key(SelectedRow.Row) : null));
        OnPropertyChanged(nameof(CanGoBack));
        if (!Tables.Contains(item))
        {
            TableFilter = "";
            OnlyModifiedTables = false;
        }
        if (!await SelectTableAsync(item)) return false;
        return GoTo(id);
    }

    public bool CanGoBack => _history.Count > 0;

    [RelayCommand]
    private async Task Back()
    {
        while (_history.Count > 0)
        {
            var (table, id) = _history.Pop();
            OnPropertyChanged(nameof(CanGoBack));
            var item = AllTables.FirstOrDefault(t => t.Name == table);
            if (item == null) continue;
            if (!await SelectTableAsync(item)) return;
            if (id != null) GoTo(id.Value);
            return;
        }
    }

    // ------------------------------------------------------------------ details of the selected row

    public ObservableCollection<DbcFieldView> Fields { get; } = new();
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailSubtitle = "";
    [ObservableProperty] private List<string> _previewFiles = new();
    [ObservableProperty] private string _previewPath = "";
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _modelKey = "";
    [ObservableProperty] private string _modelText = "";
    public ObservableCollection<DbcUsedBy> UsedBy { get; } = new();
    [ObservableProperty] private string _usedByText = "";
    [ObservableProperty] private bool _usedByLoading;

    partial void OnSelectedRowChanged(DbcRowView? value) => LoadDetails();

    private void LoadDetails()
    {
        var t = Table;
        var r = SelectedRow?.Row;
        var svc = Service;
        if (t == null || r == null || svc == null)
        {
            Fields.Clear();
            DetailTitle = "";
            DetailSubtitle = "";
            UsedBy.Clear();
            UsedByText = "";
            HasPreview = false;
            Preview.Clear();
            ModelKey = "";
            return;
        }
        DetailTitle = t.Label(r, _locale);
        DetailSubtitle = SubtitleOf(r);
        if (Fields.Count != t.Layout.Columns.Count || Fields.Count > 0 && !ReferenceEquals(Fields[0].Column, t.Layout.Columns[0]))
        {
            Fields.Clear();
            foreach (var c in t.Layout.Columns) Fields.Add(new DbcFieldView(this, c));
        }
        foreach (var f in Fields) f.Load(t, r, _locale, svc, Lookup);
        // previews: the files the row names, and an icon through a link (a spell's SpellIconID)
        var files = Fields.OrderBy(f => f.Column.Semantic switch { DbcSemantic.Icon => 0, DbcSemantic.Texture => 1, _ => 2 }).SelectMany(f => f.Files).ToList();
        foreach (var f in Fields.Where(f => f.HasLink && !f.LinkMissing && f.LinkTable is "SpellIcon"))
        {
            var it = Lookup("SpellIcon");
            var ir = it?.Find(f.LinkId);
            if (it != null && ir != null) files.InsertRange(0, svc.FilesOf(it, ir, it.Layout.Column("TextureFilename")!));
        }
        PreviewFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var img = PreviewFiles.FirstOrDefault(p => p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase));
        if (img != null) ShowPreview(img);
        else
        {
            HasPreview = false;
            Preview.Clear();
            PreviewPath = "";
        }
        (ModelKey, ModelText) = ModelEntry(t, r);
        _ = LoadUsedByAsync();
    }

    [RelayCommand]
    private void ShowFileFromList(string? path)
    {
        if (path != null) ShowPreview(path);
    }

    internal void ShowPreview(string path)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
        {
            var key = "file:" + path;
            if (_browser.FindEntryByKey(key) != null) _ = _browser.NavigateToKeyAsync(key, false);
            else SetStatus($"{path}: open it from the Browse tab.", false);
            return;
        }
        PreviewPath = path;
        HasPreview = true;
        _ = Preview.LoadAsync(path);
    }

    /// <summary>The Browse entry that shows this row in 3D (a creature, item or game object display, a spell effect, a model).</summary>
    private (string Key, string Text) ModelEntry(DbcTable t, DbcRow r)
    {
        var id = t.Key(r);
        string? Find(string token) =>
            _session.Entries.FirstOrDefault(e => (" " + e.Links + " ").Contains(" " + token + " ", StringComparison.Ordinal))?.Key;
        string? key = t.Name switch
        {
            "CreatureDisplayInfo" when id != null => _browser.FindEntryByKey($"cdisplay:{id}")?.Key ?? Find($"creature:{id}"),
            "ItemDisplayInfo" when id != null => _browser.FindEntryByKey($"idisplay:{id}")?.Key ?? Find($"item:{id}"),
            "GameObjectDisplayInfo" when id != null => _browser.FindEntryByKey($"godisplay:{id}")?.Key ?? Find($"gameobject:{id}"),
            "SpellVisualEffectName" when id != null => _browser.FindEntryByKey($"spell:{id}")?.Key,
            "ChrRaces" when id != null => _browser.FindEntryByKey($"race:{id}:0")?.Key,
            _ => null,
        };
        if (key == null)
            foreach (var p in PreviewFiles.Where(p => p.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)))
                if (_browser.FindEntryByKey("file:" + p) != null) { key = "file:" + p; break; }
        if (key == null) return ("", "");
        var e = _browser.FindEntryByKey(key);
        return (key, e == null ? "" : $"Show in 3D: {e.Name} ({e.KindLabel})");
    }

    [RelayCommand]
    private async Task ShowModel()
    {
        if (ModelKey.Length == 0) return;
        if (!await _browser.NavigateToKeyAsync(ModelKey, false)) SetStatus("This entry is not in the Browse list.", true);
    }

    private async Task LoadUsedByAsync()
    {
        int token = ++_usedByToken;
        UsedBy.Clear();
        var t = Table;
        var r = SelectedRow?.Row;
        var svc = Service;
        if (t == null || r == null || svc == null || t.Key(r) is not { } id)
        {
            UsedByText = t != null && !t.Layout.HasKey ? "Other tables cannot link to this table (it has no ID column)." : "";
            return;
        }
        var cols = svc.LinkingColumns(t.Name);
        if (cols.Count == 0)
        {
            UsedByText = "No other table links to this table.";
            return;
        }
        UsedByLoading = true;
        UsedByText = "Looking for rows that use this row...";
        var name = t.Name;
        var current = t;
        int locale = _locale;
        try
        {
            var list = await Task.Run(() => svc.UsedBy(name, id, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) ? current : svc.Lookup(n), 300, locale));
            if (token != _usedByToken) return;
            foreach (var u in list) UsedBy.Add(u);
            UsedByText = list.Count == 0 ? $"Nothing links to this row (checked {string.Join(", ", cols.Take(6))}{(cols.Count > 6 ? ", ..." : "")})."
                : $"{list.Count}{(list.Count >= 300 ? "+" : "")} row{(list.Count == 1 ? "" : "s")} use this row.";
        }
        finally
        {
            if (token == _usedByToken) UsedByLoading = false;
        }
    }

    [RelayCommand]
    private Task OpenUsedBy(DbcUsedBy? u) => u?.Id is { } id ? NavigateAsync(u.Table, id, true) : Task.CompletedTask;

    // ------------------------------------------------------------------ rows

    [RelayCommand]
    private void Undo()
    {
        if (Editor?.Undo() == true) AfterStructuralChange("Undone.");
    }

    [RelayCommand]
    private void Redo()
    {
        if (Editor?.Redo() == true) AfterStructuralChange("Redone.");
    }

    private void AfterStructuralChange(string status, DbcRow? select = null)
    {
        SetStatus(status, false);
        var keep = select ?? SelectedRow?.Row;
        _ = ApplyFilterAsync().ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var r in Rows) r.Refresh();
            var v = keep == null ? null : Rows.FirstOrDefault(x => ReferenceEquals(x.Row, keep));
            if (v != null)
            {
                SelectedRow = v;
                ScrollRequested?.Invoke(v);
            }
            LoadDetails();
        }), TaskScheduler.Default);
    }

    [RelayCommand]
    private void AddRow()
    {
        var t = Table;
        if (t == null || Editor == null) return;
        var (row, msg) = _edit!.AddRow();
        AfterStructuralChange(t.Layout.HasKey ? msg + " Fill in its values on the right." : msg, row);
    }

    [RelayCommand]
    private void DuplicateRow()
    {
        var t = Table;
        var src = SelectedRow?.Row;
        if (t == null || Editor == null || src == null) return;
        var (copy, msg) = _edit!.Duplicate(src);
        AfterStructuralChange(msg, copy);
    }

    [RelayCommand]
    private async Task DeleteRows()
    {
        var t = Table;
        if (t == null || Editor == null) return;
        var rows = SelectedRows.Count > 0 ? SelectedRows.Select(v => v.Row).ToList() : SelectedRow != null ? new List<DbcRow> { SelectedRow.Row } : new();
        if (rows.Count == 0) return;
        // rows that other rows link to
        var edit = _edit!;
        var used = await Task.Run(() => edit.LinksTo(rows));
        if (!ScreenshotRunner.Running)
        {
            var msg = rows.Count == 1 ? $"Delete {t.Label(rows[0], _locale)}?" : $"Delete {rows.Count} rows?";
            if (used.Count > 0) msg += "\n\nOther rows link to " + (rows.Count == 1 ? "it" : "some of them") + ":\n" + string.Join("\n", used.Take(8)) +
                                       "\n\nThose links will point at nothing (the game shows nothing, a question mark or an error there).";
            if (!Dialogs.Confirm("Delete rows", msg + "\n\nYou can undo it (Ctrl+Z) until you close the table.", "Delete", "Cancel", used.Count > 0 ? DialogKind.Warning : DialogKind.Question)) return;
        }
        var deleted = edit.DeleteRows(rows);
        AfterStructuralChange(deleted + (rows.Count > 1 && used.Count > 0 ? " Some of them were used by other rows." : ""));
    }

    /// <summary>Bulk edit of the selected rows: set a column to a value, add to it or multiply it.</summary>
    public (bool Ok, string Message) BulkEdit(DbcColumn c, BulkOperation op, string value, IReadOnlyList<DbcRow> rows)
    {
        if (Table == null || _edit == null || rows.Count == 0) return (false, "Select the rows to change first.");
        var res = _edit.BulkEdit(c, op, value, rows);
        if (res.Ok) AfterStructuralChange($"{res.Changed} value{(res.Changed == 1 ? "" : "s")} changed in {c.Name}.");
        return (res.Ok, res.Message);
    }

    [RelayCommand]
    private void OpenBulkEdit()
    {
        var t = Table;
        if (t == null) return;
        var rows = SelectedRows.Count > 0 ? SelectedRows.Select(v => v.Row).ToList() : SelectedRow != null ? new List<DbcRow> { SelectedRow.Row } : new();
        if (rows.Count == 0)
        {
            Dialogs.Info("Bulk edit", "Select the rows to change first (Ctrl+click or Shift+click in the table; Ctrl+A selects all rows shown).");
            return;
        }
        var win = new Views.DbcBulkEditWindow(this, rows) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
    }

    // ------------------------------------------------------------------ save, discard, compare

    [RelayCommand]
    private void Save() => SaveCore(interactive: true);

    /// <summary>Save the table into the workspace (one change set). Interactive: shows the changes first.</summary>
    public bool SaveCore(bool interactive)
    {
        var t = Table;
        var svc = Service;
        if (t == null || svc == null || Editor == null) return false;
        if (!Editor.IsDirty)
        {
            if (interactive) Dialogs.Info("Nothing to save", "This table has no unsaved changes.");
            return true;
        }
        var diff = _edit!.CompareWithGame();
        if (interactive)
        {
            var win = new Views.DbcDiffWindow($"Save {t.Name}.dbc to my workspace", diff,
                $"Your version of {t.Name}.dbc compared with the game's file: {diff.Summary}\n\nSaving puts it into your workspace (My changes). Nothing in the game changes until you use Test in game or Build release patch." +
                (_edit.ServerNote.Length > 0 ? "\n\n" + _edit.ServerNote : ""),
                "Save to my workspace") { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() != true) return false;
        }
        try
        {
            var (_, message) = _edit.Save(diff);
            _loadedStamp = WorkspaceStamp(t.Name);
            RefreshTableStates();
            _main.Toast(message);
            NotifyEditState();
            return true;
        }
        catch (Exception e) when (e is DbcException or WoWData.Core.Workspace.WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not save the table", e.Message);
            return false;
        }
    }

    [RelayCommand]
    private async Task Discard()
    {
        var t = Table;
        if (t == null || Editor == null || !Editor.IsDirty) return;
        if (!ScreenshotRunner.Running && !Dialogs.Confirm("Discard my changes", $"Throw away the unsaved changes of {t.Name}.dbc? The version in your workspace (or the game's) is loaded again.", "Discard", "Keep editing", DialogKind.Warning)) return;
        await LoadTableAsync(t.Name);
    }

    [RelayCommand]
    private void CompareWithGame()
    {
        var t = Table;
        if (t == null || _gameTable == null)
        {
            Dialogs.Info("Compare with the game", "The game has no such file to compare with.");
            return;
        }
        var diff = _edit!.CompareWithGame();
        new Views.DbcDiffWindow($"{t.Name}.dbc: your version and the game's", diff,
            $"Your version (with {(Editor!.IsDirty ? "the unsaved changes" : "the changes in your workspace")}) compared with the game's file: {diff.Summary}", null) { Owner = Application.Current.MainWindow }.Show();
    }

    // ------------------------------------------------------------------ export, import

    /// <summary>Which rows an export writes.</summary>
    public IReadOnlyList<DbcRow> ExportRows(ExportScope scope) => scope switch
    {
        ExportScope.Shown => Rows.Select(r => r.Row).ToList(),
        ExportScope.Selected => (SelectedRows.Count > 0 ? SelectedRows : SelectedRow != null ? new List<DbcRowView> { SelectedRow } : new()).Select(r => r.Row).ToList(),
        _ => Table?.Rows.ToList() ?? new List<DbcRow>(),
    };

    [RelayCommand]
    private void Export(string? scopeText)
    {
        var t = Table;
        if (t == null) return;
        var scope = Enum.TryParse<ExportScope>(scopeText, out var s) ? s : ExportScope.All;
        var rows = ExportRows(scope);
        if (rows.Count == 0)
        {
            Dialogs.Info("Export", "There are no rows to export.");
            return;
        }
        var file = Dialogs.PickSaveFile($"Export {t.Name} ({rows.Count:N0} rows)", "CSV file for spreadsheets (*.csv)|*.csv|JSON file (*.json)|*.json",
            t.Name + (scope == ExportScope.All ? "" : scope == ExportScope.Shown ? "_shown" : "_selected") + ".csv", _session.Settings.DefaultExportDir);
        if (file == null) return;
        try
        {
            ExportTo(file, rows);
            _main.Toast($"Exported {rows.Count:N0} rows of {t.Name} to {Path.GetFileName(file)}.");
            Dialogs.ShowInExplorer(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not export", e.Message);
        }
    }

    public void ExportTo(string file, IReadOnlyList<DbcRow> rows) => (_edit ?? throw new DbcException("No table is open.")).ExportTo(file, rows);

    [RelayCommand]
    private void Import()
    {
        var t = Table;
        if (t == null || Editor == null) return;
        var file = Dialogs.PickOpenFile($"Import into {t.Name}", "CSV or JSON (*.csv;*.json)|*.csv;*.json|All files (*.*)|*.*", _session.Settings.DefaultExportDir);
        if (file == null) return;
        var win = new Views.DbcImportWindow(this, file) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
    }

    /// <summary>What importing a file would do (nothing is changed).</summary>
    public DbcImportPlan PlanImport(string file, DbcMissingRows missing) => (_edit ?? throw new DbcException("No table is open.")).PlanImport(file, missing);

    public void ApplyImport(DbcImportPlan plan, string file)
    {
        AfterStructuralChange(_edit!.ApplyImport(plan, file));
    }

    // ------------------------------------------------------------------ game and server

    [RelayCommand]
    private async Task TestInGame()
    {
        var t = Table;
        if (t == null) return;
        if (Editor?.IsDirty == true && !SaveCore(interactive: true)) return;
        var path = DbcService.PathOf(t.Name);
        if (_session.Workspace?.IsModified(path) != true)
        {
            Dialogs.Info("Test in game", $"{t.Name}.dbc is the same as the game's file: there is nothing to install.");
            return;
        }
        await _browser.InstallLooseAsync(new[] { path });
        RefreshTableStates();
    }

    [RelayCommand]
    private void BuildPatch()
    {
        if (Editor?.IsDirty == true && !SaveCore(interactive: true)) return;
        _browser.BuildPatchCommand.Execute(null);
    }

    [RelayCommand]
    private void CopyToServer()
    {
        var t = Table;
        if (Editor?.IsDirty == true && !SaveCore(interactive: true)) return;
        var win = new Views.DbcServerCopyWindow(this, t?.Name) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
        RefreshTableStates();
    }

    /// <summary>The "Copy to server" service for the session (the server data folder from the settings).</summary>
    public DbcServerCopy? ServerCopy(Func<List<string>>? running = null) => _session.DbcServerCopy(running);

    public WoWData.Core.Workspace.Workspace? Workspace => _session.Workspace;

    public void Toast(string text) => _main.Toast(text);

    [RelayCommand]
    private void OpenMyChanges() => _browser.SelectedTab = 1;
}

public enum ExportScope { All, Shown, Selected }
