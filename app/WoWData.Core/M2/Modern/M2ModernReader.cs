using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.M2.Modern;

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md (3.3): reads a chunked model of a modern client (MD21, header
/// versions 272 / 274) into the SAME <see cref="M2Model"/> the 1.12 viewer uses, so the renderer, the animation
/// evaluation (<see cref="M2Anim"/>) and the texture code stay as they are. Our own code, written from the format
/// (FORMAT KNOWLEDGE, each part checked on the real 1.60 copy, see the real-copy tests):
/// <list type="bullet">
/// <item>the chunks: MD21 (the MD20 header and its arrays, offsets relative to the chunk's data), SFID (the .skin files
///   by FileDataID: the views first, then LOD skins), TXID (one texture FileDataID per texture record), M3: AFID (the
///   .anim files, <see cref="ModernAnimations"/>) and SKID (the skeleton file and its parent); every other chunk is
///   listed in <see cref="M2ModernInfo"/> and not used;</item>
/// <item>the header of version 264 and later: no playable-animation table, no texture flip-books, the views replaced by
///   a skin profile count; bones of 0x58 bytes (a name hash before the tracks), sequences of 0x40 bytes (a duration
///   instead of start / end, blend in / out), attachments 0x28, events 0x24;</item>
/// <item>tracks with one key array per sequence (<c>M2Array&lt;M2Array&lt;T&gt;&gt;</c>, no ranges): put onto one
///   synthetic timeline (sequence i plays from its Start to its End), each track's keys concatenated with ranges per
///   sequence: exactly the 1.12 shape <see cref="M2Anim"/> evaluates. A sequence's last key is repeated at its end so
///   the value holds there (1.12 would lean towards the next sequence's first key). Sequences without keys for a
///   track get no range (the track's default applies); aliases (flag 0x40) share their target's part of the timeline;
///   external sequences (flag 0x20 clear: their keys are in .anim files) keep their place without keys until
///   <see cref="ModernAnimations.Ensure"/> loads them;</item>
/// <item>bone rotations as compressed quaternions (four int16): a negative value v is (v + 32768) / 32767, a positive
///   one (v - 32767) / 32767, so 0x7FFF is 0 and -1 is 1;</item>
/// <item>the .skin file (SKIN): vertex list, triangles, bone palettes, submeshes of 0x30 bytes and texture units (the
///   1.12 record). A submesh's <c>level</c> is the high part of its index start (MEASURED on the 1.60 copy: 61 of
///   998 creature models have more than 65,535 triangle indices in view 0; their triangles stay in the vertex range
///   of the plain 16-bit vertex start).</item>
/// </list>
/// M3: particle emitters and ribbons are read into <see cref="M2ModernInfo.Effects"/> (drawn by the 3D view of another
/// version); lights and cameras are counted in <see cref="M2ModernInfo.NotShown"/>, not converted.
/// </summary>
public static class M2ModernReader
{
    public const uint Md21 = 0x3132444d;   // 'MD21'
    public const uint Md20 = 0x3032444d;   // 'MD20'
    public const uint Skin = 0x4e494b53;   // 'SKIN'

    /// <summary>True when the bytes start like a chunked model (MD21).</summary>
    public static bool IsChunked(ReadOnlySpan<byte> data) => data.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Md21;

    /// <summary>The chunks of a chunked model: (magic, offset of the data, size).</summary>
    public static List<(string Magic, int Offset, int Size)> Chunks(ReadOnlySpan<byte> data)
    {
        var res = new List<(string, int, int)>();
        int o = 0;
        while (o + 8 <= data.Length)
        {
            var magic = Encoding.ASCII.GetString(data.Slice(o, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(o + 4, 4));
            if (size < 0 || o + 8L + size > data.Length) throw new M2Exception($"chunk {magic} at 0x{o:x}: {size} bytes run past the end of the file (0x{data.Length:x})");
            res.Add((magic, o + 8, size));
            o += 8 + size;
        }
        return res;
    }

    /// <summary>
    /// Reads a chunked model. <paramref name="readFile"/> returns a file by FileDataID (the .skin files; it may throw
    /// for an encrypted or missing file, which is reported); <paramref name="nameOf"/> names a texture FileDataID (its
    /// known name, else "#id.blp"). Throws <see cref="M2Exception"/> when the model or its first view cannot be read.
    /// </summary>
    public static M2Model Load(byte[] data, Func<uint, byte[]?> readFile, Func<uint, string>? nameOf = null)
    {
        if (!IsChunked(data)) throw new M2Exception(data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Md20
            ? "an MD20 model without chunks: the modern reader reads chunked (MD21) models; 1.12 models open in your game's window"
            : "not a chunked M2 (MD21) file");
        nameOf ??= id => $"#{id}.blp";
        var chunks = Chunks(data);
        var md = chunks.FirstOrDefault(c => c.Magic == "MD21");
        if (md.Magic == null) throw new M2Exception("no MD21 chunk");
        uint[] sfid = U32s(data, chunks, "SFID");
        uint[] txid = U32s(data, chunks, "TXID");
        var body = data.AsSpan(md.Offset, md.Size).ToArray();
        var b = new Buf(body);
        if (b.U32(0) != Md20) throw new M2Exception("the MD21 chunk does not hold an MD20 header");
        uint version = b.U32(4);
        if (version < 264) throw new M2Exception($"M2 version {version}: the modern reader reads versions 264 and later");
        int profiles = (int)b.U32(0x44);
        var info = new M2ModernInfo { Version = version, SkinProfiles = profiles, SkinFiles = sfid, TextureFiles = txid };
        foreach (var c in chunks)
        {
            var (used, meaning) = M2ModernInfo.Describe(c.Magic);
            info.Chunks.Add(new M2ChunkInfo(c.Magic, c.Size, used, meaning));
        }
        var afid = chunks.FirstOrDefault(c => c.Magic == "AFID");
        if (afid.Magic != null)
            for (int i = 0; i + 8 <= afid.Size; i += 8)
            {
                var s = data.AsSpan(afid.Offset + i);
                uint f = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
                if (f != 0) info.AnimFiles.Add(new M2AnimFile(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), f));
            }
        var skid = chunks.FirstOrDefault(c => c.Magic == "SKID");
        if (skid.Magic != null && skid.Size >= 4) info.SkeletonFile = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(skid.Offset));
        foreach (var c in chunks.Where(c => !M2ModernInfo.Describe(c.Magic).Used).GroupBy(c => c.Magic))
            info.Ignored.Add($"{c.Key}: {M2ModernInfo.Describe(c.Key).Meaning}");
        var skel = info.SkeletonFile != 0 ? ReadSkeleton(info.SkeletonFile, readFile, info) : null;

        var views = new List<M2View>();
        int want = Math.Min(profiles, sfid.Length);
        for (int v = 0; v < want; v++)
        {
            byte[]? skin;
            string why;
            try
            {
                skin = readFile(sfid[v]);
                why = skin == null ? "not in this client" : "";
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                skin = null;
                why = e.Message;
            }
            if (skin == null)
            {
                if (v == 0) throw new M2Exception($"its first view (skin file #{sfid[v]}) cannot be read: {why}");
                info.Notes.Add($"view {v + 1} (skin file #{sfid[v]}) cannot be read: {why}");
                break;
            }
            var view = ReadSkin(skin, $"skin file #{sfid[v]}");
            views.Add(view);
            info.ViewTriangles.Add(view.Triangles.Length / 3);
        }
        if (want == 0) info.Notes.Add(profiles == 0 ? "the model has no views (skin profiles)" : "the model names no skin files (SFID)");
        info.ViewsRead = views.Count;
        var unitLookup = TextureLayers(views, info);
        var conv = new Converter(b, info, skel);
        var file = conv.Build(nameOf, txid, unitLookup);
        var model = M2Model.FromModern(file, views.ToArray(), info);
        // M3: the particle emitters and ribbons, drawn by the 3D view of another version
        var fx = conv.Effects(model.Bones.Length, model.Textures.Length, model.RenderFlags.Length);
        if (fx.Particles.Count + fx.Ribbons.Count > 0)
        {
            info.Effects = fx;
            info.Approximated.Add($"{Plural(fx.Particles.Count, "particle emitter")} and {Plural(fx.Ribbons.Count, "ribbon")} drawn by the viewer's own simulation (close to the game's look, not exact)");
            info.Approximated.AddRange(fx.Approximated());
        }
        conv.Finish();
        // M3: the external animations, loaded when a sequence is chosen
        if (info.ExternalSequences > 0)
        {
            var anims = new ModernAnimations(readFile, conv);
            anims.Bind(model, info.Effects, conv.ExtRecords);
            info.Animations = anims;
            int noFile = Enumerable.Range(0, model.Sequences.Length).Count(i => anims.IsExternal(i) && anims.FileOf(i) == 0);
            if (noFile > 0) info.Notes.Add($"{Plural(noFile, "animation")} kept outside the model name{(noFile == 1 ? "s" : "")} no .anim file (AFID): {(noFile == 1 ? "it has" : "they have")} no keys");
        }
        return model;
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static byte[]? TryRead(Func<uint, byte[]?> readFile, uint id, out string why)
    {
        try
        {
            var d = readFile(id);
            why = d == null ? "not in this client" : "";
            return d;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>The chunks of a skeleton file by magic (the first of each), as buffers of their data.</summary>
    private static Dictionary<string, Buf> SkelChunks(byte[] s)
    {
        var res = new Dictionary<string, Buf>();
        foreach (var (m, off, size) in Chunks(s))
            if (!res.ContainsKey(m)) res[m] = new Buf(s.AsSpan(off, size).ToArray());
        return res;
    }

    private static List<M2AnimFile> AnimFilesOf(Buf? afid)
    {
        var res = new List<M2AnimFile>();
        if (afid == null) return res;
        for (int i = 0; i + 8 <= afid.Length; i += 8)
        {
            uint f = afid.U32(i + 4);
            if (f != 0) res.Add(new M2AnimFile(afid.U16(i), afid.U16(i + 2), f));
        }
        return res;
    }

    /// <summary>
    /// M3: the skeleton file of a model with a SKID chunk (FORMAT KNOWLEDGE, MEASURED on the 1.60 copy: 22 creature
    /// models, their own bone, sequence and attachment arrays empty): SKS1 (global sequences, sequences, lookup), SKB1
    /// (bones, key bone lookup), SKA1 (attachment points, lookup), AFID (its .anim files), SKPD (the parent skeleton's
    /// FileDataID at +8); every offset relative to its chunk's data. A child skeleton's bones equal its parent's
    /// (MEASURED: index, name hash, key bone and parent of every bone); the parent adds the sequences the child does not
    /// have. Returns null (with a note) when the skeleton cannot be read: the model then shows without bones.
    /// </summary>
    private static SkeletonSource? ReadSkeleton(uint fdid, Func<uint, byte[]?> readFile, M2ModernInfo info)
    {
        var s = TryRead(readFile, fdid, out var why);
        if (s == null)
        {
            info.Notes.Add($"skeleton file #{fdid} cannot be read ({why}): the model is shown without bones and animations");
            return null;
        }
        Dictionary<string, Buf> c;
        try { c = SkelChunks(s); }
        catch (M2Exception e)
        {
            info.Notes.Add($"skeleton file #{fdid} is broken ({e.Message}): the model is shown without bones and animations");
            return null;
        }
        if (!c.TryGetValue("SKS1", out var sks1) || !c.TryGetValue("SKB1", out var skb1))
        {
            info.Notes.Add($"skeleton file #{fdid} has no bones or sequences (SKB1 / SKS1): the model is shown without them");
            return null;
        }
        var src = new SkeletonSource { File = fdid, Sks1 = sks1, Skb1 = skb1, Ska1 = c.GetValueOrDefault("SKA1"), AnimFiles = AnimFilesOf(c.GetValueOrDefault("AFID")) };
        info.AnimFiles.AddRange(src.AnimFiles);
        info.SkeletonRead = true;
        foreach (var x in c.Keys.Where(k => k is not ("SKL1" or "SKS1" or "SKB1" or "SKA1" or "SKPD" or "AFID")))
            info.Ignored.Add($"skeleton {x}: {M2ModernInfo.Describe(x).Meaning}");
        if (c.TryGetValue("SKPD", out var skpd) && skpd.Length >= 12 && skpd.U32(8) is var parent and not 0)
        {
            info.SkeletonParentFile = parent;
            var p = TryRead(readFile, parent, out var pwhy);
            Dictionary<string, Buf>? pc = null;
            if (p != null)
                try { pc = SkelChunks(p); }
                catch (M2Exception e) { pwhy = e.Message; }
            if (pc == null || !pc.TryGetValue("SKS1", out var psks1) || !pc.TryGetValue("SKB1", out var pskb1))
                info.Notes.Add($"parent skeleton #{parent} cannot be read ({(p == null || pc == null ? pwhy : "no bones or sequences")}): only the skeleton's own animations are shown");
            else
            {
                src.ParentFile = parent;
                src.ParentSks1 = psks1;
                src.ParentSkb1 = pskb1;
                src.ParentAnimFiles = AnimFilesOf(pc.GetValueOrDefault("AFID"));
                // child bone -> parent bone by the bone's name hash (+0x0C), else the same index
                int n = (int)skb1.U32(0), pn = (int)pskb1.U32(0);
                uint o = skb1.U32(4), po = pskb1.U32(4);
                var byHash = new Dictionary<uint, int>();
                for (int i = 0; i < pn && pskb1.Fits((uint)(i + 1), po, 0x58); i++) byHash.TryAdd(pskb1.U32((int)po + i * 0x58 + 0x0C), i);
                src.ParentBone = new int[n];
                for (int i = 0; i < n; i++)
                {
                    uint h = skb1.Fits((uint)(i + 1), o, 0x58) ? skb1.U32((int)o + i * 0x58 + 0x0C) : 0;
                    src.ParentBone[i] = h != 0 && byHash.TryGetValue(h, out int j) ? j : i < pn ? i : -1;
                }
                if (pc.ContainsKey("SKPD")) info.Notes.Add($"parent skeleton #{parent} has a parent of its own: its animations are not added");
            }
        }
        return src;
    }

    /// <summary>
    /// The texture layers of the texture units, in the 1.12 form the renderer draws (MEASURED on the 1.60 copy: the
    /// texture-coordinate lookup of modern models is empty; a unit's layers are described by its shader id instead).
    /// FORMAT KNOWLEDGE of the shader id: 0x8000 and up name a combiner effect of several layers (a base texture with a
    /// reflection or glow); below it, bits 0x70 / 0x07 are the operations of the first / second layer and 0x80 / 0x08
    /// mark a layer mapped by the reflection (sphere) instead of its UV set. The 1.12 renderer multiplies two layers:
    /// a unit whose second layer is a plain multiply (operation 1, no reflection) keeps both layers; every other unit
    /// with several layers is drawn with its first layer only (counted in <see cref="M2ModernInfo.Approximated"/>; the
    /// full combiners come with the effects, M3). Returns the synthetic texture-coordinate lookup (0 = first UV set,
    /// 1 = second, 0xFFFF = sphere map) and points every unit at its pair.
    /// </summary>
    private static byte[] TextureLayers(List<M2View> views, M2ModernInfo info)
    {
        var pairs = new List<(ushort, ushort)>();
        int firstOnly = 0, sphere = 0, combined = 0;
        foreach (var v in views)
            foreach (var tu in v.TexUnits)
            {
                int sid = tu.ShaderId;
                bool combiner = (sid & 0x8000) != 0;
                bool env1 = false;
                if (tu.TextureCount >= 2 && (combiner || (sid & 0x7) != 1 || (sid & 0x8) != 0))
                {
                    // M3: the reflection and glow combiners the 3D view draws (LayerOp), else the first texture only
                    var (op, env) = LayerOp(sid);
                    if (op > 0)
                    {
                        tu.TextureCount = 2;
                        info.LayerOps[tu] = op;
                        env1 = env;
                        combined++;
                    }
                    else
                    {
                        tu.TextureCount = 1;
                        firstOnly++;
                    }
                }
                bool env0 = !combiner && (sid & 0x80) != 0;
                if (env0) sphere++;
                var pair = (env0 ? (ushort)0xFFFF : (ushort)0, env1 ? (ushort)0xFFFF : (ushort)1);
                int at = pairs.IndexOf(pair);
                if (at < 0)
                {
                    at = pairs.Count;
                    pairs.Add(pair);
                }
                tu.TexUnitLookup = at * 2;
            }
        if (combined > 0) info.Approximated.Add($"{combined} texture unit{(combined == 1 ? "" : "s")} with a reflection or glow layer drawn with the viewer's version of the combiner (close, not exact)");
        if (firstOnly > 0) info.Approximated.Add($"{firstOnly} texture unit{(firstOnly == 1 ? "" : "s")} with a reflection or glow layer drawn with {(firstOnly == 1 ? "its" : "their")} first texture only");
        if (sphere > 0) info.Approximated.Add($"{sphere} texture unit{(sphere == 1 ? "" : "s")} drawn as a reflection (sphere map), from the shader id");
        var bytes = new byte[pairs.Count * 4];
        for (int i = 0; i < pairs.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4), pairs[i].Item1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4 + 2), pairs[i].Item2);
        }
        return bytes;
    }

    /// <summary>The two-layer combiners the 3D view draws.</summary>
    public const int OpReflectByAlpha = 1, OpAddAlpha = 2, OpAddAlphaMasked = 3, OpMod2x = 4, OpAdd = 5;

    /// <summary>
    /// M3: how the 3D view combines a unit's second layer with its first (0: it does not, the first texture only), and
    /// whether the second layer is mapped by the reflection (sphere). FORMAT KNOWLEDGE of the modern client's combiners
    /// by shader id, the second layer's role MEASURED on the 1.60 copy by its texture names (where the names are known:
    /// combiner 0x8000: 30 of 30 reflection maps (ArmorReflect..., OrbReflect...), 0x8001: 51 of 52, 0x8002: 2 of 3,
    /// 0x8003: 6 of 6, 0x000E: 37 of 37; 0x0014: a glow and a rune):
    /// <list type="bullet">
    /// <item>0x8000 / 0x8003 "opaque, mod2x by the reflection where the first texture's alpha is low":
    ///   mix(base x reflection x 2, base, base alpha) (0x8003's third layer is left out);</item>
    /// <item>0x8001 "opaque, add the reflection by its alpha": base + reflection x its alpha;</item>
    /// <item>0x8002: the same, masked by the first texture's alpha;</item>
    /// <item>below 0x8000, the second layer's operation (bits 0x07) 4 / 6 (mod2x) and 3 (add), mapped by the reflection
    ///   when bit 0x08 is set.</item>
    /// </list>
    /// </summary>
    public static (int Op, bool Env) LayerOp(int shaderId)
    {
        if ((shaderId & 0x8000) != 0)
            return (shaderId & 0x7FFF) switch
            {
                0 or 3 => (OpReflectByAlpha, true),
                1 => (OpAddAlpha, true),
                2 => (OpAddAlphaMasked, true),
                _ => (0, false),
            };
        bool env = (shaderId & 0x8) != 0;
        return (shaderId & 0x7) switch
        {
            4 or 6 => (OpMod2x, env),
            3 => (OpAdd, env),
            _ => (0, false),
        };
    }

    private static uint[] U32s(byte[] data, List<(string Magic, int Offset, int Size)> chunks, string magic)
    {
        var c = chunks.FirstOrDefault(x => x.Magic == magic);
        if (c.Magic == null) return System.Array.Empty<uint>();
        var a = new uint[c.Size / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Offset + i * 4));
        return a;
    }

    /// <summary>A compressed quaternion component (int16) as a float: negative v -> (v + 32768) / 32767, else
    /// (v - 32767) / 32767.</summary>
    public static float DecompressQuat(short v) => (v < 0 ? v + 32768 : v - 32767) / 32767f;

    /// <summary>The inverse of <see cref="DecompressQuat"/> (tests and fixtures).</summary>
    public static short CompressQuat(float f)
    {
        f = Math.Clamp(f, -1f, 1f);
        int v = f >= 0 ? (int)MathF.Round(f * 32767f) - 32768 : (int)MathF.Round(f * 32767f) + 32767;
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }

    // ------------------------------------------------------------------ the .skin file

    /// <summary>Reads a .skin file (SKIN, version 264 and later) into a view.</summary>
    public static M2View ReadSkin(byte[] s, string what = "skin file")
    {
        var b = new Buf(s);
        if (s.Length < 0x30 || b.U32(0) != Skin) throw new M2Exception($"{what} is not a SKIN file");
        var vertices = b.Block(4, 2, what + " vertices");
        var indices = b.Block(0x0C, 2, what + " triangles");
        var bones = b.Block(0x14, 4, what + " bone palettes");
        var subs = b.Block(0x1C, 0x30, what + " submeshes");
        var units = b.Block(0x24, 0x18, what + " texture units");
        var v = new M2View
        {
            Indices = ToU16(vertices), Triangles = ToU16(indices), Properties = bones.ToArray(), BoneLimit = b.U32(0x2C),
            Submeshes = new M2Submesh[subs.Length / 0x30], TexUnits = new M2TexUnit[units.Length / 0x18],
        };
        for (int k = 0; k < v.Submeshes.Length; k++)
        {
            var x = subs.Slice(k * 0x30, 0x30);
            int level = U16(x, 2);
            v.Submeshes[k] = new M2Submesh
            {
                GeosetId = U16(x, 0), Level = level, VertexStart = U16(x, 4), VertexCount = U16(x, 6), IndexStart = U16(x, 8) + (level << 16),
                IndexCount = U16(x, 10), BoneCount = U16(x, 12), BoneStart = U16(x, 14), BoneInfluences = U16(x, 16), CenterBone = U16(x, 18),
                Center = new System.Numerics.Vector3(F32(x, 20), F32(x, 24), F32(x, 28)),
            };
        }
        for (int k = 0; k < v.TexUnits.Length; k++)
        {
            var x = units.Slice(k * 0x18, 0x18);
            v.TexUnits[k] = new M2TexUnit
            {
                Flags = x[0], Priority = unchecked((sbyte)x[1]), ShaderId = U16(x, 2), Submesh = U16(x, 4), GeosetIndex = U16(x, 6), Color = unchecked((short)U16(x, 8)),
                RenderFlags = U16(x, 10), MaterialLayer = U16(x, 12), TextureCount = U16(x, 14), TextureLookup = U16(x, 16), TexUnitLookup = U16(x, 18),
                TransparencyLookup = U16(x, 20), TextureTransformLookup = U16(x, 22),
            };
        }
        return v;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    private static float F32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);

    private static ushort[] ToU16(ReadOnlySpan<byte> s)
    {
        var a = new ushort[s.Length / 2];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]);
        return a;
    }

    // ------------------------------------------------------------------ bounds-checked reads

    internal sealed class Buf
    {
        public readonly byte[] B;
        public Buf(byte[] b) { B = b; }
        public int Length => B.Length;

        public uint U32(int o) => o + 4 <= B.Length ? BinaryPrimitives.ReadUInt32LittleEndian(B.AsSpan(o)) : throw new M2Exception($"read past the end at 0x{o:x}");
        public ushort U16(int o) => o + 2 <= B.Length ? BinaryPrimitives.ReadUInt16LittleEndian(B.AsSpan(o)) : throw new M2Exception($"read past the end at 0x{o:x}");
        public short I16(int o) => unchecked((short)U16(o));

        /// <summary>The elements of the M2Array {count, offset} at <paramref name="at"/>.</summary>
        public ReadOnlySpan<byte> Block(int at, int elemSize, string what)
        {
            uint n = U32(at), o = U32(at + 4);
            return Range(n, o, elemSize, what);
        }

        public ReadOnlySpan<byte> Range(uint n, uint o, int elemSize, string what)
        {
            if (n == 0) return ReadOnlySpan<byte>.Empty;
            long bytes = (long)n * elemSize;
            if (o > B.Length || o + bytes > B.Length)
                throw new M2Exception($"{what}: {n} x {elemSize} bytes at 0x{o:x} run past the end (0x{B.Length:x})");
            return B.AsSpan((int)o, (int)bytes);
        }

        public bool Fits(uint n, uint o, int elemSize) => n == 0 || (o <= B.Length && o + (long)n * elemSize <= B.Length);
    }
}
