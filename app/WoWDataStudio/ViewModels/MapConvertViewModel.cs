using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;

namespace WoWDataStudio.ViewModels;

/// <summary>A map of the source in the "Convert map..." window.</summary>
public sealed record SourceMapChoice(SourceMap Map)
{
    public string Label => $"{Map.Name} ({Map.Id}, {Map.Directory})";
}

/// <summary>
/// 0.14.0: the "Convert map..." window of the Other versions tab: pick a map of the 1.60 client, a zone (with its
/// subzones), tiles or the whole map, look at the conversion report (a dry run), then convert: the files go into your
/// 1.12 workspace as one change set (My changes, Test in game and the release patch take over). The logic is
/// <see cref="MapConverter"/>, shared with "wds versions convert-map".
/// </summary>
public sealed partial class MapConvertViewModel : ObservableObject, IDisposable
{
    private readonly AppSession _session;
    private readonly ClientCopy _copy;
    private readonly string? _listfile;
    private readonly IReadOnlyList<ClientCopy> _found;
    private MapConversionSource? _source;
    private string? _previewKey;
    private CancellationTokenSource? _cts;

    public MapConvertViewModel(AppSession session, ClientCopy copy, string? listfile, IReadOnlyList<ClientCopy> found)
    {
        _session = session;
        _copy = copy;
        _listfile = listfile;
        _found = found;
        Heading = $"Convert a map of {copy.Name} ({copy.Version}.{copy.Build}) into your 1.12 game";
    }

    public string Heading { get; }
    public ObservableCollection<SourceMapChoice> Maps { get; } = new();
    public ObservableCollection<string> Zones { get; } = new();

    [ObservableProperty] private SourceMapChoice? _selectedMap;
    [ObservableProperty] private bool _byZone = true;
    [ObservableProperty] private bool _byTiles;
    [ObservableProperty] private bool _wholeMap;
    [ObservableProperty] private string _zoneText = "";
    [ObservableProperty] private string _tilesText = "";
    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _namesText = "";
    [ObservableProperty] private bool _canConvert;
    [ObservableProperty] private string _resultText = "";

    /// <summary>The last result (preview or conversion).</summary>
    public MapConversionResult? Result { get; private set; }

    /// <summary>Set after a conversion was saved (the window then closes).</summary>
    public ChangeSet? Saved { get; private set; }

    partial void OnSelectedMapChanged(SourceMapChoice? value)
    {
        Zones.Clear();
        Invalidate();
        if (value == null || _source == null) return;
        foreach (var z in MapConverter.ZonesOf(_source.Source, value.Map)) Zones.Add(z.Name);
        if (ByZone && Zones.Count > 0 && !Zones.Contains(ZoneText, StringComparer.OrdinalIgnoreCase)) ZoneText = Zones[0];
        if (Zones.Count == 0 && ByZone) WholeMap = true;
    }

    partial void OnByZoneChanged(bool value) => Invalidate();
    partial void OnByTilesChanged(bool value) => Invalidate();
    partial void OnWholeMapChanged(bool value) => Invalidate();
    partial void OnZoneTextChanged(string value) => Invalidate();
    partial void OnTilesTextChanged(string value) => Invalidate();

    private void Invalidate() => CanConvert = _previewKey != null && _previewKey == Key();

    private string Key() => $"{SelectedMap?.Map.Id}|{(ByZone ? "z:" + ZoneText.Trim() : ByTiles ? "t:" + TilesText.Trim() : "all")}";

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    private Task? _loading;

