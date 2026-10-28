using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using WoWData.Core.Imaging;

namespace WoWDataStudio.Rendering;

/// <summary>
/// Draws a <see cref="TerrainScene"/> with OpenGL 3.3 core (the same viewport and camera as the model viewer):
/// the terrain per chunk with up to 4 texture layers (tiled 8 times per chunk, as the client does) blended by their
/// 4-bit alpha maps and darkened by the shadow map (M4: up to 8 layers for another version's tiles, layers 4-7 from a
/// second alpha atlas; a chunk with at most 4 layers is drawn exactly as before), lit with the MCNR normals; holes are left out of the mesh; liquids as
/// translucent coloured surfaces; doodads with their own textures in their rest pose (alpha-keyed and blended materials as
/// in the model viewer, no animation, no particles); WMO groups with the first texture of each batch (no portals,
/// lights or vertex colours). Must run with the GL context current.
/// </summary>
public sealed class TerrainRenderer : IDisposable
{
    private int _terrainProg, _staticProg, _colorProg;
    private bool _init;
    private TerrainAsset? _uploaded;
    private int _tVao, _tVbo, _tEbo;
    private int _wVao, _wVbo, _liquidCount;
    private readonly List<int> _textures = new();
    private readonly List<int> _atlases = new();
    private readonly List<int> _atlases2 = new();
    private readonly Dictionary<ModelAsset, (int Vao, int Vbo, int Ebo)> _doodadBuffers = new();
    private readonly Dictionary<WmoMesh, (int Vao, int Vbo, int Ebo)> _wmoBuffers = new();
    private readonly ConditionalWeakTable<RgbaImage, Holder> _imageTex = new();
    private readonly List<Holder> _allImageTex = new();
    private int _grey, _white;
    private readonly Dictionary<string, int> _uniforms = new();

    private sealed class Holder { public int Id; }

    public Vector3 LightDir { get; set; } = Vector3.Normalize(new Vector3(0.35f, 0.45f, 0.82f));

