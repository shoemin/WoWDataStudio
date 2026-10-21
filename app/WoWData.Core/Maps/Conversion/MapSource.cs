using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;

namespace WoWData.Core.Maps.Conversion;

/// <summary>A map of the source version (its Map table row and where its WDT is).</summary>
public sealed record SourceMap(int Id, string Directory, string Name, int InstanceType, int AreaTableId, int LoadingScreenId,
                               int ParentMapId, int CosmeticParentMapId, int MaxPlayers, uint WdtFileDataId)
{
    public string Label => $"{Name} ({Id}, {Directory})";
}

/// <summary>An area of the source version (its AreaTable row): the values a 1.12 AreaTable row can take over.</summary>
public sealed record SourceArea(int Id, string Name, int MapId, int ParentId, int AreaBit, uint Flags, int ExplorationLevel, int FactionGroupMask,
                                int ZoneMusic, int IntroSound, int AmbienceId, int SoundProviderPref, int SoundProviderPrefUnderwater);

/// <summary>
/// Where a map conversion reads from: one client version's maps, areas, tiles, textures and minimaps, with the names of
/// its assets (the user's own 1.12 files, or a listfile the user chose). The 1.60 CASC client is the first source
/// (<see cref="Casc160MapSource"/>); 2.x / 3.x MPQ clients plug in here later with their own readers into the neutral model.
/// </summary>
public interface IMapSource
{
    /// <summary>The client in words ("_classic_beta_, 1.60.1.70450").</summary>
    string Label { get; }
    MapSourceVersion Version { get; }
    /// <summary>Where the asset names come from, in words.</summary>
    string NamesText { get; }
    IAssetNames Names { get; }
    IReadOnlyList<SourceMap> Maps();
    /// <summary>The map by ID, directory or name; null when there is none.</summary>
    SourceMap? FindMap(string text);
    IReadOnlyDictionary<int, SourceArea> Areas();
    NeutralMap ReadWdt(SourceMap map);
    NeutralTile ReadTile(SourceMap map, NeutralMap wdt, int x, int y);
    /// <summary>The 256 chunk area IDs of a tile (cheap: the root file's MCNK headers only), or null.</summary>
    int[]? ChunkAreas(SourceMap map, NeutralMap wdt, int x, int y);
    /// <summary>The 1.12 kind of a liquid type ID, or null when it is unknown.</summary>
    LiquidKind? LiquidKindOf(int liquidType);
    /// <summary>A file by FileDataID (null when absent; throws on encrypted or not downloaded files).</summary>
    byte[]? ReadFile(uint fileDataId);
    /// <summary>The FileDataID of a path in this client (its name hashes, the listfile), or null.</summary>
    uint? FileDataIdOf(string path);
    /// <summary>The tile's minimap texture, or null.</summary>
    byte[]? ReadMinimap(NeutralMap wdt, int x, int y);
}

/// <summary>
/// The 1.60 Classic client (CASC, 1.60.1.70450) as a conversion source: Map / AreaTable / LiquidType from its DB2 tables
/// with the named layout of that build (<see cref="Db2Layouts"/>), tiles through <see cref="Adt160Reader"/>. Read only.
/// </summary>
public sealed class Casc160MapSource : IMapSource
{
    private readonly OtherClient _client;
    private readonly CascStorage _storage;
    private readonly object _gate = new();
    private List<SourceMap>? _maps;
    private Dictionary<int, SourceArea>? _areas;
    private Dictionary<int, LiquidKind>? _liquids;

    public Casc160MapSource(OtherClient client, IAssetNames names, string namesText)
    {
        _client = client;
        _storage = client.Casc ?? throw new ArgumentException($"{client.Copy.Name} is not a modern (CASC) client.");
        Names = new CompositeAssetNames(names, new CascAssetNames(_storage));
        NamesText = namesText;
        if (Db2Layouts.ForBuild(client.Copy.Build) == null)
            throw new NotSupportedException($"{client.Copy.Name} ({client.Copy.Label}): the app knows the map tables (Map, AreaTable, LiquidType) of 1.60.1.70450 only. " +
                                            $"Known builds: {string.Join(", ", Db2Layouts.KnownBuilds())}.");
    }

