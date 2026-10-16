using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using WoWData.Core.Dbc;
using WoWData.Core.Mpq;

namespace WoWData.Core.Usage;

/// <summary>One aggregated placement row: how many times <see cref="Model"/> is placed on a map tile in an area
/// (AreaTable id, 0 = unknown), directly by the tile (<see cref="Parent"/> empty) or inside a placed WMO.</summary>
public readonly record struct PlacementCount(string Model, int Map, int TileX, int TileY, int Area, string Parent, int Count);

/// <summary>What <see cref="MapScanner.Scan"/> found: the chunk area ids of every tile and the placements.</summary>
public sealed class MapScanResult
{
    /// <summary>(map, tile x, tile y) -> 256 AreaTable ids, index = chunk y * 16 + chunk x (MCNK indexY / indexX).</summary>
    public Dictionary<(int Map, int TileX, int TileY), int[]> Areas { get; } = new();
    public List<PlacementCount> Placements { get; } = new();
    /// <summary>0.6.1: the ground textures (MTEX names, listfile spelling) of every tile: (texture, map, tile x, tile y).</summary>
    public List<(string Texture, int Map, int TileX, int TileY)> GroundTextures { get; } = new();
    public int Maps { get; set; }
    public int Tiles { get; set; }
    public int DoodadPlacements { get; set; }
    public int WmoPlacements { get; set; }
    public int WmoDoodads { get; set; }
}

/// <summary>
/// The map-tile placement index (MODEL_TOOL_RESEARCH / ROADMAP "Search index addition"): every map of Map.dbc, its WDT
/// tile list (MAIN flags &amp; 1), and per ADT tile the MCNK area ids (the 0x80-byte MCNK header: indexX @+4, indexY
/// @+8, areaId @+0x34, located through MHDR.ofsMCIN as CMapArea_ParseAdtHeader does), the M2 placements (MMDX / MMID /
/// MDDF, 36 bytes each) and the WMO placements (MWMO / MWID / MODF, 64 bytes each). Placements on tile borders are
/// listed by several tiles with the same unique id: each is counted once. A WMO's own doodads are counted per placed
/// WMO: the doodads of set 0 (always shown) plus those of the placement's doodad set (MODS / MODN / MODD of the root
/// WMO). The area of a placement is looked up at its own position.
/// Positions: MDDF/MODF position (a, height, b) -> tile x = a / 533.33, tile y = b / 533.33 (the ADT file name is
/// &lt;map&gt;_&lt;x&gt;_&lt;y&gt;); world X = 17066.67 - b, world Y = 17066.67 - a. Server spawns use world coordinates.
/// </summary>
public static class MapScanner
{
    public const float TileSize = 1600f / 3f;
    public const float MapHalf = 32 * TileSize;

    /// <summary>The tile and chunk of a world position (server coordinates), or null outside the 64x64 grid.</summary>
    public static (int TileX, int TileY, int ChunkIndex)? Locate(float worldX, float worldY)
    {
        float a = 32f - worldY / TileSize, b = 32f - worldX / TileSize;
        return LocateTileUnits(a, b);
    }

    private static (int TileX, int TileY, int ChunkIndex)? LocateTileUnits(float a, float b)
    {
        if (!(a >= 0 && a < 64 && b >= 0 && b < 64)) return null;
        int tx = (int)a, ty = (int)b;
        int cx = Math.Clamp((int)((a - tx) * 16), 0, 15), cy = Math.Clamp((int)((b - ty) * 16), 0, 15);
        return (tx, ty, cy * 16 + cx);
    }

    /// <summary>Area id at a world position from a scanned area table (0 when the tile has no terrain).</summary>
    public static int AreaAt(IReadOnlyDictionary<(int, int, int), int[]> areas, int map, float worldX, float worldY)
    {
        var loc = Locate(worldX, worldY);
        if (loc == null) return 0;
        return areas.TryGetValue((map, loc.Value.TileX, loc.Value.TileY), out var a) ? a[loc.Value.ChunkIndex] : 0;
    }

    private sealed record WmoInfo(string[] DoodadNames, (int Start, int Count)[] Sets);

