using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWDataStudio.Helpers;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>The "Edit in Noggit" window: what Noggit is, the prepared project, and the steps.</summary>
public sealed partial class NoggitLaunchViewModel : ObservableObject
{
    public NoggitLaunchInfo Info { get; }
    public string? NoggitExe { get; }
    public bool NoggitFound => NoggitExe != null;
    public bool NoggitMissing => NoggitExe == null;
    public bool AlreadyRunning { get; }
    public int Synced { get; }
    public bool SettingsRequested { get; private set; }
    [ObservableProperty] private string _launchStatus = "";

    public NoggitLaunchViewModel(NoggitLaunchInfo info, string? exe, int synced, bool running)
    {
        Info = info;
        NoggitExe = exe;
        Synced = synced;
        AlreadyRunning = running;
        if (running) LaunchStatus = "Noggit is already running. To see the new bookmark, open the project again (File > back to the project selection, or restart Noggit).";
    }

    public string ProjectFile => Info.ProjectFile;
    public string SaveDir => Info.SaveDir;
    public string ClientNote => Info.ClientNote;
    public string Bookmark => Info.BookmarkName;
    public string ExeText => NoggitExe ?? "not found: set it in Settings (Maps and terrain)";
    public string SourceNote
    {
        get
        {
            var src = NoggitService.ForkSourceDir(NoggitExe);
            return src != null ? $"Its source code and licence (GPL-3, COPYING) are in {src}." : "Its source code: github.com/pkuzic/noggit-cinder (our fixed fork: third_party\\noggit-cinder-fork, see FORK_CHANGES.md).";
        }
    }
    public string SyncNote => Synced > 0 ? $"{Synced} map file{(Synced == 1 ? "" : "s")} you imported earlier {(Synced == 1 ? "was" : "were")} copied into the project, so Noggit continues from your version." : "";
    public bool HasSyncNote => Synced > 0;

    [RelayCommand]
    private void StartNoggit()
    {
        if (NoggitExe == null) return;
        try
        {
            var p = NoggitService.Launch(NoggitExe);
            LaunchStatus = p == null ? "Noggit did not start." : $"Noggit started (process {p.Id}). Follow the steps below in its window.";
            try { Clipboard.SetText(ProjectFile); } catch (System.Runtime.InteropServices.ExternalException) { }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            LaunchStatus = "Noggit could not be started: " + e.Message;
        }
    }

    [RelayCommand]
    private void CopyProjectPath()
    {
        try
        {
            Clipboard.SetText(ProjectFile);
            LaunchStatus = "The project file's path is on the clipboard: paste it into Noggit's \"Open an existing project\" file dialog.";
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    [RelayCommand]
    private void OpenProjectFolder() => Dialogs.OpenPath(Info.ProjectDir);

    [RelayCommand]
    private void OpenSettings() => SettingsRequested = true;
}

/// <summary>A row of the map import review: one file, its checks and summary, include and Compare.</summary>
public sealed partial class MapImportRow : ObservableObject
{
    private readonly MapImportViewModel _owner;
    public MapFileChange Change { get; }

    public MapImportRow(MapImportViewModel owner, MapFileChange c)
    {
        _owner = owner;
        Change = c;
        _include = c.Include;
    }

    public string Title => Change.Tile != null ? $"{Change.FileName}  (tile {Change.Tile.Value.X},{Change.Tile.Value.Y})" : Change.FileName;
    public string Kind => Change.Kind == null ? "other file" : MapImporter.FormatOf(Change.Kind);
    public string Summary => Change.Summary;
    public List<string> Problems => Change.Problems.Concat(Change.Notes).ToList();
    public bool HasProblems => Problems.Count > 0;
    public bool CanInclude => Change.CanInclude;
    public bool CanCompare => Change.Kind != null && !Change.Unchanged;
    public string Status => Change.Kind == null ? "Not imported" : Change.HasErrors ? "Cannot be imported" : Change.Unchanged ? "Unchanged" : Include ? "Will be saved" : "Left out";
    public string StatusBrushKey => Change.HasErrors ? "Bad" : Change.Kind == null || Change.Unchanged ? "TextMuted" : Include ? "Good" : "TextMuted";
    public string Details => Change.Diff == null ? "" : string.Join("\n", Change.Diff.Lines.Take(60).Select(l => $"{l.Section}: {l.Message}")) + (Change.Diff.Lines.Count > 60 ? $"\n... {Change.Diff.Lines.Count - 60} more" : "");
    public bool HasDetails => Change.Diff is { Lines.Count: > 0 };
    public string Validation => Change.Kind == null ? "" : Change.BlockReason != null && Change.Validation.Ok ? "The 1.12 check passed, but this file cannot be imported (see below)." :
        Change.HasErrors ? $"The 1.12 check found {Change.Validation.Count(MapSeverity.Error)} error(s): the game would read this file wrongly." :
        Change.Validation.Count(MapSeverity.Warning) > 0 ? $"The 1.12 check passed with {Change.Validation.Count(MapSeverity.Warning)} warning(s)." : "The 1.12 check passed: the game's loaders read this file correctly.";

    [ObservableProperty] private bool _include;

    partial void OnIncludeChanged(bool value)
    {
        Change.Include = value && Change.CanInclude;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrushKey));
        _owner.UpdateSummary();
    }

