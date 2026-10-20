using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.Dbc;
using WoWData.Core.Designers;
using WoWData.Core.M2;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.12.0: the spell designer and the class and talent designer in Core, on the real client's tables (read only) and a
/// temporary workspace (no server here; the server side is DesignerServerTests): the tooltip variables as the client
/// fills them in, the form's friendly units, new and cloned spells, the checks, visuals (compose, duplicate with tint and
/// size), learning rows, talent tree validity (the stock trees, and every rule broken on purpose), class views, class
/// masks, a new class's data, the engine report, and CSV / JSON round trips of the designers' rows.
/// </summary>
public class DesignerTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("designer");
    private readonly List<(string, long, DateTime)> _client = TestEnv.DataSnapshot();

    public void Dispose()
    {
        Assert.Equal(_client, TestEnv.DataSnapshot());   // the real game folder was only read
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private (Workspace Ws, DesignSession S, SpellDesigner D) Open()
    {
        var fake = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fake, "Data"));
        var paths = new AppPaths(Path.Combine(_root, "home")).ForClient(fake);
        var ws = new Workspace(paths);
        var s = new DesignSession(new DbcService(TestEnv.Client, ws), null, (900000, 999999));
        return (ws, s, new SpellDesigner(s));
    }

    // ------------------------------------------------------------------ tooltip substitution

    [Fact]
    public void TooltipsReadLikeTheGame()
    {
        var (_, s, d) = Open();
        // the game's own texts (1.12 tooltips): Shadow Bolt rank 1 at level 1, Corruption, Renew
        Assert.Equal("Sends a shadowy bolt at the enemy, causing 12 to 16 Shadow damage.", d.Tooltip(686, 1).Text);
        Assert.Equal("Corrupts the target, causing 40 Shadow damage over 12 sec.", d.Tooltip(172).Text);
        Assert.Equal("Heals the target of 45 damage over 15 sec.", d.Tooltip(139).Text);
        // per level: Shadow Bolt grows 0.4 per level up to its max level 5: 12-16 at 1, 13.6-17.6 at 5 ("13 to 18")
        Assert.Equal("Sends a shadowy bolt at the enemy, causing 13 to 18 Shadow damage.", d.Tooltip(686, 5).Text);
        var t = d.Spells;
        var row = t.Find(686)!;
        string X(string text, int level = 1) => SpellText.Expand(text, t, row, s.Lookups, level).Text;
        Assert.Equal("12", X("$m1"));
        Assert.Equal("16", X("$M1"));
        Assert.Equal("30.0", X("$r"));                                 // "%.1f", as the client prints it
        Assert.Equal("6 to 8", X("$/2;s1"));                           // a scale: half
        Assert.Equal("40", X("$172o1"));                               // another spell's value
        Assert.Equal("Corruption 12 sec", X("Corruption $172d"));
        Assert.Equal("12 points", X("$m1 $lpoint:points;"));
        Assert.Equal("him", X("$ghim:her;"));
        var bad = SpellText.Expand("Deals $k1 damage", t, row, s.Lookups, 1);
        Assert.Equal("Deals $1 damage", bad.Text);                     // the client prints a bare "$" and goes on
        Assert.Single(bad.Problems);
        Assert.Contains("$99999999s1", SpellText.Expand("$99999999s1", t, row, s.Lookups, 1).Problems.Single().Token);
        Assert.Equal("until cancelled", SpellText.FormatDuration(-1));
        Assert.Equal("30 sec", SpellText.FormatDuration(30000));
        Assert.Equal("1.50 min", SpellText.FormatDuration(90000));
        Assert.Equal("2 min", SpellText.FormatDuration(120000));
        Assert.Equal("1 hour", SpellText.FormatDuration(3600000));
        Assert.Equal("2 hrs", SpellText.FormatDuration(7200000));
    }

    // ------------------------------------------------------------------ the form

    [Fact]
    public void TheFormTakesFriendlyUnits()
    {
        var (_, s, d) = Open();
        long id = d.Clone(686, "Shadow Lance");
        Assert.Equal(35000, id);   // 0.13.3: the first free ID of the spell ID range (VMaNGOS casts no spell above 40000)
        var t = d.Spells;
        var row = t.Find(id)!;
        long N(string c) => t.Number(row, t.Layout.Column(c)!);
        Assert.True(d.Set(id, "cast-time", "2").Ok);
        Assert.Equal(2000, s.Table("SpellCastTimes").Number(s.Table("SpellCastTimes").Find(N("CastingTimeIndex"))!, s.Table("SpellCastTimes").Layout.Column("Base")!));
        Assert.True(d.Set(id, "cast-time", "instant").Ok);
        Assert.Equal(1, N("CastingTimeIndex"));
        Assert.True(d.Set(id, "cooldown", "1.5 min").Ok);
        Assert.Equal(90000, N("RecoveryTime"));
        Assert.True(d.Set(id, "range", "30").Ok);
        Assert.Equal(30f, s.Table("SpellRange").Float(s.Table("SpellRange").Find(N("RangeIndex"))!, s.Table("SpellRange").Layout.Column("RangeMax")!));
        Assert.True(d.Set(id, "range", "self").Ok);
        Assert.Equal(1, N("RangeIndex"));
        // a value no row has: a new row is added (and named)
        var castRows = s.Table("SpellCastTimes").Rows.Count;
        var r = d.Set(id, "cast-time", "2.37");
        Assert.True(r.Ok && r.Message.Contains("Added SpellCastTimes row"), r.Message);
        Assert.Equal(castRows + 1, s.Table("SpellCastTimes").Rows.Count);
        Assert.True(d.Set(id, "duration", "until cancelled").Ok);
        Assert.Equal(-1, s.Table("SpellDuration").Number(s.Table("SpellDuration").Find(N("DurationIndex"))!, s.Table("SpellDuration").Layout.Column("Duration")!));
        Assert.True(d.Set(id, "duration", "none").Ok);
        Assert.Equal(0, N("DurationIndex"));
        Assert.True(d.Set(id, "effect1-value", "40-48").Ok);
        Assert.Equal((39, 1, 9), (N("EffectBasePoints1"), N("EffectBaseDice1"), N("EffectDieSides1")));
        Assert.Equal("40-48", d.Show(id, "effect1-value"));
        Assert.True(d.Set(id, "icon", "Spell_Shadow_ShadowBolt").Ok);
        Assert.Equal(213, N("SpellIconID"));
        Assert.False(d.Set(id, "icon", "No_Such_Icon_Anywhere").Ok);
        Assert.True(d.Set(id, "school", "Fire").Ok);
        Assert.Equal(2, N("School"));
        Assert.True(d.Set(id, "attributes", "+Passive").Ok);
        Assert.Equal(0x10040, N("Attributes") & 0xFFFFFFFF);
        Assert.True(d.Set(id, "attributes", "-Passive").Ok);
        Assert.Equal(0x10000, N("Attributes") & 0xFFFFFFFF);
        Assert.True(d.Set(id, "power-type", "Rage").Ok);
        Assert.True(d.Set(id, "cost", "15").Ok);
        Assert.Equal(150, N("ManaCost"));                               // rage x10
        Assert.Equal("15 rage", d.Show(id, "cost"));
        Assert.False(d.Set(id, "school", "Banana").Ok);                 // refused: nothing changes
        Assert.Equal(2, N("School"));
        Assert.True(d.Set(id, "server-bonus1", "0.857").Ok);
        Assert.Equal("0.857", d.ServerExtras(id)["effectBonusCoefficient1"]);
        Assert.Throws<DesignException>(() => SpellForm.Require("no-such-field"));
        Assert.All(SpellForm.Fields.Where(f => f.Kind != SpellFieldKind.ServerOnly), f => Assert.NotNull(t.Layout.Column(f.Column)));
    }

    // ------------------------------------------------------------------ new, clone, save

    [Fact]
    public void NewAndClonedSpellsSaveIntoOneChangeSet()
    {
        var (ws, s, d) = Open();
        long a = d.New("Shadow Lance", rank: "Rank 1");
        long b = d.Clone(686, "Shadow Lance", rank: "Rank 2");
        Assert.Equal((35000, 35001), (a, b));
        Assert.Throws<DesignException>(() => d.New("Twice", a));
        Assert.True(d.Set(a, "effect1", "School damage").Ok);
        Assert.True(d.Set(a, "effect1-target-a", "Unit enemy").Ok);
        Assert.True(d.Set(a, "effect1-value", "20-30").Ok);
        Assert.True(d.Set(a, "description", "Deals $s1 Shadow damage.").Ok);
        Assert.Empty(d.Check(a).Where(p => p.IsError));
        var res = d.Save();
        Assert.Contains("Spell", res.ClientTables);
        Assert.True(res.ServerSkipped);
        var rec = ws.Get(@"DBFilesClient\Spell.dbc")!;
        Assert.StartsWith("Spell designer: ", ws.Manifest.ChangeSets[rec.ChangeSet].Label);
        // a fresh session reads them back from the workspace
        var (_, s2, d2) = (ws, new DesignSession(new DbcService(TestEnv.Client, ws), null, (900000, 999999)), (SpellDesigner?)null);
        d2 = new SpellDesigner(s2);
        Assert.Equal("Shadow Lance (Rank 1) [35000]", d2.Title(35000));
        Assert.Equal("Deals 20 to 30 Shadow damage.", d2.Tooltip(35000).Text);
        Assert.Equal(d.Spells.Write(), d2.Spells.Write());
        Assert.Equal(35002, s2.FreeSpellId());
    }

    // ------------------------------------------------------------------ 0.13.3: spell IDs the server casts

    /// <summary>VMaNGOS casts no spell above 40000 (MAX_SPELL_ID) and the 1.12 game knows none above 65535: new spells
    /// come from the spell ID range, other IDs are refused, old ones are errors and "Fix all spell IDs" moves them with
    /// their references (learn spell trigger, skill line, talent rank).</summary>
    [Fact]
    public void NewSpellsGetIdsTheServerCasts()
    {
        var (_, s, d) = Open();
        Assert.Equal((35000L, 39999L), s.SpellIds);
        Assert.Equal(35000, d.New("A"));
        Assert.Equal(35001, d.Clone(686, "B"));
        var e = Assert.Throws<DesignException>(() => d.New("C", 900000));
        Assert.Contains("MAX_SPELL_ID 40000", e.Message);
        Assert.Contains("48032", e.Message);                         // 900000 & 0xFFFF: what the 1.12 game would read
        Assert.Throws<DesignException>(() => d.Clone(686, "D", 40001));
        Assert.Equal(40000, d.New("E", 40000));                      // 40000 itself is cast (Id > MAX_SPELL_ID returns)
        // the setting: a range up to 40000
        var set = new AppSettings();
        Assert.Equal("35000-39999", SettingsCatalog.Get(set, "spell-ids"));
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Set(set, "spell-ids", "35000-50000"));
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Set(set, "spell-ids", "banana"));
        SettingsCatalog.Set(set, "spell-ids", "36000-36009");
        Assert.Equal((36000L, 36009L), (set.SpellIdStart, set.SpellIdEnd));
        SettingsCatalog.Unset(set, "spell-ids");
        Assert.Equal(35000, set.SpellIdStart);
        var s2 = new DesignSession(s.Dbc, null, (900000, 999999), null, (36000, 36001));
        var d2 = new SpellDesigner(s2);
        Assert.Equal(36000, d2.New("F"));
        Assert.Equal(36001, d2.New("G"));
        Assert.Contains("is full", Assert.Throws<DesignException>(() => d2.New("H")).Message);

        // a spell of 0.12.0-0.13.2 (900000), its learn spell (900001), a skill line and a talent rank naming it
        var (_, s3, d3) = Open();
        var ss = s3.Session("Spell");
        ss.Duplicate(d3.Row(686), 900000);
        var (lr, _) = ss.Duplicate(d3.Row(1381), 900001);
        s3.SetNumber("Spell", lr, "EffectTriggerSpell1", 900000);
        var (sr, _) = s3.Session("SkillLineAbility").AddRow(s3.NextId("SkillLineAbility", max: 65535));
        s3.SetNumber("SkillLineAbility", sr, "Spell", 900000);
        s3.SetNumber("SkillLineAbility", sr, "SkillLine", 593);
        var tal = s3.Table("Talent");
        var trow = tal.Rows.First(r => tal.Number(r, tal.Layout.Column("SpellRank1")!) != 0);
        s3.SetNumber("Talent", trow, "SpellRank1", 900000);
        Assert.Equal(new List<long> { 900000, 900001 }, d3.UnusableSpells());
        Assert.Contains(d3.Check(900000), p => p.IsError && p.Field == "ID");
        var r = d3.ChangeIds();
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Talent", r.Message);
        Assert.Equal((35000L, 35001L), (d3.Moved[900000], d3.Moved[900001]));
        Assert.Equal(35000, d3.Spells.Number(d3.Row(35001), d3.Spells.Layout.Column("EffectTriggerSpell1")!));
        Assert.Equal(35000, s3.Table("SkillLineAbility").Number(sr, s3.Table("SkillLineAbility").Layout.Column("Spell")!));
        Assert.Equal(35000, tal.Number(trow, tal.Layout.Column("SpellRank1")!));
        Assert.Null(d3.Spells.Find(900000));
        Assert.Null(d3.Spells.Find(900001));
        Assert.Empty(d3.UnusableSpells());
        Assert.True(d3.ChangeIds().Ok);                               // nothing left: says so
        Assert.False(d3.ChangeId(686).Ok);                             // the game's own spells stay
        var moved = d3.ChangeId(35000, 35500, withLearnSpells: false);
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(35500, d3.Spells.Number(d3.Row(35001), d3.Spells.Layout.Column("EffectTriggerSpell1")!));
        Assert.Equal(35500, d3.Moved[900000]);
    }

    // ------------------------------------------------------------------ checks

    [Fact]
    public void ChecksExplainWhatTheGameAndServerCannotHandle()
    {
        var (_, s, d) = Open();
        long id = d.New("Broken");
        List<DesignProblem> P() => d.Check(id);
        bool Has(DesignSeverity sev, string text) => P().Any(p => p.Severity == sev && p.Message.Contains(text, StringComparison.Ordinal));
        Assert.True(Has(DesignSeverity.Warning, "has no effect"));
        d.Set(id, "effect1", "Apply aura");
        Assert.True(Has(DesignSeverity.Error, "needs an aura type"));
        d.Set(id, "effect1-aura", "Periodic damage");
        Assert.True(Has(DesignSeverity.Error, "\"Tick every\" is 0"));
        Assert.True(Has(DesignSeverity.Warning, "no duration"));
        d.Set(id, "effect1-period", "3");
        d.Set(id, "duration", "12");
        Assert.False(Has(DesignSeverity.Error, "Tick every"));
        d.Set(id, "effect1-aura", "Proc trigger spell");
        Assert.True(Has(DesignSeverity.Error, "\"Proc on\" is empty"));
        Assert.True(Has(DesignSeverity.Error, "needs a trigger spell"));
        d.Set(id, "effect1-trigger", "133");
        d.Set(id, "proc-flags", "Deal melee swing");
        d.Set(id, "proc-chance", "10");
        Assert.DoesNotContain(P(), p => p.IsError && p.Field == "Effect 1");
        d.Set(id, "effect1-aura", "Mod parry skill");                     // empty handler on the 1.12 server
        Assert.True(Has(DesignSeverity.Error, "does nothing with it"));
        d.Set(id, "effect1", "Dummy");
        Assert.True(Has(DesignSeverity.Warning, "unless a script is written"));
        Assert.True(Has(DesignSeverity.Warning, "aura type is ignored"));
        d.Set(id, "effect1", "Stealth");                                  // EffectUnused on the server
        Assert.True(Has(DesignSeverity.Error, "does nothing with it"));
        d.Set(id, "effect1", "Summon");
        Assert.True(Has(DesignSeverity.Error, "needs the creature to summon"));
        d.Set(id, "effect1", "School damage");
        d.Set(id, "effect1-target-a", "Enum units enemy aoe at src loc");
        Assert.True(Has(DesignSeverity.Error, "needs a radius"));
        d.Set(id, "effect1-radius", "8");
        Assert.False(Has(DesignSeverity.Error, "needs a radius"));
        d.Set(id, "description", "Deals $s1 damage and $k2 more.");
        Assert.True(Has(DesignSeverity.Warning, "$k"));
        Assert.Equal(SpellRules.MaxEffect, SpellRules.AllEffects().Last().Id);
        Assert.Equal(ServerSupport.Unknown, SpellRules.Effect(131).Server);
        Assert.Equal(ServerSupport.DoesNothing, SpellRules.Aura(46).Server);
        Assert.Equal(ServerSupport.NeedsScript, SpellRules.Effect(77).Server);
        Assert.All(SpellRules.AllEffects().Concat(SpellRules.AllAuras()), x => Assert.False(string.IsNullOrWhiteSpace(x.Explanation)));
        // the save refuses errors
        d.Set(id, "effect2", "Apply aura");
        Assert.Throws<DesignException>(() => d.Save());
        // a stock spell has no errors
        Assert.DoesNotContain(d.Check(686), p => p.IsError);
        Assert.DoesNotContain(d.Check(133), p => p.IsError);
    }

    // ------------------------------------------------------------------ visuals

    [Fact]
    public void VisualsAreComposedAndDuplicatedWithTintAndSize()
    {
        var (ws, s, d) = Open();
        long id = d.Clone(686, "Shadow Lance");
        var vt = s.Table("SpellVisual");
        long fireballVisual = d.Spells.Number(d.Row(133), d.Spells.Layout.Column("SpellVisual")!);
        var msg = SpellVisuals.Compose(s, d, id, new Dictionary<string, string> { ["missile"] = "spell:133", ["impact"] = "visual:" + fireballVisual });
        Assert.Contains("Made SpellVisual", msg);       // visual 64 is shared with Shadow Bolt: a copy
        long own = d.Spells.Number(d.Row(id), d.Spells.Layout.Column("SpellVisual")!);
        Assert.NotEqual(64, own);
        var info = SpellVisuals.Describe(s, own);
        long V(long v, string c) => vt.Number(vt.Find(v)!, vt.Layout.Column(c)!);
        Assert.Equal(V(fireballVisual, "MissileModel"), V(own, "MissileModel"));
        Assert.Equal(V(fireballVisual, "ImpactKit"), V(own, "ImpactKit"));
        Assert.Equal(V(64, "CastKit"), V(own, "CastKit"));
        Assert.Contains($"Shadow Lance [{id}]", info.UsedBy.Single());
        // its own visual now: a second change happens in place
        Assert.Contains("Changed SpellVisual", SpellVisuals.Compose(s, d, id, new Dictionary<string, string> { ["precast"] = "none" }));
        Assert.Equal(0, V(own, "PrecastKit"));

        // duplicate: own kits and effect models, x1.5, tinted
        var missileName = s.Table("SpellVisualEffectName").Find(V(own, "MissileModel"))!;
        var missileFile = Path.ChangeExtension(s.Table("SpellVisualEffectName").Text(missileName, s.Table("SpellVisualEffectName").Layout.Column("FileName")!), ".m2");
        var dup = SpellVisuals.Duplicate(s, d, id, 1.5f, new Vector3(0.5f, 0.25f, 1f), "T1");
        Assert.Contains("size x1.5", dup);
        long copy = d.Spells.Number(d.Row(id), d.Spells.Layout.Column("SpellVisual")!);
        Assert.NotEqual(own, copy);
        var copyMissile = s.Table("SpellVisualEffectName").Find(V(copy, "MissileModel"))!;
        var copyFile = s.Table("SpellVisualEffectName").Text(copyMissile, s.Table("SpellVisualEffectName").Layout.Column("FileName")!);
        Assert.StartsWith(@"Spells\WDS\", copyFile);
        var res = d.Save();
        Assert.True(res.Files.Count >= 2, res.Message);
        var orig = M2File.Parse(TestEnv.Client.Read(missileFile)!);
        var made = M2File.Parse(ws.ReadEdited(Path.ChangeExtension(copyFile, ".m2"))!);
        Assert.Empty(Workspace.ModelProblems(ws.ReadEdited(Path.ChangeExtension(copyFile, ".m2"))!));
        static Vector3[] Positions(M2Data v) => Enumerable.Range(0, v.Count).Select(i => new Vector3(
            BitConverter.ToSingle(v.Bytes, i * v.Elem.Size), BitConverter.ToSingle(v.Bytes, i * v.Elem.Size + 4), BitConverter.ToSingle(v.Bytes, i * v.Elem.Size + 8))).ToArray();
        var ov = Positions(orig["vertices"]);
        var mv = Positions(made["vertices"]);
        Assert.Equal(ov.Length, mv.Length);
        for (int i = 0; i < ov.Length; i += Math.Max(1, ov.Length / 20)) Assert.True(Vector3.Distance(ov[i] * 1.5f, mv[i]) < 1e-3f);
        Assert.Equal(orig.Header.F32(0, "bounds_radius") * 1.5f, made.Header.F32(0, "bounds_radius"), 3);
        // every texture of the copy is a tinted copy under Spells\WDS\, the originals untouched
        var texs = Enumerable.Range(0, made["textures"].Count).Select(i => made["textures"].Child(i, "filename").AsString()).Where(n => n.Length > 0).ToList();
        Assert.All(texs, n => Assert.StartsWith(@"Spells\WDS\", n));
        Assert.All(texs, n => Assert.True(ws.IsModified(n), n));
        Assert.Equal(64, d.Spells.Number(d.Row(686), d.Spells.Layout.Column("SpellVisual")!));   // Shadow Bolt keeps its look
        Assert.Throws<DesignException>(() => SpellVisuals.ParseTint("purple"));
        Assert.Equal(new Vector3(0.5019608f, 0.2509804f, 1f), SpellVisuals.ParseTint("#8040FF"));
    }

    // ------------------------------------------------------------------ learning (client side)

    [Fact]
    public void SkillLinesAndLearnSpells()
    {
        var (_, s, d) = Open();
        long id = d.Clone(686, "Shadow Lance");
        var r = d.AddSkillLine(id, 593, 1L << 8);   // Destruction, Warlock
        Assert.True(r.Ok, r.Message);
        var sl = d.SkillLines(id).Single();
        Assert.Equal((593L, 0x100L), (sl.SkillLine, sl.ClassMask));
        Assert.True(sl.Id > 0 && sl.Id <= 65535);
        Assert.False(d.AddSkillLine(id, 999999, 0).Ok);
        long learn = d.EnsureLearnSpell(id, out bool created);
        Assert.True(created);
        var t = d.Spells;
        Assert.Equal(36, t.Number(d.Row(learn), t.Layout.Column("Effect1")!));
        Assert.Equal(id, t.Number(d.Row(learn), t.Layout.Column("EffectTriggerSpell1")!));
        Assert.Equal("Shadow Lance", d.Name(learn));
        // 0.13.2 (found in game): like the game's own learn spells (a copy of Shadow Bolt (Rank 2)'s 1381): effect 1 has no
        // implicit target, the range is not "self", no description, the learn glow; the icon is the taught spell's
        Assert.Equal(0, t.Number(d.Row(learn), t.Layout.Column("EffectImplicitTargetA1")!));
        Assert.Equal(t.Number(d.Row(1381), t.Layout.Column("RangeIndex")!), t.Number(d.Row(learn), t.Layout.Column("RangeIndex")!));
        Assert.NotEqual(1, t.Number(d.Row(learn), t.Layout.Column("RangeIndex")!));
        Assert.Equal(t.Number(d.Row(1381), t.Layout.Column("SpellVisual")!), t.Number(d.Row(learn), t.Layout.Column("SpellVisual")!));
        Assert.Equal(t.Number(d.Row(686), t.Layout.Column("SpellIconID")!), t.Number(d.Row(learn), t.Layout.Column("SpellIconID")!));
        Assert.Equal("", t.Text(d.Row(learn), t.Layout.Column("Description")!, 0));
        Assert.DoesNotContain(d.Check(learn), p => p.IsError || p.Field == "Description" || p.Field == "Effect 1");
        Assert.Equal(learn, d.EnsureLearnSpell(id, out created));
        Assert.False(created);
        Assert.False(d.AddToTrainer(id, "3156", 20, 2500).Ok);   // trainers need the server database
        Assert.Equal(2500, SpellDesigner.ParseMoney("25s"));
        Assert.Equal(15025, SpellDesigner.ParseMoney("1g 50s 25c"));
        Assert.Equal("1g 50s 25c", SpellDesigner.Money(15025));
        Assert.True(d.RemoveSkillLine(id, 593).Ok);
        Assert.Empty(d.SkillLines(id));
    }

    // ------------------------------------------------------------------ talent trees

    [Fact]
    public void TheStockTalentTreesAreValid()
    {
        var (_, s, d) = Open();
        var t = new TalentDesigner(s, d);
        int trees = 0;
        foreach (long cls in new long[] { 1, 2, 3, 4, 5, 7, 8, 9, 11 })
            foreach (var tree in t.Trees(cls))
            {
                trees++;
                var errors = t.Check(tree.Id).Where(p => p.IsError).ToList();
                Assert.True(errors.Count == 0, $"{tree.Name}: " + string.Join("; ", errors.Select(e => e.Text)));
                Assert.InRange(tree.Talents.Count, 10, TalentDesigner.MaxTalents);
            }
        Assert.Equal(27, trees);
        Assert.Contains("Mortal Strike 0/1 ^133", t.Grid(161));
    }

    /// <summary>The 1.12 client's reading of Talent.dbc (TalentInfo_Initialize @004f2c00): each time the tree ID changes
    /// from one row to the next, the tree starts again at that row, so a tree shows only its LAST run of rows.</summary>
    private static int TalentsTheGameShows(DbcTable t, long tab)
    {
        var col = t.Layout.Column("TabID")!;
        int shown = 0;
        long prev = -1;
        foreach (var r in t.Rows)
        {
            long cur = t.Number(r, col);
            if (cur == prev) { if (cur == tab) shown++; }
            else if (cur == tab) shown = 1;
            prev = cur;
        }
        return shown;
    }

    [Fact]
    public void ANewTalentStaysWithItsTreeAsTheGameNeeds()
    {
        // 0.13.2 (found in game, 0.13.1): a talent added to the Warlock's Destruction tree (301) went to the end of
        // Talent.dbc (the next free ID), and the game then showed ONLY that talent in the tree
        var (ws, s, d) = Open();
        var t = new TalentDesigner(s, d);
        var talents = s.Table("Talent");
        Assert.Equal(16, TalentsTheGameShows(talents, 301));
        Assert.Empty(t.SplitTrees());   // the game's own file: every tree is one run of rows
        long tal = t.AddTalent(301, 0, 0, t.MakeRankSpells(17778, "Test Talent", 1));
        Assert.Empty(t.SplitTrees());
        Assert.Equal(17, TalentsTheGameShows(talents, 301));
        var col = talents.Layout.Column("TabID")!;
        int at = talents.Rows.IndexOf(talents.Find(tal)!);
        Assert.Equal(301, talents.Number(talents.Rows[at - 1], col));
        Assert.NotEqual(301, talents.Number(talents.Rows[at + 1], col));
        Assert.True(s.Session("Talent").Editor.Undo());   // the move is one undo step: back at the end of the file
        Assert.Equal(1, TalentsTheGameShows(talents, 301));
        Assert.True(s.Session("Talent").Editor.Redo());
        Assert.Equal(17, TalentsTheGameShows(talents, 301));
        t.Save();
        var saved = new DbcEditSession(new DbcService(TestEnv.Client, ws), "Talent").Table;
        Assert.Equal(17, TalentsTheGameShows(saved, 301));
        Assert.Equal(talents.Rows.Count, saved.Rows.Count);
    }

    [Fact]
    public void SavingRegroupsATreeSplitByAnOlderVersion()
    {
        // a Talent.dbc saved by 0.12.0-0.13.1 (or edited row by row in the Data tables tab): a Destruction row at the end
        var (ws, s, d) = Open();
        var t = new TalentDesigner(s, d);
        var ranks = t.MakeRankSpells(17778, "Test Talent", 1);
        var row = s.AddRow("Talent");
        s.SetNumber("Talent", row, "TabID", 301);
        s.SetNumber("Talent", row, "SpellRank1", ranks[0]);
        var talents = s.Table("Talent");
        Assert.Equal(1, TalentsTheGameShows(talents, 301));
        Assert.Equal((301L, 2, 1, 17), t.SplitTrees().Single());
        Assert.Contains(t.Check(301), p => p.Severity == DesignSeverity.Warning && p.Message.Contains("only the last group (1 of 17 talents)"));
        var res = t.Save();
        Assert.Contains(res.Notes, n => n.Contains("1 row(s) moved"));
        Assert.Empty(t.SplitTrees());
        var saved = new DbcEditSession(new DbcService(TestEnv.Client, ws), "Talent").Table;
        Assert.Equal(17, TalentsTheGameShows(saved, 301));
        var game = s.Session("Talent").GameTable!;   // every other tree as in the game's own file
        var col = game.Layout.Column("TabID")!;
        foreach (var tab in game.Rows.Select(r => game.Number(r, col)).Distinct())
            Assert.Equal(TalentsTheGameShows(game, tab) + (tab == 301 ? 1 : 0), TalentsTheGameShows(saved, tab));
    }

    [Fact]
    public void TalentTreeRulesAreEnforced()
    {
        var (_, s, d) = Open();
        var t = new TalentDesigner(s, d);
        long tab = t.AddTree(1, "Stance Disciplines", "Ability_Warrior_DefensiveStance", "WarriorProtection");
        Assert.Equal(3, t.Tree(tab).Order);
        var ranks = t.MakeRankSpells(12295, "Riposte Drill", 1);
        long a = t.AddTalent(tab, 0, 0, ranks);
        var five = t.MakeRankSpells(12295, "Iron Line", 5, new[] { "1", "2", "3", "4", "5" });
        Assert.Equal(new[] { "Rank 1", "Rank 5" }, new[] { five[0], five[4] }.Select(x => d.Spells.Text(d.Row(x), d.Spells.Layout.Column("NameSubtext")!, 0)));
        Assert.Equal("5", d.Show(five[4], "effect1-value"));
        long b = t.AddTalent(tab, 1, 0, five);
        Assert.True(t.SetPrereq(b, a, 1).Ok);
        bool Err(string text) => t.Check(tab).Any(p => p.IsError && p.Message.Contains(text, StringComparison.Ordinal));
        bool Warn(string text) => t.Check(tab).Any(p => !p.IsError && p.Message.Contains(text, StringComparison.Ordinal));
        Assert.False(t.Check(tab).Any(p => p.IsError), string.Join("; ", t.Check(tab).Select(p => p.Text)));
        Assert.True(Warn("needs 5 points spent"));                     // tier 1 needs 5 points; the row above offers 1
        // the grid's bounds and one talent per cell
        Assert.Throws<DesignException>(() => t.AddTalent(tab, 8, 0, ranks));
        Assert.Throws<DesignException>(() => t.AddTalent(tab, 0, 4, ranks));
        Assert.Throws<DesignException>(() => t.AddTalent(tab, 0, 0, five));
        Assert.Throws<DesignException>(() => t.AddTalent(tab, 2, 0, Enumerable.Repeat(686L, 6).ToList()));
        Assert.False(t.Move(b, 0, 0).Ok);
        // a prerequisite below its talent, a rank it does not have, a circle
        long c = t.AddTalent(tab, 2, 1, t.MakeRankSpells(12295, "Thunder Step", 1));
        Assert.True(t.SetPrereq(a, c).Ok);
        Assert.True(Err("lower row"));
        Assert.True(t.SetPrereq(a, 0).Ok);
        Assert.False(t.SetPrereq(c, a, 3).Ok);                          // Riposte Drill has 1 rank
        Assert.True(t.SetPrereq(a, b, 5).Ok);
        Assert.True(Err("in a circle"));
        Assert.True(t.SetPrereq(a, 0).Ok);
        // ranks must follow each other; a rank spell in two talents
        var row = s.Table("Talent").Find(c)!;
        s.SetNumber("Talent", row, "SpellRank3", five[0]);
        Assert.True(Err("ranks must follow each other"));
        Assert.True(Err("also a rank of talent"));
        s.SetNumber("Talent", row, "SpellRank3", 0);
        // the arrow passes under another talent
        long e = t.AddTalent(tab, 3, 0, t.MakeRankSpells(12295, "Bulwark Drill", 1));
        Assert.True(t.SetPrereq(e, a).Ok);
        Assert.True(Warn("passes under"));
        // a prerequisite in another tree
        Assert.True(t.SetPrereq(e, 124).Ok);                            // Improved Heroic Strike (Arms)
        Assert.True(Err("another tree"));
        Assert.True(t.SetPrereq(e, 0).Ok);
        // too many trees for one class
        t.AddTree(1, "Fifth");
        Assert.Throws<DesignException>(() => t.AddTree(1, "Sixth"));
        Assert.True(t.RemoveTree(tab).Ok);
        Assert.Null(s.Table("Talent").Find(a));
    }

    // ------------------------------------------------------------------ classes

    [Fact]
    public void ClassViewMasksAndANewClass()
    {
        var (ws, s, d) = Open();
        var k = new ClassDesigner(s, d);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 7, 8, 9, 11 }, k.Classes().Select(c => c.Id));
        Assert.Equal(9, k.Resolve("WARLOCK"));
        Assert.Equal(9, k.Resolve("Warlock"));
        var list = k.SpellList(9);
        Assert.Contains(list, x => x.Spell == 686 && x.Level == 1);
        Assert.Contains(list, x => x.Spell == 172 && x.Level == 4);
        Assert.Contains(list, x => x.Source == SpellSource.Talent && x.From == "Affliction");
        Assert.Contains(k.Skills(9), x => x.Name == "Destruction");
        Assert.True(k.Assign(9, 686, 2).Ok);
        Assert.Equal(2, d.Spells.Number(d.Row(686), d.Spells.Layout.Column("SpellLevel")!));

        // class masks (game tables)
        var sla = s.Table("SkillLineAbility");
        var someRow = sla.Rows.First(r => sla.Number(r, sla.Layout.Column("ClassMask")!) == 0x100);
        long rid = sla.Key(someRow)!.Value;
        Assert.True(k.EditMask("SkillLineAbility", "ClassMask", new[] { rid.ToString() }, 1, true).Ok);
        Assert.Equal(0x101, sla.Number(someRow, sla.Layout.Column("ClassMask")!) & 0xFFFFFFFF);
        Assert.True(k.EditMask("SkillLineAbility", "ClassMask", new[] { rid.ToString() }, 1, false).Ok);
        Assert.Equal(0x100, sla.Number(someRow, sla.Layout.Column("ClassMask")!) & 0xFFFFFFFF);
        Assert.False(k.EditMask("Spell", null, new[] { "133" }, 1, true).Ok);
        Assert.False(k.EditMask("SkillLineAbility", "ClassMask", new[] { "99999999" }, 1, true).Ok);
        Assert.Equal(0x200, ClassDesigner.Bit(10));

        // a new class (experimental): ID 6 is free; the data and the report
        Assert.Equal(6, k.FreeClassId());
        var (id, done) = k.NewClass("Knight", "knight", 2, new long[] { 1 });
        Assert.Equal(6, id);
        Assert.Throws<DesignException>(() => k.NewClass("Knight", "KNIGHT", 2, new long[] { 1 }));
        var info = k.Info(6);
        Assert.Equal(("Knight", "KNIGHT"), (info.Name, info.Token));
        Assert.Equal(new[] { "Human" }, info.Races);
        Assert.True(info.SpellFamily >= 14);
        var srci = s.Table("SkillRaceClassInfo");
        Assert.Contains(srci.Rows, r => (srci.Number(r, srci.Layout.Column("ClassMask")!) & 0x20) != 0);
        var cso = s.Table("CharStartOutfit");
        Assert.Contains(cso.Rows, r => cso.Number(r, cso.Layout.Column("Class")!) == 6 && cso.Number(r, cso.Layout.Column("Race")!) == 1);
        Assert.Contains(done, x => x.Contains("server database is not connected"));
        var report = ClassDesigner.EngineReport(6);
        Assert.Contains(report, e => e.Side == "server" && e.Where.Contains("SharedDefines.h") && !e.DoneByDesigner);
        Assert.Contains(report, e => e.Side == "client" && e.Where.Contains("CharacterCreate") && !e.DoneByDesigner);
        Assert.Contains(report, e => e.Side == "client" && e.Where.Contains("@004706b0"));
        Assert.Contains(ClassDesigner.EngineReport(12), e => e.What.Contains("MAX_CLASSES raised"));
        var res = k.Save();
        Assert.Contains("ChrClasses", res.ClientTables);
        Assert.Contains(res.Notes, n => n.Contains("EXPERIMENTAL"));
        Assert.Contains(res.Notes, n => n.Contains("Copy tables to the server"));
        // the saved table loads in the game's layout (17 fields)
        var chr = DbcTable.Load(ws.ReadEdited(@"DBFilesClient\ChrClasses.dbc")!, "ChrClasses");
        Assert.Equal(17, chr.FieldCount);
        Assert.NotNull(chr.Find(6));
    }

    // ------------------------------------------------------------------ CSV / JSON round trips of the designers' rows

    [Fact]
    public void DesignedRowsRoundTripThroughCsvAndJson()
    {
        var (ws, s, d) = Open();
        long id = d.Clone(686, "Shadow Lance");
        d.Set(id, "description", "Deals $s1 damage, \"quoted\", over $d.");
        d.Set(id, "duration", "4");
        var t = new TalentDesigner(s, d);
        long tab = t.AddTree(1, "Stance Disciplines");
        long tal = t.AddTalent(tab, 0, 1, t.MakeRankSpells(12295, "Iron Line", 2, new[] { "1", "2" }));
        d.Save();
        foreach (var (table, ids) in new[] { ("Spell", new long[] { id }), ("Talent", new[] { tal }), ("TalentTab", new[] { tab }) })
            foreach (var ext in new[] { ".csv", ".json" })
            {
                var e = new DbcEditSession(new DbcService(TestEnv.Client, ws), table);
                var rows = ids.Select(x => e.Row(x)!).ToList();
                var file = Path.Combine(_root, table + ext);
                e.ExportTo(file, rows);
                var plan = e.PlanImport(file, DbcMissingRows.Keep);
                Assert.True(plan.Errors.Count == 0, string.Join("; ", plan.Errors));
                Assert.True(plan.Changed.Count == 0 && plan.Added.Count == 0, $"{table}{ext}: {plan.Preview.Summary}");
            }
    }
}
