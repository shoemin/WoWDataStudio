using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.Maps;

/// <summary>What changed, counted for the plain-language summary ("heights changed in 37 chunks, texture layer added,
/// 4 doodads moved").</summary>
public sealed class MapDiffStats
{
    public int HeightChunks, NormalChunks, LayerChunks, LayersAdded, LayersRemoved, LayersReplaced, AlphaChunks, ShadowChunks;
    public int HoleChunks, AreaChunks, LiquidChunks, EmitterChunks, OtherHeaderChunks;
    public double MaxHeightChange;
    public int DoodadsAdded, DoodadsRemoved, DoodadsMoved, DoodadsRotated, DoodadsScaled, DoodadsOther;
    public int WmosAdded, WmosRemoved, WmosMoved, WmosRotated, WmosOther;
    public int TexturesAdded, TexturesRemoved;
    public int WdlTiles, WdtTilesAdded, WdtTilesRemoved;
    public bool WdtFlags, Structure;
}

/// <summary>A semantic diff of two map files (port of wowdata/maps/diff.py): content lines and layout-only lines.</summary>
public sealed class MapDiffResult
{
    public string AName { get; }
    public string BName { get; }
    public List<(string Section, string Message)> Lines { get; } = new();
    public List<(string Section, string Message)> Layout { get; } = new();
    public bool IdenticalBytes { get; set; }
    public MapDiffStats Stats { get; } = new();

    public MapDiffResult(string a, string b)
    {
        AName = a;
        BName = b;
    }

    public void Add(string section, string msg) => Lines.Add((section, msg));
    public bool SameContent => Lines.Count == 0;

    public string Summary()
    {
        if (IdenticalBytes) return "byte-identical";
        if (Lines.Count == 0) return Layout.Count > 0 ? $"same content (layout differs: {Layout.Count} item(s))" : "same content";
        return $"{Lines.Count} content difference(s), {Layout.Count} layout difference(s)";
    }

