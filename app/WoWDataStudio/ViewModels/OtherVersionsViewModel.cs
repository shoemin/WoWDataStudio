using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Mpq;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWDataStudio.Helpers;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A client in the list of the Other versions tab.</summary>
public sealed record ClientRow(ClientCopy Copy)
{
    public string Name => Copy.Name;
    public string Version => Copy.Version;
    public int Build => Copy.Build;
    public string Release => Copy.Release;
    public string Data => Copy.Format.ToString().ToUpperInvariant();
    public string ReadableText => Copy.Readable ? "yes" : "not yet";
}

public sealed record VersionFileRow(string Path, string Archive);

public sealed record VersionTableRow(string Name, int Rows, int Fields, string Columns);

public sealed record ArchiveRow(int Order, string Name, string Note);

public sealed class CompareClientChoice : ObservableObject
{
    public ClientCopy Copy { get; init; } = null!;
    public string Label => $"{Copy.Version} · {Copy.Name}";
    private bool _checked = true;
    public bool IsChecked { get => _checked; set => SetProperty(ref _checked, value); }
}

/// <summary>
/// 0.14.0 (experimental): the Other versions tab. Finds World of Warcraft clients of any version in a folder and reads
/// the MPQ ones (1.x to 3.x): their archives in the order that client reads them, their files (texture preview,
/// extract), their data tables with that version's layout (raw columns where it is not known yet), and one row compared
/// across versions. Nothing in those clients is ever changed. Shares <see cref="ClientCopies"/>, <see cref="OtherClient"/>
/// and <see cref="VersionCompare"/> with "wds versions".
/// </summary>
public sealed partial class OtherVersionsViewModel : ObservableObject, IDisposable
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private OtherClient? _open;
    private bool _loaded;

    public OtherVersionsViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        _folder = session.Settings.OtherClientsDir ?? "";
        _blizzardOnly = session.Settings.OtherClientsBlizzardOnly;
    }

    public ObservableCollection<ClientRow> Clients { get; } = new();
    public ObservableCollection<ArchiveRow> Archives { get; } = new();
    public ObservableCollection<VersionFileRow> Files { get; } = new();
    public ObservableCollection<VersionTableRow> Tables { get; } = new();
    public ObservableCollection<CompareClientChoice> CompareClients { get; } = new();

    [ObservableProperty] private string _folder;
    [ObservableProperty] private bool _blizzardOnly;
    [ObservableProperty] private ClientRow? _selectedClient;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _clientSummary = "";
    [ObservableProperty] private string _filePattern = @"World\Maps\*.wdt";
    [ObservableProperty] private string _fileCount = "";
    [ObservableProperty] private VersionFileRow? _selectedFile;
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private VersionTableRow? _selectedTable;
    [ObservableProperty] private DataView? _tableRows;
    [ObservableProperty] private string _tableNote = "";
    [ObservableProperty] private string _rowFilter = "";
    [ObservableProperty] private string _compareTable = "Map";
    [ObservableProperty] private string _compareId = "0";
    [ObservableProperty] private bool _compareOnlyDifferences;
    [ObservableProperty] private DataView? _compareRows;
    [ObservableProperty] private string _compareNote = "";

    public bool HasClient => _open != null;

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        if (Folder.Length > 0 && Directory.Exists(Folder)) await FindClientsAsync();
        else SetStatus("Pick the folder that holds your copies of other World of Warcraft versions, then \"Find clients\". They are only read, never changed.");
    }

    partial void OnBlizzardOnlyChanged(bool value)
    {
        _session.Settings.OtherClientsBlizzardOnly = value;
        SaveSettings();
        if (SelectedClient != null) Opening = OpenClientAsync(SelectedClient);
    }

    partial void OnSelectedClientChanged(ClientRow? value)
    {
        if (value != null) Opening = OpenClientAsync(value);
    }

    /// <summary>The opening of the selected client (the screenshots wait for it).</summary>
    public Task Opening { get; private set; } = Task.CompletedTask;

    partial void OnSelectedFileChanged(VersionFileRow? value) => ShowPreview(value);

    partial void OnSelectedTableChanged(VersionTableRow? value)
    {
        if (value != null) LoadTable(value.Name);
    }

    partial void OnRowFilterChanged(string value) => ApplyRowFilter();

    private void SaveSettings()
    {
        try { _session.Settings.Save(_session.Paths); }
        catch (IOException) { }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var f = Dialogs.PickFolder("The folder with copies of other World of Warcraft versions", Folder.Length > 0 ? Folder : null);
        if (f == null) return;
        Folder = f;
        _ = FindClientsAsync();
    }

    [RelayCommand]
    private async Task FindClientsAsync()
    {
        if (!Directory.Exists(Folder))
        {
            SetStatus($"The folder \"{Folder}\" does not exist.", true);
            return;
        }
        _session.Settings.OtherClientsDir = Folder;
        SaveSettings();
        var folder = Folder;
        var found = await _main.RunBusyAsync("Looking for World of Warcraft clients...", () => Task.Run(() => ClientCopies.Find(folder)));
        Clients.Clear();
        CompareClients.Clear();
        foreach (var c in found) Clients.Add(new ClientRow(c));
        foreach (var c in found.Where(c => c.Readable).GroupBy(c => (c.Version, c.Build)).Select(g => g.First()).OrderBy(c => c.Build))
            CompareClients.Add(new CompareClientChoice { Copy = c });
        SetStatus(found.Count == 0 ? "No World of Warcraft client was found in that folder." :
            $"{found.Count} clients found, {found.Count(c => c.Readable)} readable." + (found.Any(c => c.Format == ClientFormat.Casc) ? " CASC clients (6.x and later, Battle.net) are listed but cannot be read yet." : ""));
    }

    private async Task OpenClientAsync(ClientRow row)
    {
        _open?.Dispose();
        _open = null;
        Archives.Clear();
        Files.Clear();
        Tables.Clear();
        TableRows = null;
        Preview = null;
        PreviewText = "";
        OnPropertyChanged(nameof(HasClient));
        if (!row.Copy.Readable)
        {
            ClientSummary = $"{row.Name}: World of Warcraft {row.Version} (build {row.Build}), {row.Release}. Its data is stored in {row.Data}, which cannot be read yet.";
            return;
        }
        try
        {
            var bo = BlizzardOnly;
            var (oc, tables) = await _main.RunBusyAsync($"Opening {row.Name}...", () => Task.Run(() =>
            {
                var o = OtherClient.Open(row.Copy, bo);
                var list = new List<VersionTableRow>();
                foreach (var n in o.TableNames())
                {
                    try
                    {
                        var t = o.Table(n);
                        if (t != null) list.Add(new VersionTableRow(n, t.Rows.Count, t.FieldCount, t.Layout.IsFallback ? "raw" : t.Layout.CoverageText));
                    }
                    catch (DbcException) { list.Add(new VersionTableRow(n, 0, 0, "unreadable")); }
                }
                o.Data.Names();
                return (o, list);
            }));
            _open = oc;
            OnPropertyChanged(nameof(HasClient));
            int i = 1;
            foreach (var a in ClientData.ClientArchiveOrder(row.Copy.DataDir))
            {
                bool custom = oc.CustomArchives.Contains(a, StringComparer.OrdinalIgnoreCase);
                Archives.Add(new ArchiveRow(i++, a, custom ? oc.BlizzardOnly ? "not Blizzard's: left out" : "not Blizzard's: a server's or a mod's patch" : ""));
            }
            foreach (var t in tables) Tables.Add(t);
            var c = row.Copy;
            ClientSummary = $"{c.Name}: World of Warcraft {c.Version} (build {c.Build}), {c.Release}{(c.Locale != null ? ", " + c.Locale : "")}. " +
                            $"{oc.Data.Names().Count:N0} files, {tables.Count} tables ({tables.Count(t => t.Columns != "raw" && t.Columns != "unreadable")} with named columns).";
            SetStatus(oc.CustomArchives.Count > 0 && !oc.BlizzardOnly
                ? $"{string.Join(", ", oc.CustomArchives)} is not Blizzard's and replaces some of the game's files. Tick \"Blizzard archives only\" to read the game as Blizzard shipped it."
                : $"{c.Name} is open (read only).");
            SearchFiles();
        }
        catch (Exception e) when (e is IOException or MpqException or NotSupportedException or UnauthorizedAccessException)
        {
            SetStatus($"{row.Name} could not be opened: {e.Message}", true);
        }
    }

    [RelayCommand]
    private void SearchFiles()
    {
        Files.Clear();
        if (_open == null) return;
        var names = _open.Data.Names();
        var hits = _open.Data.Glob(FilePattern.Replace('/', '\\'));
        foreach (var f in hits.Take(5000)) Files.Add(new VersionFileRow(f, names.TryGetValue(f.ToUpperInvariant(), out var lf) ? lf.Archive : ""));
        FileCount = hits.Count > 5000 ? $"{hits.Count:N0} files match; the first 5,000 are listed (narrow the pattern)." : $"{hits.Count:N0} files match.";
    }

    private void ShowPreview(VersionFileRow? f)
    {
        Preview = null;
        PreviewText = "";
        if (f == null || _open == null) return;
        try
        {
            var data = _open.Data.Read(f.Path);
            if (data == null) return;
            if (f.Path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            {
                var blp = BlpFile.Parse(data);
                var img = blp.Decode();
                Preview = ImageSources.ToBitmap(img);
                PreviewText = $"{img.Width} x {img.Height}, {data.Length:N0} bytes, from {f.Archive}";
            }
            else PreviewText = $"{data.Length:N0} bytes, from {f.Archive}";
        }
        catch (Exception e) when (e is BlpException or MpqException or IOException)
        {
            PreviewText = $"Cannot show it: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task ExtractSelectedAsync(System.Collections.IList? selected)
    {
        if (_open == null) return;
        var items = selected?.OfType<VersionFileRow>().ToList() ?? new List<VersionFileRow>();
        if (items.Count == 0 && SelectedFile != null) items.Add(SelectedFile);
        if (items.Count == 0)
        {
            SetStatus("Select one or more files first.", true);
            return;
        }
        var dest = Dialogs.PickFolder("Where to copy the files (their folders are kept)", _session.Settings.ExportDir);
        if (dest == null) return;
        var full = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        if (full.StartsWith(_open.Copy.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("That folder is inside the client: other versions are only read. Pick another folder.", true);
            return;
        }
        var oc = _open;
        long bytes = await _main.RunBusyAsync($"Copying {items.Count} files...", () => Task.Run(() =>
        {
            long b = 0;
            foreach (var it in items)
            {
                var data = oc.Data.Read(it.Path);
                if (data == null) continue;
                var outPath = Path.Combine(dest, it.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, data);
                b += data.Length;
            }
            return b;
        }));
        _main.Toast($"{items.Count} files ({bytes:N0} bytes) copied to {dest}.");
    }

    private DbcTable? _table;

    private void LoadTable(string name)
    {
        TableRows = null;
        TableNote = "";
        _table = null;
        if (_open == null) return;
        try
        {
            var t = _open.Table(name);
            if (t == null) return;
            _table = t;
            var dt = new DataTable(name);
            foreach (var c in t.Layout.Columns) dt.Columns.Add(c.Name.Replace(".", "_"), typeof(string)).Caption = c.Name;
            // one line per row: a text with line breaks (battleground descriptions) shows them as " / "
            foreach (var r in t.Rows) dt.Rows.Add(t.Layout.Columns.Select(c => (object)OneLine(t.Format(r, c))).ToArray());
            TableRows = dt.DefaultView;
            TableNote = t.Layout.IsFallback
                ? $"{name} has no known layout for {_open.Copy.Version} yet: every field is shown as a raw number (col_N), texts as their offsets."
                : $"{name}: {t.Rows.Count:N0} rows, {t.Layout.CoverageText} for {_open.Copy.Version}. Read only.";
            ApplyRowFilter();
        }
        catch (DbcException e)
        {
            TableNote = $"{name} could not be read: {e.Message}";
        }
    }

    private static string OneLine(string s) => s.IndexOf('\n') < 0 ? s : s.Replace("\r\n", "\n").Replace("\n", " / ");

    private void ApplyRowFilter()
    {
        if (TableRows == null) return;
        var f = RowFilter.Trim().Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
        TableRows.RowFilter = f.Length == 0 ? "" : string.Join(" OR ", TableRows.Table!.Columns.Cast<DataColumn>().Select(c => $"[{c.ColumnName}] LIKE '%{f}%'"));
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        CompareRows = null;
        CompareNote = "";
        if (!long.TryParse(CompareId.Trim(), out var id))
        {
            CompareNote = "The ID must be a whole number.";
            return;
        }
        var picks = CompareClients.Where(c => c.IsChecked).Select(c => c.Copy).ToList();
        if (picks.Count == 0)
        {
            CompareNote = "Tick at least one version.";
            return;
        }
        var table = CompareTable.Trim();
        var bo = BlizzardOnly;
        var cmp = await _main.RunBusyAsync($"Reading {table} {id} in {picks.Count} versions...", () => Task.Run(() =>
        {
            var opened = new List<OtherClient>();
            try
            {
                foreach (var p in picks) opened.Add(OtherClient.Open(p, bo));
                return VersionCompare.Row(table, id, opened.Select(o => (o.Copy.Version, o.Table(table))).ToList());
            }
            finally
            {
                foreach (var o in opened) o.Dispose();
            }
        }));
        var dt = new DataTable("compare");
        dt.Columns.Add("Column", typeof(string));
        var heads = cmp.Clients.Select((c, i) => cmp.Clients.Take(i).Contains(c) ? $"{c} ({i + 1})" : c).ToList();
        foreach (var h in heads) dt.Columns.Add("v" + h.Replace(".", "_").Replace(" ", "_").Replace("(", "").Replace(")", ""), typeof(string)).Caption = h;
        foreach (var (col, vals) in cmp.Columns)
        {
            if (CompareOnlyDifferences && vals.Distinct().Count() <= 1) continue;
            dt.Rows.Add(new object[] { col }.Concat(vals.Select(v => (object)(v == null ? "-" : OneLine(v)))).ToArray());
        }
        CompareRows = dt.DefaultView;
        CompareNote = cmp.Notes.Count == 0 ? $"{table} {id}: {cmp.Columns.Count} named columns in {cmp.Clients.Count} versions." : string.Join(" ", cmp.Notes);
    }

    public void Dispose() => _open?.Dispose();
}
