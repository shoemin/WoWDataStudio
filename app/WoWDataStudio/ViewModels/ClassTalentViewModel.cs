using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Blp;
using WoWData.Core.Designers;
using WoWData.Core.Session;
using WoWDataStudio.Helpers;

namespace WoWDataStudio.ViewModels;

/// <summary>One cell of the talent grid: a talent (or an empty place), with its position on the canvas.</summary>
public sealed class TalentCellView
{
    public const double CellW = 104, CellH = 78, Left = 16, Top = 16;
    public int Tier { get; init; }
    public int Column { get; init; }
    public TalentNode? Talent { get; init; }
    public ImageSource? Icon { get; init; }
    public bool IsEmpty => Talent == null;
    public bool HasTalent => Talent != null;
    public bool IsSelected { get; init; }
    public double X => Left + Column * CellW;
    public double Y => Top + Tier * CellH;
    public string Name => Talent?.Name ?? "";
    public string RankText => Talent == null ? "" : $"0/{Talent.MaxRank}";
    public string Tip => Talent == null ? $"Tier {Tier} ({Tier * 5} points), column {Column}: empty. Select it, then \"Add talent here\"." :
        $"{Talent.Name} [{Talent.Id}]\nTier {Tier}, column {Column}\nRanks: {string.Join(", ", Talent.Ranks)}" + (Talent.Prereqs.Count > 0 ? "\nNeeds: " + string.Join(", ", Talent.Prereqs.Select(p => $"{p.Talent} rank {p.Rank + 1}")) : "");
}

/// <summary>An arrow piece of the grid (from a prerequisite to the talent that needs it).</summary>
public sealed record TalentArrowView(double X1, double Y1, double X2, double Y2);

public sealed record ClassItem(long Id, string Name)
{
    public override string ToString() => $"{Id} · {Name}";
}

public sealed record TreeItem(long Id, string Name, int Order, int Count)
{
    public override string ToString() => $"{Order}: {Name} ({Count} talents) [{Id}]";
}

public sealed class RaceChoice : ObservableObject
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    private bool _checked;
    public bool IsChecked { get => _checked; set => SetProperty(ref _checked, value); }
}

