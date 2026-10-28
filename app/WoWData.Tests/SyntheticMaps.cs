using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Neutral;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// M4: the maps of the synthetic "1.60" client (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 5.1), made only from our
/// own bytes: a WDT and two split tiles written by <see cref="Adt160Writer"/> from tiles built in code, minimap BLPs made
/// by <see cref="BlpEncoder"/>, a WMO-only map, a map whose WDT is not downloaded, and a WMO of version 17 in the modern
/// FileDataID form (GFID, MOMT texture FileDataIDs, MODI, no MOTX, a batch with the large material field, unknown
/// modern chunks to skip). Expected values come from how the fixture is built.
/// </summary>
public static class SyntheticMaps
{
    public const uint MapWdt = 1100001, DungeonWdt = 1100002, MissingWdt = 1100003;
    public const uint TileARoot = 1100010, TileAObj0 = 1100011, TileAObj1 = 1100012, TileATex0 = 1100013;
    public const uint TileBRoot = 1100014, TileBObj0 = 1100015, TileBObj1 = 1100016, TileBTex0 = 1100017;
    public const uint MinimapA = 1100020, MinimapB = 1100021;
    public const uint WmoRoot = 1100030, WmoGroup = 1100031, WmoTexture = 1100032;
    /// <summary>The six ground textures of tile A (1100040-1100045).</summary>
    public const uint Ground0 = 1100040;
    public const int TileAX = 32, TileAY = 32, TileBX = 33, TileBY = 32;
    public const string MapDir = "TestMap";
    /// <summary>Zones: 100 "Test Zone" with its subzone 101 "Test Subzone"; 102 "Other Zone"; 103 is locked.</summary>
    public const int Zone = 100, Subzone = 101, OtherZone = 102;
    /// <summary>The minimap colours of the two tiles.</summary>
    public static readonly (byte R, byte G, byte B) ColourA = (220, 60, 40), ColourB = (40, 90, 220);

    /// <summary>The rows the map tables of the test client get (merged into <see cref="SyntheticClients.Tables"/>).</summary>
    public static Dictionary<string, (IReadOnlyList<Row> Rows, IReadOnlyList<int>? Locked)> Tables() => new()
    {
        ["Map"] = (new[]
        {
            R(0, ("Directory", MapDir), ("MapName", "Test Continent"), ("WdtFileDataID", (int)MapWdt)),
            R(1, ("Directory", "TestDungeon"), ("MapName", "Test Dungeon"), ("InstanceType", 1), ("WdtFileDataID", (int)DungeonWdt)),
            R(3, ("Directory", "TestMissing"), ("MapName", "Test Missing"), ("InstanceType", 1), ("WdtFileDataID", (int)MissingWdt)),
        }, new[] { 2 }),
        ["AreaTable"] = (new[]
        {
            R(Zone, ("AreaName", "Test Zone"), ("MapID", 0)),
            R(Subzone, ("AreaName", "Test Subzone"), ("MapID", 0), ("ParentAreaID", Zone)),
            R(OtherZone, ("AreaName", "Other Zone"), ("MapID", 0)),
        }, new[] { 103 }),
        ["LiquidType"] = (new[] { R(5, ("Name", "Slow Water"), ("Type", 0)), R(7, ("Name", "Slow Magma"), ("Type", 2)) }, null),
    };