    public string Label => $"{_client.Copy.Name}, {_client.Copy.Version}.{_client.Copy.Build}";
    public MapSourceVersion Version => MapSourceVersion.V160;
    public string NamesText { get; }
    public IAssetNames Names { get; }
    public OtherClient Client => _client;

    private Db2Table Table(string name)
    {
        var t = _client.Catalog.Open(_storage, name) ?? throw new InvalidDataException($"{_client.Copy.Name} has no {name} table on this computer.");
        if (Db2Layouts.ForBuild(_client.Copy.Build) is not { } l || !l.Apply(t, out var why))
            throw new InvalidDataException($"{_client.Copy.Name}: the {name} table does not have the columns the app knows ({(Db2Layouts.ForBuild(_client.Copy.Build) == null ? "no layout" : "layout does not match")}).");
        return t;
    }

    private static int Col(Db2Table t, string name)
    {
        try { return t.ColumnIndex(name); }
        catch (KeyNotFoundException) { return -1; }
    }

    private static int I(Db2Record r, int c) => c < 0 ? 0 : r.Int(c);
    private static string S(Db2Record r, int c) => c < 0 ? "" : r.String(c) ?? "";

    public IReadOnlyList<SourceMap> Maps()
    {
        lock (_gate)
        {
            if (_maps != null) return _maps;
            var t = Table("Map");
            int dir = Col(t, "Directory"), name = Col(t, "MapName"), type = Col(t, "InstanceType"), area = Col(t, "AreaTableID"), ls = Col(t, "LoadingScreenID"),
                parent = Col(t, "ParentMapID"), cosmetic = Col(t, "CosmeticParentMapID"), players = Col(t, "MaxPlayers"), wdt = Col(t, "WdtFileDataID");
            _maps = t.Records
                .Select(r => new SourceMap(r.Id, S(r, dir), S(r, name), I(r, type), I(r, area), I(r, ls), parent < 0 ? -1 : I(r, parent), cosmetic < 0 ? -1 : I(r, cosmetic),
                    I(r, players), wdt < 0 ? 0 : r.UInt(wdt)))
                .GroupBy(m => m.Id).Select(g => g.First()).OrderBy(m => m.Id).ToList();
            return _maps;
        }
    }

