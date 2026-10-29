using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WoWData.Cli;
using WoWData.Core.Db2;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// Synthetic WoWDBDefs definition text for the tests, written by the tests themselves (never a copy of a real .dbd
/// file): a definition shaped like our measured layout of a table (the same column count, types and layout hash, so it
/// fits the synthetic WDC5 tables of <see cref="SyntheticClients"/>), with names of its own ("D_" + our name).
/// </summary>
public static class SyntheticDbd
{
    /// <summary>A .dbd text for <paramref name="table"/> of 1.60.1.70450: one field per column of our layout (X1..XN become
    /// X[N]), a non-inline ID when the layout has no ID column, a non-inline relation when <paramref name="relation"/>.</summary>
    public static string For(string table, string build = "1.60.1.70450", uint? layoutHash = null, bool relation = false, int dropColumns = 0)
    {
        var layouts = Db2Layouts.ForBuild(70450)!;
        var l = layouts.Get(table)!;
        uint hash = layoutHash ?? layouts.LayoutHash(table) ?? 0;
        var cols = l.Columns.Take(l.Columns.Count - dropColumns).ToList();
        var fields = new List<(string Name, string Type, int Array)>();
        for (int i = 0; i < cols.Count; i++)
        {
            var c = cols[i];
            string type = c.IsString ? "string" : c.IsFloat ? "float" : "int";
            var m = Regex.Match(c.Name, @"^(.*\D)1$");
            if (m.Success)
            {
                int n = 1;
                while (i + n < cols.Count && cols[i + n].Name == m.Groups[1].Value + (n + 1).ToString(CultureInfo.InvariantCulture)) n++;
                if (n > 1)
                {
                    fields.Add(("D_" + m.Groups[1].Value, type, n));
                    i += n - 1;
                    continue;
                }
            }
            fields.Add((c.Name == "ID" ? "ID" : "D_" + c.Name, type, 0));
        }
        var sb = new StringBuilder("COLUMNS\n");
        bool idColumn = fields.Any(f => f.Name == "ID");
        if (!idColumn) sb.Append("int ID\n");
        if (relation) sb.Append("int<Parent::ID> ParentID // the parent row\n");
        // a column our file marks as a best guess (~Name) is "not confirmed" (?) here too
        foreach (var f in fields) sb.Append($"{f.Type} {f.Name}{(cols.Any(c => !c.Verified && !c.Name.StartsWith("col_", StringComparison.Ordinal) && "D_" + c.Name == f.Name) ? "?" : "")}\n");
        sb.Append("\nBUILD 0.5.3.3368\n$id$ID<32>\n");
        sb.Append($"\nLAYOUT {hash:X8}\nBUILD {build}\nCOMMENT written by the tests\n");
        if (!idColumn) sb.Append("$noninline,id$ID<32>\n");
        foreach (var f in fields) sb.Append(f.Name == "ID" ? "$id$ID<32>\n" : $"{f.Name}{(f.Type == "int" ? "<32>" : "")}{(f.Array > 0 ? $"[{f.Array}]" : "")}\n");
        if (relation) sb.Append("$noninline,relation$ParentID<32>\n");
        return sb.ToString();
    }

    /// <summary>Writes a folder of synthetic definitions (the Map, ItemSparse and ItemDisplayInfoMaterialRes tables of the
    /// synthetic "1.60" client) and returns it.</summary>
    public static string WriteFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Map.dbd"), For("Map"));
        File.WriteAllText(Path.Combine(dir, "ItemSparse.dbd"), For("ItemSparse"));
        File.WriteAllText(Path.Combine(dir, "ItemDisplayInfoMaterialRes.dbd"), For("ItemDisplayInfoMaterialRes", relation: true));
        // a definition for another layout of this build: the table falls back to our measured names, with the reason
        File.WriteAllText(Path.Combine(dir, "CreatureModelData.dbd"), For("CreatureModelData", layoutHash: 0x12345678));
        return dir;
    }
}

