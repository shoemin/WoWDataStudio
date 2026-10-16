namespace WoWData.Core.Maps;

public enum MapSeverity { Error = 0, Warning = 1, Info = 2 }

/// <summary>One finding of the validator: the same fields, codes and words as wowdata/maps/validate.py.</summary>
public sealed record MapFinding(MapSeverity Severity, string Code, string File, string Where, string Message)
{
    public string SeverityText => Severity switch { MapSeverity.Error => "error", MapSeverity.Warning => "warning", _ => "info" };

    public override string ToString() => $"{SeverityText,-7} {Code,-14} {File}{(Where.Length > 0 ? $" [{Where}]" : "")}: {Message}";

    /// <summary>The finding in plain words for a modder (what it means, what to do).</summary>
    public string Plain => MapValidator.Explain(this);
}

public sealed class MapReport
{
    public List<MapFinding> Findings { get; } = new();
    public int Files { get; set; }

    public void Add(MapSeverity sev, string code, string file, string where, string message) => Findings.Add(new MapFinding(sev, code, file, where, message));

    public int Count(MapSeverity s) => Findings.Count(f => f.Severity == s);
    public bool Ok => Count(MapSeverity.Error) == 0;
    public IEnumerable<MapFinding> Errors => Findings.Where(f => f.Severity == MapSeverity.Error);
    public IEnumerable<MapFinding> Warnings => Findings.Where(f => f.Severity == MapSeverity.Warning);
}

/// <summary>
/// The validator for 1.12.1 (build 5875) map files: ADT tiles, WDT and WDL. A port of wowdata/maps/validate.py (the
/// reference; it holds the full evidence from our decompile): every rule is derived from what the 1.12 client's loaders
/// read, and calibrated on the real client data (all 2,429 ADTs, 43 WDTs and 43 WDLs pass with no error and no warning).
/// The C# results are checked against the Python's on real and deliberately broken files (MapTests).
///  * error: the 1.12 client (or the server's extractor) would read wrong data, garble, overrun or crash;
///  * warning: the client copes, but the file differs from every stock 1.12 file in a way that indicates a wrong writer;
///  * info: harmless differences from the stock files.
/// Loaders: CMap_LoadWdl @006944a0 (positional: MAOF at file offset 20), CMap_LoadWdt @00694760 (positional MVER, MPHD,
/// MAIN [, MWMO, MODF]), CMapArea_ParseAdtHeader @006c2010 (MHDR offsets), CMapChunk_SetupMcnkPointers @006af970 (MCNK
/// sub-chunks at header offset + 8, unchecked), CMapChunk_AddLayer @006aff80 (at most 4 layers, 4-bit alpha),
/// CMapChunk_CreateLiquids @006af760 (804-byte MCLQ layers), CMapChunk_CreateSoundEmitters @006af810 (52-byte MCSE).
/// </summary>
public static class MapValidator
{
    private const MapSeverity ERROR = MapSeverity.Error, WARNING = MapSeverity.Warning, INFO = MapSeverity.Info;

    public static readonly string[] StockTopOrder = { "MVER", "MHDR", "MCIN", "MTEX", "MMDX", "MMID", "MWMO", "MWID", "MDDF", "MODF" };

    private static readonly (string Magic, string Field)[] FSubs =
    {
        ("MCVT", "ofsHeight"), ("MCNR", "ofsNormal"), ("MCLY", "ofsLayer"), ("MCRF", "ofsRefs"),
        ("MCSH", "ofsShadow"), ("MCAL", "ofsAlpha"), ("MCLQ", "ofsLiquid"), ("MCSE", "ofsSndEmitters"),
    };

    private const uint KnownMcnkFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x8000;

    private static string S(object? v) => Py.Str(v);
    private static string X(long v) => Py.Hex(v);

