using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WoWData.Core.ModelIO;

public sealed class GltfException : Exception
{
    public GltfException(string message) : base(message) { }
}

/// <summary>
/// A glTF 2.0 file read for the model import: the JSON (as a mutable tree), its binary buffers and where it came from.
/// Reads .gltf with external or data-URI buffers and .glb. Accessors are read with byteStride, byteOffset,
/// normalized integer types and sparse substitution, as the specification describes (Blender writes all of them).
/// Written from the Khronos glTF 2.0 specification; no exporter code was used.
/// </summary>
public sealed class GltfFile
{
    public const uint GlbMagic = 0x46546C67;
    public JsonObject Json { get; }
    public string? Path { get; }
    public string? BaseDir { get; }
    private readonly List<byte[]> _buffers = new();
    private int[]? _parents;

    private GltfFile(JsonObject json, string? path, byte[]? glbBin)
    {
        Json = json;
        Path = path;
        BaseDir = path == null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        var buffers = Arr("buffers");
        for (int i = 0; i < buffers.Count; i++)
        {
            var b = buffers[i]!.AsObject();
            var uri = b["uri"]?.GetValue<string>();
            if (uri == null)
            {
                if (i != 0 || glbBin == null) throw new GltfException($"buffer {i} has no data");
                _buffers.Add(glbBin);
            }
            else _buffers.Add(ReadUri(uri) ?? throw new GltfException($"the buffer file {uri} is missing next to the glTF file"));
        }
    }

    public static GltfFile Load(string path)
    {
        if (!File.Exists(path)) throw new GltfException($"{path} does not exist");
        return Parse(File.ReadAllBytes(path), path);
    }

    public static GltfFile Parse(byte[] data, string? path = null)
    {
        if (data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == GlbMagic)
        {
            int pos = 12;
            JsonObject? json = null;
            byte[]? bin = null;
            while (pos + 8 <= data.Length)
            {
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
                pos += 8;
                if (pos + len > data.Length) throw new GltfException("the .glb file is truncated");
                if (type == 0x4E4F534A) json = JsonNode.Parse(data.AsSpan(pos, len))!.AsObject();
                else if (type == 0x004E4942) bin = data.AsSpan(pos, len).ToArray();
                pos += (len + 3) & ~3;
            }
            return new GltfFile(json ?? throw new GltfException("the .glb file has no JSON chunk"), path, bin);
        }
        JsonObject root;
        try { root = JsonNode.Parse(data)!.AsObject(); }
        catch (JsonException e) { throw new GltfException("not a glTF file (the JSON could not be read): " + e.Message); }
        if (root["asset"]?["version"]?.GetValue<string>() is not { } v || !v.StartsWith("2", StringComparison.Ordinal))
            throw new GltfException("not a glTF 2.0 file");
        return new GltfFile(root, path, null);
    }

