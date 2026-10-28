using System.IO;
using System.Linq;
using System.Text.Json;
using WoWData.Cli;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.8.0: the command line (wds) group by group, in-process and as its own process, against isolated homes and FAKE game
/// and server folders (CliTestEnv): the help of every command, usage errors, --json envelopes and the data of every
/// command group, the exit codes (0 ok, 2 usage, 3 validation, 4 refused, 5 needs --yes, 6 not found), and that nothing
/// is written into the real client.
/// </summary>
[Collection(IndexCollection.Name)]
public class CliHelpTests
{
    [Fact]
    public void EveryCommandHasHelpWithExamples()
    {
        using var env = new CliTestEnv("help", withIndex: false);
        Assert.True(CliApp.Tree.Count >= 70, $"only {CliApp.Tree.Count} commands");
        Assert.Equal(CliApp.Tree.Count, CliApp.Tree.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var c in CliApp.Tree)
        {
            var r = env.Run(c.Path.Split(' ').Append("--help").ToArray());
            Assert.True(r.Code == 0, $"{c.Path} --help\n{r}");
            Assert.Contains("Usage: wds " + c.Path, r.Out);
            Assert.Contains("Examples:", r.Out);
            Assert.True(c.Examples.Length > 0 && c.Examples.All(e => e.StartsWith("wds ", StringComparison.Ordinal)), c.Path);
            // the examples parse: their command exists and their options are the command's
            foreach (var ex in c.Examples)
            {
                var words = ex.Split(' ').Skip(1).ToList();
                Assert.True(CliApp.Find(words[0]) != null || words.Count > 1 && CliApp.Find(words[0] + " " + words[1]) != null || words[0].StartsWith("--", StringComparison.Ordinal), $"example of {c.Path}: {ex}");
                foreach (var o in words.Where(w => w.StartsWith("--", StringComparison.Ordinal)).Select(w => w[2..].Split('=')[0]))
                    Assert.True(c.Options.Any(x => x.Name == o) || CliApp.GlobalOptions.Any(x => x.Name == o) || o is "dry-run" or "version", $"example of {c.Path} uses --{o}");
            }
        }
        var main = env.Run("--help");
        Assert.Equal(0, main.Code);
        foreach (var (name, _) in CliApp.Groups) Assert.Contains(name, main.Out);
        foreach (var g in CliApp.Tree.Select(c => c.Group).Distinct())
        {
            var gh = env.Run(g, "--help");
            Assert.True(gh.Code == 0, gh.ToString());
        }
    }

    [Fact]
    public void UsageErrorsAndVersion()
    {
        using var env = new CliTestEnv("usage", withIndex: false);
        Assert.Equal(ExitCode.Usage, env.Run("no-such-group").Code);
        Assert.Equal(ExitCode.Usage, env.Run("dbc", "no-such-command").Code);
        Assert.Equal(ExitCode.Usage, env.Run("dbc", "get", "Spell").Code);                     // missing argument
        Assert.Equal(ExitCode.Usage, env.Run("settings", "get", "client", "extra").Code);       // too many
        Assert.Equal(ExitCode.Usage, env.Run("settings", "list", "--bogus").Code);              // unknown option
        Assert.Equal(ExitCode.Usage, env.Run("settings", "list", "--dry-run").Code);            // no preview here
        Assert.Equal(ExitCode.Usage, env.Run("dbc").Code);                                       // a group alone: its help, as usage
        var j = env.Json("no-such-group");
        Assert.Equal("usage", j.Json.GetProperty("status").GetString());
        var v = env.Json("version");
        Assert.Equal(WoWData.Core.StudioInfo.Version, v.Data.GetProperty("version").GetString());
        Assert.Equal(WoWData.Core.StudioInfo.Build, v.Data.GetProperty("build").GetString());
        var cmds = env.Json("commands");
        Assert.Equal(CliApp.Tree.Count, cmds.Data.GetProperty("commands").GetArrayLength());
        var feats = env.Json("features");
        Assert.Equal(WoWData.Core.Features.FeatureRegistry.All.Count, feats.Data.GetArrayLength());
        // as its own process: the same codes, results on stdout, errors on stderr
        var p = env.RunExe("version");
        Assert.Equal(0, p.Code);
        Assert.Contains(WoWData.Core.StudioInfo.Version, p.Out);
        var bad = env.RunExe("dbc", "get");
        Assert.Equal(ExitCode.Usage, bad.Code);
        Assert.Contains("missing argument", bad.Err);
        Assert.Equal("", bad.Out.Trim());
    }
}

