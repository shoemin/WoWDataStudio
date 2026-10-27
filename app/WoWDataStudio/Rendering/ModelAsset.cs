using System.Numerics;
using System.Runtime.InteropServices;
using WoWData.Core.Imaging;
using WoWData.Core.M2;

namespace WoWDataStudio.Rendering;

/// <summary>The GPU vertex layout (48 bytes): the M2 vertex with its 4 bone indices and 8-bit weights.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GpuVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector2 Uv2;
    public byte B0, B1, B2, B3;
    public byte W0, W1, W2, W3;
    public const int Size = 48;
}

/// <summary>One draw of a view: a texture unit on its submesh (a submesh with two texture units is drawn twice).</summary>
public sealed class Batch
{
    public int TexUnitIndex;
    public int SubmeshIndex;
    public int GeosetId;
    /// <summary>First index and index count in the view's index buffer.</summary>
    public int IndexStart, IndexCount;
    public M2TexUnit Unit = null!;
    public M2RenderFlags Flags;
    public bool Blended => Flags.Blend >= 2;
    /// <summary>M2 texture indices of the (up to 2) layers; -1 = none.</summary>
    public int[] Textures = { -1, -1 };
    /// <summary>Per layer: sphere environment mapping (tex-unit lookup >= 3, M2Scene_SetBatchTextureStages).</summary>
    public bool[] EnvMap = { false, false };
    /// <summary>Per layer: index into the texture transforms, or -1.</summary>
    public int[] TexTransform = { -1, -1 };
    public int LayerCount;
}

public sealed class ViewData
{
    public int Index;
    /// <summary>Model vertex indices, three per triangle (view.triangles mapped through view.indices).</summary>
    public uint[] Indices = Array.Empty<uint>();
    public List<Batch> Batches = new();
    public int TriangleCount => Indices.Length / 3;
    public int BoneLimit;
}

/// <summary>
/// Everything the renderer needs from a model, built on a background thread: the vertex buffer, the index buffers and
/// draw batches of every view (level of detail), the model's own texture files decoded, and the parsed model for the
/// animation and material state.
/// </summary>
public sealed class ModelAsset
{
    public string Path { get; }
    public M2Model Model { get; }
    public GpuVertex[] Vertices { get; }
    public ViewData[] Views { get; }
    /// <summary>Decoded type-0 textures by M2 texture index (null when the file is missing).</summary>
    public Dictionary<int, RgbaImage?> FileTextures { get; } = new();
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public int[] GeosetIds { get; }
    public TimeSpan LoadTime { get; set; }

    private ModelAsset(string path, M2Model m)
    {
        Path = path;
        Model = m;
        var v = m.Vertices;
        Vertices = new GpuVertex[v.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < v.Length; i++)
        {
            ref readonly var s = ref v[i];
            var n = s.Normal;
            float len = n.Length();
            Vertices[i] = new GpuVertex
            {
                Position = s.Position, Normal = len > 1e-6f ? n / len : Vector3.UnitZ, Uv = s.Uv, Uv2 = s.Uv2,
                B0 = s.B0, B1 = s.B1, B2 = s.B2, B3 = s.B3, W0 = s.W0, W1 = s.W1, W2 = s.W2, W3 = s.W3,
            };
            min = Vector3.Min(min, s.Position);
            max = Vector3.Max(max, s.Position);
        }
        if (v.Length == 0 || !float.IsFinite(min.X))
        {
            min = m.BoundsMin;
            max = m.BoundsMax;
        }
        BoundsMin = min;
        BoundsMax = max;
        Views = new ViewData[m.Views.Length];
        for (int vi = 0; vi < m.Views.Length; vi++) Views[vi] = BuildView(m, vi);
        GeosetIds = m.Views.Length == 0 ? Array.Empty<int>() : m.Views[0].Submeshes.Select(s => s.GeosetId).Distinct().OrderBy(g => g).ToArray();
    }

    private static ViewData BuildView(M2Model m, int vi)
    {
        var view = m.Views[vi];
        var ind = view.Indices;
        var tri = view.Triangles;
        var vd = new ViewData { Index = vi, BoneLimit = (int)view.BoneLimit, Indices = new uint[tri.Length] };
        for (int k = 0; k < tri.Length; k++)
        {
            int t = tri[k];
            vd.Indices[k] = t < ind.Length ? ind[t] : 0u;
        }
        for (int ti = 0; ti < view.TexUnits.Length; ti++)
        {
            var tu = view.TexUnits[ti];
            if (tu.Submesh >= view.Submeshes.Length) continue;
            var sm = view.Submeshes[tu.Submesh];
            if (sm.IndexCount == 0 || sm.IndexStart + sm.IndexCount > tri.Length) continue;
            var b = new Batch
            {
                TexUnitIndex = ti, SubmeshIndex = tu.Submesh, GeosetId = sm.GeosetId, IndexStart = sm.IndexStart, IndexCount = sm.IndexCount,
                Unit = tu, Flags = m.FlagsOf(tu), LayerCount = Math.Clamp(tu.TextureCount, 1, 2),
            };
            for (int layer = 0; layer < b.LayerCount; layer++)
            {
                int li = tu.TextureLookup + layer;
                b.Textures[layer] = li < m.TextureLookup.Length && m.TextureLookup[li] < m.Textures.Length ? m.TextureLookup[li] : -1;
                int ui = tu.TexUnitLookup + layer;
                b.EnvMap[layer] = tu.TexUnitLookup != 0xFFFF && ui < m.TexUnitLookup.Length && (ushort)m.TexUnitLookup[ui] >= 3;
                int ai = tu.TextureTransformLookup + layer;
                if (tu.TextureTransformLookup != 0xFFFF && ai < m.TextureTransformLookup.Length)
                {
                    int a = (ushort)m.TextureTransformLookup[ai];
                    b.TexTransform[layer] = a < m.TextureTransforms.Length ? a : -1;
                }
            }
            vd.Batches.Add(b);
        }
        return vd;
    }

    /// <summary>Parse the model, build the buffers and decode its own texture files.</summary>
    public static ModelAsset Build(string path, byte[] data, Func<string, RgbaImage?> loadTexture)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = Build(path, M2Model.Load(data), loadTexture);
        a.LoadTime = sw.Elapsed;
        return a;
    }

    /// <summary>M2 (design 2.2): build the buffers of a model already read (by any <see cref="IModelSource"/>, e.g. the
    /// modern reader) and decode its own texture files.</summary>
    public static ModelAsset Build(string path, M2Model m, Func<string, RgbaImage?> loadTexture)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = new ModelAsset(path, m);
        for (int i = 0; i < m.Textures.Length; i++)
            if (m.Textures[i].Type == 0) a.FileTextures[i] = m.Textures[i].FileName.Length > 0 ? loadTexture(m.Textures[i].FileName) : null;
        a.LoadTime = sw.Elapsed;
        return a;
    }

    public int TriangleCount(int view) => view < Views.Length ? Views[view].TriangleCount : 0;
}