    /// <summary>A BLP of one colour, <paramref name="size"/> pixels square.</summary>
    public static byte[] Blp(int size, (byte R, byte G, byte B) c)
    {
        var img = new RgbaImage(size, size);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = c.R; img.Pixels[i + 1] = c.G; img.Pixels[i + 2] = c.B; img.Pixels[i + 3] = 255; }
        return BlpEncoder.EncodeNew(img, "dxt1");
    }

    /// <summary>
    /// Tile A (32, 32) as built in code: heights rising with the chunk index; chunk 0 with 6 layers whose 8-bit blend
    /// weights paint row bands (layer L gets 40 * L in rows 10L..10L+9); 8 x 8 holes in chunk 1; a water instance
    /// (LiquidType 5) on the left half of chunk 2 with sloped heights; a magma instance (LiquidType 7) in chunk 3; area
    /// 101 in chunks 0-127, 100 in 128-255; two cube doodads and a building scaled by 2 (MODF flag 0x4).
    /// </summary>
    public static NeutralTile TileA()
    {
        var t = NeutralTile.CreateFlat(MapDir, TileAX, TileAY, 20f);
        t.Source = MapSourceVersion.V160;
        t.AlphaBlend = AlphaBlendMode.Weights;
        for (int k = 0; k < 6; k++) t.Textures.Add(new AssetRef(null, Ground0 + (uint)k));
        for (int i = 0; i < 256; i++)
        {
            var c = t.Chunks[i]!;
            for (int k = 0; k < 145; k++) c.Heights[k] = i * 0.25f + (k % 17) * 0.1f;
            c.AreaId = i < 128 ? (uint)Subzone : Zone;
            c.Layers.Add(new NeutralLayer { Texture = t.Textures[i % 2] });
        }
        var c0 = t.Chunks[0]!;
        c0.Layers.Clear();
        for (int L = 0; L < 6; L++)
        {
            var layer = new NeutralLayer { Texture = t.Textures[L] };
            if (L > 0)
            {
                layer.Flags = 0x100;
                layer.Alpha = new byte[4096];
                for (int r = 10 * L; r < 10 * L + 10 && r < 64; r++)
                    for (int col = 0; col < 64; col++) layer.Alpha[r * 64 + col] = (byte)(40 * L);
            }
            c0.Layers.Add(layer);
        }
        t.Chunks[1]!.Holes = 0x0000_0000_0018_1800UL;      // fine holes at rows 1-2, columns 3-4
        var water = new NeutralLiquid { LiquidType = 5, Min = 21f, Max = 23f, RectX = 0, RectY = 0, RectW = 4, RectH = 8, Heights = new float[5 * 9] };
        for (int r = 0; r <= 8; r++) for (int col = 0; col <= 4; col++) water.Heights[r * 5 + col] = 21f + col * 0.5f;
        for (int r = 0; r < 8; r++) for (int col = 0; col < 4; col++) water.Exists |= 1UL << (r * 8 + col);
        t.Chunks[2]!.Liquids.Add(water);
        t.Chunks[3]!.Liquids.Add(new NeutralLiquid { LiquidType = 7, Min = 22f, Max = 22f, Exists = ulong.MaxValue });
        t.DoodadModels.Add(new AssetRef(null, CubeModel));
        t.WmoModels.Add(new AssetRef(null, WmoRoot));
        var corner = TerrainMath.MapHalf - TileAX * TerrainMath.TileSize;
        t.Doodads.Add(new NeutralDoodad { Model = t.DoodadModels[0], UniqueId = 501, Position = new Vector3(TerrainMath.MapHalf - corner + 100, 25, TerrainMath.MapHalf - corner + 100), ScaleRaw = 1024, Flags = 0x40 });
        t.Doodads.Add(new NeutralDoodad { Model = t.DoodadModels[0], UniqueId = 502, Position = new Vector3(TerrainMath.MapHalf - corner + 150, 25, TerrainMath.MapHalf - corner + 120), ScaleRaw = 2048, Flags = 0x40 });
        t.Wmos.Add(new NeutralWmo
        {
            Model = t.WmoModels[0], UniqueId = 503, Position = new Vector3(TerrainMath.MapHalf - corner + 200, 22, TerrainMath.MapHalf - corner + 200),
            ExtentMin = new Vector3(0, 0, 0), ExtentMax = new Vector3(10, 10, 10), Flags = 0x8 | 0x4, ScaleRaw = 2048,
        });
        t.Chunks[0]!.DoodadRefs.AddRange(new[] { 0, 1 });
        t.Chunks[0]!.WmoRefs.Add(0);
        return t;
    }

    /// <summary>Tile B (33, 32): flat at 10 yards, one layer, area 102. Its obj0 file is encrypted in the store.</summary>
    public static NeutralTile TileB()
    {
        var t = NeutralTile.CreateFlat(MapDir, TileBX, TileBY, 10f);
        t.Source = MapSourceVersion.V160;
        t.AlphaBlend = AlphaBlendMode.Weights;
        t.Textures.Add(new AssetRef(null, Ground0));
        foreach (var c in t.AllChunks)
        {
            c.AreaId = OtherZone;
            c.Layers.Add(new NeutralLayer { Texture = t.Textures[0] });
        }
        return t;
    }

    private static byte[] Chunk(string magic, byte[] data)
    {
        var b = new byte[8 + data.Length];
        for (int i = 0; i < 4; i++) b[i] = (byte)magic[3 - i];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
        data.CopyTo(b, 8);
        return b;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] U32(params uint[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] F32(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    /// <summary>
    /// The building's root (MVER 17, the modern FileDataID form; layout as our reader reads it, FORMAT KNOWLEDGE checked
    /// on the 1.60 copy): MOHD (1 material, 1 group, 1 doodad def, bounds -1..1), MOMT (texture 1 = <see cref="WmoTexture"/>
    /// at +0x0C, blend 0), MOGN, MOGI, MODS, MODI (the cube model), MODD, GFID (<see cref="WmoGroup"/>), and MOMX / MAVG /
    /// MGI2 with made-up bytes that a reader must skip. No MOTX.
    /// </summary>
    public static byte[] WmoRootFile()
    {
        var mohd = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(0), 1);      // materials
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(4), 1);      // groups
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(20), 1);     // doodad defs
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(24), 1);     // doodad sets
        F32(-1, -1, -1, 1, 1, 1).CopyTo(mohd, 0x24);
        var momt = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(momt.AsSpan(12), WmoTexture);
        var mogi = new byte[32];
        F32(-1, -1, -1, 1, 1, 1).CopyTo(mogi, 4);
        BinaryPrimitives.WriteInt32LittleEndian(mogi.AsSpan(28), -1);
        var mods = new byte[32];
        Encoding.ASCII.GetBytes("Set_$DefaultGlobal").CopyTo(mods, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(mods.AsSpan(24), 1);
        var modd = new byte[40];
        BinaryPrimitives.WriteSingleLittleEndian(modd.AsSpan(32), 1f);     // scale
        return Cat(Chunk("MVER", U32(17)), Chunk("MOHD", mohd), Chunk("MOMT", momt), Chunk("MOMX", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            Chunk("MOGN", new byte[4]), Chunk("MOGI", mogi), Chunk("MODS", mods), Chunk("MODI", U32(CubeModel)), Chunk("MODD", modd),
            Chunk("MAVG", new byte[12]), Chunk("MGI2", new byte[16]), Chunk("GFID", U32(WmoGroup)));
    }

    /// <summary>The building's group: MVER 17, MOGP (a 0x44 header, flags 0) with MOPY, MOVI (a box: 12 triangles over 8
    /// corners), MOVT, MONR, MOTV and one MOBA batch whose flag 0x2 says "material in the 16-bit field at +0x0A" (0 there,
    /// 0xFF in the old byte at +0x17, which would be out of range).</summary>
    public static byte[] WmoGroupFile()
    {
        var pos = Enumerable.Range(0, 8).Select(CubeCorner).ToArray();
        var movt = F32(pos.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray());
        var monr = F32(pos.SelectMany(p => { var n = Vector3.Normalize(p); return new[] { n.X, n.Y, n.Z }; }).ToArray());
        var motv = F32(pos.SelectMany(p => new[] { (p.X + 1) / 2, (p.Y + 1) / 2 }).ToArray());
        var movi = CubeTriangles.SelectMany(BitConverter.GetBytes).ToArray();
        var moba = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(moba.AsSpan(10), 0);                              // material (large)
        BinaryPrimitives.WriteUInt32LittleEndian(moba.AsSpan(12), 0);                              // first index
        BinaryPrimitives.WriteUInt16LittleEndian(moba.AsSpan(16), (ushort)CubeTriangles.Length);    // index count
        BinaryPrimitives.WriteUInt16LittleEndian(moba.AsSpan(20), 7);                               // last vertex
        moba[22] = 0x2;
        moba[23] = 0xFF;
        var inner = Cat(Chunk("MOPY", new byte[CubeTriangles.Length / 3 * 2]), Chunk("MOVI", movi), Chunk("MOVT", movt), Chunk("MONR", monr), Chunk("MOTV", motv), Chunk("MOBA", moba));
        var header = new byte[0x44];
        F32(-1, -1, -1, 1, 1, 1).CopyTo(header, 0x0C);
        return Cat(Chunk("MVER", U32(17)), Chunk("MOGP", Cat(header, inner)));
    }

    /// <summary>The map files of the test client.</summary>
    public static IEnumerable<StoreFile> Files()
    {
        var a = Adt160Writer.WriteTile(TileA(), null, new Adt160WriterOptions { BigAlpha = true }, new uint[] { TileARoot, TileAObj0, TileAObj1, TileATex0, 0, 0, 0, MinimapA });
        var b = Adt160Writer.WriteTile(TileB(), null, new Adt160WriterOptions { BigAlpha = true }, new uint[] { TileBRoot, TileBObj0, TileBObj1, TileBTex0, 0, 0, 0, MinimapB });
        var map = new NeutralMap { Name = MapDir, Source = MapSourceVersion.V160, Flags = 0x284 };
        map.Tile(TileAX, TileAY).HasAdt = true;
        map.Tile(TileBX, TileBY).HasAdt = true;
        var wdt = Adt160Writer.WriteWdt(map, null, new Adt160WriterOptions { BigAlpha = true },
            new Dictionary<int, uint[]> { [TileAY * 64 + TileAX] = a.Maid!, [TileBY * 64 + TileBX] = b.Maid! });
        var dungeon = new NeutralMap { Name = "TestDungeon", Source = MapSourceVersion.V160, Flags = 0x241 };
        dungeon.GlobalWmo = new NeutralWmo { Model = new AssetRef(null, WmoRoot), Flags = 0x8, ExtentMin = new Vector3(-1), ExtentMax = new Vector3(1) };
        var dwdt = Adt160Writer.WriteWdt(dungeon, null, new Adt160WriterOptions { BigAlpha = false });
        yield return new StoreFile(MapWdt, null, wdt);
        yield return new StoreFile(DungeonWdt, null, dwdt);
        // not downloaded (other bytes than the dungeon's WDT: a store keeps one copy of equal contents)
        yield return new StoreFile(MissingWdt, null, dwdt.Concat(new byte[] { 0x58, 0x58, 0x58, 0x58, 0, 0, 0, 0 }).ToArray(), false);
        yield return new StoreFile(TileARoot, null, a.Root!);
        yield return new StoreFile(TileAObj0, null, a.Obj0!);
        yield return new StoreFile(TileAObj1, null, a.Obj1!);
        yield return new StoreFile(TileATex0, null, a.Tex0!);
        yield return new StoreFile(TileBRoot, null, b.Root!);
        yield return new StoreFile(TileBObj0, null, b.Obj0!, true, 0);           // encrypted: tile B shows no placements
        yield return new StoreFile(TileBObj1, null, b.Obj1!);
        yield return new StoreFile(TileBTex0, null, b.Tex0!);
        yield return new StoreFile(MinimapA, null, Blp(64, ColourA));
        yield return new StoreFile(MinimapB, null, Blp(64, ColourB));
        yield return new StoreFile(WmoRoot, null, WmoRootFile());
        yield return new StoreFile(WmoGroup, null, WmoGroupFile());
        yield return new StoreFile(WmoTexture, null, Blp(16, (180, 170, 150)));
        for (int k = 0; k < 6; k++) yield return new StoreFile(Ground0 + (uint)k, null, Blp(16, ((byte)(40 * k), 160, (byte)(200 - 30 * k))));
    }
}
