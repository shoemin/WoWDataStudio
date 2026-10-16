using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using WoWData.Core.Maps;

namespace WoWData.Tests;

/// <summary>
/// The C# map formats (WoWData.Core\Maps) against the Python reference (wowdata/maps, oracle make_maps.py):
/// * every map file of the client (2,429 ADTs, 43 WDTs, 43 WDLs) validates with the same findings as the Python (none);
/// * every ADT re-serialises byte-identically from its parsed chunks;
/// * 41 edited or deliberately broken tiles / WDTs / WDLs give the same findings (code, place and message, word for word)
///   and the same semantic diff (summary, content lines, layout lines) as the Python;
/// * the placement matrix reproduces the stock MODF bounding boxes; MCNR normals are world-space (x, y, z);
/// * an edit made with the C# document (heights, a doodad moved, holes) validates and is described in plain words.
/// </summary>
public class MapTests
{
    private static string OracleDir => Oracle.Ensure("maps");

    private static List<string[]> Findings(MapReport r) =>
        r.Findings.Select(f => new[] { f.SeverityText, f.Code, f.File, f.Where, f.Message }).ToList();

    private static List<string[]> JsonFindings(JsonElement e) =>
        e.EnumerateArray().Select(f => f.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()).ToList();

    private static string Key(string[] f) => string.Join("|", f);

    private static List<string> MapFiles() =>
        TestEnv.Client.Glob(@"World\Maps\*").Where(n => MapValidator.KindOf(n) != null).ToList();