[Collection(IndexCollection.Name)]
public class CliSetupTests
{
    [Fact]
    public void SetupSettingsServerDbAndIndex()
    {
        using var env = new CliTestEnv("setup");
        Assert.Equal(0, env.Run("setup", "check", env.Client).Code);
        Assert.Equal(ExitCode.NotFound, env.Run("setup", "check", env.Work).Code);
        Assert.Equal(ExitCode.NotFound, env.Run("setup", "client", env.Work).Code);
        var c = env.Json("setup", "client");
        Assert.Equal(env.Client, c.Data.GetProperty("client").GetString());
        var st = env.Json("setup", "status");
        Assert.True(st.Data.GetProperty("clientOk").GetBoolean());
        Assert.True(st.Data.GetProperty("index").GetProperty("current").GetBoolean(), st.Out);
        Assert.Equal(0, st.Data.GetProperty("workspace").GetProperty("changes").GetInt32());
        var ix = env.Json("index", "status");
        Assert.True(ix.Data.GetProperty("exists").GetBoolean() && ix.Data.GetProperty("current").GetBoolean());

        // settings: get / set / unset, the checks of the window, the password never printed
        Assert.Equal(ExitCode.ValidationFailed, env.Run("settings", "set", "server.port", "99999").Code);
        Assert.Equal(ExitCode.ValidationFailed, env.Run("settings", "set", "ground-textures", "purple").Code);
        Assert.Equal(ExitCode.ValidationFailed, env.Run("settings", "set", "noggit-client", Path.Combine(env.Client, "Data")).Code);   // never the game folder
        Assert.Equal(ExitCode.NotFound, env.Run("settings", "get", "no.such.key").Code);
        Assert.Equal(0, env.Run("settings", "set", "ground-textures", "specular").Code);
        Assert.Equal("specular", env.Json("settings", "get", "ground-textures").Data.GetProperty("value").GetString());
        Assert.Equal(0, env.Run("settings", "unset", "ground-textures").Code);
        Assert.Equal("as-the-game", env.Json("settings", "get", "ground-textures").Data.GetProperty("value").GetString());
        var pw = env.RunWith("Very$ecret1\n", "settings", "set", "server.password", "--stdin");
        Assert.Equal(0, pw.Code);
        Assert.DoesNotContain("Very$ecret1", pw.Out + pw.Err);
        var list = env.Json("settings", "list");
        Assert.DoesNotContain("Very$ecret1", list.Out);
        Assert.Contains(list.Data.GetProperty("settings").EnumerateArray(), x => x.GetProperty("key").GetString() == "server.password" && x.GetProperty("value").GetString() == "(set)");
        Assert.DoesNotContain("Very$ecret1", File.ReadAllText(new AppPaths(env.Home).SettingsFile));
        Assert.Equal("Very$ecret1", AppSettings.Load(new AppPaths(env.Home)).Server.Password);
        var typed = env.Run("settings", "set", "server.password", "Other1");
        Assert.Contains("--stdin", typed.Err);   // warned about the shell history
        Assert.DoesNotContain("Other1", typed.Out);

        // the server database: the defaults, a refused connection (port 1: never the real server)
        Assert.Equal(0, env.Run("settings", "set", "server.port", "1").Code);
        var test = env.Json("serverdb", "test");
        Assert.Equal(ExitCode.Error, test.Code);
        Assert.False(test.Data.GetProperty("connected").GetBoolean());
        Assert.Equal(0, env.Run("serverdb", "defaults").Code);
        Assert.Equal("3306", env.Json("settings", "get", "server.port").Data.GetProperty("value").GetString());
        Assert.Equal(0, env.Run("settings", "set", "server.enabled", "false").Code);
        Assert.Equal(ExitCode.Error, env.Json("serverdb", "reload").Code);   // turned off: not connected
        Assert.Equal(0, env.Run("settings", "set", "server.enabled", "false").Code);
        Assert.Equal(0, env.Run("settings", "set", "server.port", "1").Code);

        // the manual
        var m = env.Json("manual", "command-line");
        Assert.True(File.Exists(m.Data.GetProperty("file").GetString()), m.Out);
    }

    [Fact]
    public void NoGameFolderIsNotFound()
    {
        var root = TestEnv.TempDir("cli_nogame");
        try
        {
            var o = new StringWriter();
            int code = CliApp.RunAsync(new[] { "--home", Path.Combine(root, "home"), "find", "search", "boar" }, o, new StringWriter()).GetAwaiter().GetResult();
            Assert.Equal(ExitCode.NotFound, code);
        }
        finally { Directory.Delete(root, true); }
    }
}