/// <summary>
/// M5 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): our own .dbd parser on test-written text, choosing a definition by
/// build and layout hash, naming a synthetic WDC5 table, the fallback to our measured names with the reason, the
/// other-clients.dbd setting, the table views and wds commands of another version with a WoWDBDefs folder, and the
/// disagreement report (tests only). No WoWDBDefs file is ever copied into the repository; the real copy is read only
/// by <see cref="RealDbdTests"/> (opt-in).
/// </summary>
public class DbdTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("dbd");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Sample = """
        COLUMNS
        int ID
        string Directory // the folder
        locstring Title_lang
        int<Other::ID> OtherID
        float Pos
        int Flags?
        int Unused

        BUILD 0.5.3.3368, 0.5.5.3494
        BUILD 0.6.0.3592-0.7.6.3712
        $id$ID<32>
        Directory
        Title_lang
        Unused<32>

        LAYOUT 0A0B0C0D, 11223344
        BUILD 9.9.9.99999
        BUILD 1.60.1.70000-1.60.1.70999
        COMMENT two layouts, one range
        $noninline,id$ID<32>
        Directory
        Title_lang
        Pos[3]
        OtherID<u16> // a link
        Flags<8>[2]
        $noninline,relation$OtherID<32>

        LAYOUT DEADBEEF
        BUILD 1.60.1.70450
        $id$ID<32>
        Directory
        """;

    [Fact]
    public void TheParserReadsColumnsAndDefinitions()
    {
        var f = DbdFile.Parse(Sample.Replace("\n", "\r\n"), "Sample");
        Assert.Equal("Sample", f.Table);
        Assert.Equal(7, f.Columns.Count);
        Assert.Equal(("string", "the folder"), (f.Columns["Directory"].Type, f.Columns["Directory"].Comment));
        Assert.True(f.Columns["Title_lang"].IsString);
        Assert.Equal(("Other", "ID"), (f.Columns["OtherID"].ForeignTable, f.Columns["OtherID"].ForeignColumn));
        Assert.True(f.Columns["Pos"].IsFloat);
        Assert.False(f.Columns["Flags"].Verified);
        Assert.True(f.Columns["Unused"].Verified);
        Assert.Equal(3, f.Definitions.Count);

        var old = f.Definitions[0];
        Assert.Empty(old.LayoutHashes);
        Assert.Equal(2, old.Builds.Count);
        Assert.Single(old.Ranges);
        Assert.True(old.HasBuild(new DbdBuild(0, 7, 0, 3694)));      // inside the range
        Assert.False(old.HasBuild(new DbdBuild(0, 8, 0, 3734)));
        Assert.Equal(4, old.ColumnCount);                              // the ID is in the record

        var mid = f.Definitions[1];
        Assert.Equal(new uint[] { 0x0A0B0C0D, 0x11223344 }, mid.LayoutHashes);
        Assert.Equal("two layouts, one range", mid.Comment);
        Assert.True(mid.HasBuild(new DbdBuild(1, 60, 1, 70450)));
        var id = mid.Fields[0];
        Assert.True(id.IsId && id.NonInline && id.Size == 32 && !id.Unsigned);
        var pos = mid.Fields.Single(x => x.Name == "Pos");
        Assert.Equal(3, pos.ArrayLength);
        var link = mid.Fields.First(x => x.Name == "OtherID");
        Assert.True(link.Unsigned && link.Size == 16 && link.Comment == "a link" && !link.NonInline);
        var flags = mid.Fields.Single(x => x.Name == "Flags");
        Assert.Equal((8, false, 2), (flags.Size, flags.Unsigned, flags.ArrayLength));
        Assert.True(mid.Fields[^1].IsRelation && mid.Fields[^1].NonInline);
        Assert.Equal(1 + 1 + 3 + 1 + 2, mid.ColumnCount);             // the non-inline ID and relation are not in the record
        Assert.Equal(new DbdBuild(1, 60, 1, 70450), DbdBuild.TryParse("1.60.1.70450", out var b) ? b : default);
        Assert.True(DbdBuild.TryParse("3.3.5a.12340", out var wotlk) && wotlk == new DbdBuild(3, 3, 5, 12340));
        Assert.False(DbdBuild.TryParse("1.60.1", out _));
    }

    [Fact]
    public void ADefinitionIsChosenByBuildAndLayoutHashOrSaysWhyNot()
    {
        var f = DbdFile.Parse(Sample, "Sample");
        // the build is listed twice: the layout hash decides
        Assert.Same(f.Definitions[2], f.Find(new DbdBuild(1, 60, 1, 70450), 0xDEADBEEF, out var kind, out _));
        Assert.Equal(DbdFile.MatchKind.BuildAndLayout, kind);
        Assert.Same(f.Definitions[1], f.Find(new DbdBuild(1, 60, 1, 70450), 0x11223344, out kind, out _));
        // a listed build with another layout: none, and why
        Assert.Null(f.Find(new DbdBuild(1, 60, 1, 70450), 0x01010101, out kind, out var why));
        Assert.Equal(DbdFile.MatchKind.None, kind);
        Assert.Contains("is for layout 0A0B0C0D, 11223344, DEADBEEF, the file has layout 01010101", why);
        // a build not listed: the layout hash alone
        Assert.Same(f.Definitions[1], f.Find(new DbdBuild(2, 0, 0, 1), 0x0A0B0C0D, out kind, out _));
        Assert.Equal(DbdFile.MatchKind.Layout, kind);
        Assert.Null(f.Find(new DbdBuild(2, 0, 0, 1), 0x01010101, out _, out why));
        Assert.Contains("has no definition for 2.0.0.1 or layout 01010101", why);
        // an old table without a layout hash (WDBC, WDB2): the build alone
        Assert.Same(f.Definitions[0], f.Find(new DbdBuild(0, 5, 5, 3494), null, out kind, out _));
        Assert.Equal(DbdFile.MatchKind.Build, kind);
        Assert.Null(f.Find(new DbdBuild(4, 0, 0, 1), null, out _, out why));
        Assert.Contains("has no definition for 4.0.0.1", why);
    }

    [Fact]
    public void TextTheParserCannotReadGivesTheLine()
    {
        Assert.Contains("line 1: the file must start with COLUMNS", Assert.Throws<DbdFormatException>(() => DbdFile.Parse("int ID\n", "X")).Message);
        Assert.Contains("line 2: unknown column type \"byte\"", Assert.Throws<DbdFormatException>(() => DbdFile.Parse("COLUMNS\nbyte ID\n", "X")).Message);
        Assert.Contains("line 5: the field Missing is not in COLUMNS", Assert.Throws<DbdFormatException>(() => DbdFile.Parse("COLUMNS\nint ID\n\nBUILD 1.0.0.1\nMissing\n", "X")).Message);
        Assert.Contains("line 5: \"ID<12>\" has a size", Assert.Throws<DbdFormatException>(() => DbdFile.Parse("COLUMNS\nint ID\n\nBUILD 1.0.0.1\nID<12>\n", "X")).Message);
        Assert.Contains("line 4: \"1.x\" is not a build", Assert.Throws<DbdFormatException>(() => DbdFile.Parse("COLUMNS\nint ID\n\nBUILD 1.x\nID\n", "X")).Message);
        Assert.Contains("is not a layout hash", Assert.Throws<DbdFormatException>(() => DbdFile.Parse("COLUMNS\nint ID\n\nLAYOUT XYZ\nID\n", "X")).Message);
    }

    [Fact]
    public void TheFolderIsFoundCheckedAndReadAgainWhenAFileChanges()
    {
        Assert.Null(DbdFolder.Resolve(null));
        Assert.Null(DbdFolder.Check("", out _));
        Assert.Contains("does not exist", DbdFolder.Check(Path.Combine(_root, "nope"), out _));
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        Assert.Contains("has no WoWDBDefs definition files", DbdFolder.Check(empty, out _));
        Assert.Null(DbdFolder.Open(empty));
        // a copy of the whole repository: its definitions folder is used
        var repo = Path.Combine(_root, "WoWDBDefs-copy");
        var defs = SyntheticDbd.WriteFolder(Path.Combine(repo, "definitions"));
        Assert.Equal(Path.GetFullPath(defs), DbdFolder.Resolve(repo));
        Assert.Null(DbdFolder.Check(repo, out var found));
        Assert.StartsWith("4 WoWDBDefs definition files (.dbd) in ", found);
        var d = DbdFolder.Open(repo)!;
        Assert.Same(d, DbdFolder.Open(defs));                         // the same folder: the same object
        Assert.Equal(4, d.Count);
        Assert.True(d.Has("map") && !d.Has("Spell"));
        Assert.Equal($"WoWDBDefs (CC BY-SA 4.0), from {Path.GetFullPath(defs)}", d.Attribution);
        Assert.NotNull(d.Get("Map"));
        // a file that cannot be read: the reason; once fixed it is read again
        var bad = Path.Combine(defs, "Map.dbd");
        File.WriteAllText(bad, "COLUMNS\nbyte ID\n");
        File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddMinutes(1));
        Assert.Null(d.Get("Map", out var err));
        Assert.Contains("unknown column type", err);
        Assert.False(d.Knows("Map", "1.60.1.70450", null, out var why));
        Assert.Contains("Map.dbd could not be read", why);
        File.WriteAllText(bad, SyntheticDbd.For("Map"));
        File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddMinutes(2));
        Assert.NotNull(d.Get("Map"));
        Assert.True(d.Knows("Map", "1.60.1.70450", null, out _));
    }

    private static Db2Table SyntheticTable(string name)
    {
        var (rows, locked) = Tables()[name];
        return Db2Table.Read(Wdc5(name, rows, locked).Bytes, new Db2ReadOptions { Name = name });
    }

    [Fact]
    public void ADefinitionNamesASyntheticTableOrLeavesItUnchangedWithTheReason()
    {
        var d = DbdFolder.Open(SyntheticDbd.WriteFolder(Path.Combine(_root, "defs")))!;
        var map = SyntheticTable("Map");
        Assert.True(d.Apply(map, "1.60.1.70450", out var why), why);
        Assert.Equal("D_Directory", map.Columns[0].Name);
        Assert.Equal(Db2ValueType.String, map.Columns[0].Type);       // the strings are read as strings
        Assert.Equal("TestMap", map.Records[0].Text(map.ColumnIndex("D_Directory")));
        Assert.Equal(new[] { "D_Flags1", "D_Flags2", "D_Flags3" }, map.Columns.TakeLast(3).Select(c => c.Name));   // Flags[3]
        Assert.Null(map.Layout);
        // the same table of a build the file does not list, and a layout it does not know: unchanged, with the reason
        var other = SyntheticTable("Map");
        Assert.True(d.Apply(other, "1.60.1.99999", out _));            // the layout hash alone still fits
        var cmd = SyntheticTable("CreatureModelData");
        Assert.False(d.Apply(cmd, "1.60.1.70450", out why));
        Assert.Contains("CreatureModelData.dbd's definition for 1.60.1.70450 is for layout 12345678", why);
        Assert.StartsWith("col_", cmd.Columns[0].Name);
        // a definition with one column too few: refused, never shifted
        var defs2 = Directory.CreateDirectory(Path.Combine(_root, "defs2")).FullName;
        File.WriteAllText(Path.Combine(defs2, "Map.dbd"), SyntheticDbd.For("Map", dropColumns: 1));
        var short1 = SyntheticTable("Map");
        Assert.False(DbdFolder.Open(defs2)!.Apply(short1, "1.60.1.70450", out why));
        Assert.Contains("Map.dbd's definition for 1.60.1.70450 has 28 columns in the record, the file 29", why);
        Assert.StartsWith("col_", short1.Columns[0].Name);
        Assert.False(d.Apply(SyntheticTable("SpellName"), "1.60.1.70450", out why));
        Assert.Equal("no SpellName.dbd in the WoWDBDefs folder", why);
    }

    [Fact]
    public void WoWDBDefsComeFirstThenOurMeasuredNamesThenColN()
    {
        var d = DbdFolder.Open(SyntheticDbd.WriteFolder(Path.Combine(_root, "defs")))!;
        var n = ColumnNames.Apply(SyntheticTable("Map"), 70450, "1.60.1.70450", d);
        Assert.Equal((ColumnNameSource.WoWDBDefs, ""), (n.Source, n.Fallback));
        Assert.Equal("WoWDBDefs", n.Text);
        // no folder: our measured file, as before M5
        var ours = SyntheticTable("Map");
        n = ColumnNames.Apply(ours, 70450, "1.60.1.70450", null);
        Assert.Equal((ColumnNameSource.Measured, ""), (n.Source, n.Fallback));
        Assert.Equal("Directory", ours.Columns[0].Name);
        // a definition that does not fit: our measured names, with the reason
        n = ColumnNames.Apply(SyntheticTable("CreatureModelData"), 70450, "1.60.1.70450", d);
        Assert.Equal(ColumnNameSource.Measured, n.Source);
        Assert.StartsWith("measured (our layout file); not WoWDBDefs: CreatureModelData.dbd's definition", n.Long);
        // a build without our layout file and no definition: col_N
        n = ColumnNames.Apply(SyntheticTable("SpellName"), 99999, "9.9.9.99999", d);
        Assert.Equal((ColumnNameSource.None, "no SpellName.dbd in the WoWDBDefs folder"), (n.Source, n.Fallback));
        Assert.Equal(ColumnNameSource.WoWDBDefs, ColumnNames.Expected("Map", 0xD43AFAC3, 70450, "1.60.1.70450", d).Source);
        Assert.Equal(ColumnNameSource.Measured, ColumnNames.Expected("SpellName", null, 70450, "1.60.1.70450", d).Source);
        Assert.Equal(ColumnNameSource.None, ColumnNames.Expected("SpellName", null, 99999, "9.9.9.99999", null).Source);
    }

    [Fact]
    public void TheTableViewsOfAnotherVersionShowTheNameSource()
    {
        var (flavor, _) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        var defs = SyntheticDbd.WriteFolder(Path.Combine(_root, "defs"));
        using var oc = OtherClient.Open(ClientCopies.Inspect(flavor)!);
        Assert.Equal("1.60.1.70450", oc.VersionOf());
        var before = oc.TableView("Map")!;
        Assert.Equal(ColumnNameSource.Measured, before.Naming.Source);
        Assert.Contains("MapName", before.Columns);
        oc.Dbd = DbdFolder.Open(defs);
        var map = oc.TableView("Map")!;
        Assert.Equal(ColumnNameSource.WoWDBDefs, map.Naming.Source);
        Assert.Equal("ID", map.Columns[0]);                            // the ID list
        Assert.Contains("D_MapName", map.Columns);
        Assert.False(map.IsRaw);
        Assert.StartsWith("All columns named (WoWDBDefs)", map.Coverage);
        Assert.Equal("Test Continent", map.Row(0)![VersionTableExport.Column(map, "D_MapName")]);
        // the relation of a child table is not a column; the parent ID stays the reader's
        var res = oc.TableView("ItemDisplayInfoMaterialRes")!;
        Assert.Equal(ColumnNameSource.WoWDBDefs, res.Naming.Source);
        Assert.Contains("D_ComponentSection", res.Columns);
        // fallback with the reason, and a table WoWDBDefs does not have
        var cmd = oc.TableView("CreatureModelData")!;
        Assert.Equal(ColumnNameSource.Measured, cmd.Naming.Source);
        Assert.Contains("is for layout 12345678", cmd.Naming.Fallback);
        Assert.Equal(ColumnNameSource.Measured, oc.ExpectedNaming("CreatureDisplayInfo").Source);
        Assert.Equal(ColumnNameSource.WoWDBDefs, oc.ExpectedNaming("Map").Source);
        // compare: our "MapName" and a "MapName_lang" are one column
        var lang = Directory.CreateDirectory(Path.Combine(_root, "lang")).FullName;
        File.WriteAllText(Path.Combine(lang, "Map.dbd"), SyntheticDbd.For("Map").Replace("D_MapName", "MapName_lang").Replace("D_Directory", "Directory"));
        using var oc2 = OtherClient.Open(ClientCopies.Inspect(flavor)!);
        oc2.Dbd = DbdFolder.Open(lang);
        var t2 = oc2.TableView("Map")!;
        Assert.Contains("MapName_lang", t2.Columns);
        Assert.Equal(VersionTableExport.Column(t2, "MapName_lang"), VersionTableExport.Column(t2, "MapName"));
        var cmp = VersionCompare.Row("Map", 0, new List<(string, VersionTable?)> { ("ours", before), ("WoWDBDefs", t2) });
        var name = cmp.Columns.Single(c => c.Column == "MapName");
        Assert.Equal(new[] { "Test Continent", "Test Continent" }, name.Values);
        Assert.Contains("WoWDBDefs: column names from WoWDBDefs.", cmp.Notes);
    }

    [Fact]
    public void TheSettingIsCheckedLikeTheSettingsWindow()
    {
        var s = new AppSettings();
        Assert.Equal("", SettingsCatalog.Get(s, "other-clients.dbd"));
        Assert.Contains("has no WoWDBDefs definition files", Assert.Throws<ArgumentException>(() => SettingsCatalog.Set(s, "other-clients.dbd", Directory.CreateDirectory(Path.Combine(_root, "e")).FullName)).Message);
        Assert.Contains("does not exist", Assert.Throws<ArgumentException>(() => SettingsCatalog.Set(s, "other-clients.dbd", Path.Combine(_root, "missing"))).Message);
        var defs = SyntheticDbd.WriteFolder(Path.Combine(_root, "defs"));
        SettingsCatalog.Set(s, "other-clients.dbd", defs);
        Assert.Equal(defs, SettingsCatalog.Get(s, "other-clients.dbd"));
        Assert.Equal("WoWDBDefs folder (optional)", SettingsCatalog.Info("other-clients.dbd")!.Label);
        SettingsCatalog.Unset(s, "other-clients.dbd");
        Assert.Null(s.OtherClientsDbd);
        // the Settings window's field says the same
        var vm = new WoWDataStudio.ViewModels.SettingsViewModel(s, "") { DbdFolder = defs };
        Assert.True(vm.DbdOk);
        Assert.StartsWith("Found 4 WoWDBDefs definition files", vm.DbdCheck);
        Assert.True(vm.DbdChanged);
        Assert.Null(vm.ApplyOtherVersions(s));
        Assert.Equal(defs, s.OtherClientsDbd);
        vm.DbdFolder = Path.Combine(_root, "missing");
        Assert.False(vm.DbdOk);
        Assert.NotNull(vm.ApplyOtherVersions(new AppSettings()));
    }

    private CliRun Run(string home, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.RunAsync(new[] { "--home", home }.Concat(args).ToArray(), o, e).GetAwaiter().GetResult();
        return new CliRun(code, o.ToString(), e.ToString());
    }

    [Fact]
    public void TheCommandLineShowsTheNameSourceOfEveryTable()
    {
        var home = Path.Combine(_root, "home");
        var (flavor, lf) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        var defs = SyntheticDbd.WriteFolder(Path.Combine(_root, "WoWDBDefs", "definitions"));
        Assert.Equal(0, Run(home, "settings", "set", "other-clients.listfile", lf).Code);
        // without a folder: as before (our measured names)
        var t0 = Run(home, "dbc", "tables", "--version", flavor, "--json");
        Assert.True(t0.Code == 0, t0.ToString());
        Assert.Contains(t0.Data.GetProperty("tables").EnumerateArray(), t => t.GetProperty("name").GetString() == "Map" && t.GetProperty("names").GetString() == "measured");
        Assert.Equal(ExitCode.ValidationFailed, Run(home, "settings", "set", "other-clients.dbd", Path.Combine(_root, "bnet")).Code);
        var set = Run(home, "settings", "set", "other-clients.dbd", Path.Combine(_root, "WoWDBDefs"));
        Assert.True(set.Code == 0, set.ToString());
        Assert.Contains("Found 4 WoWDBDefs definition files", set.Out);

        var tables = Run(home, "dbc", "tables", "--version", flavor, "--json");
        Assert.True(tables.Code == 0, tables.ToString());
        var byName = tables.Data.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("names").GetString());
        Assert.Equal("WoWDBDefs", byName["Map"]);
        Assert.Equal("WoWDBDefs", byName["CreatureModelData"]);        // expected (the stand-in executable gives no layout hash); reading it falls back, below
        Assert.Equal("measured", byName["CreatureDisplayInfo"]);       // no definition
        Assert.Equal(Path.GetFullPath(defs), tables.Data.GetProperty("dbd").GetString());
        var text = Run(home, "dbc", "tables", "--version", flavor);
        Assert.Contains("Column names: 4 tables from WoWDBDefs (CC BY-SA 4.0), from " + Path.GetFullPath(defs), text.Out);

        var info = Run(home, "dbc", "info", "Map", "--version", flavor);
        Assert.True(info.Code == 0, info.ToString());
        Assert.Contains("Column names: WoWDBDefs (CC BY-SA 4.0), from ", info.Out);
        Assert.Contains("D_MapName", info.Out);
        var cmd = Run(home, "dbc", "info", "CreatureModelData", "--version", flavor, "--json");
        Assert.Equal("measured", cmd.Data.GetProperty("names").GetString());
        Assert.Contains("is for layout 12345678", cmd.Data.GetProperty("namesFallback").GetString());
        var rows = Run(home, "dbc", "rows", "Map", "--where", "MapName=continent", "--version", flavor, "--json");   // our name is not a column now
        Assert.Equal(ExitCode.NotFound, rows.Code);
        rows = Run(home, "dbc", "rows", "Map", "--where", "D_MapName=continent", "--version", flavor, "--json");
        Assert.True(rows.Code == 0, rows.ToString());
        Assert.Equal(("WoWDBDefs", 1), (rows.Data.GetProperty("names").GetString(), rows.Data.GetProperty("matched").GetInt32()));

        // the Other versions tab's commands: versions tables / rows / compare
        var vt = Run(home, "versions", "tables", flavor, "--json");
        Assert.True(vt.Code == 0, vt.ToString());
        Assert.Contains(vt.Data.GetProperty("tables").EnumerateArray(), t => t.GetProperty("name").GetString() == "Map" && t.GetProperty("names").GetString() == "WoWDBDefs");
        var vtr = Run(home, "versions", "tables", flavor, "--read", "--json");
        Assert.Contains(vtr.Data.GetProperty("tables").EnumerateArray(), t => t.GetProperty("name").GetString() == "CreatureModelData" && t.GetProperty("names").GetString() == "measured"
                                                                              && t.GetProperty("namesFallback").GetString()!.Contains("12345678", StringComparison.Ordinal));
        var vr = Run(home, "versions", "rows", flavor, "Map");
        Assert.True(vr.Code == 0, vr.ToString());
        Assert.Contains("Column names: WoWDBDefs (CC BY-SA 4.0), from ", vr.Out);
        Assert.Contains("D_Directory", vr.Out);
        var cmp = Run(home, "versions", "compare", "Map", "0", "--clients", flavor, "--folder", Path.Combine(_root, "bnet"), "--json");
        Assert.True(cmp.Code == 0, cmp.ToString());
        Assert.Contains(cmp.Data.GetProperty("notes").EnumerateArray(), n => n.GetString()!.EndsWith("column names from WoWDBDefs.", StringComparison.Ordinal));
        // nothing was written into the definitions folder
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(_root, "WoWDBDefs"), "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void TheReadOnlyDataTablesTabShowsTheNameSource()
    {
        var (flavor, lf) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        var defs = SyntheticDbd.WriteFolder(Path.Combine(_root, "defs"));
        MainWindowVersionTests.OnDispatcher(async () =>
        {
            var paths = new AppPaths(Path.Combine(_root, "apphome"));
            using var session = new AppSession(paths, AppSettings.Load(paths));
            session.Settings.OtherClientsListfile = lf;
            var main = new WoWDataStudio.ViewModels.MainViewModel(session);
            Assert.True(await main.OpenOtherClientAsync(ClientCopies.Inspect(flavor)!, null), main.WelcomeError);
            var t = main.Browser!.VersionTables;
            await t.EnsureLoadedAsync();
            Assert.StartsWith("Column names: our measured layout file", t.NamesSource);
            Assert.Equal("column names: measured (our layout file)", t.Tables.Single(x => x.Name == "Map").Columns);
            // a WoWDBDefs folder chosen in Settings: the tab lists the tables again
            session.Settings.OtherClientsDbd = defs;
            session.Other!.Dbd = DbdFolder.Open(defs);
            await t.ReloadAsync();
            Assert.Contains("(4 named by WoWDBDefs, ", t.Status);
            await t.SelectTableAsync("Map");
            Assert.Equal($"Column names: WoWDBDefs (CC BY-SA 4.0), from {Path.GetFullPath(defs)}; else our measured layout file, else col_N.", t.NamesSource);
            Assert.Equal("column names: WoWDBDefs", t.Tables.Single(x => x.Name == "Map").Columns);
            Assert.Equal(ColumnNameSource.WoWDBDefs, t.Table!.Naming.Source);
            Assert.Contains("D_MapName", t.Table.Columns);
            Assert.Contains("Column names: WoWDBDefs.", t.TableNote);
            await t.SelectTableAsync("CreatureModelData");
            Assert.Contains("Column names: measured (our layout file); not WoWDBDefs: CreatureModelData.dbd's definition for 1.60.1.70450 is for layout 12345678", t.TableNote);
        });
    }

    // ------------------------------------------------------------------ the disagreement report (tests only)

    [Fact]
    public void TheDisagreementReportListsLayoutHashColumnCountAndNameDifferences()
    {
        var defs = Directory.CreateDirectory(Path.Combine(_root, "defs")).FullName;
        File.WriteAllText(Path.Combine(defs, "Map.dbd"), SyntheticDbd.For("Map").Replace("D_Directory", "Directory").Replace("D_MapName", "MapName_lang"));
        File.WriteAllText(Path.Combine(defs, "AreaTable.dbd"), SyntheticDbd.For("AreaTable", layoutHash: 0x0BADF00D));
        File.WriteAllText(Path.Combine(defs, "LiquidType.dbd"), SyntheticDbd.For("LiquidType", dropColumns: 2));
        var report = DbdDisagreement.Report(Db2Layouts.ForBuild(70450)!, DbdFolder.Open(defs)!, "1.60.1.70450");
        Assert.Contains(report, r => r.Table == "AreaTable" && r.Kind == DbdDisagreement.Kinds.LayoutHash && r.Text.Contains("0BADF00D", StringComparison.Ordinal));
        Assert.Contains(report, r => r.Table == "LiquidType" && r.Kind == DbdDisagreement.Kinds.ColumnCount);
        // names: Directory and MapName agree (with "_lang" left out), the other columns are named differently
        var names = report.Where(r => r.Table == "Map" && r.Kind == DbdDisagreement.Kinds.Name).ToList();
        Assert.DoesNotContain(names, r => r.Text.Contains("Directory", StringComparison.Ordinal) || r.Text.StartsWith("MapName ", StringComparison.Ordinal));
        Assert.Contains(names, r => r.Text.StartsWith("MapDescription0 ", StringComparison.Ordinal) && r.Text.Contains("D_MapDescription0", StringComparison.Ordinal));
        Assert.DoesNotContain(report, r => r.Table == "Map" && r.Kind != DbdDisagreement.Kinds.Name);
        Assert.Contains(report, r => r.Table == "CreatureDisplayInfo" && r.Kind == DbdDisagreement.Kinds.NoDefinition);
    }
}