    // ------------------------------------------------------------------------------------------- WDL
    public static MapReport ValidateWdl(byte[] b, string name = "", MapReport? report = null, IReadOnlyCollection<(int X, int Y)>? wdtTiles = null)
    {
        var r = report ?? new MapReport();
        r.Files++;
        var w = new WdlFile(b, name);
        if (b.Length < 20 + MapBytes.WdlMaofSize)
        {
            r.Add(ERROR, "WDL-SIZE", name, "", $"file is {b.Length} bytes; the 1.12 loader reads 0x{X(20 + MapBytes.WdlMaofSize)} bytes of header + offset table");
            return r;
        }
        if (w.MverMagic != "MVER" || w.MverSize != 4)
            r.Add(ERROR, "WDL-MVER", name, "0", $"expected MVER of size 4, found {S(w.MverMagic)} size {S(w.MverSize)}");
        else if (w.Version != 18)
            r.Add(WARNING, "WDL-VERSION", name, "8", $"MVER version {S(w.Version)} (stock: 18)");
        if (w.TableMagic != "MAOF" || w.TableSize != MapBytes.WdlMaofSize)
            r.Add(ERROR, "WDL-POS", name, "12",
                "the 1.12 client (CMap_LoadWdl @006944a0) reads the offset table at file offset 20, right after MVER; " +
                $"found chunk {S(w.TableMagic)} (size {S(w.TableSize)}) there instead of MAOF (size 0x4000)" +
                (w.TableMagic is "MWMO" or "MWID" or "MODF" ? " -- WotLK low-res WMO chunks (MWMO/MWID/MODF) before MAOF shift every tile" : ""));
        int bad = 0;
        for (int i = 0; i < w.Offsets.Length; i++)
        {
            uint o = w.Offsets[i];
            if (o == 0) continue;
            if (o + 8L + MapBytes.WdlMareData > b.Length || MapBytes.MagicAt(b, o) != "MARE")
            {
                bad++;
                if (bad <= 5)
                    r.Add(ERROR, "WDL-MARE", name, $"tile {i % 64},{i / 64}",
                        $"offset 0x{X(o)} does not point at a MARE chunk with 0x{X(MapBytes.WdlMareData)} bytes inside the file ({S(MapBytes.MagicAt(b, o))})");
            }
            else if (MapBytes.U32(b, o + 4) != MapBytes.WdlMareData)
                r.Add(WARNING, "WDL-MARE-SIZE", name, $"tile {i % 64},{i / 64}", $"MARE size {MapBytes.U32(b, o + 4)} (the client reads 0x442 bytes regardless)");
        }
        if (bad > 5) r.Add(ERROR, "WDL-MARE", name, "", $"{bad - 5} more bad tile offsets");
        var names = w.Top.Select(t => t.Magic).ToList();
        foreach (var m in new[] { "MWMO", "MWID", "MODF" })
            if (names.Contains(m))
                r.Add(w.TableMagic == "MAOF" ? WARNING : ERROR, "WDL-WOTLK", name, "", $"{m} chunk present (WotLK low-resolution WMO data; no stock 1.12 WDL has it)");
        if (names.Contains("MAHO")) r.Add(INFO, "WDL-MAHO", name, "", "MAHO (hole masks) present: never read by 1.12, absent from stock WDLs");
        if (wdtTiles != null)
        {
            var a = new HashSet<(int, int)>(wdtTiles);
            var t = new HashSet<(int, int)>(w.Tiles());
            var tMinusA = t.Except(a).OrderBy(p => p).ToList();
            var aMinusT = a.Except(t).OrderBy(p => p).ToList();
            if (tMinusA.Count > 0)
                r.Add(WARNING, "WDL-TILES", name, "", $"{tMinusA.Count} WDL tiles have no ADT in the WDT MAIN table, e.g. {Py.Repr(Tuples(tMinusA.Take(3)))}");
            if (aMinusT.Count > 0)
                r.Add(WARNING, "WDL-TILES", name, "", $"{aMinusT.Count} WDT tiles have no WDL heights (no far terrain), e.g. {Py.Repr(Tuples(aMinusT.Take(3)))}");
        }
        return r;
    }

    private static List<PyTuple> Tuples(IEnumerable<(int, int)> l) => l.Select(p => new PyTuple(p.Item1, p.Item2)).ToList();

    // ------------------------------------------------------------------------------------------- WDT
    public static MapReport ValidateWdt(byte[] b, string name = "", MapReport? report = null)
    {
        var r = report ?? new MapReport();
        r.Files++;
        var w = new WdtFile(b, name);
        var expectedSizes = new Dictionary<string, long> { ["MVER"] = 4, ["MPHD"] = MapBytes.WdtMphdSize, ["MAIN"] = MapBytes.WdtMainSize, ["MODF"] = MapBytes.ModfSize };
        foreach (var s in w.Slots)
        {
            if (s.Magic == null)
            {
                r.Add(ERROR, "WDT-POS", name, s.Position.ToString(), $"file ends before the {s.Expected} chunk the 1.12 loader reads here");
                continue;
            }
            if (s.Magic != s.Expected)
                r.Add(ERROR, "WDT-POS", name, s.Position.ToString(), $"CMap_LoadWdt @00694760 reads {s.Expected} here positionally; found {s.Magic}");
            else if (expectedSizes.TryGetValue(s.Expected, out var es) && s.Size != es)
                r.Add(ERROR, "WDT-SIZE", name, s.Position.ToString(), $"{s.Expected} size {s.Size}, the 1.12 loader reads {es} bytes");
        }
        if (w.Version != null && w.Version != 18) r.Add(WARNING, "WDT-VERSION", name, "8", $"MVER version {w.Version} (stock: 18)");
        if (b.Length < w.EndOfRead) r.Add(ERROR, "WDT-TRUNC", name, "", $"file is {b.Length} bytes; the 1.12 loader reads {w.EndOfRead}");
        uint fl = w.MphdFlags ?? 0;
        if ((fl & 0x4) != 0)
            r.Add(ERROR, "WDT-BIGALPHA", name, "MPHD", "MPHD flag 0x4 (8-bit \"big\" alpha maps): the 1.12 client only reads 4-bit alpha, so a map saved with this flag has garbled texture blending");
        if ((fl & 0x2) != 0) r.Add(WARNING, "WDT-MCCV", name, "MPHD", "MPHD flag 0x2 (MCCV vertex shading, WotLK); no stock 1.12 WDT has it");
        if ((fl & ~0xFu) != 0) r.Add(WARNING, "WDT-MPHD", name, "MPHD", $"unknown MPHD flags 0x{X(fl & ~0xFu)} (stock 1.12: only 0x1)");
        if ((fl & 0x8) != 0) r.Add(INFO, "WDT-MPHD", name, "MPHD", "MPHD flag 0x8 (doodad sort) is ignored by 1.12; no stock WDT has it");
        var badBits = new List<(int X, int Y, uint F)>();
        for (int i = 0; i < w.Main.Length; i++)
            if ((w.Main[i].Flags & ~1u) != 0) badBits.Add((i % 64, i / 64, w.Main[i].Flags));
        if (badBits.Count > 0)
        {
            var sev = badBits.Any(t => (t.F & 2) != 0) ? ERROR : WARNING;
            r.Add(sev, "WDT-MAIN", name, "", $"{badBits.Count} MAIN entries have flags other than 0x1, e.g. tile {badBits[0].X},{badBits[0].Y} = 0x{X(badBits[0].F)} " +
                                              "(bit 0x2 makes the 1.12 client think the tile is already being loaded: it is never loaded)");
        }
        if (w.Main.Any(m => m.AsyncId != 0)) r.Add(WARNING, "WDT-MAIN", name, "", "MAIN asyncId dwords are not 0 (stock: always 0)");
        if ((fl & 1) != 0)
        {
            if (string.IsNullOrEmpty(w.GlobalWmoName)) r.Add(ERROR, "WDT-GLOBALWMO", name, "", "MPHD flag 0x1 (global WMO map) but the MWMO name is empty");
        }
        else
        {
            var trailing = w.Top.Where(t => t.Offset >= w.AfterMain).Select(t => t.Magic).ToList();
            if (trailing.Count > 0 && !(trailing.Count == 1 && trailing[0] == "MWMO"))
                r.Add(INFO, "WDT-TAIL", name, "", $"chunks after MAIN: {Py.Repr(trailing)} (stock: one empty MWMO; not read by 1.12)");
            if (trailing.Count == 0) r.Add(INFO, "WDT-TAIL", name, "", "no (empty) MWMO after MAIN (stock files have one; 1.12 does not read it)");
        }
        return r;
    }

