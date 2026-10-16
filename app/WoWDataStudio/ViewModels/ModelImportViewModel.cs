using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;

namespace WoWDataStudio.ViewModels;

/// <summary>One check of a change, ready to show: glyph, text and colour by level.</summary>
public sealed class RuleItem
{
    public RuleItem(ModelRule r)
    {
        Text = r.Text;
        Level = r.Level;
    }

    public string Text { get; }
    public RuleLevel Level { get; }
    public string Glyph => Level switch { RuleLevel.Blocker => "", RuleLevel.Warning => "", _ => "" };
    public string BrushKey => Level switch { RuleLevel.Blocker => "Bad", RuleLevel.Warning => "Warn", _ => "Accent" };
}

/// <summary>A row of the import review: one change, its checkbox, checks, options and compare button.</summary>
public sealed partial class ModelChangeRow : ObservableObject
{
    private readonly ModelImportViewModel _owner;
    public ModelChange Change { get; }

    public ModelChangeRow(ModelImportViewModel owner, ModelChange change)
    {
        _owner = owner;
        Change = change;
        _include = change.Include;
        Refresh();
    }

    public string Title => Change.Title;
    public string KindGlyph => Change.Kind switch { ModelChangeKind.Mesh => "", ModelChangeKind.Skeleton => "\uE8D4", ModelChangeKind.Effects => "\uE945", _ => "" };
    /// <summary>0.10.0: the skeleton and animation changes, one per line.</summary>
    public List<string> Lines => Change.Lines.Take(40).Select(l => "\u2022 " + l).Concat(Change.Lines.Count > 40 ? new[] { $"\u2022 and {Change.Lines.Count - 40} more" } : Array.Empty<string>()).ToList();
    public bool HasLines => Change.Lines.Count > 0 && !Change.Unchanged;
    public bool CanInclude => Change.CanInclude && !Change.Unchanged;
    public bool IsUnchanged => Change.Unchanged && !Change.HasBlocker;
    public bool CanCompare => !Change.Unchanged && Change.NewBytes != null && Change.OriginalBytes != null;
    public string Status => Change.Unchanged && !Change.HasBlocker ? "Unchanged" : Change.HasBlocker ? "Cannot be imported" : Include ? "Will be saved" : "Left out";
    public string StatusBrushKey => Change.Unchanged && !Change.HasBlocker ? "TextMuted" : Change.HasBlocker ? "Bad" : Include ? "Good" : "TextMuted";

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private List<RuleItem> _rules = new();
    [ObservableProperty] private bool _include;
    [ObservableProperty] private bool _busy;

    // texture options
    public bool ShowResize => Change.Kind == ModelChangeKind.Texture && Change.NeedsResize;
    public bool ShowFormat => Change.Kind == ModelChangeKind.Texture && Change.AlphaQuestion;
    public List<string> ResizeOptions { get; private set; } = new();
    public List<string> FormatOptions { get; private set; } = new();
    [ObservableProperty] private int _resizeChoice;
    [ObservableProperty] private int _formatChoice;
    private bool _syncing;

