using System.Globalization;
using System.Text;
using System.Text.Json;
using WoWData.Core.Designers;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Session;
using WoWData.Core.Workspace;

namespace IngameRunner;

/// <summary>
/// The in-game visual test of WoW Data Studio's designers: 0.12.0 (spell designer, class and talent designer) and 0.13.0
/// (quest-chain designer). The run script is tests\ingame\run_designers.ps1. Every edit goes through the app's own Core
/// designers (the same calls as the Spell designer / Classes and talents / Quest chains tabs and `wds spells|talents|quests`),
/// in an isolated Data Studio home (RUNDIR\studio_home):
///   IngameRunner designers prepare CLIENT HOME RUNDIR [S1 T1 Q1]     the three items, saved in the workspace; SQL + apply plan (NOT applied)
///   IngameRunner designers server  CLIENT HOME RUNDIR plan|apply|undo|status [--port N --user U --no-password --state DIR]
///   IngameRunner designers wdb     CLIENT HOME RUNDIR                 "Clear the game's cache (WDB)" of CLIENT
///   IngameRunner designers dryrun  CLIENT HOME RUNDIR DUMP.sql        apply + undo against a throwaway MySQL loaded from DUMP.sql
///   IngameRunner designers regroup CLIENT HOME RUNDIR                 2026-10-16 (0.13.2): the talent designer's Save on the prepared workspace
///                                                                     (puts every tree's Talent.dbc rows together again; see TalentDesigner.RegroupTrees)
///   IngameRunner designers scenarios CLIENT HOME RUNDIR               2026-10-16: RUNDIR\lua\*.lua and sessions_designers.json again from ids.json
/// Never writes the live database except "server apply" / "server undo" (the run script's two steps). The prepare only READS the
/// live world database (the designers' server editor), as the app does.
/// </summary>
public static class Designers
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ------------------------------------------------------------------ what is made (names; the IDs are the app's first free custom IDs)
    public const string S1Name = "WDS Shadow Lance", S1Rank = "Rank 1", S1Icon = "Spell_Holy_HolyBolt", S1Tint = "#40FF60";
    public const float S1Size = 1.5f;
    public const long S1Source = 686, S1VisualFrom = 133;        // Shadow Bolt (Rank 1); Fireball (Rank 1) lends its missile and impact
    public const string S1Value = "777-888";
    public const long TrainerLook = 3156, GiverLook = 3143;      // Nartok (warlock trainer, Valley of Trials); Gornek
    public const string TrainerName = "WDS Test Trainer", GiverName = "WDS Quest Giver", NpcSub = "WoW Data Studio";
    public const long TrainerCost = 100, TrainerLevel = 1;      // 1 silver, level 1
    public const long TalentTree = 301, MovedTalent = 963, TalentTemplate = 17778;   // Destruction; Shadowburn; Cataclysm (Rank 1)
    public const int NewTier = 0, NewColumn = 0, MoveTier = 1, MoveColumn = 3;
    public const string T1Name = "WDS Test Talent", T1Icon = "Spell_Nature_StarFall", T1Value = "-7";
    public const string QATitle = "WDS: Boar Culling", QBTitle = "WDS: A Lesson in Shadow";
    public const long Boar = 3098, RewardItem = 4536, RewardCount = 5;   // Mottled Boar; Shiny Red Apple
    // where (Kalimdor, Valley of Trials): the hunting spot P between four Mottled Boar spawns (4691 and 7979 in front, 18 and 22 yd, facing
    // north), the quest giver 3.5 yd west of P and the trainer 3.5 yd east of P (both inside the 5 yd interaction distance)
    public const double PX = -695.0, PY = -4300.0, GX = -695.0, GY = -4296.5, TX = -695.0, TY = -4303.5;
    // 2026-10-16: the boars' viewpoint B (run 20261014_2308's hot reload and the 0.10 run's b1/s2: 16 yd west of Mottled Boar spawn 4691,
    // facing east, where TargetNearestEnemy finds the boars). From P the boars are 18-40 yd away to the north-west and north-east: outside
    // the game's TargetNearestEnemy area (41 yd ahead, 10 yd around: CVars targetNearestDistance / targetNearestDistanceRadius), so the
    // first run's c1 and q1 targeted no boar. c1 starts at B; q1 goes there for the kills (".go xyzo", a GM command) and back to P.
    public const string BoarView = "-682.7,-4271,39.3,4.712";
    // the three boar viewpoints (".go xyzo X Y Z O"): 16 yd from Mottled Boar spawns 4691 (west of it, facing east: BoarView), 7979 (west of
    // it, facing east) and 8547 (east of it, facing west); z = the ground (the game's map) + 0.5
    static readonly (double X, double Y, double O)[] BoarViews = { (-682.7, -4271.0, 4.712), (-681.0, -4301.1, 4.712), (-715.9, -4299.8, 1.571) };
    static string GoView(QuestDesigner qd, double x, double y, double o) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1:0.0} {2:0.0} {3:0.000}", x, y, (qd.GroundHeight(1, x, y) ?? throw new InvalidOperationException($"no ground at {x} {y}")) + 0.5, o);
    public static readonly string[] AllowedTables =
        { "spell_template", "creature_template", "creature", "npc_trainer", "quest_template", "creature_questrelation", "creature_involvedrelation", "skill_line_ability" };
    /// <summary>0.13.3: the app's spell ID range (new spells; VMaNGOS casts no spell above 40000).</summary>
    public const long SpellStart = 35000, SpellEnd = 39999;
    /// <summary>The highest SkillLineAbility ID of the game and the server as shipped (a new skill line row comes after it).</summary>
    public const long LastStockSkillLineAbility = 15030;

    static string LuaDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\lua\designers"));

    public static int Run(string[] a)
    {
        if (a.Length < 5) { Console.Error.WriteLine("usage: IngameRunner designers prepare|server|wdb|dryrun CLIENT HOME RUNDIR ..."); return 2; }
        string sub = a[1], client = a[2], home = a[3], run = a[4];
        Directory.CreateDirectory(run);
        return sub switch
        {
            "prepare" => Prepare(client, home, run, a.Skip(5).ToArray()),
            "server" => V010.Server(client, home, run, a.Skip(5).ToArray(), Safety),
            "wdb" => V010.Wdb(client, home, run),
            "dryrun" => DryRun(client, home, run, a[5]),
            "regroup" => Regroup(client, home, run),
            "learnfix" => LearnFix(client, home, run),
            "scenarios" => Scenarios(client, home, run),
            "chartest" => CharTest(run, a[5]),
            _ => throw new ArgumentException("unknown designers command " + sub),
        };
    }

    // ================================================================== prepare

    sealed class Ids
    {
        public long S1 { get; set; }
        public long Learn { get; set; }
        public long Visual { get; set; }
        public long Trainer { get; set; }
        public long TrainerSpawn { get; set; }
        public long T1Spell { get; set; }
        public long T1Talent { get; set; }
        public long Giver { get; set; }
        public long GiverSpawn { get; set; }
        public long QA { get; set; }
        public long QB { get; set; }
    }

    static string _run = "";
    static AppSession _s = null!;
    static Workspace Ws => _s.Workspace!;
    static Ids _ids = new();

    static (DesignSession D, SpellDesigner Sp) NewDesign()
    {
        var svc = _s.ServerData ?? throw new InvalidOperationException("no server data service");
        _ = svc.Schema;   // the server database must be reachable (the designers then edit both sides)
        var d = new DesignSession(_s.Dbc ?? throw new InvalidOperationException("no data tables"), svc.NewEditor(), (_s.Settings.ServerCustomIdStart, _s.Settings.ServerCustomIdEnd), null, (_s.Settings.SpellIdStart, _s.Settings.SpellIdEnd));
        return (d, new SpellDesigner(d));
    }

    static int Prepare(string client, string home, string run, string[] only)
    {
        _run = run;
        _s = V010.Open(client, home);
        var reportPath = Path.Combine(run, "app_report.json");
        var idsPath = Path.Combine(run, "ids.json");
        var all = new List<ItemReport>();
        var svc = _s.ServerData ?? throw new InvalidOperationException("no server data service");
        var (cs, ce) = svc.CustomIds;
        if (cs != 900000 || ce < 900100) throw new InvalidOperationException($"custom ID range is {cs}-{ce}, expected 900000-999999");
        // 0.13.3: spells come from the spell ID range (VMaNGOS casts no spell above 40000)
        if (svc.SpellIds != (SpellStart, SpellEnd)) throw new InvalidOperationException($"spell ID range is {svc.SpellIds.Start}-{svc.SpellIds.End}, expected {SpellStart}-{SpellEnd}");
        // a fresh start in this (isolated) workspace: every earlier edit of an earlier prepare is reverted
        foreach (var v in Ws.List().ToList()) Ws.Revert(v.Record.Path);
        Console.WriteLine($"workspace reset: {Ws.List().Count} changes left");
        var groups = new (string Id, Func<ItemReport> Fn)[] { ("S1", SpellItem), ("T1", TalentItem), ("Q1", QuestItem) };
        foreach (var (id, fn) in groups)
        {
            if (only.Length > 0 && !only.Contains(id)) continue;
            Console.WriteLine($"==== {id}");
            ItemReport r;
            try { r = fn(); }
            catch (Exception e) { r = new ItemReport { Id = id, Ok = false, Error = e.ToString(), Verdict = "FAIL (exception): " + e.Message }; }
            Console.WriteLine($"  {r.Id} -> {(r.Ok ? "ok" : "PROBLEM")}: {r.Verdict}");
            all.Add(r);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(all, J));
            File.WriteAllText(idsPath, JsonSerializer.Serialize(_ids, J));
        }
        // the server deliverables: SQL patch + revert, the Apply to server plan (NOT applied), the run's safety rule
        var sdir = Path.Combine(run, "server");
        Directory.CreateDirectory(sdir);
        var saved = svc.SavedChanges();
        var script = ServerSql.Build(svc, saved);
        File.WriteAllText(Path.Combine(sdir, "designers_server.sql"), script.Patch, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(sdir, "designers_server.revert.sql"), script.Revert, new UTF8Encoding(false));
        var plan = new ServerApplier(svc).Plan();
        var safety = Safety(svc, plan);
        File.WriteAllText(Path.Combine(sdir, "apply_plan.txt"), V010.PlanText(plan, safety), new UTF8Encoding(false));
        var serverLine = $"SQL patch: {script.Summary} ({script.Rows} rows, tables {string.Join(", ", script.Tables)}) -> server\\designers_server.sql (+ .revert.sql); " +
                         $"apply plan: {plan.Changes.Sum(c => c.Rows.Count)} rows, conflicts {plan.Conflicts.Count}, already applied {plan.AlreadyApplied.Count}; " +
                         "safety: " + (safety.Count == 0 ? "every row is a NEW row with a custom ID in an allowed table" : string.Join("; ", safety));
        Console.WriteLine(serverLine);
        foreach (var r in all)
        {
            r.Compare.Add(serverLine);
            if (safety.Count > 0 || plan.Conflicts.Count > 0) { r.Ok = false; r.Verdict += " | SERVER PLAN REFUSED by the run's safety rule"; }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(all, J));
        var list = Ws.List().Select(v => new { v.Record.Path, v.Record.Kind, v.Record.ChangeSet, Set = Ws.Manifest.ChangeSets.GetValueOrDefault(v.Record.ChangeSet)?.Label, v.StatusText, v.Record.Summary }).ToList();
        File.WriteAllText(Path.Combine(run, "workspace_changes.json"), JsonSerializer.Serialize(list, J));
        Console.WriteLine($"workspace: {list.Count} files in {Ws.Manifest.ChangeSets.Count} change sets");
        WriteViewpoints(run);
        WriteLua(run);
        File.WriteAllText(idsPath, JsonSerializer.Serialize(_ids, J));
        _s.Dispose();
        return all.All(x => x.Ok) && safety.Count == 0 ? 0 : 1;
    }

    static void Problems(ItemReport r, string what, IEnumerable<DesignProblem> ps)
    {
        var l = ps.ToList();
        r.Validation.Add($"{what}: " + (l.Count == 0 ? "no problems" : string.Join(" | ", l.Select(p => p.Text))));
    }

    static void Need(ItemReport r, DesignResult res, string what)
    {
        r.Validation.Add($"{what}: {(res.Ok ? "" : "REFUSED: ")}{res.Message}" + (res.Problems is { Count: > 0 } p ? " [" + string.Join(" | ", p.Select(x => x.Text)) + "]" : ""));
        if (!res.Ok) throw new InvalidOperationException($"{what} refused: {res.Message}");
    }

    static void Saved(ItemReport r, DesignSaveResult res)
    {
        r.ChangeSets.Add($"{res.ChangeSet}: {res.Message}");
        r.Files.AddRange(res.ClientTables.Select(t => $@"DBFilesClient\{t}.dbc"));
        r.Files.AddRange(res.Files);
        r.Files.AddRange(res.ServerTables.Select(t => $"server: {t}"));
        foreach (var n in res.Notes) r.Notes.Add("save note: " + n);
        if (res.ServerSkipped) throw new InvalidOperationException("the server rows were skipped (the server database is not connected)");
    }

    // ------------------------------------------------------------------ S1: the spell, its visual, its trainer

    static ItemReport SpellItem()
    {
        var r = new ItemReport
        {
            Id = "S1",
            Title = $"Spell designer: \"{S1Name}\" ({S1Rank}), a clone of Shadow Bolt ({S1Source}) with its own name, tooltip, damage {S1Value}, icon {S1Icon} " +
                    $"and visual (Fireball's missile and impact, then Duplicate visual tinted {S1Tint} at size x{S1Size}); taught by a new test trainer \"{TrainerName}\" (a copy of Nartok) for 1s at level 1",
            How = "Spell designer tab through Core (the same calls as the tab and `wds spells`): SpellDesigner.Clone -> Set (name, rank, description, effect1-value, " +
                  "icon) -> SpellVisuals.Compose (missile + impact from spell 133) -> SpellVisuals.Duplicate (tint, size) -> the trainer NPC (Server data: " +
                  "ServerEditor.Duplicate of creature_template 3156, placed with QuestDesigner.PlaceNpc, z from the terrain) -> SpellDesigner.AddToTrainer " +
                  "(makes the learn spell) -> Check -> Save to my workspace (one change set). NOTHING is applied here.",
        };
        var (d, sp) = NewDesign();
        var ed = d.Server!;
        var qd = new QuestDesigner(d, sp);
        // the spell
        long id = sp.Clone(S1Source, S1Name, rank: S1Rank);
        _ids.S1 = id;
        r.Validation.Add($"Clone {S1Source} ({sp.Title(S1Source)}) -> {sp.Title(id)}");
        Need(r, sp.Set(id, "description", "Hurls a lance of green-tinted shadow at the enemy, causing $s1 Shadow damage. (WoW Data Studio test spell)"), "description");
        Need(r, sp.Set(id, "effect1-value", S1Value), "effect1-value");
        Need(r, sp.Set(id, "icon", S1Icon), "icon");
        r.Validation.Add("visual (compose): " + SpellVisuals.Compose(d, sp, id, new Dictionary<string, string> { ["missile"] = $"spell:{S1VisualFrom}", ["impact"] = $"spell:{S1VisualFrom}" }));
        r.Validation.Add("visual (duplicate): " + SpellVisuals.Duplicate(d, sp, id, S1Size, SpellVisuals.ParseTint(S1Tint), $"V{id}"));
        var st = d.Table("Spell");
        _ids.Visual = st.Number(sp.Row(id), st.Layout.Column("SpellVisual")!);
        var vi = SpellVisuals.Describe(d, _ids.Visual);
        foreach (var m in vi.Models) r.Compare.Add($"visual {_ids.Visual} {m.Slot} ({m.Where}): {m.Name} = {m.File}");
        // the trainer: a copy of Nartok with its own (empty) trainer list, the Trainer flag only, placed east of the hunting spot
        var ct = ed.Table("creature_template");
        long tEntry = ed.SuggestId(ct);
        var (tKey, tr) = ed.Duplicate("creature_template", ed.ResolveKey(ct, TrainerLook.ToString(CultureInfo.InvariantCulture)), new (string, string?)[]
        {
            ("name", TrainerName), ("subname", NpcSub), ("npc_flags", "16"), ("trainer_id", "0"), ("gossip_menu_id", "0"), ("script_name", ""), ("ai_name", ""),
        }.Where(v => ct.Has(v.Item1)), tEntry);
        if (!tr.Ok || tKey == null) throw new InvalidOperationException("trainer NPC refused: " + tr.Message);
        _ids.Trainer = tEntry;
        r.Validation.Add($"New trainer NPC {tEntry} (copy of {TrainerLook}): {tr.Message}");
        var (tGuid, tMsg) = qd.PlaceNpc(tEntry, 1, TX, TY, null, Facing(TX, TY, PX, PY));
        _ids.TrainerSpawn = tGuid;
        r.Validation.Add($"Placed: {tMsg}");
        var add = sp.AddToTrainer(id, tEntry.ToString(CultureInfo.InvariantCulture), TrainerLevel, TrainerCost);
        Need(r, add, $"Add to trainer {tEntry}");
        _ids.Learn = sp.LearnSpellsOf(id).FirstOrDefault();
        // 0.13.3: a skill line (the Warlock's Destruction, 593), so the class trainer's window lists the spell (TrainerFrame_SetServices)
        Need(r, sp.AddSkillLine(id, 593, 0x100), "Add to skill line Destruction (Warlock)");
        foreach (var t in sp.Trainers(id)) r.Compare.Add($"trainer entry: {t.Table} {t.Key}: {t.TrainerName} teaches learn spell {t.LearnSpell} -> {t.Spell} at level {t.Level} for {SpellDesigner.Money(t.Cost)}");
        // what the tab shows: the tooltip preview, the checks, the changed fields against the original
        r.Compare.Add("tooltip preview: " + sp.Tooltip(id).Text.Replace("\n", " / "));
        r.Compare.Add("tooltip preview of the learn spell: " + sp.Tooltip(_ids.Learn).Text.Replace("\n", " / "));
        foreach (var f in SpellForm.Fields)
        {
            string a0, b0;
            try { a0 = sp.Show(S1Source, f.Id); b0 = sp.Show(id, f.Id); } catch (DesignException) { continue; }
            if (a0 != b0) r.Compare.Add($"field {f.Id}: '{a0}' -> '{b0}'");
        }
        var checks = sp.Check(id);
        Problems(r, $"checks of {sp.Title(id)}", checks);
        Problems(r, $"checks of the learn spell {_ids.Learn}", sp.Check(_ids.Learn));
        var res = sp.Save($"Test S1: {S1Name} (spell, visual, trainer {TrainerName})");
        Saved(r, res);
        // after the save: Check client and server finds no difference for the designed spells (the manual's promise)
        var ed2 = _s.ServerData!.NewEditor();
        var issues = ServerConsistency.Check(ed2, "spells", new[] { id, _ids.Learn });
        r.Validation.Add($"Check client and server (spells {id}, {_ids.Learn}) after the save: {issues.Count} difference(s)" + string.Concat(issues.Select(i => "; " + i.Text)));
        bool filesOk = res.Files.Any(f => f.StartsWith(@"Spells\WDS\", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".m2", StringComparison.OrdinalIgnoreCase));
        r.Ok = !checks.Any(p => p.IsError) && issues.Count == 0 && _ids.Learn > 0 && filesOk && res.ClientTables.Contains("Spell");
        r.Verdict = r.Ok ? $"app steps passed: spell {id} + learn spell {_ids.Learn}, visual {_ids.Visual} with {res.Files.Count} copied model/texture file(s), trainer {tEntry} placed (spawn {tGuid}); checks clean; client and server in step"
                         : "app-level problem: see validation";
        return r;
    }

    // ------------------------------------------------------------------ T1: the Destruction tree

    static ItemReport TalentItem()
    {
        var r = new ItemReport
        {
            Id = "T1",
            Title = $"Talent designer: the Warlock's Destruction tree ({TalentTree}): a new 1-rank talent \"{T1Name}\" in the empty place tier {NewTier} column {NewColumn} " +
                    $"(rank spell made from Cataclysm {TalentTemplate}: mana cost {T1Value}%, icon {T1Icon}, own description) and Shadowburn ({MovedTalent}) moved from tier 2 column 3 to tier {MoveTier} column {MoveColumn}",
            How = "Classes and talents tab through Core: TalentDesigner.MakeRankSpells (Spell designer clones) -> SpellDesigner.Set (icon) -> AddTalent -> Move -> " +
                  "Check the tree -> Save to my workspace. Talent.dbc is a client table (the server reads its own copy: NOT copied, see the report); the rank spell's " +
                  "spell_template row is a new custom ID.",
        };
        var (d, sp) = NewDesign();
        var td = new TalentDesigner(d, sp);
        r.Compare.Add("before:\n" + td.Grid(TalentTree));
        var ranks = td.MakeRankSpells(TalentTemplate, T1Name, 1, new[] { T1Value }, 1, "Reduces the Mana cost of your Destruction spells by $s1%. (WoW Data Studio test talent)");
        _ids.T1Spell = ranks[0];
        Need(r, sp.Set(ranks[0], "icon", T1Icon), "rank spell icon");
        r.Compare.Add($"rank spell {ranks[0]}: {sp.Title(ranks[0])}: tooltip preview '{sp.Tooltip(ranks[0]).Text.Replace("\n", " / ")}'");
        _ids.T1Talent = td.AddTalent(TalentTree, NewTier, NewColumn, ranks);
        r.Validation.Add($"Add talent here (tier {NewTier}, column {NewColumn}): talent {_ids.T1Talent}");
        Need(r, td.Move(MovedTalent, MoveTier, MoveColumn), $"Move talent {MovedTalent}");
        var checks = td.Check(TalentTree);
        Problems(r, "Check the tree", checks);
        Problems(r, $"checks of the rank spell {ranks[0]}", sp.Check(ranks[0]));
        r.Compare.Add("after:\n" + td.Grid(TalentTree));
        var res = td.Save($"Test T1: Destruction - {T1Name} + Shadowburn moved");
        Saved(r, res);
        r.Ok = !checks.Any(p => p.IsError) && res.ClientTables.Contains("Talent") && res.ServerTables.Contains("spell_template");
        r.Verdict = r.Ok ? $"app steps passed: talent {_ids.T1Talent} (rank spell {ranks[0]}) at tier {NewTier} col {NewColumn}, Shadowburn at tier {MoveTier} col {MoveColumn}; the tree check is clean"
                         : "app-level problem: see validation";
        return r;
    }

    // ------------------------------------------------------------------ Q1: the quest chain

    static ItemReport QuestItem()
    {
        var r = new ItemReport
        {
            Id = "Q1",
            Title = $"Quest-chain designer: a 2-step Horde chain at the Valley of Trials: new quest giver \"{GiverName}\" (looks like Gornek {GiverLook}) placed by map position " +
                    $"-> \"{QATitle}\" (kill 2 Mottled Boars, 1s) -> turn in -> \"{QBTitle}\" offered next in chain (reward: learn {S1Name} + {RewardCount} Shiny Red Apples); texts with $N / $c / $B",
            How = "Quest chains tab through Core: QuestDesigner.NewNpc (map, x, y; z from the terrain: the map picker's path) -> New -> Set (texts, levels, races, " +
                  "category, objective, money) -> SetRelation (giver, ender) -> AddAfter (next in chain) -> Set -> SetRewardSpell (the S1 spell; its learn spell) -> " +
                  "QuestRules.Check / QuestChains.Open / Preview / TestInGame -> Save to my workspace.",
        };
        var (d, sp) = NewDesign();
        var qd = new QuestDesigner(d, sp);
        var (g, gGuid, gMsg) = qd.NewNpc(GiverName, GiverLook, NpcSub, map: 1, x: GX, y: GY, z: null, orientation: Facing(GX, GY, PX, PY));
        _ids.Giver = g;
        _ids.GiverSpawn = gGuid ?? 0;
        r.Validation.Add("New quest giver: " + gMsg);
        void Set(long q, string f, string v) => Need(r, qd.Set(q, f, v), $"quest {q} {f}");
        long a = qd.New(QATitle);
        _ids.QA = a;
        Set(a, "details", "Greetings, $N. The Mottled Boars of the Valley of Trials have grown bold.$B$BA $c of your skill should make short work of two of them. (WoW Data Studio test quest)");
        Set(a, "objectives", "Slay 2 Mottled Boars, then return to the WDS Quest Giver.");
        Set(a, "progress-text", "Have you thinned the herd yet, $N?");
        Set(a, "completion-text", "Well fought, $c. But there is more to learn.");
        Set(a, "level", "1");
        Set(a, "min-level", "1");
        Set(a, "races", "Horde");
        Set(a, "category", "14");
        Set(a, "target1", $"creature:{Boar}");
        Set(a, "target1-count", "2");
        Set(a, "money", "1s");
        Set(a, "xp", "0");
        Need(r, qd.SetRelation(a, $"creature:{g}", giver: true), "giver");
        Need(r, qd.SetRelation(a, $"creature:{g}", giver: false), "ender");
        long b = qd.AddAfter(a, QBTitle);
        _ids.QB = b;
        Set(b, "details", "You did well, $N. Every $c should know the Shadow Lance.$B$BTake this lesson, and these apples for the road.");
        Set(b, "objectives", "Speak again with the WDS Quest Giver.");
        Set(b, "completion-text", "Go now, $N, and practise on the boars.");
        Set(b, "reward-item1", RewardItem.ToString(CultureInfo.InvariantCulture));
        Set(b, "reward-item1-count", RewardCount.ToString(CultureInfo.InvariantCulture));
        Set(b, "xp", "0");
        if (_ids.S1 == 0) _ids.S1 = sp.Search(S1Name).FirstOrDefault().Id;
        Need(r, qd.SetRewardSpell(b, _ids.S1, learn: true), $"reward spell {_ids.S1}");
        // what the tab shows
        var chain = QuestChains.Open(qd, a);
        r.Compare.Add("flowchart: " + chain.Describe().Replace("\r", "").Replace("\n", " / "));
        var problems = QuestRules.Check(qd, new[] { a, b });
        r.Validation.Add("Checks (the whole chain): " + (problems.Count == 0 ? "no problems" : string.Join(" | ", problems.Select(p => p.Text))));
        var player = new QuestTextPlayer("Statclone", "Orc", "Warlock", false);
        foreach (var q in new[] { a, b })
            foreach (var (f, pv) in qd.Preview(q, player).Where(x => x.Preview.Text.Length > 0))
                r.Compare.Add($"text preview {q} {f.Id}: {pv.Text.Replace("\n", "\\n")}" + (pv.UnknownTokens.Count > 0 ? $" (unknown tokens: {string.Join(", ", pv.UnknownTokens)})" : ""));
        foreach (var q in new[] { a, b }) r.Notes.Add($"objectives {q}: " + string.Join("; ", qd.Objectives(q).Select(o => o.Text)));
        r.Notes.Add("Test in game: " + string.Join(" / ", qd.TestInGame(a)));
        r.Notes.Add($"summary {a}: {qd.Summary(a).Text}; {b}: {qd.Summary(b).Text}");
        var res = qd.Save($"Test Q1: {QATitle} -> {QBTitle} (giver {GiverName})");
        Saved(r, res);
        bool errors = problems.Any(p => p.Problem.IsError);
        r.Ok = !errors && res.ServerTables.Contains("quest_template") && _ids.Giver > 0 && _ids.GiverSpawn > 0;
        r.Verdict = r.Ok ? $"app steps passed: giver {g} (spawn {gGuid}), quests {a} -> {b} (next in chain), reward spell {_ids.S1} through learn spell {_ids.Learn}; chain checks without errors"
                         : "app-level problem: see validation";
        return r;
    }

    static double Facing(double x, double y, double tx, double ty)
    {
        double o = Math.Atan2(ty - y, tx - x);
        if (o < 0) o += 2 * Math.PI;
        return Math.Round(o, 4);
    }

    // ------------------------------------------------------------------ the run's safety rule for the server

    /// <summary>Only NEW rows, only in the tables the three items need, and every key in the custom ID range 900000-999999 (a
    /// quest relation's or trainer row's key is two custom IDs: NPC + quest / NPC + learn spell); since 0.13.3 spell IDs in the
    /// spell ID range 35000-39999 and a new skill line row after the game's own. Nothing else is ever applied.</summary>
    public static List<string> Safety(ServerDataService svc, ServerApplyPlan plan)
    {
        var bad = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "patch", "patch_min", "patch_max", "build", "build_min", "build_max", "slot" };
        foreach (var ch in plan.Changes)
        {
            if (!AllowedTables.Contains(ch.Table.Name)) { bad.Add($"{ch.Table.Name}: not a table of this test ({string.Join(", ", AllowedTables)})"); continue; }
            foreach (var r in ch.Rows.Values)
            {
                if (!r.IsAdded) { bad.Add($"{ch.Table.Name} {r.Key}: {r.KindText} (only new rows are allowed)"); continue; }
                // 0.13.3: a spell ID column holds an ID of the spell ID range; a new skill line row an ID after the game's own
                bool SpellCol(string k) => ch.Table.Name == "spell_template" && k.Equals("entry", StringComparison.OrdinalIgnoreCase) || ch.Table.Name == "npc_trainer" && k.Equals("spell", StringComparison.OrdinalIgnoreCase);
                var keys = ch.Table.Key.Where(k => !skip.Contains(k)).Select(k => (k, v: WorldValue.Long(r.After![ch.Table.IndexOf(k)]))).ToList();
                if (ch.Table.Name == "skill_line_ability")
                {
                    if (keys.Any(x => x.v <= LastStockSkillLineAbility || x.v > 65535)) bad.Add($"{ch.Table.Name} {r.Key}: not a new skill line row (after {LastStockSkillLineAbility}, at most 65535)");
                    long sid = WorldValue.Long(r.After![ch.Table.IndexOf("spell_id")]);
                    if (sid < SpellStart || sid > SpellEnd) bad.Add($"{ch.Table.Name} {r.Key}: spell {sid} outside the spell ID range {SpellStart}-{SpellEnd}");
                    continue;
                }
                if (keys.Count == 0 || keys.Any(x => SpellCol(x.k) ? x.v < SpellStart || x.v > SpellEnd : x.v < 900000 || x.v > 999999))
                    bad.Add($"{ch.Table.Name} {r.Key}: key outside the custom ID range 900000-999999 (spells: the spell ID range {SpellStart}-{SpellEnd})");
            }
        }
        foreach (var c in plan.Conflicts) bad.Add("conflict: " + c);
        foreach (var x in plan.AlreadyApplied) bad.Add("already on the server: " + x);
        return bad;
    }

    // ------------------------------------------------------------------ dry run

    static int DryRun(string client, string home, string run, string dump)
    {
        var ids = JsonSerializer.Deserialize<Ids>(File.ReadAllText(Path.Combine(run, "ids.json")))!;
        var present = new List<(string, long)>
        {
            ("spell_template", ids.S1), ("spell_template", ids.Learn), ("spell_template", ids.T1Spell), ("creature_template", ids.Trainer), ("creature_template", ids.Giver),
            ("quest_template", ids.QA), ("quest_template", ids.QB),
        };
        return V010.DryRun(client, home, run, dump, present, Safety);
    }

    // ------------------------------------------------------------------ the character restore, tried on a throwaway MySQL

    /// <summary>
    /// charsnap.ps1 -Restore against a throwaway MySQL: the live characters schema (mysqldump --no-data, read-only) and the rows of a
    /// snapshot of the test character are loaded; what the sessions will do is simulated (the S1 spell and the T1 rank spell learned, the two
    /// quests, the reward item in the bags, money, position, a cooldown, an aura), plus a row of ANOTHER character that must survive; then
    /// the restore runs and the snapshot after it must equal the source snapshot ("differences: 0").
    /// </summary>
    static int CharTest(string run, string snapshot)
    {
        var dir = Path.Combine(run, "chartest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var serverDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\..\Server"));
        var bin = Path.Combine(serverDir, "mysql5", "bin");
        var charsnap = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\charsnap.ps1"));
        var log = new List<string>();
        void L(string s) { Console.WriteLine(s); log.Add(s); }
        // the live schema, read-only (the harness's account; the password from the app's default settings, never printed)
        var schema = Path.Combine(dir, "characters_schema.sql");
        var live = new ServerDbSettings();
        RunExe(Path.Combine(bin, "mysqldump.exe"), new[] { "--host=127.0.0.1", "--port=3306", "-u" + live.User, "--no-data", "--skip-comments", "--skip-dump-date", $"--result-file={schema}", "characters" }, null, live.Password);
        L($"live characters schema (no rows, read-only): {new FileInfo(schema).Length:N0} bytes");
        using var tm = ThrowawayMySql.Start(ThrowawayMySql.FindMysqld(serverDir) ?? throw new InvalidOperationException("no mysqld.exe"));
        L($"throwaway MySQL on 127.0.0.1:{tm.Port}");
        string[] My(string db) => new[] { "--host=127.0.0.1", $"--port={tm.Port}", "-uroot", "--default-character-set=utf8", db };
        RunExe(Path.Combine(bin, "mysql.exe"), new[] { "--host=127.0.0.1", $"--port={tm.Port}", "-uroot", "-e", "CREATE DATABASE characters DEFAULT CHARACTER SET utf8" }, null, "");
        RunExe(Path.Combine(bin, "mysql.exe"), My("characters"), schema, "");
        RunExe(Path.Combine(bin, "mysql.exe"), My("characters"), snapshot, "");
        L($"loaded the snapshot {snapshot}");
        string Snap(string name, params string[] extra)
        {
            var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", charsnap, "-Out", Path.Combine(dir, name), "-Port", tm.Port.ToString(CultureInfo.InvariantCulture), "-NoPassword" };
            args.AddRange(extra);
            var o = RunExe("powershell.exe", args.ToArray(), null, null);
            foreach (var l in o.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0)) L("  " + l);
            return o.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
        }
        var first = Snap("t0_loaded.sql", "-Compare", snapshot);
        // what the sessions do to the character (and a row of another character, which must survive)
        var mutate = Path.Combine(dir, "mutate.sql");
        File.WriteAllText(mutate, string.Join("\n", new[]
        {
            "INSERT INTO character_spell (guid, spell, active, disabled) VALUES (1000, 900000, 1, 0), (1000, 900002, 1, 0);",
            "INSERT INTO character_queststatus (guid, quest, status, rewarded) VALUES (1000, 900000, 1, 1), (1000, 900001, 1, 1);",
            "INSERT INTO item_instance (guid, item_id, owner_guid, count, enchantments) VALUES (99999990, 4536, 1000, 5, '0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 ');",
            "INSERT INTO character_inventory (guid, bag, slot, item_guid, item_id) VALUES (1000, 0, 30, 99999990, 4536);",
            "UPDATE characters SET money = money + 100, position_x = -695, position_y = -4300, played_time_total = played_time_total + 600, logout_time = logout_time + 600 WHERE guid = 1000;",
            "INSERT INTO character_spell_cooldown (guid, spell, spell_expire_time) VALUES (1000, 900000, 1999999999);",
            "DELETE FROM character_action WHERE guid = 1000 AND button = 11;",
            "INSERT INTO character_spell (guid, spell, active, disabled) VALUES (1001, 133, 1, 0);",
        }) + "\n");
        try { RunExe(Path.Combine(bin, "mysql.exe"), My("characters"), mutate, ""); }
        catch (InvalidOperationException e) { L("a simulated change was refused by the schema (adjust mutate.sql): " + e.Message); throw; }
        var changed = Snap("t1_changed.sql", "-Compare", snapshot);
        var restored = Snap("t2_restored.sql", "-Restore", snapshot);
        var other = RunExe(Path.Combine(bin, "mysql.exe"), new[] { "--host=127.0.0.1", $"--port={tm.Port}", "-uroot", "-N", "-B", "characters", "-e", "SELECT COUNT(*) FROM character_spell WHERE guid=1001 AND spell=133" }, null, "").Trim();
        L($"loaded == snapshot: {first}; after the simulated sessions: {changed}; after the restore: {restored}; the other character's row kept: {other == "1"}");
        bool ok = first == "differences: 0" && changed != "differences: 0" && restored == "differences: 0" && other == "1";
        L(ok ? "RESULT: charsnap.ps1 -Restore puts the character's rows back exactly and leaves other characters' rows alone" : "RESULT: the restore test FAILED");
        File.WriteAllLines(Path.Combine(dir, "chartest_report.txt"), log);
        return ok ? 0 : 1;
    }

    static string RunExe(string exe, string[] args, string? stdinFile, string? password)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdinFile != null, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (password != null) psi.Environment["MYSQL_PWD"] = password;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        if (stdinFile != null)
        {
            using (var fs = File.OpenRead(stdinFile)) fs.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.Close();
        }
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {err.Result.Trim()} {o.Trim()}");
        return o;
    }

    // ------------------------------------------------------------------ viewpoints and the scenarios

    /// <summary>sessions_designers.json: the viewpoints (the hunting spot P on the ground, from the game's map; facing north).</summary>
    static void WriteViewpoints(string run)
    {
        var (d, sp) = NewDesign();
        var qd = new QuestDesigner(d, sp);
        double z = (qd.GroundHeight(1, PX, PY) ?? throw new InvalidOperationException("no ground at the hunting spot")) + 0.5;
        string pos = string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.000}", PX, PY, z, 0.0);
        var vps = new[]
        {
            new { key = "q1", lua = "d_quest.lua", pos, camera = "9,12", stay = 230, why = "Q1: the giver's '!', the quest texts ($N / $c), the kills tracked, turn-in, the follow-up, the rewards (S1 learned, apples)" },
            new { key = "t1", lua = "d_trainer.lua", pos, camera = "6,10", stay = 170, why = "S1: the test trainer lists the spell (name, icon, cost); bought; the spell book and tooltip" },
            new { key = "c1", lua = "d_cast.lua", pos = BoarView, camera = "10,14", stay = 190, why = "S1: cast on Mottled Boars: the green x1.5 Fireball-shaped missile and impact (frame series), the damage in the combat log" },
            new { key = "k1", lua = "d_talent.lua", pos, camera = "6,10", stay = 150, why = "T1: the Destruction tree: the new talent at tier 1 column 1, Shadowburn moved up, the tooltips" },
        };
        File.WriteAllText(Path.Combine(run, "sessions_designers.json"), JsonSerializer.Serialize(vps, J));
        Console.WriteLine($"viewpoints: {Path.Combine(run, "sessions_designers.json")} (P on the ground at z {z - 0.5:0.##})");
    }

    /// <summary>2026-10-16: the scenarios (and the viewpoints' boar position for c1) again, from RUNDIR\ids.json (no app step).</summary>
    static int Scenarios(string client, string home, string run)
    {
        _ids = JsonSerializer.Deserialize<Ids>(File.ReadAllText(Path.Combine(run, "ids.json"))) ?? throw new InvalidOperationException("no ids.json");
        _s = V010.Open(client, home);
        WriteLua(run);
        _s.Dispose();
        var path = Path.Combine(run, "sessions_designers.json");
        var vps = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var vp in vps)
            if ((string?)vp!["key"] == "c1") vp["pos"] = BoarView;
        File.WriteAllText(path, vps.ToJsonString(J));
        Console.WriteLine($"viewpoints: {path} (c1 at the boars' viewpoint {BoarView})");
        return 0;
    }

    /// <summary>
    /// 2026-10-16 (0.13.2): the talent designer's Save on the prepared workspace, as a user of 0.13.2 does with a Talent.dbc
    /// saved by 0.12.0-0.13.1: Check of the tree (the warning), Save (the rows of every tree together again), the result.
    /// </summary>
    static int Regroup(string client, string home, string run)
    {
        _run = run;
        _s = V010.Open(client, home);
        var (d, sp) = NewDesign();
        var td = new TalentDesigner(d, sp);
        string Runs()
        {
            var t = d.Table("Talent");
            var col = t.Layout.Column("TabID")!;
            var idx = t.Rows.Select((r, i) => (Tab: t.Number(r, col), i)).Where(x => x.Tab == TalentTree).Select(x => x.i).ToList();
            return $"tree {TalentTree} rows at {string.Join(",", idx)} of {t.Rows.Count}";
        }
        Console.WriteLine("before: " + Runs() + "; split trees: " + string.Join("; ", td.SplitTrees().Select(x => $"{x.Tab}: {x.Runs} groups, the game shows {x.Shown} of {x.Total}")));
        foreach (var p in td.Check(TalentTree).Where(p => p.Field == "Talents")) Console.WriteLine("check: " + p.Text);
        var res = td.Save("Talent designer (0.13.2): trees together");
        Console.WriteLine("save: " + res.Message);
        Console.WriteLine("after: " + Runs() + "; split trees: " + td.SplitTrees().Count);
        Console.WriteLine(td.Grid(TalentTree));
        bool ok = td.SplitTrees().Count == 0;
        _s.Dispose();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 2026-10-16: run 2 found that the quest reward did not teach S1 (the character got 900000 and 900001 only as disabled
    /// spells). The learn spell made by 0.12.0-0.13.1 differs from the game's own learn spells in effect 1's target
    /// ("Unit" 25 instead of none) and its range ("self" instead of 100 yards). This sets those two with the spell
    /// designer (the user's way: Effect 1 > Target, Range) and saves (the server's row follows), for run 3.
    /// </summary>
    static int LearnFix(string client, string home, string run)
    {
        _run = run;
        _ids = JsonSerializer.Deserialize<Ids>(File.ReadAllText(Path.Combine(run, "ids.json"))) ?? throw new InvalidOperationException("no ids.json");
        _s = V010.Open(client, home);
        var (d, sp) = NewDesign();
        long l = _ids.Learn;
        Console.WriteLine($"before: effect1-target-a {sp.Show(l, "effect1-target-a")}, range {sp.Show(l, "range")}, description [{sp.Show(l, "description")}]");
        foreach (var (f, v) in new[] { ("effect1-target-a", "0"), ("range", "100"), ("description", "") })
        {
            var r = sp.Set(l, f, v);
            Console.WriteLine($"set {f} = {v}: {r.Message}");
            if (!r.Ok) { _s.Dispose(); return 1; }
        }
        foreach (var p in sp.Check(l)) Console.WriteLine("check: " + p.Text);
        var res = sp.Save("Spell designer: learn spell like the game's own (target none, 100 yards)");
        Console.WriteLine("save: " + res.Message);
        Console.WriteLine($"after: effect1-target-a {sp.Show(l, "effect1-target-a")}, range {sp.Show(l, "range")}");
        _s.Dispose();
        return 0;
    }

    /// <summary>The scenarios with this run's IDs and names filled in (tests\ingame\lua\designers\*.lua -> RUNDIR\lua\).</summary>
    static void WriteLua(string run)
    {
        var dir = Path.Combine(run, "lua");
        Directory.CreateDirectory(dir);
        var tokens = new Dictionary<string, string>
        {
            ["__S1__"] = _ids.S1.ToString(CultureInfo.InvariantCulture), ["__S1NAME__"] = S1Name, ["__LEARN__"] = _ids.Learn.ToString(CultureInfo.InvariantCulture),
            ["__T1SPELL__"] = _ids.T1Spell.ToString(CultureInfo.InvariantCulture), ["__T1NAME__"] = T1Name, ["__T1TALENT__"] = _ids.T1Talent.ToString(CultureInfo.InvariantCulture),
            ["__QA__"] = _ids.QA.ToString(CultureInfo.InvariantCulture), ["__QB__"] = _ids.QB.ToString(CultureInfo.InvariantCulture),
            ["__QATITLE__"] = QATitle, ["__QBTITLE__"] = QBTitle, ["__GIVER__"] = GiverName, ["__TRAINER__"] = TrainerName,
            ["__ITEM__"] = RewardItem.ToString(CultureInfo.InvariantCulture), ["__ITEMCOUNT__"] = RewardCount.ToString(CultureInfo.InvariantCulture),
            ["__COST__"] = TrainerCost.ToString(CultureInfo.InvariantCulture),
        };
        // 2026-10-16: P (".go xyzo" back to the giver) and the three boar viewpoints
        var (vd, vsp) = NewDesign();
        var qd = new QuestDesigner(vd, vsp);
        tokens["__P__"] = GoView(qd, PX, PY, 0.0);
        for (int i = 0; i < BoarViews.Length; i++) tokens[$"__B{i + 1}__"] = GoView(qd, BoarViews[i].X, BoarViews[i].Y, BoarViews[i].O);
        foreach (var k in new[] { "__P__", "__B1__", "__B2__", "__B3__" }) Console.WriteLine($"  {k} = .go xyzo {tokens[k]}");
        foreach (var f in Directory.EnumerateFiles(LuaDir, "*.lua"))
        {
            var text = File.ReadAllText(f);
            foreach (var (k, v) in tokens) text = text.Replace(k, v);
            if (text.Contains("__", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(text, @"__[A-Z0-9]+__"))
                throw new InvalidOperationException($"{Path.GetFileName(f)}: a token was not filled in");
            File.WriteAllText(Path.Combine(dir, Path.GetFileName(f)), text, new UTF8Encoding(false));
        }
        Console.WriteLine($"scenarios: {dir}");
    }
}
