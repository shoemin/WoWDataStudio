using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Versions.Catalog;
using WoWData.Core.Workspace;

namespace WoWDataStudio.ViewModels;

/// <summary>An update in the list of the Updates tab.</summary>
public sealed record UpdateRow(UpdateItem Item)
{
    public string Title => Item.Title;
    public string Kind => Item.KindText;
    public string Class => Item.ClassText;
    public string Where => Item.Where;
    public int Files => Item.Files;
    public string Size => Item.SizeText;
    public string Import => Item.Importable ? "yes" : "not yet";
}

/// <summary>
/// 0.14.0 (experimental): the Updates tab of Other versions: the update catalog of the open modern client against your
/// 1.12 game (every difference, grouped into updates you can pick, with its portability class), filtered by kind, class,
/// zone or folder and words; the details of an update; Preview import (a dry run) and Import picked updates (one change
/// set in your workspace). The logic is <see cref="UpdateCatalogScanner"/> and <see cref="UpdateImporter"/>, shared with
/// "wds versions updates", "update-info" and "import-updates".
/// </summary>
public sealed partial class UpdateCatalogViewModel : ObservableObject, IDisposable
{
    public const string AllKinds = "All kinds";
    public const string AllClasses = "All classes";

    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private ClientCopy? _copy;
    private string? _listfile;
    private IReadOnlyList<ClientCopy> _found = Array.Empty<ClientCopy>();
    private UpdateCatalogSource? _source;
    private CancellationTokenSource? _cts;
    private string? _previewKey;