    private readonly record struct Doodad(uint Uid, string Name, float A, float B);
    private readonly record struct WmoPlace(uint Uid, string Name, float A, float B, int DoodadSet);

    private sealed class Tile
    {
        public int X, Y;
        public int[]? Areas;
        public List<Doodad> Doodads = new();
        public List<WmoPlace> Wmos = new();
        public List<string> Textures = new();
    }

    /// <summary>Scans every map. <paramref name="canonical"/> turns a file name from the map files into the listfile's
    /// spelling (.mdx -> .m2). <paramref name="progress"/> gets (tiles done, tiles total).</summary>
    public static MapScanResult Scan(ClientData client, IEnumerable<MapRec> maps, Func<string, string> canonical,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var result = new MapScanResult();
        var jobs = new List<(MapRec Map, List<(int X, int Y)> Tiles, WmoPlace? Global)>();
        foreach (var m in maps)
        {
            if (m.Directory.Length == 0) continue;
            var wdt = client.Read($"World\\Maps\\{m.Directory}\\{m.Directory}.wdt");
            if (wdt == null) continue;
            var (tiles, global) = ReadWdt(wdt);
            if (tiles.Count == 0 && global == null) continue;
            jobs.Add((m, tiles, global));
        }
        int total = jobs.Sum(j => j.Tiles.Count), done = 0;
        var wmoCache = new ConcurrentDictionary<string, WmoInfo?>(StringComparer.OrdinalIgnoreCase);
        var nameCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Canon(string n) => nameCache.GetOrAdd(n, x => canonical(x));
        var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) };

        foreach (var (map, tileList, global) in jobs)
        {
            var tiles = new ConcurrentBag<Tile>();
            Parallel.ForEach(tileList, po, t =>
            {
                var data = client.Read($"World\\Maps\\{map.Directory}\\{map.Directory}_{t.X}_{t.Y}.adt");
                if (data != null)
                {
                    var tile = ReadAdt(data, t.X, t.Y);
                    if (tile != null) tiles.Add(tile);
                }
                int d = Interlocked.Increment(ref done);
                if (d % 64 == 0) progress?.Invoke(d, total);
            });
            result.Maps++;
            result.Tiles += tiles.Count;
            var areas = new Dictionary<(int, int), int[]>();
            foreach (var t in tiles)
                if (t.Areas != null)
                {
                    areas[(t.X, t.Y)] = t.Areas;
                    result.Areas[(map.Id, t.X, t.Y)] = t.Areas;
                }
            var counts = new Dictionary<(string Model, int Tx, int Ty, int Area, string Parent), int>();
            void Count(string model, float a, float b, string parent, int n = 1)
            {
                var loc = LocateTileUnits(a / TileSize, b / TileSize);
                int tx = loc?.TileX ?? -1, ty = loc?.TileY ?? -1;
                int area = loc != null && areas.TryGetValue((tx, ty), out var ar) ? ar[loc.Value.ChunkIndex] : 0;
                var k = (model, tx, ty, area, parent);
                counts[k] = counts.GetValueOrDefault(k) + n;
            }
            var seenD = new HashSet<uint>();
            var seenW = new HashSet<uint>();
            var wmos = new List<WmoPlace>();
            foreach (var t in tiles.OrderBy(t => t.Y).ThenBy(t => t.X))
            {
                foreach (var tex in t.Textures.Where(x => x.Length > 0).Select(Canon).Distinct(StringComparer.OrdinalIgnoreCase))
                    result.GroundTextures.Add((tex, map.Id, t.X, t.Y));
                foreach (var d in t.Doodads)
                    if (seenD.Add(d.Uid))
                    {
                        Count(Canon(d.Name), d.A, d.B, "");
                        result.DoodadPlacements++;
                    }
                foreach (var w in t.Wmos)
                    if (seenW.Add(w.Uid)) wmos.Add(w);
            }
            if (global != null) wmos.Add(global.Value);
            foreach (var w in wmos)
            {
                var wmo = Canon(w.Name);
                Count(wmo, w.A, w.B, "");
                result.WmoPlacements++;
                var info = wmoCache.GetOrAdd(wmo, p => ReadWmo(client, p));
                if (info == null) continue;
                var perModel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                void AddSet(int s)
                {
                    if (s < 0 || s >= info.Sets.Length) return;
                    var (start, count) = info.Sets[s];
                    for (int i = start; i < start + count && i < info.DoodadNames.Length; i++)
                        if (info.DoodadNames[i].Length > 0) perModel[info.DoodadNames[i]] = perModel.GetValueOrDefault(info.DoodadNames[i]) + 1;
                }
                AddSet(0);
                if (w.DoodadSet != 0) AddSet(w.DoodadSet);
                foreach (var (m, n) in perModel)
                {
                    Count(Canon(m), w.A, w.B, wmo, n);
                    result.WmoDoodads += n;
                }
            }
            foreach (var (k, n) in counts)
                result.Placements.Add(new PlacementCount(k.Model, map.Id, k.Tx, k.Ty, k.Area, k.Parent, n));
            ct.ThrowIfCancellationRequested();
        }
        progress?.Invoke(total, total);
        return result;
    }

    private static uint U32(byte[] b, int p) => p >= 0 && p + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)) : 0;
    private static float F32(byte[] b, int p) => p >= 0 && p + 4 <= b.Length ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p)) : 0;

    /// <summary>Positional WDT read as CMap_LoadWdt @00694760: MVER, MPHD (32 bytes), MAIN (64x64 x 8 bytes), then for
    /// a WMO-only map (MPHD flags &amp; 1) MWMO + one MODF.</summary>
    private static (List<(int X, int Y)> Tiles, WmoPlace? Global) ReadWdt(byte[] b)
    {
        var tiles = new List<(int, int)>();
        int pos = 8 + 4;                 // MVER header + version
        if (pos + 8 + 32 > b.Length) return (tiles, null);
        uint flags = U32(b, pos + 8);
        pos += 8 + 32;                   // MPHD
        int main = pos + 8;
        for (int i = 0; i < 64 * 64; i++)
            if ((U32(b, main + i * 8) & 1) != 0) tiles.Add((i % 64, i / 64));
        pos = main + 64 * 64 * 8;
        WmoPlace? global = null;
        if ((flags & 1) != 0 && pos + 8 <= b.Length)
        {
            int nameSize = (int)U32(b, pos + 4);
            if (nameSize > 0 && pos + 8 + nameSize <= b.Length)
            {
                var name = ZString(b, pos + 8, nameSize);
                pos += 8 + nameSize;
                if (name.Length > 0 && pos + 8 + 64 <= b.Length)
                {
                    int m = pos + 8;
                    global = new WmoPlace(U32(b, m + 4), name, F32(b, m + 8), F32(b, m + 16), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(m + 0x3A)));
                }
            }
        }
        return (tiles, global);
    }

    private static string ZString(byte[] b, int start, int max)
    {
        int end = start;
        int limit = Math.Min(b.Length, start + max);
        while (end < limit && b[end] != 0) end++;
        return Encoding.Latin1.GetString(b, start, end - start);
    }

    /// <summary>String block -> {offset: string}.</summary>
    private static Dictionary<uint, string> Strings(byte[] b, int start, int size)
    {
        var d = new Dictionary<uint, string>();
        int end = Math.Min(b.Length, start + size), p = start;
        while (p < end)
        {
            int e = p;
            while (e < end && b[e] != 0) e++;
            d[(uint)(p - start)] = Encoding.Latin1.GetString(b, p, e - p);
            p = e + 1;
        }
        return d;
    }

    private static Tile? ReadAdt(byte[] b, int x, int y)
    {
        // CMapArea_ParseAdtHeader: MHDR = file + MVER.size + 8, its data 8 bytes later; offsets relative to that data
        int mhdr = 8 + (int)U32(b, 4) + 8;
        if (mhdr + 64 > b.Length) return null;
        int Block(int field, out int size)
        {
            uint ofs = U32(b, mhdr + field * 4);
            int hp = mhdr + (int)ofs;
            if (ofs == 0 || hp + 8 > b.Length) { size = 0; return -1; }
            size = (int)Math.Min(U32(b, hp + 4), (uint)(b.Length - hp - 8));
            return hp + 8;
        }
        var t = new Tile { X = x, Y = y };
        // MTEX: the ground texture names
        int mtex = Block(2, out int mtexSize);
        if (mtex >= 0) t.Textures.AddRange(Strings(b, mtex, mtexSize).Values);
        // MCIN: 256 x {offset, size, flags, async id}
        int mcin = Block(1, out int mcinSize);
        if (mcin >= 0)
        {
            var areas = new int[256];
            for (int i = 0; i < 256 && (i + 1) * 16 <= mcinSize; i++)
            {
                int off = (int)U32(b, mcin + i * 16);
                int h = off + 8;
                if (off <= 0 || h + 0x80 > b.Length) continue;
                int ix = (int)U32(b, h + 4), iy = (int)U32(b, h + 8);
                if (ix is < 0 or > 15 || iy is < 0 or > 15) continue;
                areas[iy * 16 + ix] = (int)U32(b, h + 0x34);
            }
            t.Areas = areas;
        }
        // M2 placements
        int mmdx = Block(3, out int mmdxSize), mmid = Block(4, out int mmidSize), mddf = Block(7, out int mddfSize);
        if (mmdx >= 0 && mmid >= 0 && mddf >= 0)
        {
            var names = Strings(b, mmdx, mmdxSize);
            int nIds = mmidSize / 4;
            for (int i = 0; i + 36 <= mddfSize; i += 36)
            {
                int p = mddf + i;
                uint nameId = U32(b, p);
                if (nameId >= nIds || !names.TryGetValue(U32(b, mmid + (int)nameId * 4), out var name) || name.Length == 0) continue;
                t.Doodads.Add(new Doodad(U32(b, p + 4), name, F32(b, p + 8), F32(b, p + 16)));
            }
        }
        // WMO placements
        int mwmo = Block(5, out int mwmoSize), mwid = Block(6, out int mwidSize), modf = Block(8, out int modfSize);
        if (mwmo >= 0 && mwid >= 0 && modf >= 0)
        {
            var names = Strings(b, mwmo, mwmoSize);
            int nIds = mwidSize / 4;
            for (int i = 0; i + 64 <= modfSize; i += 64)
            {
                int p = modf + i;
                uint nameId = U32(b, p);
                if (nameId >= nIds || !names.TryGetValue(U32(b, mwid + (int)nameId * 4), out var name) || name.Length == 0) continue;
                t.Wmos.Add(new WmoPlace(U32(b, p + 4), name, F32(b, p + 8), F32(b, p + 16), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 0x3A))));
            }
        }
        return t;
    }

    /// <summary>Root WMO: doodad names (MODN), doodad definitions (MODD, 40 bytes: name offset in the low 24 bits) and
    /// doodad sets (MODS, 32 bytes: name[20], first index, count, pad). Returns null for a missing or unreadable file.</summary>
    private static WmoInfo? ReadWmo(ClientData client, string path)
    {
        byte[]? b;
        try { b = client.Read(path); }
        catch (Exception e) when (e is MpqException or IOException) { return null; }
        if (b == null) return null;
        Dictionary<uint, string>? modn = null;
        int moddPos = -1, moddSize = 0;
        var sets = new List<(int, int)>();
        int p = 0;
        while (p + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, p, 4);
            int size = (int)U32(b, p + 4);
            if (size < 0 || p + 8 + size > b.Length) break;
            switch (id)
            {
                case "NDOM": modn = Strings(b, p + 8, size); break;
                case "DDOM": moddPos = p + 8; moddSize = size; break;
                case "SDOM":
                    for (int i = 0; i + 32 <= size; i += 32) sets.Add(((int)U32(b, p + 8 + i + 20), (int)U32(b, p + 8 + i + 24)));
                    break;
            }
            p += 8 + size;
        }
        if (modn == null || moddPos < 0) return new WmoInfo(Array.Empty<string>(), sets.ToArray());
        var names = new string[moddSize / 40];
        for (int i = 0; i < names.Length; i++)
        {
            uint ofs = U32(b, moddPos + i * 40) & 0xFFFFFF;
            names[i] = modn.TryGetValue(ofs, out var n) ? n : "";
        }
        return new WmoInfo(names, sets.ToArray());
    }
}
