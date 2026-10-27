using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using WoWData.Core.Imaging;
using WoWData.Core.M2;

namespace WoWDataStudio.Rendering;

/// <summary>
/// Draws a <see cref="ModelScene"/> with OpenGL 3.3 core: GPU skinning (bone matrices in a float texture, up to 4
/// weights per vertex), per-batch blend / depth / cull state from the M2 render flags (the decompile's tables, see
/// <see cref="BlendFactors"/>), colour / transparency / UV animation, geoset visibility, and the helpers (grid, ground,
/// skeleton, bounding box, wireframe). All methods must run with the GL context current (inside the viewport's render
/// callback).
/// </summary>
public sealed partial class ModelRenderer : IDisposable
{
    private int _modelProg, _lineProg;
    private int _vao, _vbo;
    private readonly Dictionary<int, (int Ebo, int Count)> _ebos = new();
    private int _boneTex;
    private int _lineVao, _lineVbo;
    private int _white, _grey, _magenta;
    private readonly ConditionalWeakTable<RgbaImage, TexHandle> _textures = new();
    private readonly List<TexHandle> _allTextures = new();
    private ModelAsset? _uploaded;
    private Matrix4x4[] _bones = Array.Empty<Matrix4x4>();
    private bool[] _done = Array.Empty<bool>();
    private float[] _boneData = Array.Empty<float>();
    private readonly Dictionary<string, int> _uniforms = new();
    private bool _init;

    public string GlInfo { get; private set; } = "";

    private sealed class TexHandle
    {
        public int Id;
    }

    /// <summary>M2 blend mode -> (enable, src, dst): g_M2BlendToGxBlendTable {0,1,2,10,3,4,5} then
    /// g_GxGlBlendSrcByMode / g_GxGlBlendDstByMode of the client's OpenGL device.</summary>
    public static (bool Enable, BlendingFactor Src, BlendingFactor Dst) BlendFactors(int blend) => blend switch
    {
        0 or 1 => (false, BlendingFactor.One, BlendingFactor.Zero),
        2 => (true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha),
        3 => (true, BlendingFactor.One, BlendingFactor.One),
        4 => (true, BlendingFactor.SrcAlpha, BlendingFactor.One),
        5 => (true, BlendingFactor.DstColor, BlendingFactor.Zero),
        6 => (true, BlendingFactor.DstColor, BlendingFactor.SrcColor),
        _ => (true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha),
    };

