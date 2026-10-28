using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WoWData.Core.Maps;

/// <summary>One render batch of a WMO group: a run of indices drawn with one material.</summary>
public readonly record struct WmoBatch(int IndexStart, int IndexCount, int Material);

public sealed class WmoGroup
{
    public int Index;
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] Uvs = Array.Empty<Vector2>();
    public ushort[] Indices = Array.Empty<ushort>();
    public List<WmoBatch> Batches = new();
    public uint Flags;
    public bool Indoor => (Flags & 0x2000) != 0;
}

/// <summary>What the root file of a WMO says about its groups and doodad sets (<see cref="WmoGeometry.ReadRoot"/>).</summary>
public sealed class WmoRootInfo
{
    public int GroupCount;
    public int DoodadCount;
    public Vector3 BoundsMin, BoundsMax;
    /// <summary>Per group: its name (MOGN) and flags (0x2000 = indoor).</summary>
    public List<(string Name, uint Flags)> Groups { get; } = new();
    public List<(string Name, int Start, int Count)> DoodadSets { get; } = new();
}

/// <summary>
/// The geometry of a WMO building (version 17, the 1.12 format): the root file's textures (MOTX), materials (MOMT:
/// texture 1 at +0x0C, blend mode at +0x08), group count and bounding box (MOHD), and every group file
/// (&lt;name&gt;_NNN.wmo: MOGP with MOVT positions, MONR normals, MOTV texture coordinates, MOVI indices and MOBA batches:
/// start index @+12, count @+16, material @+23). The preview draws the groups with their first texture; portals,
/// lights, vertex colours, liquids and the WMO's own doodads are not drawn (documented in the manual).
/// <para>M4: the FileDataID form of a modern client (MVER 17 with a <c>GFID</c> chunk; MEASURED on 60 placed 1.60 roots:
/// every one has GFID and none has MOTX): the groups are the files of the first MOHD-group-count FileDataIDs of GFID
/// (more entries are level-of-detail groups), MOMT's texture 1 (+0x0C) is a texture FileDataID (MEASURED: 989 of 989
/// non-zero values are BLP2 files of the client), the doodads' models are the FileDataIDs of MODI, and a batch with
/// flag 0x2 (+0x16) takes its material from the 16-bit field at +0x0A (MEASURED: 1,657 of 3,144 batches, every
/// material then within MOMT). The other modern chunks (MOMX, MGI2, MAVG, MNLD, MFED, MDDI...) are skipped.</para>
/// </summary>
public sealed class WmoGeometry
{
    public string Path { get; init; } = "";
    public List<string> MaterialTextures { get; } = new();
    public List<uint> MaterialBlend { get; } = new();
    public List<WmoGroup> Groups { get; } = new();
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public int GroupCount { get; private set; }
    public int DoodadDefs { get; private set; }
    /// <summary>M4: the root names its groups, textures and doodad models by FileDataID (GFID; a modern client).</summary>
    public bool FileDataIdMode { get; private set; }
    /// <summary>M4: the doodad models the root lists (MODI FileDataIDs in the modern form, else the MODN names).</summary>
    public List<string> DoodadModels { get; } = new();

