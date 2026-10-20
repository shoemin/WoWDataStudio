using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Blp;
using WoWData.Core.Designers;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Session;
using WoWDataStudio.Helpers;

namespace WoWDataStudio.ViewModels;

/// <summary>One field of the spell form: its label, its value as text (friendly units), its choices when it has named
/// values, and its help.</summary>
public sealed partial class SpellFieldView : ObservableObject
{
    private readonly SpellDesignerViewModel _vm;
    private bool _loading;

    public SpellFieldView(SpellDesignerViewModel vm, SpellField f, string text, IReadOnlyList<DbcChoice>? choices, long value)
    {
        _vm = vm;
        F = f;
        _loading = true;
        _text = text;
        if (choices != null)
        {
            Choices.AddRange(choices);
            if (Choices.All(c => c.Value != value)) Choices.Insert(0, new DbcChoice(value, $"{value} (not a known value)"));
            _choice = Choices.First(c => c.Value == value);
        }
        _loading = false;
    }

    public SpellField F { get; }
    public string Label => F.Label;
    public string Help => F.Help + (FlagNames.Length > 0 ? "\nBits: " + FlagNames : "");
    public string FlagNames { get; init; } = "";
    public bool IsChoice => Choices.Count > 0;
    public bool IsText => !IsChoice;
    public bool IsLong => F.Id is "description" or "aura-text";
    public List<DbcChoice> Choices { get; } = new();

    /// <summary>The named bits of a flags field, as checkboxes ("Passive", "Is ability"...).</summary>
    public ObservableCollection<SpellFlagItem> Flags { get; } = new();
    public bool HasFlags => Flags.Count > 0;
    private long _flagValue;

    /// <summary>Fill the checkboxes of a flags field (called once, while loading).</summary>
    internal void SetFlags(WoWData.Core.Dbc.DbcNamedValues values, long value)
    {
        _loading = true;
        _flagValue = value & 0xFFFFFFFF;
        foreach (var (bit, name) in values.Values.Where(x => x.Value != 0)) Flags.Add(new SpellFlagItem(this, bit, name, (_flagValue & bit) == bit));
        _loading = false;
    }

    internal void FlagToggled()
    {
        if (_loading) return;
        long named = 0, on = 0;
        foreach (var f in Flags)
        {
            named |= f.Bit;
            if (f.IsOn) on |= f.Bit;
        }
        long v = (_flagValue & ~named) | on;   // the bits without a name are kept
        _vm.SetField(this, "0x" + v.ToString("X", CultureInfo.InvariantCulture));
    }

    [ObservableProperty] private string _text;
    [ObservableProperty] private DbcChoice? _choice;

    [RelayCommand]
    private void Commit() => _vm.SetField(this, Text);