    // ------------------------------------------------------------------------------------------- ADT
    public static MapReport ValidateAdt(byte[] b, string name = "", MapReport? report = null, int maxPerCode = 8)
    {
        var r = report ?? new MapReport();
        r.Files++;
        var seen = new Dictionary<string, int>();
        void Add(MapSeverity sev, string code, string where, string msg)
        {
            seen[code] = seen.GetValueOrDefault(code) + 1;
            if (seen[code] <= maxPerCode) r.Add(sev, code, name, where, msg);
            else if (seen[code] == maxPerCode + 1) r.Add(sev, code, name, "", $"(more {code} findings in this file are not listed)");
        }

        if (b.Length < 0x14 + 64)
        {
            Add(ERROR, "ADT-SIZE", "", $"file too small ({b.Length} bytes)");
            return r;
        }
        var a = new AdtFile(b, name);
        // --- top level: MVER / MHDR position (CMapArea_ParseAdtHeader)
        if (MapBytes.MagicAt(b, 0) != "MVER" || a.MverSize != 4)
            Add(ERROR, "ADT-MVER", "0", "expected MVER of size 4 at offset 0 (the client finds MHDR at 8 + MVER.size); " +
                                        $"found {S(MapBytes.MagicAt(b, 0))} size {S(a.MverSize)}");
        else if (a.Version != 18)
            Add(WARNING, "ADT-VERSION", "8", $"MVER version {S(a.Version)} (stock: 18)");
        if (MapBytes.MagicAt(b, a.MhdrPos) != "MHDR")
        {
            Add(ERROR, "ADT-MHDR", a.MhdrPos.ToString(), $"no MHDR right after MVER (found {S(MapBytes.MagicAt(b, a.MhdrPos))})");
            return r;
        }
        uint mhdrSize = MapBytes.U32Or0(b, a.MhdrPos + 4);
        if (mhdrSize != 64) Add(WARNING, "ADT-MHDR", a.MhdrPos.ToString(), $"MHDR size {mhdrSize} (stock: 64)");
        foreach (var (field, magic) in MapBytes.MhdrChunks)
        {
            a.Blocks.TryGetValue(magic, out var blk);
            if (a.Mhdr.GetValueOrDefault(field) == 0)
            {
                Add(ERROR, "ADT-MHDR-OFS", "MHDR." + field, $"offset is 0: the client would read {magic} from the MHDR itself");
                continue;
            }
            if (blk.Size == null || blk.Header + 8 + blk.Size > b.Length)
            {
                Add(ERROR, "ADT-MHDR-OFS", "MHDR." + field, $"{magic} at 0x{X(blk.Header)} runs past the end of the file");
                continue;
            }
            if (MapBytes.MagicAt(b, blk.Header) != magic)
                Add(ERROR, "ADT-MHDR-OFS", "MHDR." + field, $"points at {S(MapBytes.MagicAt(b, blk.Header))}, not {magic}");
        }
        foreach (var field in new[] { "ofsMFBO", "ofsMH2O", "ofsMTXF" })
            if (a.Mhdr.GetValueOrDefault(field) != 0) Add(WARNING, "ADT-WOTLK", "MHDR." + field, $"non-zero ({field[3..]} is a WotLK chunk; stock 1.12: 0)");
        if (a.Mhdr.GetValueOrDefault("flags") != 0) Add(WARNING, "ADT-MHDR", "MHDR.flags", $"0x{X(a.Mhdr["flags"])} (stock 1.12: 0; flag 1 = MFBO, WotLK)");
        // --- sequential walk: order, truncation, WotLK chunks
        var top = new List<string?>();
        foreach (var (pos, m, s) in a.Top)
        {
            top.Add(m);
            if (pos + 8 + s > b.Length) Add(ERROR, "ADT-TRUNC", pos.ToString(), $"chunk {S(m)} (size {s}) runs past the end of the file");
            if (m != null && MapBytes.WotlkTopChunks.Contains(m))
                Add(m is "MH2O" or "MFBO" or "MTXF" ? ERROR : WARNING, "ADT-WOTLK", pos.ToString(), $"{m} is not a 1.12 chunk (later expansions only)");
        }
        var head = top.Where(m => m != "MCNK").ToList();
        int nMcnk = top.Count(m => m == "MCNK");
        if (!head.SequenceEqual(StockTopOrder) || nMcnk != 256 || !top.Take(10).SequenceEqual(StockTopOrder))
            Add(WARNING, "ADT-ORDER", "", $"chunk order {Py.Repr(head)} + {nMcnk} MCNK differs from the stock order {Py.Repr(StockTopOrder)} + 256 MCNK " +
                                          "(the client locates chunks through MHDR/MCIN offsets, so this alone is harmless)");
        // --- string tables and placements
        int ntex = a.Textures.Count;
        var mmdxStarts = a.Mmdx.Select(kv => kv.Key).ToHashSet();
        var mwmoStarts = a.Mwmo.Select(kv => kv.Key).ToHashSet();
        for (int i = 0; i < a.Mmid.Length; i++)
            if (!mmdxStarts.Contains(a.Mmid[i])) Add(ERROR, "ADT-MMID", $"MMID[{i}]", $"offset {a.Mmid[i]} is not the start of an MMDX name");
        for (int i = 0; i < a.Mwid.Length; i++)
            if (!mwmoStarts.Contains(a.Mwid[i])) Add(ERROR, "ADT-MWID", $"MWID[{i}]", $"offset {a.Mwid[i]} is not the start of an MWMO name");
        foreach (var (magic, rec) in new[] { ("MDDF", MapBytes.MddfSize), ("MODF", MapBytes.ModfSize) })
        {
            a.Blocks.TryGetValue(magic, out var blk);
            if (blk.Size is { } size && size != 0 && size % rec != 0) Add(ERROR, "ADT-" + magic, magic, $"size {size} is not a multiple of {rec} bytes");
        }
        for (int i = 0; i < a.Mddf.Count; i++)
        {
            var d = a.Mddf[i];
            if (d.NameId >= a.Mmid.Length) Add(ERROR, "ADT-MDDF", $"MDDF[{i}]", $"nameId {d.NameId} out of range (MMID has {a.Mmid.Length} entries)");
            if (d.Flags != 0) Add(INFO, "ADT-MDDF-FLAGS", $"MDDF[{i}]", $"flags 0x{X(d.Flags)} (stock 1.12: 0; not read by the client)");
            if (d.Scale == 0) Add(WARNING, "ADT-MDDF", $"MDDF[{i}]", "scale 0 (invisible doodad)");
        }
        for (int i = 0; i < a.Modf.Count; i++)
            if (a.Modf[i].NameId >= a.Mwid.Length) Add(ERROR, "ADT-MODF", $"MODF[{i}]", $"nameId {a.Modf[i].NameId} out of range (MWID has {a.Mwid.Length} entries)");
        foreach (var (kind, uidsList) in new[] { ("MDDF", a.Mddf.Select(d => d.Uid).ToList()), ("MODF", a.Modf.Select(d => d.Uid).ToList()) })
        {
            var uids = new Dictionary<uint, List<int>>();
            var order = new List<uint>();
            for (int i = 0; i < uidsList.Count; i++)
            {
                if (!uids.TryGetValue(uidsList[i], out var l))
                {
                    uids[uidsList[i]] = l = new List<int>();
                    order.Add(uidsList[i]);
                }
                l.Add(i);
            }
            var dup = order.Where(u => uids[u].Count > 1).ToList();
            if (dup.Count > 0)
                Add(WARNING, "ADT-UID", kind, $"{dup.Count} unique ids used more than once in this tile, e.g. {dup[0]} at {Py.Repr(uids[dup[0]])} (the client " +
                                              "hashes placements by unique id: the duplicates are not placed)");
        }
        // --- MCIN / MCNK
        if (a.Mcin.Count != 256) Add(ERROR, "ADT-MCIN", "MCIN", $"{a.Mcin.Count} entries, the client indexes 256");
        var starts = new HashSet<long>();
        for (int i = 0; i < a.Mcin.Count; i++)
        {
            var ent = a.Mcin[i];
            var c = a.Mcnk[i];
            string where = $"MCNK {i} ({i % 16},{i / 16})";
            if (c == null || c.Magic != "MCNK")
            {
                Add(ERROR, "ADT-MCIN", where, $"MCIN offset 0x{X(ent.Offset)} does not point at an MCNK chunk");
                continue;
            }
            if (starts.Contains(c.Offset)) Add(ERROR, "ADT-MCIN", where, "two MCIN entries point at the same MCNK");
            starts.Add(c.Offset);
            if (c.End > b.Length)
            {
                Add(ERROR, "ADT-TRUNC", where, "MCNK runs past the end of the file");
                continue;
            }
            if (ent.Size != c.Size + 8) Add(WARNING, "ADT-MCIN", where, $"MCIN size {ent.Size} != MCNK size + 8 ({c.Size + 8})");
            if (ent.Flags != 0 || ent.AsyncId != 0)
                Add(WARNING, "ADT-MCIN", where, $"MCIN flags/asyncId {ent.Flags}/{ent.AsyncId} (stock: 0/0; the client uses bit 0 itself)");
            if (!c.HasHeader || c.Size < MapBytes.McnkHeaderSize)
            {
                Add(ERROR, "ADT-MCNK", where, "MCNK smaller than its 0x80-byte header");
                continue;
            }
            ValidateMcnk(b, a, c, i, ntex, Add, where);
        }
        return r;
    }

