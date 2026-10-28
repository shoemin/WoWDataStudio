using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Neutral;
using WoWDataStudio.Rendering;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// M4 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): the Maps refactor (<c>IMapView</c>, <c>LoadTerrain</c>
/// moved into <see cref="MapCatalog"/>, the renderer's 8-layer extension for other versions) leaves what the 3D view
/// draws for a 1.12 tile exactly as it was. The digests below were taken with the code before the refactor (HEAD
/// e6dfc39): every vertex, index, chunk's layers, texture path, alpha/shadow atlas, liquid vertex, placement matrix and
/// building mesh is hashed.
/// </summary>
public class TerrainAsset112UnchangedTests
{
    private readonly ITestOutputHelper _out;

    public TerrainAsset112UnchangedTests(ITestOutputHelper output) => _out = output;

    /// <summary>Digest of the 1.12 tile from code (<see cref="SyntheticTile112"/>) before the refactor.</summary>
    public const string SyntheticDigest = "D58CB6719D73580A9345700BD38CD40B075D6306296B1675DFF8508EC7FD7709";
    /// <summary>Digest of Azeroth 32_48 of the stock 1.12 client (geometry, layers, atlases, liquids, placements) before the refactor.</summary>
    public const string RealDigest = "2E47FCBDB35996639180FA299B203305D595FA8A13D837787521BA349D40ABEB";

    /// <summary>A 1.12 tile written by <see cref="Adt112Writer"/> from a tile built in code: uneven heights, 4 layers with
    /// alpha maps, holes, a shadowed area, a river and a magma layer, two doodads and a building.</summary>
    public static byte[] SyntheticTile112()
    {
        var t = NeutralTile.CreateFlat("Synth", 30, 40, 5f);
        var tex = new[] { @"Tileset\Test\GrassA.blp", @"Tileset\Test\DirtB.blp", @"Tileset\Test\RockC.blp", @"Tileset\Test\SandD.blp" };
        foreach (var p in tex) t.Textures.Add(new AssetRef(p));
        for (int i = 0; i < 256; i++)
        {
            var c = t.Chunks[i]!;
            for (int k = 0; k < 145; k++) c.Heights[k] = (float)(Math.Sin(i * 0.37 + k * 0.11) * 6 + (i % 16) * 0.5);
            for (int k = 0; k < 145; k++)
            {
                c.Normals[k * 3] = (sbyte)((i + k) % 40 - 20);
                c.Normals[k * 3 + 1] = (sbyte)((i * 3 + k) % 30 - 15);
                c.Normals[k * 3 + 2] = 120;
            }
            c.AreaId = (uint)(10 + i % 3);
            int nl = 1 + i % 4;
            for (int L = 0; L < nl; L++)
            {
                var layer = new NeutralLayer { Texture = t.Textures[(i + L) % 4] };
                if (L > 0)
                {
                    layer.Flags = 0x100;
                    layer.Alpha = new byte[4096];
                    for (int a = 0; a < 4096; a++) layer.Alpha[a] = NeutralLayer.Expand4(((a / 64) * L + a % 64 + i) % 16);
                }
                c.Layers.Add(layer);
            }
            if (i % 7 == 0) c.Holes = NeutralChunk.ExpandLowResHoles(0x0660);
            if (i % 5 == 0)
            {
                c.Flags |= 1;
                c.Shadow = new bool[4096];
                for (int a = 0; a < 4096; a++) c.Shadow[a] = (a / 64 + a % 64) % 9 == 0;
            }
            if (i is 34 or 35 or 200)
            {
                var l = new NeutralLiquid { Origin = LiquidOrigin.Mclq, Kind = i == 200 ? LiquidKind.Magma : LiquidKind.River, Min = 3, Max = 4, Exists = 0x00FF00FF00FF00FFUL, Heights = new float[81] };
                for (int k = 0; k < 81; k++) l.Heights[k] = 3 + (k % 9) * 0.1f;
                c.Liquids.Add(l);
            }
        }
        t.DoodadModels.Add(new AssetRef(@"World\Test\Tree01.m2"));
        t.DoodadModels.Add(new AssetRef(@"World\Test\Rock01.m2"));
        t.WmoModels.Add(new AssetRef(@"World\Test\House01.wmo"));
        t.Doodads.Add(new NeutralDoodad { Model = t.DoodadModels[0], UniqueId = 7, Position = new Vector3(16100, 10, 16200), Rotation = new Vector3(0, 45, 0), ScaleRaw = 1500 });
        t.Doodads.Add(new NeutralDoodad { Model = t.DoodadModels[1], UniqueId = 8, Position = new Vector3(16150, 12, 16220), Rotation = new Vector3(10, 0, 5), ScaleRaw = 900 });
        t.Wmos.Add(new NeutralWmo { Model = t.WmoModels[0], UniqueId = 9, Position = new Vector3(16120, 8, 16210), Rotation = new Vector3(0, 90, 0), ExtentMin = new Vector3(16100, 0, 16190), ExtentMax = new Vector3(16140, 20, 16230), DoodadSet = 1 });
        return Adt112Writer.WriteAdt(t);
    }

