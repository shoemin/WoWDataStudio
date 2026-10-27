using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

public enum ViewerSubject { None, Creature, Npc, Character, Item, Plain }

/// <summary>One entry of the skin picker: a creature display or an item display using the model.</summary>
public sealed class VariantItem
{
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    public long DisplayId { get; init; }
    public bool IsItem { get; init; }
    public string[] Skins { get; init; } = Array.Empty<string>();
    public override string ToString() => Label;
}

public sealed class SequenceItem
{
    public int Index { get; init; }
    /// <summary>0.10.0: the animation's name ("Stand [2]").</summary>
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public double DurationMs { get; init; }
    public override string ToString() => Label;
}

public sealed class LodItem
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

public sealed class BackgroundItem
{
    public string Label { get; init; } = "";
    public Vector3 Color { get; init; }
    public override string ToString() => Label;
}

public sealed partial class GeosetItem : ObservableObject
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public string Group { get; init; } = "";
    [ObservableProperty] private bool _isVisible;
    public Action<GeosetItem>? Toggled;
    partial void OnIsVisibleChanged(bool value) => Toggled?.Invoke(this);
}

/// <summary>A texture of the model as shown now: which slot, which file (click to open it in the texture viewer).</summary>
public sealed class ModelTextureItem
{
    public string Slot { get; init; } = "";
    public string FileName { get; init; } = "";
    public string? Path { get; init; }
    public string Tip { get; init; } = "";
    public bool CanOpen => Path != null;
}

public sealed class StatItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
}

/// <summary>Built models (geometry + decoded textures), the most recent first. Shared by the docked viewer and the
/// pop-out windows, so a model shown in both is read and decoded once (each view uploads its own GL copy).</summary>
public sealed class ModelAssetCache
{
    private readonly Dictionary<string, ModelAsset> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    public int Capacity { get; init; } = 8;

    public bool TryGet(string model, out ModelAsset asset)
    {
        lock (_map)
        {
            if (_map.TryGetValue(model, out asset!))
            {
                Touch(model);
                return true;
            }
            return false;
        }
    }