    private static void ValidateMcnk(byte[] b, AdtFile a, McnkRecord c, int i, int ntex, Action<MapSeverity, string, string, string> add, string where)
    {
        uint fl = c.Flags;
        if (c["indexX"] != (uint)(i % 16) || c["indexY"] != (uint)(i / 16))
            add(WARNING, "ADT-MCNK-IDX", where, $"header index {c["indexX"]},{c["indexY"]} (the client places the chunk by its MCIN position)");
        if ((fl & 0x10000) != 0) add(ERROR, "ADT-WOTLK", where, "MCNK flag 0x10000 (WotLK high-resolution holes): 1.12 reads the 16-bit hole mask");
        if ((fl & ~(KnownMcnkFlags | 0x10000)) != 0) add(WARNING, "ADT-MCNK-FLAGS", where, $"unknown MCNK flags 0x{X(fl & ~(KnownMcnkFlags | 0x10000))}");
        uint holes = c["holes"];
        if (holes > 0xFFFF && holes != 0xFFFFFFFF) add(WARNING, "ADT-HOLES", where, $"hole mask 0x{X(holes)} uses the high 16 bits (1.12: 16-bit mask)");
        else if (holes == 0xFFFFFFFF) add(INFO, "ADT-HOLES", where, "hole mask 0xFFFFFFFF (whole-chunk hole; stock files only use the low 16 bits)");
        if (c["ofsMCCV"] != 0) add(WARNING, "ADT-WOTLK", where, "ofsMCCV is set (MCCV vertex colours are WotLK)");
        if (c["unused78"] != 0 || c["unused7C"] != 0) add(INFO, "ADT-MCNK-TAIL", where, "header bytes 0x78..0x7F not zero");
        long end = c.End;
        long nl = c["nLayers"];
        long nrefs = (long)c["nDoodadRefs"] + c["nMapObjRefs"];
        int nliq = MapBytes.McnkLiquidFlags.Count(bit => (fl & bit) != 0);
        var need = new Dictionary<string, long>
        {
            ["MCVT"] = MapBytes.McvtSize, ["MCNR"] = MapBytes.McnrSize, ["MCLY"] = MapBytes.MclySize * nl, ["MCRF"] = 4 * nrefs,
            ["MCSH"] = (fl & 1) != 0 ? MapBytes.McshSize : 0, ["MCLQ"] = (long)MapBytes.MclqLayerSize * nliq,
            ["MCSE"] = (long)MapBytes.McseSize * c["nSndEmitters"],
        };
        if (nl > 4) add(ERROR, "ADT-MCLY", where, $"{nl} texture layers: the 1.12 client stores at most 4 (CMapChunk_AddLayer writes past its 4-entry layer array)");
        // MCLY records
        var layers = new List<(uint Tid, uint Lf, uint Oa, uint Eff)>();
        long lyOfs = c.Offset + c["ofsLayer"] + 8;
        for (int L = 0; L < Math.Min(nl, 8); L++)
        {
            long p = lyOfs + L * 16;
            if (p + 16 > end) break;
            layers.Add((MapBytes.U32(b, p), MapBytes.U32(b, p + 4), MapBytes.U32(b, p + 8), MapBytes.U32(b, p + 12)));
        }
        long alphaNeed = 0;
        var alphaOfs = new List<(int L, uint Oa)>();
        for (int L = 0; L < layers.Count; L++)
        {
            var (tid, lf, oa, _) = layers[L];
            if (tid >= ntex) add(ERROR, "ADT-MCLY", where, $"layer {L} textureId {tid} out of range (MTEX has {ntex} names)");
            if ((lf & 0x200) != 0) add(ERROR, "ADT-ALPHA", where, $"layer {L} has MCLY flag 0x200 (compressed alpha, WotLK): 1.12 reads raw 4-bit alpha");
            if ((lf & ~0x7FFu) != 0) add(WARNING, "ADT-MCLY", where, $"layer {L} unknown MCLY flags 0x{X(lf)}");
            if (L == 0 && (lf & 0x100) != 0) add(WARNING, "ADT-MCLY", where, "layer 0 has an alpha map (flag 0x100); stock files never do");
            if ((lf & 0x100) != 0)
            {
                alphaOfs.Add((L, oa));
                alphaNeed = Math.Max(alphaNeed, oa + (long)MapBytes.AlphaLayerSize);
            }
        }
        need["MCAL"] = alphaNeed;
        if (alphaOfs.Count > 0)
        {
            var steps = alphaOfs.Select(t => (long)t.Oa).ToList();
            long sizeAlphaData = (long)c["sizeAlpha"] - 8;
            if (steps.Any(o => o % MapBytes.BigAlphaLayerSize == 0 && o != 0) && steps.All(o => o % MapBytes.BigAlphaLayerSize == 0)
                                                                                && sizeAlphaData >= (long)MapBytes.BigAlphaLayerSize * steps.Count)
                add(ERROR, "ADT-BIGALPHA", where, "alpha layers are 4096 bytes apart (8-bit \"big\" alpha): 1.12 reads 2048-byte 4-bit layers (texture blending garbled)");
            else if (sizeAlphaData != (long)MapBytes.AlphaLayerSize * steps.Count && sizeAlphaData > (long)MapBytes.AlphaLayerSize * steps.Count)
                add(WARNING, "ADT-ALPHA", where, $"sizeAlpha {c["sizeAlpha"]} is larger than {steps.Count} alpha layers of 2048 bytes");
        }
        // locate every sub-chunk the client uses and check bounds/overlap/magic
        var spans = new List<(long Start, long Stop, string Magic)>();
        foreach (var (magic, field) in FSubs)
        {
            long n = need[magic];
            long ofs = c[field];
            if (n == 0 && magic != "MCVT" && magic != "MCNR")
            {
                if (ofs != 0 && c.Offset + ofs + 8 > end) add(WARNING, "ADT-SUBOFS", where, $"{magic} offset points outside the MCNK (unused here)");
                continue;
            }
            if (ofs == 0)
            {
                add(ERROR, "ADT-SUBOFS", where, $"{magic} needed ({n} bytes) but its offset is 0");
                continue;
            }
            long hp = c.Offset + ofs;
            long start = hp + 8, stop = hp + 8 + n;
            if (hp < c.Offset + 8 + MapBytes.McnkHeaderSize || stop > end)
            {
                add(ERROR, "ADT-SUBOFS", where, $"{magic} data 0x{X(start)}..0x{X(stop)} lies outside the MCNK data (0x{X(c.Offset + 8 + MapBytes.McnkHeaderSize)}..0x{X(end)})");
                continue;
            }
            var m = MapBytes.MagicAt(b, hp);
            if (m != magic) add(WARNING, "ADT-SUBMAGIC", where, $"{magic} offset points at {S(m)} (the client does not check, but every stock file has the magic there)");
            spans.Add((start, stop, magic));
        }
        spans.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : x.Stop != y.Stop ? x.Stop.CompareTo(y.Stop) : string.CompareOrdinal(x.Magic, y.Magic));
        for (int k = 0; k + 1 < spans.Count; k++)
            if (spans[k + 1].Start < spans[k].Stop)
                add(ERROR, "ADT-OVERLAP", where, $"{spans[k].Magic} and {spans[k + 1].Magic} overlap (0x{X(spans[k + 1].Start)} < 0x{X(spans[k].Stop)})");
        // fixed sizes recorded in the sub-chunk headers (informative)
        foreach (var (magic, field, expect) in new[] { ("MCVT", "ofsHeight", MapBytes.McvtSize), ("MCNR", "ofsNormal", MapBytes.McnrSize) })
        {
            long hp = c.Offset + c[field];
            if (hp + 8 <= end && MapBytes.MagicAt(b, hp) == magic && MapBytes.U32(b, hp + 4) != expect)
                add(WARNING, "ADT-SUBSIZE", where, $"{magic} size {MapBytes.U32(b, hp + 4)} (stock and 1.12: {expect})");
        }
        // MCRF references
        long rf = c.Offset + c["ofsRefs"] + 8;
        if (rf + 4 * nrefs <= end)
        {
            for (long k = 0; k < nrefs; k++)
            {
                uint rref = MapBytes.U32(b, rf + 4 * k);
                if (k < c["nDoodadRefs"])
                {
                    if (rref >= a.Mddf.Count) add(ERROR, "ADT-MCRF", where, $"doodad ref {k} -> MDDF[{rref}] out of range ({a.Mddf.Count} entries)");
                }
                else if (rref >= a.Modf.Count) add(ERROR, "ADT-MCRF", where, $"WMO ref {k} -> MODF[{rref}] out of range ({a.Modf.Count} entries)");
            }
        }
        // MCLQ
        uint sl = c["sizeLiquid"];
        if (nliq > 0)
        {
            if (sl != 8 + MapBytes.MclqLayerSize * nliq) add(WARNING, "ADT-MCLQ", where, $"sizeLiquid {sl}, expected 8 + 804 x {nliq} layers");
            long bas = c.Offset + c["ofsLiquid"] + 8;
            for (int q = 0; q < nliq; q++)
            {
                long p = bas + q * MapBytes.MclqLayerSize + MapBytes.MclqTilesOfs;
                if (p + 64 > end) break;
                int junk = 0;
                for (int t = 0; t < 64; t++) if ((b[p + t] & 0x30) != 0) junk++;
                if (junk > 0)
                    add(WARNING, "ADT-MCLQ-BITS", where, $"liquid layer {q}: {junk} tile bytes have bits 0x10/0x20 set (stock: never; the server mapextractor treats tile != 0x0F as water -> phantom server water)");
            }
        }
        else if (sl != 0 && sl != 8) add(WARNING, "ADT-MCLQ", where, $"sizeLiquid {sl} but no liquid flag (0x4/0x8/0x10/0x20) is set");
        // MCSE: the header MCSE size, when the magic is there, must agree with 52-byte records
        uint nSe = c["nSndEmitters"];
        long seHp = c.Offset + c["ofsSndEmitters"];
        if (nSe != 0 && seHp + 8 <= end && MapBytes.MagicAt(b, seHp) == "MCSE")
        {
            long sz = MapBytes.U32(b, seHp + 4);
            if (sz != (long)MapBytes.McseSize * nSe)
                add(ERROR, "ADT-MCSE", where, $"MCSE holds {sz} bytes for {nSe} emitters; 1.12 records are 52 bytes ({(sz == 28L * nSe ? "28-byte WotLK records" : "size mismatch")})");
        }
        // sub-chunks in the MCNK the client never addresses (WotLK leftovers such as MCCV): coverage of the addressed
        // sub-chunks (header + max(data the client uses, declared size when sane)); MCNR is followed by 13 pad bytes
        long pos0 = c.Offset + 8 + MapBytes.McnkHeaderSize;
        var cover = new List<(long S, long E)>();
        foreach (var (magic, field) in FSubs)
        {
            if (c[field] == 0) continue;
            long hp = c.Offset + c[field];
            if (hp < pos0 || hp + 8 > end) continue;
            long declared = MapBytes.MagicAt(b, hp) == magic ? MapBytes.U32(b, hp + 4) : 0;
            if (declared > end - hp - 8) declared = 0;
            long size = Math.Max(need.GetValueOrDefault(magic), declared) + (magic == "MCNR" ? MapBytes.McnrPad : 0);
            cover.Add((hp, Math.Min(end, hp + 8 + size)));
        }
        cover.Sort();
        var gaps = new List<(long S, long E)>();
        long cur = pos0;
        foreach (var (s, e) in cover)
        {
            if (s > cur) gaps.Add((cur, s));
            cur = Math.Max(cur, e);
        }
        if (cur < end) gaps.Add((cur, end));
        foreach (var (s, e) in gaps)
        {
            var m = e - s >= 8 ? MapBytes.MagicAt(b, s) : null;
            if (m != null && MapBytes.WotlkSubChunks.Contains(m)) add(WARNING, "ADT-WOTLK", where, $"{m} sub-chunk (WotLK) in the MCNK at 0x{X(s)}; 1.12 never reads it");
            else if (e - s >= 4) add(INFO, "ADT-UNREAD", where, $"{e - s} bytes at 0x{X(s)} are not addressed by any MCNK offset ({m ?? "data"})");
        }
    }

    // ------------------------------------------------------------------------------------------- dispatch
    public static string? KindOf(string name)
    {
        var low = name.ToLowerInvariant();
        foreach (var ext in new[] { ".adt", ".wdt", ".wdl" })
            if (low.EndsWith(ext, StringComparison.Ordinal)) return ext[1..];
        return null;
    }

    public static MapReport ValidateBytes(byte[] b, string name, MapReport? report = null, IReadOnlyCollection<(int X, int Y)>? wdtTiles = null) => KindOf(name) switch
    {
        "adt" => ValidateAdt(b, name, report),
        "wdt" => ValidateWdt(b, name, report),
        "wdl" => ValidateWdl(b, name, report, wdtTiles),
        _ => throw new ArgumentException("not a map file: " + name),
    };

    /// <summary>Validate one map's files together. <paramref name="items"/>: (name, loader). Adds the cross-file checks:
    /// WDT MAIN tiles &lt;-&gt; ADT files present, WDT &lt;-&gt; WDL tile sets.</summary>
    public static MapReport ValidateMapSet(IEnumerable<(string Name, Func<byte[]> Load)> itemsIn, MapReport? report = null)
    {
        var r = report ?? new MapReport();
        var items = itemsIn.ToList();
        var wdt = items.Where(t => KindOf(t.Name) == "wdt").ToList();
        List<(int X, int Y)>? wdtTiles = null;
        string? wdtName = null;
        if (wdt.Count > 0)
        {
            wdtName = wdt[0].Name;
            var b = wdt[0].Load();
            ValidateWdt(b, wdtName, r);
            var w = new WdtFile(b);
            wdtTiles = w.IsGlobalWmo ? new List<(int, int)>() : w.Tiles();
        }
        var adts = new Dictionary<(int, int), string>();
        foreach (var (n, ld) in items)
        {
            var k = KindOf(n);
            if (k == "adt")
            {
                ValidateAdt(ld(), n, r);
                if (TileXY(n) is { } xy) adts[xy] = n;
            }
            else if (k == "wdl") ValidateWdl(ld(), n, r, wdtTiles);
        }
        if (wdtTiles != null && adts.Count > 0)
        {
            var missing = wdtTiles.Except(adts.Keys).OrderBy(p => p).ToList();
            var extra = adts.Keys.Except(wdtTiles).OrderBy(p => p).ToList();
            if (missing.Count > 0)
                r.Add(INFO, "MAP-TILES", wdtName!, "", $"{missing.Count} tiles flagged in MAIN have no ADT in this set, e.g. {Py.Repr(Tuples(missing.Take(3)))} " +
                                                        "(fine if they come from the client archives; the client fails on a flagged tile with no file)");
            if (extra.Count > 0)
                r.Add(WARNING, "MAP-TILES", wdtName!, "", $"{extra.Count} ADTs are not flagged in the WDT MAIN table, so the client never loads them, e.g. {Py.Repr(extra.Take(3).Select(t => adts[t]).ToList())}");
        }
        return r;
    }

    /// <summary>(x, y) of a tile file name &lt;map&gt;_&lt;x&gt;_&lt;y&gt;.adt, or null.</summary>
    public static (int X, int Y)? TileXY(string name)
    {
        var bas = Path.GetFileName(name.Replace('\\', '/'));
        if (bas.Length < 4) return null;
        var parts = bas[..^4].Split('_');
        if (parts.Length < 2) return null;
        if (int.TryParse(parts[^2].Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(parts[^1].Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int y))
            return (x, y);
        return null;
    }

    // ------------------------------------------------------------------------------------------- plain words
    /// <summary>A finding in words a modder understands: what it means for the game and what to do.</summary>
    public static string Explain(MapFinding f)
    {
        string what = f.Code switch
        {
            "ADT-SIZE" or "ADT-TRUNC" or "WDT-TRUNC" or "WDL-SIZE" => "The file is cut short: the game would read past its end. Save it again in Noggit.",
            "ADT-MVER" or "ADT-MHDR" or "ADT-MHDR-OFS" or "WDL-MVER" or "WDT-POS" or "WDT-SIZE" => "The file's table of contents is wrong: the game would read the wrong data. Save it again in Noggit.",
            "ADT-MCIN" or "ADT-MCNK" or "ADT-SUBOFS" or "ADT-OVERLAP" => "One of the 256 terrain squares (chunks) of the tile points at the wrong data: the game would show garbage or crash there.",
            "ADT-MCLY" => "A terrain square has too many texture layers or names a texture that is not in the tile's list. The game shows at most 4 layers per square.",
            "ADT-ALPHA" or "ADT-BIGALPHA" or "WDT-BIGALPHA" => "The texture blending is saved in a later game version's format (8-bit or compressed alpha). The 1.12 game only reads 4-bit alpha: the ground textures would look garbled. In Noggit, convert the map's alpha to 4-bit (it is the default for new vanilla maps).",
            "ADT-MCSE" => "Ambient sound emitters are in a later game version's format (28-byte records). The 1.12 game reads 52-byte records: sounds would be wrong.",
            "ADT-MCLQ" or "ADT-MCLQ-BITS" => "The water data has stray bits. The game shows the water fine, but the server's map extractor would see water where there is none.",
            "ADT-MMID" or "ADT-MWID" or "ADT-MDDF" or "ADT-MODF" or "ADT-MCRF" => "A placed object (doodad or building) refers to a model name or placement that does not exist in the tile.",
            "ADT-UID" => "Two placed objects share one unique id: the game shows only one of them.",
            "ADT-WOTLK" or "WDL-WOTLK" or "WDT-MCCV" => "The file contains data from a later game version (WotLK). The 1.12 game ignores it or reads it wrongly.",
            "WDL-POS" or "WDL-MARE" => "The far-distance terrain (WDL) is laid out the way later game versions expect: the horizon would be wrong. Our fixed Noggit writes it correctly.",
            "WDT-MAIN" => "The map's tile list has flags the game does not expect: a tile may never load.",
            "WDT-GLOBALWMO" => "This map is a single building (global WMO) but the building's name is missing.",
            "MAP-TILES" or "WDL-TILES" => "The map's tile list and the tiles found do not match.",
            "ADT-ORDER" => "The chunks are in an unusual order. The game finds them anyway, so this alone is harmless.",
            "ADT-UNREAD" or "WDT-TAIL" or "WDL-MAHO" or "ADT-MCNK-TAIL" or "ADT-MDDF-FLAGS" => "Harmless: the file has extra bytes the game never reads.",
            _ => f.Severity == MapSeverity.Error ? "The game would read this file wrongly." : f.Severity == MapSeverity.Warning ? "This differs from every original 1.12 file; check it." : "Harmless difference from the original files.",
        };
        var where = f.Where.Length > 0 ? $" ({f.Where})" : "";
        return $"{what}{where} Details: {f.Message}";
    }
}
