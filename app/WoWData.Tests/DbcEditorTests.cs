using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using WoWData.Core.Dbc;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// The DBC editor's core (0.7.0) on the real 1.12.1 client (read only): the layouts against every file of the game, the
/// writer (every table written back byte for byte), edits through the workspace and the loose install of a FAKE client
/// folder, CSV / JSON round trips, links and "Used by", validation, undo / redo, and "Copy to server" into a fake server
/// folder. The real Client\Data and the real server are never written.
/// </summary>
public class DbcEditorTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = TestEnv.TempDir("dbc");
    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();

    public DbcEditorTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Assert.Equal(_before, TestEnv.DataSnapshot());   // the real Client\Data folder is unchanged
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static readonly Lazy<List<(string Name, byte[] Data)>> AllFiles = new(() =>
        TestEnv.Client.Glob(DbcService.Folder + "\\*.dbc").Select(p => (Path.GetFileNameWithoutExtension(p), TestEnv.Client.Read(p)!)).ToList());

    private static DbcTable Game(string name) => DbcTable.Load(TestEnv.Client.Read(DbcService.PathOf(name))!, name);

    // ------------------------------------------------------------------ layouts against the files

    [Fact]
    public void EveryClientTableHasALayoutWhoseRecordSizeMatchesTheFile()
    {
        var files = AllFiles.Value;
        Assert.Equal(154, files.Count);
        var layouts = DbcLayouts.Default;
        Assert.Equal(154, layouts.Tables.Count);
        foreach (var (name, data) in files)
        {
            var l = layouts.Get(name);
            Assert.True(l != null, $"{name}: no layout");
            int fields = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
            int recSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            Assert.True(l!.FieldCount == fields, $"{name}: layout has {l.FieldCount} fields, the file {fields}");
            Assert.True(l.RecordSize == recSize, $"{name}: layout has {l.RecordSize} bytes per row, the file {recSize}");
            Assert.False(DbcTable.Load(data, name).Layout.IsFallback, name);
            Assert.False(string.IsNullOrWhiteSpace(l.About), $"{name}: no description");
            Assert.False(string.IsNullOrWhiteSpace(l.Evidence), $"{name}: no evidence");
            Assert.False(string.IsNullOrWhiteSpace(l.Category), $"{name}: no category");
        }
        // every link points at a table with an ID column
        foreach (var l in layouts.Tables)
            foreach (var c in l.Columns.Where(c => c.ForeignKey != null))
                Assert.True(layouts.Get(c.ForeignKey!)!.HasKey, $"{l.Name}.{c.Name} links to {c.ForeignKey}, which has no ID column");
    }

    [Fact]
    public void EveryTableWritesBackByteForByte()
    {
        int rows = 0;
        foreach (var (name, data) in AllFiles.Value)
        {
            var t = DbcTable.Load(data, name);
            Assert.True(data.AsSpan().SequenceEqual(t.Write()), $"{name}: written differently");
            // the same through a full copy (every row cloned) and with every text rewritten (string block rebuilt)
            var c = t.Clone();
            foreach (var r in c.Rows)
                for (int f = 0; f < c.FieldCount; f++)
                    if (c.IsStringField(f)) c.SetStringBytes(r, f, c.StringBytes(r, f));
            Assert.True(data.AsSpan().SequenceEqual(c.Write()), $"{name}: rebuilt string block differs");
            rows += t.Rows.Count;
        }
        _out.WriteLine($"154 tables, {rows:N0} rows written back byte for byte");
    }

    [Fact]
    public void TextColumnsHoldStringOffsetsAndIdsAreUnique()
    {
        foreach (var (name, data) in AllFiles.Value)
        {
            var t = DbcTable.Load(data, name);
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            int strStart = DbcTable.HeaderSize + count * t.RecordSize;
            int strSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
            foreach (var r in t.Rows)
                for (int f = 0; f < t.FieldCount; f++)
                {
                    if (!t.IsStringField(f)) continue;
                    uint off = t.Raw(r, f);
                    Assert.True(off < strSize, $"{name} field {f}: offset {off} outside the string block");
                    Assert.True(off == 0 || data[strStart + off - 1] == 0, $"{name} field {f}: offset {off} is not the start of a string");
                }
            if (t.Layout.KeyColumn is { } k)
                Assert.True(t.Rows.Select(r => t.Number(r, k)).Distinct().Count() == t.Rows.Count, $"{name}: {k.Name} is not unique");
            Assert.Equal(0, t.BadStringOffsets());
            Assert.Empty(DbcValidation.FileProblems(data, name));
        }
    }

    [Fact]
    public void LinksAndFloatsAreConsistentWithTheData()
    {
        var tables = AllFiles.Value.ToDictionary(f => f.Name, f => DbcTable.Load(f.Data, f.Name), StringComparer.OrdinalIgnoreCase);
        var weak = new List<string>();
        foreach (var t in tables.Values)
            foreach (var c in t.Layout.Columns)
            {
                if (c.Storage == DbcStorage.Float && c.Verified)
                {
                    int bad = t.Rows.Count(r => !float.IsFinite(t.Float(r, c)));
                    Assert.True(bad == 0, $"{t.Name}.{c.Name}: {bad} values are not finite decimal numbers");
                    int huge = t.Rows.Count(r => Math.Abs(t.Float(r, c)) > 1e9);
                    if (huge > 0) _out.WriteLine($"{t.Name}.{c.Name}: {huge} values above 1e9");
                }
                if (c.Semantic == DbcSemantic.Bool)
                    Assert.True(t.Rows.All(r => t.Raw(r, c.Field) <= 1), $"{t.Name}.{c.Name} is yes / no but holds other values");
                if (c.ForeignKey == null) continue;
                var target = tables[c.ForeignKey];
                var vals = t.Rows.Select(r => t.Number(r, c)).Where(v => v != 0 && v != -1 && v != uint.MaxValue).ToList();
                if (vals.Count == 0) continue;
                double found = vals.Count(v => target.Find(v) != null) / (double)vals.Count;
                _out.WriteLine($"{t.Name}.{c.Name} -> {c.ForeignKey}: {found:P1} of {vals.Count} links resolve{(c.Verified ? "" : " (not verified)")}");
                // Blizzard's files have some links to rows that were removed (SkillRaceClassInfo -> SkillLine: 74 %); a wrong
                // column position would resolve far less
                if (c.Verified && found < 0.7) weak.Add($"{t.Name}.{c.Name} -> {c.ForeignKey} ({found:P0})");
            }
        Assert.True(weak.Count == 0, "verified links that mostly do not resolve: " + string.Join(", ", weak));
    }

    [Fact]
    public void ImportantTablesAreFullyNamed()
    {
        var important = new[]
        {
            "Spell", "SpellIcon", "SpellVisual", "SpellVisualKit", "SpellVisualEffectName", "SpellItemEnchantment", "ItemDisplayInfo", "ItemSet",
            "ItemRandomProperties", "CreatureDisplayInfo", "CreatureDisplayInfoExtra", "CreatureModelData", "ChrRaces", "ChrClasses",
            "CharSections", "Faction", "FactionTemplate", "Map", "AreaTable", "WorldMapArea", "TaxiNodes", "TaxiPath", "TaxiPathNode",
            "Talent", "TalentTab", "SkillLine", "SkillLineAbility", "Lock", "GameObjectDisplayInfo", "Emotes", "EmotesText",
            "EmotesTextData", "EmotesTextSound", "SoundEntries", "Light", "LightParams", "LightIntBand", "LightFloatBand", "LightSkybox",
        };
        // the columns of those tables whose meaning could not be verified (listed in the manual's known issues)
        var allowed = new HashSet<string>
        {
            "ChrClasses.PlayerClass", "Map.col_16", "Map.col_17", "Map.col_18", "SkillLineAbility.CharacterPoints1", "SkillLineAbility.CharacterPoints2",
            "SpellVisualEffectName.col_3", "SpellVisualKit.KitType", "LightParams.col_3",
            "SpellVisualKit.CharParamTwo1", "SpellVisualKit.CharParamTwo2", "SpellVisualKit.CharParamTwo3", "SpellVisualKit.CharParamTwo4",
            "SpellVisualKit.CharParamThree1", "SpellVisualKit.CharParamThree2", "SpellVisualKit.CharParamThree3", "SpellVisualKit.CharParamThree4",
        };
        foreach (var n in important)
        {
            var l = DbcLayouts.Default.Get(n)!;
            var open = l.Columns.Where(c => !c.Verified && !allowed.Contains($"{n}.{c.Name}")).Select(c => c.Name).ToList();
            Assert.True(open.Count == 0, $"{n}: {l.CoverageText}: {string.Join(", ", open)}");
        }
        // the rest: some unverified columns are allowed, but every column has a name (col_N) and a hint
        foreach (var l in DbcLayouts.Default.Tables)
            foreach (var c in l.Columns.Where(c => !c.Verified))
                Assert.False(string.IsNullOrWhiteSpace(c.Hint), $"{l.Name}.{c.Name} has no hint");
        var counts = DbcLayouts.Default.Tables.GroupBy(l => l.Coverage).ToDictionary(g => g.Key, g => g.Count());
        _out.WriteLine($"fully named {counts.GetValueOrDefault(DbcCoverage.Full)}, partly {counts.GetValueOrDefault(DbcCoverage.Partial)}, raw {counts.GetValueOrDefault(DbcCoverage.Raw)}");
    }

    [Fact]
    public void ReadsTheSameValuesAsThePythonReference()
    {
        // the oracle: wowdata.dbc (the Python reference) reads every table; the editor's layouts must give the same headers
        // and, for the tables Python has a typed layout for, the same value in every typed field of every row
        var dir = Oracle.Ensure("dbc");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "dbc.json")));
        int tables = 0, values = 0;
        foreach (var t in doc.RootElement.EnumerateObject())
        {
            tables++;
            var tab = Game(t.Name);
            Assert.Equal(t.Value.GetProperty("count").GetInt32(), tab.Rows.Count);
            Assert.Equal(t.Value.GetProperty("fields").GetInt32(), tab.FieldCount);
            Assert.Equal(t.Value.GetProperty("record_size").GetInt32(), tab.RecordSize);
            if (!t.Value.TryGetProperty("typed", out var typed)) continue;
            foreach (var col in typed.EnumerateObject())
            {
                int field = int.Parse(col.Name);
                var kind = col.Value.GetProperty("kind").GetString();
                int r = 0;
                foreach (var v in col.Value.GetProperty("values").EnumerateArray())
                {
                    var row = tab.Rows[r++];
                    if (kind is "s" or "loc")
                    {
                        Assert.True(tab.IsStringField(field), $"{t.Name} field {field} ({col.Value.GetProperty("name")}) is text in Python");
                        Assert.Equal(v.GetString(), tab.Text(row, field));
                    }
                    else Assert.Equal(v.GetInt64() & 0xFFFFFFFF, tab.Raw(row, field));
                    values++;
                }
            }
        }
        Assert.Equal(154, tables);
        _out.WriteLine($"{tables} headers and {values:N0} typed values agree with wowdata.dbc");
    }

    [Fact]
    public void TypedValuesMatchTheIndexReaders()
    {
        // the layouts agree with the readers the index uses (wowdata's Python layouts, checked by the index tests)
        var spell = Game("Spell");
        var fireball = spell.Find(133)!;
        var L = spell.Layout;
        Assert.Equal("Fireball", spell.Text(fireball, L.Column("Name")!));
        Assert.Equal("Rank 1", spell.Text(fireball, L.Column("NameSubtext")!));
        Assert.Equal(67, spell.Number(fireball, L.Column("SpellVisual")!));
        Assert.Equal(2, spell.Number(fireball, L.Column("School")!));
        Assert.Equal("Fire", L.Column("School")!.Values!.NameOf(2));
        var old = DbcTables.Spell(new DbcFile(TestEnv.Client.Read(DbcService.PathOf("Spell"))!, "Spell")).First(s => s.Id == 133);
        Assert.Equal(old.Name, spell.Text(fireball, L.Column("Name")!));
        var icon = Game("SpellIcon");
        var ir = icon.Find(spell.Number(fireball, L.Column("SpellIconID")!))!;
        Assert.Equal(@"Interface\Icons\Spell_Fire_FlameBolt", icon.Text(ir, icon.Layout.Column("TextureFilename")!));
        var cdi = Game("CreatureDisplayInfo");
        var olds = DbcTables.CreatureDisplayInfo(new DbcFile(TestEnv.Client.Read(DbcService.PathOf("CreatureDisplayInfo"))!, "CreatureDisplayInfo"));
        foreach (var o in olds.Take(500))
        {
            var r = cdi.Find(o.Id)!;
            Assert.Equal(o.ModelId, cdi.Number(r, cdi.Layout.Column("ModelID")!));
            Assert.Equal(o.Scale, cdi.Float(r, cdi.Layout.Column("CreatureModelScale")!));
            Assert.Equal(o.Skin[0], cdi.Text(r, cdi.Layout.Column("TextureVariation1")!));
        }
    }

    // ------------------------------------------------------------------ editing, undo, validation

    [Fact]
    public void EditUndoRedoAndRowOperations()
    {
        var t = Game("SpellIcon");
        var ed = new DbcEditor(t);
        var col = t.Layout.Column("TextureFilename")!;
        var row = t.Find(185) ?? t.Rows[10];
        string before = t.Text(row, col);
        Assert.Equal(1, ed.SetCell(row, col.Field, DbcCell.Of(@"Interface\Icons\Spell_Fire_Fireball02"), "Edit"));
        Assert.True(ed.IsDirty);
        Assert.Equal(@"Interface\Icons\Spell_Fire_Fireball02", t.Text(row, col));
        Assert.True(ed.Undo());
        Assert.Equal(before, t.Text(row, col));
        Assert.False(ed.IsDirty);
        Assert.True(ed.Redo());
        Assert.True(ed.IsDirty);
        ed.Undo();
        // add, duplicate, delete
        int n = t.Rows.Count;
        long id = t.SuggestId();
        Assert.Equal(t.Rows.Max(r => t.Key(r)!.Value) + 1, id);
        var copy = ed.Duplicate(row, id);
        Assert.Equal(n + 1, t.Rows.Count);
        Assert.Same(copy, t.Find(id));
        Assert.Equal(before, t.Text(copy, col));
        ed.DeleteRows(new[] { row }, "Delete");
        Assert.Null(t.Find(t.Key(row)!.Value));
        ed.Undo();
        ed.Undo();
        Assert.Equal(n, t.Rows.Count);
        Assert.False(ed.IsDirty);
        Assert.True(Game("SpellIcon").Write().AsSpan().SequenceEqual(t.Write()));
        // the saved state is remembered; a new edit after undo loses the redo
        ed.SetCell(row, col.Field, DbcCell.Of("x"), "a");
        ed.MarkSaved();
        Assert.False(ed.IsDirty);
        ed.Undo();
        Assert.True(ed.IsDirty);
        ed.SetCell(row, col.Field, DbcCell.Of("y"), "b");
        Assert.False(ed.CanRedo);
        Assert.True(ed.IsDirty);
    }

    [Fact]
    public void ValidationChecksTypesLinksIdsAndTexts()
    {
        var spell = Game("Spell");
        var icons = Game("SpellIcon");
        DbcTable? Look(string n) => n == "SpellIcon" ? icons : n == "Spell" ? spell : null;
        var row = spell.Find(133)!;
        var L = spell.Layout;
        List<DbcProblem> Check(string col, string text) => DbcValidation.Check(spell, row, L.Column(col)!, text, Look, out _);
        Assert.Empty(Check("SpellIconID", "185"));
        Assert.Contains(Check("SpellIconID", "999999"), p => p.IsError && p.Message.Contains("There is no SpellIcon with ID 999999"));
        Assert.Empty(Check("SpellIconID", "0"));                     // 0 = none
        Assert.Contains(Check("ManaCost", "-5"), p => p.IsError);     // unsigned
        Assert.Contains(Check("ManaCost", "abc"), p => p.IsError);
        Assert.Contains(Check("ManaCost", "99999999999"), p => p.IsError);
        Assert.Empty(Check("PowerType", "-2"));                       // signed, a known value (Health)
        Assert.Contains(Check("PowerType", "77"), p => !p.IsError);   // an unknown choice is only a warning
        Assert.Empty(Check("School", "Fire"));                        // a choice by its name
        Assert.Empty(Check("Speed", "24.5"));
        Assert.Contains(Check("Speed", "fast"), p => p.IsError);
        Assert.Contains(Check("ID", "116"), p => p.IsError && p.Message.Contains("already used"));   // Frostbolt has 116
        Assert.Empty(Check("ID", "133"));
        Assert.Empty(Check("Name", "Fire Blast"));
        Assert.Contains(Check("Name", "a\0b"), p => p.IsError);
        var path = DbcValidation.Check(icons, icons.Rows[0], icons.Layout.Column("TextureFilename")!, @"Interface\Icons\" + new string('x', 300), Look, out _);
        Assert.Contains(path, p => p.IsError && p.Message.Contains("259"));
        Assert.Contains(DbcValidation.Check(icons, icons.Rows[0], icons.Layout.Column("TextureFilename")!, "Interface/Icons/x", Look, out _), p => !p.IsError);
        var races = Game("CharHairGeosets");
        Assert.Contains(DbcValidation.Check(races, races.Rows[0], races.Layout.Column("Showscalp")!, "2", Look, out _), p => p.IsError);
        // byte columns
        var outfit = Game("CharStartOutfit");
        Assert.Contains(DbcValidation.Check(outfit, outfit.Rows[0], outfit.Layout.Column("Race")!, "300", null, out _), p => p.IsError && p.Message.Contains("255"));
    }

    [Fact]
    public void BulkEditOfSelectedRows()
    {
        var t = Game("SpellCastTimes");
        var ed = new DbcEditor(t);
        var c = t.Layout.Column("Base")!;
        var rows = t.Rows.Where(r => t.Number(r, c) > 0).Take(5).ToList();
        var before = rows.Select(r => t.Number(r, c)).ToList();
        var cells = rows.Select(r => (r, c.Field, DbcCell.Of(unchecked((uint)(int)(t.Number(r, c) * 2))))).ToList();
        Assert.Equal(5, ed.SetCells(cells, "Multiply Base by 2"));
        Assert.Equal(before.Select(v => v * 2), rows.Select(r => t.Number(r, c)));
        ed.Undo();
        Assert.Equal(before, rows.Select(r => t.Number(r, c)));
    }

    // ------------------------------------------------------------------ workspace, compare, loose install

    private (ClientPaths Paths, Workspace Ws, DbcService Svc) FakeClient()
    {
        var fake = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fake, "Data"));
        var paths = new AppPaths(Path.Combine(_root, "home")).ForClient(fake);
        var ws = new Workspace(paths);
        return (paths, ws, new DbcService(TestEnv.Client, ws));   // reads the real archives, writes only below _root
    }

    [Fact]
    public void EditGoesToTheWorkspaceComparesAndInstallsLoose()
    {
        var (paths, ws, svc) = FakeClient();
        var t = svc.LoadCurrent("SpellIcon");
        var ed = new DbcEditor(t);
        var col = t.Layout.Column("TextureFilename")!;
        var r = t.Find(185)!;
        ed.SetCell(r, col.Field, DbcCell.Of(@"Interface\Icons\Spell_Fire_Fireball02"), "Change the icon");
        var id = t.SuggestId();
        var added = t.NewRow(id);
        t.SetText(added, col.Field, @"Interface\Icons\INV_Misc_QuestionMark");
        ed.AddRow(added, "Add a row");
        var rec = svc.Save(t, "1 row changed, 1 added")!;
        ed.MarkSaved();
        Assert.Equal("dbc", rec.Kind);
        Assert.Equal(@"DBFilesClient\SpellIcon.dbc", rec.Path);
        Assert.True(ws.IsModified(@"DBFilesClient\SpellIcon.dbc"));
        Assert.Equal("patch.MPQ", rec.Original.Archive);
        // compare: one changed row (the icon), one added
        var diff = svc.CompareWithGame("SpellIcon");
        Assert.Equal(1, diff.Changed);
        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Removed);
        var ch = diff.Rows.Single(x => x.Kind == DbcRowChangeKind.Changed);
        Assert.Equal("185", ch.Key);
        Assert.Equal("TextureFilename", ch.Cells.Single().Column);
        // the info shows the workspace edit; the lookup sees the edit
        Assert.True(svc.Info("SpellIcon").InWorkspace);
        Assert.Equal(@"Interface\Icons\Spell_Fire_Fireball02", svc.Lookup("SpellIcon")!.Text(svc.Lookup("SpellIcon")!.Find(185)!, col));
        // Test in game (loose files) into the FAKE client, then read back
        var actions = ws.InstallLoose(new[] { rec.Path });
        var dest = Path.Combine(paths.DataDir, "DBFilesClient", "SpellIcon.dbc");
        Assert.Equal(dest, actions.Single().Dest);
        var back = DbcTable.Load(File.ReadAllBytes(dest), "SpellIcon");
        Assert.Equal(@"Interface\Icons\Spell_Fire_Fireball02", back.Text(back.Find(185)!, col));
        Assert.Equal(@"Interface\Icons\INV_Misc_QuestionMark", back.Text(back.Find(id)!, col));
        Assert.True(svc.Info("SpellIcon").LooseIsCurrentEdit);
        // the release patch carries it and reads back through the client's lookup order
        var build = ws.BuildPatch("patch-3.MPQ", install: false);
        using (var patch = new MpqArchive(build.Path))
            Assert.True(patch.Read(@"DBFilesClient\SpellIcon.dbc")!.AsSpan().SequenceEqual(File.ReadAllBytes(dest)));
        // saving the game's version again removes the edit
        var game = svc.LoadGame("SpellIcon");
        Assert.Null(svc.Save(game, "back to the game"));
        Assert.False(ws.IsModified(@"DBFilesClient\SpellIcon.dbc"));
        Assert.False(File.Exists(dest));   // the revert also removed the loose copy it had installed
    }

    [Fact]
    public void TheWorkspaceRefusesABrokenTable()
    {
        var (_, ws, _) = FakeClient();
        var data = TestEnv.Client.Read(DbcService.PathOf("SpellIcon"))!;
        var broken = (byte[])data.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(8), 3);   // a field count the game does not accept
        BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(12), 12);
        var e = Assert.Throws<WorkspaceException>(() => ws.SaveEdit(@"DBFilesClient\SpellIcon.dbc", broken, new OriginInfo(), ws.BeginChangeSet("x")));
        Assert.Contains("would not load", e.Message);
        Assert.NotEmpty(DbcValidation.FileProblems(data.AsSpan(0, 30).ToArray(), "SpellIcon"));
    }

    // ------------------------------------------------------------------ CSV and JSON

    [Fact]
    public void CsvAndJsonRoundTripEveryTable()
    {
        foreach (var (name, data) in AllFiles.Value)
        {
            var t = DbcTable.Load(data, name);
            if (t.Rows.Count > 3000) continue;   // the big tables are covered below (time)
            var csv = DbcExchange.ToCsv(t);
            var plan = DbcExchange.PlanCsv(t, csv, DbcMissingRows.Remove, null);
            Assert.True(plan.Errors.Count == 0, $"{name}: {string.Join("; ", plan.Errors.Take(3))}");
            Assert.True(plan.IsEmpty, $"{name}: CSV import of its own export changes {plan.Preview.Summary} {string.Join(" | ", plan.Preview.Rows.Take(2).Select(r => r.Key + " " + r.Details()))}");
            var json = DbcExchange.ToJson(t);
            var jp = DbcExchange.PlanJson(t, json, DbcMissingRows.Remove, null);
            Assert.True(jp.Errors.Count == 0 && jp.IsEmpty, $"{name}: JSON round trip: {string.Join("; ", jp.Errors.Take(3))} {jp.Preview.Summary}");
        }
        foreach (var name in new[] { "Spell", "ItemDisplayInfo" })
        {
            var t = Game(name);
            var plan = DbcExchange.PlanCsv(t, DbcExchange.ToCsv(t), DbcMissingRows.Remove, null);
            Assert.True(plan.Errors.Count == 0 && plan.IsEmpty, $"{name}: {plan.Preview.Summary}");
        }
    }

    [Fact]
    public void CsvImportShowsChangesAddsAndRemovesAndAppliesAsOneStep()
    {
        var t = Game("SpellIcon");
        var csv = DbcExchange.ToCsv(t, t.Rows.Take(5));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.Equal("ID,TextureFilename", lines[0]);
        var id0 = t.Key(t.Rows[0])!.Value;
        lines[1] = $"{id0},Interface\\Icons\\Changed,Name";   // a value with a comma (quoted below)
        lines[1] = $"{id0},\"Interface\\Icons\\Changed, with comma\"";
        lines.Add("99999,Interface\\Icons\\Brand_New");
        var text = string.Join("\r\n", lines) + "\r\n";
        // keep the rows the file does not have
        var plan = DbcExchange.PlanCsv(t, text, DbcMissingRows.Keep, null);
        Assert.Empty(plan.Errors);
        Assert.Single(plan.Changed);
        Assert.Single(plan.Added);
        Assert.Empty(plan.Removed);
        Assert.Equal("Interface\\Icons\\Changed, with comma", plan.Preview.Rows.First(r => r.Kind == DbcRowChangeKind.Changed).Cells.Single().After);
        // the file is the whole table: the others are removed
        var all = DbcExchange.PlanCsv(t, text, DbcMissingRows.Remove, null);
        Assert.Equal(t.Rows.Count - 5, all.Removed.Count);
        var ed = new DbcEditor(t);
        int n = t.Rows.Count;
        ed.Apply(plan, "Import");
        Assert.Equal(n + 1, t.Rows.Count);
        Assert.Equal("Interface\\Icons\\Brand_New", t.Text(t.Find(99999)!, t.Layout.Column("TextureFilename")!));
        ed.Undo();
        Assert.Equal(n, t.Rows.Count);
        Assert.True(Game("SpellIcon").Write().AsSpan().SequenceEqual(t.Write()));
        // errors block the import
        var bad = DbcExchange.PlanCsv(t, "ID,TextureFilename\r\nabc,x\r\n5,y\r\n5,z\r\n", DbcMissingRows.Keep, null);
        Assert.Contains(bad.Errors, e => e.Contains("line 2"));
        Assert.Contains(bad.Errors, e => e.Contains("appears twice"));
        Assert.False(bad.CanApply);
        var wrong = DbcExchange.PlanCsv(t, "Foo,Bar\r\n1,2\r\n", DbcMissingRows.Keep, null);
        Assert.NotEmpty(wrong.Errors);
        // links are validated
        var spell = Game("Spell");
        var sc = DbcExchange.ToCsv(spell, new[] { spell.Find(133)! }).Replace(",185,", ",999999,");
        var sp = DbcExchange.PlanCsv(spell, sc, DbcMissingRows.Keep, n2 => n2 == "SpellIcon" ? t : n2 == "Spell" ? spell : null);
        Assert.Contains(sp.Errors, e => e.Contains("There is no SpellIcon with ID 999999"));
    }

    [Fact]
    public void LocalizedTextsKeepTheOtherLanguages()
    {
        var t = Game("ChrRaces");
        var c = t.Layout.Column("Name")!;
        var r = t.Find(2)!;
        Assert.Equal("Orc", t.Text(r, c, 0));
        var ed = new DbcEditor(t);
        ed.SetCell(r, c.Field + 3, DbcCell.Of("Ork"), "deDE");   // German
        Assert.Equal("Orc", t.Text(r, c, 0));
        Assert.Equal("Ork", t.Text(r, c, 3));
        var flags = t.Raw(r, DbcTable.LocFlagsField(c));
        var back = DbcTable.Load(t.Write(), "ChrRaces");
        Assert.Equal("Ork", back.Text(back.Find(2)!, c, 3));
        Assert.Equal("Orc", back.Text(back.Find(2)!, c, 0));
        Assert.Equal(flags, back.Raw(back.Find(2)!, DbcTable.LocFlagsField(c)));
        var csv = DbcExchange.ToCsv(t, new[] { r });
        Assert.Contains("Name_deDE", csv);
        Assert.Contains("Ork", csv);
    }

    // ------------------------------------------------------------------ links, Used by, files

    [Fact]
    public void LinksUsedByAndFiles()
    {
        var (_, _, svc) = FakeClient();
        DbcTable? Look(string n) => svc.Lookup(n);
        Assert.Equal(@"Interface\Icons\Spell_Fire_FlameBolt", svc.LinkLabel("SpellIcon", 185, Look));
        Assert.Null(svc.LinkLabel("SpellIcon", 999999, Look));
        var used = svc.UsedBy("SpellIcon", 185, Look);
        Assert.Contains(used, u => u.Table == "Spell" && u.Id == 133 && u.Column == "SpellIconID");
        Assert.Contains("Spell.SpellIconID", svc.LinkingColumns("SpellIcon"));
        // the icon file of a SpellIcon row, the model and the skin of a creature display, an item's icon and textures
        var icons = svc.Lookup("SpellIcon")!;
        Assert.Equal(@"Interface\Icons\Spell_Fire_FlameBolt.blp", svc.FilesOf(icons, icons.Find(185)!, icons.Layout.Column("TextureFilename")!).Single(), ignoreCase: true);
        var cdi = svc.Lookup("CreatureDisplayInfo")!;
        var boar = cdi.Find(503)!;   // Mottled Boar
        var skin = svc.FilesOf(cdi, boar, cdi.Layout.Column("TextureVariation1")!);
        Assert.Contains(skin, p => p.StartsWith(@"Creature\Boar\", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase));
        var cmd = svc.Lookup("CreatureModelData")!;
        var model = svc.FilesOf(cmd, cmd.Find(cdi.Number(boar, cdi.Layout.Column("ModelID")!))!, cmd.Layout.Column("ModelName")!);
        Assert.Equal(@"Creature\Boar\Boar.m2", model.Single(), ignoreCase: true);
        var idi = svc.Lookup("ItemDisplayInfo")!;
        var withIcon = idi.Rows.First(r => idi.Text(r, idi.Layout.Column("InventoryIcon")!) == "INV_Sword_04");
        Assert.Equal(@"Interface\Icons\INV_Sword_04.blp", svc.FilesOf(idi, withIcon, idi.Layout.Column("InventoryIcon")!).Single(), ignoreCase: true);
        // region textures are found with their _U / _M / _F variant (not every name of the file has its texture in 1.12)
        var torso = idi.Layout.Column("TextureTorsoUpper")!;
        var withTorso = idi.Rows.Where(r => idi.Text(r, torso).Length > 0).Take(400).ToList();
        int found = withTorso.Count(r => svc.FilesOf(idi, r, torso).Any(p => p.StartsWith(@"Item\TextureComponents\TorsoUpperTexture\", StringComparison.OrdinalIgnoreCase)));
        Assert.True(found > withTorso.Count / 2, $"{found} of {withTorso.Count} torso textures found");
        // the list: every table with its archives (patch order) and the 4 tables patch.MPQ deletes
        var list = svc.List();
        Assert.Equal(158, list.Count);
        Assert.Equal(4, list.Count(i => i.DeletedInGame));
        var sp = list.Single(i => i.Name == "Spell");
        Assert.Equal("patch-2.MPQ", sp.Archive);
        Assert.StartsWith("From patch-2.MPQ (overrides", sp.SourceText);
    }

    // ------------------------------------------------------------------ server

    [Fact]
    public void ServerUseIsKnownForEveryTable()
    {
        var L = DbcLayouts.Default;
        Assert.Equal(DbcServerUse.ServerDatabase, L.Get("Spell")!.ServerUse);
        Assert.Equal("spell_template", L.Get("Spell")!.ServerTable);
        Assert.Equal(DbcServerUse.ServerDatabase, L.Get("FactionTemplate")!.ServerUse);
        Assert.Equal(DbcServerUse.ServerDatabase, L.Get("TaxiNodes")!.ServerUse);
        Assert.Equal(DbcServerUse.ServerFile, L.Get("TaxiPath")!.ServerUse);
        Assert.Equal(DbcServerUse.ServerFile, L.Get("CreatureDisplayInfo")!.ServerUse);
        Assert.Equal(DbcServerUse.ClientOnly, L.Get("SpellIcon")!.ServerUse);
        Assert.Equal(DbcServerUse.ClientOnly, L.Get("ItemDisplayInfo")!.ServerUse);
        // the 46 tables VMaNGOS's LoadDBCStores reads, and the 11 it takes from its database
        Assert.Equal(46, L.Tables.Count(t => t.ServerUse == DbcServerUse.ServerFile));
        Assert.Equal(11, L.Tables.Count(t => t.ServerUse == DbcServerUse.ServerDatabase));
        Assert.All(L.Tables, t => Assert.False(string.IsNullOrWhiteSpace(t.ServerText)));
    }

    [Fact]
    public void CopyToServerBacksUpRefusesWhileRunningAndUndoes()
    {
        var (paths, ws, svc) = FakeClient();
        var serverDbc = Path.Combine(_root, "FakeServer", "data", "5875", "dbc");
        Directory.CreateDirectory(serverDbc);
        var game = TestEnv.Client.Read(DbcService.PathOf("TaxiPath"))!;
        File.WriteAllBytes(Path.Combine(serverDbc, "TaxiPath.dbc"), game);
        Assert.Equal(serverDbc, DbcServerCopy.DefaultServerDbcDir(Path.Combine(_root, "FakeServer", "data")));
        // edit TaxiPath (server reads its file), SpellIcon (client only) and Spell (server database)
        foreach (var (name, col, val) in new[] { ("TaxiPath", "Cost", 123u), ("SpellIcon", "", 0u), ("Spell", "ManaCost", 1u) })
        {
            var t = svc.LoadCurrent(name);
            if (name == "SpellIcon") t.SetText(t.Rows[0], 1, "x");
            else t.SetRaw(t.Rows[0], t.Layout.Column(col)!.Field, val);
            svc.Save(t, "test");
        }
        bool running = true;
        var copy = new DbcServerCopy(serverDbc, Path.Combine(_root, "backups"), paths.ServerDbcHistoryFile, () => running ? new List<string> { "mangosd.exe (process 1)" } : new List<string>());
        var plan = copy.Plan(ws);
        Assert.Equal(3, plan.Count);
        Assert.True(plan.Single(p => p.Table == "TaxiPath").CanCopy);
        Assert.False(plan.Single(p => p.Table == "Spell").CanCopy);
        Assert.Contains("spell_template", plan.Single(p => p.Table == "Spell").StatusText);
        Assert.False(plan.Single(p => p.Table == "SpellIcon").CanCopy);
        var e = Assert.Throws<DbcException>(() => copy.Copy(ws));
        Assert.Contains("running", e.Message);
        Assert.True(File.ReadAllBytes(Path.Combine(serverDbc, "TaxiPath.dbc")).AsSpan().SequenceEqual(game));   // untouched
        Assert.Throws<DbcException>(() => { running = false; copy.Copy(ws, new[] { "Spell" }); });   // never the server's Spell.dbc
        running = false;
        var (rec, done) = copy.Copy(ws);
        Assert.Single(rec.Written);
        Assert.False(File.Exists(Path.Combine(serverDbc, "Spell.dbc")));
        Assert.False(File.Exists(Path.Combine(serverDbc, "SpellIcon.dbc")));
        var mine = ws.ReadEdited(@"DBFilesClient\TaxiPath.dbc")!;
        Assert.True(File.ReadAllBytes(Path.Combine(serverDbc, "TaxiPath.dbc")).AsSpan().SequenceEqual(mine));
        Assert.True(File.ReadAllBytes(Path.Combine(rec.Backup, "TaxiPath.dbc")).AsSpan().SequenceEqual(game));
        Assert.Contains(done, d => d.Contains("Restart the world server"));
        Assert.True(copy.Plan(ws).Single(p => p.Table == "TaxiPath").Identical);
        running = true;
        Assert.Throws<DbcException>(() => copy.Undo(rec));
        running = false;
        copy.Undo(rec);
        Assert.True(File.ReadAllBytes(Path.Combine(serverDbc, "TaxiPath.dbc")).AsSpan().SequenceEqual(game));
        Assert.NotNull(copy.LoadHistory().Single().Undone);
    }
}