    private void Init()
    {
        if (_init) return;
        _init = true;
        GlInfo = $"{GL.GetString(StringName.Renderer)} | OpenGL {GL.GetString(StringName.Version)}";
        _modelProg = Program(Shaders.ModelVs, Shaders.ModelFs);
        _lineProg = Program(Shaders.LineVs, Shaders.LineFs);
        _vao = GlTracker.GenVertexArray();
        _vbo = GlTracker.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int s = GpuVertex.Size;
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, s, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, s, 12);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, s, 24);
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, s, 32);
        GL.EnableVertexAttribArray(4);
        GL.VertexAttribIPointer(4, 4, VertexAttribIntegerType.UnsignedByte, s, (IntPtr)40);
        GL.EnableVertexAttribArray(5);
        GL.VertexAttribPointer(5, 4, VertexAttribPointerType.UnsignedByte, true, s, 44);
        GL.BindVertexArray(0);
        _lineVao = GlTracker.GenVertexArray();
        _lineVbo = GlTracker.GenBuffer();
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 28, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 28, 12);
        GL.BindVertexArray(0);
        _boneTex = GlTracker.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _boneTex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _white = Solid(255, 255, 255, 255);
        _grey = Solid(128, 128, 128, 255);
        _magenta = Checker();
    }

    private static int Compile(ShaderType type, string src)
    {
        int sh = GL.CreateShader(type);
        GL.ShaderSource(sh, src);
        GL.CompileShader(sh);
        GL.GetShader(sh, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile error: {GL.GetShaderInfoLog(sh)}");
        return sh;
    }

    private static int Program(string vs, string fs)
    {
        int v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
        int p = GlTracker.CreateProgram();
        GL.AttachShader(p, v);
        GL.AttachShader(p, f);
        GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("shader link error: " + GL.GetProgramInfoLog(p));
        GL.DeleteShader(v);
        GL.DeleteShader(f);
        return p;
    }

    private int U(int prog, string name)
    {
        string key = prog + ":" + name;
        if (!_uniforms.TryGetValue(key, out int loc)) _uniforms[key] = loc = GL.GetUniformLocation(prog, name);
        return loc;
    }

    private static void Mat(int loc, Matrix4x4 m) => GL.UniformMatrix4(loc, 1, false, ref m.M11);

    private static int Solid(byte r, byte g, byte b, byte a)
    {
        var img = new RgbaImage(2, 2);
        for (int i = 0; i < 4; i++)
        {
            img.Pixels[i * 4] = r; img.Pixels[i * 4 + 1] = g; img.Pixels[i * 4 + 2] = b; img.Pixels[i * 4 + 3] = a;
        }
        return Upload(img, true, true);
    }

    private static int Checker()
    {
        var img = new RgbaImage(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int o = img.Offset(x, y);
                bool on = ((x / 2) + (y / 2)) % 2 == 0;
                img.Pixels[o] = on ? (byte)200 : (byte)40; img.Pixels[o + 1] = on ? (byte)0 : (byte)40; img.Pixels[o + 2] = on ? (byte)200 : (byte)40; img.Pixels[o + 3] = 255;
            }
        return Upload(img, true, true);
    }

    private static int Upload(RgbaImage img, bool wrapU, bool wrapV)
    {
        int id = GlTracker.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, img.Width, img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, img.Pixels);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(wrapU ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(wrapV ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        return id;
    }

    /// <summary>The GL texture of an image, uploaded once per image object (switching back to a skin costs nothing).</summary>
    private int TextureOf(RgbaImage img)
    {
        if (_textures.TryGetValue(img, out var h)) return h.Id;
        h = new TexHandle { Id = Upload(img, true, true) };
        _textures.Add(img, h);
        _allTextures.Add(h);
        return h.Id;
    }

    private void UploadAsset(ModelAsset a)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        var span = MemoryMarshal.AsBytes(a.Vertices.AsSpan());
        GL.BufferData(BufferTarget.ArrayBuffer, span.Length, ref MemoryMarshal.GetReference(span), BufferUsageHint.StaticDraw);
        foreach (var (ebo, _) in _ebos.Values) GlTracker.DeleteBuffer(ebo);
        _ebos.Clear();
        GL.BindVertexArray(_vao);
        foreach (var v in a.Views)
        {
            int ebo = GlTracker.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, v.Indices.Length * 4, v.Indices, BufferUsageHint.StaticDraw);
            _ebos[v.Index] = (ebo, v.Indices.Length);
        }
        GL.BindVertexArray(0);
        int n = Math.Max(1, a.Model.Bones.Length);
        _bones = new Matrix4x4[n];
        _done = new bool[n];
        _boneData = new float[n * 16];
        _uploaded = a;
    }

    /// <summary>Releases textures nobody references any more (old skins of an unloaded model).</summary>
    public void CollectTextures()
    {
        // ConditionalWeakTable entries vanish with their images; delete GL names whose handles are no longer reachable
        var live = new HashSet<TexHandle>();
        foreach (var kv in _textures) live.Add(kv.Value);
        for (int i = _allTextures.Count - 1; i >= 0; i--)
        {
            if (live.Contains(_allTextures[i])) continue;
            GlTracker.DeleteTexture(_allTextures[i].Id);
            _allTextures.RemoveAt(i);
        }
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>The texture of an M2 texture index for the current look (placeholders: grey for an empty replaceable
    /// slot such as a cape, magenta for a missing file).</summary>
    private int TextureFor(ModelScene scene, ModelAsset a, int ti)
    {
        if (ti < 0 || ti >= a.Model.Textures.Length) return _white;
        var t = a.Model.Textures[ti];
        if (t.Type == 0) return a.FileTextures.TryGetValue(ti, out var img) && img != null ? TextureOf(img) : _magenta;
        return scene.Look != null && scene.Look.Slots.TryGetValue(t.Type, out var r) ? TextureOf(r.Image) : _grey;
    }

    private void Wrap(M2TextureDef t)
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(t.WrapU ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(t.WrapV ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
    }

    private void ComputeBones(ModelScene scene, ModelAsset a, OrbitCamera cam)
    {
        var m = a.Model;
        if (m.Bones.Length == 0) return;
        var (right, up, back) = cam.Basis();
        M2Anim.BoneMatrices(m, scene.AnimTime, _bones, _done, (i, mat) => Billboard(m.Bones[i], mat, right, up, back));
        for (int i = 0; i < m.Bones.Length; i++)
        {
            var b = _bones[i];
            int o = i * 16;
            _boneData[o] = b.M11; _boneData[o + 1] = b.M12; _boneData[o + 2] = b.M13; _boneData[o + 3] = b.M14;
            _boneData[o + 4] = b.M21; _boneData[o + 5] = b.M22; _boneData[o + 6] = b.M23; _boneData[o + 7] = b.M24;
            _boneData[o + 8] = b.M31; _boneData[o + 9] = b.M32; _boneData[o + 10] = b.M33; _boneData[o + 11] = b.M34;
            _boneData[o + 12] = b.M41; _boneData[o + 13] = b.M42; _boneData[o + 14] = b.M43; _boneData[o + 15] = b.M44;
        }
    }

    /// <summary>Billboarded bones (flags &amp; 0x78, the switch in M2Model_UpdateBones @00714260): the rotation part is
    /// replaced so that the bone's X axis faces the camera, keeping the axis lengths (scale) and the pivot's position.
    /// 0x40 keeps the bone's Z axis (cylindrical); 0x8, 0x10 and 0x20 face the camera fully.</summary>
    private static Matrix4x4 Billboard(M2Bone bone, Matrix4x4 m, Vector3 right, Vector3 up, Vector3 back)
    {
        uint f = bone.Flags & 0x78;
        if (f == 0) return m;
        float sx = new Vector3(m.M11, m.M12, m.M13).Length(), sy = new Vector3(m.M21, m.M22, m.M23).Length(), sz = new Vector3(m.M31, m.M32, m.M33).Length();
        Vector3 x, y, z;
        if (f == 0x40)
        {
            z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
            x = back - z * Vector3.Dot(back, z);
            if (x.LengthSquared() < 1e-8f) return m;
            x = Vector3.Normalize(x);
            y = Vector3.Cross(z, x);
        }
        else
        {
            x = back;
            y = right;
            z = up;
        }
        var p = Vector3.Transform(bone.Pivot, m);
        var r = new Matrix4x4(x.X * sx, x.Y * sx, x.Z * sx, 0, y.X * sy, y.Y * sy, y.Z * sy, 0, z.X * sz, z.Y * sz, z.Z * sz, 0, 0, 0, 0, 1);
        var t = p - Vector3.Transform(bone.Pivot, r);
        r.M41 = t.X; r.M42 = t.Y; r.M43 = t.Z;
        return r;
    }

    public Vector3 LightDirView { get; set; } = Vector3.Normalize(new Vector3(-0.45f, 0.55f, 0.7f));
    public Vector3 Ambient { get; set; } = new(0.48f, 0.48f, 0.5f);
    public Vector3 Diffuse { get; set; } = new(0.62f, 0.6f, 0.56f);

    /// <summary>Draw the scene into the bound framebuffer.</summary>
    public void Render(ModelScene scene, OrbitCamera cam, int width, int height)
    {
        Init();
        var bg = scene.Background;
        GL.Viewport(0, 0, Math.Max(1, width), Math.Max(1, height));
        GL.ClearColor(bg.X, bg.Y, bg.Z, 1f);
        GL.DepthMask(true);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var a = scene.Asset;
        var view = cam.View;
        var viewProj = view * cam.Projection(Math.Max(1, width) / (float)Math.Max(1, height));
        if (a != null && !ReferenceEquals(a, _uploaded)) UploadAsset(a);
        if (scene.ShowGround || scene.ShowGrid) DrawGround(scene, a, viewProj);
        if (a == null) return;
        ComputeBones(scene, a, cam);
        DrawModel(scene, a, view, viewProj, false);
        // M3: the particles and ribbons of another version's model (a path of its own; 1.12 models have no Modern info)
        if (a.Model.Modern?.Effects is { } fx) DrawEffects(scene, a, fx, cam, viewProj);
        if (scene.Wireframe) DrawModel(scene, a, view, viewProj, true);
        if (scene.ShowBounds) DrawBounds(a, viewProj);
        if (scene.ShowBones) DrawSkeleton(a, viewProj);
        if (scene.ShowAttachments) DrawAttachments(a, viewProj);
    }

    /// <summary>0.10.0: the attachment points as small cyan diamonds with a line to their bone.</summary>
    private void DrawAttachments(ModelAsset a, Matrix4x4 viewProj)
    {
        var m = a.Model;
        var col = new Vector4(0.25f, 0.95f, 1f, 1f);
        var link = new Vector4(0.25f, 0.95f, 1f, 0.45f);
        float r = Math.Max(0.006f, (a.BoundsMax - a.BoundsMin).Length() * 0.012f);
        foreach (var at in m.Attachments)
        {
            var mat = at.Bone < _bones.Length ? _bones[at.Bone] : Matrix4x4.Identity;
            var p = Vector3.Transform(at.Position, mat);
            if (at.Bone < m.Bones.Length) Line(Vector3.Transform(m.Bones[at.Bone].Pivot, mat), p, link);
            var x = new Vector3(r, 0, 0);
            var y = new Vector3(0, r, 0);
            var z = new Vector3(0, 0, r);
            Line(p - x, p + y, col); Line(p + y, p + x, col); Line(p + x, p - y, col); Line(p - y, p - x, col);
            Line(p - z, p + z, col);
        }
        FlushLines(viewProj, PrimitiveType.Lines, false, 2f);
    }

    private void DrawModel(ModelScene scene, ModelAsset a, Matrix4x4 view, Matrix4x4 viewProj, bool wire)
    {
        var m = a.Model;
        if (!_ebos.TryGetValue(scene.ViewIndex, out var ebo)) return;
        var vd = a.Views[scene.ViewIndex];
        GL.UseProgram(_modelProg);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo.Ebo);
        Mat(U(_modelProg, "uViewProj"), viewProj);
        Mat(U(_modelProg, "uView"), view);
        bool skinned = m.Bones.Length > 0;
        GL.Uniform1(U(_modelProg, "uSkinned"), skinned ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _boneTex);
        if (skinned)
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 4, m.Bones.Length, 0, PixelFormat.Rgba, PixelType.Float, _boneData);
        GL.Uniform1(U(_modelProg, "uBoneTex"), 2);
        GL.Uniform1(U(_modelProg, "uTex0"), 0);
        GL.Uniform1(U(_modelProg, "uTex1"), 1);
        var light = Vector3.Normalize(LightDirView);
        GL.Uniform3(U(_modelProg, "uLightDir"), light.X, light.Y, light.Z);
        GL.Uniform3(U(_modelProg, "uAmbient"), Ambient.X, Ambient.Y, Ambient.Z);
        GL.Uniform3(U(_modelProg, "uDiffuse"), Diffuse.X, Diffuse.Y, Diffuse.Z);
        GL.Uniform1(U(_modelProg, "uSolid"), wire ? 1 : 0);
        GL.Uniform4(U(_modelProg, "uSolidColor"), 0.95f, 0.85f, 0.3f, 0.85f);
        if (wire)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            GL.Enable(EnableCap.PolygonOffsetLine);
            GL.PolygonOffset(-1f, -1f);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
        }
        var time = scene.AnimTime;
        var gs = m.GlobalSequences;
        // opaque and alpha-keyed batches first (file order), then the blended ones by priority plane
        var order = vd.Batches.Where(b => scene.IsGeosetVisible(b.GeosetId)).ToList();
        if (!wire)
            order = order.Where(b => !b.Blended).Concat(order.Where(b => b.Blended).OrderBy(b => b.Unit.Priority).ThenBy(b => b.TexUnitIndex)).ToList();
        foreach (var b in order)
        {
            if (!wire)
            {
                var tu = b.Unit;
                // colour and alpha (M2Scene_SetBatchBlendState): colour track x tint, colour alpha x transparency weight
                var color = Vector3.One;
                float alpha = 1f;
                if (tu.Color >= 0 && tu.Color < m.Colors.Length)
                {
                    color = M2Anim.Eval(m.Colors[tu.Color].Color, time, gs, Vector3.One);
                    alpha *= M2Anim.EvalFixed16(m.Colors[tu.Color].Alpha, time, gs, 1f);
                }
                if (tu.TransparencyLookup < m.TransparencyLookup.Length)
                {
                    int ti = m.TransparencyLookup[tu.TransparencyLookup];
                    if (ti < m.Transparency.Length) alpha *= M2Anim.EvalFixed16(m.Transparency[ti], time, gs, 1f);
                }
                alpha = Math.Clamp(alpha, 0f, 1f);
                int blend = b.Flags.Blend;
                bool lit = !b.Flags.Unlit && blend <= 4;
                var emissive = blend == 5 ? Vector3.One : blend == 6 ? new Vector3(0.5f) : Vector3.Zero;
                var diffuse = blend >= 5 ? Vector3.Zero : color;
                GL.Uniform4(U(_modelProg, "uColor"), diffuse.X, diffuse.Y, diffuse.Z, alpha);
                GL.Uniform3(U(_modelProg, "uEmissive"), emissive.X, emissive.Y, emissive.Z);
                GL.Uniform1(U(_modelProg, "uLit"), lit ? 1 : 0);
                GL.Uniform1(U(_modelProg, "uCombiner"), blend >= 5 ? 1 : 0);
                GL.Uniform1(U(_modelProg, "uLayers"), b.LayerCount);
                float alphaRef = blend switch { 0 => -1f, 1 => alpha * 224f / 255f, _ => 1f / 255f };
                GL.Uniform1(U(_modelProg, "uAlphaRef"), alphaRef);
                var (en, src, dst) = BlendFactors(blend);
                // M3: a material of another version in blend mode 7 ("blend add", stored as 4 in the 1.12 shape)
                if (m.Modern is { } mi && b.Unit.RenderFlags < mi.MaterialBlends.Length && mi.MaterialBlends[b.Unit.RenderFlags] == 7)
                    (en, src, dst, _) = ModernBlendFactors(7);
                if (en)
                {
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(src, dst);
                }
                else GL.Disable(EnableCap.Blend);
                if (b.Flags.NoDepthTest) GL.Disable(EnableCap.DepthTest);
                else
                {
                    GL.Enable(EnableCap.DepthTest);
                    GL.DepthFunc(DepthFunction.Lequal);
                }
                GL.DepthMask(!b.Flags.NoDepthWrite);
                if (b.Flags.TwoSided) GL.Disable(EnableCap.CullFace);
                else
                {
                    GL.Enable(EnableCap.CullFace);
                    GL.CullFace(TriangleFace.Back);
                    GL.FrontFace(FrontFaceDirection.Ccw);
                }
                for (int layer = 0; layer < 2; layer++)
                {
                    GL.ActiveTexture(layer == 0 ? TextureUnit.Texture0 : TextureUnit.Texture1);
                    int ti = layer < b.LayerCount ? b.Textures[layer] : -1;
                    GL.BindTexture(TextureTarget.Texture2D, TextureFor(scene, a, ti));
                    if (ti >= 0 && ti < m.Textures.Length) Wrap(m.Textures[ti]);
                    var texMat = b.TexTransform[layer] >= 0 ? M2Anim.TextureMatrix(m.TextureTransforms[b.TexTransform[layer]], time, gs) : Matrix4x4.Identity;
                    Mat(U(_modelProg, layer == 0 ? "uTexMat0" : "uTexMat1"), texMat);
                    GL.Uniform1(U(_modelProg, layer == 0 ? "uEnv0" : "uEnv1"), b.EnvMap[layer] ? 1 : 0);
                }
            }
            GL.DrawElements(PrimitiveType.Triangles, b.IndexCount, DrawElementsType.UnsignedInt, b.IndexStart * 4);
        }
        if (wire)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            GL.Disable(EnableCap.PolygonOffsetLine);
        }
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
        GL.BindVertexArray(0);
    }

    // ---------------------------------------------------------------- helpers: lines

    private readonly List<float> _lines = new();

    private void Line(Vector3 p, Vector3 q, Vector4 c)
    {
        _lines.Add(p.X); _lines.Add(p.Y); _lines.Add(p.Z); _lines.Add(c.X); _lines.Add(c.Y); _lines.Add(c.Z); _lines.Add(c.W);
        _lines.Add(q.X); _lines.Add(q.Y); _lines.Add(q.Z); _lines.Add(c.X); _lines.Add(c.Y); _lines.Add(c.Z); _lines.Add(c.W);
    }

    private void FlushLines(Matrix4x4 viewProj, PrimitiveType type, bool depthTest, float width = 1f)
    {
        if (_lines.Count == 0) return;
        GL.UseProgram(_lineProg);
        Mat(U(_lineProg, "uViewProj"), viewProj);
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        var arr = _lines.ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * 4, arr, BufferUsageHint.StreamDraw);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (depthTest) GL.Enable(EnableCap.DepthTest);
        else GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.DrawArrays(type, 0, arr.Length / 7);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
        _lines.Clear();
    }

    private static float NiceStep(float size)
    {
        float raw = Math.Max(size / 8f, 0.01f);
        float p = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float n = raw / p;
        return (n < 1.5f ? 1 : n < 3.5f ? 2 : n < 7.5f ? 5 : 10) * p;
    }

    private void DrawGround(ModelScene scene, ModelAsset? a, Matrix4x4 viewProj)
    {
        var min = a?.BoundsMin ?? new Vector3(-1);
        var max = a?.BoundsMax ?? new Vector3(1);
        float size = Math.Max(Math.Max(max.X - min.X, max.Y - min.Y), 0.5f) * 1.6f;
        float step = NiceStep(size);
        int cells = Math.Max(4, (int)MathF.Ceiling(size / step));
        float half = cells * step / 2;
        var c = new Vector3(MathF.Round((min.X + max.X) / 2 / step) * step, MathF.Round((min.Y + max.Y) / 2 / step) * step, 0);
        if (scene.ShowGround)
        {
            // two triangles as a translucent plate (drawn without depth write, below the grid)
            var col = new Vector4(0.55f, 0.6f, 0.68f, 0.18f);
            var p00 = c + new Vector3(-half, -half, 0);
            var p10 = c + new Vector3(half, -half, 0);
            var p11 = c + new Vector3(half, half, 0);
            var p01 = c + new Vector3(-half, half, 0);
            foreach (var p in new[] { p00, p10, p11, p00, p11, p01 })
            {
                _lines.Add(p.X); _lines.Add(p.Y); _lines.Add(p.Z); _lines.Add(col.X); _lines.Add(col.Y); _lines.Add(col.Z); _lines.Add(col.W);
            }
            FlushLines(viewProj, PrimitiveType.Triangles, true);
        }
        if (scene.ShowGrid)
        {
            var line = new Vector4(0.75f, 0.8f, 0.86f, 0.35f);
            var axisX = new Vector4(0.9f, 0.35f, 0.3f, 0.8f);
            var axisY = new Vector4(0.35f, 0.8f, 0.4f, 0.8f);
            for (int i = -cells / 2; i <= cells / 2; i++)
            {
                float o = i * step;
                Line(c + new Vector3(o, -half, 0), c + new Vector3(o, half, 0), line);
                Line(c + new Vector3(-half, o, 0), c + new Vector3(half, o, 0), line);
            }
            // the model's front (+X) in red, its left (+Y) in green, from the origin
            Line(Vector3.Zero, new Vector3(half, 0, 0), axisX);
            Line(Vector3.Zero, new Vector3(0, half, 0), axisY);
            FlushLines(viewProj, PrimitiveType.Lines, true);
        }
    }

    private void DrawBounds(ModelAsset a, Matrix4x4 viewProj)
    {
        var lo = a.Model.BoundsMin;
        var hi = a.Model.BoundsMax;
        if (!(float.IsFinite(lo.X) && hi.X >= lo.X)) { lo = a.BoundsMin; hi = a.BoundsMax; }
        var c = new Vector4(0.3f, 0.85f, 1f, 0.9f);
        Vector3 P(int i) => new((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
        int[][] edges = { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 }, new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 } };
        foreach (var e in edges) Line(P(e[0]), P(e[1]), c);
        FlushLines(viewProj, PrimitiveType.Lines, true, 1.5f);
    }

    private void DrawSkeleton(ModelAsset a, Matrix4x4 viewProj)
    {
        var m = a.Model;
        var bone = new Vector4(1f, 0.85f, 0.2f, 0.95f);
        var key = new Vector4(1f, 0.4f, 0.3f, 0.95f);
        float r = Math.Max(0.004f, (a.BoundsMax - a.BoundsMin).Length() * 0.006f);
        for (int i = 0; i < m.Bones.Length; i++)
        {
            var p = Vector3.Transform(m.Bones[i].Pivot, _bones[i]);
            int par = m.Bones[i].Parent;
            if (par >= 0 && par < m.Bones.Length) Line(Vector3.Transform(m.Bones[par].Pivot, _bones[par]), p, bone);
            var col = m.Bones[i].KeyBoneId >= 0 ? key : bone;
            Line(p - new Vector3(r, 0, 0), p + new Vector3(r, 0, 0), col);
            Line(p - new Vector3(0, r, 0), p + new Vector3(0, r, 0), col);
            Line(p - new Vector3(0, 0, r), p + new Vector3(0, 0, r), col);
        }
        FlushLines(viewProj, PrimitiveType.Lines, false, 1.5f);
    }

    // ---------------------------------------------------------------- snapshot

    /// <summary>Render the scene into an offscreen framebuffer (4x MSAA, resolved) and read it back as RGBA, top row first.
    /// <paramref name="restoreFramebuffer"/> is rebound afterwards.</summary>
    public RgbaImage RenderToImage(ModelScene scene, OrbitCamera cam, int width, int height, int restoreFramebuffer)
    {
        Init();
        return Offscreen(width, height, restoreFramebuffer, (w, h) => Render(scene, cam, w, h));
    }

    /// <summary>Run <paramref name="draw"/> into an offscreen framebuffer (4x MSAA, resolved) and read it back as RGBA, top
    /// row first. <paramref name="restoreFramebuffer"/> is rebound afterwards. Shared by the model and terrain views.</summary>
    public static RgbaImage Offscreen(int width, int height, int restoreFramebuffer, Action<int, int> draw)
    {
        width = Math.Clamp(width, 16, 8192);
        height = Math.Clamp(height, 16, 8192);
        int msFbo = GL.GenFramebuffer(), color = GL.GenRenderbuffer(), depth = GL.GenRenderbuffer();
        int fbo = GL.GenFramebuffer(), colorTex = GL.GenRenderbuffer();
        try
        {
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, RenderbufferStorage.Rgba8, width, height);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, RenderbufferStorage.Depth24Stencil8, width, height);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
            draw(width, height);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colorTex);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, width, height);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colorTex);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
            GL.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
            var px = new byte[width * height * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            var img = new RgbaImage(width, height);
            int row = width * 4;
            for (int y = 0; y < height; y++) System.Buffer.BlockCopy(px, (height - 1 - y) * row, img.Pixels, y * row, row);
            for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
            return img;
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, restoreFramebuffer);
            GL.DeleteFramebuffer(msFbo);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteRenderbuffer(color);
            GL.DeleteRenderbuffer(depth);
            GL.DeleteRenderbuffer(colorTex);
        }
    }

    public void Dispose()
    {
        if (!_init) return;
        foreach (var t in _allTextures) GlTracker.DeleteTexture(t.Id);
        _allTextures.Clear();
        foreach (var (ebo, _) in _ebos.Values) GlTracker.DeleteBuffer(ebo);
        _ebos.Clear();
        GlTracker.DeleteBuffer(_vbo);
        GlTracker.DeleteBuffer(_lineVbo);
        GlTracker.DeleteVertexArray(_vao);
        GlTracker.DeleteVertexArray(_lineVao);
        GlTracker.DeleteTexture(_boneTex);
        GlTracker.DeleteTexture(_white);
        GlTracker.DeleteTexture(_grey);
        GlTracker.DeleteTexture(_magenta);
        GlTracker.DeleteProgram(_modelProg);
        GlTracker.DeleteProgram(_lineProg);
        DisposeEffects();
        _init = false;
        _uploaded = null;
    }
}