/// <summary>
/// The disagreement report between our measured layout file and a WoWDBDefs folder (design 4.3; tests only, never
/// used by the app, never writes names): per table of our file, whether WoWDBDefs has a definition for the build, the
/// same layout hash, the same column count, and the columns named differently (for a human to look at).
/// </summary>
public static class DbdDisagreement
{
    public enum Kinds { NoDefinition, LayoutHash, ColumnCount, Name }

    public sealed record Line(string Table, Kinds Kind, string Text);

    public static List<Line> Report(Db2Layouts measured, DbdFolder dbd, string version)
    {
        var res = new List<Line>();
        DbdBuild.TryParse(version, out var build);
        foreach (var l in measured.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var f = dbd.Get(l.Name, out var err);
            var hash = measured.LayoutHash(l.Name);
            var d = f?.Find(build, null, out _, out _);
            if (f == null || d == null)
            {
                res.Add(new Line(l.Name, Kinds.NoDefinition, f == null ? (err.Length > 0 ? err : $"no {l.Name}.dbd") : $"{l.Name}.dbd has no definition for {version}"));
                continue;
            }
            var withHash = hash is { } h ? f.Find(build, h, out _, out _) : d;
            if (withHash == null)
            {
                res.Add(new Line(l.Name, Kinds.LayoutHash, $"ours {hash:X8}, WoWDBDefs {string.Join(", ", d.LayoutHashes.Select(x => x.ToString("X8", CultureInfo.InvariantCulture)))}"));
                continue;
            }
            d = withHash;
            int ours = l.Columns.Count;
            if (d.ColumnCount != ours)
            {
                res.Add(new Line(l.Name, Kinds.ColumnCount, $"ours {ours} columns, WoWDBDefs {d.ColumnCount}"));
                continue;
            }
            var theirs = d.InlineFields.SelectMany(x => x.ArrayLength > 0 ? Enumerable.Range(1, x.ArrayLength).Select(i => x.Name + i.ToString(CultureInfo.InvariantCulture)) : new[] { x.Name }).ToList();
            for (int i = 0; i < ours; i++)
            {
                var a = l.Columns[i].Name;
                if (a.StartsWith("col_", StringComparison.Ordinal)) continue;
                if (!string.Equals(VersionTableExport.LogicalName(a), VersionTableExport.LogicalName(theirs[i]), StringComparison.OrdinalIgnoreCase))
                    res.Add(new Line(l.Name, Kinds.Name, $"{a} (ours, column {i}) is {theirs[i]} in WoWDBDefs"));
            }
        }
        return res;
    }
}