    public SourceMap? FindMap(string text)
    {
        var t = text.Trim();
        var maps = Maps();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && maps.FirstOrDefault(m => m.Id == id) is { } byId) return byId;
        return maps.FirstOrDefault(m => string.Equals(m.Directory, t, StringComparison.OrdinalIgnoreCase))
               ?? maps.FirstOrDefault(m => string.Equals(m.Name, t, StringComparison.OrdinalIgnoreCase))
               ?? maps.FirstOrDefault(m => string.Equals(MapConverter.ReadableDirectory(m.Name, m.Id), t, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<int, SourceArea> Areas()
    {
        lock (_gate)
        {
            if (_areas != null) return _areas;
            var t = Table("AreaTable");
            int name = Col(t, "AreaName"), map = Col(t, "MapID"), parent = Col(t, "ParentAreaID"), bit = Col(t, "AreaBit"), flags = Col(t, "Flags1"),
                level = Col(t, "ExplorationLevel"), team = Col(t, "FactionGroupMask"), music = Col(t, "ZoneMusic"), intro = Col(t, "IntroSound"),
                amb = Col(t, "AmbienceID"), spp = Col(t, "SoundProviderPref"), sppu = Col(t, "SoundProviderPrefUnderwater");
            _areas = new Dictionary<int, SourceArea>();
            foreach (var r in t.Records)
                _areas.TryAdd(r.Id, new SourceArea(r.Id, S(r, name), I(r, map), I(r, parent), I(r, bit), flags < 0 ? 0 : r.UInt(flags), I(r, level), I(r, team),
                    I(r, music), I(r, intro), I(r, amb), I(r, spp), I(r, sppu)));
            return _areas;
        }
    }

    /// <summary>
    /// LiquidType.Type (0 water, 1 ocean, 2 magma, 3 slime); the PBR waters of 1.60 all have Type 0 and the two PBR oceans
    /// (1250, 1296) are marked by Flags 0x400 (MEASURED, db2_layouts_70450.txt [LiquidType]).
    /// </summary>
    public LiquidKind? LiquidKindOf(int liquidType)
    {
        lock (_gate)
        {
            if (_liquids == null)
            {
                _liquids = new Dictionary<int, LiquidKind>();
                try
                {
                    var t = Table("LiquidType");
                    int type = Col(t, "Type"), flags = Col(t, "Flags");
                    foreach (var r in t.Records)
                    {
                        int k = I(r, type);
                        uint f = flags < 0 ? 0 : r.UInt(flags);
                        _liquids.TryAdd(r.Id, k switch { 1 => LiquidKind.Ocean, 2 => LiquidKind.Magma, 3 => LiquidKind.Slime, _ => (f & 0x400) != 0 ? LiquidKind.Ocean : LiquidKind.River });
                    }
                }
                catch (InvalidDataException) { }
            }
            return _liquids.TryGetValue(liquidType, out var kind) ? kind : null;
        }
    }

    public NeutralMap ReadWdt(SourceMap map)
    {
        if (map.WdtFileDataId == 0) throw new InvalidDataException($"{map.Label} has no WDT (no terrain).");
        var b = _storage.ReadFileDataId(map.WdtFileDataId) ?? throw new InvalidDataException($"{map.Label}: its WDT (FileDataID {map.WdtFileDataId}) is not in {_client.Copy.Name}.");
        return Adt160Reader.ReadWdt(b, map.Directory, Names);
    }

    public NeutralTile ReadTile(SourceMap map, NeutralMap wdt, int x, int y) => Adt160Reader.ReadTile(_storage, wdt, x, y, Names);

    public int[]? ChunkAreas(SourceMap map, NeutralMap wdt, int x, int y)
    {
        var maid = wdt.Tile(x, y).Maid;
        if (maid == null || maid[0] == 0) return null;
        var b = _storage.ReadFileDataId(maid[0]);
        if (b == null) return null;
        var res = new int[256];
        int k = 0;
        foreach (var c in MapBytes.Chunks(b))
        {
            if (c.Magic != "MCNK" || c.Offset + 8 + MapBytes.McnkHeaderSize > b.Length) continue;
            long h = c.Offset + 8;
            int ix = (int)MapBytes.U32(b, h + 4), iy = (int)MapBytes.U32(b, h + 8);
            int idx = ix < 16 && iy < 16 ? iy * 16 + ix : k;
            if (idx < 256) res[idx] = (int)MapBytes.U32(b, h + 0x34);
            k++;
        }
        return res;
    }

    public byte[]? ReadFile(uint fileDataId) => _storage.ReadFileDataId(fileDataId);

    public uint? FileDataIdOf(string path) => Names.FileDataIdOf(path) ?? _storage.FileDataIdOf(ClientData.ResolveName(path));

    public byte[]? ReadMinimap(NeutralMap wdt, int x, int y)
    {
        var maid = wdt.Tile(x, y).Maid;
        return maid == null || maid[7] == 0 ? null : _storage.ReadFileDataId(maid[7]);
    }
}

/// <summary>
/// The asset names of a conversion, by the user's rule: from the user's own 1.12 files or a listfile the user chose,
/// never a bundled or downloaded one. The 1.12 client's file names are hashed into a Classic Era (1.15.x) storage's
/// root (<see cref="ClassicPathResolver"/>: FileDataIDs are the same as 1.60's); without an Era copy, into the source's
/// own root (which knows far fewer names, MAP_SURVEY_160 section 1). The table is cached in the app's folder.
/// </summary>
public static class ConvertNames
{
    /// <summary>A Classic Era copy among the clients found, or next to the source in its Battle.net folder; null when none.</summary>
    public static ClientCopy? FindEra(ClientCopy source, IEnumerable<ClientCopy>? found)
    {
        bool IsEra(ClientCopy c) => c.Format == ClientFormat.Casc && !string.Equals(c.Folder, source.Folder, StringComparison.OrdinalIgnoreCase)
                                    && c.Version.Split('.') is [var a, var b, ..] && a == "1" && int.TryParse(b, out var minor) && minor is >= 13 and <= 15;
        var list = (found ?? Array.Empty<ClientCopy>()).Where(IsEra).ToList();
        var parent = Path.GetDirectoryName(source.Folder.TrimEnd('\\', '/'));
        if (parent != null)
            foreach (var flavor in new[] { "_classic_era_", "_anniversary_", "_classic_era_ptr_" })
            {
                var d = Path.Combine(parent, flavor);
                if (!list.Any(c => string.Equals(c.Folder, d, StringComparison.OrdinalIgnoreCase)) && Directory.Exists(d) && ClientCopies.Inspect(d) is { } c2 && IsEra(c2)) list.Add(c2);
            }
        // the plain Era flavor first, then the same Battle.net folder as the source
        return list.OrderBy(c => Path.GetFileName(c.Folder).Equals("_classic_era_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => string.Equals(Path.GetDirectoryName(c.Folder), parent, StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
    }

    /// <summary>
    /// The 1.12 path resolver for <paramref name="source"/>: the user's 1.12 names hashed into <paramref name="era"/>
    /// (or the source when null), cached in <paramref name="cacheDir"/>. <paramref name="text"/> says what was used.
    /// </summary>
    public static ClassicPathResolver Resolver(ClientData client112, CascStorage source, CascStorage? era, string? cacheDir, string sourceName, string? eraName, out string text)
    {
        var hashInto = era ?? source;
        ClassicPathResolver r;
        if (cacheDir != null)
        {
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(client112.DataDir.ToLowerInvariant() + "|" + client112.Fingerprint() + "|" + hashInto.BuildName)))[..12].ToLowerInvariant();
            r = ClassicPathResolver.LoadOrBuild(Path.Combine(cacheDir, $"names112_{key}.tsv"), client112, hashInto);
        }
        else r = ClassicPathResolver.Build(client112, hashInto);
        text = era != null
            ? $"your 1.12 game's file names, found by their hashes in {eraName} ({r.Count:N0} names)"
            : $"your 1.12 game's file names, found by their hashes in {sourceName} itself ({r.Count:N0} names; a Classic Era copy next to it would name far more)";
        return r;
    }
}

/// <summary>
/// A source opened for a conversion, shared by the app and "wds versions convert-map": the client (read only), the
/// Classic Era copy whose name hashes name the user's 1.12 files (when one is found), and the name table. Dispose it
/// when done.
/// </summary>
public sealed class MapConversionSource : IDisposable
{
    private MapConversionSource(OtherClient client, CascStorage? era, IMapSource source)
    {
        Client = client;
        Era = era;
        Source = source;
    }

    public OtherClient Client { get; }
    public CascStorage? Era { get; }
    public IMapSource Source { get; }

    /// <summary>
    /// Opens <paramref name="copy"/> (a 1.60 CASC client; MPQ clients are not a conversion source yet) with names from
    /// <paramref name="client112"/> (hashed into a Classic Era copy among <paramref name="found"/> or next to the source)
    /// and the user's <paramref name="listfile"/>. <paramref name="cacheDir"/> keeps the name table between runs.
    /// </summary>
    public static MapConversionSource Open(ClientCopy copy, string? listfile, ClientData client112, IEnumerable<ClientCopy>? found, string? cacheDir)
    {
        if (copy.Format != ClientFormat.Casc)
            throw new NotSupportedException($"{copy.Name} ({copy.Label}): maps can be converted from the modern 1.60 Classic client (CASC) only; 2.x and 3.x maps come later.");
        var oc = OtherClient.Open(copy, false, listfile);
        CascStorage? era = null;
        try
        {
            var eraCopy = ConvertNames.FindEra(copy, found);
            if (eraCopy != null)
            {
                try { era = CascStorage.Open(eraCopy.Folder); }
                catch (Exception e) when (e is IOException or InvalidDataException or CascMissingException or UnauthorizedAccessException) { era = null; }
            }
            var resolver = ConvertNames.Resolver(client112, oc.Casc!, era, cacheDir, copy.Name, era != null ? eraCopy!.Name : null, out var text);
            if (oc.ListfileNames > 0) text += $"; {oc.ListfileNames:N0} names from your listfile";
            return new MapConversionSource(oc, era, new Casc160MapSource(oc, resolver, text));
        }
        catch
        {
            era?.Dispose();
            oc.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Era?.Dispose();
        Client.Dispose();
    }
}
