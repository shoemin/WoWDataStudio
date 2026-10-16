using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Designers;
using WoWData.Core.Index;
using WoWData.Core.Session;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>One field of the quest editor: label, the text to edit, choices or checkboxes, help and what it shows.</summary>
public sealed partial class QuestFieldView : ObservableObject
{
    private readonly QuestDesignerViewModel _vm;
    private bool _loading;

    public QuestFieldView(QuestDesignerViewModel vm, QuestField f, string text, string shown, IReadOnlyList<(long Value, string Name)> choices, long value)
    {
        _vm = vm;
        F = f;
        _loading = true;
        _text = text;
        _original = text;
        Shown = shown;
        foreach (var (v, n) in choices) Choices.Add(new DbcChoice(v, $"{v} · {n}"));
        if (Choices.Count > 0)
        {
            if (Choices.All(c => c.Value != value)) Choices.Insert(0, new DbcChoice(value, $"{value} (not a known value)"));
            _choice = Choices.First(c => c.Value == value);
        }
        _loading = false;
    }

    public QuestField F { get; }
    public string Label => F.Label;
    public string Help => F.Help + $"\n({F.Column}; on the command line: {F.Id})";
    /// <summary>The value in words (the linked creature's name...), under the box.</summary>
    public string Shown { get; }
    public bool HasShown => Shown.Length > 0 && Shown != Text;
    public bool IsChoice => Choices.Count > 0;
    public bool IsText => !IsChoice;
    public bool IsLong => F.Kind == QuestFieldKind.Text;
    public List<DbcChoice> Choices { get; } = new();
    public ObservableCollection<QuestBitItem> Bits { get; } = new();
    public bool HasBits => Bits.Count > 0;
    private long _bitValue;

    internal void SetBits(IReadOnlyList<(long Bit, string Name)> bits, long value)
    {
        _loading = true;
        _bitValue = value;
        foreach (var (b, n) in bits) Bits.Add(new QuestBitItem(this, b, n, (value & b) == b));
        _loading = false;
    }

    internal void BitToggled()
    {
        if (_loading) return;
        long named = 0, on = 0;
        foreach (var b in Bits)
        {
            named |= b.Bit;
            if (b.IsOn) on |= b.Bit;
        }
        long v = (_bitValue & ~named) | on;
        _vm.SetField(this, v.ToString(CultureInfo.InvariantCulture));
    }

    [ObservableProperty] private string _text;
    [ObservableProperty] private DbcChoice? _choice;

    private readonly string _original;

    [RelayCommand]
    public void Commit() => _vm.SetField(this, Text);

    /// <summary>Commit when the text was changed (the box lost the focus).</summary>
    public void CommitIfChanged()
    {
        if (Text != _original) Commit();
    }