    private static IEnumerable<(string Magic, int Pos, int Size)> Chunks(byte[] b, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            var m = Encoding.ASCII.GetString(new[] { b[p + 3], b[p + 2], b[p + 1], b[p] });
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)), (uint)(end - p - 8));
            yield return (m, p + 8, size);
            p += 8 + size;
        }
    }

    /// <summary>Reads the root and its groups through <paramref name="read"/> (game path -> bytes or null). Returns null
    /// when the root cannot be read. M4: <paramref name="nameOf"/> names a FileDataID of the modern form (the name the
    /// session reads it by; default "#id.ext").</summary>
    public static WmoGeometry? Load(string path, Func<string, byte[]?> read, Func<uint, string, string>? nameOf = null)
    {
        var root = read(path);
        if (root == null) return null;
        nameOf ??= (id, ext) => $"#{id}{ext}";
        var w = new WmoGeometry { Path = path };
        byte[] motx = Array.Empty<byte>();
        byte[] modn = Array.Empty<byte>();
        var momtTex = new List<uint>();
        var gfid = new List<uint>();
        var modi = new List<uint>();
        foreach (var (m, p, size) in Chunks(root, 0, root.Length))
        {
            switch (m)
            {
                case "MOHD" when size >= 0x3C:
                    w.GroupCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + 4));
                    w.DoodadDefs = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + 20));
                    w.BoundsMin = ReadVec3(root, p + 0x24);
                    w.BoundsMax = ReadVec3(root, p + 0x30);
                    break;
                case "MOTX":
                    motx = root.AsSpan(p, size).ToArray();
                    break;
                case "MOMT":
                    for (int i = 0; i + 64 <= size; i += 64)
                    {
                        uint blend = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i + 8));
                        momtTex.Add(BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i + 12)));
                        w.MaterialBlend.Add(blend);
                    }
                    break;
                case "GFID":
                    for (int i = 0; i + 4 <= size; i += 4) gfid.Add(BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i)));
                    break;
                case "MODI":
                    for (int i = 0; i + 4 <= size; i += 4) modi.Add(BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i)));
                    break;
                case "MODN":
                    modn = root.AsSpan(p, size).ToArray();
                    break;
            }
        }
        w.FileDataIdMode = gfid.Count > 0;
        foreach (var tex in momtTex)
            w.MaterialTextures.Add(w.FileDataIdMode ? tex == 0 ? "" : nameOf(tex, ".blp") : ZString(motx, (int)tex));
        if (w.FileDataIdMode) w.DoodadModels.AddRange(modi.Where(id => id != 0).Distinct().Select(id => nameOf(id, ".m2")));
        else
            for (int i = 0; i < modn.Length;)
            {
                var s = ZString(modn, i);
                if (s.Length > 0) w.DoodadModels.Add(s);
                i += s.Length + 1;
            }
        var stem = path[..^4];
        for (int g = 0; g < Math.Min(w.GroupCount, 512); g++)
        {
            byte[]? data;
            if (w.FileDataIdMode)
            {
                if (g >= gfid.Count || gfid[g] == 0) continue;
                data = read(nameOf(gfid[g], ".wmo"));
            }
            else data = read($"{stem}_{g:D3}.wmo");
            if (data == null) continue;
            var grp = ReadGroup(data, g, w.FileDataIdMode);
            if (grp != null) w.Groups.Add(grp);
        }
        return w;
    }

    /// <summary>The parts of a WMO root the tile breakdown lists, without reading the group files: the groups (MOGI
    /// flags + MOGN names, 32 bytes per group: flags @0, name offset @28) and the doodad sets (MODS, 32 bytes: name[20],
    /// first doodad, count).</summary>
    public static WmoRootInfo ReadRoot(byte[] root)
    {
        var info = new WmoRootInfo();
        byte[] mogn = Array.Empty<byte>();
        var mogi = new List<(uint Flags, int Name)>();
        foreach (var (m, p, size) in Chunks(root, 0, root.Length))
        {
            switch (m)
            {
                case "MOHD" when size >= 0x3C:
                    info.GroupCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + 4));
                    info.DoodadCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + 20));
                    info.BoundsMin = ReadVec3(root, p + 0x24);
                    info.BoundsMax = ReadVec3(root, p + 0x30);
                    break;
                case "MOGN":
                    mogn = root.AsSpan(p, size).ToArray();
                    break;
                case "MOGI":
                    for (int i = 0; i + 32 <= size; i += 32)
                        mogi.Add((BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i)), BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(p + i + 28))));
                    break;
                case "MODS":
                    for (int i = 0; i + 32 <= size; i += 32)
                    {
                        var name = Encoding.Latin1.GetString(root, p + i, 20);
                        int z = name.IndexOf('\0');
                        info.DoodadSets.Add((z < 0 ? name : name[..z], (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i + 20)),
                            (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(p + i + 24))));
                    }
                    break;
            }
        }
        for (int g = 0; g < mogi.Count; g++)
        {
            var name = mogi[g].Name >= 0 ? ZString(mogn, mogi[g].Name) : "";
            info.Groups.Add((name.Length > 0 ? name : $"group {g}", mogi[g].Flags));
        }
        return info;
    }

    private static Vector3 ReadVec3(byte[] b, int p) => new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p + 4)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p + 8)));

    private static string ZString(byte[] b, int start)
    {
        if (start < 0 || start >= b.Length) return "";
        int e = Array.IndexOf(b, (byte)0, start);
        return Encoding.Latin1.GetString(b, start, (e < 0 ? b.Length : e) - start);
    }

    private static WmoGroup? ReadGroup(byte[] b, int index, bool modern = false)
    {
        foreach (var (m, p, size) in Chunks(b, 0, b.Length))
        {
            if (m != "MOGP" || size < 0x44) continue;
            var g = new WmoGroup { Index = index, Flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 8)) };
            foreach (var (sm, sp, ss) in Chunks(b, p + 0x44, p + size))
            {
                switch (sm)
                {
                    case "MOVT":
                        g.Positions = new Vector3[ss / 12];
                        for (int i = 0; i < g.Positions.Length; i++) g.Positions[i] = ReadVec3(b, sp + i * 12);
                        break;
                    case "MONR":
                        g.Normals = new Vector3[ss / 12];
                        for (int i = 0; i < g.Normals.Length; i++) g.Normals[i] = ReadVec3(b, sp + i * 12);
                        break;
                    case "MOTV":
                        if (g.Uvs.Length == 0)
                        {
                            g.Uvs = new Vector2[ss / 8];
                            for (int i = 0; i < g.Uvs.Length; i++)
                                g.Uvs[i] = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(sp + i * 8)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(sp + i * 8 + 4)));
                        }
                        break;
                    case "MOVI":
                        g.Indices = new ushort[ss / 2];
                        for (int i = 0; i < g.Indices.Length; i++) g.Indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(sp + i * 2));
                        break;
                    case "MOBA":
                        for (int i = 0; i + 24 <= ss; i += 24)
                        {
                            int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(sp + i + 12));
                            int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(sp + i + 16));
                            int mat = b[sp + i + 23];
                            if (modern && (b[sp + i + 22] & 0x2) != 0) mat = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(sp + i + 10));
                            g.Batches.Add(new WmoBatch(start, count, mat));
                        }
                        break;
                }
            }
            // keep only what is consistent
            if (g.Normals.Length != g.Positions.Length) g.Normals = Enumerable.Repeat(Vector3.UnitZ, g.Positions.Length).ToArray();
            if (g.Uvs.Length != g.Positions.Length) g.Uvs = new Vector2[g.Positions.Length];
            g.Batches.RemoveAll(bt => bt.IndexStart < 0 || bt.IndexStart + bt.IndexCount > g.Indices.Length);
            if (g.Batches.Count == 0 && g.Indices.Length > 0) g.Batches.Add(new WmoBatch(0, g.Indices.Length, -1));
            for (int i = 0; i < g.Indices.Length; i++)
                if (g.Indices[i] >= g.Positions.Length) g.Indices[i] = 0;
            return g;
        }
        return null;
    }
}