    /// <summary>The changes in plain words, e.g. "heights changed in 37 chunks (up to 12.5 yards), a texture layer added
    /// in 3 chunks, 4 doodads moved". "No changes" when the content is the same.</summary>
    public string PlainSummary()
    {
        if (SameContent) return "No changes";
        var s = Stats;
        var parts = new List<string>();
        static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
        if (s.HeightChunks > 0) parts.Add($"heights changed in {N(s.HeightChunks, "chunk", "chunks")} (up to {s.MaxHeightChange:0.##} yards)");
        if (s.LayersAdded > 0) parts.Add(s.LayersAdded == 1 ? "a texture layer added" : $"{s.LayersAdded} texture layers added");
        if (s.LayersRemoved > 0) parts.Add(s.LayersRemoved == 1 ? "a texture layer removed" : $"{s.LayersRemoved} texture layers removed");
        if (s.LayersReplaced > 0) parts.Add($"textures replaced in {N(s.LayersReplaced, "chunk", "chunks")}");
        if (s.AlphaChunks > 0) parts.Add($"texture painting changed in {N(s.AlphaChunks, "chunk", "chunks")}");
        if (s.HoleChunks > 0) parts.Add($"holes changed in {N(s.HoleChunks, "chunk", "chunks")}");
        if (s.LiquidChunks > 0) parts.Add($"water changed in {N(s.LiquidChunks, "chunk", "chunks")}");
        if (s.AreaChunks > 0) parts.Add($"zone (area id) changed in {N(s.AreaChunks, "chunk", "chunks")}");
        if (s.ShadowChunks > 0) parts.Add($"shadows changed in {N(s.ShadowChunks, "chunk", "chunks")}");
        if (s.DoodadsAdded > 0) parts.Add($"{N(s.DoodadsAdded, "doodad", "doodads")} added");
        if (s.DoodadsRemoved > 0) parts.Add($"{N(s.DoodadsRemoved, "doodad", "doodads")} removed");
        if (s.DoodadsMoved > 0) parts.Add($"{N(s.DoodadsMoved, "doodad", "doodads")} moved");
        if (s.DoodadsRotated > 0) parts.Add($"{N(s.DoodadsRotated, "doodad", "doodads")} turned");
        if (s.DoodadsScaled > 0) parts.Add($"{N(s.DoodadsScaled, "doodad", "doodads")} resized");
        if (s.WmosAdded > 0) parts.Add($"{N(s.WmosAdded, "building", "buildings")} (WMO) added");
        if (s.WmosRemoved > 0) parts.Add($"{N(s.WmosRemoved, "building", "buildings")} (WMO) removed");
        if (s.WmosMoved > 0) parts.Add($"{N(s.WmosMoved, "building", "buildings")} moved");
        if (s.WmosRotated > 0) parts.Add($"{N(s.WmosRotated, "building", "buildings")} turned");
        if (s.EmitterChunks > 0) parts.Add($"sound emitters changed in {N(s.EmitterChunks, "chunk", "chunks")}");
        if (s.TexturesAdded > 0) parts.Add($"{N(s.TexturesAdded, "texture", "textures")} added to the tile's list");
        if (s.WdlTiles > 0) parts.Add($"far-distance heights changed for {N(s.WdlTiles, "tile", "tiles")}");
        if (s.WdtTilesAdded > 0) parts.Add($"{N(s.WdtTilesAdded, "tile", "tiles")} added to the map");
        if (s.WdtTilesRemoved > 0) parts.Add($"{N(s.WdtTilesRemoved, "tile", "tiles")} removed from the map");
        if (s.WdtFlags) parts.Add("map flags changed");
        if (parts.Count == 0) parts.Add(Lines.Count == 1 ? "1 other difference" : $"{Lines.Count} other differences");
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

/// <summary>
/// Semantic, chunk-level diff of two 1.12 map files (ADT, WDT or WDL), a port of wowdata/maps/diff.py. It compares what
/// the client reads, not raw bytes: names are resolved through their index tables, placements are compared by record,
/// terrain per MCNK and per sub-chunk. Offsets and sizes that only move because something else changed are layout.
/// The messages are word for word those of the Python (checked by MapTests).
/// </summary>
public static class MapDiff
{
    private static string S(object? v) => Py.Str(v);

    private static void CmpSeq<T>(MapDiffResult d, string section, IList<T> a, IList<T> b, string label, Func<T, T, bool> eq, Func<T, string> str, int limit = 6)
    {
        if (a.Count == b.Count && a.Zip(b).All(p => eq(p.First, p.Second))) return;
        if (a.Count != b.Count) d.Add(section, $"{label}: {a.Count} -> {b.Count} entries");
        int shown = 0;
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (eq(a[i], b[i])) continue;
            if (shown < limit) d.Add(section, $"{label}[{i}]: {str(a[i])} -> {str(b[i])}");
            shown++;
        }
        if (shown > limit) d.Add(section, $"{label}: {shown - limit} more changed entries");
    }

    private static (int N, double Max) FloatsDelta(IList<double> a, IList<double> b)
    {
        int n = 0;
        double mx = 0;
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            double x = a[i], y = b[i];
            if (x != y && !(double.IsNaN(x) && double.IsNaN(y)))
            {
                n++;
                double dd = Math.Abs(x - y);
                if (dd > mx) mx = dd;
            }
        }
        return (n, mx);
    }

    // --------------------------------------------------------------------------------------------- ADT
    /// <summary>A placement with its resolved name (diff.py's dict(r, name=...)); compared field by field like Python.</summary>
    private sealed class Placement
    {
        public uint NameId;
        public readonly List<KeyValuePair<string, object>> Fields = new();   // in Python's key order, without nameId
        public uint Uid => (uint)Fields[0].Value;
        public string Name => (string)Fields[^1].Value;
        public object? Get(string k) => Fields.FirstOrDefault(f => f.Key == k).Value;
    }

    private static bool ValueEq(object? x, object? y) => (x, y) switch
    {
        (PyTuple a, PyTuple b) => a.Items.Length == b.Items.Length && a.Items.Zip(b.Items).All(p => ValueEq(p.First, p.Second)),
        (double a, double b) => a == b,
        (null, null) => true,
        (null, _) or (_, null) => false,
        _ => Equals(x, y),
    };

    private static bool PlacementEq(Placement a, Placement b) => a.Fields.Count == b.Fields.Count && a.Fields.Zip(b.Fields).All(p => p.First.Key == p.Second.Key && ValueEq(p.First.Value, p.Second.Value));

    private static PyDict AsDict(Placement p)
    {
        var d = new PyDict();
        foreach (var kv in p.Fields) d.Add(kv.Key, kv.Value);
        return d;
    }

    private static PyTuple T3(float a, float b, float c) => new((double)a, (double)b, (double)c);