    private const string TerrainVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uViewProj;
        out vec3 vNormal;
        out vec2 vUv;
        out float vHeight;
        void main() {
            gl_Position = uViewProj * vec4(aPos, 1.0);
            vNormal = aNormal;
            vUv = aUv;
            vHeight = aPos.z;
        }
        """;

    private const string TerrainFs = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv;
        in float vHeight;
        uniform sampler2D uL0;
        uniform sampler2D uL1;
        uniform sampler2D uL2;
        uniform sampler2D uL3;
        uniform sampler2D uL4;
        uniform sampler2D uL5;
        uniform sampler2D uL6;
        uniform sampler2D uL7;
        uniform sampler2D uAlpha;
        uniform sampler2D uAlpha2;
        uniform int uLayers;
        uniform vec2 uCell;
        uniform int uTextured;
        uniform int uSolid;
        uniform vec4 uSolidColor;
        uniform vec3 uLight;
        uniform int uHiMode;   // 0 none, 1 highlighted chunk, 2 dimmed, 3 lit by the weight of the layers in uHiMask
        uniform int uHiMask;
        out vec4 frag;
        vec3 dimmed(vec3 c) { return mix(vec3(dot(c, vec3(0.3, 0.59, 0.11))), c, 0.2) * 0.5 + 0.1; }
        void main() {
            if (uSolid == 1) { frag = uSolidColor; return; }
            vec2 tuv = vUv * 8.0;
            vec4 al = texture(uAlpha, uCell + (vUv * 63.0 + 0.5) / 1024.0);
            vec3 c;
            if (uTextured == 1) {
                c = texture(uL0, tuv).rgb;
                if (uLayers > 1) c = mix(c, texture(uL1, tuv).rgb, al.r);
                if (uLayers > 2) c = mix(c, texture(uL2, tuv).rgb, al.g);
                if (uLayers > 3) c = mix(c, texture(uL3, tuv).rgb, al.b);
                if (uLayers > 4) {
                    vec4 a2 = texture(uAlpha2, uCell + (vUv * 63.0 + 0.5) / 1024.0);
                    c = mix(c, texture(uL4, tuv).rgb, a2.r);
                    if (uLayers > 5) c = mix(c, texture(uL5, tuv).rgb, a2.g);
                    if (uLayers > 6) c = mix(c, texture(uL6, tuv).rgb, a2.b);
                    if (uLayers > 7) c = mix(c, texture(uL7, tuv).rgb, a2.a);
                }
            } else {
                c = vec3(0.52, 0.58, 0.42);
            }
            vec3 n = normalize(vNormal);
            float d = max(dot(n, uLight), 0.0);
            float shade = mix(0.62, 1.0, al.a);
            c = c * (0.42 + 0.72 * d) * shade;
            if (uHiMode == 1) c = mix(c, vec3(1.0, 0.86, 0.25), 0.35);
            else if (uHiMode == 2) c = dimmed(c);
            else if (uHiMode == 3 && uLayers > 4) {
                vec4 a2 = texture(uAlpha2, uCell + (vUv * 63.0 + 0.5) / 1024.0);
                float a[8] = float[8](0.0, al.r, al.g, al.b, a2.r, a2.g, a2.b, a2.a);
                float w = 0.0, rest = 1.0;
                for (int L = 7; L >= 0; L--) {
                    float aL = L == 0 ? 1.0 : (L < uLayers ? a[L] : 0.0);
                    if ((uHiMask & (1 << L)) != 0) w += aL * rest;
                    rest *= 1.0 - aL;
                }
                c = mix(dimmed(c), mix(c, vec3(1.0, 0.86, 0.25), 0.18), clamp(w, 0.0, 1.0));
            }
            else if (uHiMode == 3) {
                float a1 = uLayers > 1 ? al.r : 0.0;
                float a2 = uLayers > 2 ? al.g : 0.0;
                float a3 = uLayers > 3 ? al.b : 0.0;
                float w = 0.0;
                if ((uHiMask & 1) != 0) w += (1.0 - a1) * (1.0 - a2) * (1.0 - a3);
                if ((uHiMask & 2) != 0) w += a1 * (1.0 - a2) * (1.0 - a3);
                if ((uHiMask & 4) != 0) w += a2 * (1.0 - a3);
                if ((uHiMask & 8) != 0) w += a3;
                c = mix(dimmed(c), mix(c, vec3(1.0, 0.86, 0.25), 0.18), clamp(w, 0.0, 1.0));
            }
            frag = vec4(c, 1.0);
        }
        """;

    private const string StaticVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uModel;
        uniform mat4 uViewProj;
        out vec3 vNormal;
        out vec2 vUv;
        void main() {
            vec4 w = uModel * vec4(aPos, 1.0);
            gl_Position = uViewProj * w;
            vNormal = mat3(uModel) * aNormal;
            vUv = aUv;
        }
        """;

    private const string StaticFs = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv;
        uniform sampler2D uTex;
        uniform float uAlphaRef;
        uniform vec3 uLight;
        uniform float uAmbient;
        uniform int uHiMode;   // 0 none, 1 highlighted, 2 dimmed
        out vec4 frag;
        void main() {
            vec4 t = texture(uTex, vUv);
            if (uAlphaRef >= 0.0 && t.a < uAlphaRef) discard;
            vec3 n = normalize(vNormal);
            float d = abs(dot(n, uLight));
            vec3 c = t.rgb * (uAmbient + 0.65 * d);
            if (uHiMode == 1) c = mix(c, vec3(1.0, 0.85, 0.15), 0.75) + 0.08;
            else if (uHiMode == 2) c = mix(vec3(dot(c, vec3(0.3, 0.59, 0.11))), c, 0.2) * 0.5 + 0.1;
            frag = vec4(c, t.a);
        }
        """;

