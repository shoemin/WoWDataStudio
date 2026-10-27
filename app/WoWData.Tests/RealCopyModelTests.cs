using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.M2.Modern;
using WoWData.Core.Mpq;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// M2 on the real "1.60" Classic beta copy (opt-in: WOWDATA_CLIENT_COPIES; read only): sampled creature, item and
/// game object models load through the modern reader, and every loaded model's view-0 triangle count equals the count
/// its .skin file states (read independently from the file's header). Every number was measured on the copy of
/// 2026-10-20 (a snapshot: the build cannot change). The samples: every distinct creature model of CreatureDisplayInfo,
/// every 15th distinct item model of ItemDisplayInfo, every 7th distinct game object file of GameObjectDisplayInfo.
/// </summary>
[Collection(PerformanceCollection.Name)]
public class RealCopyModelTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = TestEnv.TempDir("realmodels");

    public RealCopyModelTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Sample(int Tried, int Loaded, int NotLocal, int NotChunked, int Failed, int TrianglesMatch, int V272, int V274, int HighIndexStart, long Quats, long UnitQuats);

    private static Sample Run(CascStorage st, IReadOnlyList<uint> models, int step, List<string> failures, Stopwatch time)
    {
        int tried = 0, loaded = 0, notLocal = 0, notChunked = 0, failed = 0, match = 0, v272 = 0, v274 = 0, high = 0;
        long quats = 0, unit = 0;
        for (int k = 0; k < models.Count; k += step)
        {
            uint f = models[k];
            tried++;
            byte[]? data;
            try { data = st.State(f) == CascFileState.Local ? st.ReadFileDataId(f) : null; }
            catch (Exception e) when (e is CascMissingException or CascEncryptedException) { data = null; }
            if (data == null) { notLocal++; continue; }
            if (!M2ModernReader.IsChunked(data)) { notChunked++; continue; }
            try
            {
                time.Start();
                var m = M2ModernReader.Load(data, id => st.ReadFileDataId(id));
                time.Stop();
                loaded++;
                if (m.Version == 272) v272++;
                else if (m.Version == 274) v274++;
                var skin = st.ReadFileDataId(m.Modern!.SkinFiles[0])!;
                // the .skin file's own count: its triangle-index array ({count, offset} at 0x0C)
                if (BinaryPrimitives.ReadUInt32LittleEndian(skin.AsSpan(0x0C)) / 3 == m.Views[0].Triangles.Length / 3) match++;
                if (m.Views[0].Submeshes.Any(s => s.Level != 0)) high++;
                foreach (var b in m.Bones)
                    foreach (var q in b.Rotation.Values)
                    {
                        quats++;
                        if (MathF.Abs(MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W) - 1) < 0.01f) unit++;
                    }
            }
            catch (M2Exception e)
            {
                time.Stop();
                failed++;
                failures.Add($"#{f}: {e.Message}");
            }
        }
        return new Sample(tried, loaded, notLocal, notChunked, failed, match, v272, v274, high, quats, unit);
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void SampledCreatureItemAndObjectModelsLoadWithTheirSkinCounts()
    {
        using var st = CascStorage.Open(ClientCopyFactAttribute.PathOf(RealCopyOpenTests.Beta)!);
        var cat = new Catalog160(st, Db2Catalog.ForStorage(st), st.Build);
        var creature = cat.CreatureDisplayIds().Select(i => cat.CreatureDisplay(i)!.Model).Where(m => m != 0).Distinct().OrderBy(x => x).ToList();
        var items = cat.ItemDisplayIds().SelectMany(i => cat.ItemDisplay(i)!.Models.Select(m => m.File)).Distinct().OrderBy(x => x).ToList();
        var objects = cat.GameObjectDisplayIds().Select(cat.GameObjectModel).Where(m => m != 0).Distinct().OrderBy(x => x).ToList();
        Assert.Equal((1017, 6296, 3196), (creature.Count, items.Count, objects.Count));
        var failures = new List<string>();
        var time = new Stopwatch();
        var c = Run(st, creature, 1, failures, time);
        var i = Run(st, items, 15, failures, time);
        var o = Run(st, objects, 7, failures, time);
        foreach (var (what, x) in new[] { ("creature", c), ("item", i), ("game object", o) })
            _out.WriteLine($"{what}: {x}");
        _out.WriteLine($"{c.Loaded + i.Loaded + o.Loaded} models read in {time.Elapsed.TotalSeconds:0.0} s");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        // creatures: every distinct model; 19 are not on this computer
        Assert.Equal(new Sample(1017, 998, 19, 0, 0, 998, 710, 288, 61, c.Quats, c.Quats), c);
        // items: 420 sampled, 1 not on this computer
        Assert.Equal(new Sample(420, 419, 1, 0, 0, 419, 87, 332, 0, i.Quats, i.Quats), i);
        // game objects: 457 sampled, 4 not on this computer, 10 are buildings (WMO, not chunked M2: later, with the maps)
        Assert.Equal(new Sample(457, 443, 4, 10, 0, 443, 409, 34, 0, o.Quats, o.Quats), o);
        // every compressed bone rotation decodes to a unit quaternion (+- 0.01); M3: 68,542,995 with the bones of the
        // 22 skeleton files (and their parents' animations) of the character-style models (56,991,534 without, in M2)
        Assert.Equal(68542995, c.Quats + i.Quats + o.Quats);
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void TheBoarDisplayShowsItsSkinAndTheSwordItsItemTexture()
    {
        var root = ClientCopyFactAttribute.Root!;
        var found = ClientCopies.Find(root);
        var beta = found.Single(x => x.Folder.EndsWith(RealCopyOpenTests.Beta, StringComparison.OrdinalIgnoreCase));
        var c112Dir = Path.Combine(root, RealCopyOpenTests.Client112, "Data");
        using var c112 = Directory.Exists(c112Dir) ? new ClientData(c112Dir) : null;
        var paths = new AppPaths(Path.Combine(_root, "home"));
        using var s = new AppSession(paths, AppSettings.Load(paths));
        s.OpenOtherAsync(beta, new OtherOpenOptions { Client112 = c112, Found = found }, null).GetAwaiter().GetResult();
        // Mottled Boar's display 503: Creature\Boar\Boar.m2 (1.12 name through Era), skin BoarSkinIvory
        var boar = s.ModelSource!.Load(@"Creature\Boar\Boar.m2");
        Assert.Equal((272u, 401, 504, 61, 17), (boar.Version, boar.Vertices.Length, boar.Views[0].Triangles.Length / 3, boar.Bones.Length, boar.Sequences.Length));
        var look = s.Looks!.CreatureDisplay(503);
        Assert.EndsWith("BoarSkinIvory.blp", look.Slots[11].Path!, StringComparison.OrdinalIgnoreCase);
        Assert.True(look.Slots[11].Image.Width <= ModernModelResolver.MaxTextureSize);
        // the animation names of your 1.12 game for the same ids
        if (c112 != null) Assert.Equal(new[] { "Stand", "Run" }, new[] { 0, 5 }.Select(s.Looks.AnimationName));
        Assert.Contains(boar.Sequences, q => q.AnimId == 0);
        // Worn Shortsword (item 25): item display 1542, its model texture in slot 2; its blade's reflection layer is drawn
        // with the first texture only (an approximation the model statistics name)
        var item = s.Index!.ItemDisplayOf(25)!.Value;
        Assert.Equal(1542, item);
        var d = s.Details(s.FindEntry("item:25")!);
        var model = d.Models.First(m => m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase));
        var sword = s.ModelSource.Load(model);
        Assert.EndsWith("Sword_1H_Short_A_02Rusty.blp", s.Looks.ItemDisplay((int)item, model).Slots[2].Path!, StringComparison.OrdinalIgnoreCase);
        // M3: its blade's reflection layer (combiner 0x8001, ArmorReflect3) is combined by the 3D view (approximately)
        Assert.Contains(sword.Modern!.Approximated, a => a.Contains("drawn with the viewer's version of the combiner", StringComparison.Ordinal));
        Assert.Equal(M2ModernReader.OpAddAlpha, Assert.Single(sword.Modern.LayerOps).Value);
    }
}