    /// <summary>The client asks for .mdx/.mdl and loads .m2 (M2Cache_GetShared @00706a50), case-insensitively.</summary>
    public static string NormModel(string name)
    {
        var n = name.ToUpperInvariant().Replace('/', '\\');
        if (n.EndsWith(".MDX", StringComparison.Ordinal) || n.EndsWith(".MDL", StringComparison.Ordinal)) n = n[..^4] + ".M2";
        return n;
    }

    private static string StrAt(List<KeyValuePair<uint, string>> table, uint ofs, string fallback)
    {
        foreach (var kv in table) if (kv.Key == ofs) return kv.Value;
        return fallback;
    }

    private static List<Placement> ResolvedDoodads(AdtFile adt)
    {
        var outList = new List<Placement>();
        foreach (var r in adt.Mddf)
        {
            var name = r.NameId < adt.Mmid.Length ? StrAt(adt.Mmdx, adt.Mmid[r.NameId], "?") : "?";
            var p = new Placement { NameId = r.NameId };
            p.Fields.Add(new("uid", r.Uid));
            p.Fields.Add(new("pos", T3(r.X, r.Y, r.Z)));
            p.Fields.Add(new("rot", T3(r.RotX, r.RotY, r.RotZ)));
            p.Fields.Add(new("scale", (uint)r.Scale));
            p.Fields.Add(new("flags", (uint)r.Flags));
            p.Fields.Add(new("name", NormModel(name)));
            outList.Add(p);
        }
        return outList;
    }

    private static List<Placement> ResolvedWmos(AdtFile adt)
    {
        var outList = new List<Placement>();
        foreach (var r in adt.Modf)
        {
            var name = r.NameId < adt.Mwid.Length ? StrAt(adt.Mwmo, adt.Mwid[r.NameId], "?") : "?";
            var p = new Placement { NameId = r.NameId };
            p.Fields.Add(new("uid", r.Uid));
            p.Fields.Add(new("pos", T3(r.X, r.Y, r.Z)));
            p.Fields.Add(new("rot", T3(r.RotX, r.RotY, r.RotZ)));
            p.Fields.Add(new("extents", new PyTuple(r.Extents.Select(e => (object?)(double)e).ToArray())));
            p.Fields.Add(new("flags", (uint)r.Flags));
            p.Fields.Add(new("doodadSet", (uint)r.DoodadSet));
            p.Fields.Add(new("nameSet", (uint)r.NameSet));
            p.Fields.Add(new("pad", (uint)r.Pad));
            p.Fields.Add(new("name", name.ToUpperInvariant()));
            outList.Add(p);
        }
        return outList;
    }

    private sealed class McnkParts
    {
        public List<KeyValuePair<string, object>> Header = new();
        public double[] Mcvt = Array.Empty<double>();
        public byte[] Mcnr = Array.Empty<byte>();
        public List<(string Tex, uint Flags, uint Effect)> Mcly = new();
        public List<byte[]> Mcal = new();
        public byte[] Mcsh = Array.Empty<byte>();
        public List<long> RefsD = new(), RefsW = new();
        public List<byte[]> Mclq = new();
        public byte[] Mcse = Array.Empty<byte>();
    }