    /// <summary>A stable texture per path (its colour from the path's hash).</summary>
    public static RgbaImage? FakeTexture(string path)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        var img = new RgbaImage(4, 4);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = h[0]; img.Pixels[i + 1] = h[1]; img.Pixels[i + 2] = h[2]; img.Pixels[i + 3] = 255; }
        return img;
    }

    /// <summary>Everything the renderer reads from a terrain asset, hashed (build time excluded).</summary>
    public static string Digest(TerrainAsset a)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Bytes(ReadOnlySpan<byte> b) => sha.AppendData(b);
        void I(long v) => Bytes(BitConverter.GetBytes(v));
        void F(float v) => Bytes(BitConverter.GetBytes(v));
        void S(string s) { I(s.Length); Bytes(Encoding.UTF8.GetBytes(s)); }
        void V(Vector3 v) { F(v.X); F(v.Y); F(v.Z); }
        void M(Matrix4x4 m) { foreach (var f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) F(f); }
        V(a.Origin);
        Bytes(MemoryMarshal.AsBytes(a.Vertices.AsSpan()));
        Bytes(MemoryMarshal.AsBytes(a.Indices.AsSpan()));
        I(a.Chunks.Count);
        foreach (var c in a.Chunks)
        {
            I(c.Tile); I(c.ChunkX); I(c.ChunkY); I(c.IndexStart); I(c.IndexCount); I(c.LayerCount);
            for (int L = 0; L < 4; L++) I(c.Layers[L]);
        }
        I(a.TexturePaths.Count);
        foreach (var p in a.TexturePaths) S(p);
        foreach (var p in a.DrawnTexturePaths) S(p);
        I(a.Textures.Count(t => t != null));
        I(a.AlphaAtlases.Count);
        foreach (var at in a.AlphaAtlases) Bytes(at.Pixels);
        Bytes(MemoryMarshal.AsBytes(a.Liquid.AsSpan()));
        I(a.Doodads.Count);
        foreach (var (asset, world) in a.Doodads) { S(asset.Path); M(world); }
        I(a.Wmos.Count);
        foreach (var (mesh, world) in a.Wmos)
        {
            S(mesh.Path); M(world);
            Bytes(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()));
            Bytes(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
            foreach (var b in mesh.Batches) { I(b.Start); I(b.Count); I(b.Texture); I(b.AlphaKey ? 1 : 0); I(b.Blend ? 1 : 0); }
        }
        foreach (var u in a.DoodadUids) I(u);
        foreach (var u in a.WmoUids) I(u);
        V(a.BoundsMin); V(a.BoundsMax);
        I(a.MissingModels); I(a.DoodadModels); I(a.GroundTextures); I(a.MissingSpecular); I(a.SpecularGround ? 1 : 0);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    [Fact]
    public void ASynthetic112TilesTerrainAssetIsUnchanged()
    {
        var bytes = SyntheticTile112();
        var tile = TerrainTile.Load(bytes, "Synth", 30, 40);
        var a = TerrainAsset.Build(new[] { tile }, _ => null, FakeTexture, new TerrainBuildOptions());
        var d = Digest(a);
        _out.WriteLine($"synthetic digest {d}; {a.Summary}");
        Assert.Equal(SyntheticDigest, d);
        AssertNoModernParts(a);
    }

    /// <summary>A 1.12 tile uses none of the parts added for other versions: no second alpha atlas, no layer past 4.</summary>
    private static void AssertNoModernParts(TerrainAsset a)
    {
        Assert.All(a.AlphaAtlases2, Assert.Null);
        Assert.All(a.Chunks, c => Assert.True(c.LayerCount <= 4 && c.Layers.Skip(4).All(l => l == -1)));
    }

    [Client112Fact]
    public void AStock112TilesTerrainAssetWithItsPlacementsIsUnchanged()
    {
        var client = Neutral112Data.Client;
        // the tile as the Maps tab reads it now: MapCatalog.LoadTerrain (today's code moved there)
        var cat = MapCatalog.Load(client, null);
        cat.ReadTile = client.Read;
        IMapView view = cat;
        var tile = view.LoadTerrain(view.ByDirectory("Azeroth")!, 32, 48)!;
        var a = TerrainAsset.Build(new[] { tile }, client.Read, FakeTexture, new TerrainBuildOptions());
        var d = Digest(a);
        _out.WriteLine($"real digest {d}; {a.Summary}");
        Assert.True(a.Doodads.Count > 0 && a.Wmos.Count > 0, a.Summary);
        Assert.Equal(RealDigest, d);
        AssertNoModernParts(a);
        Assert.All(tile.AllChunks, c => Assert.Null(c.HighResHoles));
        Assert.Empty(tile.Notes);
        Assert.All(tile.Wmos, w => Assert.Equal(1f, w.Scale));
    }
}