    public void Refresh()
    {
        _syncing = true;
        Detail = Change.Detail;
        Rules = Change.Rules.Select(r => new RuleItem(r)).ToList();
        Include = Change.Include;
        if (Change.Kind == ModelChangeKind.Texture && Change.EditedImage is { } img && Change.Texture != null)
        {
            var orig = Change.Plan?.Original;
            var size = orig != null ? (orig.Width, orig.Height) : Change.ResizeTo ?? (img.Width, img.Height);
            bool pow2 = ImageOps.IsPow2(img.Width) && ImageOps.IsPow2(img.Height) && img.Width <= 512 && img.Height <= 512;
            ResizeOptions = pow2 && (img.Width, img.Height) != size
                ? new List<string> { $"Resize to {size.Item1}x{size.Item2} (the original size)", $"Keep my size ({img.Width}x{img.Height})" }
                : new List<string> { $"Resize to {size.Item1}x{size.Item2} (the original size)" };
            string of = orig?.FormatName ?? "the original format";
            FormatOptions = new List<string> { $"Keep {of} (transparency is ignored)", "Save as DXT3 (keeps sharp transparency)", "Save as DXT5 (keeps smooth transparency)" };
            FormatChoice = Change.Format switch { ImportFormatChoice.Dxt3KeepAlpha => 1, ImportFormatChoice.Dxt5KeepAlpha => 2, _ => 0 };
        }
        _syncing = false;
        OnPropertyChanged(nameof(ResizeOptions));
        OnPropertyChanged(nameof(FormatOptions));
        OnPropertyChanged(nameof(ShowResize));
        OnPropertyChanged(nameof(ShowFormat));
        OnPropertyChanged(nameof(CanInclude));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(IsUnchanged));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrushKey));
    }

    partial void OnIncludeChanged(bool value)
    {
        if (_syncing) return;
        Change.Include = value && Change.CanInclude;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrushKey));
        if (Change.IsModelPart) _owner.Recompose();
        _owner.UpdateSummary();
    }

    partial void OnResizeChoiceChanged(int value)
    {
        if (_syncing || Change.EditedImage is not { } img) return;
        var orig = Change.Plan?.Original;
        Change.ResizeTo = value == 1 ? (img.Width, img.Height) : orig != null ? (orig.Width, orig.Height) : Change.ResizeTo;
        _ = _owner.ReplanAsync(this);
    }

    partial void OnFormatChoiceChanged(int value)
    {
        if (_syncing) return;
        Change.Format = value switch { 1 => ImportFormatChoice.Dxt3KeepAlpha, 2 => ImportFormatChoice.Dxt5KeepAlpha, _ => ImportFormatChoice.SameAsOriginal };
        _ = _owner.ReplanAsync(this);
    }

    [RelayCommand]
    private void Compare() => _owner.RequestCompare(this);
}

/// <summary>
/// The review screen of "Import edited model...": what changed against the export (mesh, each texture), the
/// plain-language checks, include checkboxes, a Compare button per row, and "Save into my workspace" (one change set).
/// </summary>
public sealed partial class ModelImportViewModel : ObservableObject
{
    public ModelImportAnalysis Analysis { get; }
    public ObservableCollection<ModelChangeRow> Rows { get; } = new();
    public List<RuleItem> General { get; }
    public string Heading => $"Import edited model: {Path.GetFileName(Analysis.ModelPath)}";
    public string Source => $"From {Analysis.Edited.Description} ({Analysis.Edited.GltfPath}). " +
                            (Analysis.BaseModel != null ? $"Compared with {Analysis.BaseDescription}, the version it was exported from." : "");
    public string UsersWarning => Analysis.UsersWarning;
    public bool HasUsersWarning => !string.IsNullOrEmpty(UsersWarning) && Rows.Any(r => r.Change.Kind == ModelChangeKind.Mesh && !r.Change.Unchanged);
    public bool NothingChanged => Analysis.NothingChanged;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canSave;

    /// <summary>Set by the window: opens a comparison (texture or 3D) for a row.</summary>
    public Action<ModelChangeRow>? CompareRequested { get; set; }

    public ModelImportViewModel(ModelImportAnalysis analysis)
    {
        Analysis = analysis;
        General = analysis.General.Select(r => new RuleItem(r)).ToList();
        // changes first, then what could not be imported, then the unchanged ones
        foreach (var c in analysis.Changes.OrderBy(c => c.Unchanged && !c.HasBlocker ? 2 : c.HasBlocker ? 1 : 0).ThenBy(c => c.Kind))
            Rows.Add(new ModelChangeRow(this, c));
        UpdateSummary();
    }

    public void UpdateSummary()
    {
        int sel = Analysis.Changes.Count(c => c.Include && c.CanInclude && !c.Unchanged);
        int changed = Analysis.Changes.Count(c => !c.Unchanged);
        int same = Analysis.Changes.Count(c => c.Unchanged && !c.HasBlocker);
        Summary = Analysis.NothingChanged
            ? "No changes found: nothing will be written."
            : $"{sel} of {changed} change{(changed == 1 ? "" : "s")} selected; {same} unchanged. Saving puts the selected files into your workspace as one import (\"Revert this import\" undoes it).";
        CanSave = sel > 0;
        OnPropertyChanged(nameof(HasUsersWarning));
    }