    partial void OnChoiceChanged(DbcChoice? value)
    {
        if (!_loading && value != null) _vm.SetField(this, value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>One named bit of a flags field (a checkbox).</summary>
public sealed partial class SpellFlagItem : ObservableObject
{
    private readonly SpellFieldView _field;

    public SpellFlagItem(SpellFieldView field, long bit, string name, bool on)
    {
        _field = field;
        Bit = bit;
        Name = name;
        _isOn = on;
    }

    public long Bit { get; }
    public string Name { get; }
    public string Tip => $"{Name} (0x{Bit:X})";
    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _field.FlagToggled();
}

/// <summary>A group of the form (Basics, Casting, Effect 1...), with the effect's explanation for effect groups.</summary>
public sealed class SpellFieldGroupView
{
    public string Name { get; init; } = "";
    public List<SpellFieldView> Fields { get; init; } = new();
    public string Explanation { get; init; } = "";
    public bool HasExplanation => Explanation.Length > 0;
    public bool IsExpanded { get; set; }
}

public sealed record SpellHit(long Id, string Name, string Rank)
{
    public string Text => $"{Name}{(Rank.Length > 0 ? $" ({Rank})" : "")} [{Id}]";
}

public sealed record VisualHit(long Visual, string Spells)
{
    public string Text => $"{Visual}: {Spells}";
}

/// <summary>
/// 0.12.0: the Spell designer tab: find a spell, make a new one or clone it, edit it through the grouped form with the
/// live tooltip preview and the checks, pick or duplicate its visual (with the 3D view of its effect models), make it
/// learnable (trainers, skill lines) and save it into the workspace with the server's spell_template in sync. All the
/// logic is WoWData.Core.Designers (wds spells uses the same).
/// </summary>
public sealed partial class SpellDesignerViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private readonly BrowserViewModel _browser;

    public SpellDesignerViewModel(AppSession session, MainViewModel main, BrowserViewModel browser)
    {
        _session = session;
        _main = main;
        _browser = browser;
        Viewer = new ModelViewerViewModel(session, browser.ModelCache);
    }

    public DesignSession? Session { get; private set; }
    public SpellDesigner? Designer { get; private set; }
    public ModelViewerViewModel Viewer { get; }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _serverText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    internal void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    /// <summary>Open the design session (the game's tables, and the server database when it is on).
    /// 0.13.3: one load at a time. Two callers at once (a tab switch and a command) used to open two sessions, and the
    /// designer of one could end up paired with the session of the other: a new spell's visual or rank spells then
    /// seemed missing ("SpellVisual ... does not exist", "There is no Spell with ID ..."), seen in the screenshot run.</summary>
    public Task EnsureLoadedAsync()
    {
        if (IsLoaded) return Task.CompletedTask;
        return _loadTask ??= LoadAsync();
    }

    private Task? _loadTask;

    private async Task LoadAsync()
    {
        try
        {
            var (session, designer, text) = await Task.Run(OpenSession);
            Session = session;
            Designer = designer;
            ServerText = text;
            IsLoaded = true;
            RefreshUnusable();
        }
        finally { _loadTask = null; }
    }

    private (DesignSession Session, SpellDesigner Designer, string ServerText) OpenSession()
    {
        var dbc = _session.Dbc ?? throw new InvalidOperationException("No game folder is open.");
        ServerEditor? ed = null;
        string? note = null;
        if (_session.Settings.Server.Enabled && _session.ServerData is { } svc)
        {
            try
            {
                _ = svc.Schema;
                ed = svc.NewEditor();
            }
            catch (ServerDataException e) { note = $"The server database could not be reached ({e.Message}): only the game's tables are edited."; }
        }
        else note = "The server database is turned off (Settings): only the game's tables are edited; \"Sync with the server\" brings it in line later.";
        var session = new DesignSession(dbc, ed, (_session.Settings.ServerCustomIdStart, _session.Settings.ServerCustomIdEnd), note, (_session.Settings.SpellIdStart, _session.Settings.SpellIdEnd));
        var text = ed != null ? $"Server: {ed.Service.Database} on {ed.Service.Db.Settings.Host}:{ed.Service.Db.Settings.Port}. Saving writes spell_template (and trainers, skill lines) into your workspace in step with the game's tables." : note!;
        return (session, new SpellDesigner(session), text);
    }

    /// <summary>Start again (after a save or a discard, and when the game folder or the server changed).</summary>
    public void Reset()
    {
        IsLoaded = false;
        Session = null;
        Designer = null;
        CurrentId = 0;
        Groups.Clear();
    }

    // ------------------------------------------------------------------ find, new, clone

    [ObservableProperty] private string _searchText = "";
    public ObservableCollection<SpellHit> Results { get; } = new();
    [ObservableProperty] private SpellHit? _selectedResult;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newRank = "Rank 1";

    [RelayCommand]
    private async Task SearchAsync()
    {
        await EnsureLoadedAsync();
        Results.Clear();
        if (SearchText.Trim().Length == 0) return;
        foreach (var h in Designer!.Search(SearchText, 200)) Results.Add(new SpellHit(h.Id, h.Name, h.Rank));
        SelectedResult = Results.FirstOrDefault();
        SetStatus(Results.Count == 0 ? $"No spell matches \"{SearchText}\"." : $"{Results.Count} spell(s). Open one, or clone it into a new spell.");
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedResult == null) return;
        await OpenAsync(SelectedResult.Id);
    }

    public async Task OpenAsync(long id)
    {
        await EnsureLoadedAsync();
        if (!Designer!.Exists(id))
        {
            SetStatus($"There is no spell {id}.", true);
            return;
        }
        CurrentId = id;
        Refresh();
        SetStatus($"{Designer.Title(id)}: edit a field and press Enter; the tooltip and the checks follow.");
    }

    [RelayCommand]
    private async Task NewSpellAsync()
    {
        await EnsureLoadedAsync();
        var name = NewName.Trim().Length > 0 ? NewName.Trim() : "New spell";
        try
        {
            CurrentId = Designer!.New(name, rank: NewRank.Trim());
            Refresh();
            SetStatus($"New spell {CurrentId}: {name} (instant, self, mana). Choose its effects; \"Save to my workspace\" keeps it.");
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        await EnsureLoadedAsync();
        long src = SelectedResult?.Id ?? CurrentId;
        if (src == 0)
        {
            SetStatus("Find and select the spell to clone first.", true);
            return;
        }
        try
        {
            var name = NewName.Trim().Length > 0 ? NewName.Trim() : null;
            CurrentId = Designer!.Clone(src, name, rank: name != null && NewRank.Trim().Length > 0 ? NewRank.Trim() : null);
            Refresh();
            SetStatus($"Cloned {Designer.Title(src)} as {Designer.Title(CurrentId)}: every value, the icon and the visual were copied.");
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    // ------------------------------------------------------------------ the form

    [ObservableProperty] private long _currentId;
    [ObservableProperty] private string _title = "No spell open: find one on the left, or make a new one.";
    [ObservableProperty] private int _level = 1;
    [ObservableProperty] private string _tooltipText = "";
    [ObservableProperty] private string _auraText = "";
    [ObservableProperty] private ImageSource? _iconImage;
    [ObservableProperty] private string _iconText = "";
    public bool HasSpell => CurrentId > 0;
    public ObservableCollection<SpellFieldGroupView> Groups { get; } = new();
    public ObservableCollection<DesignProblem> Problems { get; } = new();
    [ObservableProperty] private string _checksText = "";

    partial void OnCurrentIdChanged(long value) => OnPropertyChanged(nameof(HasSpell));
    partial void OnLevelChanged(int value) => RefreshPreview();

    private readonly HashSet<string> _expanded = new() { "Basics", "Casting", "Effect 1" };

    /// <summary>Rebuild the form from the spell's row.</summary>
    public void Refresh()
    {
        if (Designer == null || CurrentId == 0) return;
        var d = Designer;
        var row = d.Row(CurrentId);
        var t = d.Spells;
        foreach (var g in Groups.Where(g => g.IsExpanded)) _expanded.Add(g.Name);
        foreach (var g in Groups.Where(g => !g.IsExpanded)) _expanded.Remove(g.Name);
        Groups.Clear();
        Title = d.Title(CurrentId);
        foreach (var g in SpellForm.Groups)
        {
            var fields = new List<SpellFieldView>();
            foreach (var f in SpellForm.Fields.Where(f => f.Group == g))
            {
                var c = t.Layout.Column(f.Column);
                List<DbcChoice>? choices = null;
                long value = 0;
                string flags = "";
                if (c != null && c.IsNumber) value = t.Number(row, c);
                if (f.Kind == SpellFieldKind.Choice && c?.Values != null && !c.Values.IsFlags)
                    choices = c.Values.Values.Select(v => new DbcChoice(v.Value, $"{v.Value} · {v.Name}")).ToList();
                if (f.Kind == SpellFieldKind.Flags && c?.Values != null) flags = string.Join(", ", c.Values.Values.Where(v => v.Value != 0).Select(v => v.Name));
                var text = f.Kind == SpellFieldKind.Flags && c?.Values != null && value != 0 ? c.Values.Describe(value) : SpellForm.Show(Session!, row, f, d.ServerExtras(CurrentId));
                var fv = new SpellFieldView(this, f, text, choices, value) { FlagNames = flags };
                if (f.Kind == SpellFieldKind.Flags && c?.Values is { IsFlags: true } named) fv.SetFlags(named, value);
                fields.Add(fv);
            }
            string expl = "";
            if (g.StartsWith("Effect ", StringComparison.Ordinal))
            {
                int e = g[7] - '0';
                long eff = t.Number(row, t.Layout.Column($"Effect{e}")!);
                long aura = t.Number(row, t.Layout.Column($"EffectAura{e}")!);
                if (eff != 0)
                {
                    var ei = SpellRules.Effect(eff);
                    expl = $"What this effect does: {ei.Explanation}" + (ei.MiscValue.Length > 0 ? $" Misc value: {ei.MiscValue}." : "") + " " + ei.ServerText;
                    if (aura != 0)
                    {
                        var ai = SpellRules.Aura(aura);
                        expl += $"\nThe aura: {ai.Explanation}" + (ai.MiscValue.Length > 0 ? $" Misc value: {ai.MiscValue}." : "") + " " + ai.ServerText;
                    }
                }
                else expl = "This effect slot is empty: choose an effect type.";
            }
            Groups.Add(new SpellFieldGroupView { Name = g, Fields = fields, Explanation = expl, IsExpanded = _expanded.Contains(g) });
        }
        Level = Math.Clamp(Level <= 1 ? SpellText.DefaultLevel(t, row) : Level, 1, 255);
        RefreshPreview();
        RefreshIcon();
        RefreshVisual();
        RefreshLearning();
        OnPropertyChanged(nameof(HasSpell));
    }

    private void RefreshPreview()
    {
        if (Designer == null || CurrentId == 0) return;
        var desc = Designer.Tooltip(CurrentId, Level);
        TooltipText = desc.Text;
        var t = Designer.Spells;
        AuraText = t.Text(Designer.Row(CurrentId), t.Layout.Column("AuraDescription")!, 0).Length > 0 ? Designer.Tooltip(CurrentId, Level, aura: true).Text : "(no buff text)";
        Problems.Clear();
        foreach (var p in Designer.Check(CurrentId)) Problems.Add(p);
        ChecksText = Problems.Count == 0 ? "No problems: the 1.12 client and server can handle this spell." : $"{Problems.Count(p => p.IsError)} error(s) (saving is refused), {Problems.Count(p => !p.IsError)} warning(s) or note(s).";
    }

    private void RefreshIcon()
    {
        IconImage = null;
        IconText = "";
        var t = Designer!.Spells;
        long icon = t.Number(Designer.Row(CurrentId), t.Layout.Column("SpellIconID")!);
        var it = Session!.Lookup("SpellIcon");
        if (it?.Find(icon) is not { } ir) return;
        var path = it.Text(ir, it.Layout.Column("TextureFilename")!, 0) + ".blp";
        IconText = SpellForm.IconName(path[..^4]);
        try
        {
            var data = Session.ReadFile(path);
            if (data != null) IconImage = ImageSources.ToBitmap(BlpFile.Parse(data).Decode(0));
        }
        catch (Exception e) when (e is BlpException or ArgumentException or InvalidOperationException) { }
    }

    /// <summary>A field was edited (Enter, a choice): set it through the designer, then show the new state.</summary>
    public void SetField(SpellFieldView f, string text)
    {
        if (Designer == null || CurrentId == 0) return;
        var r = Designer.Set(CurrentId, f.F.Id, text);
        SetStatus(r.Message, !r.Ok);
        Refresh();
    }

    // ------------------------------------------------------------------ icons

    [ObservableProperty] private string _iconSearch = "";
    public ObservableCollection<string> IconResults { get; } = new();
    [ObservableProperty] private string? _selectedIcon;

    [RelayCommand]
    private void FindIcons()
    {
        IconResults.Clear();
        var w = IconSearch.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0 || _session.Data == null) return;
        foreach (var p in _session.Data.Glob("Interface\\Icons\\*.blp").Where(p => w.All(p.ToLowerInvariant().Contains)).Take(200)) IconResults.Add(SpellForm.IconName(p[..^4]));
        SetStatus($"{IconResults.Count} icon(s). Pick one and \"Use this icon\".");
    }

    [RelayCommand]
    private void UseIcon()
    {
        if (SelectedIcon == null || Designer == null || CurrentId == 0) return;
        var r = Designer.Set(CurrentId, "icon", SelectedIcon);
        SetStatus(r.Message, !r.Ok);
        Refresh();
    }

    // ------------------------------------------------------------------ visuals

    public ObservableCollection<string> VisualParts { get; } = new();
    public ObservableCollection<VisualModel> VisualModels { get; } = new();
    [ObservableProperty] private VisualModel? _selectedModel;
    [ObservableProperty] private string _visualSearch = "";
    public ObservableCollection<VisualHit> VisualResults { get; } = new();
    [ObservableProperty] private VisualHit? _selectedVisual;
    public List<string> SlotChoices { get; } = SpellVisuals.Slots.Select(s => s.Id == "state" ? "aura" : s.Id).ToList();
    [ObservableProperty] private string _composeSlot = "missile";
    [ObservableProperty] private string _tintText = "#8040FF";
    [ObservableProperty] private string _scaleText = "1.5";

    private void RefreshVisual()
    {
        VisualParts.Clear();
        VisualModels.Clear();
        var t = Designer!.Spells;
        long v = t.Number(Designer.Row(CurrentId), t.Layout.Column("SpellVisual")!);
        if (v <= 0 || Session!.Lookup("SpellVisual")?.Find(v) == null)
        {
            VisualParts.Add("No visual.");
            return;
        }
        var info = SpellVisuals.Describe(Session, v);
        VisualParts.Add($"SpellVisual {v}" + (info.UsedBy.Count > 1 ? $" (shared with {info.UsedBy.Count - 1} other spell(s): changing a part makes a copy)" : ""));
        foreach (var s in info.Slots.Where(s => s.Value != 0)) VisualParts.Add($"{s.Slot.Label}: {s.Text}");
        foreach (var m in info.Models) VisualModels.Add(m);
    }

    [RelayCommand]
    private async Task FindVisualsAsync()
    {
        await EnsureLoadedAsync();
        VisualResults.Clear();
        foreach (var (v, sp) in SpellVisuals.Find(Session!, VisualSearch, 100)) VisualResults.Add(new VisualHit(v, sp));
        SetStatus($"{VisualResults.Count} visual(s). \"Use this visual\" takes it whole; \"Use its part\" takes one part ({ComposeSlot}).");
    }

    [RelayCommand]
    private void UseVisual()
    {
        if (SelectedVisual == null || Designer == null || CurrentId == 0) return;
        var r = Designer.Set(CurrentId, "visual", SelectedVisual.Visual.ToString(CultureInfo.InvariantCulture));
        SetStatus(r.Message, !r.Ok);
        Refresh();
    }

    [RelayCommand]
    private void UseVisualPart()
    {
        if (SelectedVisual == null || Designer == null || CurrentId == 0) return;
        try
        {
            var slot = ComposeSlot == "aura" ? "state" : ComposeSlot;
            SetStatus(SpellVisuals.Compose(Session!, Designer, CurrentId, new Dictionary<string, string> { [slot] = $"visual:{SelectedVisual.Visual}" }));
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
        Refresh();
    }

    [RelayCommand]
    private void DuplicateVisual()
    {
        if (Designer == null || CurrentId == 0) return;
        try
        {
            if (!float.TryParse(ScaleText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)) throw new DesignException("The size is a number (1 = unchanged).");
            System.Numerics.Vector3? tint = TintText.Trim().Length == 0 ? null : SpellVisuals.ParseTint(TintText);
            SetStatus(SpellVisuals.Duplicate(Session!, Designer, CurrentId, scale, tint, $"V{CurrentId}") + " Save to see the copies in 3D.");
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
        Refresh();
    }

    /// <summary>"Show in 3D": the selected effect model in the designer's own 3D view (saved files; a new copy after saving).</summary>
    [RelayCommand]
    private async Task ShowModelAsync()
    {
        var m = SelectedModel ?? VisualModels.FirstOrDefault();
        if (m == null) return;
        var path = System.IO.Path.ChangeExtension(m.File.Replace('/', '\\'), ".m2");
        if (!_session.FileExists(path))
        {
            SetStatus($"{path} is not saved yet: \"Save to my workspace\" first, then show it.", true);
            return;
        }
        var entry = new IndexEntry { Kind = AssetKind.SpellEffect, Key = "file:" + path, Path = path, Name = m.Name };
        var details = new EntryDetails { Title = $"{m.Name} ({m.Where})" };
        details.Models.Add(path);
        await Viewer.LoadAsync(entry, details);
    }

    // ------------------------------------------------------------------ learning

    public ObservableCollection<string> Trainers { get; } = new();
    public ObservableCollection<string> SkillLines { get; } = new();
    [ObservableProperty] private string _trainerText = "";
    [ObservableProperty] private string _trainerLevel = "";
    [ObservableProperty] private string _trainerCost = "0";
    [ObservableProperty] private string _skillLineText = "";
    [ObservableProperty] private string _skillClasses = "";

    private void RefreshLearning()
    {
        Trainers.Clear();
        SkillLines.Clear();
        foreach (var t in Designer!.Trainers(CurrentId)) Trainers.Add($"{t.TrainerName}: level {t.Level}, {SpellDesigner.Money(t.Cost)} (learn spell {t.LearnSpell})");
        if (Trainers.Count == 0) Trainers.Add(Session!.HasServer ? "No trainer teaches it." : "Trainers need the server database (Settings).");
        foreach (var s in Designer.SkillLines(CurrentId)) SkillLines.Add($"{s.SkillName} (SkillLineAbility {s.Id}): {SpellDesigner.ClassNames(s.ClassMask)}, {(s.AcquireMethod == 1 ? "automatic" : "taught")}");
        if (SkillLines.Count == 0) SkillLines.Add("No skill line.");
        if (TrainerLevel.Length == 0) TrainerLevel = Designer.Spells.Number(Designer.Row(CurrentId), Designer.Spells.Layout.Column("SpellLevel")!).ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void AddToTrainer()
    {
        if (Designer == null || CurrentId == 0) return;
        try
        {
            if (!long.TryParse(TrainerLevel, out var lvl)) throw new DesignException("The level is a number.");
            var r = Designer.AddToTrainer(CurrentId, TrainerText.Trim(), lvl, SpellDesigner.ParseMoney(TrainerCost));
            SetStatus(r.Message, !r.Ok);
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
        Refresh();
    }

    [RelayCommand]
    private void AddToSkillLine()
    {
        if (Designer == null || CurrentId == 0) return;
        try
        {
            if (!long.TryParse(SkillLineText.Trim(), out var skill)) throw new DesignException("The skill line is a SkillLine ID (Data tables > SkillLine).");
            long mask = 0;
            foreach (var part in SkillClasses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var names = WoWData.Core.Dbc.DbcLayouts.Default.NamedValues.GetValueOrDefault("Class");
                long id = long.TryParse(part, out var n) ? n : names?.Values.FirstOrDefault(v => string.Equals(v.Name, part, StringComparison.OrdinalIgnoreCase)).Value ?? 0;
                if (id is < 1 or > 32) throw new DesignException($"\"{part}\" is not a class.");
                mask |= 1L << (int)(id - 1);
            }
            var r = Designer.AddSkillLine(CurrentId, skill, mask);
            SetStatus(r.Message, !r.Ok);
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
        Refresh();
    }

    [RelayCommand]
    private void SyncServer()
    {
        if (Designer == null || CurrentId == 0) return;
        if (!Session!.HasServer)
        {
            SetStatus(Session.ServerNote, true);
            return;
        }
        Designer.Touch(CurrentId);
        SetStatus($"{Designer.Title(CurrentId)}: the server's spell_template follows the game's row when you save.");
    }

    // ------------------------------------------------------------------ change ID (0.13.3)

    /// <summary>The ID "Change ID" gives the open spell (empty: the first free one of the spell ID range).</summary>
    [ObservableProperty] private string _newId = "";
    /// <summary>The spells you made with IDs the server never casts (above 40000), in words; empty when there are none.</summary>
    [ObservableProperty] private string _unusableText = "";
    public bool HasUnusable => UnusableText.Length > 0;
    partial void OnUnusableTextChanged(string value) => OnPropertyChanged(nameof(HasUnusable));

    private void RefreshUnusable()
    {
        var list = Designer?.UnusableSpells() ?? new List<long>();
        UnusableText = list.Count == 0 ? "" : $"{list.Count} spell(s) you made have IDs above 40000, which the VMaNGOS server never casts (and the game cannot know above 65535): " +
            string.Join(", ", list.Take(8)) + (list.Count > 8 ? ", ..." : "") + ". \"Fix all spell IDs\" gives them free IDs of the spell ID range.";
    }

    [RelayCommand]
    private void ChangeId()
    {
        if (Designer == null || CurrentId == 0) return;
        long target;
        if (NewId.Trim().Length == 0)
        {
            try { target = Session!.FreeSpellId(); }
            catch (DesignException e) { SetStatus(e.Message, true); return; }
        }
        else if (!long.TryParse(NewId.Trim(), out target))
        {
            SetStatus("New ID: a whole number of at most 40000, or empty for the first free ID of the spell ID range.", true);
            return;
        }
        var r = Designer.ChangeId(CurrentId, target);
        SetStatus(r.Message, !r.Ok);
        if (!r.Ok) return;
        NewId = "";
        CurrentId = target;
        Refresh();
        RefreshUnusable();
        SetStatus(r.Message, false);
    }

    [RelayCommand]
    private void FixAllIds()
    {
        if (Designer == null) return;
        var r = Designer.ChangeIds();
        if (r.Ok && CurrentId != 0 && Designer.Moved.TryGetValue(CurrentId, out var moved)) CurrentId = moved;
        if (CurrentId != 0 && Designer.Exists(CurrentId)) Refresh();
        RefreshUnusable();
        SetStatus(r.Message, !r.Ok);
    }

    // ------------------------------------------------------------------ save, discard

    public bool IsDirty => Session?.IsDirty ?? false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Designer == null) return;
        try
        {
            var res = await _main.RunBusyAsync("Saving the spell into your workspace...", () => Task.Run(() => Designer.Save()));
            SetStatus(res.Message);
            _main.Toast(res.Message + " Next: \"Test in game\" or \"Build release patch\"" + (res.ServerTables.Count > 0 ? ", and \"Apply to server...\"." : "."));
            long keep = CurrentId;
            Reset();
            await EnsureLoadedAsync();
            if (keep > 0 && Designer!.Exists(keep)) await OpenAsync(keep);
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        long keep = CurrentId;
        Reset();
        await EnsureLoadedAsync();
        if (keep > 0 && Designer!.Exists(keep)) await OpenAsync(keep);
        else Title = "No spell open: find one on the left, or make a new one.";
        SetStatus("Your unsaved designer edits were thrown away.");
    }

    /// <summary>Open the rank 1 spell of a talent (the Classes and talents tab's link).</summary>
    public async Task ShowSpellAsync(long id)
    {
        _browser.SelectedTab = BrowserViewModel.SpellsTab;
        await OpenAsync(id);
    }
}