    private static McnkParts Parts(byte[] b, AdtFile adt, McnkRecord c)
    {
        var p = new McnkParts();
        uint fl = c.Flags;
        long o = c.Offset;
        foreach (var (name, _) in MapBytes.McnkFields)
        {
            if (name.StartsWith("ofs", StringComparison.Ordinal) || name.StartsWith("size", StringComparison.Ordinal)) continue;
            object v = MapBytes.FloatFields.Contains(name) ? (double)c.Float(name) : c[name];
            p.Header.Add(new(name, v));
        }
        p.Header.Add(new("lowQualityMap", Convert.ToHexString(c.LowQualityMap).ToLowerInvariant()));
        p.Header.Add(new("noEffectDoodad", c.NoEffectDoodad));
        long q = o + c["ofsHeight"] + 8;
        if (q + MapBytes.McvtSize <= b.Length)
        {
            p.Mcvt = new double[145];
            for (int i = 0; i < 145; i++) p.Mcvt[i] = MapBytes.F32(b, q + i * 4);
        }
        q = o + c["ofsNormal"] + 8;
        p.Mcnr = MapBytes.Slice(b, q, q + MapBytes.McnrSize);
        q = o + c["ofsLayer"] + 8;
        long mcal = o + c["ofsAlpha"] + 8;
        for (long L = 0; L < c["nLayers"]; L++)
        {
            if (q + 16 * (L + 1) > b.Length) break;
            uint tid = MapBytes.U32(b, q + 16 * L), lf = MapBytes.U32(b, q + 16 * L + 4), oa = MapBytes.U32(b, q + 16 * L + 8), eff = MapBytes.U32(b, q + 16 * L + 12);
            var tex = tid < adt.Textures.Count ? adt.Textures[(int)tid].ToUpperInvariant() : $"#{tid}";
            p.Mcly.Add((tex, lf, eff));
            if ((lf & 0x100) != 0) p.Mcal.Add(MapBytes.Slice(b, mcal + oa, mcal + oa + MapBytes.AlphaLayerSize));
        }
        q = o + c["ofsShadow"] + 8;
        p.Mcsh = (fl & 1) != 0 ? MapBytes.Slice(b, q, q + MapBytes.McshSize) : Array.Empty<byte>();
        long nd = c["nDoodadRefs"], n = nd + c["nMapObjRefs"];
        q = o + c["ofsRefs"] + 8;
        if (q + 4 * n <= b.Length)
        {
            for (long k = 0; k < n; k++)
            {
                uint r = MapBytes.U32(b, q + 4 * k);
                if (k < nd) p.RefsD.Add(r < adt.Mddf.Count ? adt.Mddf[(int)r].Uid : -1);
                else p.RefsW.Add(r < adt.Modf.Count ? adt.Modf[(int)r].Uid : -1);
            }
        }
        p.RefsD.Sort();
        p.RefsW.Sort();
        int nliq = MapBytes.McnkLiquidFlags.Count(bit => (fl & bit) != 0);
        q = o + c["ofsLiquid"] + 8;
        for (int k = 0; k < nliq; k++) p.Mclq.Add(MapBytes.Slice(b, q + k * MapBytes.MclqLayerSize, q + (k + 1) * MapBytes.MclqLayerSize));
        q = o + c["ofsSndEmitters"] + 8;
        p.Mcse = MapBytes.Slice(b, q, q + (long)MapBytes.McseSize * c["nSndEmitters"]);
        return p;
    }