    /// <summary>Opens the client and its names (the first time: about a second; building the name table once: a few
    /// seconds). Called when the window opens; later calls wait for the same load.</summary>
    public Task LoadAsync() => _loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        if (_session.Data == null || _session.Workspace == null || _session.Dbc == null)
        {
            SetStatus("Open your 1.12 game folder first (Change World of Warcraft folder...): the conversion goes into its workspace.", true);
            return;
        }
        IsBusy = true;
        SetStatus($"Opening {_copy.Name} and the names of your 1.12 files...");
        try
        {
            var data = _session.Data;
            var cache = Path.Combine(_session.Paths.Root, "cache", "names");
            _source = await Task.Run(() => MapConversionSource.Open(_copy, _listfile, data, _found, cache));
            var maps = await Task.Run(() => _source.Source.Maps().Where(m => m.WdtFileDataId != 0).ToList());
            foreach (var m in maps) Maps.Add(new SourceMapChoice(m));
            NamesText = "Names: " + _source.Source.NamesText + ".";
            SelectedMap = Maps.FirstOrDefault(m => m.Map.Id == 0) ?? Maps.FirstOrDefault();
            SetStatus($"{maps.Count} maps with terrain. Pick one and what to convert, then click Preview.");
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidDataException or WoWData.Core.Casc.CascMissingException or UnauthorizedAccessException)
        {
            SetStatus($"{_copy.Name} cannot be converted from: {e.Message}", true);
        }
        finally { IsBusy = false; }
    }

    private MapConvertRequest? Request()
    {
        if (SelectedMap == null)
        {
            SetStatus("Pick a map first.", true);
            return null;
        }
        if (ByZone && ZoneText.Trim().Length == 0)
        {
            SetStatus("Type or pick a zone.", true);
            return null;
        }
        List<(int X, int Y)>? tiles = null;
        if (ByTiles)
        {
            try { tiles = MapConverter.ParseTiles(TilesText); }
            catch (ArgumentException e)
            {
                SetStatus(e.Message, true);
                return null;
            }
        }
        return new MapConvertRequest { Map = SelectedMap.Map.Id.ToString(), Zone = ByZone ? ZoneText.Trim() : null, Tiles = tiles };
    }

    private MapConvertTarget Target() => new() { Client = _session.Data!, Workspace = _session.Workspace!, Dbc = _session.Dbc!, Locale = _session.GameLocale };

    private async Task<MapConversionResult?> RunAsync(bool dryRun)
    {
        if (_source == null) return null;
        var req = Request();
        if (req == null) return null;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(p => SetStatus(p));
        var target = Target();
        var src = _source.Source;
        var ct = _cts.Token;
        try
        {
            return await Task.Run(() => MapConverter.Run(src, req, target, dryRun, progress, ct));
        }
        catch (ArgumentException e) { SetStatus(e.Message, true); }
        catch (WorkspaceException e) { SetStatus(e.Message, true); }
        catch (OperationCanceledException) { SetStatus("Stopped. Nothing was written."); }
        catch (Exception e) when (e is IOException or InvalidDataException or WoWData.Core.Casc.CascMissingException or WoWData.Core.Casc.CascEncryptedException)
        {
            SetStatus($"The conversion stopped: {e.Message}. Nothing was written.", true);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
        return null;
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        var key = Key();
        var res = await RunAsync(dryRun: true);
        if (res == null) return;
        Result = res;
        ReportText = string.Join(Environment.NewLine, res.Lines().Concat(new[] { "", "Then:" }).Concat(res.ServerSteps.Select(s => "  " + s)));
        _previewKey = res.Ok ? key : null;
        Invalidate();
        SetStatus(res.Ok ? $"Preview ready ({res.Elapsed.TotalSeconds:0.0} s): nothing was written. Convert saves {res.Files.Count} files into your workspace as one change set."
                         : "The 1.12 check refused converted files: they cannot be saved.", !res.Ok);
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsBusy) return;
        if (!CanConvert)
        {
            SetStatus("Click Preview first (and again after changing the map or the selection).", true);
            return;
        }
        var res = await RunAsync(dryRun: false);
        if (res?.ChangeSet == null) return;
        Result = res;
        Saved = res.ChangeSet;
        _previewKey = null;
        CanConvert = false;
        ResultText = $"Saved {res.ChangeSet.Files.Count} files into your workspace (\"{res.ChangeSet.Label}\"). Revert this import in My changes takes it all back.";
        SetStatus(ResultText);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _source?.Dispose();
    }
}