    private const string ColorVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec4 aColor;
        uniform mat4 uViewProj;
        uniform float uDim;
        out vec4 vColor;
        void main() { gl_Position = uViewProj * vec4(aPos, 1.0); vColor = vec4(aColor.rgb * uDim, aColor.a); }
        """;

    private const string ColorFs = """
        #version 330 core
        in vec4 vColor;
        out vec4 frag;
        void main() { frag = vColor; }
        """;

    private void Init()
    {
        if (_init) return;
        _init = true;
        _terrainProg = Program(TerrainVs, TerrainFs);
        _staticProg = Program(StaticVs, StaticFs);
        _colorProg = Program(ColorVs, ColorFs);
        _grey = Solid(140, 140, 140);
        _white = Solid(255, 255, 255);
    }

    private static int Program(string vs, string fs)
    {
        int Compile(ShaderType t, string src)
        {
            int sh = GL.CreateShader(t);
            GL.ShaderSource(sh, src);
            GL.CompileShader(sh);
            GL.GetShader(sh, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{t} compile error: {GL.GetShaderInfoLog(sh)}");
            return sh;
        }
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

    private static int Solid(byte r, byte g, byte b)
    {
        var img = new RgbaImage(2, 2);
        for (int i = 0; i < 4; i++) { img.Pixels[i * 4] = r; img.Pixels[i * 4 + 1] = g; img.Pixels[i * 4 + 2] = b; img.Pixels[i * 4 + 3] = 255; }
        return Upload(img, true);
    }

    private static int Upload(RgbaImage img, bool mip, bool clamp = false)
    {
        int id = GlTracker.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, img.Width, img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, img.Pixels);
        if (mip) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mip ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        var wrap = (int)(clamp ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, wrap);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, wrap);
        return id;
    }

    private int TextureOf(RgbaImage img)
    {
        if (_imageTex.TryGetValue(img, out var h)) return h.Id;
        h = new Holder { Id = Upload(img, true) };
        _imageTex.Add(img, h);
        _allImageTex.Add(h);
        return h.Id;
    }

    private static (int Vao, int Vbo, int Ebo) Buffers<TV>(TV[] verts, uint[] indices, int stride, Action layout) where TV : struct
    {
        int vao = GlTracker.GenVertexArray(), vbo = GlTracker.GenBuffer(), ebo = GlTracker.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        var span = MemoryMarshal.AsBytes(verts.AsSpan());
        GL.BufferData(BufferTarget.ArrayBuffer, span.Length, ref MemoryMarshal.GetReference(span), BufferUsageHint.StaticDraw);
        layout();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * 4, indices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
        return (vao, vbo, ebo);
    }

    private static void Attr(int index, int size, int stride, int offset)
    {
        GL.EnableVertexAttribArray(index);
        GL.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, offset);
    }

    private void FreeAsset()
    {
        if (_tVao != 0) { GlTracker.DeleteVertexArray(_tVao); GlTracker.DeleteBuffer(_tVbo); GlTracker.DeleteBuffer(_tEbo); _tVao = 0; }
        if (_wVao != 0) { GlTracker.DeleteVertexArray(_wVao); GlTracker.DeleteBuffer(_wVbo); _wVao = 0; }
        foreach (var t in _textures) if (t != _grey) GlTracker.DeleteTexture(t);
        _textures.Clear();
        foreach (var t in _atlases) GlTracker.DeleteTexture(t);
        _atlases.Clear();
        foreach (var t in _atlases2) if (t != 0) GlTracker.DeleteTexture(t);
        _atlases2.Clear();
        foreach (var (vao, vbo, ebo) in _doodadBuffers.Values.Concat(_wmoBuffers.Values)) { GlTracker.DeleteVertexArray(vao); GlTracker.DeleteBuffer(vbo); GlTracker.DeleteBuffer(ebo); }
        _doodadBuffers.Clear();
        _wmoBuffers.Clear();
        foreach (var h in _allImageTex) GlTracker.DeleteTexture(h.Id);
        _allImageTex.Clear();
        _imageTex.Clear();
        _uploaded = null;
    }

    private void UploadAsset(TerrainAsset a)
    {
        FreeAsset();
        if (a.Vertices.Length > 0 && a.Indices.Length > 0)
            (_tVao, _tVbo, _tEbo) = Buffers(a.Vertices, a.Indices, TerrainVertex.Size, () =>
            {
                Attr(0, 3, TerrainVertex.Size, 0);
                Attr(1, 3, TerrainVertex.Size, 12);
                Attr(2, 2, TerrainVertex.Size, 24);
            });
        foreach (var t in a.Textures) _textures.Add(t == null ? _grey : Upload(t, true));
        foreach (var at in a.AlphaAtlases) _atlases.Add(Upload(at, false, clamp: true));
        foreach (var at in a.AlphaAtlases2) _atlases2.Add(at == null ? 0 : Upload(at, false, clamp: true));
        if (a.Liquid.Length > 0)
        {
            _wVao = GlTracker.GenVertexArray();
            _wVbo = GlTracker.GenBuffer();
            GL.BindVertexArray(_wVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _wVbo);
            var span = MemoryMarshal.AsBytes(a.Liquid.AsSpan());
            GL.BufferData(BufferTarget.ArrayBuffer, span.Length, ref MemoryMarshal.GetReference(span), BufferUsageHint.StaticDraw);
            Attr(0, 3, ColorVertex.Size, 0);
            Attr(1, 4, ColorVertex.Size, 12);
            GL.BindVertexArray(0);
            _liquidCount = a.Liquid.Length;
        }
        foreach (var (asset, _) in a.Doodads)
        {
            if (_doodadBuffers.ContainsKey(asset) || asset.Views.Length == 0) continue;
            _doodadBuffers[asset] = Buffers(asset.Vertices, asset.Views[0].Indices, GpuVertex.Size, () =>
            {
                Attr(0, 3, GpuVertex.Size, 0);
                Attr(1, 3, GpuVertex.Size, 12);
                Attr(2, 2, GpuVertex.Size, 24);
            });
        }
        foreach (var (mesh, _) in a.Wmos)
        {
            if (_wmoBuffers.ContainsKey(mesh)) continue;
            _wmoBuffers[mesh] = Buffers(mesh.Vertices, mesh.Indices, StaticVertex.Size, () =>
            {
                Attr(0, 3, StaticVertex.Size, 0);
                Attr(1, 3, StaticVertex.Size, 12);
                Attr(2, 2, StaticVertex.Size, 24);
            });
        }
        _uploaded = a;
    }

    public void Render(TerrainScene scene, OrbitCamera cam, int width, int height)
    {
        Init();
        var bg = scene.Background;
        GL.Viewport(0, 0, Math.Max(1, width), Math.Max(1, height));
        GL.ClearColor(bg.X, bg.Y, bg.Z, 1f);
        GL.DepthMask(true);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        var a = scene.Asset;
        if (a == null) return;
        if (!ReferenceEquals(a, _uploaded)) UploadAsset(a);
        var viewProj = cam.View * cam.Projection(Math.Max(1, width) / (float)Math.Max(1, height));
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        var hi = scene.Highlight is { IsEmpty: false } h ? h : null;
        if (_tVao != 0)
        {
            DrawTerrain(scene, a, viewProj, false, hi);
            if (scene.Wireframe) DrawTerrain(scene, a, viewProj, true, hi);
        }
        if (scene.ShowWmos) DrawWmos(a, viewProj, blendedPass: false, hi);
        if (scene.ShowDoodads) DrawDoodads(a, viewProj, blendedPass: false, hi);
        if (scene.ShowWater && _wVao != 0)
        {
            GL.UseProgram(_colorProg);
            Mat(U(_colorProg, "uViewProj"), viewProj);
            GL.Uniform1(U(_colorProg, "uDim"), hi != null ? 0.45f : 1f);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.BindVertexArray(_wVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _liquidCount);
            GL.DepthMask(true);
        }
        if (scene.ShowWmos) DrawWmos(a, viewProj, blendedPass: true, hi);
        if (scene.ShowDoodads) DrawDoodads(a, viewProj, blendedPass: true, hi);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);
        GL.BindVertexArray(0);
    }

    private void DrawTerrain(TerrainScene scene, TerrainAsset a, Matrix4x4 viewProj, bool wire, TerrainHighlight? hi)
    {
        GL.UseProgram(_terrainProg);
        Mat(U(_terrainProg, "uViewProj"), viewProj);
        GL.Uniform3(U(_terrainProg, "uLight"), LightDir.X, LightDir.Y, LightDir.Z);
        GL.Uniform1(U(_terrainProg, "uTextured"), scene.ShowTextures ? 1 : 0);
        GL.Uniform1(U(_terrainProg, "uSolid"), wire ? 1 : 0);
        GL.Uniform4(U(_terrainProg, "uSolidColor"), 0.1f, 0.1f, 0.1f, 0.6f);
        for (int i = 0; i < 4; i++) GL.Uniform1(U(_terrainProg, "uL" + i), i);
        GL.Uniform1(U(_terrainProg, "uAlpha"), 4);
        for (int i = 4; i < 8; i++) GL.Uniform1(U(_terrainProg, "uL" + i), i + 1);     // M4: layers 4-7 on units 5-8
        GL.Uniform1(U(_terrainProg, "uAlpha2"), 9);
        GL.BindVertexArray(_tVao);
        if (wire)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            GL.Enable(EnableCap.PolygonOffsetLine);
            GL.PolygonOffset(-1f, -1f);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        int lastTile = -1;
        foreach (var c in a.Chunks)
        {
            if (c.IndexCount == 0) continue;
            if (!wire)
            {
                if (c.Tile != lastTile)
                {
                    GL.ActiveTexture(TextureUnit.Texture4);
                    GL.BindTexture(TextureTarget.Texture2D, _atlases[c.Tile]);
                    lastTile = c.Tile;
                }
                for (int L = 0; L < 4; L++)
                {
                    GL.ActiveTexture(TextureUnit.Texture0 + L);
                    GL.BindTexture(TextureTarget.Texture2D, c.Layers[L] >= 0 ? _textures[c.Layers[L]] : _white);
                }
                if (c.LayerCount > 4)
                {
                    // M4: another version's chunk with more than 4 layers
                    for (int L = 4; L < 8; L++)
                    {
                        GL.ActiveTexture(TextureUnit.Texture1 + L);
                        GL.BindTexture(TextureTarget.Texture2D, L < c.LayerCount && c.Layers[L] >= 0 ? _textures[c.Layers[L]] : _white);
                    }
                    GL.ActiveTexture(TextureUnit.Texture9);
                    GL.BindTexture(TextureTarget.Texture2D, c.Tile < _atlases2.Count && _atlases2[c.Tile] != 0 ? _atlases2[c.Tile] : _white);
                }
                GL.Uniform1(U(_terrainProg, "uLayers"), Math.Max(1, c.LayerCount));
                GL.Uniform2(U(_terrainProg, "uCell"), c.ChunkX * 64 / 1024f, c.ChunkY * 64 / 1024f);
                int mode = 0, mask = 0;
                if (hi != null)
                {
                    if (hi.Texture != null)
                    {
                        if (c.Tile == 0)
                            for (int L = 0; L < c.LayerCount; L++)
                                if (c.Layers[L] >= 0 && string.Equals(WoWData.Core.Mpq.ClientData.ResolveName(a.TexturePaths[c.Layers[L]]), hi.Texture, StringComparison.OrdinalIgnoreCase)) mask |= 1 << L;
                        mode = mask != 0 ? 3 : 2;
                    }
                    else mode = c.Tile == 0 && hi.Chunks.Contains(c.ChunkY * 16 + c.ChunkX) ? 1 : 2;
                }
                GL.Uniform1(U(_terrainProg, "uHiMode"), mode);
                GL.Uniform1(U(_terrainProg, "uHiMask"), mask);
            }
            GL.DrawElements(PrimitiveType.Triangles, c.IndexCount, DrawElementsType.UnsignedInt, c.IndexStart * 4);
        }
        if (wire)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            GL.Disable(EnableCap.PolygonOffsetLine);
            GL.Disable(EnableCap.Blend);
        }
    }

    private void StaticCommon(Matrix4x4 viewProj)
    {
        GL.UseProgram(_staticProg);
        Mat(U(_staticProg, "uViewProj"), viewProj);
        GL.Uniform3(U(_staticProg, "uLight"), LightDir.X, LightDir.Y, LightDir.Z);
        GL.Uniform1(U(_staticProg, "uTex"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    private void SetBlend(bool alphaKey, bool blend)
    {
        GL.Uniform1(U(_staticProg, "uAlphaRef"), alphaKey ? 224f / 255f : blend ? 1f / 255f : -1f);
        if (blend)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
        }
        else
        {
            GL.Disable(EnableCap.Blend);
            GL.DepthMask(true);
        }
    }

    private void DrawDoodads(TerrainAsset a, Matrix4x4 viewProj, bool blendedPass, TerrainHighlight? hi)
    {
        StaticCommon(viewProj);
        GL.Uniform1(U(_staticProg, "uAmbient"), 0.5f);
        for (int di = 0; di < a.Doodads.Count; di++)
        {
            var (asset, world) = a.Doodads[di];
            if (!_doodadBuffers.TryGetValue(asset, out var buf)) continue;
            GL.Uniform1(U(_staticProg, "uHiMode"), hi == null ? 0 : di < a.DoodadUids.Count && hi.Doodads.Contains(a.DoodadUids[di]) ? 1 : 2);
            var view = asset.Views[0];
            Mat(U(_staticProg, "uModel"), world);
            GL.BindVertexArray(buf.Vao);
            foreach (var b in view.Batches)
            {
                int blend = b.Flags.Blend;
                bool blended = blend >= 2;
                if (blended != blendedPass) continue;
                if (blend >= 3) continue;   // additive / modulate glows and shadows: left out of the preview
                int ti = b.Textures[0];
                int tex = _grey;
                if (ti >= 0 && ti < asset.Model.Textures.Length && asset.Model.Textures[ti].Type == 0 && asset.FileTextures.TryGetValue(ti, out var img) && img != null) tex = TextureOf(img);
                GL.BindTexture(TextureTarget.Texture2D, tex);
                SetBlend(blend == 1, blended);
                GL.DrawElements(PrimitiveType.Triangles, b.IndexCount, DrawElementsType.UnsignedInt, b.IndexStart * 4);
            }
        }
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    private void DrawWmos(TerrainAsset a, Matrix4x4 viewProj, bool blendedPass, TerrainHighlight? hi)
    {
        StaticCommon(viewProj);
        GL.Uniform1(U(_staticProg, "uAmbient"), 0.55f);
        for (int wi = 0; wi < a.Wmos.Count; wi++)
        {
            var (mesh, world) = a.Wmos[wi];
            if (!_wmoBuffers.TryGetValue(mesh, out var buf)) continue;
            GL.Uniform1(U(_staticProg, "uHiMode"), hi == null ? 0 : wi < a.WmoUids.Count && hi.Wmos.Contains(a.WmoUids[wi]) ? 1 : 2);
            Mat(U(_staticProg, "uModel"), world);
            GL.BindVertexArray(buf.Vao);
            foreach (var b in mesh.Batches)
            {
                if (b.Blend != blendedPass || b.Count == 0) continue;
                GL.BindTexture(TextureTarget.Texture2D, b.Texture >= 0 ? _textures[b.Texture] : _grey);
                SetBlend(b.AlphaKey, b.Blend);
                GL.DrawElements(PrimitiveType.Triangles, b.Count, DrawElementsType.UnsignedInt, b.Start * 4);
            }
        }
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public RgbaImage RenderToImage(TerrainScene scene, OrbitCamera cam, int width, int height, int restoreFramebuffer) =>
        ModelRenderer.Offscreen(width, height, restoreFramebuffer, (w, h) => Render(scene, cam, w, h));

    public void Dispose()
    {
        if (!_init) return;
        FreeAsset();
        GlTracker.DeleteTexture(_grey);
        GlTracker.DeleteTexture(_white);
        GlTracker.DeleteProgram(_terrainProg);
        GlTracker.DeleteProgram(_staticProg);
        GlTracker.DeleteProgram(_colorProg);
        _init = false;
    }
}