    public byte[]? ReadUri(string uri)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0) return null;
            return uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(uri[(comma + 1)..]) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri[(comma + 1)..]));
        }
        if (BaseDir == null) return null;
        var p = System.IO.Path.Combine(BaseDir, Uri.UnescapeDataString(uri).Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    public JsonArray Arr(string name) => Json[name] as JsonArray ?? new JsonArray();
    public JsonObject Node(int i) => Arr("nodes")[i]!.AsObject();
    public int NodeCount => Arr("nodes").Count;

    public static JsonObject? Extras(JsonNode? o) => o?["extras"] as JsonObject;

    public static int? IntOf(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d) && Math.Abs(d - Math.Round(d)) < 1e-9) return (int)Math.Round(d);
        if (v.TryGetValue(out string? s) && int.TryParse(s, out i)) return i;
        return null;
    }

    public static string? StrOf(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // ---------------------------------------------------------------- accessors

    private static int Components(string type) => type switch
    {
        "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
        _ => throw new GltfException($"accessor type {type}"),
    };

    private static int ComponentSize(int ct) => ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new GltfException($"component type {ct}") };

    private static double ReadComponent(ReadOnlySpan<byte> s, int ct, bool normalized) => ct switch
    {
        5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
        5121 => normalized ? s[0] / 255.0 : s[0],
        5120 => normalized ? Math.Max((sbyte)s[0] / 127.0, -1) : (sbyte)s[0],
        5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535.0 : BinaryPrimitives.ReadUInt16LittleEndian(s),
        5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767.0, -1) : BinaryPrimitives.ReadInt16LittleEndian(s),
        5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
        _ => throw new GltfException($"component type {ct}"),
    };

    public int AccessorCount(int accessor) => IntOf(Arr("accessors")[accessor]!["count"]) ?? 0;

    /// <summary>The accessor's values as doubles (count x components, row-major); normalized integers become 0..1.</summary>
    public double[] Read(int accessor, out int components)
    {
        var a = Arr("accessors")[accessor]?.AsObject() ?? throw new GltfException($"accessor {accessor} does not exist");
        int count = IntOf(a["count"]) ?? 0;
        int ct = IntOf(a["componentType"]) ?? 5126;
        components = Components(StrOf(a["type"]) ?? "SCALAR");
        bool normalized = a["normalized"]?.GetValue<bool>() == true;
        var outp = new double[count * components];
        int csize = ComponentSize(ct);
        if (IntOf(a["bufferView"]) is int bvi)
        {
            var bv = Arr("bufferViews")[bvi]!.AsObject();
            var buf = _buffers[IntOf(bv["buffer"]) ?? 0];
            int off = (IntOf(bv["byteOffset"]) ?? 0) + (IntOf(a["byteOffset"]) ?? 0);
            int stride = IntOf(bv["byteStride"]) ?? components * csize;
            if (off + (long)(count - 1) * stride + components * csize > buf.Length && count > 0) throw new GltfException($"accessor {accessor} runs past the end of its buffer");
            for (int i = 0; i < count; i++)
                for (int c = 0; c < components; c++)
                    outp[i * components + c] = ReadComponent(buf.AsSpan(off + i * stride + c * csize), ct, normalized);
        }
        if (a["sparse"] is JsonObject sp)
        {
            int n = IntOf(sp["count"]) ?? 0;
            var ind = sp["indices"]!.AsObject();
            var val = sp["values"]!.AsObject();
            var ibv = Arr("bufferViews")[IntOf(ind["bufferView"]) ?? 0]!.AsObject();
            var vbv = Arr("bufferViews")[IntOf(val["bufferView"]) ?? 0]!.AsObject();
            var ib = _buffers[IntOf(ibv["buffer"]) ?? 0];
            var vb = _buffers[IntOf(vbv["buffer"]) ?? 0];
            int ict = IntOf(ind["componentType"]) ?? 5125;
            int ioff = (IntOf(ibv["byteOffset"]) ?? 0) + (IntOf(ind["byteOffset"]) ?? 0);
            int voff = (IntOf(vbv["byteOffset"]) ?? 0) + (IntOf(val["byteOffset"]) ?? 0);
            int isz = ComponentSize(ict);
            for (int k = 0; k < n; k++)
            {
                int target = (int)ReadComponent(ib.AsSpan(ioff + k * isz), ict, false);
                for (int c = 0; c < components; c++)
                    outp[target * components + c] = ReadComponent(vb.AsSpan(voff + (k * components + c) * csize), ct, normalized);
            }
        }
        return outp;
    }

    public int[] ReadInts(int accessor)
    {
        var d = Read(accessor, out _);
        var r = new int[d.Length];
        for (int i = 0; i < d.Length; i++) r[i] = (int)d[i];
        return r;
    }

    public byte[] BufferViewBytes(int bufferView)
    {
        var bv = Arr("bufferViews")[bufferView]!.AsObject();
        var buf = _buffers[IntOf(bv["buffer"]) ?? 0];
        int off = IntOf(bv["byteOffset"]) ?? 0;
        int len = IntOf(bv["byteLength"]) ?? 0;
        return buf.AsSpan(off, len).ToArray();
    }

    /// <summary>The bytes of an image (file next to the glTF, data URI or GLB buffer view), or null when missing.</summary>
    public byte[]? ImageBytes(int image)
    {
        var im = Arr("images")[image]?.AsObject();
        if (im == null) return null;
        if (StrOf(im["uri"]) is { } uri) return ReadUri(uri);
        if (IntOf(im["bufferView"]) is int bv) return BufferViewBytes(bv);
        return null;
    }

    /// <summary>A file path for an external image (null for embedded ones).</summary>
    public string? ImageFile(int image)
    {
        var im = Arr("images")[image]?.AsObject();
        if (im == null || BaseDir == null || StrOf(im["uri"]) is not { } uri || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        return System.IO.Path.Combine(BaseDir, Uri.UnescapeDataString(uri).Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    // ---------------------------------------------------------------- transforms (System.Numerics row-vector form: v * M)

    /// <summary>The node's local transform. glTF matrices are column-major with column vectors; read in order they fill
    /// a row-vector Matrix4x4 directly.</summary>
    public Matrix4x4 Local(int node)
    {
        var n = Node(node);
        if (n["matrix"] is JsonArray m && m.Count == 16)
        {
            var f = m.Select(x => (float)x!.GetValue<double>()).ToArray();
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        var t = Vec(n["translation"], 3, 0);
        var r = Vec(n["rotation"], 4, 0);
        if (n["rotation"] == null) r[3] = 1;
        var s = Vec(n["scale"], 3, 1);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3])) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    private static float[] Vec(JsonNode? n, int len, float def)
    {
        var r = new float[len];
        Array.Fill(r, def);
        if (n is JsonArray a)
            for (int i = 0; i < len && i < a.Count; i++) r[i] = (float)a[i]!.GetValue<double>();
        return r;
    }

    public int Parent(int node)
    {
        if (_parents == null)
        {
            _parents = new int[NodeCount];
            Array.Fill(_parents, -1);
            for (int i = 0; i < NodeCount; i++)
                if (Node(i)["children"] is JsonArray ch)
                    foreach (var c in ch)
                        if (IntOf(c) is int ci && ci >= 0 && ci < _parents.Length) _parents[ci] = i;
        }
        return _parents[node];
    }

    public Matrix4x4 World(int node)
    {
        var m = Local(node);
        int guard = 0;
        for (int p = Parent(node); p >= 0 && guard < 1000; p = Parent(p), guard++) m *= Local(p);
        return m;
    }

    public IEnumerable<int> NodesWithMesh()
    {
        for (int i = 0; i < NodeCount; i++)
            if (IntOf(Node(i)["mesh"]) is not null) yield return i;
    }
}

/// <summary>Accumulates glTF JSON and one binary buffer (the writer of the model export; the layout follows
/// wowdata/gltf.py's Builder).</summary>
public sealed class GltfBuilder
{
    public const int Float = 5126, UByte = 5121, UShort = 5123, UInt = 5125;
    public const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    public JsonObject Json { get; } = new()
    {
        ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "WoW Data Studio (WoWDataTool)" },
        ["scene"] = 0,
        ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray() }),
        ["nodes"] = new JsonArray(), ["meshes"] = new JsonArray(), ["skins"] = new JsonArray(), ["animations"] = new JsonArray(),
        ["accessors"] = new JsonArray(), ["bufferViews"] = new JsonArray(),
    };

    private readonly MemoryStream _bin = new();
    private readonly Dictionary<string, int> _dedupe = new(StringComparer.Ordinal);

    public JsonArray Arr(string name) => (JsonArray)Json[name]!;
    public JsonArray Nodes => Arr("nodes");

    public int View(ReadOnlySpan<byte> data, int? target = null)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var bv = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = data.Length };
        if (target != null) bv["target"] = target.Value;
        _bin.Write(data);
        Arr("bufferViews").Add(bv);
        return Arr("bufferViews").Count - 1;
    }

    /// <summary>An accessor over float data (count x components).</summary>
    public int AccessorF(float[] data, string type, int? target = null, bool minMax = false, bool dedupe = false)
    {
        int comps = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new ArgumentException(type) };
        var bytes = new byte[data.Length * 4];
        for (int i = 0; i < data.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), data[i]);
        string? key = null;
        if (dedupe)
        {
            key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)) + "|f|" + type;
            if (_dedupe.TryGetValue(key, out int hit)) return hit;
        }
        int count = data.Length / comps;
        var acc = new JsonObject { ["bufferView"] = View(bytes, target), ["componentType"] = Float, ["count"] = count, ["type"] = type };
        if (minMax && count > 0)
        {
            var mn = new JsonArray();
            var mx = new JsonArray();
            for (int c = 0; c < comps; c++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    float v = data[i * comps + c];
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
                mn.Add(lo);
                mx.Add(hi);
            }
            acc["min"] = mn;
            acc["max"] = mx;
        }
        Arr("accessors").Add(acc);
        int idx = Arr("accessors").Count - 1;
        if (key != null) _dedupe[key] = idx;
        return idx;
    }

    public int AccessorBytes(byte[] data, string type, int componentType, int count, int? target = null, bool normalized = false)
    {
        var acc = new JsonObject { ["bufferView"] = View(data, target), ["componentType"] = componentType, ["count"] = count, ["type"] = type };
        if (normalized) acc["normalized"] = true;
        Arr("accessors").Add(acc);
        return Arr("accessors").Count - 1;
    }

    public int AccessorIndices(int[] indices, int vertexCount)
    {
        if (vertexCount < 65536)
        {
            var b = new byte[indices.Length * 2];
            for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)indices[i]);
            return AccessorBytes(b, "SCALAR", UShort, indices.Length, ElementArrayBuffer);
        }
        var b4 = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b4.AsSpan(i * 4), (uint)indices[i]);
        return AccessorBytes(b4, "SCALAR", UInt, indices.Length, ElementArrayBuffer);
    }

    public int AddNode(JsonObject node)
    {
        Nodes.Add(node);
        return Nodes.Count - 1;
    }

    public void AddChild(int parent, int child)
    {
        var p = Nodes[parent]!.AsObject();
        if (p["children"] is not JsonArray ch)
        {
            ch = new JsonArray();
            p["children"] = ch;
        }
        ch.Add(child);
    }

    public byte[] Bin()
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        return _bin.ToArray();
    }

    /// <summary>The finished JSON (a deep copy) with empty top-level arrays removed and the buffer pointing at
    /// <paramref name="binUri"/>.</summary>
    public JsonObject Finish(string binUri, int binLength)
    {
        var g = JsonNode.Parse(Json.ToJsonString())!.AsObject();
        foreach (var k in g.Where(kv => kv.Value is JsonArray { Count: 0 }).Select(kv => kv.Key).ToList()) g.Remove(k);
        g["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binLength, ["uri"] = binUri });
        return g;
    }

    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
}