    public async Task ReplanAsync(ModelChangeRow row)
    {
        row.Busy = true;
        try
        {
            await Task.Run(() => ModelImportAnalyzer.PlanTexture(row.Change, Analysis.Context));
            row.Refresh();
            UpdateSummary();
        }
        finally
        {
            row.Busy = false;
        }
    }

    public void RequestCompare(ModelChangeRow row) => CompareRequested?.Invoke(row);

    /// <summary>0.10.0: the model rows (mesh, skeleton and animations, effects) are one file: build it again after a
    /// checkbox changed, then refresh their checks.</summary>
    public void Recompose()
    {
        ModelImportAnalyzer.Compose(Analysis);
        foreach (var r in Rows.Where(r => r.Change.IsModelPart)) r.Refresh();
    }

    /// <summary>Original vs. edited of a texture row (the 3a compare).</summary>
    public CompareViewModel? TextureCompare(ModelChangeRow row)
    {
        var c = row.Change;
        if (c.NewBytes == null || c.OriginalBytes == null) return null;
        return new CompareViewModel(Path.GetFileName(c.GamePath ?? c.Title), new TextureCompareSide(CompareViewModel.OriginalLabel, c.OriginalBytes),
            new TextureCompareSide(CompareViewModel.EditLabel, c.NewBytes));
    }

    /// <summary>Original vs. edited model in two synchronized 3D views. <paramref name="look"/> is the look to show
    /// (textures of the replaceable slots); the right side also shows the edited textures of this import.</summary>
    public static ModelCompareViewModel MeshCompare(string modelPath, byte[] original, byte[] edited, ModelLook? look, Func<string, RgbaImage?> loadTexture,
        IEnumerable<ModelChange> textureChanges, string title, Func<int, string>? animName = null)
    {
        var editedTextures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in textureChanges)
            if (c.Kind == ModelChangeKind.Texture && c.Include && c.CanInclude && c.NewBytes != null && c.GamePath != null)
                editedTextures[c.GamePath] = BlpFile.Parse(c.NewBytes).Decode(0);
        RgbaImage? EditedFirst(string p) => editedTextures.TryGetValue(p, out var img) ? img : loadTexture(p);
        var left = ModelAsset.Build(modelPath, original, loadTexture);
        var right = ModelAsset.Build(modelPath, edited, EditedFirst);
        ModelLook? rightLook = look;
        if (look != null && editedTextures.Count > 0)
        {
            rightLook = new ModelLook { Geosets = look.Geosets };
            foreach (var (slot, rt) in look.Slots)
                rightLook.Slots[slot] = rt.Path != null && editedTextures.TryGetValue(rt.Path, out var img) ? new ResolvedTexture(img, rt.Source, rt.Path, rt.Parts) : rt;
        }
        var lm = M2Model.Load(original);
        var rm = M2Model.Load(edited);
        var vm = new ModelCompareViewModel(title,
            new ModelCompareSide(CompareViewModel.OriginalLabel, left, look, $"{lm.Vertices.Length:N0} vertices, {(lm.Views.Length > 0 ? lm.Views[0].Triangles.Length / 3 : 0):N0} triangles, {lm.Bones.Length} bones, {lm.Sequences.Length} animations"),
            new ModelCompareSide(CompareViewModel.EditLabel, right, rightLook, $"{rm.Vertices.Length:N0} vertices, {(rm.Views.Length > 0 ? rm.Views[0].Triangles.Length / 3 : 0):N0} triangles, {rm.Bones.Length} bones, {rm.Sequences.Length} animations"))
        {
            Subtitle = "Left: the model as it is now. Right: your edited model as the game will load it. Both views share one camera; pick an animation below to play it on both (Bones shows the skeleton in the main viewer).",
        };
        vm.InitSequences(animName ?? (id => $"Anim{id}"));
        return vm;
    }

    /// <summary>Save the selected changes into the workspace (one change set).</summary>
    public ChangeSet Commit(Workspace ws, WoWData.Core.Index.AssetIndex? index) => ModelImportCommitter.Commit(Analysis, ws, index);
}