    partial void OnChoiceChanged(DbcChoice? value)
    {
        if (!_loading && value != null) _vm.SetField(this, value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>One checkbox of a class, race or flags field.</summary>
public sealed partial class QuestBitItem : ObservableObject
{
    private readonly QuestFieldView _field;

    public QuestBitItem(QuestFieldView field, long bit, string name, bool on)
    {
        _field = field;
        Bit = bit;
        Name = name;
        _isOn = on;
    }

    public long Bit { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _field.BitToggled();
}

public sealed class QuestFieldGroupView
{
    public string Name { get; init; } = "";
    public List<QuestFieldView> Fields { get; init; } = new();
    public bool IsExpanded { get; set; }
}

public sealed record QuestProblemRow(QuestProblem P)
{
    private static readonly System.Windows.Media.Brush Red = Freeze(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly System.Windows.Media.Brush Orange = Freeze(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly System.Windows.Media.Brush Grey = Freeze(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80));
    private static System.Windows.Media.Brush Freeze(System.Windows.Media.Color c) { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return b; }
    public string Text => P.Text;
    public bool IsError => P.Problem.IsError;
    public string Icon => P.Problem.Severity switch { DesignSeverity.Error => "", DesignSeverity.Warning => "", _ => "" };
    public System.Windows.Media.Brush Brush => P.Problem.Severity switch { DesignSeverity.Error => Red, DesignSeverity.Warning => Orange, _ => Grey };
}

public sealed record QuestRelationRow(QuestRelation R, bool Giver)
{
    public string Text => $"{(Giver ? "Gives" : "Takes back")}: {R.Text}";
}

/// <summary>
/// 0.13.0: the Quest chains tab: find a quest and open its chain as a flowchart (lanes per faction or class, columns in
/// order, links, choice groups, problem badges), edit the selected quest in plain fields (texts with the live $N / $C /
/// $R preview, levels, classes and races as checkboxes, chain links, objectives, rewards), its givers and enders (with
/// the model preview, "Show on map"), drop sources (linked to the loot editor), "speak to" and "explore" objectives, the
/// reward spell (linked to the Spell designer), build chains (after, before, branch, join, the other faction, the
/// discipline template), new quest-giver NPCs placed on the Maps tab, checks in plain words, and save / apply / test.
/// All the logic is WoWData.Core.Designers.QuestDesigner (wds quests uses the same). It shares the Spell designer's
/// session: reward spells made here are spells there, and one save writes everything.
/// </summary>
public sealed partial class QuestDesignerViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private readonly BrowserViewModel _browser;
    private readonly SpellDesignerViewModel _spells;

    public QuestDesignerViewModel(AppSession session, MainViewModel main, BrowserViewModel browser, SpellDesignerViewModel spells)
    {
        _session = session;
        _main = main;
        _browser = browser;
        _spells = spells;
        Viewer = new ModelViewerViewModel(session, browser.ModelCache);
        foreach (var f in new[] { "Alliance", "Horde" }) Factions.Add(f);
    }

    public QuestDesigner? Designer { get; private set; }
    public ModelViewerViewModel Viewer { get; }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _hasServer;
    [ObservableProperty] private string _serverText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    internal void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    public async Task EnsureLoadedAsync()
    {
        if (Designer != null && _spells.Session != null && ReferenceEquals(Designer.Session, _spells.Session)) return;
        await _spells.EnsureLoadedAsync();
        Designer = new QuestDesigner(_spells.Session!, _spells.Designer!);
        HasServer = Designer.HasServer;
        ServerText = HasServer
            ? $"Quests live in the server's world database ({_spells.Session!.Server!.Service.Database}). Edits go into your workspace; \"Apply to server...\" writes them."
            : QuestDesigner.NoServer + (_spells.Session!.ServerNote.Length > 0 ? " " + _spells.Session.ServerNote : "");
        IsLoaded = true;
        if (HasServer && Chain != null && Designer.Exists(Chain.Start)) RefreshChain();
        else if (CurrentId != 0 && !(HasServer && Designer.Exists(CurrentId))) CloseQuest();
    }

    private bool Ready()
    {
        if (Designer is { HasServer: true }) return true;
        SetStatus(QuestDesigner.NoServer, true);
        return false;
    }

    private void Do(DesignResult r)
    {
        SetStatus(r.Message + (r.Problems is { Count: > 0 } p ? "  " + string.Join("  ", p.Take(3).Select(x => x.Text)) : ""), !r.Ok);
    }

    private void Guard(Action a)
    {
        if (!Ready()) return;
        try { a(); }
        catch (DesignException e) { SetStatus(e.Message, true); }
        catch (WoWData.Core.ServerData.ServerDataException e) { SetStatus(e.Message, true); }
    }

    // ------------------------------------------------------------------ which sections are open (kept while you work)

    [ObservableProperty] private bool _openAdd = true;
    [ObservableProperty] private bool _openBranch;
    [ObservableProperty] private bool _openFaction;
    [ObservableProperty] private bool _openTemplate;
    [ObservableProperty] private bool _openNpc;
    [ObservableProperty] private bool _openCategories;
    [ObservableProperty] private bool _showGivers = true;
    /// <summary>The panel under the flowchart: 0 the chain's checks, 1 the text preview.</summary>
    [ObservableProperty] private int _bottomTab;
    [ObservableProperty] private bool _showObjectives = true;
    [ObservableProperty] private bool _showDrops;
    [ObservableProperty] private bool _showTalk;
    [ObservableProperty] private bool _showExplore;
    [ObservableProperty] private bool _showReward;
    [ObservableProperty] private bool _showChecks = true;

    /// <summary>For the screenshots and the manual: open only these sections of the editor (and field groups by name).</summary>
    public void ExpandOnly(params string[] sections)
    {
        bool S(string n) => sections.Contains(n, StringComparer.Ordinal);
        ShowGivers = S("givers"); BottomTab = S("preview") ? 1 : 0; ShowObjectives = S("objective-words"); ShowDrops = S("drops");
        ShowTalk = S("talk"); ShowExplore = S("explore"); ShowReward = S("reward"); ShowChecks = S("checks");
        foreach (var g in Groups) g.IsExpanded = S(g.Name);   // field groups by their names: "Texts", "Objectives"...
        if (CurrentId != 0) OpenQuest(CurrentId);
    }

    /// <summary>For the screenshots: open only one section of the left column.</summary>
    public void OpenLeft(string section)
    {
        OpenAdd = section == "add"; OpenBranch = section == "branch"; OpenFaction = section == "faction";
        OpenTemplate = section == "template"; OpenNpc = section == "npc"; OpenCategories = section == "categories";
    }

    // ------------------------------------------------------------------ find

    [ObservableProperty] private string _searchText = "";
    public ObservableCollection<QuestSummary> Results { get; } = new();
    [ObservableProperty] private QuestSummary? _selectedResult;

    [RelayCommand]
    public async Task SearchAsync()
    {
        await EnsureLoadedAsync();
        if (!Ready()) return;
        var d = Designer!;
        var text = SearchText;
        var hits = await Task.Run(() => d.Find(text, 200));
        Results.Clear();
        foreach (var h in hits) Results.Add(h);
        SelectedResult = Results.FirstOrDefault();
        SetStatus(hits.Count == 0 ? $"No quest matches \"{text}\"." : $"{hits.Count} quest(s). Select one and \"Open chain\".");
    }

    [RelayCommand]
    public async Task OpenChainAsync()
    {
        if (SelectedResult == null) { SetStatus("Find a quest first, select it, then \"Open chain\".", true); return; }
        await OpenAsync(SelectedResult.Id);
    }

    /// <summary>Open a quest's chain and the quest in the editor.</summary>
    public async Task OpenAsync(long id)
    {
        await EnsureLoadedAsync();
        if (!Ready()) return;
        var d = Designer!;
        if (!d.Exists(id)) { SetStatus($"There is no quest {id}.", true); return; }
        var chain = await _main.RunBusyAsync("Laying out the quest chain...", () => Task.Run(() => QuestChains.Open(d, id)));
        Chain = chain;
        FillProblems(QuestRules.Check(d, chain.Nodes.Select(n => n.Id).ToList()));
        UpdateChainText();
        OpenQuest(id);
        SetStatus($"Opened the chain of {d.Label(id)}: click a quest in the flowchart to edit it.");
    }

    // ------------------------------------------------------------------ the chain

    [ObservableProperty] private QuestChain? _chain;
    [ObservableProperty] private string _chainText = "No chain open.";
    [ObservableProperty] private long _selectedQuest;
    public ObservableCollection<QuestProblemRow> ChainProblems { get; } = new();
    [ObservableProperty] private QuestProblemRow? _selectedProblem;

    [ObservableProperty] private string _chainChecksText = "";

    /// <summary>The chain's problems, errors first.</summary>
    private void FillProblems(IEnumerable<QuestProblem> problems)
    {
        ChainProblems.Clear();
        foreach (var p in problems.OrderBy(p => p.Problem.Severity).ThenBy(p => p.Quest)) ChainProblems.Add(new QuestProblemRow(p));
        int e = ChainProblems.Count(p => p.P.Problem.Severity == DesignSeverity.Error), w = ChainProblems.Count(p => p.P.Problem.Severity == DesignSeverity.Warning);
        ChainChecksText = ChainProblems.Count == 0 ? "No problems: every quest of the chain can be taken, done and handed in." :
            e > 0 ? $"{e} error(s): the chain cannot be played as it is. {w} warning(s) and {ChainProblems.Count - e - w} note(s) below them." : $"No errors. {w} warning(s) and {ChainProblems.Count - w} note(s): probably fine, worth a look.";
    }

    private void UpdateChainText()
    {
        var c = Chain;
        if (c == null) { ChainText = "No chain open."; return; }
        int e = ChainProblems.Count(p => p.P.Problem.Severity == DesignSeverity.Error), w = ChainProblems.Count(p => p.P.Problem.Severity == DesignSeverity.Warning);
        ChainText = $"Chain of {Designer!.Label(c.Start)}: {c.Nodes.Count} quest(s) in {c.Lanes.Count} lane(s), {c.Columns} step(s). Checks: {e} error(s), {w} warning(s)." + (c.Truncated ? " (Only the first quests are shown.)" : "");
    }

    /// <summary>Lay the chain out again (after an edit), keeping the selection.</summary>
    public void RefreshChain()
    {
        var d = Designer;
        if (d == null || !d.HasServer) return;
        long start = Chain?.Start ?? CurrentId;
        if (start == 0 || !d.Exists(start)) start = CurrentId;
        if (start == 0 || !d.Exists(start)) { Chain = null; FillProblems(Array.Empty<QuestProblem>()); UpdateChainText(); return; }
        // the chain of the open quest (it may have joined another chain)
        if (CurrentId != 0 && d.Exists(CurrentId) && !QuestChains.Members(d, start).Contains(CurrentId)) start = CurrentId;
        Chain = QuestChains.Open(d, start);
        FillProblems(QuestRules.Check(d, Chain.Nodes.Select(n => n.Id).ToList()));
        UpdateChainText();
        SelectedQuest = CurrentId;
    }

    partial void OnSelectedQuestChanged(long value)
    {
        if (value != 0 && value != CurrentId && Designer?.Exists(value) == true) OpenQuest(value);
    }

    partial void OnSelectedProblemChanged(QuestProblemRow? value)
    {
        if (value != null && Designer?.Exists(value.P.Quest) == true && value.P.Quest != CurrentId) OpenQuest(value.P.Quest);
    }

    [RelayCommand]
    private void CheckChain()
    {
        if (!Ready() || Chain == null) { SetStatus("Open a chain first.", true); return; }
        RefreshChain();
        SetStatus(ChainText);
    }

    // ------------------------------------------------------------------ the quest editor

    [ObservableProperty] private long _currentId;
    [ObservableProperty] private string _title = "No quest open: find one on the left and open its chain, or make a new one.";
    [ObservableProperty] private bool _hasQuest;
    public ObservableCollection<QuestFieldGroupView> Groups { get; } = new();
    public ObservableCollection<QuestRelationRow> Relations { get; } = new();
    [ObservableProperty] private QuestRelationRow? _selectedRelation;
    public ObservableCollection<string> Objectives { get; } = new();
    public ObservableCollection<QuestProblemRow> QuestProblems { get; } = new();

    private void CloseQuest()
    {
        CurrentId = 0;
        HasQuest = false;
        Groups.Clear();
        Relations.Clear();
        Objectives.Clear();
        QuestProblems.Clear();
        Title = "No quest open: find one on the left and open its chain, or make a new one.";
    }

    /// <summary>Show a quest in the editor (the chain stays).</summary>
    public void OpenQuest(long id)
    {
        var d = Designer;
        if (d == null || !d.Exists(id)) return;
        var expanded = Groups.Where(g => g.IsExpanded).Select(g => g.Name).ToHashSet();
        if (Groups.Count == 0) expanded.Add("Texts");
        CurrentId = id;
        HasQuest = true;
        SelectedQuest = id;
        var s = d.Summary(id);
        Title = $"{s.Title} [{id}]";
        Groups.Clear();
        foreach (var g in QuestForm.Groups)
        {
            var fields = new List<QuestFieldView>();
            foreach (var f in QuestForm.Fields.Where(f => f.Group == g && d.Quests.Has(f.Column) && SlotVisible(d, id, f)))
            {
                long raw = d.Raw(id, f);
                var shown = f.Kind is QuestFieldKind.Link or QuestFieldKind.Target or QuestFieldKind.Category or QuestFieldKind.Money ? d.Show(id, f) : "";
                var fv = new QuestFieldView(this, f, d.EditValue(id, f), shown, d.Choices(f), raw);
                var bits = d.Bits(f);
                if (bits.Count > 0) fv.SetBits(bits, raw);
                fields.Add(fv);
            }
            Groups.Add(new QuestFieldGroupView { Name = g, Fields = fields, IsExpanded = expanded.Contains(g) });
        }
        RefreshSide();
        UpdatePreview();
    }

    private static readonly System.Text.RegularExpressions.Regex Slot = new(@"^(target|item|source-item|choice|reward-item|rep|objective-text)(\d)(-.*)?$");

    /// <summary>Numbered slots (kill 1-4, items 1-4, choices 1-6...): the used ones and the first empty one are shown.</summary>
    private static bool SlotVisible(QuestDesigner d, long id, QuestField f)
    {
        var m = Slot.Match(f.Id);
        if (!m.Success) return true;
        string family = m.Groups[1].Value;
        int k = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        bool Used(int n) => QuestForm.Find(family + n) is { } bf && (bf.IsText ? d.EditValue(id, bf).Length > 0 : d.Raw(id, bf) != 0);
        if (Used(k)) return true;
        for (int n = 1; n < k; n++) if (!Used(n)) return false;
        return true;
    }

    private void RefreshSide()
    {
        var d = Designer!;
        long id = CurrentId;
        Relations.Clear();
        foreach (var g in d.Givers(id)) Relations.Add(new QuestRelationRow(g, true));
        foreach (var g in d.Enders(id)) Relations.Add(new QuestRelationRow(g, false));
        Objectives.Clear();
        foreach (var o in d.Objectives(id)) Objectives.Add(o.Text);
        if (Objectives.Count == 0) Objectives.Add("No objective: the player only has to talk to the quest giver and the ender.");
        QuestProblems.Clear();
        foreach (var p in QuestRules.Check(d, new[] { id }).OrderBy(p => p.Problem.Severity)) QuestProblems.Add(new QuestProblemRow(p));
        if (QuestProblems.Count == 0) QuestProblems.Add(new QuestProblemRow(new QuestProblem(id, "", new DesignProblem(DesignSeverity.Note, "", "No problems found."))));
        var row = d.Require(id);
        RewardSpellText = d.Q(row, "RewSpell") is var sp and not 0 ? sp.ToString(CultureInfo.InvariantCulture) : "";
        if (DropItemText.Length == 0 && d.Q(row, "ReqItemId1") is var it and not 0) DropItemText = it.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A field's new value from the form (checked; refused values change nothing).</summary>
    public void SetField(QuestFieldView field, string text)
    {
        if (!Ready() || CurrentId == 0) return;
        var r = Designer!.Set(CurrentId, field.F.Id, text);
        Do(r);
        long id = CurrentId;
        OpenQuest(id);
        if (field.F.Group == "Chain" || field.F.Id is "level" or "min-level" or "races" or "classes" or "title") RefreshChain();
        else UpdateChainNode();
    }

    private void UpdateChainNode()
    {
        if (Chain == null || Designer == null) return;
        RefreshChain();
    }

    // ------------------------------------------------------------------ text preview

    public ObservableCollection<string> PreviewRaces { get; } = new();
    public ObservableCollection<string> PreviewClasses { get; } = new();
    [ObservableProperty] private string _previewName = "Thrall";
    [ObservableProperty] private string _previewRace = "Orc";
    [ObservableProperty] private string _previewClass = "Warrior";
    [ObservableProperty] private bool _previewFemale;
    [ObservableProperty] private string _previewText = "";

    partial void OnPreviewNameChanged(string value) => UpdatePreview();
    partial void OnPreviewRaceChanged(string value) => UpdatePreview();
    partial void OnPreviewClassChanged(string value) => UpdatePreview();
    partial void OnPreviewFemaleChanged(bool value) => UpdatePreview();

    private void UpdatePreview()
    {
        var d = Designer;
        if (d == null || CurrentId == 0 || !d.Exists(CurrentId)) { PreviewText = ""; return; }
        if (PreviewRaces.Count == 0)
        {
            foreach (var r in d.Names.Races) PreviewRaces.Add(r);
            foreach (var c in d.Names.Classes) PreviewClasses.Add(c);
        }
        var p = new QuestTextPlayer(PreviewName, PreviewRace, PreviewClass, PreviewFemale);
        var parts = d.Preview(CurrentId, p).Where(x => x.Preview.Text.Length > 0)
            .Select(x => $"{x.Field.Label}:\n{x.Preview.Text}" + (x.Preview.UnknownTokens.Count > 0 ? $"\n(The game does not know {string.Join(", ", x.Preview.UnknownTokens)}.)" : ""));
        PreviewText = string.Join("\n\n", parts);
    }

    // ------------------------------------------------------------------ givers and enders, NPC preview, map

    [ObservableProperty] private string _relationText = "";

    // the NPC picker: creatures and objects by name, with their spawns
    [ObservableProperty] private string _npcSearchText = "";
    public ObservableCollection<QuestRelation> NpcResults { get; } = new();
    [ObservableProperty] private QuestRelation? _selectedNpcResult;
    [ObservableProperty] private bool _hasNpcResults;

    [RelayCommand]
    private void FindNpc()
    {
        Guard(() =>
        {
            NpcResults.Clear();
            foreach (var r in Designer!.FindNpcs(NpcSearchText)) NpcResults.Add(r);
            HasNpcResults = NpcResults.Count > 0;
            SetStatus(NpcResults.Count == 0 ? $"No creature or object matches \"{NpcSearchText}\"." : $"{NpcResults.Count} found: select one, then \"Add giver\" or \"Add ender\".");
        });
    }

    partial void OnSelectedNpcResultChanged(QuestRelation? value)
    {
        if (value != null) RelationText = value.Key;
    }

    [RelayCommand]
    private void AddGiver() => Relation(true, true);

    [RelayCommand]
    private void AddEnder() => Relation(false, true);

    private void Relation(bool giver, bool add)
    {
        Guard(() =>
        {
            if (CurrentId == 0) throw new DesignException("Open a quest first.");
            if (RelationText.Trim().Length == 0) throw new DesignException("Type the NPC: a creature ID (234), creature:234 or object:1618.");
            Do(Designer!.SetRelation(CurrentId, RelationText, giver, add));
            OpenQuest(CurrentId);
            RefreshChain();
        });
    }

    [RelayCommand]
    private void RemoveRelation()
    {
        var sel = SelectedRelation;
        if (sel == null) { SetStatus("Select a giver or ender in the list first.", true); return; }
        Guard(() =>
        {
            Do(Designer!.SetRelation(CurrentId, sel.R.Key, sel.Giver, false));
            OpenQuest(CurrentId);
            RefreshChain();
        });
    }

    partial void OnSelectedRelationChanged(QuestRelationRow? value)
    {
        if (value != null && value.R.Kind is "creature" or "object") RelationText = value.R.Key;
    }

    /// <summary>"Preview the NPC": its model in the small 3D view.</summary>
    [RelayCommand]
    private async Task PreviewNpcAsync()
    {
        var d = Designer;
        if (d == null || !Ready()) return;
        string text = SelectedRelation?.R.Key ?? RelationText;
        try
        {
            var (kind, id) = QuestDesigner.ParseWho(text);
            if (kind != "creature") { SetStatus("The preview shows creatures (an object's model: the Browse tab).", true); return; }
            var ct = d.T("creature_template");
            var row = d.Ed.Object(ct, id) ?? throw new DesignException($"There is no creature {id}.");
            long display = Enumerable.Range(1, 4).Select(k => QuestDesigner.L(ct, row, "display_id" + k)).FirstOrDefault(x => x != 0);
            if (display == 0 || _session.Index == null) { SetStatus($"{d.CreatureName(id)} has no display to show.", true); return; }
            var entry = new IndexEntry { Kind = AssetKind.Creature, Key = $"cdisplay:{display}", Name = d.CreatureName(id), Id = display };
            var index = _session.Index;
            var catalog = _session.Catalog;
            var details = await Task.Run(() => index.Details(entry, catalog));
            details.Title = $"{d.CreatureName(id)} (creature {id}, display {display})";
            await Viewer.LoadAsync(entry, details);
            HasNpcPreview = true;
            SetStatus($"{d.CreatureName(id)}: display {display}.");
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [ObservableProperty] private bool _hasNpcPreview;

    /// <summary>"Show on map": the givers and enders of the quest (or the selected NPC) on the Maps tab.</summary>
    [RelayCommand]
    private async Task ShowOnMapAsync()
    {
        var d = Designer;
        if (d == null || !Ready() || CurrentId == 0) return;
        var spawns = new List<(QuestSpawn S, string Name)>();
        IEnumerable<QuestRelation> rels = SelectedRelation != null ? new[] { SelectedRelation.R } : d.Givers(CurrentId).Concat(d.Enders(CurrentId));
        foreach (var r in rels.Where(r => r.Kind is "creature" or "object").DistinctBy(r => r.Key))
            foreach (var sp in d.Spawns(r.Kind, r.Id, 5)) spawns.Add((sp, r.Name));
        if (spawns.Count == 0) { SetStatus("None of them is spawned anywhere: place the NPC first (New quest giver, or \"Place here\").", true); return; }
        var map = spawns[0].S.Map;
        var pins = spawns.Where(x => x.S.Map == map).Select(x => (x.S.X, x.S.Y, x.Name, x.S.Guid >= _session.Settings.ServerCustomIdStart)).ToList();
        _browser.SelectedTab = BrowserViewModel.MapsTab;
        await _browser.Maps.ShowPinsAsync(map, pins, $"{pins.Count} spawn(s) of the quest's NPCs on map {map}: " + string.Join(", ", pins.Select(p => p.Item3).Distinct().Take(4)) + ".");
    }

    // ------------------------------------------------------------------ objectives: drop sources, speak to, explore

    [ObservableProperty] private string _dropItemText = "";
    public ObservableCollection<QuestDropSource> DropSources { get; } = new();
    [ObservableProperty] private QuestDropSource? _selectedDropSource;
    [ObservableProperty] private string _dropCreatureText = "";
    [ObservableProperty] private string _dropChanceText = "35";
    [ObservableProperty] private bool _dropQuestOnly = true;

    [RelayCommand]
    private void ShowDropSources()
    {
        Guard(() =>
        {
            if (!long.TryParse(DropItemText.Trim(), out var item)) throw new DesignException("Type the item's ID.");
            DropSources.Clear();
            foreach (var s in Designer!.DropSources(item)) DropSources.Add(s);
            SetStatus(DropSources.Count == 0 ? $"Nothing gives {Designer.ItemName(item)} yet: add a drop source." : $"{DropSources.Count} source(s) of {Designer.ItemName(item)}.");
        });
    }

    [RelayCommand]
    private void AddDropSource()
    {
        Guard(() =>
        {
            if (!long.TryParse(DropItemText.Trim(), out var item)) throw new DesignException("Type the item's ID.");
            if (!long.TryParse(DropCreatureText.Trim(), out var creature)) throw new DesignException("Type the creature that should drop it (its ID).");
            if (!double.TryParse(DropChanceText.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var chance)) throw new DesignException("The chance is a percentage (35).");
            Do(Designer!.AddDropSource(item, creature, chance, DropQuestOnly));
            ShowDropSources();
            if (CurrentId != 0) OpenQuest(CurrentId);
            RefreshChain();
        });
    }

    /// <summary>"Open in the loot editor": the Server data tab with the loot rows of the selected source.</summary>
    [RelayCommand]
    private async Task OpenLootEditorAsync()
    {
        var s = SelectedDropSource;
        if (s == null || s.LootEntry == 0 || !s.Table.EndsWith("_loot_template", StringComparison.Ordinal)) { SetStatus("Select a loot source (a creature, chest or container) in the list first.", true); return; }
        if (Designer?.Session.IsDirty == true && !await SaveFirstAsync("open the loot editor")) return;
        await _browser.ShowServerTableAsync(s.Table, s.LootEntry);
    }

    [ObservableProperty] private string _talkNpcText = "";
    [ObservableProperty] private string _talkOptionText = "I am ready.";
    [ObservableProperty] private string _talkSlotText = "1";

    [RelayCommand]
    private void AddTalk()
    {
        Guard(() =>
        {
            if (CurrentId == 0) throw new DesignException("Open a quest first.");
            if (!long.TryParse(TalkNpcText.Trim(), out var npc)) throw new DesignException("Type the NPC to speak with (its creature ID).");
            Do(Designer!.AddTalkObjective(CurrentId, npc, int.TryParse(TalkSlotText, out var sl) ? sl : 1, TalkOptionText));
            OpenQuest(CurrentId);
            RefreshChain();
        });
    }

    [ObservableProperty] private string _exploreTriggerText = "";
    [ObservableProperty] private string _exploreMapText = "";
    [ObservableProperty] private string _exploreXText = "";
    [ObservableProperty] private string _exploreYText = "";
    [ObservableProperty] private string _exploreRadiusText = "15";

    [RelayCommand]
    private void AddExplore()
    {
        Guard(() =>
        {
            if (CurrentId == 0) throw new DesignException("Open a quest first.");
            long trigger;
            if (long.TryParse(ExploreTriggerText.Trim(), out var t)) trigger = t;
            else
            {
                var (map, x, y) = ParsePosition(ExploreMapText, ExploreXText, ExploreYText);
                double z = Designer!.GroundHeight(map, x, y) ?? throw new DesignException("The ground height is not known there (no terrain).");
                trigger = Designer.NewAreaTrigger(map, x, y, z, double.TryParse(ExploreRadiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rad) ? rad : 15);
                ExploreTriggerText = trigger.ToString(CultureInfo.InvariantCulture);
            }
            Do(Designer!.AddExplore(CurrentId, trigger));
            OpenQuest(CurrentId);
            RefreshChain();
        });
    }

    [RelayCommand]
    private async Task PickExploreOnMapAsync()
    {
        _browser.SelectedTab = BrowserViewModel.MapsTab;
        await _browser.Maps.BeginPickAsync("the exploration point", (m, x, y) =>
        {
            ExploreMapText = m.ToString(CultureInfo.InvariantCulture);
            ExploreXText = x.ToString("0.#", CultureInfo.InvariantCulture);
            ExploreYText = y.ToString("0.#", CultureInfo.InvariantCulture);
            ExploreTriggerText = "";
            _browser.SelectedTab = BrowserViewModel.QuestsTab;
            SetStatus($"Picked map {m} at {x:0.#}, {y:0.#}: \"Add explore objective\" makes the area trigger there.");
        }, long.TryParse(ExploreMapText, out var mm) ? mm : null);
    }

    private static (long Map, double X, double Y) ParsePosition(string map, string x, string y)
    {
        if (!long.TryParse(map.Trim(), out var m)) throw new DesignException("Type the map (0 Eastern Kingdoms, 1 Kalimdor) or pick the place on the map.");
        if (!double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var xx) || !double.TryParse(y.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var yy))
            throw new DesignException("Type the position (x and y) or pick it on the map.");
        return (m, xx, yy);
    }

    // ------------------------------------------------------------------ reward spell

    [ObservableProperty] private string _rewardSpellText = "";
    [ObservableProperty] private bool _rewardLearn = true;

    [RelayCommand]
    private void SetRewardSpell()
    {
        Guard(() =>
        {
            if (CurrentId == 0) throw new DesignException("Open a quest first.");
            long spell = RewardSpellText.Trim().Length == 0 ? 0 : long.TryParse(RewardSpellText.Trim(), out var s) ? s : throw new DesignException("Type the spell's ID.");
            Do(Designer!.SetRewardSpell(CurrentId, spell, RewardLearn));
            OpenQuest(CurrentId);
            RefreshChain();
        });
    }

    [RelayCommand]
    private async Task OpenSpellDesignerAsync()
    {
        if (!long.TryParse(RewardSpellText.Trim(), out var spell) || spell == 0) { SetStatus("Type or set the reward spell first.", true); return; }
        await _spells.ShowSpellAsync(spell);
    }

    // ------------------------------------------------------------------ build the chain

    [ObservableProperty] private string _newTitle = "";

    [RelayCommand]
    private void NewQuest() => Build(d => d.New(NewTitle, like: CurrentId != 0 ? CurrentId : null), "New quest");

    [RelayCommand]
    private void AddAfter() => Build(d => d.AddAfter(RequireOpen(), NewTitle), "Added after");

    [RelayCommand]
    private void AddBefore() => Build(d => d.AddBefore(RequireOpen(), NewTitle), "Added before");

    private long RequireOpen() => CurrentId != 0 ? CurrentId : throw new DesignException("Open (select) the quest to add it next to first.");

    private void Build(Func<QuestDesigner, long> f, string what)
    {
        Guard(() =>
        {
            if (NewTitle.Trim().Length == 0) throw new DesignException("Type the new quest's title first.");
            long id = f(Designer!);
            SetStatus($"{what}: {Designer!.Label(id)}. Set its texts, objectives and rewards on the right.");
            OpenQuest(id);
            RefreshChain();
        });
    }

    [ObservableProperty] private string _branchTitles = "";
    [ObservableProperty] private bool _branchChoice = true;
    [ObservableProperty] private string _branchRewards = "";

    [RelayCommand]
    private void AddBranch()
    {
        Guard(() =>
        {
            long from = RequireOpen();
            var titles = BranchTitles.Split(new[] { '\n', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var rewards = BranchRewards.Trim().Length == 0 ? null : BranchRewards.Split(',', StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, out var v) ? v : throw new DesignException($"\"{x}\" is not an item ID.")).ToList();
            var ids = Designer!.Branch(from, titles, BranchChoice, rewards);
            SetStatus($"{(BranchChoice ? $"A choice (exclusive group {ids[0]})" : "Parallel follow-ups")} after {Designer.Label(from)}: {string.Join(", ", ids.Select(Designer.Label))}.");
            RefreshChain();
        });
    }

    [ObservableProperty] private string _joinIds = "";
    [ObservableProperty] private string _joinTitle = "";
    [ObservableProperty] private bool _joinAll;

    [RelayCommand]
    private void Join()
    {
        Guard(() =>
        {
            var ids = JoinIds.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var v) ? v : throw new DesignException($"\"{x}\" is not a quest ID.")).ToList();
            long m = Designer!.Join(ids, JoinTitle, JoinAll);
            SetStatus($"New quest {Designer.Label(m)} after {(JoinAll ? "all" : "any one")} of {string.Join(", ", ids)}.");
            OpenQuest(m);
            RefreshChain();
        });
    }

    public ObservableCollection<string> Factions { get; } = new();
    [ObservableProperty] private string _duplicateFaction = "Horde";
    [ObservableProperty] private string _duplicateSwaps = "";
    [ObservableProperty] private string _duplicateSuffix = "";

    [RelayCommand]
    private void DuplicateChain()
    {
        Guard(() =>
        {
            long from = RequireOpen();
            var swaps = new QuestSwaps();
            foreach (var line in DuplicateSwaps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) swaps.Add(line);
            var ids = QuestChains.Members(Designer!, from);
            var res = Designer!.DuplicateForFaction(ids, DuplicateFaction, swaps, DuplicateSuffix.Length > 0 ? DuplicateSuffix : null);
            SetStatus(res.Message + (res.Notes.Count > 0 ? " " + string.Join(" ", res.Notes.Take(3)) : ""));
            var first = res.Map!.TryGetValue(from, out var nf) ? nf : res.Quests[0];
            OpenQuest(first);
            RefreshChain();
        });
    }

    // the discipline template
    [ObservableProperty] private string _tplName = "";
    [ObservableProperty] private string _tplFaction = "Alliance";
    [ObservableProperty] private string _tplLevel = "20";
    [ObservableProperty] private string _tplGiver = "";
    [ObservableProperty] private string _tplIntro = "";
    [ObservableProperty] private string _tplOutpost = "";
    [ObservableProperty] private string _tplKill = "";
    [ObservableProperty] private string _tplCount = "8";
    [ObservableProperty] private string _tplSpell = "";
    [ObservableProperty] private string _tplCloneSpell = "12295";
    [ObservableProperty] private string _tplXp = "1450";

    [RelayCommand]
    private void MakeTemplate()
    {
        Guard(() =>
        {
            var o = new DisciplineOptions
            {
                Discipline = TplName, Faction = TplFaction, Level = long.TryParse(TplLevel, out var lv) ? lv : 20, Giver = TplGiver, IntroGiver = TplIntro.Trim().Length > 0 ? TplIntro : null,
                Outpost = TplOutpost, KillTarget = TplKill, KillCount = long.TryParse(TplCount, out var kc) ? kc : 8, Xp = long.TryParse(TplXp, out var xp) ? xp : 0,
                RewardSpell = long.TryParse(TplSpell, out var sp) ? sp : 0, SpellTemplate = long.TryParse(TplCloneSpell, out var cs) ? cs : 0,
            };
            var res = Designer!.Discipline(o);
            SetStatus(res.Message + (res.Notes.Count > 0 ? " " + string.Join(" ", res.Notes) : ""));
            OpenQuest(res.Quests[0]);
            RefreshChain();
        });
    }

    // new quest-giver NPCs
    [ObservableProperty] private string _npcName = "";
    [ObservableProperty] private string _npcSubname = "";
    [ObservableProperty] private string _npcCopyFrom = "";
    [ObservableProperty] private string _npcLevel = "";
    [ObservableProperty] private string _npcMap = "";
    [ObservableProperty] private string _npcX = "";
    [ObservableProperty] private string _npcY = "";
    [ObservableProperty] private string _npcMade = "";

    [RelayCommand]
    private async Task PickNpcOnMapAsync()
    {
        _browser.SelectedTab = BrowserViewModel.MapsTab;
        await _browser.Maps.BeginPickAsync(NpcName.Length > 0 ? NpcName : "the quest giver", (m, x, y) =>
        {
            NpcMap = m.ToString(CultureInfo.InvariantCulture);
            NpcX = x.ToString("0.#", CultureInfo.InvariantCulture);
            NpcY = y.ToString("0.#", CultureInfo.InvariantCulture);
            _browser.SelectedTab = BrowserViewModel.QuestsTab;
            SetStatus($"Picked map {m} at {x:0.#}, {y:0.#}.");
        }, long.TryParse(NpcMap, out var mm) ? mm : null);
    }

    [RelayCommand]
    private void MakeNpc()
    {
        Guard(() =>
        {
            if (!long.TryParse(NpcCopyFrom.Trim(), out var from)) throw new DesignException("Type the creature it should look like (\"Look like\", a creature ID).");
            long? map = null; double? x = null, y = null;
            if (NpcMap.Trim().Length > 0) { var p = ParsePosition(NpcMap, NpcX, NpcY); map = p.Map; x = p.X; y = p.Y; }
            var (entry, _, msg) = Designer!.NewNpc(NpcName, from, NpcSubname.Length > 0 ? NpcSubname : null, long.TryParse(NpcLevel, out var lv) ? lv : null, null, map, x, y);
            NpcMade = entry.ToString(CultureInfo.InvariantCulture);
            RelationText = $"creature:{entry}";
            SetStatus(msg + " \"Add giver\" / \"Add ender\" uses it now.");
        });
    }

    [RelayCommand]
    private void PlaceNpc()
    {
        Guard(() =>
        {
            var (kind, id) = QuestDesigner.ParseWho(RelationText);
            if (kind != "creature") throw new DesignException("Only creatures are placed here.");
            var (m, x, y) = ParsePosition(NpcMap, NpcX, NpcY);
            var (_, msg) = Designer!.PlaceNpc(id, m, x, y);
            SetStatus(msg);
            if (CurrentId != 0) OpenQuest(CurrentId);
        });
    }

    [ObservableProperty] private string _categoryName = "";

    [RelayCommand]
    private void NewCategory()
    {
        Guard(() =>
        {
            long id = Designer!.NewCategory(CategoryName);
            SetStatus($"New quest log category {id}: {CategoryName} (use it as \"sort:{CategoryName}\" in Quest log category).");
        });
    }

    [RelayCommand]
    private void DeleteQuest()
    {
        if (!Ready() || CurrentId == 0) return;
        long id = CurrentId;
        if (!Dialogs.Confirm("Delete quest", $"Delete {Designer!.Label(id)} with its givers, enders and links? The quests around it are linked to each other. (Unsaved until \"Save to my workspace\"; \"Discard changes\" brings it back.)", "Delete quest")) return;
        Guard(() =>
        {
            Do(Designer.Delete(id));
            CloseQuest();
            if (Chain != null && Designer.Exists(Chain.Start)) RefreshChain(); else { Chain = null; FillProblems(Array.Empty<QuestProblem>()); UpdateChainText(); }
        });
    }

    // ------------------------------------------------------------------ files

    [RelayCommand]
    private void ExportChain()
    {
        if (!Ready() || CurrentId == 0) { SetStatus("Open a quest first.", true); return; }
        var file = Dialogs.PickSaveFile("Export the chain", "Quest file (*.json)|*.json|CSV (*.csv)|*.csv", $"quests_{CurrentId}.json");
        if (file == null) return;
        Guard(() =>
        {
            var ids = QuestChains.Members(Designer!, CurrentId);
            QuestExchange.Export(Designer!, ids, file);
            SetStatus($"Wrote {ids.Count} quest(s) to {file}.");
        });
    }

    [RelayCommand]
    private void ImportQuests()
    {
        if (!Ready()) return;
        var file = Dialogs.PickOpenFile("Import quests", "Quest files (*.json;*.csv)|*.json;*.csv");
        if (file == null) return;
        Guard(() =>
        {
            var recs = QuestExchange.Read(file);
            var msgs = QuestExchange.Import(Designer!, recs);
            SetStatus(msgs.Count == 0 ? "Nothing to change: the quests are already as in the file." : string.Join(" ", msgs.Take(4)) + " Save to keep them.");
            if (recs.Count > 0 && Designer!.Exists(recs[0].Id)) { OpenQuest(recs[0].Id); RefreshChain(); }
        });
    }

    // ------------------------------------------------------------------ save, discard, apply, test

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!Ready()) return;
        try
        {
            var d = Designer!;
            var res = await _main.RunBusyAsync("Saving the quests into your workspace...", () => Task.Run(() => d.Save()));
            SetStatus(res.Message);
            _main.Toast(res.Message);
            long keep = CurrentId;
            long start = Chain?.Start ?? 0;
            _spells.Reset();
            Designer = null;
            await EnsureLoadedAsync();
            if (start != 0 && Designer!.Exists(start)) await OpenAsync(start);
            if (keep != 0 && Designer!.Exists(keep)) OpenQuest(keep);
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
        catch (WoWData.Core.ServerData.ServerDataException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        long keep = CurrentId;
        long start = Chain?.Start ?? 0;
        _spells.Reset();
        Designer = null;
        await EnsureLoadedAsync();
        if (start != 0 && Designer!.HasServer && Designer.Exists(start)) await OpenAsync(start); else { Chain = null; FillProblems(Array.Empty<QuestProblem>()); UpdateChainText(); }
        if (keep != 0 && Designer!.HasServer && Designer.Exists(keep)) OpenQuest(keep); else CloseQuest();
        SetStatus("Your unsaved designer edits were thrown away.");
    }

    private async Task<bool> SaveFirstAsync(string what)
    {
        if (!Dialogs.Confirm("Save first", $"The quest designer has unsaved edits. Save them into your workspace before you {what}?", "Save to my workspace")) return false;
        await SaveAsync();
        return !StatusIsError;
    }

    /// <summary>"Apply to server...": the Server data tab's apply (backed up, logged, undoable) of the saved edits.</summary>
    [RelayCommand]
    private async Task ApplyToServerAsync()
    {
        if (!Ready()) return;
        if (Designer!.Session.IsDirty && !await SaveFirstAsync("apply to the server")) return;
        await _browser.Server.EnsureLoadedAsync();
        _browser.Server.ApplyToServerCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearWdb() => _browser.Server.ClearWdbCommand.Execute(null);

    /// <summary>"Test in game": how to try the quest (GM commands).</summary>
    [RelayCommand]
    private void TestInGame()
    {
        if (!Ready() || CurrentId == 0) { SetStatus("Open a quest first.", true); return; }
        var lines = Designer!.TestInGame(CurrentId);
        Dialogs.Show("Test in game", string.Join("\n", lines), DialogKind.Info, new[] { "OK" },
            "The game is never started by the app. GM commands need a GM account (account level 3 or more on your server).");
    }
}
