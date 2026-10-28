using System.IO;
using System.Linq;
using System.Text.Json;
using WoWData.Cli;
using WoWData.Core.Features;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// M1: the command line of another version opened read only, on the synthetic "1.60" client (our own bytes), in an
/// isolated home (never the user's settings): "wds versions open", the commands that read with --version, and the
/// parity rule that every other command refuses --version with exit code 4 (FeatureRegistry's VersionAware mark).
/// </summary>
public class VersionOpenCliTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("vopen");
    private readonly string _home, _flavor, _listfile;

    public VersionOpenCliTests()
    {
        _home = Path.Combine(_root, "home");
        (_flavor, _listfile) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        Assert.Equal(0, Run("settings", "set", "other-clients.listfile", _listfile).Code);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private CliRun Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.RunAsync(new[] { "--home", _home }.Concat(args).ToArray(), o, e).GetAwaiter().GetResult();
        return new CliRun(code, o.ToString(), e.ToString());
    }

    /// <summary>A run of every VersionAware command (the parity test checks this list is complete).</summary>
    private Dictionary<string, string[]> Samples()
    {
        var outDir = Path.Combine(_root, "out");
        return new Dictionary<string, string[]>
        {
            ["find search"] = new[] { "find", "search", "testcube" },
            ["find list"] = new[] { "find", "list", "--type", "texture" },
            ["find info"] = new[] { "find", "info", "cdisplay:1" },
            ["find files"] = new[] { "find", "files", @"Creature\TestCube\*" },
            ["find extract"] = new[] { "find", "extract", IconName, "--out", Path.Combine(outDir, "icon.blp") },
            ["textures info"] = new[] { "textures", "info", IconName },
            ["textures export"] = new[] { "textures", "export", IconName, "--out", Path.Combine(outDir, "icon.png") },
            ["textures export-all"] = new[] { "textures", "export-all", "cdisplay:1", "--out", Path.Combine(outDir, "all") },
            ["versions open"] = new[] { "versions", "open" },
            // M2: the models of another version
            ["models info"] = new[] { "models", "info", "cdisplay:1" },
            ["models anims"] = new[] { "models", "anims", CubeName },
            ["models skeleton"] = new[] { "models", "skeleton", "#" + CubeModel },
            ["models render"] = new[] { "models", "render", "cdisplay:1", Path.Combine(outDir, "cube.png"), "--size", "160x120" },
            // M4: the maps of another version
            ["maps list"] = new[] { "maps", "list" },
            ["maps zones"] = new[] { "maps", "zones", SyntheticMaps.MapDir },
            ["maps tiles"] = new[] { "maps", "tiles", SyntheticMaps.MapDir },
            ["maps minimap"] = new[] { "maps", "minimap", SyntheticMaps.MapDir, "--out", Path.Combine(outDir, "map.png") },
            ["maps tile"] = new[] { "maps", "tile", SyntheticMaps.MapDir, "32", "32" },
            ["maps render"] = new[] { "maps", "render", SyntheticMaps.MapDir, "32", "32", "--out", Path.Combine(outDir, "relief.png") },
            // the main window's other tabs and exports for another version: Used in game (no server), Export for Blender,
            // the read-only Data tables tab (rows, CSV / JSON), Rebuild the index
            ["find used"] = new[] { "find", "used", "cdisplay:1" },
            ["models export"] = new[] { "models", "export", "cdisplay:1", "--out", Path.Combine(outDir, "models") },
            ["dbc tables"] = new[] { "dbc", "tables" },
            ["dbc info"] = new[] { "dbc", "info", "CreatureDisplayInfo" },
            ["dbc rows"] = new[] { "dbc", "rows", "CreatureDisplayInfo" },
            ["dbc get"] = new[] { "dbc", "get", "CreatureDisplayInfo", "1" },
            ["dbc export"] = new[] { "dbc", "export", "CreatureDisplayInfo", "--out", Path.Combine(outDir, "cdi.csv") },
            ["index build"] = new[] { "index", "build" },
        };
    }

    [Fact]
    public void EveryVersionAwareCommandRunsAndEveryOtherCommandRefuses()
    {
        var aware = CliApp.Tree.Where(c => c.VersionAware).Select(c => c.Path).OrderBy(p => p).ToList();
        var samples = Samples();
        Assert.Equal(samples.Keys.OrderBy(p => p), aware);
        foreach (var (path, args) in samples)
        {
            var r = Run(args.Concat(new[] { "--version", _flavor, "--json" }).ToArray());
            Assert.True(r.Code == 0, $"wds {path} --version: {r}");
            Assert.True(r.Json.GetProperty("ok").GetBoolean(), path);
        }
        // every other command: exit code 4 with one line, nothing done (the arguments do not even matter)
        foreach (var c in CliApp.Tree.Where(c => !c.VersionAware))
        {
            var r = Run(c.Path.Split(' ').Concat(new[] { "--version", _flavor }).ToArray());
            Assert.True(r.Code == ExitCode.Refused, $"wds {c.Path} --version: {r}");
            Assert.Contains("1.60.1.70450 is open read only: wds " + c.Path, r.Err);
            // the same reason the app shows on its greyed-out controls
            if (c.Group is "server" or "serverdb" or "server-maps" or "spells" or "classes" or "talents" or "quests")
                Assert.Contains(WoWData.Core.Servers.NoServerProvider.NoServerText("1.60.1.70450"), r.Err);
            else if (r.Err.Contains("changes files", StringComparison.Ordinal))
                Assert.Contains(WoWData.Core.Versions.GameProfile.WriteReason, r.Err);
        }
        // the write commands of the main window's greyed-out controls all say why, the same way
        foreach (var w in new[] { "textures import", "changes build-patch", "changes install", "dbc set", "models import", "maps import-noggit", "changes revert" })
        {
            var r = Run(w.Split(' ').Concat(new[] { "--version", _flavor }).ToArray());
            Assert.Equal(ExitCode.Refused, r.Code);
            Assert.Contains(WoWData.Core.Versions.GameProfile.RefusedTextFor("1.60.1.70450", w), r.Err);
        }
        // the client's folder is never written: only the index in the app's folder
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(_flavor)!, "*", SearchOption.AllDirectories), f => f.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DataTablesUsedInGameAndExportForBlenderWorkOnAnotherVersion()
    {
        // the Data tables tab: tables, a row, rows found by a filter, CSV and JSON exports (never into the client)
        var tables = Run("dbc", "tables", "--filter", "display", "--version", _flavor, "--json");
        Assert.True(tables.Code == 0, tables.ToString());
        Assert.Contains(tables.Data.GetProperty("tables").EnumerateArray(), t => t.GetProperty("name").GetString() == "CreatureDisplayInfo" && t.GetProperty("columns").GetString() == "named");
        Assert.True(tables.Data.GetProperty("readOnly").GetBoolean());
        var get = Run("dbc", "get", "ItemSparse", "25", "Display", "--version", _flavor);
        Assert.True(get.Code == 0, get.ToString());
        Assert.Equal("Test Sword", get.Out.Trim());
        Assert.Equal(ExitCode.NotFound, Run("dbc", "get", "ItemSparse", "26", "--version", _flavor).Code);   // locked: never guessed
        var rows = Run("dbc", "rows", "ItemSparse", "--where", "Display=sword", "--version", _flavor, "--json");
        Assert.True(rows.Code == 0, rows.ToString());
        Assert.Equal(1, rows.Data.GetProperty("matched").GetInt32());
        var csv = Path.Combine(_root, "out", "items.csv");
        Assert.Equal(0, Run("dbc", "export", "ItemSparse", "--out", csv, "--version", _flavor).Code);
        var lines = File.ReadAllLines(csv);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Test Sword", lines[1]);
        var json = Path.Combine(_root, "out", "items.json");
        Assert.Equal(0, Run("dbc", "export", "ItemSparse", "--out", json, "--version", _flavor).Code);
        Assert.Equal("Test Sword", JsonDocument.Parse(File.ReadAllText(json)).RootElement[0].GetProperty("Display").GetString());
        Assert.Equal(ExitCode.Refused, Run("dbc", "export", "ItemSparse", "--out", Path.Combine(_flavor, "x.csv"), "--version", _flavor).Code);

        // "Used in game": what the client says, and that no server is connected (nothing made up)
        var used = Run("find", "used", "cdisplay:1", "--version", _flavor, "--json");
        Assert.True(used.Code == 0, used.ToString());
        var notes = used.Data.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        Assert.Contains(WoWData.Core.Servers.NoServerProvider.NoServerText("1.60.1.70450"), notes);
        Assert.Contains(used.Data.GetProperty("sections").EnumerateArray(), x => x.GetProperty("id").GetString() == "users");
        Assert.False(used.Data.GetProperty("serverConnected").GetBoolean());

        // "Export for Blender": the cube's package with every look, outside the client
        var dir = Path.Combine(_root, "out", "blender");
        var ex = Run("models", "export", CubeName, "--mode", "all", "--out", dir, "--version", _flavor, "--json");
        Assert.True(ex.Code == 0, ex.ToString());
        var folder = ex.Data.GetProperty("folder").GetString()!;
        Assert.StartsWith(dir, folder);
        Assert.True(File.Exists(Path.Combine(folder, "wowdata_manifest.json")));
        Assert.True(ex.Data.GetProperty("readOnly").GetBoolean());
        Assert.Contains(ex.Data.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("cannot be imported back", StringComparison.Ordinal));
        Assert.Equal(ExitCode.Refused, Run("models", "export", CubeName, "--out", Path.Combine(_flavor, "pkg"), "--version", _flavor).Code);
        // "Open in Blender" of another version is "models export --open"; "models open" says so
        var open = Run("models", "open", CubeName, "--version", _flavor);
        Assert.Equal(ExitCode.Refused, open.Code);
        Assert.Contains("--open", open.Err);
        // nothing was written into the client's folder
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(_flavor)!, "*", SearchOption.AllDirectories),
            f => f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) is "x.csv" or "items.csv" or "items.json" or "wowdata_manifest.json");
    }

    [Fact]
    public void VersionsOpenPrintsEntriesNamesAndLockedRecords()
    {
        var r = Run("versions", "open", _flavor, "--json");
        Assert.True(r.Code == 0, r.ToString());
        var d = r.Data;
        Assert.Equal("1.60.1.70450", d.GetProperty("version").GetString());
        Assert.True(d.GetProperty("readOnly").GetBoolean());
        var locked = d.GetProperty("locked").EnumerateArray().ToDictionary(x => x.GetProperty("table").GetString()!, x => (x.GetProperty("readable").GetInt32(), x.GetProperty("locked").GetInt32()));
        Assert.Equal((2, 2), locked["CreatureDisplayInfo"]);
        Assert.Equal((1, 1), locked["ItemSparse"]);
        var names = d.GetProperty("names");
        Assert.Equal(1, names.GetProperty("client").GetInt32());
        Assert.Equal(1, names.GetProperty("manifest").GetInt32());
        Assert.Equal(1, names.GetProperty("listfile").GetInt32());
        Assert.Equal(2, d.GetProperty("verifiedNames").GetInt32());
        Assert.Contains("2 of 4 creature displays readable, 2 locked (encrypted, no key)", d.GetProperty("statusLine").GetString());
        // text form
        var t = Run("versions", "open", _flavor);
        Assert.Contains("CreatureDisplayInfo", t.Out);
        Assert.Contains("1 from your listfile", t.Out);
        // no client: a usage error
        Assert.Equal(ExitCode.Usage, Run("versions", "open").Code);
    }

    [Fact]
    public void ModelsInfoAnimsSkeletonAndRenderReadTheCube()
    {
        var info = Run("models", "info", "cdisplay:1", "--version", _flavor, "--json");
        Assert.True(info.Code == 0, info.ToString());
        var d = info.Data;
        Assert.Equal(CubeName, d.GetProperty("model").GetString());
        Assert.True(d.GetProperty("readOnly").GetBoolean());
        var f = d.GetProperty("format");
        Assert.Equal(274, f.GetProperty("m2Version").GetInt32());
        Assert.Equal(CubeSkin, f.GetProperty("skinFiles")[0].GetUInt32());
        Assert.Equal(12, f.GetProperty("viewTriangles")[0].GetInt32());
        Assert.Equal((1, 1), (f.GetProperty("inlineSequences").GetInt32(), f.GetProperty("externalSequences").GetInt32()));
        Assert.Equal((8, 12, 1), (d.GetProperty("stats").GetProperty("vertices").GetInt32(), d.GetProperty("stats").GetProperty("triangles").GetInt32(), d.GetProperty("stats").GetProperty("bones").GetInt32()));
        // M3: the AFID chunk is used (the .anim files are read when an animation is chosen)
        Assert.Contains(f.GetProperty("chunks").EnumerateArray(), x => x.GetProperty("magic").GetString() == "AFID" && x.GetProperty("used").GetBoolean());
        Assert.Equal(0, d.GetProperty("effects").GetProperty("particles").GetInt32());
        // creature displays 1 and 2 (a skin, an NPC look) and item display 20 (its model texture)
        Assert.Equal(new[] { "creature", "creature", "item" }, d.GetProperty("looks").EnumerateArray().Select(x => x.GetProperty("kind").GetString()));
        var text = Run("models", "info", CubeName, "--version", _flavor);
        Assert.Contains("M2 version 274 (chunked MD21, 1 of 1 view read)", text.Out);
        Assert.Contains($"skin file #{CubeSkin} 12 triangles", text.Out);

        var anims = Run("models", "anims", CubeName, "--version", _flavor, "--json");
        Assert.Equal(0, anims.Code);
        var list = anims.Data.GetProperty("animations").EnumerateArray().ToList();
        Assert.Equal(new[] { "Anim0", "Anim4" }, list.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(new[] { "model", $".anim #{CubeAnim}" }, list.Select(x => x.GetProperty("where").GetString()));
        Assert.Equal(new[] { 1000, 500 }, list.Select(x => x.GetProperty("durationMs").GetInt32()));
        // M3: the external one loads (its .anim file read) with its 4 keys
        Assert.Equal(new[] { false, true }, list.Select(x => x.GetProperty("external").GetBoolean()));
        Assert.Equal((CubeAnim, true, 4), (list[1].GetProperty("animFile").GetUInt32(), list[1].GetProperty("loads").GetBoolean(), list[1].GetProperty("keys").GetInt32()));
        Assert.Equal((1, 1), (anims.Data.GetProperty("external").GetProperty("sequences").GetInt32(), anims.Data.GetProperty("external").GetProperty("load").GetInt32()));
        var animsText = Run("models", "anims", CubeName, "--version", _flavor);
        Assert.Contains("1 kept in .anim files, 1 of them load and play", animsText.Out);
        Assert.Contains("loads (4 keys)", animsText.Out);
        Assert.Equal(ExitCode.NotFound, Run("models", "anims", CubeName, "--sequence", "Moonwalk", "--version", _flavor).Code);
        // M3: a model whose bones and animations are in a skeleton file (and its parent), and one with effects
        var skAnims = Run("models", "anims", "#" + CubeSkelModel, "--version", _flavor, "--json");
        Assert.True(skAnims.Code == 0, skAnims.ToString());
        Assert.Equal(new[] { "skeleton", $".anim #{CubeSkelAnim}", "skeleton" }, skAnims.Data.GetProperty("animations").EnumerateArray().Select(x => x.GetProperty("where").GetString()));
        Assert.Equal((CubeSkel, CubeParentSkel), (skAnims.Data.GetProperty("external").GetProperty("skeletonFile").GetUInt32(), skAnims.Data.GetProperty("external").GetProperty("skeletonParentFile").GetUInt32()));
        var fxInfo = Run("models", "info", "#" + CubeFx, "--version", _flavor, "--json");
        Assert.True(fxInfo.Code == 0, fxInfo.ToString());
        Assert.Equal((1, 1), (fxInfo.Data.GetProperty("effects").GetProperty("particles").GetInt32(), fxInfo.Data.GetProperty("effects").GetProperty("ribbons").GetInt32()));
        Assert.Contains("effects: 1 particle emitter, 1 ribbon (drawn approximately)", Run("models", "info", "#" + CubeFx, "--version", _flavor).Out);

        var sk = Run("models", "skeleton", "#" + CubeModel, "--version", _flavor, "--json");
        Assert.Equal(0, sk.Code);
        Assert.Equal(1, sk.Data.GetProperty("bones").GetArrayLength());
        Assert.True(sk.Data.GetProperty("bones")[0].GetProperty("animated").GetBoolean());

        // a file that is not a model, and one that is not on this computer
        Assert.Equal(ExitCode.NotFound, Run("models", "info", "#" + MissingTexture, "--version", _flavor).Code);
        // the picture: never into the client's folder
        Assert.Equal(ExitCode.Refused, Run("models", "render", CubeName, Path.Combine(_flavor, "x.png"), "--version", _flavor).Code);
        var png = Path.Combine(_root, "out", "cube_walk.png");
        var r = Run("models", "render", CubeName, png, "--sequence", "0", "--time", "500", "--size", "200x150", "--version", _flavor, "--json");
        Assert.True(r.Code == 0, r.ToString());
        var img = WoWData.Core.Imaging.Png.Load(png);
        Assert.Equal((200, 150), (img.Width, img.Height));
        Assert.True(img.Pixels.Distinct().Count() > 4);
    }

    [Fact]
    public void MapsListZonesTilesMinimapTileAndRenderReadTheMaps()
    {
        var list = Run("maps", "list", "--version", _flavor, "--json");
        Assert.True(list.Code == 0, list.ToString());
        var maps = list.Data.EnumerateArray().ToList();
        Assert.Equal(new[] { "TestDungeon", "TestMap", "TestMissing" }, maps.Select(m => m.GetProperty("directory").GetString()).OrderBy(x => x));
        var tm = maps.Single(m => m.GetProperty("directory").GetString() == SyntheticMaps.MapDir);
        Assert.Equal((2, 0), (tm.GetProperty("tiles").GetInt32(), tm.GetProperty("changedFiles").GetInt32()));
        var listText = Run("maps", "list", "--version", _flavor);
        Assert.Contains("1.60.1.70450, read only: Map table: 3 readable rows, 1 locked (encrypted, no key); AreaTable: 3 readable, 1 locked; 1 map without a readable WDT.", listText.Out);
        Assert.Contains("is not on this computer: no tiles to show", listText.Out);

        var zones = Run("maps", "zones", SyntheticMaps.MapDir, "--version", _flavor, "--json");
        Assert.Equal(0, zones.Code);
        Assert.Equal(new[] { "Other Zone", "Test Zone" }, zones.Data.EnumerateArray().Select(z => z.GetProperty("name").GetString()));

        var tiles = Run("maps", "tiles", SyntheticMaps.MapDir, "--version", _flavor);
        Assert.Equal(0, tiles.Code);
        Assert.Contains($@"32,32  World\Maps\TestMap\TestMap_32_32.adt  #{SyntheticMaps.TileARoot}", tiles.Out);
        Assert.Contains("Test Continent: 2 tiles (1.60.1.70450, read only", tiles.Out);

        var tile = Run("maps", "tile", SyntheticMaps.MapDir, "32", "32", "--version", _flavor);
        Assert.True(tile.Code == 0, tile.ToString());
        Assert.Contains("2 doodads (1 models), 1 buildings (WMO), 6 ground textures", tile.Out);
        Assert.Contains("ON THIS TILE", tile.Out);
        Assert.DoesNotContain("server", tile.Out, StringComparison.OrdinalIgnoreCase);
        var locked = Run("maps", "tile", SyntheticMaps.MapDir, "33", "32", "--version", _flavor, "--json");
        Assert.Equal(0, locked.Code);
        Assert.Contains(locked.Data.GetProperty("facts").EnumerateArray(), f => f.GetString()!.Contains("encrypted", StringComparison.Ordinal));
        Assert.Equal(ExitCode.NotFound, Run("maps", "tile", SyntheticMaps.MapDir, "10", "10", "--version", _flavor).Code);
        Assert.Equal(ExitCode.NotFound, Run("maps", "minimap", "TestMissing", "--out", Path.Combine(_root, "out", "m.png"), "--version", _flavor).Code);

        var png = Path.Combine(_root, "out", "testmap.png");
        var mm = Run("maps", "minimap", SyntheticMaps.MapDir, "--out", png, "--cell", "16", "--version", _flavor, "--json");
        Assert.True(mm.Code == 0, mm.ToString());
        Assert.Equal((64, 48), (mm.Data.GetProperty("width").GetInt32(), mm.Data.GetProperty("height").GetInt32()));
        Assert.True(File.Exists(png));
        var relief = Path.Combine(_root, "out", "tile_heights.png");
        var rd = Run("maps", "render", SyntheticMaps.MapDir, "32", "32", "--kind", "heights", "--size", "128", "--out", relief, "--version", _flavor, "--json");
        Assert.True(rd.Code == 0, rd.ToString());
        Assert.Equal(128, WoWData.Core.Imaging.Png.Load(relief).Width);
        // never into the client's folder
        Assert.Equal(ExitCode.Refused, Run("maps", "render", SyntheticMaps.MapDir, "32", "32", "--out", Path.Combine(_flavor, "x.png"), "--version", _flavor).Code);
        Assert.Equal(ExitCode.Refused, Run("maps", "minimap", SyntheticMaps.MapDir, "--out", Path.Combine(_flavor, "x.png"), "--version", _flavor).Code);
        // the editing map commands refuse
        Assert.Equal(ExitCode.Refused, Run("maps", "noggit", SyntheticMaps.MapDir, "--version", _flavor).Code);
        Assert.Equal(ExitCode.Refused, Run("maps", "breakdown", SyntheticMaps.MapDir, "32", "32", "--version", _flavor).Code);
    }

    [Fact]
    public void FindAndTexturesShowFileDataIdsAndExportPng()
    {
        var info = Run("find", "info", "cdisplay:1", "--version", _flavor, "--json");
        Assert.Equal(0, info.Code);
        var files = info.Data.GetProperty("files").EnumerateArray().ToList();
        Assert.Contains(files, f => f.GetProperty("fileDataId").GetUInt32() == CubeModel && f.GetProperty("nameSource").GetString() == "client" && f.GetProperty("state").GetString() == "local");
        Assert.Contains(files, f => f.GetProperty("fileDataId").GetUInt32() == LockedSkin && f.GetProperty("state").GetString() == "encrypted");
        Assert.Contains(info.Data.GetProperty("facts").EnumerateArray(), f => f.GetString()!.Contains("is encrypted (key 0807060504030201)", StringComparison.Ordinal));
        var text = Run("find", "info", "cdisplay:1", "--version", _flavor);
        Assert.Contains("FILES OF 1.60.1.70450", text.Out);

        var listed = Run("find", "files", @"Creature\TestCube\*", "--version", _flavor, "--json");
        Assert.Equal(2, listed.Data.GetProperty("total").GetInt32());

        var png = Path.Combine(_root, "out", "skin.png");
        var ex = Run("textures", "export", "#" + ModelTexture, "--out", png, "--version", _flavor, "--json");
        Assert.Equal(0, ex.Code);
        Assert.True(File.Exists(png));
        Assert.False(File.Exists(Path.Combine(_root, "out", "skin.blp.json")));
        // a locked texture: not found, with the reason
        var locked = Run("textures", "export", "#" + LockedSkin, "--out", Path.Combine(_root, "out", "locked.png"), "--version", _flavor);
        Assert.Equal(ExitCode.NotFound, locked.Code);
        Assert.Contains("encrypted", locked.Err);
        // never into the client's folder
        var inside = Run("find", "extract", IconName, "--out", Path.Combine(_flavor, "x.blp"), "--version", _flavor);
        Assert.Equal(ExitCode.Refused, inside.Code);
        // "--version" alone still prints the version of wds
        var v = Run("--version");
        Assert.Equal(0, v.Code);
        Assert.Contains("WoW Data Studio", v.Out);
    }
}