    public UpdateCatalogViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        Kinds.Add(AllKinds);
        foreach (var k in Enum.GetValues<UpdateKind>()) Kinds.Add(UpdateNames.Of(k));
        Classes.Add(AllClasses);
        foreach (var c in Enum.GetValues<Portability>()) Classes.Add(UpdateNames.Of(c));
    }

    public ObservableCollection<UpdateRow> Rows { get; } = new();
    public ObservableCollection<string> Kinds { get; } = new();
    public ObservableCollection<string> Classes { get; } = new();

    [ObservableProperty] private string _kindFilter = AllKinds;
    [ObservableProperty] private string _classFilter = AllClasses;
    [ObservableProperty] private string _whereFilter = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _hideUnchanged = true;
    [ObservableProperty] private bool _onlyImportable;
    [ObservableProperty] private UpdateRow? _selectedRow;
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _report = "";
    [ObservableProperty] private bool _canImport;
    [ObservableProperty] private bool _canScan;

    /// <summary>The catalog shown (null before a scan).</summary>
    public UpdateCatalog? Catalog { get; private set; }

    /// <summary>The last preview or import.</summary>
    public UpdateImportResult? Result { get; private set; }

    partial void OnKindFilterChanged(string value) => ApplyFilter();
    partial void OnClassFilterChanged(string value) => ApplyFilter();
    partial void OnWhereFilterChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnHideUnchangedChanged(bool value) => ApplyFilter();
    partial void OnOnlyImportableChanged(bool value) => ApplyFilter();
    partial void OnSelectedRowChanged(UpdateRow? value) => Details = value == null ? Summary : string.Join(Environment.NewLine, value.Item.Lines(40));

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    /// <summary>The client the tab compares (called when a client is opened in the Other versions tab).</summary>
    public void SetClient(ClientCopy? copy, string? listfile, IReadOnlyList<ClientCopy> found)
    {
        _cts?.Cancel();
        _source?.Dispose();
        _source = null;
        _copy = copy;
        _listfile = listfile;
        _found = found;
        Catalog = null;
        Rows.Clear();
        Details = "";
        Report = "";
        Summary = "";
        _previewKey = null;
        CanImport = false;
        CanScan = copy?.Format == ClientFormat.Casc;
        SetStatus(copy == null ? "" : CanScan
            ? $"Click Scan for updates to compare {copy.Name} with your 1.12 game (the first scan takes a minute or two; the result is kept)."
            : $"{copy.Name} is not a modern (CASC) client: the update catalog compares the 1.60 Classic client with your game.");
    }

    private bool Ready()
    {
        if (_copy == null || !CanScan)
        {
            SetStatus("Open the 1.60 Classic client (_classic_beta_) in the list of clients first.", true);
            return false;
        }
        if (_session.Data == null || _session.Workspace == null || _session.Dbc == null)
        {
            SetStatus("Open your 1.12 game folder first (Change World of Warcraft folder...): the catalog compares with it.", true);
            return false;
        }
        return true;
    }

    [RelayCommand]
    private Task ScanAsync() => LoadAsync(false);

    [RelayCommand]
    private Task RescanAsync() => LoadAsync(true);

    /// <summary>Opens both sides and loads the cached catalog, or scans (with progress in the status line).</summary>
    public async Task LoadAsync(bool rescan)
    {
        if (IsBusy || !Ready()) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(p => SetStatus(p));
        try
        {
            var copy = _copy!;
            var data = _session.Data!;
            var cache = Path.Combine(_session.Paths.Root, "cache");
            SetStatus($"Opening {copy.Name} and the names of your 1.12 files...");
            _source ??= await Task.Run(() => UpdateCatalogSource.Open(copy, _listfile, data, _found, cache), ct);
            var src = _source;
            Catalog = await Task.Run(() => UpdateCatalogStore.LoadOrScan(src, rescan, progress, ct), ct);
            Summary = string.Join(Environment.NewLine, Catalog.SummaryLines());
            Details = Summary;
            ApplyFilter();
            SetStatus($"Scanned {Catalog.Scanned:g} ({Catalog.Seconds:0} s). Pick updates in the list (Ctrl or Shift for several), then Preview import." +
                      (Catalog.Notes.Count > 0 ? " Notes: " + string.Join(" ", Catalog.Notes.Take(3)) : ""));
        }
        catch (OperationCanceledException) { SetStatus("Stopped. The catalog was not changed."); }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidDataException or WoWData.Core.Casc.CascMissingException or UnauthorizedAccessException)
        {
            SetStatus($"The catalog could not be made: {e.Message}", true);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>The list after the filters.</summary>
    public void ApplyFilter()
    {
        Rows.Clear();
        if (Catalog == null) return;
        var f = new UpdateFilter
        {
            Kind = KindFilter == AllKinds ? null : UpdateNames.ParseKind(KindFilter),
            Class = ClassFilter == AllClasses ? null : UpdateNames.ParseClass(ClassFilter),
            Where = WhereFilter, Search = Search, HideNoChange = HideUnchanged, OnlyImportable = OnlyImportable,
        };
        foreach (var i in Catalog.Filter(f)) Rows.Add(new UpdateRow(i));
        InvalidatePreview();
    }

    private static List<UpdateItem> Picks(IList? selected, UpdateRow? single)
    {
        var l = selected?.OfType<UpdateRow>().Select(r => r.Item).ToList() ?? new List<UpdateItem>();
        if (l.Count == 0 && single != null) l.Add(single.Item);
        return l;
    }

    private static string KeyOf(List<UpdateItem> picks) => string.Join("|", picks.Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal));

    private void InvalidatePreview()
    {
        _previewKey = null;
        CanImport = false;
    }

    /// <summary>The selection changed in the list: a new preview is needed before importing.</summary>
    public void SelectionChanged(IList? selected)
    {
        var key = KeyOf(Picks(selected, SelectedRow));
        CanImport = _previewKey != null && _previewKey == key;
    }

    private MapConvertTarget Target() => new() { Client = _session.Data!, Workspace = _session.Workspace!, Dbc = _session.Dbc!, Locale = _session.GameLocale };

    private async Task<UpdateImportResult?> RunAsync(List<UpdateItem> picks, bool dryRun)
    {
        if (IsBusy || Catalog == null || _source == null || !Ready()) return null;
        if (picks.Count == 0)
        {
            SetStatus("Pick one or more updates in the list first.", true);
            return null;
        }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(p => SetStatus(p));
        var src = _source;
        var cat = Catalog;
        var target = Target();
        var ids = picks.Select(p => p.Id).ToList();
        try
        {
            return await Task.Run(() => UpdateImporter.Run(src, cat, ids, target, dryRun, progress, ct), ct);
        }
        catch (OperationCanceledException) { SetStatus("Stopped. Nothing was written."); }
        catch (ArgumentException e) { SetStatus(e.Message, true); }
        catch (WorkspaceException e) { SetStatus(e.Message + " Nothing was written.", true); }
        catch (Exception e) when (e is IOException or InvalidDataException or WoWData.Core.Casc.CascMissingException or WoWData.Core.Casc.CascEncryptedException)
        {
            SetStatus($"The import stopped: {e.Message}. Nothing was written.", true);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
        return null;
    }

    [RelayCommand]
    private async Task PreviewAsync(IList? selected)
    {
        var picks = Picks(selected, SelectedRow);
        var res = await RunAsync(picks, dryRun: true);
        if (res == null) return;
        Result = res;
        Report = string.Join(Environment.NewLine, res.Lines());
        bool any = res.Imported.Count > 0;
        _previewKey = res.Ok && any ? KeyOf(picks) : null;
        CanImport = _previewKey != null;
        SetStatus(!any ? "None of the picked updates can be imported yet (see why in the report)."
                  : res.Ok ? $"Preview ready ({res.Elapsed.TotalSeconds:0.0} s): nothing was written. Import picked updates saves {res.FileCount:N0} files into your workspace as one change set."
                  : "The 1.12 check refused converted files: they cannot be saved.", !any || !res.Ok);
    }

    [RelayCommand]
    private async Task ImportAsync(IList? selected)
    {
        var picks = Picks(selected, SelectedRow);
        if (!CanImport || _previewKey != KeyOf(picks))
        {
            SetStatus("Click Preview import first (and again after changing the picks).", true);
            return;
        }
        var res = await RunAsync(picks, dryRun: false);
        if (res == null) return;
        Result = res;
        Report = string.Join(Environment.NewLine, res.Lines());
        InvalidatePreview();
        if (res.ChangeSet is { } cs)
        {
            SetStatus($"Saved {cs.Files.Count:N0} files into your workspace (\"{cs.Label}\"). Revert this import in My changes takes it all back.");
            _main.Toast($"Imported {res.Imported.Count} updates into your workspace: {cs.Files.Count:N0} files. Next: My changes > Test all in game" + (res.Maps.Count > 0 ? ", the release patch and the server steps in the report." : "."));
        }
        else SetStatus("Nothing was saved (see the report).", true);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _source?.Dispose();
    }
}