    public void Add(string model, ModelAsset asset)
    {
        lock (_map)
        {
            if (_map.ContainsKey(model)) Touch(model);
            else _lru.AddFirst(model);
            _map[model] = asset;
            while (_lru.Count > Capacity)
            {
                _map.Remove(_lru.Last!.Value);
                _lru.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_map)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    /// <summary>Move a key to the front (paths compare without case, like the map).</summary>
    private void Touch(string model)
    {
        for (var n = _lru.First; n != null; n = n.Next)
        {
            if (!string.Equals(n.Value, model, StringComparison.OrdinalIgnoreCase)) continue;
            _lru.Remove(n);
            _lru.AddFirst(n);
            return;
        }
    }
}

/// <summary>
/// The 3D viewer of the details panel: loads the selected model on a background thread, resolves its textures through
/// the selected variant (creature display, item display, or the character-creation choices), and drives the
/// <see cref="ModelScene"/> shown by the viewport (animation, level of detail, geosets, display options).
/// </summary>
public sealed partial class ModelViewerViewModel : ObservableObject
{
    private readonly AppSession _session;
    private int _token;
    private readonly ModelAssetCache _assetCache;
    private (int Race, int Sex)? _race;
    private bool _syncing, _applyingVariant;
    private IndexEntry? _entry;

    public ModelScene Scene { get; } = new();
    public OrbitCamera Camera { get; } = new();

    public ObservableCollection<VariantItem> Variants { get; } = new();
    public ObservableCollection<SequenceItem> Sequences { get; } = new();
    public ObservableCollection<LodItem> Lods { get; } = new();
    public ObservableCollection<GeosetItem> Geosets { get; } = new();
    public ObservableCollection<ModelTextureItem> ModelTextures { get; } = new();
    public ObservableCollection<StatItem> Stats { get; } = new();
    public ObservableCollection<int> SkinColors { get; } = new();
    public ObservableCollection<int> Faces { get; } = new();
    public ObservableCollection<int> HairStyles { get; } = new();
    public ObservableCollection<int> HairColors { get; } = new();
    public ObservableCollection<int> FacialHairs { get; } = new();
    public List<BackgroundItem> Backgrounds { get; } = new()
    {
        new() { Label = "Slate (default)", Color = new Vector3(0.17f, 0.19f, 0.22f) },
        new() { Label = "Mid grey", Color = new Vector3(0.45f, 0.46f, 0.48f) },
        new() { Label = "Light grey", Color = new Vector3(0.82f, 0.84f, 0.86f) },
        new() { Label = "Black", Color = Vector3.Zero },
        new() { Label = "White", Color = Vector3.One },
        new() { Label = "Sky blue", Color = new Vector3(0.45f, 0.62f, 0.82f) },
        new() { Label = "Green (for keying)", Color = new Vector3(0f, 0.7f, 0.2f) },
    };

    /// <summary>Raised when the user clicks a texture of the model: the browser opens it in the texture viewer.</summary>
    public event Action<string, string>? OpenTextureRequested;

    /// <summary>Set by the view: renders the viewport offscreen (used by "Save image...").</summary>
    public Func<Task<RgbaImage>>? Capture { get; set; }

    [ObservableProperty] private bool _hasModel;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private ViewerSubject _subject;
    [ObservableProperty] private VariantItem? _selectedVariant;
    [ObservableProperty] private SequenceItem? _selectedSequence;
    [ObservableProperty] private LodItem? _selectedLod;
    [ObservableProperty] private BackgroundItem? _selectedBackground;
    [ObservableProperty] private string _backgroundHex = "#2B3038";
    [ObservableProperty] private bool _playing = true;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private double _timeMs;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _showBones;
    /// <summary>0.10.0: attachment points overlay and loop / play once.</summary>
    [ObservableProperty] private bool _showAttachments;
    [ObservableProperty] private bool _loop = true;
    /// <summary>0.10.0: the current animation in words (loops, chance, blend, move speed, what the game plays it for)
    /// and its events.</summary>
    [ObservableProperty] private string _animationInfo = "";
    [ObservableProperty] private string _eventsInfo = "";
    /// <summary>0.10.0: bones, key bones and attachment points of the model.</summary>
    [ObservableProperty] private string _skeletonInfo = "";
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showGround = true;
    [ObservableProperty] private int _skinColor;
    [ObservableProperty] private int _face;
    [ObservableProperty] private int _hairStyle;
    [ObservableProperty] private int _hairColor;
    [ObservableProperty] private int _facialHair;
    [ObservableProperty] private string _variantNote = "";
    [ObservableProperty] private string _lookNote = "";
    [ObservableProperty] private double _lastLoadMs;
    [ObservableProperty] private double _lastLookMs;
    /// <summary>The model file itself is edited (an imported mesh is in your workspace).</summary>
    [ObservableProperty] private bool _isModelModified;

    // ---- 0.5.1: pop-out windows and navigation
    /// <summary>Orbit or fly (the camera bar and the viewport bind to it).</summary>
    [ObservableProperty] private CameraNavigation _navigation;
    /// <summary>The help overlay of the 3D view (H).</summary>
    [ObservableProperty] private bool _showHelp;
    /// <summary>A pop-out window follows the selection and plays the animation: this (docked) view stops redrawing every
    /// frame to spare the graphics card, and says so.</summary>
    [ObservableProperty] private bool _pausedForPopout;
    /// <summary>"Open in new window" (set by the browser for the docked viewer; null in a pop-out window).</summary>
    [ObservableProperty] private System.Windows.Input.ICommand? _popOutCommand;
    /// <summary>"Bring it to front": shows the following pop-out window (set by the browser).</summary>
    [ObservableProperty] private System.Windows.Input.ICommand? _showPopoutCommand;
    /// <summary>The name of the shown entry, e.g. "Mottled Boar" (the pop-out window's title).</summary>
    [ObservableProperty] private string _entryName = "";

    /// <summary>The entry and details last loaded (a pop-out window opens on them).</summary>
    public (IndexEntry Entry, EntryDetails Details)? Loaded => _entry != null && _details != null ? (_entry, _details) : null;
    private EntryDetails? _details;

    /// <summary>M2: the looks of the open game's models (your 1.12 game's resolver, or another version's).</summary>
    private IModelLooks Looks => _session.Looks!;

    public void RefreshModified() => IsModelModified = ModelPath.Length > 0 && _session.Workspace?.IsModified(ModelPath) == true;

    public bool IsCharacter => Subject == ViewerSubject.Character;
    public bool HasVariants => Variants.Count > 1;
    public string VariantHeader => Subject == ViewerSubject.Item ? $"Item displays using this model ({Variants.Count})" : $"Displays using this model ({Variants.Count})";
    public string TimeText => $"{TimeMs / 1000.0:0.00} / {DurationMs / 1000.0:0.00} s";
    public string SpeedText => $"{Speed:0.##}x";
    public string GeosetHeader => $"Geosets ({Geosets.Count(g => g.IsVisible)} of {Geosets.Count} shown)";
    public string PlayGlyph => Playing ? "" : "";
    public string PlayTip => Playing ? "Pause (Space)" : "Play (Space)";

    public ModelViewerViewModel(AppSession session, ModelAssetCache? cache = null)
    {
        _session = session;
        _assetCache = cache ?? new ModelAssetCache();
        _selectedBackground = Backgrounds[0];
        Scene.ShowGrid = true;
        Scene.ShowGround = true;
    }

    // ---------------------------------------------------------------- loading

    public void Clear()
    {
        _token++;
        _entry = null;
        _details = null;
        EntryName = "";
        HasModel = false;
        IsModelModified = false;
        IsLoading = false;
        Message = "";
        Scene.Asset = null;
        Scene.Look = null;
        ClearLists();
    }

    private void ClearLists()
    {
        Variants.Clear();
        Sequences.Clear();
        Lods.Clear();
        Geosets.Clear();
        ModelTextures.Clear();
        Stats.Clear();
        FormatNotes = "";
        SkinColors.Clear();
        Faces.Clear();
        HairStyles.Clear();
        HairColors.Clear();
        FacialHairs.Clear();
        OnPropertyChanged(nameof(HasVariants));
        OnPropertyChanged(nameof(VariantHeader));
        OnPropertyChanged(nameof(GeosetHeader));
    }

    private sealed class Plan
    {
        public string Model = "";
        public ViewerSubject Subject;
        public List<VariantItem> Variants = new();
        public VariantItem? Selected;
        public (int Race, int Sex)? Race;
        public CharacterOptions? Options;
        public ModelLook Look = new();
        public ModelAsset Asset = null!;
        public string Note = "";
    }

    /// <summary>Show the model of an entry (creature, character, item, game object, world object, spell effect).</summary>
    public async Task LoadAsync(IndexEntry entry, EntryDetails details)
    {
        int token = ++_token;
        _entry = entry;
        _details = details;
        EntryName = details.Title;
        IsLoading = true;
        Message = "";
        var sw = Stopwatch.StartNew();
        try
        {
            var plan = await Task.Run(() => _session.IsReadOnly ? MakePlanModern(entry, details) : MakePlan(entry, details));
            if (token != _token) return;
            if (plan == null)
            {
                HasModel = false;
                Scene.Asset = null;
                ClearLists();
                return;
            }
            Apply(plan);
            LastLoadMs = sw.Elapsed.TotalMilliseconds;
            AddStat("Load time", $"{LastLoadMs:0} ms");
        }
        catch (Exception e)
        {
            // any problem with one model (a broken file, a missing table) is shown in the view, never thrown at the app
            if (token != _token) return;
            HasModel = false;
            Scene.Asset = null;
            ClearLists();
            Message = "Could not show this model: " + e.Message;
        }
        finally
        {
            if (token == _token) IsLoading = false;
        }
    }

    private ModelAsset LoadAsset(string model)
    {
        if (_assetCache.TryGet(model, out var hit)) return hit;
        if (_session.IsReadOnly)
        {
            // M2: another version: the modern reader (.skin files by FileDataID), its looks' texture loader
            var ma = ModelAsset.Build(model, _session.ModelSource!.Load(model), Looks.LoadBlp);
            _assetCache.Add(model, ma);
            return ma;
        }
        var res = _session.Models!;
        // your edited model (an imported mesh) wins over the game's
        var data = _session.Workspace?.ReadEdited(model) ?? _session.Data!.Read(model) ?? throw new IOException($"{model} is not in the game archives");
        var a = ModelAsset.Build(model, data, res.LoadBlp);
        _assetCache.Add(model, a);
        return a;
    }

    /// <summary>Forget cached models and decoded textures (after an edit was imported or reverted).</summary>
    public void InvalidateTextures()
    {
        _session.Looks?.ClearTextureCache();
        _assetCache.Clear();
    }

    /// <summary>Reload the textures (after an edit was imported or reverted) keeping the view, animation and choices.</summary>
    public async Task ReloadLookAsync()
    {
        var path = ModelPath;
        if (Scene.Asset == null || path.Length == 0) return;
        try
        {
            var asset = await Task.Run(() => LoadAsset(path));
            if (!string.Equals(path, ModelPath, StringComparison.OrdinalIgnoreCase)) return;
            Scene.ReplaceAsset(asset);
            RefreshModified();
            RefreshStats();
            RefreshSequences();
            RefreshSkeletonInfo();
            RefreshAnimationInfo();
            var v = SelectedVariant;
            if (Subject == ViewerSubject.Character && _race != null)
            {
                var (race, sex) = _race.Value;
                var look = new CharacterLook(SkinColor, Face, HairStyle, HairColor, FacialHair);
                await ApplyLookAsync(() => Looks.Character(race, sex, look));
            }
            else if (v != null)
                await ApplyLookAsync(() => v.IsItem ? Looks.ItemDisplay((int)v.DisplayId, path) : Looks.CreatureDisplay((int)v.DisplayId));
            else RefreshModelTextures();
        }
        catch (Exception e) when (e is IOException or M2Exception)
        {
            Message = "Could not reload the model: " + e.Message;
        }
    }

    private static string SkinShortName(string model, string skin)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(model);
        var s = skin;
        if (s.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) s = s[stem.Length..];
        if (s.StartsWith("Skin", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.TrimStart('_');
        return s.Length == 0 ? skin : s;
    }

    private Plan? MakePlan(IndexEntry e, EntryDetails d)
    {
        var index = _session.Index!;
        var res = _session.Models!;
        var cat = _session.Catalog!;
        var parts = e.Key.Split(':');
        var plan = new Plan();
        _ = res.AnimationNames;      // read AnimationData here, not on the UI thread
        long displayId = 0;
        switch (parts[0])
        {
            case "creature":
                displayId = index.CreatureDisplayIds(long.Parse(parts[1])).FirstOrDefault();
                break;
            case "cdisplay":
                displayId = long.Parse(parts[1]);
                break;
            case "race":
            {
                int race = int.Parse(parts[1]), sex = int.Parse(parts[2]);
                plan.Race = (race, sex);
                plan.Subject = ViewerSubject.Character;
                break;
            }
            case "item":
            case "idisplay":
            {
                long id = parts[0] == "item" ? index.ItemDisplayOf(long.Parse(parts[1])) ?? 0 : long.Parse(parts[1]);
                plan.Subject = ViewerSubject.Item;
                plan.Selected = new VariantItem { DisplayId = id, IsItem = true };
                break;
            }
            case "go":
            case "godisplay":
                plan.Subject = ViewerSubject.Plain;
                break;
            default:
                plan.Subject = ViewerSubject.Plain;
                break;
        }
        var model = d.Models.FirstOrDefault(m => m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase));
        if (displayId > 0 && cat.CreatureDisplay((int)displayId)?.Model is { } cm) model = cm;
        if (model == null)
        {
            if (d.Models.Any(m => m.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("this is a WMO building; the 3D view shows M2 models only (WMO support is planned with the maps module).");
            return null;
        }
        plan.Model = model;
        plan.Asset = LoadAsset(model);
        plan.Race ??= res.RaceOfModel(model);

        if (displayId > 0)
        {
            var chain = cat.CreatureDisplay((int)displayId);
            plan.Subject = chain?.Extra != null ? ViewerSubject.Npc : plan.Race != null ? ViewerSubject.Character : ViewerSubject.Creature;
        }
        if (plan.Subject == ViewerSubject.Plain && plan.Race == null && index.CreatureDisplaysUsingModel(model).Count > 0) plan.Subject = ViewerSubject.Creature;
        if (plan.Subject == ViewerSubject.Plain && plan.Race != null) plan.Subject = ViewerSubject.Character;
        if (plan.Subject == ViewerSubject.Plain && index.ItemDisplaysUsingModel(model).Count > 0) plan.Subject = ViewerSubject.Item;

        switch (plan.Subject)
        {
            case ViewerSubject.Creature:
            case ViewerSubject.Npc:
            {
                List<(long Id, string[] Skins, long ExtraId)> displays;
                if (plan.Subject == ViewerSubject.Npc || plan.Race != null)
                {
                    // humanoid NPCs on a character model: only this creature's own displays (a race model has thousands)
                    var ids = parts[0] == "creature" ? index.CreatureDisplayIds(long.Parse(parts[1])) : new List<long> { displayId };
                    displays = ids.Select(i => (i, Array.Empty<string>(), 1L)).ToList();
                }
                else displays = index.CreatureDisplaysUsingModel(model);
                var names = index.HasServer ? index.CreatureNamesForDisplays(displays.Select(x => x.Id)) : new Dictionary<long, string>();
                if (parts[0] == "creature")      // the selected creature names its own displays (several creatures share one)
                    foreach (var own in index.CreatureDisplayIds(long.Parse(parts[1]))) names[own] = e.Name;
                foreach (var (id, skins, extra) in displays)
                {
                    var skinText = string.Join(" / ", skins.Where(s => s.Length > 0).Select(s => SkinShortName(model, s)));
                    string who = names.TryGetValue(id, out var n) ? n : $"Display {id}";
                    plan.Variants.Add(new VariantItem
                    {
                        DisplayId = id, Skins = skins,
                        Label = skinText.Length > 0 ? $"{who} ({skinText})" : extra > 0 ? $"{who} (NPC look)" : who,
                        Detail = $"display {id}" + (skins.Any(s => s.Length > 0) ? " · " + string.Join(", ", skins.Where(s => s.Length > 0)) : ""),
                    });
                }
                plan.Variants = plan.Variants.OrderBy(v => v.Label.StartsWith("Display ", StringComparison.Ordinal)).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.DisplayId).ToList();
                plan.Selected = plan.Variants.FirstOrDefault(v => v.DisplayId == displayId) ?? plan.Variants.FirstOrDefault();
                plan.Look = plan.Selected != null ? res.CreatureDisplay((int)plan.Selected.DisplayId) : res.DefaultFor(model);
                int skinsCount = plan.Variants.SelectMany(v => v.Skins).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (plan.Subject == ViewerSubject.Creature && plan.Variants.Count > 1)
                    plan.Note = $"{plan.Variants.Count} displays use {System.IO.Path.GetFileName(model)} with {skinsCount} skin{(skinsCount == 1 ? "" : "s")}.";
                break;
            }
            case ViewerSubject.Item:
            {
                var displays = index.ItemDisplaysUsingModel(model);
                var names = index.HasServer ? index.ItemNamesForDisplays(displays.Select(x => x.Id)) : new Dictionary<long, string>();
                if (parts[0] == "item" && plan.Selected != null) names[plan.Selected.DisplayId] = e.Name;
                foreach (var (id, tex) in displays.GroupBy(x => x.Id).Select(g => g.First()))
                {
                    string who = names.TryGetValue(id, out var n) ? n : $"Item display {id}";
                    plan.Variants.Add(new VariantItem
                    {
                        DisplayId = id, IsItem = true, Label = $"{who} ({System.IO.Path.GetFileNameWithoutExtension(tex)})", Detail = $"item display {id} · {tex}",
                    });
                }
                plan.Variants = plan.Variants.OrderBy(v => v.Label.StartsWith("Item display ", StringComparison.Ordinal)).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToList();
                long want = plan.Selected?.DisplayId ?? 0;
                plan.Selected = plan.Variants.FirstOrDefault(v => v.DisplayId == want) ?? plan.Variants.FirstOrDefault();
                plan.Look = plan.Selected != null ? res.ItemDisplay((int)plan.Selected.DisplayId, model) : new ModelLook();
                if (plan.Variants.Count > 1) plan.Note = $"{plan.Variants.Count} item displays use {System.IO.Path.GetFileName(model)}.";
                break;
            }
            case ViewerSubject.Character:
            {
                var (race, sex) = plan.Race!.Value;
                plan.Options = res.Options(race, sex);
                plan.Look = res.Character(race, sex, new CharacterLook());
                break;
            }
            default:
                plan.Look = new ModelLook();
                break;
        }
        return plan;
    }

    private void Apply(Plan plan)
    {
        _syncing = true;
        try
        {
            ClearLists();
            Subject = plan.Subject;
            _race = plan.Race;
            ModelPath = plan.Model;
            RefreshModified();
            var a = plan.Asset;
            var m = a.Model;
            Scene.Asset = a;
            Scene.Look = plan.Look;
            Scene.VisibleGeosets = null;
            Camera.Frame(a.BoundsMin, a.BoundsMax);
            HasModel = true;
            VariantNote = plan.Note;
            LookNote = string.Join(" ", plan.Look.Notes.Where(n => !n.StartsWith("character look", StringComparison.Ordinal)));

            _applyingVariant = true;
            foreach (var v in plan.Variants) Variants.Add(v);
            SelectedVariant = plan.Selected;
            _applyingVariant = false;

            FillSequences(m);
            SelectedSequence = Sequences.FirstOrDefault(s => s.Index == Scene.Sequence);
            DurationMs = Scene.Duration;
            for (int i = 0; i < a.Views.Length; i++)
                Lods.Add(new LodItem
                {
                    Index = i,
                    // M2: the .skin files of 1.60 leave the bone limit at 0 (MEASURED in every sampled model): not shown
                    Label = m.Modern != null && a.Views[i].BoneLimit == 0
                        ? $"{i + 1}: {m.Views[i].Submeshes.Length} parts, {a.Views[i].TriangleCount:N0} triangles"
                        : $"{i + 1}: {a.Views[i].BoneLimit} bones, {m.Views[i].Submeshes.Length} parts, {a.Views[i].TriangleCount:N0} triangles",
                });
            SelectedLod = Lods.FirstOrDefault();

            if (plan.Options is { } o)
            {
                foreach (var x in o.SkinColors) SkinColors.Add(x);
                foreach (var x in o.HairStyles) HairStyles.Add(x);
                foreach (var x in o.FacialHairs) FacialHairs.Add(x);
                SkinColor = SkinColors.FirstOrDefault();
                HairStyle = HairStyles.FirstOrDefault();
                FacialHair = FacialHairs.FirstOrDefault();
                RefillFaces(o);
                RefillHairColors(o);
                Face = Faces.FirstOrDefault();
                HairColor = HairColors.FirstOrDefault();
                _options = o;
            }
            else _options = null;

            RefreshGeosets();
            RefreshModelTextures();
            RefreshStats();
            RefreshSkeletonInfo();
            RefreshAnimationInfo();
            OnPropertyChanged(nameof(IsCharacter));
            OnPropertyChanged(nameof(HasVariants));
            OnPropertyChanged(nameof(VariantHeader));
            Scene.Playing = Playing;
            Scene.Speed = (float)Speed;
            ApplyDisplayOptions();
        }
        finally
        {
            _syncing = false;
        }
    }

    private CharacterOptions? _options;

    /// <summary>0.10.0: the animation list, with the same names as the Blender actions and "wds models anims" ("Stand [2]"
    /// = the second variation).</summary>
    private void FillSequences(M2Model m)
    {
        var res = Looks;
        var names = WoWData.Core.ModelIO.ModelExporter.ActionNames(m, res.AnimationName);
        Sequences.Clear();
        for (int i = 0; i < m.Sequences.Length; i++)
        {
            var s = m.Sequences[i];
            Sequences.Add(new SequenceItem { Index = i, DurationMs = s.Duration, Name = names[i], Label = $"{names[i]}  ·  {s.Duration / 1000.0:0.0#} s" });
        }
    }

    /// <summary>After an import or a revert changed the model: the animation list again, keeping the chosen one by name.</summary>
    private void RefreshSequences()
    {
        var m = Scene.Asset?.Model;
        if (m == null) return;
        string? keep = SelectedSequence?.Name;
        _syncing = true;
        FillSequences(m);
        var pick = Sequences.FirstOrDefault(x => x.Name == keep) ?? Sequences.FirstOrDefault(x => x.Index == Scene.Sequence);
        _syncing = false;
        SelectedSequence = pick;
        DurationMs = Scene.Duration;
    }

    private void RefillFaces(CharacterOptions o)
    {
        Faces.Clear();
        foreach (var x in o.FacesBySkin.GetValueOrDefault(SkinColor) ?? o.FacesBySkin.Values.FirstOrDefault() ?? new List<int> { 0 }) Faces.Add(x);
    }

    private void RefillHairColors(CharacterOptions o)
    {
        HairColors.Clear();
        foreach (var x in o.HairColorsByStyle.GetValueOrDefault(HairStyle) ?? new List<int> { 0 }) HairColors.Add(x);
    }

    private void ApplyDisplayOptions()
    {
        Scene.Wireframe = Wireframe;
        Scene.ShowBones = ShowBones;
        Scene.ShowAttachments = ShowAttachments;
        Scene.Loop = Loop;
        Scene.ShowBounds = ShowBounds;
        Scene.ShowGrid = ShowGrid;
        Scene.ShowGround = ShowGround;
        if (SelectedBackground != null) Scene.Background = SelectedBackground.Color;
    }

    // ---------------------------------------------------------------- variants and character looks

    partial void OnSelectedVariantChanged(VariantItem? value)
    {
        if (_applyingVariant || value == null || Scene.Asset == null) return;
        _ = ApplyLookAsync(() => value.IsItem ? Looks.ItemDisplay((int)value.DisplayId, ModelPath) : Looks.CreatureDisplay((int)value.DisplayId));
    }

    private int _lookToken;

    private async Task ApplyLookAsync(Func<ModelLook> make)
    {
        int token = ++_lookToken;
        var sw = Stopwatch.StartNew();
        var look = await Task.Run(make);
        if (token != _lookToken) return;
        Scene.Look = look;
        Scene.VisibleGeosets = null;
        LastLookMs = sw.Elapsed.TotalMilliseconds;
        LookNote = string.Join(" ", look.Notes.Where(n => !n.StartsWith("character look", StringComparison.Ordinal)));
        RefreshGeosets();
        RefreshModelTextures();
    }

    private void CharacterChanged()
    {
        if (_syncing || _race == null || Subject != ViewerSubject.Character) return;
        var (race, sex) = _race.Value;
        var look = new CharacterLook(SkinColor, Face, HairStyle, HairColor, FacialHair);
        _ = ApplyLookAsync(() => Looks.Character(race, sex, look));
    }

    partial void OnSkinColorChanged(int value)
    {
        if (_syncing || _options == null) return;
        _syncing = true;
        int face = Face;
        RefillFaces(_options);
        Face = Faces.Contains(face) ? face : Faces.FirstOrDefault();
        _syncing = false;
        CharacterChanged();
    }

    partial void OnFaceChanged(int value) => CharacterChanged();

    partial void OnHairStyleChanged(int value)
    {
        if (_syncing || _options == null) return;
        _syncing = true;
        int c = HairColor;
        RefillHairColors(_options);
        HairColor = HairColors.Contains(c) ? c : HairColors.FirstOrDefault();
        _syncing = false;
        CharacterChanged();
    }

    partial void OnHairColorChanged(int value) => CharacterChanged();
    partial void OnFacialHairChanged(int value) => CharacterChanged();

    /// <summary>Set every character choice at once (screenshots, tests).</summary>
    public Task SetCharacterAsync(CharacterLook look)
    {
        if (_race == null) return Task.CompletedTask;
        _syncing = true;
        SkinColor = look.Skin;
        if (_options != null) RefillFaces(_options);
        Face = look.Face;
        HairStyle = look.HairStyle;
        if (_options != null) RefillHairColors(_options);
        HairColor = look.HairColor;
        FacialHair = look.FacialHair;
        _syncing = false;
        var (race, sex) = _race.Value;
        return ApplyLookAsync(() => Looks.Character(race, sex, look));
    }

    [RelayCommand]
    private void RandomLook()
    {
        if (_options == null) return;
        var r = Random.Shared;
        static int Pick(IList<int> l, Random r) => l.Count == 0 ? 0 : l[r.Next(l.Count)];
        _syncing = true;
        SkinColor = Pick(SkinColors, r);
        RefillFaces(_options);
        Face = Pick(Faces, r);
        HairStyle = Pick(HairStyles, r);
        RefillHairColors(_options);
        HairColor = Pick(HairColors, r);
        FacialHair = Pick(FacialHairs, r);
        _syncing = false;
        CharacterChanged();
    }

    // ---------------------------------------------------------------- geosets

    /// <summary>Friendly names of character geoset groups (the group is id / 100; names from wowdev, matched to the
    /// client's defaults in CCharacterComponent_ApplyGeosets).</summary>
    public static string GeosetName(int id, bool character)
    {
        if (!character) return id == 0 ? "Main mesh (0)" : $"Mesh part {id}";
        if (id == 0) return "Body (0)";
        int g = id / 100, v = id % 100;
        string group = g switch
        {
            0 => "Hair style",
            1 => "Facial hair 1 (beard)",
            2 => "Facial hair 2 (sideburns)",
            3 => "Facial hair 3 (moustache)",
            4 => "Gloves",
            5 => "Boots",
            6 => "Tail / unused",
            7 => "Ears",
            8 => "Sleeves",
            9 => "Leg cuffs",
            10 => "Chest",
            11 => "Pants",
            12 => "Tabard",
            13 => "Robe / trousers",
            14 => "Unused",
            15 => "Cape",
            _ => "Group " + g,
        };
        return $"{group} {v} ({id})";
    }

    private void RefreshGeosets()
    {
        var a = Scene.Asset;
        foreach (var g in Geosets) g.Toggled = null;
        Geosets.Clear();
        if (a == null) return;
        bool character = _race != null;
        foreach (var id in a.GeosetIds)
        {
            var item = new GeosetItem { Id = id, Label = GeosetName(id, character), Group = character ? (id == 0 ? "Body" : (id / 100).ToString(CultureInfo.InvariantCulture)) : "" };
            item.IsVisible = Scene.IsGeosetVisible(id);
            item.Toggled = OnGeosetToggled;
            Geosets.Add(item);
        }
        OnPropertyChanged(nameof(GeosetHeader));
    }

    private void OnGeosetToggled(GeosetItem _)
    {
        Scene.VisibleGeosets = Geosets.Where(g => g.IsVisible).Select(g => g.Id).ToHashSet();
        OnPropertyChanged(nameof(GeosetHeader));
    }

    [RelayCommand]
    private void ShowAllGeosets()
    {
        foreach (var g in Geosets) { g.Toggled = null; g.IsVisible = true; g.Toggled = OnGeosetToggled; }
        OnGeosetToggled(null!);
    }

    [RelayCommand]
    private void DefaultGeosets()
    {
        Scene.VisibleGeosets = null;
        foreach (var g in Geosets) { g.Toggled = null; g.IsVisible = Scene.IsGeosetVisible(g.Id); g.Toggled = OnGeosetToggled; }
        OnPropertyChanged(nameof(GeosetHeader));
    }

    // ---------------------------------------------------------------- textures and stats

    private void RefreshModelTextures()
    {
        ModelTextures.Clear();
        var a = Scene.Asset;
        if (a == null) return;
        var m = a.Model;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < m.Textures.Length; i++)
        {
            var t = m.Textures[i];
            string slot = M2Schema.TextureTypes.TryGetValue(t.Type, out var tn) ? tn : $"slot {t.Type}";
            if (t.Type == 0)
            {
                var p = Looks.Canonical(t.FileName);
                if (!seen.Add(p)) continue;
                bool ok = a.FileTextures.TryGetValue(i, out var img) && img != null;
                ModelTextures.Add(new ModelTextureItem { Slot = "model file", FileName = System.IO.Path.GetFileName(p), Path = ok ? p : null, Tip = ok ? p : p + " (missing)" });
                continue;
            }
            if (Scene.Look?.Slots.TryGetValue(t.Type, out var r) == true)
            {
                foreach (var part in r.Parts)
                {
                    if (!seen.Add(part)) continue;
                    ModelTextureItem item = new()
                    {
                        Slot = r.Parts.Count > 1 ? $"{slot}: part of the composite" : slot, FileName = System.IO.Path.GetFileName(part), Path = part,
                        Tip = r.Parts.Count > 1 ? $"{part}\nCombined with the other parts into the body texture, like the game does." : part,
                    };
                    ModelTextures.Add(item);
                }
            }
            else ModelTextures.Add(new ModelTextureItem { Slot = slot, FileName = "(chosen by the game, e.g. a worn item)", Tip = "Shown grey: nothing fills this slot here." });
        }
    }

    private void AddStat(string label, string value) => Stats.Add(new StatItem { Label = label, Value = value });

    private RigInfo? _rig;

    /// <summary>0.10.0: bones, key bones, attachment points and events of the shown model.</summary>
    private void RefreshSkeletonInfo()
    {
        var a = Scene.Asset;
        if (a == null) { SkeletonInfo = ""; _rig = null; return; }
        _rig = RigInfo.Read(a.Model, _session.Looks?.Animations);
        var r = _rig;
        var keys = r.Bones.Where(b => b.KeyBoneId >= 0).Select(b => $"{b.KeyBoneName} = {b.Name}").ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{r.Bones.Count} bones ({r.Bones.Count(b => b.Animated)} animated, {r.Bones.Count(b => b.Vertices > 0)} move vertices), {r.Sequences.Count} animations, {r.GlobalSequences.Count} global sequences.");
        if (keys.Count > 0) sb.AppendLine("Key bones (the game finds them by name): " + string.Join(", ", keys));
        if (r.Attachments.Count > 0) sb.AppendLine("Attachment points: " + string.Join(", ", r.Attachments.Select(x => $"{x.Name} ({x.Id}) on {RigInfo.BoneName(x.Bone, x.Bone < r.Bones.Count ? r.Bones[x.Bone].KeyBoneId : -1)}")));
        if (r.Events.Count > 0) sb.AppendLine("Events: " + string.Join(", ", r.Events.Select(e => e.Code).Distinct()));
        if (r.Particles + r.Ribbons > 0) sb.AppendLine($"Particle emitters: {r.Particles}, ribbons: {r.Ribbons} (kept as they are when you edit; advanced JSON editing with Skeleton and animations).");
        SkeletonInfo = sb.ToString().TrimEnd();
    }

    /// <summary>0.10.0: the current animation's properties and events.</summary>
    private void RefreshAnimationInfo()
    {
        var r = _rig;
        int si = Scene.Sequence;
        if (r == null || si < 0 || si >= r.Sequences.Count) { AnimationInfo = EventsInfo = ""; return; }
        var s = r.Sequences[si];
        var cat = _session.Looks?.Animations;
        var parts = new List<string> { s.Loops ? "loops" : "plays once" };
        if (s.Variations > 1) parts.Add($"variation {s.Variation + 1} of {s.Variations}, chance {s.ChancePercent:0.#} %");
        parts.Add($"blend {s.BlendTime} ms");
        if (s.MoveSpeed != 0) parts.Add($"move speed {s.MoveSpeed:0.##} yd/s");
        if (s.ReplayMax > 0) parts.Add($"repeats {s.ReplayMin}-{s.ReplayMax}");
        var roles = AnimationRoles.Of(s.AnimId);
        AnimationInfo = $"{s.Name} (animation {s.AnimId}): {string.Join(", ", parts)}." + (roles.Count > 0 ? $" The game plays it for: {string.Join("; ", roles)}." : "") +
                        (cat?.FallbackText(s.AnimId) is { } fb ? $" Without it the game plays {fb}." : "") +
                        ExternalKeysText(Scene.Asset?.Model, si);
        var ev = r.Events.SelectMany(e => e.Triggers.Where(t => t.Sequence == si).Select(t => (e.Code, e.Meaning, t.Time))).OrderBy(x => x.Time).ToList();
        EventsInfo = ev.Count == 0 ? "" : "Events: " + string.Join("; ", ev.Select(x => $"{x.Code} {x.Meaning} at {x.Time / 1000.0:0.00} s"));
    }

    private void RefreshStats()
    {
        Stats.Clear();
        var a = Scene.Asset;
        if (a == null) return;
        var m = a.Model;
        int view = Math.Min(Scene.ViewIndex, Math.Max(0, a.Views.Length - 1));
        AddStat("Vertices", m.Vertices.Length.ToString("N0"));
        AddStat("Triangles", a.Views.Length == 0 ? "0" : $"{a.Views[view].TriangleCount:N0} (view {view + 1})");
        AddStat("Bones", m.Bones.Length.ToString("N0"));
        AddStat("Sequences", m.Sequences.Length.ToString("N0"));
        AddStat("Texture units", a.Views.Length == 0 ? "0" : $"{m.Views[view].TexUnits.Length:N0} (view {view + 1})");
        AddStat("Attachments", m.Attachments.Length.ToString("N0"));
        AddStat("Textures", m.Textures.Length.ToString("N0"));
        AddStat("Geosets", a.GeosetIds.Length.ToString("N0"));
        if (m.ParticleCount + m.RibbonCount > 0) AddStat("Particles / ribbons", $"{m.ParticleCount} / {m.RibbonCount} (not drawn)");
        if (m.Modern is { } mi)
        {
            // M2: a model of another version (the modern reader's report is under the numbers)
            AddStat("Format", $"M2 {mi.Version} (MD21)");
            AddStat("Views read", $"{mi.ViewsRead} of {mi.SkinProfiles}");
            if (mi.ExternalSequences > 0) AddStat("In .anim files", $"{mi.ExternalSequences} (loaded when chosen)");
            if (mi.Effects is { } fx) AddStat("Particles / ribbons", $"{fx.Particles.Count} / {fx.Ribbons.Count} (drawn approximately)");
        }
        else AddStat("Format", $"M2 version 0x{m.Version:x}");
        FormatNotes = ModernNotes(m);
    }

    // ---------------------------------------------------------------- animation and display options

    partial void OnSelectedSequenceChanged(SequenceItem? value)
    {
        if (value == null || Scene.Asset == null) return;
        if (Scene.Sequence != value.Index) Scene.Sequence = value.Index;
        DurationMs = Scene.Duration;
        RefreshAnimationInfo();
        _syncing = true;
        TimeMs = 0;
        _syncing = false;
        OnPropertyChanged(nameof(TimeText));
    }

    partial void OnSelectedLodChanged(LodItem? value)
    {
        if (value == null) return;
        Scene.ViewIndex = value.Index;
        RefreshStats();
    }

    partial void OnPlayingChanged(bool value)
    {
        Scene.Playing = value;
        OnPropertyChanged(nameof(PlayGlyph));
        OnPropertyChanged(nameof(PlayTip));
    }

    partial void OnSpeedChanged(double value)
    {
        Scene.Speed = (float)value;
        OnPropertyChanged(nameof(SpeedText));
    }

    partial void OnTimeMsChanged(double value)
    {
        OnPropertyChanged(nameof(TimeText));
        if (_syncing) return;
        Scene.SequenceTime = value;
    }

    partial void OnDurationMsChanged(double value) => OnPropertyChanged(nameof(TimeText));
    partial void OnWireframeChanged(bool value) => Scene.Wireframe = value;
    partial void OnShowBonesChanged(bool value) => Scene.ShowBones = value;
    partial void OnShowAttachmentsChanged(bool value) => Scene.ShowAttachments = value;
    partial void OnLoopChanged(bool value) => Scene.Loop = value;
    partial void OnShowBoundsChanged(bool value) => Scene.ShowBounds = value;
    partial void OnShowGridChanged(bool value) => Scene.ShowGrid = value;
    partial void OnShowGroundChanged(bool value) => Scene.ShowGround = value;
    partial void OnSelectedBackgroundChanged(BackgroundItem? value)
    {
        if (value == null) return;
        Scene.Background = value.Color;
        _syncing = true;
        BackgroundHex = $"#{(int)Math.Round(value.Color.X * 255):X2}{(int)Math.Round(value.Color.Y * 255):X2}{(int)Math.Round(value.Color.Z * 255):X2}";
        _syncing = false;
    }

    /// <summary>A custom background colour typed as #RRGGBB.</summary>
    partial void OnBackgroundHexChanged(string value)
    {
        if (_syncing) return;
        var t = value.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return;
        Scene.Background = new Vector3(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    }

    /// <summary>Called by the view after every rendered frame: moves the timeline with the clock.</summary>
    public void OnFrame()
    {
        if (Scene.Asset == null) return;
        if (Scene.Sequence != (SelectedSequence?.Index ?? -1) && Sequences.Count > Scene.Sequence)
            SelectedSequence = Sequences[Scene.Sequence];
        if (Scene.Playing != Playing) Playing = Scene.Playing;
        double t = Scene.SequenceTime;
        if (Math.Abs(t - TimeMs) >= 10 || (t == 0 && TimeMs != 0))
        {
            _syncing = true;
            TimeMs = t;
            _syncing = false;
        }
    }

    [RelayCommand]
    private void TogglePlay() => Playing = !Playing;

    [RelayCommand]
    private void ResetView() => Camera.ResetView();

    [RelayCommand]
    private void OpenTexture(ModelTextureItem? item)
    {
        if (item?.Path != null) OpenTextureRequested?.Invoke(item.Path, item.Slot);
    }

    [RelayCommand]
    private async Task SaveImage()
    {
        if (Capture == null || Scene.Asset == null) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(ModelPath) + (SelectedVariant != null ? "_" + SelectedVariant.DisplayId : "") +
                   (SelectedSequence != null ? "_" + Looks.AnimationName(Scene.Asset.Model.Sequences[SelectedSequence.Index].AnimId) : "") + ".png";
        var dir = System.IO.Path.Combine(_session.Settings.DefaultExportDir, "Images");
        Directory.CreateDirectory(dir);
        var file = Dialogs.PickSaveFile("Save image of the 3D view", "PNG image (*.png)|*.png", name, dir);
        if (file == null) return;
        try
        {
            var img = await Capture();
            await Task.Run(() => Png.Save(img, file, false));
            Dialogs.ShowInExplorer(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dialogs.Error("Could not save the image", e.Message);
        }
    }

    /// <summary>A side-by-side comparison of two variants of the shown model (the demo of the 3c compare): two skins of a
    /// creature or item, or the current character look against the default one.</summary>
    public async Task<ModelCompareViewModel?> MakeCompareAsync(VariantItem? left = null, VariantItem? right = null)
    {
        var asset = Scene.Asset;
        if (asset == null) return null;
        var res = Looks;
        var path = ModelPath;
        if (Subject == ViewerSubject.Character && _race != null)
        {
            var (race, sex) = _race.Value;
            var look = new CharacterLook(SkinColor, Face, HairStyle, HairColor, FacialHair);
            var (l, r) = await Task.Run(() => (res.Character(race, sex, look), res.Character(race, sex, new CharacterLook())));
            return new ModelCompareViewModel($"Compare looks: {System.IO.Path.GetFileName(path)}",
                new ModelCompareSide("Current look", asset, l, $"skin {look.Skin}, face {look.Face}, hair {look.HairStyle}/{look.HairColor}, facial hair {look.FacialHair}"),
                new ModelCompareSide("Default look", asset, r, "all choices 0 (character creation start)"));
        }
        if (Variants.Count < 2) return null;
        left ??= SelectedVariant ?? Variants[0];
        var a = left;
        right ??= Variants.FirstOrDefault(v => v != a && !v.Skins.SequenceEqual(a.Skins)) ?? Variants.First(v => v != a);
        var b = right;
        ModelLook Look(VariantItem v) => v.IsItem ? res.ItemDisplay((int)v.DisplayId, path) : res.CreatureDisplay((int)v.DisplayId);
        var (la, lb) = await Task.Run(() => (Look(a), Look(b)));
        return new ModelCompareViewModel($"Compare skins: {System.IO.Path.GetFileName(path)}",
            new ModelCompareSide(a.Label, asset, la, a.Detail), new ModelCompareSide(b.Label, asset, lb, b.Detail));
    }

    /// <summary>What "Export for Blender" needs to know about the model shown: what it is, the selected display, the
    /// character look and the geosets shown.</summary>
    public (WoWData.Core.ModelIO.ModelSubject Subject, long? Display, CharacterLook? Look, HashSet<int>? Geosets, List<long>? NpcDisplays) ExportContext()
    {
        var subject = Subject switch
        {
            ViewerSubject.Creature => WoWData.Core.ModelIO.ModelSubject.Creature,
            ViewerSubject.Npc => WoWData.Core.ModelIO.ModelSubject.Npc,
            ViewerSubject.Character => WoWData.Core.ModelIO.ModelSubject.Character,
            ViewerSubject.Item => WoWData.Core.ModelIO.ModelSubject.Item,
            _ => WoWData.Core.ModelIO.ModelSubject.Plain,
        };
        CharacterLook? look = subject == WoWData.Core.ModelIO.ModelSubject.Character ? new CharacterLook(SkinColor, Face, HairStyle, HairColor, FacialHair) : null;
        var geosets = Scene.VisibleGeosets ?? Scene.Look?.Geosets;
        return (subject, SelectedVariant?.DisplayId, look, geosets == null ? null : new HashSet<int>(geosets),
            subject == WoWData.Core.ModelIO.ModelSubject.Npc ? Variants.Select(v => v.DisplayId).ToList() : null);
    }

    /// <summary>The look shown now (for the original-vs-edited comparison).</summary>
    public ModelLook? CurrentLook => Scene.Look;

    /// <summary>
    /// Take over what another viewer shows (opening a pop-out window from the docked viewer): the same model loaded the
    /// same way, then the same skin or character look, animation, speed, detail level, display options, background and
    /// camera.
    /// </summary>
    public async Task CopyFromAsync(ModelViewerViewModel other)
    {
        if (other.Loaded is not { } l) return;
        await LoadAsync(l.Entry, l.Details);
        if (!HasModel || !string.Equals(ModelPath, other.ModelPath, StringComparison.OrdinalIgnoreCase)) return;
        if (other.Subject == ViewerSubject.Character)
            await SetCharacterAsync(new CharacterLook(other.SkinColor, other.Face, other.HairStyle, other.HairColor, other.FacialHair));
        else if (other.SelectedVariant is { } v && Variants.FirstOrDefault(x => x.DisplayId == v.DisplayId && x.IsItem == v.IsItem) is { } mine && mine != SelectedVariant)
        {
            _applyingVariant = true;
            SelectedVariant = mine;
            _applyingVariant = false;
            await ApplyLookAsync(() => mine.IsItem ? Looks.ItemDisplay((int)mine.DisplayId, ModelPath) : Looks.CreatureDisplay((int)mine.DisplayId));
        }
        if (other.SelectedSequence is { } s) SelectedSequence = Sequences.FirstOrDefault(x => x.Index == s.Index) ?? SelectedSequence;
        if (other.SelectedLod is { } lod) SelectedLod = Lods.FirstOrDefault(x => x.Index == lod.Index) ?? SelectedLod;
        Speed = other.Speed;
        Wireframe = other.Wireframe;
        ShowBones = other.ShowBones;
        ShowBounds = other.ShowBounds;
        ShowGrid = other.ShowGrid;
        ShowGround = other.ShowGround;
        SelectedBackground = Backgrounds.FirstOrDefault(b => b.Label == other.SelectedBackground?.Label) ?? SelectedBackground;
        if (other.BackgroundHex != BackgroundHex) BackgroundHex = other.BackgroundHex;
        if (other.Scene.VisibleGeosets is { } g)
        {
            Scene.VisibleGeosets = new HashSet<int>(g);
            foreach (var item in Geosets) { item.Toggled = null; item.IsVisible = g.Contains(item.Id); item.Toggled = OnGeosetToggled; }
            OnPropertyChanged(nameof(GeosetHeader));
        }
        Playing = other.Playing;
        if (!other.Playing) TimeMs = other.TimeMs;
        Camera.CopyFrom(other.Camera);
    }

    /// <summary>Save the current view as a PNG without a dialog (screenshots and tests).</summary>
    public async Task SaveImageToAsync(string file)
    {
        if (Capture == null) throw new InvalidOperationException("the 3D view is not shown");
        var img = await Capture();
        Png.Save(img, file, false);
    }
}