    [RelayCommand]
    private void Compare() => _owner.CompareRequested?.Invoke(this);
}

/// <summary>The review screen of "Import map changes".</summary>
public sealed partial class MapImportViewModel : ObservableObject
{
    public MapImportAnalysis Analysis { get; }
    public ObservableCollection<MapImportRow> Rows { get; } = new();
    public List<string> General => Analysis.General;
    public string Heading => Analysis.Heading ?? $"Import map changes: {Analysis.Map.Name}";
    public string Source => Analysis.SourceText ?? $"From Noggit's save folder {Analysis.SourceDir}. Each file is compared with what the app has now (your earlier import of it, or the game's file).";
    public bool FromBlender => Analysis.ChangeSetPrefix != "Map import";
    public string Intro => FromBlender
        ? "Each row is one tile. Only heights came back from Blender; the normals (lighting) around them were recomputed and everything else is kept. Tick what to keep, click Compare to see the tile before and after in 3D. Nothing is written until you click \"Save into my workspace\"; the game itself only changes when you test or release."
        : "Each row is one file Noggit saved. Tick what to keep, click Compare to see the tile before and after in 3D. Files the 1.12 check rejects cannot be imported. Nothing is written until you click \"Save into my workspace\"; the game itself only changes when you test or release.";
    public Action<MapImportRow>? CompareRequested { get; set; }
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canSave;

    public MapImportViewModel(MapImportAnalysis a)
    {
        Analysis = a;
        foreach (var c in a.Changes.OrderBy(c => c.Kind == null ? 3 : c.Unchanged ? 2 : c.HasErrors ? 1 : 0).ThenBy(c => c.GamePath, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new MapImportRow(this, c));
        UpdateSummary();
    }

    public void UpdateSummary()
    {
        int sel = Analysis.Changes.Count(c => c.Include && c.CanInclude);
        int changed = Analysis.ChangedCount;
        int same = Analysis.Changes.Count(c => c.Unchanged);
        int bad = Analysis.Changes.Count(c => c.HasErrors && !c.Unchanged);
        Summary = $"{sel} of {changed} changed file{(changed == 1 ? "" : "s")} selected; {same} unchanged" + (bad > 0 ? $"; {bad} cannot be imported ({(FromBlender ? "see the reason under the tile" : "fix them in Noggit and save again")})" : "") +
                  ". Saving puts the selected files into your workspace as one import (\"Revert this import\" undoes it).";
        CanSave = sel > 0;
    }
}

/// <summary>
/// Before / after of a terrain tile: two synchronized 3D terrain views (one camera), the height difference as a heat map
/// over the relief (red = raised, blue = lowered) and the semantic diff in words and in detail.
/// </summary>
public sealed partial class MapCompareViewModel : ObservableObject
{
    public string Title { get; }
    public string LeftLabel { get; }
    public string RightLabel { get; }
    public TerrainScene Left { get; } = new();
    public TerrainScene Right { get; } = new();
    public OrbitCamera Camera { get; } = new();
    public BitmapSource DifferenceImage { get; }
    public RgbaImage DifferenceRgba { get; }
    public MapDiffResult? Diff { get; }
    public string Summary { get; }
    public string Details { get; }
    public string Legend => "Height difference seen from above (north up): grey = unchanged, red = raised, blue = lowered; the stronger the colour, the bigger the change.";
    [ObservableProperty] private bool _showDoodads = true;
    [ObservableProperty] private bool _showWater = true;

    public MapCompareViewModel(string title, string leftLabel, string rightLabel, TerrainAsset left, TerrainAsset right, RgbaImage difference, MapDiffResult? diff, string? note)
    {
        Title = title;
        LeftLabel = leftLabel;
        RightLabel = rightLabel;
        Left.Asset = left;
        Right.Asset = right;
        TerrainScene.FrameTile(Camera, left);
        DifferenceRgba = difference;
        DifferenceImage = ImageSources.ToBitmap(difference, AlphaView.IgnoreAlpha);
        Diff = diff;
        Summary = note ?? (diff == null ? "" : diff.PlainSummary() + ".");
        Details = diff == null ? "" : string.Join("\n", diff.Lines.Take(200).Select(l => $"{l.Section}: {l.Message}")) + (diff.Lines.Count > 200 ? $"\n... {diff.Lines.Count - 200} more" : "");
    }

    partial void OnShowDoodadsChanged(bool value)
    {
        Left.ShowDoodads = Right.ShowDoodads = value;
        Left.ShowWmos = Right.ShowWmos = value;
    }

    partial void OnShowWaterChanged(bool value) => Left.ShowWater = Right.ShowWater = value;

    [RelayCommand]
    private void ResetView() => Camera.ResetView();
}