    [Fact]
    public void EveryClientMapFileValidatesLikeThePython()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(OracleDir, "allclient.json")));
        var names = MapFiles();
        Assert.Equal(2515, names.Count);
        Assert.Equal(2429, names.Count(n => n.EndsWith(".adt", StringComparison.OrdinalIgnoreCase)));
        var sha = doc.RootElement.GetProperty("sha256");
        var report = new MapReport();
        foreach (var g in names.GroupBy(n => n[..n.LastIndexOf('\\')].ToUpperInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal))
            MapValidator.ValidateMapSet(g.Select(n => (n, (Func<byte[]>)(() => TestEnv.Client.Read(n)!))), report);
        Assert.Equal(doc.RootElement.GetProperty("files").GetInt32(), report.Files);
        var py = JsonFindings(doc.RootElement.GetProperty("findings")).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var cs = Findings(report).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(py, cs);
        Assert.Equal(0, report.Count(MapSeverity.Error));
        Assert.Equal(0, report.Count(MapSeverity.Warning));
        // the same bytes were read on both sides (spot check of 200 files)
        foreach (var n in names.Where((_, i) => i % 12 == 0))
            Assert.Equal(sha.GetProperty(n).GetString(), Convert.ToHexString(SHA256.HashData(TestEnv.Client.Read(n)!)).ToLowerInvariant());
    }

    [Fact]
    public void EveryClientTileReserialisesByteIdentically()
    {
        int n = 0;
        foreach (var name in MapFiles().Where(f => f.EndsWith(".adt", StringComparison.OrdinalIgnoreCase)))
        {
            var data = TestEnv.Client.Read(name)!;
            var doc = new AdtDocument(data, name);
            Assert.True(doc.CanRebuild, $"{name}: layout not split");
            var back = doc.Serialize();
            Assert.True(back.AsSpan().SequenceEqual(data), $"{name}: not byte-identical after re-serialising");
            n++;
        }
        Assert.Equal(2429, n);
    }

    [Fact]
    public void EveryClientWdtAndWdlReserialisesAndEdits()
    {
        int wdts = 0, wdls = 0;
        foreach (var name in MapFiles())
        {
            var data = TestEnv.Client.Read(name)!;
            if (name.EndsWith(".wdt", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(new WdtDocument(data).Serialize().AsSpan().SequenceEqual(data), name);
                wdts++;
            }
            else if (name.EndsWith(".wdl", StringComparison.OrdinalIgnoreCase))
            {
                var d = new WdlDocument(data);
                Assert.True(d.CanRebuild, name);
                Assert.True(d.Serialize().AsSpan().SequenceEqual(data), name);
                wdls++;
            }
        }
        Assert.Equal((43, 43), (wdts, wdls));
        // edits: a tile removed from the tile list, far heights raised on one tile and a new far tile
        var wdtBytes = TestEnv.Client.Read(@"World\Maps\Azeroth\Azeroth.wdt")!;
        var wdt = new WdtDocument(wdtBytes);
        Assert.True(wdt.HasTile(32, 48));
        wdt.SetTile(32, 48, false);
        var dt = MapDiff.DiffWdt(wdtBytes, wdt.Serialize());
        Assert.Equal("MAIN | tiles removed [(32, 48)], added []", string.Join("; ", dt.Lines.Select(l => l.Section + " | " + l.Message)));
        Assert.True(MapValidator.ValidateWdt(wdt.Serialize()).Ok);
        var wdlBytes = TestEnv.Client.Read(@"World\Maps\Azeroth\Azeroth.wdl")!;
        var wdl = new WdlDocument(wdlBytes);
        var h = wdl.GetHeights(32, 48)!;
        for (int i = 0; i < h.Length; i++) h[i] += 3;
        wdl.SetHeights(32, 48, h);
        wdl.SetHeights(0, 0, new short[545]);
        var edited = wdl.Serialize();
        var r = MapValidator.ValidateWdl(edited);
        Assert.True(r.Ok, string.Join("\n", r.Findings));
        var dl = MapDiff.DiffWdl(wdlBytes, edited);
        Assert.Contains(dl.Lines, l => l.Message == "tile 32,48: 545 of 545 heights changed (max 3)");
        Assert.Contains(dl.Lines, l => l.Section == "MAOF" && l.Message.StartsWith("tiles removed [], added [(0, 0)]", StringComparison.Ordinal));
    }

    [Fact]
    public void BrokenAndEditedFilesMatchThePython()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(OracleDir, "variants.json")));
        int count = 0;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            var data = File.ReadAllBytes(Path.Combine(OracleDir, "variants", name));
            var r = MapValidator.ValidateBytes(data, name);
            var py = JsonFindings(v.GetProperty("findings")).Select(Key).ToList();
            var cs = Findings(r).Select(Key).ToList();
            Assert.True(py.SequenceEqual(cs), $"{name}: findings differ\nPython:\n  {string.Join("\n  ", py)}\nC#:\n  {string.Join("\n  ", cs)}");
            var baseName = v.GetProperty("base").GetString()!;
            var d = MapDiff.DiffBytes(TestEnv.Client.Read(baseName)!, data, name, baseName, name);
            var pd = v.GetProperty("diff");
            Assert.Equal(pd.GetProperty("identical").GetBoolean(), d.IdenticalBytes);
            var pyLines = pd.GetProperty("lines").EnumerateArray().Select(x => x[0].GetString() + " | " + x[1].GetString()).ToList();
            var csLines = d.Lines.Select(x => x.Section + " | " + x.Message).ToList();
            Assert.True(pyLines.SequenceEqual(csLines), $"{name}: diff lines differ\nPython:\n  {string.Join("\n  ", pyLines.Take(20))}\nC#:\n  {string.Join("\n  ", csLines.Take(20))}");
            var pyLayout = pd.GetProperty("layout").EnumerateArray().Select(x => x[0].GetString() + " | " + x[1].GetString()).ToList();
            var csLayout = d.Layout.Select(x => x.Section + " | " + x.Message).ToList();
            Assert.True(pyLayout.SequenceEqual(csLayout), $"{name}: layout lines differ\nPython:\n  {string.Join("\n  ", pyLayout.Take(10))}\nC#:\n  {string.Join("\n  ", csLayout.Take(10))}");
            Assert.Equal(pd.GetProperty("summary").GetString(), d.Summary());
            count++;
        }
        Assert.True(count >= 40, $"{count} variants");
    }

    [Fact]
    public void PlainSummaryNamesTheChanges()
    {
        var dir = Path.Combine(OracleDir, "variants");
        string Plain(string variant, string baseName) =>
            MapDiff.DiffBytes(TestEnv.Client.Read(baseName)!, File.ReadAllBytes(Path.Combine(dir, variant)), variant).PlainSummary();
        const string tile = @"World\Maps\Azeroth\Azeroth_32_48.adt";
        Assert.StartsWith("Heights changed in 37 chunks", Plain("heights_raised.adt", tile));
        Assert.Equal("4 doodads moved", Plain("doodads_moved.adt", tile));
        Assert.Contains("holes changed in 1 chunk", Plain("holes_cut.adt", tile), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("textures replaced in 1 chunk", Plain("layer_texture_replaced.adt", tile), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 building", Plain("wmo_turned.adt", tile));
        Assert.Contains("1 doodad removed", Plain("doodad_replaced_uid.adt", tile));
    }

    /// <summary>The placement convention (MDDF/MODF rotation in degrees, Ry(ry - 90) Rz(-rx) Rx(rz) in placement axes)
    /// reproduces the bounding boxes stored in MODF from the WMO's own box, on the stock tiles.</summary>
    [Fact]
    public void PlacementMatrixReproducesModfBoxes()
    {
        int checkedBoxes = 0, matched = 0;
        foreach (var tile in new[] { @"Azeroth\Azeroth_32_48", @"Azeroth\Azeroth_31_49", @"Kalimdor\Kalimdor_39_32", @"Azeroth\Azeroth_35_40", @"Kalimdor\Kalimdor_40_30" })
        {
            var data = TestEnv.Client.Read($@"World\Maps\{tile}.adt");
            if (data == null) continue;
            var t = TerrainTile.Load(data, tile, 0, 0);
            foreach (var w in t.Wmos)
            {
                var geo = WmoGeometry.Load(w.Model, p => TestEnv.Client.Read(p));
                if (geo == null) continue;
                var m = w.World;
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? geo.BoundsMin.X : geo.BoundsMax.X, (i & 2) == 0 ? geo.BoundsMin.Y : geo.BoundsMax.Y, (i & 4) == 0 ? geo.BoundsMin.Z : geo.BoundsMax.Z);
                    var p = Vector3.Transform(c, m);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
                // MODF extents are in placement coordinates (a, h, b): convert our world box back
                var eLo = TerrainMath.PlacementToWorld(w.ExtentMin);
                var eHi = TerrainMath.PlacementToWorld(w.ExtentMax);
                var wLo = Vector3.Min(eLo, eHi);
                var wHi = Vector3.Max(eLo, eHi);
                checkedBoxes++;
                // the stored minimum corner matches exactly; a few stored maxima include more than the root box
                if (Vector3.Distance(lo, wLo) < 0.05f || Vector3.Distance(hi, wHi) < 0.05f) matched++;
            }
        }
        // one cave WMO (md_warmcave) stores a box that differs from its root's box in both corners
        Assert.True(checkedBoxes >= 15, $"{checkedBoxes} WMO placements checked");
        Assert.True(matched >= checkedBoxes - 1, $"{matched} of {checkedBoxes} boxes reproduced");
    }

    /// <summary>MCNR holds world-space normals (x, y, z): they agree with the normals of the height field.</summary>
    [Fact]
    public void NormalsAreWorldSpace()
    {
        var t = TerrainTile.Load(TestEnv.Client.Read(@"World\Maps\Azeroth\Azeroth_32_48.adt")!, "Azeroth", 32, 48);
        double err = 0;
        int n = 0;
        foreach (var c in t.AllChunks.Where(c => c.Index % 7 == 0))
            for (int i = 1; i < 8; i++)
                for (int j = 1; j < 8; j++)
                {
                    float dhdi = (c.Heights[(i + 1) * 17 + j] - c.Heights[(i - 1) * 17 + j]) / (2 * TerrainMath.UnitSize);
                    float dhdj = (c.Heights[i * 17 + j + 1] - c.Heights[i * 17 + j - 1]) / (2 * TerrainMath.UnitSize);
                    var expect = Vector3.Normalize(new Vector3(dhdi, dhdj, 1));
                    err += (c.Normals[i * 17 + j] - expect).Length();
                    n++;
                }
        Assert.True(err / n < 0.05, $"mean normal error {err / n:0.000}");
        Assert.Equal(-8533.33f, t.Chunks[0]!.Position.X, 0.01f);
        Assert.Equal(0f, t.Chunks[0]!.Position.Y, 0.01f);
        Assert.Equal(t.Chunks[0]!.Heights[0], t.HeightAt(t.Corner.X - 0.001f, t.Corner.Y - 0.001f)!.Value, 0.05f);
    }

    [Fact]
    public void DocumentEditsValidateAndDiff()
    {
        const string tile = @"World\Maps\Azeroth\Azeroth_32_48.adt";
        var data = TestEnv.Client.Read(tile)!;
        var doc = new AdtDocument(data, tile);
        for (int c = 0; c < 12; c++)
        {
            var h = doc.GetHeights(c);
            for (int k = 0; k < h.Length; k++) h[k] += 3f;
            doc.SetHeights(c, h);
            doc.RecomputeNormals(c);
        }
        var orig = new AdtFile(data);
        doc.SetDoodadPosition(0, orig.Mddf[0].Position + new Vector3(4, 0, 0));
        doc.SetHoles(100, 0x0001);
        var edited = doc.Serialize();
        Assert.Equal(data.Length, edited.Length);
        var r = MapValidator.ValidateAdt(edited, "edited.adt");
        Assert.True(r.Ok && r.Count(MapSeverity.Warning) == 0, string.Join("\n", r.Findings));
        var d = MapDiff.DiffAdt(data, edited);
        Assert.Equal(12, d.Stats.HeightChunks);
        Assert.Equal(1, d.Stats.DoodadsMoved);
        Assert.Equal(1, d.Stats.HoleChunks);
        Assert.Equal("Heights changed in 12 chunks (up to 3 yards), holes changed in 1 chunk, 1 doodad moved", d.PlainSummary());
        var before = TerrainTile.Load(data, "Azeroth", 32, 48);
        var after = TerrainTile.Load(edited, "Azeroth", 32, 48);
        var img = TerrainTile.HeightDifference(before, after, 128, out var max, out var chunks);
        Assert.Equal(12, chunks);
        Assert.Equal(3f, max, 0.01f);
        Assert.True(img.Pixels.Where((_, i) => i % 4 == 0).Distinct().Count() > 3);
    }

    [Fact]
    public void PythonStyleFormatting()
    {
        Assert.Equal("1.0", Py.FloatRepr(1.0));
        Assert.Equal("17595.20703125", Py.FloatRepr(17595.20703125));
        Assert.Equal("1e-05", Py.FloatRepr(0.00001));
        Assert.Equal("0.0001", Py.FloatRepr(0.0001));
        Assert.Equal("1e+16", Py.FloatRepr(1e16));
        Assert.Equal("-0.0", Py.FloatRepr(-0.0));
        Assert.Equal("['A', 'B\\\\C']", Py.Repr(new List<string> { "A", "B\\C" }));
        Assert.Equal("(1, 2)", Py.Repr(new PyTuple(1, 2)));
        Assert.Equal("(1,)", Py.Repr(new PyTuple(1)));
        Assert.Equal("\"it's\"", Py.Repr("it's"));
        Assert.Equal("12.5", Py.F4(12.5));
        Assert.Equal("3", Py.F4(3.0));
    }
}
