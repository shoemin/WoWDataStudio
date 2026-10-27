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
        }
        // the client's folder is never written: only the index in the app's folder
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(_flavor)!, "*", SearchOption.AllDirectories), f => f.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
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
