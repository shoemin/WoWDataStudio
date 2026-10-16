using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;
using WoWData.Core.Session;
using WoWDataStudio.Services;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

/// <summary>A view of the Server data tab's list (Items, Creatures (NPCs), Quests...).</summary>
public sealed class ServerViewItem
{
    public ServerViewItem(ServerViewDef def) => Def = def;
    public ServerViewDef Def { get; }
    public string Label => Def.Label;
    public string About => Def.About;
    public override string ToString() => Label;
}

public sealed partial class ServerFlagItem : ObservableObject
{
    private readonly ServerFieldView _field;

    public ServerFlagItem(ServerFieldView field, long bit, string name, bool on)
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

    partial void OnIsOnChanged(bool value) => _field.FlagToggled();
}

/// <summary>One value of the selected record in the details panel: an editor, its meaning, its link.</summary>
public sealed partial class ServerFieldView : ObservableObject
{
    private readonly ServerDataViewModel _vm;
    private readonly bool _loading;

    public ServerFieldView(ServerDataViewModel vm, ServerFieldValue f)
    {
        _vm = vm;
        F = f;
        _loading = true;
        _text = f.Value ?? WorldValue.NullText;
        var info = f.Info;
        long v = WorldValue.Long(f.Value);
        if (IsEnum)
        {
            foreach (var (val, name) in info!.Values!.Values) Choices.Add(new DbcChoice(val, $"{val} · {name}"));
            if (Choices.All(c => c.Value != v)) Choices.Insert(0, new DbcChoice(v, $"{v} (not a known value)"));
            _choice = Choices.First(c => c.Value == v);
        }
        if (IsFlags)
            foreach (var (bit, name) in info!.Values!.Values.Where(x => x.Value != 0))
                Flags.Add(new ServerFlagItem(this, bit, name, (v & bit) == bit));
        _boolValue = v != 0;
        _loading = false;
    }

    public ServerFieldValue F { get; }
    public string Name => F.Name;
    public string TypeText => F.TypeText + (F.IsKey ? ", key" : "");
    public string Hint => F.Hint;
    public bool Verified => F.Described && F.Verified;
    public string Display => F.Display;
    public string Problem => F.Problem;
    public bool HasProblem => F.Problem.Length > 0;
    public string Tip => $"{F.Name}: {F.TypeText}" + (F.Hint.Length > 0 ? "\n" + F.Hint : "") + (F.IsKey ? "\nPart of the row's key: changing it moves the row." : "") +
                         (F.Info?.Link != null ? $"\nLinks to: {F.Info.Link}" : "");
    public bool IsEnum => F.Info?.Semantic is ServerSemantic.Enum && F.Info.Values != null && F.Column.IsNumber;
    public bool IsBool => F.Info?.Semantic == ServerSemantic.Bool && F.Column.IsNumber;
    public bool IsFlags => F.Info?.Semantic == ServerSemantic.Flags && F.Info.Values != null && F.Column.IsNumber;
    public bool IsPlainBox => !IsEnum && !IsBool;
    public bool ShowsExtra => !IsEnum && !IsBool && !HasLink && Display != (F.Value ?? "NULL");
    public bool HasLink => F.LinkTarget != null;
    public bool LinkMissing => F.LinkMissing;
    public string LinkText => F.LinkTarget == null ? "" : F.LinkMissing ? $"{F.LinkTarget} {F.LinkId} does not exist" : $"→ {(F.LinkTarget.StartsWith("dbc:", StringComparison.Ordinal) ? F.LinkTarget[4..] + " (game)" : F.LinkTarget)} {F.LinkId}: {Display[(Display.IndexOf('·') + 1)..].Trim()}";
    public List<DbcChoice> Choices { get; } = new();
    public ObservableCollection<ServerFlagItem> Flags { get; } = new();

    [ObservableProperty] private string _text;
    [ObservableProperty] private DbcChoice? _choice;
    [ObservableProperty] private bool _boolValue;

    [RelayCommand]
    private void Commit() => _vm.EditField(this, Text);