/// <summary>
/// 0.12.0: the Classes and talents tab: a class's talent trees on the 1.12 grid (add / remove trees, place and move
/// talents, ranks, prerequisites with their arrows, icons and backgrounds), the class's spell list by level (trainers,
/// skill lines, automatic, starting, talents) with level / trainer assignment, its skill lines, class-mask edits, and the
/// EXPERIMENTAL new class with the report of what the game and the server still need in code. It shares the Spell
/// designer's session: rank spells made here are spells there, and one save writes everything.
/// </summary>
public sealed partial class ClassTalentViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private readonly SpellDesignerViewModel _spells;

    public ClassTalentViewModel(AppSession session, MainViewModel main, SpellDesignerViewModel spells)
    {
        _session = session;
        _main = main;
        _spells = spells;
        foreach (var e in ClassDesigner.EngineReport()) EngineItems.Add(e);
    }

    private TalentDesigner? _talents;
    private ClassDesigner? _classes;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    private void SetStatus(string text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_talents != null && _spells.Session != null && ReferenceEquals(_talents.Session, _spells.Session)) return;
        await _spells.EnsureLoadedAsync();
        _talents = new TalentDesigner(_spells.Session!, _spells.Designer!);
        _classes = new ClassDesigner(_spells.Session!, _spells.Designer!);
        var keep = SelectedClass?.Id ?? 1;
        Classes.Clear();
        foreach (var c in _classes.Classes()) Classes.Add(new ClassItem(c.Id, c.Name));
        Races.Clear();
        foreach (var (v, n) in WoWData.Core.Dbc.DbcLayouts.Default.NamedValues["Race"].Values.Where(x => x.Value is >= 1 and <= 8)) Races.Add(new RaceChoice { Id = v, Name = n });
        CopyFrom = Classes.FirstOrDefault(c => c.Id == 2);
        SelectedClass = Classes.FirstOrDefault(c => c.Id == keep) ?? Classes.FirstOrDefault();
    }

    // ------------------------------------------------------------------ class

    public ObservableCollection<ClassItem> Classes { get; } = new();
    [ObservableProperty] private ClassItem? _selectedClass;
    [ObservableProperty] private string _classText = "";

    partial void OnSelectedClassChanged(ClassItem? value)
    {
        if (value == null || _classes == null) return;
        var info = _classes.Info(value.Id);
        ClassText = $"{info.Name} ({info.Token}, class {info.Id}): {info.PowerText}, spell family {info.SpellFamily}. Races: {string.Join(", ", info.Races)}.";
        RefreshTrees();
        RefreshSpells();
    }

    // ------------------------------------------------------------------ trees and the grid

    public ObservableCollection<TreeItem> Trees { get; } = new();
    [ObservableProperty] private TreeItem? _selectedTree;
    public ObservableCollection<TalentCellView> Cells { get; } = new();
    public ObservableCollection<TalentArrowView> Arrows { get; } = new();
    public ObservableCollection<DesignProblem> TreeProblems { get; } = new();
    [ObservableProperty] private TalentCellView? _selectedCell;
    [ObservableProperty] private string _treeName = "";
    [ObservableProperty] private string _treeIcon = "";
    [ObservableProperty] private string _treeBackground = "";
    [ObservableProperty] private string _treeOrder = "";
    [ObservableProperty] private string _newTreeName = "";
    [ObservableProperty] private ImageSource? _background;
    [ObservableProperty] private string _checksText = "";
    public double GridWidth => TalentCellView.Left * 2 + TalentDesigner.MaxColumns * TalentCellView.CellW;
    public double GridHeight => TalentCellView.Top * 2 + TalentDesigner.MaxTiers * TalentCellView.CellH;

    public void RefreshTrees()
    {
        if (_talents == null || SelectedClass == null) return;
        var keep = SelectedTree?.Id;
        Trees.Clear();
        foreach (var t in _talents.Trees(SelectedClass.Id)) Trees.Add(new TreeItem(t.Id, t.Name, t.Order, t.Talents.Count));
        SelectedTree = Trees.FirstOrDefault(t => t.Id == keep) ?? Trees.FirstOrDefault();
        if (SelectedTree == null) RefreshGrid();
    }

    partial void OnSelectedTreeChanged(TreeItem? value) => RefreshGrid();

    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource? Image(string path)
    {
        if (_icons.TryGetValue(path, out var img)) return img;
        try
        {
            var data = _spells.Session?.ReadFile(path);
            img = data == null ? null : ImageSources.ToBitmap(BlpFile.Parse(data).Decode(0));
        }
        catch (Exception e) when (e is BlpException or ArgumentException or InvalidOperationException) { img = null; }
        _icons[path] = img;
        return img;
    }

    public void RefreshGrid()
    {
        Cells.Clear();
        Arrows.Clear();
        TreeProblems.Clear();
        Background = null;
        if (_talents == null || SelectedTree == null)
        {
            ChecksText = "";
            return;
        }
        var tree = _talents.Tree(SelectedTree.Id);
        TreeName = tree.Name;
        TreeIcon = tree.Icon.Length > 0 ? tree.Icon : tree.IconId.ToString(CultureInfo.InvariantCulture);
        TreeBackground = tree.Background;
        TreeOrder = tree.Order.ToString(CultureInfo.InvariantCulture);
        var bg = tree.Background.Contains('\\') ? tree.Background : "Interface\\TalentFrame\\" + tree.Background;
        Background = Image(bg + "-TopLeft.blp");
        var sel = SelectedCell;
        for (int tier = 0; tier < TalentDesigner.MaxTiers; tier++)
            for (int col = 0; col < TalentDesigner.MaxColumns; col++)
            {
                var n = tree.Talents.FirstOrDefault(x => x.Tier == tier && x.Column == col);
                ImageSource? icon = n != null && n.Icon.Length > 0 ? Image("Interface\\Icons\\" + n.Icon + ".blp") : null;
                Cells.Add(new TalentCellView { Tier = tier, Column = col, Talent = n, Icon = icon, IsSelected = sel != null && sel.Tier == tier && sel.Column == col });
            }
        double Cx(int c) => TalentCellView.Left + c * TalentCellView.CellW + 26;
        double Cy(int t) => TalentCellView.Top + t * TalentCellView.CellH + 22;
        foreach (var n in tree.Talents)
            foreach (var (pre, _) in n.Prereqs)
            {
                var p = tree.Talents.FirstOrDefault(x => x.Id == pre);
                if (p == null) continue;
                if (p.Column == n.Column || p.Tier == n.Tier) Arrows.Add(new TalentArrowView(Cx(p.Column), Cy(p.Tier), Cx(n.Column), Cy(n.Tier)));
                else
                {
                    Arrows.Add(new TalentArrowView(Cx(p.Column), Cy(p.Tier), Cx(n.Column), Cy(p.Tier)));
                    Arrows.Add(new TalentArrowView(Cx(n.Column), Cy(p.Tier), Cx(n.Column), Cy(n.Tier)));
                }
            }
        foreach (var p in _talents.Check(SelectedTree.Id)) TreeProblems.Add(p);
        ChecksText = TreeProblems.Count == 0 ? "No problems: the 1.12 talent frame and server can use this tree." : $"{TreeProblems.Count(p => p.IsError)} error(s), {TreeProblems.Count(p => !p.IsError)} warning(s) or note(s).";
        if (sel != null) SelectCell(Cells.First(c => c.Tier == sel.Tier && c.Column == sel.Column));
    }

    [RelayCommand]
    private void SelectCell(TalentCellView? cell)
    {
        if (cell == null) return;
        SelectedCell = cell;
        if (cell.Talent is { } t)
        {
            TalentRanks = string.Join(", ", t.Ranks);
            TalentPrereq = t.Prereqs.Count > 0 ? $"{t.Prereqs[0].Talent}:{t.Prereqs[0].Rank + 1}" : "";
            MoveTier = t.Tier.ToString(CultureInfo.InvariantCulture);
            MoveColumn = t.Column.ToString(CultureInfo.InvariantCulture);
            SetStatus($"{t.Name} [{t.Id}]: tier {t.Tier}, column {t.Column}, {t.MaxRank} rank(s).");
        }
        else
        {
            TalentRanks = "";
            TalentPrereq = "";
            SetStatus($"Tier {cell.Tier} ({cell.Tier * 5} points), column {cell.Column} is empty: \"Add talent here\" (rank spells) or \"Make rank spells\".");
        }
        foreach (var c in Cells.ToList())
        {
            int i = Cells.IndexOf(c);
            bool on = c.Tier == cell.Tier && c.Column == cell.Column;
            if (c.IsSelected != on) Cells[i] = new TalentCellView { Tier = c.Tier, Column = c.Column, Talent = c.Talent, Icon = c.Icon, IsSelected = on };
        }
        OnPropertyChanged(nameof(HasSelectedTalent));
    }

    public bool HasSelectedTalent => SelectedCell?.Talent != null;

    private void Done(DesignResult r)
    {
        SetStatus(r.Message, !r.Ok);
        RefreshTrees();
        RefreshGrid();
    }

    [RelayCommand]
    private void AddTree()
    {
        if (_talents == null || SelectedClass == null) return;
        try
        {
            var id = _talents.AddTree(SelectedClass.Id, NewTreeName.Trim().Length > 0 ? NewTreeName.Trim() : "New tree");
            SetStatus($"New tree {id} for {SelectedClass.Name}: give it an icon and a background, then place talents.");
            RefreshTrees();
            SelectedTree = Trees.FirstOrDefault(t => t.Id == id);
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private void ApplyTree()
    {
        if (_talents == null || SelectedTree == null) return;
        var msgs = new List<string>();
        foreach (var (f, v) in new[] { ("name", TreeName), ("icon", TreeIcon), ("background", TreeBackground), ("order", TreeOrder) })
        {
            if (v.Trim().Length == 0) continue;
            var r = _talents.SetTree(SelectedTree.Id, f, v.Trim());
            if (!r.Ok)
            {
                Done(r);
                return;
            }
            msgs.Add(r.Message);
        }
        Done(DesignResult.Done(string.Join(" ", msgs)));
    }

    [RelayCommand]
    private void RemoveTree()
    {
        if (_talents == null || SelectedTree == null) return;
        if (!Services.Dialogs.Confirm("Remove tree", $"Remove the tree {SelectedTree.Name} and its {SelectedTree.Count} talent(s) from your workspace's tables? Their rank spells stay.", "Remove tree")) return;
        Done(_talents.RemoveTree(SelectedTree.Id));
    }

    // ------------------------------------------------------------------ talents

    [ObservableProperty] private string _talentRanks = "";
    [ObservableProperty] private string _talentPrereq = "";
    [ObservableProperty] private string _moveTier = "";
    [ObservableProperty] private string _moveColumn = "";
    [ObservableProperty] private string _rankTemplate = "";
    [ObservableProperty] private string _rankName = "";
    [ObservableProperty] private string _rankCount = "5";
    [ObservableProperty] private string _rankValues = "";

    private static List<long> ParseIds(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => long.TryParse(x, out var v) ? v : throw new DesignException($"\"{x}\" is not a spell ID.")).ToList();

    [RelayCommand]
    private void AddTalentHere()
    {
        if (_talents == null || SelectedTree == null || SelectedCell == null || SelectedCell.Talent != null) return;
        try
        {
            var id = _talents.AddTalent(SelectedTree.Id, SelectedCell.Tier, SelectedCell.Column, ParseIds(TalentRanks));
            Done(DesignResult.Done($"New talent {id} at tier {SelectedCell.Tier}, column {SelectedCell.Column}."));
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private void MakeRankSpells()
    {
        if (_talents == null) return;
        try
        {
            if (!long.TryParse(RankTemplate.Trim(), out var tpl)) throw new DesignException("The template is a spell ID (a passive talent spell of the class, say).");
            if (!int.TryParse(RankCount.Trim(), out var count)) throw new DesignException("The number of ranks is 1-5.");
            var values = RankValues.Trim().Length == 0 ? null : RankValues.Split(',', StringSplitOptions.TrimEntries).ToList();
            var ids = _talents.MakeRankSpells(tpl, RankName.Trim().Length > 0 ? RankName.Trim() : _spells.Designer!.Name(tpl), count, values);
            TalentRanks = string.Join(", ", ids);
            SetStatus($"Made {ids.Count} rank spell(s): {TalentRanks} (open them in the Spell designer). Now \"Add talent here\" or \"Apply talent changes\".");
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private void ApplyTalent()
    {
        if (_talents == null || SelectedCell?.Talent is not { } t) return;
        try
        {
            var msgs = new List<string>();
            if (int.TryParse(MoveTier, out var tier) && int.TryParse(MoveColumn, out var col) && (tier != t.Tier || col != t.Column))
            {
                var r = _talents.Move(t.Id, tier, col);
                if (!r.Ok) { Done(r); return; }
                msgs.Add(r.Message);
            }
            var ranks = ParseIds(TalentRanks);
            if (!ranks.SequenceEqual(t.Ranks))
            {
                var r = _talents.SetRanks(t.Id, ranks);
                if (!r.Ok) { Done(r); return; }
                msgs.Add(r.Message);
            }
            var pre = TalentPrereq.Trim();
            var cur = t.Prereqs.Count > 0 ? $"{t.Prereqs[0].Talent}:{t.Prereqs[0].Rank + 1}" : "";
            if (pre != cur)
            {
                var parts = pre.Split(':');
                long pid = pre.Length == 0 ? 0 : long.TryParse(parts[0], out var p) ? p : throw new DesignException("The prerequisite is a talent ID, with :rank (\"1234:5\").");
                int rank = parts.Length > 1 && int.TryParse(parts[1], out var rk) ? rk : 0;
                var r = _talents.SetPrereq(t.Id, pid, rank);
                if (!r.Ok) { Done(r); return; }
                msgs.Add(r.Message);
            }
            Done(DesignResult.Done(msgs.Count == 0 ? "Nothing changed." : string.Join(" ", msgs)));
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private void RemoveTalent()
    {
        if (_talents == null || SelectedCell?.Talent is not { } t) return;
        Done(_talents.RemoveTalent(t.Id));
    }

    [RelayCommand]
    private async Task OpenRankSpellAsync()
    {
        if (SelectedCell?.Talent is not { Ranks.Count: > 0 } t) return;
        await _spells.ShowSpellAsync(t.Ranks[0]);
    }

    // ------------------------------------------------------------------ spells by level

    public ObservableCollection<ClassSpell> Spells { get; } = new();
    public ObservableCollection<ClassSkill> Skills { get; } = new();
    [ObservableProperty] private ClassSpell? _selectedSpell;
    [ObservableProperty] private string _assignLevel = "";
    [ObservableProperty] private bool _assignAllTrainers;
    [ObservableProperty] private string _assignCost = "";
    [ObservableProperty] private string _spellsNote = "";

    public void RefreshSpells()
    {
        Spells.Clear();
        Skills.Clear();
        if (_classes == null || SelectedClass == null) return;
        foreach (var s in _classes.SpellList(SelectedClass.Id)) Spells.Add(s);
        foreach (var s in _classes.Skills(SelectedClass.Id)) Skills.Add(s);
        SpellsNote = _spells.Session!.HasServer ? $"{Spells.Count} spells: from the class's trainers, its skill lines, its starting spells and its talents." :
            $"{Spells.Count} spells from the skill lines and talents. The trainers and starting spells need the server database (Settings).";
    }

    partial void OnSelectedSpellChanged(ClassSpell? value)
    {
        if (value != null) AssignLevel = value.Level.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void AssignSpellLevel()
    {
        if (_classes == null || SelectedClass == null || SelectedSpell == null) return;
        try
        {
            if (!long.TryParse(AssignLevel, out var lvl)) throw new DesignException("The level is a number.");
            long? cost = AssignCost.Trim().Length > 0 ? SpellDesigner.ParseMoney(AssignCost) : null;
            var r = _classes.Assign(SelectedClass.Id, SelectedSpell.Spell, lvl, AssignAllTrainers ? "all" : null, cost);
            SetStatus(r.Message, !r.Ok);
            RefreshSpells();
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task OpenClassSpellAsync()
    {
        if (SelectedSpell != null) await _spells.ShowSpellAsync(SelectedSpell.Spell);
    }

    // ------------------------------------------------------------------ class masks

    public List<string> MaskTables { get; } = ClassDesigner.MaskColumns.Select(m => $"{m.Table}.{m.Column}").ToList();
    [ObservableProperty] private string _maskTable = "SkillRaceClassInfo.ClassMask";
    [ObservableProperty] private string _maskIds = "";

    [RelayCommand]
    private void AddToMask() => EditMask(true);

    [RelayCommand]
    private void RemoveFromMask() => EditMask(false);

    private void EditMask(bool add)
    {
        if (_classes == null || SelectedClass == null) return;
        try
        {
            var parts = MaskTable.Split('.');
            var r = _classes.EditMask(parts[0], parts[1], MaskIds.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries), SelectedClass.Id, add);
            SetStatus(r.Message, !r.Ok);
            RefreshTrees();
            RefreshSpells();
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    // ------------------------------------------------------------------ a new class (EXPERIMENTAL)

    [ObservableProperty] private string _newClassName = "";
    [ObservableProperty] private string _newClassToken = "";
    [ObservableProperty] private ClassItem? _copyFrom;
    public ObservableCollection<RaceChoice> Races { get; } = new();
    public ObservableCollection<EngineItem> EngineItems { get; } = new();
    public ObservableCollection<string> NewClassDone { get; } = new();

    [RelayCommand]
    private void CreateClass()
    {
        if (_classes == null || CopyFrom == null) return;
        var races = Races.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        try
        {
            var (id, done) = _classes.NewClass(NewClassName.Trim(), NewClassToken.Trim(), CopyFrom.Id, races);
            NewClassDone.Clear();
            foreach (var d in done) NewClassDone.Add(d);
            EngineItems.Clear();
            foreach (var e in ClassDesigner.EngineReport(id)) EngineItems.Add(e);
            Classes.Add(new ClassItem(id, NewClassName.Trim()));
            SetStatus($"EXPERIMENTAL: the data of class {id} ({NewClassName.Trim()}) is ready to save. The game and the server still need the code changes listed under \"What a new class still needs\" before it can be played.");
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    // ------------------------------------------------------------------ save, discard (shared with the Spell designer)

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_classes == null) return;
        try
        {
            var res = await _main.RunBusyAsync("Saving the classes and talents into your workspace...", () => Task.Run(() => _talents!.Save()));
            SetStatus(res.Message);
            _main.Toast(res.Message);
            _spells.Reset();
            _talents = null;
            await EnsureLoadedAsync();
        }
        catch (DesignException e) { SetStatus(e.Message, true); }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        _spells.Reset();
        _talents = null;
        await EnsureLoadedAsync();
        NewClassDone.Clear();
        SetStatus("Your unsaved designer edits were thrown away.");
    }

    /// <summary>For the screenshots and tests: select a tree by ID.</summary>
    public void SelectTree(long id) => SelectedTree = Trees.FirstOrDefault(t => t.Id == id);
}
