using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Versions;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A file of an entry of another version: name (or #FileDataID), role, FileDataID, name source, state.</summary>
public sealed class OtherFileRow
{
    public OtherFileRow(ModernFileRef f) => File = f;
    public ModernFileRef File { get; }
    public string Path => File.Path;
    public string FileName => System.IO.Path.GetFileName(File.Path);
    public string Role => File.Role;
    public string FileDataId => File.FileDataId.ToString();
    public string NameSource => NameSources.Short(File.Source);
    public string State => File.State;
    public bool IsLocked => File.State != "local";
    public string Tip => $"{File.Path}\nFileDataID {File.FileDataId}\n{NameSources.Describe(File.Source)}\n{File.State}";
}

/// <summary>
/// Another version opened read only in this window (docs\manual\other-versions.md): the same tabs as your 1.12 game.
/// What differs is kept here: the read-only state and its reason (every write is greyed out with
/// <see cref="WriteRefusal"/>, the same text "wds" prints when it refuses), the server provider's state ("no server
/// connected"), the status line with the locked records and the name sources, an entry's files with their
/// FileDataIDs, the building preview of a WMO, the read-only Data tables tab and the exports to a folder.
/// For your 1.12 game none of this shows and nothing of the 1.12 behaviour changes.
/// </summary>
public sealed partial class BrowserViewModel
{
    /// <summary>True while another version is open (read only).</summary>
    public bool IsReadOnly => _session.IsReadOnly;
    /// <summary>The reason on every greyed-out write (null for your 1.12 game: nothing is greyed out).</summary>
    public string? WriteRefusal => _session.WriteRefusal;
    /// <summary>The reason on a control that needs the world server (another version: its server provider's "no server
    /// connected"), null for your 1.12 game.</summary>
    public string? ServerRefusal => _session.IsReadOnly ? _session.Server?.Detail : null;
    /// <summary>The reason on a feature that is not there for other versions yet (it reads 1.12 files), null for 1.12.</summary>
    public string? NotYetRefusal => _session.IsReadOnly ? $"Not for {_session.Profile?.VersionBuild} yet: this works on your 1.12 game only. {GameProfileText}" : null;
    private string GameProfileText => "Open your 1.12 game again with File > Back to your 1.12 game.";

    /// <summary>The line under the app's name in the top bar.</summary>
    public string GameTitle => _session.Profile is { IsOwnClient: false } p
        ? $"World of Warcraft {p.VersionBuild} ({p.Copy?.Release}) · read only"
        : "World of Warcraft 1.12 game data";

    /// <summary>Which version is open, for the read-only banner.</summary>
    public string ReadOnlyBanner => _session.Profile is { IsOwnClient: false } p
        ? $"{p.Copy?.Name} {p.VersionBuild} is open read only, with the same tabs as your game. {GameProfile.WriteReason} Exports to a folder work (textures as PNG, models for Blender, pictures, rows as CSV or JSON)."
        : "";

    /// <summary>What the Server data tab and the designers show for another version: its server provider's state.</summary>
    public string ServerMissingText => _session.Server?.Detail ?? "";

    /// <summary>What the designer tabs show for another version.</summary>
    public string DesignerMissingText => _session.Profile is { IsOwnClient: false } p
        ? $"The designers make and change spells, classes, talents and quests of your 1.12 game (its data tables and its server). {p.WriteRefusal} {ServerMissingText}"
        : "";

    // ------------------------------------------------------------------ status line (locked records, names)

    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _namesLine = "";

    /// <summary>The tips of the type buttons while another version is open (what it lists).</summary>
    private static readonly Dictionary<AssetKind, string> OtherChipTips = new()
    {
        [AssetKind.Creature] = "Creature displays (by model and skin), and the few creatures the client names itself.",
        [AssetKind.Character] = "Player characters of other versions come later: none are listed.",
        [AssetKind.Item] = "Items the client names, and item displays with their models, textures and icons.",
        [AssetKind.GameObject] = "Game object displays, and the objects the client places itself (signposts...).",
        [AssetKind.WorldObject] = @"Named models and buildings under World\.",
        [AssetKind.SpellEffect] = "Spell effect models (SpellVisualEffectName) and the spells that show them.",
        [AssetKind.Texture] = "Named textures (.blp).",
    };

    private void InitReadOnly()
    {
        if (!_session.IsReadOnly) return;
        try
        {
            var st = OtherVersionStatus.Of(_session);
            StatusLine = st.StatusLine;
            NamesLine = st.NamesLine + (st.FileEntriesLeftOut > 0 ? $"; {st.FileEntriesLeftOut:N0} more named files are left out of the list (\"wds find files ... --version\" lists them)" : "");
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            StatusLine = "";
            NamesLine = "";
        }
        BuildingScene.Background = new System.Numerics.Vector3(0.55f, 0.68f, 0.82f);
    }

    // ------------------------------------------------------------------ the files of an entry (FileDataID, name source, state)

    [ObservableProperty] private List<OtherFileRow> _files = new();
    public bool HasFiles => Files.Count > 0;
    partial void OnFilesChanged(List<OtherFileRow> value) => OnPropertyChanged(nameof(HasFiles));

    // ------------------------------------------------------------------ building preview (a WMO of another version)

    public Rendering.TerrainScene BuildingScene { get; } = new();
    public Rendering.OrbitCamera BuildingCamera { get; } = new();
    [ObservableProperty] private bool _isBuildingEntry;
    [ObservableProperty] private string _buildingStatus = "";
    private int _buildingToken;

    /// <summary>Shows a building (root .wmo) in the preview: its groups by FileDataID with the first texture of each batch.</summary>
    public async Task LoadBuildingAsync(string path)
    {
        int token = ++_buildingToken;
        BuildingStatus = "Loading the building...";
        BuildingScene.Asset = null;
        var files = _session.Files;
        var looks = _session.Looks;
        if (files == null || looks == null) return;
        try
        {
            var (asset, geo) = await Task.Run(() =>
            {
                Func<uint, string, string> nameOf = (id, ext) => WoWData.Core.M2.Modern.ModernModelSource.NameOf(files, id, ext);
                var g = WoWData.Core.Maps.WmoGeometry.Load(path, p => _session.ReadCurrent(p), nameOf);
                var a = Rendering.TerrainAsset.BuildBuilding(path, p => _session.ReadCurrent(p), looks.LoadBlp, nameOf);
                return (a, g);
            });
            if (token != _buildingToken) return;
            BuildingScene.Asset = asset;
            BuildingCamera.HomeYaw = 30;
            BuildingCamera.HomePitch = 25;
            BuildingCamera.Frame(asset.BoundsMin, asset.BoundsMax);
            BuildingStatus = BuildingText(asset, geo);
            // its textures in "Textures it uses" (click one to see it in the texture viewer)
            foreach (var tp in asset.TexturePaths)
                if (!Textures.Any(t => string.Equals(t.Path, tp, StringComparison.OrdinalIgnoreCase)))
                    Textures.Add(new TextureItem(new TextureRef(tp, "building texture", "CASC", ""), false));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            if (token == _buildingToken) BuildingStatus = "The building could not be shown: " + e.Message;
        }
    }

    /// <summary>What the building preview shows, in words.</summary>
    public static string BuildingText(Rendering.TerrainAsset a, WoWData.Core.Maps.WmoGeometry? geo)
    {
        var mesh = a.Wmos.Count > 0 ? a.Wmos[0].Mesh : null;
        int tris = mesh == null ? 0 : mesh.Indices.Length / 3;
        int groups = geo?.Groups.Count ?? 0, total = geo?.GroupCount ?? 0;
        var t = $"{groups} of {total} groups, {tris:N0} triangles, {a.Textures.Count(x => x != null)} of {a.TexturePaths.Count} textures";
        if (geo != null && geo.DoodadModels.Count > 0) t += $"; {geo.DoodadModels.Count} doodad models listed (MODI), not drawn";
        if (geo?.FileDataIdMode == true) t += "; groups and textures by FileDataID (GFID, MOMT)";
        return t + $". Built in {a.BuildTime.TotalMilliseconds:0} ms.";
    }

    [RelayCommand]
    private void ResetBuildingView() => BuildingCamera.ResetView();

    /// <summary>Clears the building preview (another entry was selected).</summary>
    private void ClearBuilding()
    {
        ++_buildingToken;
        IsBuildingEntry = false;
        BuildingScene.Asset = null;
    }

    /// <summary>The details of another version's entry that the 1.12 panel does not have: its files and, for a
    /// building, the building preview. Returns true when the entry is a building (no 3D model view).</summary>
    private bool ShowOtherDetails(IndexEntry entry, EntryDetails d)
    {
        Files = d.Files.Select(f => new OtherFileRow(f)).ToList();
        var building = entry.Kind == AssetKind.Texture || d.Models.Any(m => m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) ? null
            : d.Models.FirstOrDefault(m => m.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase));
        IsBuildingEntry = building != null;
        if (building != null) _ = LoadBuildingAsync(building);
        else ClearBuilding();
        return building != null;
    }

    // ------------------------------------------------------------------ Data tables tab (read only)

    private VersionTablesViewModel? _versionTables;
    /// <summary>The Data tables tab of another version (its DB2 tables, read only), made on first use.</summary>
    public VersionTablesViewModel VersionTables => _versionTables ??= new VersionTablesViewModel(_session, _main);

    // ------------------------------------------------------------------ exports of another version

    /// <summary>"Export texture as PNG..." of another version: the full-size texture as a PNG, no .blp.json (it is not
    /// imported back), never into the client's folder.</summary>
    private void ExportOtherTexture(string path)
    {
        var dir = Path.Combine(_session.Settings.DefaultExportDir, _session.Profile?.Id ?? "other");
        Directory.CreateDirectory(dir);
        var png = Dialogs.PickSaveFile("Export texture as PNG", "PNG image|*.png", OtherTextures.PngName(path), dir);
        if (png == null) return;
        if (ExportOtherTextureTo(path, png)) Dialogs.ShowInExplorer(png);
    }

    /// <summary>Exports a texture of another version to a PNG; refuses the client's own folders (also screenshots and tests).</summary>
    public bool ExportOtherTextureTo(string path, string png)
    {
        try
        {
            WoWData.Core.M2.Modern.ModernModelExport.CheckOutsideClient(_session, png);
            OtherTextures.ExportPng(_session, path, Path.GetFullPath(png));
            _main.Toast($"Exported {Path.GetFileName(png)} (a texture of {_session.Profile?.VersionBuild}: no .blp.json, it is not imported back).");
            return true;
        }
        catch (Exception e) when (e is ClientFileException or IOException or UnauthorizedAccessException or WoWData.Core.Blp.BlpException)
        {
            Dialogs.Error("Export texture as PNG", $"{path} could not be exported: {e.Message}");
            return false;
        }
    }

    /// <summary>"Export for Blender" of another version's model (the look shown, or every display's look).</summary>
    public async Task<WoWData.Core.ModelIO.ModelExportResult?> ExportOtherModelAsync(WoWData.Core.ModelIO.ModelExportMode mode, string parentDir, bool animations, bool skeleton = false)
    {
        var model = ModelViewer.ModelPath;
        var v = ModelViewer.SelectedVariant;
        var look = ModelViewer.CurrentLook;
        var (_, _, _, geosets, _) = ModelViewer.ExportContext();
        var folder = NewPackageFolder(parentDir, model);
        return await _main.RunBusyAsync("Exporting the model for Blender...", () => Task.Run(() =>
        {
            Directory.CreateDirectory(parentDir);
            return WoWData.Core.M2.Modern.ModernModelExport.Export(_session, model, mode, parentDir, v?.DisplayId, v?.IsItem == true, look, v?.Label, geosets, animations, folder, skeleton);
        }));
    }

    private async Task ExportOtherModelInteractiveAsync(bool openInBlender)
    {
        if (!ModelViewer.HasModel) return;
        var model = ModelViewer.ModelPath;
        int skins;
        string users;
        try
        {
            (skins, users) = await Task.Run(() =>
            {
                var ix = _session.Index!;
                int c = ix.CreatureDisplaysUsingModel(model).Count, i = ix.ItemDisplaysUsingModel(model).Select(x => x.Id).Distinct().Count();
                return (Math.Max(1, c + i), $"{c} creature display{(c == 1 ? "" : "s")} and {i} item display{(i == 1 ? "" : "s")} of {_session.Profile?.VersionBuild}");
            });
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Error("Could not export the model", e.Message);
            return;
        }
        var blender = WoWData.Core.ModelIO.BlenderTools.FindBlender();
        var dir = Path.Combine(ModelsExportDir, _session.Profile?.Id ?? "other");
        WoWData.Core.ModelIO.ModelExportResult? res;
        try
        {
            if (openInBlender)
            {
                if (blender == null)
                {
                    Dialogs.Info("Blender was not found", "Install Blender 4.5 LTS (from blender.org or the Microsoft Store), then try again. You can still use \"Export for Blender...\".");
                    return;
                }
                res = await ExportOtherModelAsync(WoWData.Core.ModelIO.ModelExportMode.CurrentLook, dir, true);
                if (res != null) OpenFileInBlender(res.GltfFiles[0], "Blender is starting with the model (an export of another version: it is not imported back).");
                return;
            }
            var win = new Views.ModelExportWindow(model, CurrentLookName(), skins, users, $"A model of {_session.Profile?.VersionBuild}, open read only: the package is for Blender and other programs; it cannot be imported back into that version.", dir, blender != null)
            {
                Owner = System.Windows.Application.Current.MainWindow,
            };
            if (win.ShowDialog() != true) return;
            res = await ExportOtherModelAsync(win.Mode, win.Folder, win.IncludeAnimations, win.IncludeSkeleton);
            if (res == null) return;
            _main.Toast($"Exported {Path.GetFileName(res.Folder)}: {res.GltfFiles.Count} glTF file{(res.GltfFiles.Count == 1 ? "" : "s")}, {res.TextureCount} textures.");
            if (win.OpenInBlender && blender != null) OpenFileInBlender(res.GltfFiles[0], "Blender is starting with the model.");
            else Dialogs.ShowInExplorer(res.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WoWData.Core.M2.M2Exception or WoWData.Core.ModelIO.GltfException or InvalidOperationException)
        {
            Dialogs.Error("Could not export the model", e.Message);
        }
    }
}