    private static (int N, int Max) NibbleDiff(byte[] a, byte[] b)
    {
        int n = 0, mx = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] == b[i]) continue;
            foreach (int s in new[] { 0, 4 })
            {
                int dx = Math.Abs(((a[i] >> s) & 0xF) - ((b[i] >> s) & 0xF));
                if (dx != 0)
                {
                    n++;
                    mx = Math.Max(mx, dx);
                }
            }
        }
        return (n, mx);
    }

    private static string F(object v) => v is double d ? Py.F4(d) : S(v);

    public static MapDiffResult DiffAdt(byte[] a, byte[] b, string aName = "a", string bName = "b")
    {
        var d = new MapDiffResult(aName, bName);
        if (a.AsSpan().SequenceEqual(b))
        {
            d.IdenticalBytes = true;
            return d;
        }
        var A = new AdtFile(a);
        var B = new AdtFile(b);
        var st = d.Stats;
        var ta = A.Top.Where(t => t.Magic != "MCNK").Select(t => t.Magic).ToList();
        var tb = B.Top.Where(t => t.Magic != "MCNK").Select(t => t.Magic).ToList();
        if (!ta.SequenceEqual(tb) || A.Top.Count(t => t.Magic == "MCNK") != B.Top.Count(t => t.Magic == "MCNK"))
        {
            d.Add("chunks", $"top-level chunks {Py.Repr(ta)} -> {Py.Repr(tb)}");
            st.Structure = true;
        }
        var chunkFields = MapBytes.MhdrChunks.Select(x => x.Field).ToHashSet();
        foreach (var k in MapBytes.MhdrFields)
        {
            bool ha = A.Mhdr.TryGetValue(k, out var va), hb = B.Mhdr.TryGetValue(k, out var vb);
            if (ha != hb || va != vb)
            {
                var line = ("MHDR", $"{k} 0x{Py.Hex(va)} -> 0x{Py.Hex(vb)}");
                if (k.StartsWith("ofs", StringComparison.Ordinal) && chunkFields.Contains(k)) d.Layout.Add(line);
                else d.Lines.Add(line);
            }
        }
        var texA = A.Textures.Select(t => t.ToUpperInvariant()).ToList();
        var texB = B.Textures.Select(t => t.ToUpperInvariant()).ToList();
        var sa = texA.OrderBy(t => t, StringComparer.Ordinal).ToList();
        var sb = texB.OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (sa.SequenceEqual(sb) && !texA.SequenceEqual(texB))
            d.Layout.Add(("MTEX", $"same {texA.Count} textures in a different order (MCLY ids are resolved to names)"));
        else
        {
            CmpSeq(d, "MTEX", texA, texB, "texture", (x, y) => x == y, x => x);
            st.TexturesAdded = texB.Except(texA).Count();
            st.TexturesRemoved = texA.Except(texB).Count();
        }
        var da = ResolvedDoodads(A);
        var db = ResolvedDoodads(B);
        var wa = ResolvedWmos(A);
        var wb = ResolvedWmos(B);
        if (!da.Select(x => x.NameId).SequenceEqual(db.Select(x => x.NameId)) || !A.Mmid.SequenceEqual(B.Mmid) || !A.Block("MMDX").AsSpan().SequenceEqual(B.Block("MMDX")))
            d.Layout.Add(("MMDX/MMID", "name tables differ in layout"));
        if (!A.Block("MWMO").AsSpan().SequenceEqual(B.Block("MWMO")) || !A.Mwid.SequenceEqual(B.Mwid))
            d.Layout.Add(("MWMO/MWID", "name tables differ in layout"));
        DiffPlacements(d, "MDDF", da, db);
        DiffPlacements(d, "MODF", wa, wb);
        // MCNK
        for (int i = 0; i < 256; i++)
        {
            var ca = i < A.Mcnk.Count ? A.Mcnk[i] : null;
            var cb = i < B.Mcnk.Count ? B.Mcnk[i] : null;
            string sec = $"MCNK {i} ({i % 16},{i / 16})";
            if (ca == null || cb == null || !ca.HasHeader || !cb.HasHeader)
            {
                if ((ca == null) != (cb == null))
                {
                    d.Add(sec, "present in only one file");
                    st.Structure = true;
                }
                continue;
            }
            if (ca.Offset != cb.Offset || ca.Size != cb.Size)
                d.Layout.Add((sec, $"offset/size 0x{Py.Hex(ca.Offset)}/{ca.Size} -> 0x{Py.Hex(cb.Offset)}/{cb.Size}"));
            var pa = Parts(a, A, ca);
            var pb = Parts(b, B, cb);
            bool otherHeader = false;
            foreach (var (k, v) in pa.Header)
            {
                var w = pb.Header.FirstOrDefault(h => h.Key == k).Value;
                if (!ValueEq(v, w))
                {
                    d.Add(sec, $"header {k}: {F(v)} -> {F(w!)}");
                    if (k == "holes") st.HoleChunks++;
                    else if (k == "areaId") st.AreaChunks++;
                    else otherHeader = true;
                }
            }
            if (otherHeader) st.OtherHeaderChunks++;
            var (n, mx) = FloatsDelta(pa.Mcvt, pb.Mcvt);
            if (n > 0)
            {
                d.Add(sec, $"MCVT: {n} of 145 heights changed (max {Py.Fixed(mx, 4)})");
                st.HeightChunks++;
                st.MaxHeightChange = Math.Max(st.MaxHeightChange, mx);
            }
            if (!pa.Mcnr.AsSpan().SequenceEqual(pb.Mcnr))
            {
                d.Add(sec, $"MCNR: {pa.Mcnr.Zip(pb.Mcnr).Count(p => p.First != p.Second)} of 435 normal bytes changed");
                st.NormalChunks++;
            }
            if (!pa.Mcly.SequenceEqual(pb.Mcly))
            {
                d.Add(sec, $"MCLY: {Py.Repr(pa.Mcly.Select(l => new PyTuple(l.Tex, l.Flags, l.Effect)).ToList())} -> {Py.Repr(pb.Mcly.Select(l => new PyTuple(l.Tex, l.Flags, l.Effect)).ToList())}");
                st.LayerChunks++;
                if (pb.Mcly.Count > pa.Mcly.Count) st.LayersAdded += pb.Mcly.Count - pa.Mcly.Count;
                else if (pb.Mcly.Count < pa.Mcly.Count) st.LayersRemoved += pa.Mcly.Count - pb.Mcly.Count;
                else if (!pa.Mcly.Select(l => l.Tex).SequenceEqual(pb.Mcly.Select(l => l.Tex))) st.LayersReplaced++;
            }
            bool alphaChanged = false;
            for (int L = 0; L < Math.Min(pa.Mcal.Count, pb.Mcal.Count); L++)
                if (!pa.Mcal[L].AsSpan().SequenceEqual(pb.Mcal[L]))
                {
                    var (nn, mm) = NibbleDiff(pa.Mcal[L], pb.Mcal[L]);
                    d.Add(sec, $"MCAL alpha layer {L + 1}: {nn} of 4096 texels changed (max {mm} of 15)");
                    alphaChanged = true;
                }
            if (pa.Mcal.Count != pb.Mcal.Count)
            {
                d.Add(sec, $"MCAL: {pa.Mcal.Count} -> {pb.Mcal.Count} alpha layers");
                alphaChanged = true;
            }
            if (alphaChanged) st.AlphaChunks++;
            if (!pa.Mcsh.AsSpan().SequenceEqual(pb.Mcsh))
            {
                int bits = pa.Mcsh.Zip(pb.Mcsh).Sum(p => BitOperations.PopCount((uint)(p.First ^ p.Second)));
                d.Add(sec, $"MCSH: {bits} shadow bits changed{(pa.Mcsh.Length == pb.Mcsh.Length ? "" : " (present in only one)")}");
                st.ShadowChunks++;
            }
            if (!pa.RefsD.SequenceEqual(pb.RefsD) || !pa.RefsW.SequenceEqual(pb.RefsW))
                d.Add(sec, $"MCRF: doodad uids {Py.Repr(pa.RefsD)} -> {Py.Repr(pb.RefsD)}, WMO uids {Py.Repr(pa.RefsW)} -> {Py.Repr(pb.RefsW)}");
            if (pa.Mclq.Count != pb.Mclq.Count || pa.Mclq.Zip(pb.Mclq).Any(p => !p.First.AsSpan().SequenceEqual(p.Second)))
            {
                if (pa.Mclq.Count != pb.Mclq.Count) d.Add(sec, $"MCLQ: {pa.Mclq.Count} -> {pb.Mclq.Count} liquid layers");
                for (int q = 0; q < Math.Min(pa.Mclq.Count, pb.Mclq.Count); q++)
                    if (!pa.Mclq[q].AsSpan().SequenceEqual(pb.Mclq[q])) d.Add(sec, $"MCLQ layer {q}: {LiquidDelta(pa.Mclq[q], pb.Mclq[q])}");
                st.LiquidChunks++;
            }
            if (!pa.Mcse.AsSpan().SequenceEqual(pb.Mcse))
            {
                d.Add(sec, $"MCSE: sound emitters differ ({pa.Mcse.Length} -> {pb.Mcse.Length} bytes)");
                st.EmitterChunks++;
            }
        }
        return d;
    }

    private static string LiquidDelta(byte[] x, byte[] y)
    {
        var outList = new List<string>();
        double Fl(byte[] z, int p) => p + 4 <= z.Length ? MapBytes.F32(z, p) : double.NaN;
        var mnx = new PyTuple(Fl(x, 0), Fl(x, 4));
        var mny = new PyTuple(Fl(y, 0), Fl(y, 4));
        if (!ValueEq(mnx, mny)) outList.Add($"min/max {Py.Repr(mnx)} -> {Py.Repr(mny)}");
        var hx = Enumerable.Range(0, 81).Select(i => Fl(x, 8 + i * 8 + 4)).ToList();
        var hy = Enumerable.Range(0, 81).Select(i => Fl(y, 8 + i * 8 + 4)).ToList();
        var (n, mx) = FloatsDelta(hx, hy);
        if (n > 0) outList.Add($"{n} heights (max {Py.Fixed(mx, 4)})");
        n = 0;
        for (int i = 0; i < 81; i++)
            if (!MapBytes.Slice(x, 8 + i * 8, 8 + i * 8 + 4).AsSpan().SequenceEqual(MapBytes.Slice(y, 8 + i * 8, 8 + i * 8 + 4))) n++;
        if (n > 0) outList.Add($"{n} vertex depth/flow records");
        int t0 = MapBytes.MclqTilesOfs;
        var tx = MapBytes.Slice(x, t0, t0 + 64);
        var ty = MapBytes.Slice(y, t0, t0 + 64);
        n = tx.Zip(ty).Count(p => p.First != p.Second);
        if (n > 0) outList.Add($"{n} tile flag bytes");
        if (!MapBytes.Slice(x, t0 + 64, x.Length).AsSpan().SequenceEqual(MapBytes.Slice(y, t0 + 64, y.Length))) outList.Add("flow data (n_flowvs/flowvs) differs");
        return outList.Count > 0 ? string.Join(", ", outList) : "differs";
    }

    private static void DiffPlacements(MapDiffResult d, string section, List<Placement> a, List<Placement> b)
    {
        if (a.Count == b.Count && a.Zip(b).All(p => PlacementEq(p.First, p.Second))) return;
        bool wmo = section == "MODF";
        var st = d.Stats;
        var ka = new Dictionary<uint, Placement>();
        foreach (var x in a) ka[x.Uid] = x;
        var kb = new Dictionary<uint, Placement>();
        foreach (var x in b) kb[x.Uid] = x;
        if (ka.Count == a.Count && kb.Count == b.Count)
        {
            foreach (var uid in ka.Keys.Except(kb.Keys).OrderBy(u => u))
            {
                d.Add(section, $"removed uid {uid} {ka[uid].Name}");
                if (wmo) st.WmosRemoved++; else st.DoodadsRemoved++;
            }
            foreach (var uid in kb.Keys.Except(ka.Keys).OrderBy(u => u))
            {
                d.Add(section, $"added uid {uid} {kb[uid].Name}");
                if (wmo) st.WmosAdded++; else st.DoodadsAdded++;
            }
            int changed = 0;
            foreach (var uid in ka.Keys.Intersect(kb.Keys).OrderBy(u => u))
            {
                var x = ka[uid];
                var y = kb[uid];
                if (PlacementEq(x, y)) continue;
                changed++;
                var fields = x.Fields.Where(f => !ValueEq(f.Value, y.Get(f.Key))).Select(f => f.Key).ToList();
                if (changed <= 8)
                    d.Add(section, $"uid {uid} {x.Name}: {string.Join(", ", fields.Select(k => $"{k} {S(x.Get(k))} -> {S(y.Get(k))}"))}");
                if (fields.Contains("pos")) { if (wmo) st.WmosMoved++; else st.DoodadsMoved++; }
                else if (fields.Contains("rot")) { if (wmo) st.WmosRotated++; else st.DoodadsRotated++; }
                else if (fields.Contains("scale")) st.DoodadsScaled++;
                else if (wmo) st.WmosOther++; else st.DoodadsOther++;
            }
            if (changed > 8) d.Add(section, $"{changed - 8} more changed placements");
            if (ka.Keys.ToHashSet().SetEquals(kb.Keys) && changed == 0) d.Layout.Add((section, "same placements in a different order"));
        }
        else
        {
            CmpSeq(d, section, a, b, "record", PlacementEq, p => Py.Repr(AsDict(p)));
            if (wmo) st.WmosOther += Math.Max(1, Math.Abs(a.Count - b.Count)); else st.DoodadsOther += Math.Max(1, Math.Abs(a.Count - b.Count));
        }
    }

    // --------------------------------------------------------------------------------------------- WDT / WDL
    public static MapDiffResult DiffWdt(byte[] a, byte[] b, string aName = "a", string bName = "b")
    {
        var d = new MapDiffResult(aName, bName);
        if (a.AsSpan().SequenceEqual(b))
        {
            d.IdenticalBytes = true;
            return d;
        }
        var A = new WdtFile(a);
        var B = new WdtFile(b);
        var ca = A.Top.Select(t => t.Magic).ToList();
        var cb = B.Top.Select(t => t.Magic).ToList();
        if (!ca.SequenceEqual(cb)) d.Add("chunks", $"{Py.Repr(ca)} -> {Py.Repr(cb)}");
        if (!A.Mphd.AsSpan().SequenceEqual(B.Mphd))
        {
            bool rest = MapBytes.Slice(A.Mphd, 4, A.Mphd.Length).AsSpan().SequenceEqual(MapBytes.Slice(B.Mphd, 4, B.Mphd.Length));
            d.Add("MPHD", $"flags 0x{Py.Hex(A.MphdFlags ?? 0)} -> 0x{Py.Hex(B.MphdFlags ?? 0)}{(rest ? "" : " (other MPHD bytes differ)")}");
            d.Stats.WdtFlags = true;
        }
        var ta = A.Tiles().ToHashSet();
        var tb = B.Tiles().ToHashSet();
        if (!ta.SetEquals(tb))
        {
            var removed = ta.Except(tb).OrderBy(p => p).ToList();
            var added = tb.Except(ta).OrderBy(p => p).ToList();
            d.Add("MAIN", $"tiles removed {Py.Repr(removed.Take(10).Select(p => new PyTuple(p.X, p.Y)).ToList())}, added {Py.Repr(added.Take(10).Select(p => new PyTuple(p.X, p.Y)).ToList())}");
            d.Stats.WdtTilesAdded = added.Count;
            d.Stats.WdtTilesRemoved = removed.Count;
        }
        if (!A.Main.SequenceEqual(B.Main) && ta.SetEquals(tb)) d.Add("MAIN", "tile flag/asyncId values differ");
        if (A.IsGlobalWmo || B.IsGlobalWmo)
        {
            if (A.GlobalWmoName != B.GlobalWmoName) d.Add("MWMO", $"{S(A.GlobalWmoName)} -> {S(B.GlobalWmoName)}");
            bool same = (A.GlobalModf == null && B.GlobalModf == null) || (A.GlobalModf != null && B.GlobalModf != null && A.GlobalModf.AsSpan().SequenceEqual(B.GlobalModf));
            if (!same) d.Add("MODF", "global WMO placement differs");
        }
        return d;
    }

    public static MapDiffResult DiffWdl(byte[] a, byte[] b, string aName = "a", string bName = "b")
    {
        var d = new MapDiffResult(aName, bName);
        if (a.AsSpan().SequenceEqual(b))
        {
            d.IdenticalBytes = true;
            return d;
        }
        var A = new WdlFile(a);
        var B = new WdlFile(b);
        var ca = A.Top.Select(t => t.Magic).ToList();
        var cb = B.Top.Select(t => t.Magic).ToList();
        var sa = ca.Where(m => m != "MARE" && m != "MAHO").ToList();
        var sb = cb.Where(m => m != "MARE" && m != "MAHO").ToList();
        if (!sa.SequenceEqual(sb)) d.Add("chunks", $"{Py.Repr(sa)} -> {Py.Repr(sb)}");
        if (ca.Contains("MAHO") != cb.Contains("MAHO")) d.Add("chunks", $"MAHO {(ca.Contains("MAHO") ? "removed" : "added")}");
        if (A.TableMagic != B.TableMagic) d.Add("layout", $"chunk at the offset-table position: {S(A.TableMagic)} -> {S(B.TableMagic)}");
        var ta = A.Tiles().ToHashSet();
        var tb = B.Tiles().ToHashSet();
        if (!ta.SetEquals(tb))
            d.Add("MAOF", $"tiles removed {Py.Repr(ta.Except(tb).OrderBy(p => p).Take(10).Select(p => new PyTuple(p.X, p.Y)).ToList())}, added {Py.Repr(tb.Except(ta).OrderBy(p => p).Take(10).Select(p => new PyTuple(p.X, p.Y)).ToList())}");
        int changed = 0;
        for (int i = 0; i < Math.Min(A.Offsets.Length, B.Offsets.Length); i++)
        {
            uint oa = A.Offsets[i], ob = B.Offsets[i];
            if (oa == 0 || ob == 0) continue;
            if (oa != ob) d.Layout.Add(("MAOF", $"tile {i % 64},{i / 64} offset 0x{Py.Hex(oa)} -> 0x{Py.Hex(ob)}"));
            var ha = A.Heights(i % 64, i / 64) ?? Array.Empty<short>();
            var hb = B.Heights(i % 64, i / 64) ?? Array.Empty<short>();
            if (!ha.AsSpan().SequenceEqual(hb))
            {
                changed++;
                if (changed <= 10)
                {
                    int n = ha.Zip(hb).Count(p => p.First != p.Second);
                    int mx = ha.Zip(hb).Select(p => Math.Abs(p.First - p.Second)).DefaultIfEmpty(0).Max();
                    d.Add("MARE", $"tile {i % 64},{i / 64}: {n} of 545 heights changed (max {mx})");
                }
            }
        }
        if (changed > 10) d.Add("MARE", $"{changed - 10} more tiles with changed heights");
        d.Stats.WdlTiles = changed;
        return d;
    }

    public static MapDiffResult DiffBytes(byte[] a, byte[] b, string name, string aName = "a", string bName = "b")
    {
        var low = name.ToLowerInvariant();
        if (low.EndsWith(".adt", StringComparison.Ordinal)) return DiffAdt(a, b, aName, bName);
        if (low.EndsWith(".wdt", StringComparison.Ordinal)) return DiffWdt(a, b, aName, bName);
        if (low.EndsWith(".wdl", StringComparison.Ordinal)) return DiffWdl(a, b, aName, bName);
        throw new ArgumentException("not a map file: " + name);
    }
}