    partial void OnChoiceChanged(DbcChoice? value)
    {
        if (!_loading && value != null) _vm.EditField(this, value.Value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (!_loading) _vm.EditField(this, value ? "1" : "0");
    }

    internal void FlagToggled()
    {
        if (_loading) return;
        long named = 0, raw = 0;
        foreach (var x in Flags)
        {
            named |= x.Bit;
            if (x.IsOn) raw |= x.Bit;
        }
        raw |= WorldValue.Long(F.Value) & ~named;   // the bits without a name are kept
        _vm.EditField(this, raw.ToString(CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void FollowLink()
    {
        if (F.LinkTarget != null && !F.LinkMissing) _ = _vm.FollowLinkAsync(F.LinkTarget, F.LinkId);
    }
}

/// <summary>A group of values ("The item", "Price", ...).</summary>
public sealed class ServerFieldGroup
{
    public string Name { get; init; } = "";
    public List<ServerFieldView> Fields { get; init; } = new();
}

/// <summary>One related row of a section (opens it).</summary>
public sealed record ServerSectionRow(string Table, string Key, string Text);

/// <summary>A list of related rows in the details (spawns, loot, the vendor's items...).</summary>
public sealed class ServerSectionView
{
    public ServerSection Section { get; init; } = null!;
    public string Title => $"{Section.Title} ({Section.Rows.Count}{(Section.Truncated ? "+" : "")})";
    public string Note => Section.Note;
    public bool HasNote => Section.Note.Length > 0;
    public List<ServerSectionRow> Rows { get; init; } = new();
    public bool HasRows => Rows.Count > 0;
    public bool CanAdd => Section.AddDefaults.Count > 0;
    public string TableText => Section.Table.Name;
}

/// <summary>
/// 0.9.0: the Server data tab: the server's world database in friendly views (items, creatures, quests, game objects,
/// spells, loot, vendors, trainers, areas, maps, flight paths, factions, languages, every table raw), a record's values
/// with their meaning and links, its related rows, Used by, the game's values side by side, editing with checks and
/// undo / redo, saving into the workspace, the client / server checks, the SQL patch, Apply to server with its undo,
/// CSV / JSON and the game's cache. Everything goes through WoWData.Core.ServerData (the command line uses the same).
/// </summary>
public sealed partial class ServerDataViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private readonly BrowserViewModel _browser;
    private readonly DispatcherTimer _searchTimer;
    private readonly Stack<(string Table, string Key)> _history = new();
    private int _searchToken, _recordToken;

    public ServerDataViewModel(AppSession session, MainViewModel main, BrowserViewModel browser)
    {
        _session = session;
        _main = main;
        _browser = browser;
        Preview = new TextureViewerViewModel(session);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _ = SearchAsync();
        };
        foreach (var v in ServerBrowser.Views) Views.Add(new ServerViewItem(v));
        if (session.Workspace != null) session.Workspace.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnWorkspaceChanged);
    }

    public ServerDataService? Service => _session.ServerData;
    public ServerEditor? Editor { get; private set; }
    public TextureViewerViewModel Preview { get; }

    // ------------------------------------------------------------------ connection

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _connectionText = "Not connected yet.";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isLoading;

    internal void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    public async Task EnsureLoadedAsync()
    {
        if (IsLoaded) return;
        await ConnectAsync();
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        var svc = Service;
        if (svc == null) return;
        IsLoading = true;
        try
        {
            svc.Db.Invalidate();
            var (ok, text, editor) = await Task.Run(() =>
            {
                try
                {
                    var s = svc.Schema;
                    var ed = svc.NewEditor();
                    var running = svc.Running();
                    return (true, $"World database {svc.Db.Describe}: {s.Describe}. The server runs content {svc.PatchText}." +
                                  (running.Count > 0 ? " The world server is running." : " The world server is not running."), ed);
                }
                catch (ServerDataException e)
                {
                    return (false, e.Message + " Check File > Settings... (server database), start the server's database, then click Connect.", (ServerEditor?)null);
                }
            });
            IsConnected = ok;
            ConnectionText = text;
            Editor = editor;
            IsLoaded = true;
            NotifyEditState();
            if (ok)
            {
                var last = _session.Settings.ServerLastView;
                SelectedView = Views.FirstOrDefault(v => v.Def.Id == last) ?? Views[0];
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>After the settings changed: connect again.</summary>
    public async Task ReconnectAsync()
    {
        _session.ResetServerData();
        IsLoaded = false;
        Editor = null;
        await ConnectAsync();
    }

    private void OnWorkspaceChanged()
    {
        // server edits reverted or saved elsewhere (My changes, wds): reload them when nothing is unsaved here
        if (Editor == null || Editor.IsDirty) { NotifyEditState(); return; }
        try
        {
            Editor.Discard();
            NotifyEditState();
            // the record asked for last (a load may be under way)
            if (_requested is { } r) _ = OpenRecordAsync(r.Table, r.Key, false);
        }
        catch (ServerDataException e) { SetStatus(e.Message, true); }
    }

    // ------------------------------------------------------------------ views and the list

    public ObservableCollection<ServerViewItem> Views { get; } = new();
    public ObservableCollection<string> SubTables { get; } = new();
    public ObservableCollection<ServerListItem> Results { get; } = new();
    [ObservableProperty] private ServerViewItem? _selectedView;
    [ObservableProperty] private string? _selectedSubTable;
    [ObservableProperty] private bool _hasSubTables;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _resultsText = "";
    [ObservableProperty] private ServerListItem? _selectedResult;
    [ObservableProperty] private string _viewAbout = "";

    partial void OnSelectedViewChanged(ServerViewItem? value)
    {
        if (value == null) return;
        _session.Settings.ServerLastView = value.Def.Id;
        ViewAbout = value.About;
        SubTables.Clear();
        var tables = value.Def.Id == "tables" ? Service?.Schema.Tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList() ?? new() : value.Def.Tables.ToList();
        foreach (var t in tables) SubTables.Add(t);
        HasSubTables = value.Def.Id is "loot" or "locales" or "tables";
        _selectedSubTable = HasSubTables ? SubTables.FirstOrDefault(t => t is "creature_loot_template" or "locales_item" or "item_template") ?? SubTables.FirstOrDefault() : null;
        OnPropertyChanged(nameof(SelectedSubTable));
        _ = SearchAsync();
    }

    partial void OnSelectedSubTableChanged(string? value) => _ = SearchAsync();

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnSelectedResultChanged(ServerListItem? value)
    {
        if (value != null) _ = OpenRecordAsync(value.Table, value.Key, false);
    }

    public async Task SearchAsync()
    {
        var ed = Editor;
        var view = SelectedView;
        if (ed == null || view == null) return;
        int token = ++_searchToken;
        var text = SearchText;
        var table = SelectedSubTable;
        IsLoading = true;
        try
        {
            var list = await Task.Run(() => new ServerBrowser(ed).Search(view.Def.Id, text, table, 300));
            if (token != _searchToken) return;
            Results.Clear();
            foreach (var r in list) Results.Add(r);
            ResultsText = list.Count == 0 ? "Nothing found." : list.Count >= 300 ? "300 shown (type more of a name to narrow the list)" : $"{list.Count} found";
        }
        catch (ServerDataException e)
        {
            if (token == _searchToken) SetStatus(e.Message, true);
        }
        finally
        {
            if (token == _searchToken) IsLoading = false;
        }
    }

    // ------------------------------------------------------------------ the record

    private (string Table, string Key)? _requested;
    public string? CurrentTable { get; private set; }
    public string? CurrentKey { get; private set; }
    public ServerRecord? Record { get; private set; }
    public ObservableCollection<ServerFieldGroup> FieldGroups { get; } = new();
    public ObservableCollection<ServerSectionView> Sections { get; } = new();
    public ObservableCollection<ServerUsedBy> UsedBy { get; } = new();
    public ObservableCollection<ClientField> ClientFields { get; } = new();
    public ObservableCollection<ServerListItem> Versions { get; } = new();
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailSubtitle = "";
    [ObservableProperty] private string _reloadText = "";
    [ObservableProperty] private bool _hasRecord;
    [ObservableProperty] private bool _hasVersions;
    [ObservableProperty] private bool _hasClient;
    [ObservableProperty] private string _clientText = "";
    [ObservableProperty] private string _usedByText = "";
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewPath = "";
    [ObservableProperty] private bool _canShowInBrowse;
    [ObservableProperty] private bool _recordEdited;
    [ObservableProperty] private string _problemsText = "";
    public bool CanGoBack => _history.Count > 0;

    public async Task OpenRecordAsync(string table, string key, bool remember)
    {
        var ed = Editor;
        if (ed == null) return;
        if (remember && CurrentTable != null && CurrentKey != null && (CurrentTable != table || CurrentKey != key)) _history.Push((CurrentTable, CurrentKey));
        _requested = (table, key);
        OnPropertyChanged(nameof(CanGoBack));
        int token = ++_recordToken;
        IsLoading = true;
        try
        {
            var rec = await Task.Run(() => new ServerBrowser(ed).Record(table, key));
            if (token != _recordToken) return;
            ShowRecord(rec);
        }
        catch (ServerDataException e)
        {
            if (token == _recordToken) SetStatus(e.Message, true);
        }
        finally
        {
            if (token == _recordToken) IsLoading = false;
        }
    }

    /// <summary>The lists shown even when they are empty (to add the first row there).</summary>
    private static readonly HashSet<string> ShownWhenEmpty = new(StringComparer.Ordinal)
    {
        "Spawns", "Sells", "Starts quests", "Ends quests", "Started by creatures", "Ended by creatures", "Sold by", "Loot when killed", "Languages",
    };

    private void ShowRecord(ServerRecord rec)
    {
        Record = rec;
        CurrentTable = rec.Table.Name;
        CurrentKey = rec.Key;
        HasRecord = true;
        DetailTitle = rec.Title;
        DetailSubtitle = rec.Subtitle;
        ReloadText = rec.ReloadText;
        RecordEdited = rec.IsEdited;
        FieldGroups.Clear();
        foreach (var g in rec.Fields.GroupBy(f => f.Group))
            FieldGroups.Add(new ServerFieldGroup { Name = g.Key, Fields = g.Select(f => new ServerFieldView(this, f)).ToList() });
        Sections.Clear();
        foreach (var s in rec.Sections.Where(s => s.Rows.Count > 0 || s.Note.Length > 0 || ShownWhenEmpty.Contains(s.Title)))
            Sections.Add(new ServerSectionView { Section = s, Rows = s.Rows.Select((r, i) => new ServerSectionRow(s.Table.Name, s.Table.KeyText(r), s.RowTexts[i])).ToList() });
        UsedBy.Clear();
        foreach (var u in rec.UsedBy) UsedBy.Add(u);
        UsedByText = rec.UsedBy.Count == 0 ? "No other row refers to it." : $"{rec.UsedBy.Count}{(rec.UsedBy.Count >= 200 ? "+" : "")} row(s) of other tables refer to it:";
        ClientFields.Clear();
        foreach (var c in rec.Client.OrderBy(c => c.Same)) ClientFields.Add(c);
        HasClient = rec.Client.Count > 0;
        int diff = rec.Client.Count(c => !c.Same);
        ClientText = !HasClient ? "" : diff == 0 ? $"The game's {rec.ClientTable}.dbc has the same values." : $"{diff} value(s) differ from the game's {rec.ClientTable}.dbc.";
        Versions.Clear();
        foreach (var v in rec.Versions) Versions.Add(v);
        HasVersions = rec.Versions.Count > 1;
        ProblemsText = string.Join("\n", rec.Problems.Select(p => p.ToString()));
        CanShowInBrowse = rec.BrowseKey != null && _browser.FindEntryByKey(rec.BrowseKey) != null;
        HasPreview = rec.Icon != null && _session.FileExists(rec.Icon);
        PreviewPath = rec.Icon ?? "";
        if (HasPreview) _ = Preview.LoadAsync(rec.Icon!);
        OnPropertyChanged(nameof(CanGoBack));
        NotifyEditState();
    }

    [RelayCommand]
    private async Task Back()
    {
        if (_history.Count == 0) return;
        var (t, k) = _history.Pop();
        OnPropertyChanged(nameof(CanGoBack));
        await OpenRecordAsync(t, k, false);
    }

    [RelayCommand]
    private Task OpenVersion(ServerListItem? v) => v == null ? Task.CompletedTask : OpenRecordAsync(v.Table, v.Key, true);

    [RelayCommand]
    private Task OpenSectionRow(ServerSectionRow? r) => r == null ? Task.CompletedTask : OpenRecordAsync(r.Table, r.Key, true);

    [RelayCommand]
    private Task OpenUsedBy(ServerUsedBy? u) => u == null ? Task.CompletedTask : OpenRecordAsync(u.Table, u.Key, true);

    /// <summary>A link: a server object opens here, a game table row in the Data tables tab.</summary>
    public async Task FollowLinkAsync(string target, long id)
    {
        var ed = Editor;
        if (ed == null) return;
        if (target.StartsWith("dbc:", StringComparison.Ordinal))
        {
            await _browser.ShowTableAsync(target[4..]);
            await _browser.Tables.NavigateAsync(target[4..], id, false);
            return;
        }
        var kind = ed.Service.Layouts.Kind(target);
        if (kind == null) return;
        try
        {
            var t = ed.Table(kind.Table);
            var key = await Task.Run(() => ed.ResolveKey(t, $"{kind.KeyColumn}={id}"));
            await OpenRecordAsync(t.Name, key, true);
        }
        catch (ServerDataException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task ShowInBrowse()
    {
        if (Record?.BrowseKey is { } k && !await _browser.NavigateToKeyAsync(k, false)) SetStatus("Not in the Browse list.", true);
    }

    // ------------------------------------------------------------------ editing

    [ObservableProperty] private string _dirtyText = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private string _undoText = "Nothing to undo";
    [ObservableProperty] private string _redoText = "Nothing to redo";
    [ObservableProperty] private string _editsText = "";

    private void NotifyEditState()
    {
        var ed = Editor;
        if (ed == null)
        {
            IsDirty = false;
            DirtyText = "";
            return;
        }
        var dirty = ed.DirtyTables();
        IsDirty = dirty.Count > 0;
        DirtyText = IsDirty ? $"Unsaved changes: {string.Join(", ", dirty)}" : ed.Changes.Count > 0 ? "Saved in your workspace" : "No changes";
        CanUndo = ed.CanUndo;
        CanRedo = ed.CanRedo;
        UndoText = ed.UndoText;
        RedoText = ed.RedoText;
        EditsText = ed.Changes.Count == 0 ? "You have no server edits." : "Your server edits: " + string.Join("; ", ed.Changes.Select(c => c.Summary));
    }

    private async Task AfterEdit(ServerEditResult r)
    {
        NotifyEditState();
        if (!r.Ok)
        {
            SetStatus(r.Message, true);
            return;
        }
        var warn = r.Warnings.FirstOrDefault();
        SetStatus(warn != null ? $"{r.Message} Note: {warn.Column}: {warn.Message}" : r.Message + " Undo with Ctrl+Z; \"Save to my workspace\" keeps it.");
        if (CurrentTable != null && CurrentKey != null)
        {
            // a key edit moved the row
            var t = Editor!.Table(CurrentTable);
            var key = Editor.Current(t, CurrentKey) != null ? CurrentKey : Editor.ChangesOf(CurrentTable)?.Rows.Values.LastOrDefault(c => c.IsAdded)?.Key ?? CurrentKey;
            await OpenRecordAsync(CurrentTable, key, false);
        }
    }

    public void EditField(ServerFieldView f, string text)
    {
        var ed = Editor;
        if (ed == null || CurrentTable == null || CurrentKey == null) return;
        if (text == (f.F.Value ?? WorldValue.NullText)) return;
        try { _ = AfterEdit(ed.Set(CurrentTable, CurrentKey, f.Name, text == WorldValue.NullText ? null : text)); }
        catch (ServerDataException e) { SetStatus(e.Message, true); }
    }

    /// <summary>Tests and screenshots: change a value of the shown record.</summary>
    public ServerEditResult? TryEdit(string column, string text)
    {
        if (Editor == null || CurrentTable == null || CurrentKey == null) return null;
        var r = Editor.Set(CurrentTable, CurrentKey, column, text);
        NotifyEditState();
        return r;
    }

    [RelayCommand]
    private async Task Undo()
    {
        if (Editor?.Undo() == true) await AfterEdit(new ServerEditResult(true, "Undone.", Array.Empty<ServerProblem>()));
    }

    [RelayCommand]
    private async Task Redo()
    {
        if (Editor?.Redo() == true) await AfterEdit(new ServerEditResult(true, "Redone.", Array.Empty<ServerProblem>()));
    }

    private string MainTable => CurrentTable ?? (SelectedView?.Def.Id == "tables" || HasSubTables ? SelectedSubTable : SelectedView?.Def.Tables.FirstOrDefault()) ?? "item_template";

    [RelayCommand]
    private async Task AddNew()
    {
        var ed = Editor;
        if (ed == null) return;
        var table = SelectedView?.Def.Id == "tables" || HasSubTables ? SelectedSubTable ?? MainTable : SelectedView?.Def.Tables.FirstOrDefault() ?? MainTable;
        try
        {
            var (key, r) = ed.Add(table);
            NotifyEditState();
            if (key != null) await OpenRecordAsync(table, key, true);
            SetStatus(r.Ok ? $"{r.Message} Fill in its values; it gets the first free ID of your custom ID range (Settings)." : r.Message, !r.Ok);
        }
        catch (ServerDataException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task AddInSection(ServerSectionView? s)
    {
        var ed = Editor;
        if (ed == null || s == null) return;
        try
        {
            var (key, r) = ed.Add(s.Section.Table.Name, s.Section.AddDefaults.Select(kv => (kv.Key, kv.Value)));
            NotifyEditState();
            if (key != null) await OpenRecordAsync(s.Section.Table.Name, key, true);
            SetStatus(r.Ok ? r.Message + " Set its values (for example the item), then Back." : r.Message, !r.Ok);
        }
        catch (ServerDataException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task DuplicateAsNew()
    {
        var ed = Editor;
        if (ed == null || CurrentTable == null || CurrentKey == null) return;
        try
        {
            var (key, r) = ed.Duplicate(CurrentTable, CurrentKey);
            NotifyEditState();
            if (key != null) await OpenRecordAsync(CurrentTable, key, true);
            SetStatus(r.Message, !r.Ok);
        }
        catch (ServerDataException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task Delete()
    {
        var ed = Editor;
        if (ed == null || CurrentTable == null || CurrentKey == null) return;
        var used = await Task.Run(() => ed.UsedBy(CurrentTable, new[] { CurrentKey }));
        var msg = $"Delete {DetailTitle} ({CurrentTable} {CurrentKey})?" + (used.Count > 0 ? $"\n\nOther rows refer to it and would point at nothing:\n{string.Join("\n", used.Take(10))}" : "") +
                  "\n\nThe row is deleted in your edits; the server is only changed by \"Apply to server\".";
        if (!ScreenshotRunner.Running && !Dialogs.Confirm("Delete", msg, "Delete", "Cancel", used.Count > 0 ? DialogKind.Warning : DialogKind.Question)) return;
        var r = ed.Delete(CurrentTable, new[] { CurrentKey });
        NotifyEditState();
        SetStatus(r.Message + " Undo with Ctrl+Z.", !r.Ok);
        if (_history.Count > 0) await Back();
        else
        {
            HasRecord = false;
            await SearchAsync();
        }
    }

    [RelayCommand]
    private void BulkEdit(ServerSectionView? section)
    {
        var ed = Editor;
        if (ed == null) return;
        string table;
        List<string> keys;
        if (section != null)
        {
            table = section.Section.Table.Name;
            keys = section.Rows.Select(r => r.Key).ToList();
        }
        else
        {
            var groups = Results.GroupBy(r => r.Table).ToList();
            if (groups.Count == 0) { SetStatus("Search for rows first: Bulk edit changes the rows of the list (or of a list in the details).", true); return; }
            table = groups[0].Key;
            keys = groups[0].Select(r => r.Key).ToList();
        }
        var win = new ServerBulkEditWindow(this, ed.Table(table), keys) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
    }

    /// <summary>The bulk edit window's Apply.</summary>
    public (bool Ok, string Message) BulkEdit(string table, IReadOnlyList<string> keys, string column, BulkOperation op, string value)
    {
        var ed = Editor;
        if (ed == null) return (false, "Not connected.");
        try
        {
            var r = ed.Bulk(table, keys, column, op, value);
            _ = AfterEdit(r);
            return (r.Ok, r.Message);
        }
        catch (ServerDataException e) { return (false, e.Message); }
    }

    [RelayCommand]
    private async Task Save() => await SaveCore(true);

    /// <summary>"Save to my workspace": shows the row differences first (interactive), then saves.</summary>
    public async Task<bool> SaveCore(bool interactive)
    {
        var ed = Editor;
        if (ed == null) return false;
        if (!ed.IsDirty)
        {
            SetStatus("Nothing to save: your server edits are already in your workspace.");
            return true;
        }
        if (interactive && !ScreenshotRunner.Running)
        {
            var diff = CombinedDiff(ed, ed.DirtyTables());
            var win = new DbcDiffWindow("Save server edits to my workspace", diff,
                $"Your edits compared with the server's rows: {diff.Summary} They go into your workspace (My changes); the server is changed only by \"Apply to server\" or an SQL patch.",
                "Save to my workspace", "On the server", "Your edit") { Owner = Application.Current.MainWindow };
            if (win.ShowDialog() != true) return false;
        }
        try
        {
            var (_, msg) = await Task.Run(() => ed.Save());
            NotifyEditState();
            SetStatus(msg);
            return true;
        }
        catch (ServerDataException e)
        {
            SetStatus(e.Message, true);
            if (interactive) Dialogs.Error("Not saved", e.Message);
            return false;
        }
    }

    [RelayCommand]
    private async Task Discard()
    {
        var ed = Editor;
        if (ed == null || !ed.IsDirty) return;
        if (!ScreenshotRunner.Running && !Dialogs.Confirm("Discard changes", "Throw away the server edits you have not saved?", "Discard changes", "Cancel", DialogKind.Warning)) return;
        ed.Discard();
        NotifyEditState();
        SetStatus("Unsaved server edits discarded.");
        if (CurrentTable != null && CurrentKey != null) await OpenRecordAsync(CurrentTable, CurrentKey, false);
    }

    private static DbcDiffResult CombinedDiff(ServerEditor ed, IEnumerable<string> tables)
    {
        var all = new DbcDiffResult();
        foreach (var t in tables)
            if (ed.ChangesOf(t) is { Rows.Count: > 0 } ch)
                foreach (var r in ServerExchange.Diff(ed, ch).Rows) all.Rows.Add(r with { Key = $"{t} {r.Key}" });
        return all;
    }

    [RelayCommand]
    private void CompareWithServer()
    {
        var ed = Editor;
        if (ed == null) return;
        var diff = CombinedDiff(ed, ed.Changes.Select(c => c.Table.Name));
        new DbcDiffWindow("Your server edits and the server", diff, $"Your server edits (saved and unsaved) compared with the server's rows: {diff.Summary}", null, "On the server", "Your edit") { Owner = Application.Current.MainWindow }.Show();
    }

    /// <summary>My changes' Compare of one server-edit file.</summary>
    public void CompareFile(string path)
    {
        var ed = Editor ?? Service?.NewEditor();
        var parsed = ServerChangeFiles.Parse(path);
        if (ed == null || parsed == null) return;
        var ch = ed.ChangesOf(parsed.Value.Table);
        if (ch == null) { Dialogs.Info("Nothing to compare", "These server edits are not in your workspace any more."); return; }
        var diff = ServerExchange.Diff(ed, ch);
        new DbcDiffWindow($"{parsed.Value.Table}: your edits and the server", diff, $"Your edits of the server's {parsed.Value.Table} table (in your workspace) compared with the server's rows: {diff.Summary}", null, "On the server", "Your edit") { Owner = Application.Current.MainWindow }.Show();
    }

    // ------------------------------------------------------------------ export / import

    [RelayCommand]
    private async Task ExportList()
    {
        var ed = Editor;
        if (ed == null) return;
        var groups = Results.GroupBy(r => r.Table).ToList();
        if (groups.Count == 0) { SetStatus("The list is empty: search first.", true); return; }
        var t = ed.Table(groups[0].Key);
        var ids = groups[0].Select(r => r.Key).ToList();
        var rows = await Task.Run(() => ids.Select(k => ed.Current(t, k)).Where(r => r != null).Cast<string?[]>().ToList());
        ExportRows(t, rows);
    }

    [RelayCommand]
    private void ExportSection(ServerSectionView? s)
    {
        if (s != null) ExportRows(s.Section.Table, s.Section.Rows);
    }

    private void ExportRows(WorldTable t, List<string?[]> rows)
    {
        var file = Dialogs.PickSaveFile($"Export {rows.Count} row(s) of {t.Name}", "CSV for spreadsheets (*.csv)|*.csv|JSON (*.json)|*.json", t.Name + ".csv", _session.Settings.DefaultExportDir);
        if (file == null) return;
        try
        {
            ServerExchange.Export(t, rows, file, Service!.Database);
            SetStatus($"Exported {rows.Count:N0} row(s) of {t.Name} to {file}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task Import(ServerSectionView? s)
    {
        var ed = Editor;
        if (ed == null) return;
        var file = Dialogs.PickOpenFile("Import rows (CSV or JSON)", "CSV or JSON (*.csv;*.json)|*.csv;*.json", _session.Settings.DefaultExportDir);
        if (file == null) return;
        var table = s?.Section.Table.Name ?? MainTable;
        var win = new ServerImportWindow(this, ed, table, file, s?.Section.Filter) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() == true)
        {
            NotifyEditState();
            if (CurrentTable != null && CurrentKey != null) await OpenRecordAsync(CurrentTable, CurrentKey, false);
        }
    }

    internal void AfterImport(string message)
    {
        NotifyEditState();
        SetStatus(message + " \"Save to my workspace\" keeps it.");
    }

    // ------------------------------------------------------------------ client / server, SQL, apply, cache

    [RelayCommand]
    private void CheckClientServer()
    {
        var ed = Editor;
        if (ed == null) { SetStatus("Connect to the server's database first.", true); return; }
        new ServerCheckWindow(this, ed, CurrentTable != null ? ServerConsistency.MapOf(CurrentTable)?.Area : null) { Owner = Application.Current.MainWindow }.Show();
    }

    internal void AfterMatch(string message)
    {
        NotifyEditState();
        SetStatus(message);
        if (CurrentTable != null && CurrentKey != null) _ = OpenRecordAsync(CurrentTable, CurrentKey, false);
    }

    private async Task<bool> EnsureSavedAsync(string what)
    {
        var ed = Editor;
        if (ed == null) return false;
        if (!ed.IsDirty) return true;
        if (ScreenshotRunner.Running) return true;
        int choice = Dialogs.Show(what, "You have server edits that are not saved in your workspace yet. Only saved edits are used.", DialogKind.Question, new[] { "Save them first", "Use the saved ones", "Cancel" });
        if (choice == 0) return await SaveCore(false);
        return choice == 1;
    }

    [RelayCommand]
    private async Task ExportSqlPatch()
    {
        var svc = Service;
        if (svc == null || Editor == null || !await EnsureSavedAsync("Export SQL patch")) return;
        var changes = svc.SavedChanges();
        if (changes.Count == 0) { Dialogs.Info("Export SQL patch", "You have no server edits in your workspace yet."); return; }
        var file = Dialogs.PickSaveFile("Export SQL patch", "SQL script (*.sql)|*.sql", $"wds_server_patch_{DateTime.Now:yyyyMMdd_HHmm}.sql", _session.Settings.DefaultExportDir);
        if (file == null) return;
        var script = ServerSql.Build(svc, changes);
        var revert = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".revert.sql");
        File.WriteAllText(file, script.Patch, new System.Text.UTF8Encoding(false));
        File.WriteAllText(revert, script.Revert, new System.Text.UTF8Encoding(false));
        Dialogs.Success("SQL patch exported", $"{file}\n{revert}\n\n{script.Rows} row(s): {script.Summary}. The patch can be run more than once; the revert script puts the rows back as they were.");
        Dialogs.ShowInExplorer(file);
    }

    [RelayCommand]
    private async Task ApplyToServer()
    {
        var svc = Service;
        if (svc == null || Editor == null || !await EnsureSavedAsync("Apply to server")) return;
        var ap = new ServerApplier(svc);
        ServerApplyPlan plan;
        try { plan = await _main.RunBusyAsync("Comparing your edits with the server...", () => Task.Run(() => ap.Plan())); }
        catch (ServerDataException e) { Dialogs.Error("Apply to server", e.Message); return; }
        if (plan.IsEmpty) { Dialogs.Info("Apply to server", "You have no server edits in your workspace yet."); return; }
        var win = new ServerApplyWindow(plan) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        bool overwrite = win.Overwrite;   // read on the UI thread
        try
        {
            var (rec, done) = await _main.RunBusyAsync("Backing up and applying to the server...", () => Task.Run(() => ap.Apply(plan, overwrite)));
            NotifyEditState();
            SetStatus($"Applied {rec.Rows} row(s) to the server.");
            Dialogs.Success("Applied to the server", string.Join("\n\n", done));
        }
        catch (ServerDataException e) { Dialogs.Error("Could not apply to the server", e.Message); }
    }

    [RelayCommand]
    private async Task UndoLastApply()
    {
        var svc = Service;
        if (svc == null) return;
        var ap = new ServerApplier(svc);
        var last = ap.Last();
        if (last == null) { Dialogs.Info("Undo last apply", "Nothing was applied to the server yet (or the last apply was undone)."); return; }
        var conflicts = await Task.Run(() => ap.UndoConflicts(last));
        var msg = $"Put the rows of the apply of {last.Time:yyyy-MM-dd HH:mm} back as they were before it ({last.Rows} row(s): {last.Summary})?\n\nThe rows as they are now are backed up first." +
                  (conflicts.Count > 0 ? $"\n\n{conflicts.Count} row(s) changed on the server since the apply; undoing overwrites them:\n{string.Join("\n", conflicts.Take(8))}" : "");
        if (!Dialogs.Confirm("Undo last apply", msg, conflicts.Count > 0 ? "Undo anyway" : "Undo last apply", "Cancel", conflicts.Count > 0 ? DialogKind.Warning : DialogKind.Question)) return;
        try
        {
            var done = await _main.RunBusyAsync("Putting the rows back...", () => Task.Run(() => ap.Undo(last, conflicts.Count > 0)));
            NotifyEditState();
            Dialogs.Success("Apply undone", string.Join("\n\n", done));
        }
        catch (ServerDataException e) { Dialogs.Error("Could not undo the apply", e.Message); }
    }

    [RelayCommand]
    private void AppliedLog()
    {
        var svc = Service;
        if (svc == null) return;
        var hist = new ServerApplier(svc).History();
        if (hist.Count == 0) { Dialogs.Info("Applied changes log", "Nothing was applied to the server yet."); return; }
        Dialogs.Show("Applied changes log", string.Join("\n", hist.TakeLast(15).Select(h => $"{h.Time:yyyy-MM-dd HH:mm}  {h.Rows} row(s): {h.Summary}" + (h.Undone != null ? $"  (undone {h.Undone:yyyy-MM-dd HH:mm})" : ""))),
            DialogKind.Info, new[] { "OK" }, $"The log: {svc.ApplyLogFile}\nThe backups: {svc.BackupRoot}");
    }

    [RelayCommand]
    private void ClearWdb()
    {
        var client = _session.Client;
        if (client == null) return;
        int pick = Dialogs.Show("Clear the game's cache (WDB)",
            "The game keeps what the server told it about items, creatures, quests and objects in its WDB folder and does not ask again: after a server edit of a name, a stat or a text, the game shows the old one until the cache is cleared.\n\nThe cache files are moved into the app's backup folder (not deleted). Close the game first.",
            DialogKind.Question, new[] { "Clear this game's cache", "Choose another game folder...", "Cancel" }, client.ClientDir);
        string? game = pick switch { 0 => client.ClientDir, 1 => Dialogs.PickFolder("Select the game folder whose cache to clear (the one with WoW.exe)", client.ClientDir), _ => null };
        if (game == null) return;
        var check = WoWData.Core.Workspace.ClientValidator.Check(game);
        if (!check.Ok) { Dialogs.Error("Clear the game's cache (WDB)", check.Message); return; }
        try
        {
            var (_, done) = WdbCache.Clear(check.ClientDir, Path.Combine(client.BackupDir, "wdb"));
            Dialogs.Success("The game's cache", string.Join("\n\n", done));
        }
        catch (Exception e) when (e is ServerDataException or IOException or UnauthorizedAccessException) { Dialogs.Error("Could not clear the cache", e.Message); }
    }

    [RelayCommand]
    private void OpenSettings() => _main.OpenSettingsCommand.Execute(null);

    /// <summary>Show a server table (from My changes, or the Data tables tab's "Open in Server data").</summary>
    public async Task ShowTableAsync(string table, long? id = null)
    {
        await EnsureLoadedAsync();
        var ed = Editor;
        if (ed == null) return;
        var view = ServerBrowser.ViewOf(table);
        SelectedView = Views.FirstOrDefault(v => v.Def.Id == view) ?? SelectedView;
        if (HasSubTables && SubTables.Contains(table)) SelectedSubTable = table;
        if (id != null && ed.Schema.Table(table) is { } t)
        {
            // the same row as in the Data tables tab (Spell 133 -> spell_template 133)
            try
            {
                var key = await Task.Run(() => ed.ResolveKey(t, id.Value.ToString(CultureInfo.InvariantCulture)));
                await OpenRecordAsync(table, key, false);
                return;
            }
            catch (ServerDataException e) { SetStatus(e.Message, true); }
        }
        var first = ed.ChangesOf(table)?.Rows.Values.FirstOrDefault(r => r.After != null);
        if (first != null) await OpenRecordAsync(table, first.Key, false);
    }
}