[Collection(IndexCollection.Name)]
public class CliFindTests
{
    [Fact]
    public void SearchListInfoUsedWhereFilesExtract()
    {
        using var env = new CliTestEnv("find");
        var s = env.Json("find", "search", "boarskinivory", "--type", "texture");
        Assert.Contains(s.Data.GetProperty("results").EnumerateArray(), x => x.GetProperty("key").GetString()!.Equals(@"file:Creature\Boar\BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase));
        foreach (var x in s.Data.GetProperty("results").EnumerateArray())
        {
            Assert.Equal("texture", x.GetProperty("kind").GetString());
            foreach (var k in new[] { "key", "name", "path", "id", "badge" }) Assert.True(x.TryGetProperty(k, out _));
        }
        Assert.Equal(ExitCode.Usage, env.Run("find", "search", "boar", "--type", "dragon").Code);
        var chars = env.Json("find", "list", "--type", "character", "--limit", "0");
        Assert.Equal(chars.Data.GetProperty("total").GetInt32(), chars.Data.GetProperty("shown").GetInt32());
        Assert.True(chars.Data.GetProperty("total").GetInt32() >= 16);
        var boarKey = CliTestEnv.BoarEntry(env);
        var info = env.Json("find", "info", boarKey);
        Assert.Contains(info.Data.GetProperty("textures").EnumerateArray(), t => t.GetProperty("path").GetString()!.EndsWith("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"Creature\Boar\Boar.m2", info.Data.GetProperty("models").EnumerateArray().Select(x => x.GetString()), StringComparer.OrdinalIgnoreCase);
        var used = env.Json("find", "used", @"Creature\Boar\BoarSkinIvory.blp");
        Assert.True(used.Data.GetProperty("sections").GetArrayLength() > 0, used.Out);
        Assert.Equal(ExitCode.NotFound, env.Run("find", "info", "zzzz-nothing-like-this-zzzz").Code);
        var where = env.Json("find", "where", @"DBFilesClient\Spell.dbc");
        Assert.Equal("patch-2.MPQ", where.Data.GetProperty("serves").GetString());
        Assert.True(where.Data.GetProperty("sources").GetArrayLength() >= 2);
        Assert.Equal(ExitCode.NotFound, env.Run("find", "where", @"No\Such\File.blp").Code);
        var files = env.Json("find", "files", @"Creature\Boar\*.blp", "--limit", "0");
        Assert.True(files.Data.GetProperty("total").GetInt32() >= 9);
        var outFile = Path.Combine(env.Work, "Spell.dbc");
        var ex = env.Json("find", "extract", @"DBFilesClient\Spell.dbc", "--out", outFile);
        Assert.Equal(0, ex.Code);
        Assert.Equal(TestEnv.Client.Read(@"DBFilesClient\Spell.dbc"), File.ReadAllBytes(outFile));
        Assert.Equal(ExitCode.Refused, env.Run("find", "extract", @"DBFilesClient\Spell.dbc", "--out", Path.Combine(env.DataDir, "x.dbc")).Code);
    }
}

[Collection(IndexCollection.Name)]
public class CliTextureTests
{
    private const string Boar = @"Creature\Boar\BoarSkinIvory.blp";

    [Fact]
    public void ExportImportCompareRevert()
    {
        using var env = new CliTestEnv("textures");
        var png = Path.Combine(env.Work, "boar.png");
        var ex = env.Json("textures", "export", Boar, "--out", png);
        Assert.True(File.Exists(png) && File.Exists(TextureService.SidecarFor(png)), ex.Out);
        Assert.Equal(Boar, ex.Data.GetProperty("files")[0].GetProperty("path").GetString(), ignoreCase: true);
        // unchanged: nothing to import (exit 0), nothing saved
        var same = env.Json("textures", "import", png);
        Assert.Equal("nothingToImport", same.Data.GetProperty("outcome").GetString());
        Assert.Equal(0, env.Json("changes", "list").Data.GetProperty("changes").GetArrayLength());
        // a red stripe
        var img = Png.Load(png);
        for (int y = 64; y < 128; y++)
            for (int x = 32; x < 160; x++)
            {
                int o = img.Offset(x, y);
                if (img.Pixels[o + 3] == 0) continue;
                img.Pixels[o] = 255; img.Pixels[o + 1] = 0; img.Pixels[o + 2] = 0;
            }
        Png.Save(img, png, true);
        var dry = env.Json("textures", "import", png, "--dry-run");
        Assert.Equal("previewed", dry.Data.GetProperty("outcome").GetString());
        Assert.Contains(dry.Data.GetProperty("facts").EnumerateArray(), f => f.GetString()!.Contains("Colour changed in", StringComparison.Ordinal));
        Assert.False(env.Paths.ChangesFile is var cf && File.Exists(cf) && new Workspace(env.Paths).IsModified(Boar));
        var imp = env.Json("textures", "import", png);
        Assert.Equal("saved", imp.Data.GetProperty("outcome").GetString());
        Assert.True(new Workspace(env.Paths).IsModified(Boar));
        var cmp = env.Json("textures", "compare", Boar, "--heatmap", Path.Combine(env.Work, "heat.png"));
        Assert.True(cmp.Data.GetProperty("colourChangedPercent").GetDouble() > 1);
        Assert.Equal(0.0, cmp.Data.GetProperty("alphaChangedPercent").GetDouble());
        Assert.True(File.Exists(Path.Combine(env.Work, "heat.png")));
        var info = env.Json("textures", "info", Boar);
        Assert.True(info.Data.GetProperty("modified").GetBoolean());
        // the edited texture is exported by default; --original gives the game's
        var e2 = env.Json("textures", "export", Boar, "--out", Path.Combine(env.Work, "e.png"));
        Assert.Equal("workspace", e2.Data.GetProperty("files")[0].GetProperty("source").GetString());
        var e3 = env.Json("textures", "export", Boar, "--original", "--out", Path.Combine(env.Work, "o.png"));
        Assert.Equal("texture.MPQ", e3.Data.GetProperty("files")[0].GetProperty("source").GetString());
        // revert needs --yes
        Assert.Equal(ExitCode.NeedsYes, env.Run("changes", "revert", Boar).Code);
        Assert.True(new Workspace(env.Paths).IsModified(Boar));
        Assert.Equal(0, env.Run("changes", "revert", Boar, "--yes").Code);
        Assert.False(new Workspace(env.Paths).IsModified(Boar));
        Assert.Equal(ExitCode.NotFound, env.Run("textures", "compare", Boar).Code);
        // a PNG without its note needs --target
        File.Delete(TextureService.SidecarFor(png));
        Assert.Equal(ExitCode.Usage, env.Run("textures", "import", png).Code);
        Assert.Equal(0, env.Run("textures", "import", png, "--target", Boar).Code);
        // export-all of an entry
        var all = env.Json("textures", "export-all", CliTestEnv.BoarEntry(env), "--out", Path.Combine(env.Work, "all"));
        Assert.True(all.Data.GetProperty("files").GetArrayLength() >= 1);
    }

    [Fact]
    public void GroundTextureEditBothAndOnlyThis()
    {
        using var env = new CliTestEnv("ground");
        const string dirt = @"Tileset\Durotar\DurotarDirt.blp";
        var png = Path.Combine(env.Work, "dirt.png");
        var ex = env.Json("textures", "export", dirt, "--out", png, "--with-partner");
        Assert.Equal(2, ex.Data.GetProperty("files").GetArrayLength());
        var img = Png.Load(png);
        for (int i = 0; i < img.Pixels.Length; i += 4) img.Pixels[i + 2] = (byte)Math.Min(255, img.Pixels[i + 2] + 80);
        Png.Save(img, png, false);
        var both = env.Json("textures", "import", png);
        Assert.Equal("saved", both.Data.GetProperty("outcome").GetString());
        var saved = both.Data.GetProperty("saved").EnumerateArray().ToList();
        Assert.Equal(2, saved.Count);
        Assert.Equal(saved[0].GetProperty("changeSet").GetString(), saved[1].GetProperty("changeSet").GetString());   // one change set
        Assert.Contains(saved, x => x.GetProperty("path").GetString()!.EndsWith("DurotarDirt_s.blp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, env.Run("changes", "revert-set", saved[0].GetProperty("changeSet").GetString()!, "--yes").Code);
        var one = env.Json("textures", "import", png, "--only-this");
        Assert.Single(one.Data.GetProperty("saved").EnumerateArray());
    }
}

[Collection(IndexCollection.Name)]
public class CliModelTests
{
    [Fact]
    public void InfoValidateExportImportCompare()
    {
        using var env = new CliTestEnv("models");
        var info = env.Json("models", "info", CliTestEnv.BoarEntry(env));
        Assert.Equal(@"Creature\Boar\Boar.m2", info.Data.GetProperty("model").GetString(), ignoreCase: true);
        Assert.True(info.Data.GetProperty("stats").GetProperty("vertices").GetInt32() > 100);
        Assert.Equal(0, env.Run("models", "validate", @"Creature\Boar\Boar.m2").Code);
        var junk = Path.Combine(env.Work, "junk.m2");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Equal(ExitCode.ValidationFailed, env.Run("models", "validate", junk).Code);
        var exp = env.Json("models", "export", @"Creature\Boar\Boar.m2", "--out", env.Work, "--no-animations");
        var folder = exp.Data.GetProperty("folder").GetString()!;
        Assert.True(File.Exists(Path.Combine(folder, "wowdata_manifest.json")), exp.Out);
        var exp2 = env.Json("models", "export", @"Creature\Boar\Boar.m2", "--out", env.Work, "--no-animations");
        Assert.NotEqual(folder, exp2.Data.GetProperty("folder").GetString());   // never overwritten
        // the package as exported: no changes found, nothing written
        var imp = env.Json("models", "import", folder);
        Assert.True(imp.Data.GetProperty("nothingChanged").GetBoolean(), imp.Out);
        Assert.Equal(0, env.Json("changes", "list").Data.GetProperty("changes").GetArrayLength());
        Assert.Equal(ExitCode.NotFound, env.Run("models", "compare", @"Creature\Boar\Boar.m2").Code);
        var addon = env.Json("models", "addon", "--zip", Path.Combine(env.Work, "addon.zip"));
        Assert.True(File.Exists(Path.Combine(env.Work, "addon.zip")));
        Assert.False(addon.Data.GetProperty("installed").GetBoolean());
    }

    [Fact]
    public void RenderUsesTheAppsRenderer()
    {
        using var env = new CliTestEnv("render");
        var png = Path.Combine(env.Work, "boar.png");
        var r = env.Json("models", "render", @"Creature\Boar\Boar.m2", png, "--display", "503", "--size", "320x240", "--original");
        Assert.True(r.Code == 0, r.ToString());
        var img = Png.Load(png);
        Assert.Equal((320, 240), (img.Width, img.Height));
        Assert.True(img.Pixels.Distinct().Count() > 4);
    }

    [Fact]
    public void InfoCountsAndRenderLeavesOutYourGamesParticles()
    {
        using var env = new CliTestEnv("render_fx");
        // a spell effect with 1 particle emitter and 3 ribbons, every one drawn
        const string model = @"Spells\Holy_Precast_High_Hand.m2";
        var info = env.Json("models", "info", model);
        var fx = info.Data.GetProperty("effects");
        Assert.Equal((1, 3, 1, 3), (fx.GetProperty("particles").GetInt32(), fx.GetProperty("ribbons").GetInt32(), fx.GetProperty("drawnParticles").GetInt32(), fx.GetProperty("drawnRibbons").GetInt32()));
        Assert.Equal(0, fx.GetProperty("leftOut").GetArrayLength());
        Assert.Contains("effects: 1 particle emitter and 3 ribbons drawn by the viewer's own simulation of the 1.12 client's rules", env.Run("models", "info", model).Out);
        Assert.Contains("--no-particles", env.Run("models", "render", "--help").Out);
        // the Fire Elemental's fire: many bright orange pixels with the particles, far fewer without
        const string fire = @"Creature\FireElemental\FireElemental.m2";
        var with = Path.Combine(env.Work, "with.png");
        var without = Path.Combine(env.Work, "without.png");
        Assert.Equal(0, env.Run("models", "render", fire, with, "--size", "320x240", "--original").Code);
        Assert.Equal(0, env.Run("models", "render", fire, without, "--size", "320x240", "--original", "--no-particles").Code);
        static int Orange(string f)
        {
            var p = Png.Load(f).Pixels;
            return Enumerable.Range(0, p.Length / 4).Count(i => p[i * 4] > 200 && p[i * 4 + 1] > 90 && p[i * 4 + 2] < 120);
        }
        int a = Orange(with), b = Orange(without);
        Assert.True(a > b + 1500, $"orange pixels: {a} with particles, {b} without");
    }
}

[Collection(IndexCollection.Name)]
public class CliMapTests
{
    [Fact]
    public void MapsTerrainCompareTestRevert()
    {
        using var env = new CliTestEnv("maps");
        var list = env.Json("maps", "list");
        Assert.Contains(list.Data.EnumerateArray(), m => m.GetProperty("directory").GetString() == "Azeroth");
        Assert.True(env.Json("maps", "zones", "Azeroth").Data.GetArrayLength() > 20);
        Assert.Equal(ExitCode.NotFound, env.Run("maps", "zones", "NoSuchMap").Code);
        var tiles = env.Json("maps", "tiles", "0");
        Assert.True(tiles.Data.GetProperty("total").GetInt32() > 500);
        Assert.Equal(ExitCode.NotFound, env.Run("maps", "tile", "Azeroth", "0", "0").Code);
        var tile = env.Json("maps", "tile", "Azeroth", "32", "48");
        Assert.Contains(tile.Data.GetProperty("facts").EnumerateArray(), f => f.GetString()!.Contains("doodads", StringComparison.Ordinal));
        var bd = env.Json("maps", "breakdown", "Azeroth", "32", "48", "--filter", "elwynnbush");
        Assert.True(bd.Data.GetProperty("pieces").GetInt32() > 100);
        foreach (var kind in new[] { "relief", "heights" })
        {
            var f = Path.Combine(env.Work, kind + ".png");
            Assert.Equal(0, env.Run("maps", "render", "Azeroth", "32", "48", "--kind", kind, "--out", f, "--size", "256").Code);
            Assert.Equal(256, Png.Load(f).Width);
        }
        var small = list.Data.EnumerateArray().Where(m => m.GetProperty("singleBuilding").ValueKind == JsonValueKind.Null && m.GetProperty("tiles").GetInt32() is > 0 and < 40)
            .OrderBy(m => m.GetProperty("tiles").GetInt32()).First().GetProperty("directory").GetString()!;
        var mini = env.Json("maps", "minimap", small, "--out", Path.Combine(env.Work, "mini.png"));
        Assert.True(mini.Code == 0, mini.ToString());
        Assert.True(Png.Load(Path.Combine(env.Work, "mini.png")).Width >= 128);

        // terrain through "Blender" (a scripted edit of the package, as the add-on writes it), the seam check, compare, test, revert
        var ex = env.Json("maps", "export-terrain", "Azeroth", "32", "48", "--out", Path.Combine(env.Work, "terrain"));
        var pkg = ex.Data.GetProperty("folder").GetString()!;
        TerrainEdit.WriteScripted(pkg, (t, i, p) => p with { Z = p.Z + 3f });   // the whole tile 3 yards up: its edges disagree with the neighbours
        var refused = env.Json("maps", "import-terrain", pkg);
        Assert.Equal(ExitCode.ValidationFailed, refused.Code);
        Assert.True(refused.Data.GetProperty("seams").GetArrayLength() > 0);
        var dry = env.Json("maps", "import-terrain", pkg, "--sync-edges", "--dry-run");
        Assert.Equal(0, dry.Code);
        Assert.Equal(JsonValueKind.Null, dry.Data.GetProperty("saved").ValueKind);
        var imp = env.Json("maps", "import-terrain", pkg, "--sync-edges");
        Assert.Equal(0, imp.Code);
        Assert.True(imp.Data.GetProperty("saved").GetProperty("files").GetArrayLength() >= 2, imp.Out);   // the tile and its synced neighbours
        var cmp = env.Json("maps", "compare", "Azeroth", "32", "48", "--out", Path.Combine(env.Work, "diff.png"));
        Assert.True(cmp.Data.GetProperty("changes").GetArrayLength() > 0);
        Assert.True(File.Exists(Path.Combine(env.Work, "diff.png")));
        var changed = env.Json("maps", "tiles", "Azeroth", "--changed");
        Assert.Contains(changed.Data.GetProperty("tiles").EnumerateArray(), t => t.GetProperty("x").GetInt32() == 32 && t.GetProperty("y").GetInt32() == 48);
        Assert.Equal(ExitCode.NeedsYes, env.Run("maps", "test-in-game", "Azeroth").Code);
        Assert.Equal(0, env.Run("maps", "test-in-game", "Azeroth", "--dry-run").Code);
        Assert.False(File.Exists(Path.Combine(env.DataDir, "World", "Maps", "Azeroth", "Azeroth_32_48.adt")));
        Assert.Equal(0, env.Run("maps", "test-in-game", "Azeroth", "--yes").Code);
        Assert.True(File.Exists(Path.Combine(env.DataDir, "World", "Maps", "Azeroth", "Azeroth_32_48.adt")));
        Assert.Equal(0, env.Run("maps", "revert", "Azeroth", "--yes").Code);
        Assert.False(File.Exists(Path.Combine(env.DataDir, "World", "Maps", "Azeroth", "Azeroth_32_48.adt")));
        Assert.Empty(ChangeOperations.MapFiles(new Workspace(env.Paths), new MapInfo { Directory = "Azeroth" }));

        // Noggit: the project in the isolated home (Noggit is not started)
        var n = env.Json("maps", "noggit", "Azeroth", "32", "48");
        Assert.True(n.Code == 0, n.ToString());
        Assert.True(File.Exists(n.Data.GetProperty("project").GetString()));
        Assert.StartsWith(env.Home, n.Data.GetProperty("project").GetString()!, StringComparison.OrdinalIgnoreCase);
        var ng = env.Json("maps", "import-noggit", "Azeroth");
        Assert.Equal(0, ng.Code);
    }

    [Fact]
    public void ServerMapsRefusesAndNeedsYes()
    {
        using var env = new CliTestEnv("servermaps");
        var st = env.Json("server-maps", "status");
        Assert.Equal(env.ServerData, st.Data.GetProperty("serverData").GetString());
        bool running = ServerMapsService.RunningWorldServers().Count > 0;
        var install = env.Json("server-maps", "install");
        Assert.Equal(running ? ExitCode.Refused : ExitCode.NeedsYes, install.Code);
        var undo = env.Json("server-maps", "undo");
        Assert.Equal(0, undo.Code);
        Assert.False(undo.Data.GetProperty("undone").GetBoolean());
        Assert.Equal(ExitCode.Usage, env.Run("server-maps", "extract", "--steps", "everything").Code);
    }
}

[Collection(IndexCollection.Name)]
public class CliDbcTests
{
    [Fact]
    public void TablesRowsEditImportExportServer()
    {
        using var env = new CliTestEnv("dbc");
        var tables = env.Json("dbc", "tables");
        Assert.True(tables.Data.GetArrayLength() >= 154);
        Assert.Equal(ExitCode.NotFound, env.Run("dbc", "info", "NoSuchTable").Code);
        var info = env.Json("dbc", "info", "Spell");
        Assert.Contains(info.Data.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "SpellIconID");
        var rows = env.Json("dbc", "rows", "Spell", "--where", "Name==Fireball", "--columns", "ID,Name,SpellIconID", "--limit", "0");
        Assert.Contains(rows.Data.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetInt64() == 133);
        Assert.Equal(3, rows.Data.GetProperty("columns").GetArrayLength());
        var get = env.Json("dbc", "get", "Spell", "133", "SpellIconID");
        Assert.Equal("185", get.Data.GetProperty("raw").GetString());
        Assert.Equal(ExitCode.NotFound, env.Run("dbc", "get", "Spell", "999999999").Code);

        // a refused value is not stored; a good one is saved into the workspace
        Assert.Equal(ExitCode.ValidationFailed, env.Run("dbc", "set", "Spell", "133", "SpellIconID", "999999").Code);
        var dry = env.Json("dbc", "set", "Spell", "133", "SpellIconID", "184", "--dry-run");
        Assert.False(dry.Data.GetProperty("saved").GetBoolean());
        Assert.False(new Workspace(env.Paths).IsModified(@"DBFilesClient\Spell.dbc"));
        var set = env.Json("dbc", "set", "Spell", "133", "SpellIconID", "184");
        Assert.True(set.Data.GetProperty("saved").GetBoolean(), set.Out);
        Assert.Equal("184", env.Json("dbc", "get", "Spell", "133", "SpellIconID").Data.GetProperty("raw").GetString());
        var diff = env.Json("dbc", "diff", "Spell");
        Assert.Equal(1, diff.Data.GetProperty("diff").GetProperty("changed").GetInt32());
        // setting it back to the game's value removes the edit (as the app does)
        var back = env.Json("dbc", "set", "Spell", "133", "SpellIconID", "185");
        Assert.True(back.Data.GetProperty("removedEdit").GetBoolean());

        // add, duplicate, delete (needs --yes), bulk
        var add = env.Json("dbc", "add", "SpellIcon", "--set", @"TextureFilename=Interface\Icons\INV_Misc_QuestionMark");
        Assert.True(add.Code == 0, add.ToString());
        long newId = add.Data.GetProperty("change").GetProperty("id").GetInt64();
        Assert.Equal(ExitCode.ValidationFailed, env.Run("dbc", "add", "SpellIcon", "--id", "185").Code);   // an ID twice
        var dup = env.Json("dbc", "duplicate", "TaxiNodes", "2", "--set", "Name=Stormwind, Old Town");
        Assert.True(dup.Code == 0, dup.ToString());
        Assert.Equal(ExitCode.NeedsYes, env.Run("dbc", "delete", "SpellIcon", newId.ToString()).Code);
        Assert.Equal(0, env.Run("dbc", "delete", "SpellIcon", newId.ToString(), "--yes").Code);
        Assert.Equal(ExitCode.NotFound, env.Run("dbc", "get", "SpellIcon", newId.ToString()).Code);
        var bulk = env.Json("dbc", "bulk", "Spell", "--where", "Name==Fireball", "--where", "NameSubtext=Rank 1", "--column", "SpellIconID", "--op", "set", "--value", "184");
        Assert.True(bulk.Data.GetProperty("change").GetProperty("changed").GetInt32() >= 1, bulk.Out);
        Assert.Equal(ExitCode.ValidationFailed, env.Run("dbc", "bulk", "Spell", "--ids", "133", "--column", "SpellIconID", "--op", "set", "--value", "-5").Code);
        Assert.Equal(ExitCode.Usage, env.Run("dbc", "bulk", "Spell", "--column", "SpellIconID", "--op", "set", "--value", "1").Code);   // no rows chosen

        // CSV round trip: export, change a value, preview, apply
        var csv = Path.Combine(env.Work, "icons.csv");
        Assert.Equal(0, env.Run("dbc", "export", "SpellIcon", "--ids", "184,185", "--out", csv).Code);
        var text = File.ReadAllText(csv);
        var changedCsv = text.Replace("Spell_Fire_FlameBolt", "Spell_Fire_FlameBolt2");
        Assert.NotEqual(text, changedCsv);
        File.WriteAllText(csv, changedCsv);
        var preview = env.Json("dbc", "import", "SpellIcon", csv);
        Assert.False(preview.Data.GetProperty("applied").GetBoolean());
        Assert.Equal(1, preview.Data.GetProperty("preview").GetProperty("changed").GetInt32());
        var apply = env.Json("dbc", "import", "SpellIcon", csv, "--apply");
        Assert.True(apply.Data.GetProperty("saved").GetBoolean(), apply.Out);
        File.WriteAllText(csv, changedCsv.Replace("184,", "abc,"));
        Assert.Equal(ExitCode.ValidationFailed, env.Run("dbc", "import", "SpellIcon", csv).Code);
        var json = Path.Combine(env.Work, "taxi.json");
        Assert.Equal(0, env.Run("dbc", "export", "TaxiNodes", "--out", json).Code);
        Assert.Contains(JsonDocument.Parse(File.ReadAllText(json)).RootElement.ValueKind, new[] { JsonValueKind.Array, JsonValueKind.Object });
        var reimport = env.Json("dbc", "import", "TaxiNodes", json);   // an exported file imported unchanged changes nothing
        Assert.Equal(0, reimport.Data.GetProperty("preview").GetProperty("changed").GetInt32());
        var usedBy = env.Json("dbc", "used-by", "SpellIcon", "185");
        Assert.True(usedBy.Data.GetProperty("usedBy").GetArrayLength() > 0);

        // copy to the (fake) server: TaxiNodes is a database table (never copied), TaxiPath a server file
        var firstPath = env.Json("dbc", "rows", "TaxiPath", "--limit", "1").Data.GetProperty("rows")[0].GetProperty("id").GetInt64();
        Assert.Equal(0, env.Run("dbc", "duplicate", "TaxiPath", firstPath.ToString()).Code);
        var plan = env.Json("dbc", "copy-to-server", "--dry-run");
        Assert.Contains(plan.Data.GetProperty("items").EnumerateArray(), i => i.GetProperty("table").GetString() == "TaxiPath" && i.GetProperty("canCopy").GetBoolean());
        Assert.Contains(plan.Data.GetProperty("items").EnumerateArray(), i => i.GetProperty("table").GetString() == "TaxiNodes" && !i.GetProperty("canCopy").GetBoolean());
        bool running = ServerMapsService.RunningWorldServers().Count > 0;
        var copy = env.Json("dbc", "copy-to-server");
        Assert.Equal(running ? ExitCode.Refused : ExitCode.NeedsYes, copy.Code);
        if (!running)
        {
            Assert.Equal(0, env.Run("dbc", "copy-to-server", "--yes").Code);
            Assert.True(File.Exists(Path.Combine(env.ServerData, "5875", "dbc", "TaxiPath.dbc")));
            Assert.False(File.Exists(Path.Combine(env.ServerData, "5875", "dbc", "TaxiNodes.dbc")));
            Assert.Equal(ExitCode.NeedsYes, env.Run("dbc", "undo-server-copy").Code);
            Assert.Equal(0, env.Run("dbc", "undo-server-copy", "--yes").Code);
            Assert.False(File.Exists(Path.Combine(env.ServerData, "5875", "dbc", "TaxiPath.dbc")));   // it was added: removed again
        }
    }
}

[Collection(IndexCollection.Name)]
public class CliScriptTests
{
    private static (int Code, string Out) Run(string exe, IEnumerable<string> args, Dictionary<string, string>? env = null, string? cwd = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new()) psi.Environment[k] = v;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(600_000)) { p.Kill(true); throw new TimeoutException(exe); }
        return (p.ExitCode, o.Result + e.Result);
    }

    /// <summary>The example scripts of scripts\ run against a fake game folder (wds.exe of this build, an isolated home).</summary>
    [Fact]
    public void ExampleScriptsWork()
    {
        using var env = new CliTestEnv("scripts");
        var scripts = Path.Combine(Oracle.ToolDir, "scripts");
        // a table through a "spreadsheet": export, change, preview, apply
        var vars = new Dictionary<string, string> { ["WDS"] = CliTestEnv.Exe, ["WDS_HOME"] = env.Home };
        var cmd = Path.Combine(scripts, "dbc_csv_round_trip.cmd");
        var csv = Path.Combine(env.Work, "SpellIcon.csv");
        var r1 = Run("cmd.exe", new[] { "/c", cmd, "SpellIcon", csv }, vars, env.Work);
        Assert.True(r1.Code == 0 && File.Exists(csv), r1.Out);
        File.WriteAllText(csv, File.ReadAllText(csv).Replace("Spell_Fire_FlameBolt", "Spell_Fire_FlameBolt2"));
        var r2 = Run("cmd.exe", new[] { "/c", cmd, "SpellIcon", csv }, vars, env.Work);
        Assert.True(r2.Code == 0, r2.Out);
        Assert.Contains("1 row changed", r2.Out);
        Assert.False(new Workspace(env.Paths).IsModified(@"DBFilesClient\SpellIcon.dbc"));
        var r3 = Run("cmd.exe", new[] { "/c", cmd, "SpellIcon", csv, "apply" }, vars, env.Work);
        Assert.True(r3.Code == 0, r3.Out);
        Assert.True(new Workspace(env.Paths).IsModified(@"DBFilesClient\SpellIcon.dbc"));
        // build and install the release patch
        var ps = Run("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scripts, "build_and_install_patch.ps1"), "-Wds", CliTestEnv.Exe, "-WdsHome", env.Home });
        Assert.True(ps.Code == 0, ps.Out);
        Assert.True(File.Exists(Path.Combine(env.DataDir, "patch-3.MPQ")));
        Assert.Contains("A release patch built by WoW Data Studio is installed", ps.Out);
        Assert.Equal(0, env.Run("changes", "uninstall-patch", "--yes").Code);
        // the zone export: with no spawns in this index's zone filter it stops with "not found" (6), not a crash
        var zone = Run("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scripts, "export_zone_creature_textures.ps1"), "-Zone", "No Such Zone", "-Out", Path.Combine(env.Work, "z"), "-Wds", CliTestEnv.Exe, "-WdsHome", env.Home });
        Assert.True(zone.Code == ExitCode.NotFound, zone.Out);
    }
}

[Collection(IndexCollection.Name)]
public class CliChangesTests
{
    private static void Tint(string png)
    {
        var img = Png.Load(png);
        for (int i = 0; i < img.Pixels.Length; i += 4) if (img.Pixels[i + 3] > 0) img.Pixels[i] = 255;
        Png.Save(img, png, true);
    }

    /// <summary>The app and wds open the same workspace at the same time: each sees the other's changes and never writes
    /// an old copy of the change list over them.</summary>
    [Fact]
    public void TheAppAndTheCommandLineShareTheWorkspace()
    {
        using var env = new CliTestEnv("shared");
        var app = new Workspace(env.Paths);   // as the app holds it while it runs
        Assert.Empty(app.ModifiedPaths());
        var png = Path.Combine(env.Work, "boar.png");
        Assert.Equal(0, env.Run("textures", "export", @"Creature\Boar\BoarSkinIvory.blp", "--out", png).Code);
        Tint(png);
        Assert.Equal(0, env.Run("textures", "import", png).Code);
        Assert.Contains(@"Creature\Boar\BoarSkinIvory.blp", app.ModifiedPaths(), StringComparer.OrdinalIgnoreCase);   // the app sees wds's edit
        const string brown = @"Creature\Boar\BoarSkinBrown.blp";
        app.SaveEdit(brown, TestEnv.Client.Read(brown)!, new OriginInfo(), app.BeginChangeSet("an edit in the app"));
        var list = env.Json("changes", "list").Data.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()!).ToList();
        Assert.Equal(2, list.Count);   // both edits are kept
        Assert.Equal(0, env.Run("changes", "revert", brown, "--yes").Code);
        Assert.Single(app.ModifiedPaths());
    }

    [Fact]
    public void InstallRemoveBuildUninstallAndForeignPatch()
    {
        using var env = new CliTestEnv("changes");
        Assert.Equal(ExitCode.NotFound, env.Run("changes", "build-patch").Code);   // nothing to build
        var png = Path.Combine(env.Work, "boar.png");
        Assert.Equal(0, env.Run("textures", "export", @"Creature\Boar\BoarSkinIvory.blp", "--out", png).Code);
        Tint(png);
        Assert.Equal(0, env.Run("textures", "import", png).Code);
        var list = env.Json("changes", "list");
        Assert.Single(list.Data.GetProperty("changes").EnumerateArray());
        Assert.Single(env.Json("changes", "sets").Data.EnumerateArray());
        Assert.Equal(0, env.Run("changes", "diff", @"Creature\Boar\BoarSkinIvory.blp").Code);
        var loose = Path.Combine(env.DataDir, "Creature", "Boar", "BoarSkinIvory.blp");

        // loose files: needs --yes; a file the app did not put there is refused without --force
        var plan = env.Json("changes", "install");
        Assert.Equal(ExitCode.NeedsYes, plan.Code);
        Assert.False(File.Exists(loose));
        Assert.Equal(0, env.Run("changes", "install", "--dry-run").Code);
        Assert.Equal(0, env.Run("changes", "install", "--yes").Code);
        Assert.True(File.Exists(loose));
        Assert.Contains("installed as loose file", env.Json("changes", "list").Data.GetProperty("changes")[0].GetProperty("status").GetString());
        Assert.Equal(ExitCode.NeedsYes, env.Run("changes", "remove").Code);
        Assert.Equal(0, env.Run("changes", "remove", "--yes").Code);
        Assert.False(File.Exists(loose));
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllText(loose, "someone else's file");
        Assert.Equal(ExitCode.Refused, env.Run("changes", "install", "--yes").Code);
        Assert.Equal("someone else's file", File.ReadAllText(loose));
        Assert.Equal(0, env.Run("changes", "install", "--force", "--yes").Code);
        Assert.Equal(0, env.Run("changes", "remove", "--yes").Code);
        Assert.Equal("someone else's file", File.ReadAllText(loose));   // put back from its backup
        File.Delete(loose);

        // the release patch: built without installing; installing needs --yes; uninstall it
        var patch = Path.Combine(env.DataDir, "patch-3.MPQ");
        var built = env.Json("changes", "build-patch");
        Assert.True(built.Data.GetProperty("built").GetBoolean());
        Assert.Null(built.Data.GetProperty("installedTo").GetString());
        Assert.False(File.Exists(patch));
        Assert.Equal(ExitCode.NeedsYes, env.Run("changes", "build-patch", "--install").Code);
        var inst = env.Json("changes", "build-patch", "--install", "--yes");
        Assert.Equal(patch, inst.Data.GetProperty("installedTo").GetString(), ignoreCase: true);
        Assert.Equal("ours", env.Json("changes", "patch-state").Data.GetProperty("owner").GetString());
        Assert.Equal(ExitCode.NeedsYes, env.Run("changes", "uninstall-patch").Code);
        Assert.Equal(0, env.Run("changes", "uninstall-patch", "--yes").Code);
        Assert.False(File.Exists(patch));

        // a patch-3.MPQ the app did not create: refused without --force, then moved to the backups (never deleted)
        WoWData.Core.Mpq.MpqWriter.Build(new Dictionary<string, byte[]> { [@"Interface\Other\mod.txt"] = new byte[] { 1, 2, 3 } }, patch, overwrite: true);
        var foreignBytes = File.ReadAllBytes(patch);
        Assert.Equal("foreign", env.Json("changes", "patch-state").Data.GetProperty("owner").GetString());
        Assert.Equal(ExitCode.Refused, env.Run("changes", "uninstall-patch", "--yes").Code);
        Assert.True(File.Exists(patch));
        Assert.Equal(0, env.Run("changes", "uninstall-patch", "--force", "--yes").Code);
        Assert.False(File.Exists(patch));
        Assert.Contains(Directory.GetFiles(env.Paths.BackupDir, "patch-3.MPQ.removed_*.bak"), f => File.ReadAllBytes(f).SequenceEqual(foreignBytes));
        // installing over a foreign patch backs it up, and uninstalling puts it back
        File.WriteAllBytes(patch, foreignBytes);
        var over = env.Run("changes", "build-patch", "--install", "--yes");
        Assert.True(over.Code == 0, over.ToString());
        Assert.Equal(0, env.Run("changes", "uninstall-patch", "--yes").Code);
        Assert.Equal(foreignBytes, File.ReadAllBytes(patch));
        File.Delete(patch);
        Assert.Equal(0, env.Run("changes", "folder").Code);
    }
}

/// <summary>0.10.0: the skeleton and animation commands (models anims, skeleton, export --with-skeleton, import,
/// render --sequence by name).</summary>
[Collection(IndexCollection.Name)]
public class CliRigTests
{
    [Fact]
    public void AnimsSkeletonExportWithSkeletonImportAndRenderByName()
    {
        using var env = new CliTestEnv("rig");
        var anims = env.Json("models", "anims", @"Creature\Boar\Boar.m2");
        var list = anims.Data.GetProperty("animations").EnumerateArray().ToList();
        Assert.Equal(13, list.Count);
        var names = list.Select(x => x.GetProperty("name").GetString()!).ToList();
        Assert.Contains("Stand [3]", names);
        var walk = list.Single(x => x.GetProperty("name").GetString() == "Walk");
        Assert.True(walk.GetProperty("loops").GetBoolean());
        Assert.Contains(walk.GetProperty("usedFor").EnumerateArray(), u => u.GetString()!.StartsWith("moving", StringComparison.Ordinal));
        // the app's viewer and the command line name the animations the same way
        var m = WoWData.Core.M2.M2Model.Load(TestEnv.Client.Read(@"Creature\Boar\Boar.m2")!);
        var res = new WoWData.Core.M2.ModelResolver(new WoWData.Core.Index.Catalog(TestEnv.Client));
        Assert.Equal(WoWData.Core.ModelIO.ModelExporter.ActionNames(m, res.AnimationName), names);
        var one = env.Json("models", "anims", @"Creature\Boar\Boar.m2", "--sequence", "Walk");
        Assert.True(one.Data.GetProperty("animations")[0].GetProperty("events").GetArrayLength() > 0);
        Assert.Equal(ExitCode.NotFound, env.Run("models", "anims", @"Creature\Boar\Boar.m2", "--sequence", "Moonwalk").Code);
        var skel = env.Json("models", "skeleton", @"Creature\Boar\Boar.m2");
        Assert.Equal(57, skel.Data.GetProperty("bones").GetArrayLength());
        Assert.Equal(9, skel.Data.GetProperty("attachments").GetArrayLength());
        Assert.Contains(skel.Data.GetProperty("bones").EnumerateArray(), b => b.GetProperty("keyBoneName").GetString() == "Head");
        // export with the skeleton, import it back: nothing changed, nothing written
        var exp = env.Json("models", "export", @"Creature\Boar\Boar.m2", "--out", env.Work, "--with-skeleton");
        Assert.Equal(57, exp.Data.GetProperty("skeleton").GetProperty("bones").GetInt32());
        var folder = exp.Data.GetProperty("folder").GetString()!;
        var imp = env.Json("models", "import", folder);
        Assert.True(imp.Data.GetProperty("nothingChanged").GetBoolean(), imp.Out);
        Assert.Contains(imp.Data.GetProperty("rows").EnumerateArray(), r => r.GetProperty("kind").GetString() == "skeleton" && r.GetProperty("unchanged").GetBoolean());
        Assert.Equal(ExitCode.Usage, env.Run("models", "export", @"Creature\Boar\Boar.m2", "--out", env.Work, "--with-skeleton", "--no-animations").Code);
        // render an animation by its name
        var png = Path.Combine(env.Work, "walk.png");
        var r2 = env.Json("models", "render", @"Creature\Boar\Boar.m2", png, "--sequence", "Walk", "--time", "300", "--size", "160x120", "--original");
        Assert.True(r2.Code == 0, r2.ToString());
        Assert.Contains("animation Walk at 300 ms", r2.Out, StringComparison.Ordinal);
        Assert.NotEqual(0, env.Run("models", "render", @"Creature\Boar\Boar.m2", png, "--sequence", "Moonwalk", "--original").Code);
    }
}
