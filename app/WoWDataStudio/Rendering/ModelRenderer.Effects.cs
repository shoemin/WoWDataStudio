using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using WoWData.Core.M2;

namespace WoWDataStudio.Rendering;

/// <summary>
/// M3 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: the particle emitters and ribbons of a model
/// (<see cref="M2Model.Effects"/>: another version's, and your 1.12 game's own), simulated by
/// <see cref="M2EffectSimulation"/> and drawn after the model when the scene shows particles
/// (<see cref="ModelScene.ShowParticles"/>): camera-facing (or XY-plane) quads with their texture cell, colour and
/// opacity, 1.12 tails stretched back along the particle's movement, ribbons as bands of their edges. The blend modes are
/// the materials' numbering for both: 0 opaque, 1 alpha key, 2 alpha, 3 add without alpha, 4 add, 5 modulate,
/// 6 modulate 2x, and for another version 7 blend add (one, one minus source alpha; FORMAT KNOWLEDGE of the modern
/// client's blend table). The 1.12 client hands a particle's blend to the material blend state itself
/// (M2Scene_DrawParticleBatch @0070d8b0), so 1.12 particles use the same table.
/// </summary>
public sealed partial class ModelRenderer
{
    private int _fxProg, _fxVao, _fxVbo;
    private M2EffectSimulation? _fxSim;
    private ModelAsset? _fxAsset;
    private int _fxSequence = -1;
    private double _fxLastMs;
    private readonly List<float> _fxVerts = new();

    /// <summary>Seconds of effect simulated before the first picture of a model or animation (so a still picture shows
    /// the effect running, not empty).</summary>
    public const float EffectPreroll = 2f;

    /// <summary>The live particles and ribbon edges (statistics and tests).</summary>
    public int EffectParticles => _fxSim?.ParticleCount ?? 0;
    public int EffectEdges => _fxSim?.EdgeCount ?? 0;

    /// <summary>GL factors of a modern blend mode (materials, particles and ribbons of another version).</summary>
    public static (bool Enable, BlendingFactor Src, BlendingFactor Dst, float AlphaRef) ModernBlendFactors(int blend) => blend switch
    {
        0 => (false, BlendingFactor.One, BlendingFactor.Zero, -1f),
        1 => (false, BlendingFactor.One, BlendingFactor.Zero, 224f / 255f),
        2 => (true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, 1f / 255f),
        3 => (true, BlendingFactor.One, BlendingFactor.One, 1f / 255f),
        4 => (true, BlendingFactor.SrcAlpha, BlendingFactor.One, 1f / 255f),
        5 => (true, BlendingFactor.DstColor, BlendingFactor.Zero, 1f / 255f),
        6 => (true, BlendingFactor.DstColor, BlendingFactor.SrcColor, 1f / 255f),
        7 => (true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha, 1f / 255f),
        _ => (true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, 1f / 255f),
    };

    private void InitEffects()
    {
        if (_fxProg != 0) return;
        _fxProg = Program(Shaders.EffectVs, Shaders.EffectFs);
        _fxVao = GlTracker.GenVertexArray();
        _fxVbo = GlTracker.GenBuffer();
        GL.BindVertexArray(_fxVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _fxVbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 36, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 36, 12);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 36, 20);
        GL.BindVertexArray(0);
    }

    private void DisposeEffects()
    {
        GlTracker.DeleteBuffer(_fxVbo);
        GlTracker.DeleteVertexArray(_fxVao);
        GlTracker.DeleteProgram(_fxProg);
        _fxVbo = _fxVao = _fxProg = 0;
        _fxSim = null;
        _fxAsset = null;
    }

    /// <summary>Steps the simulation with the scene's clock (a new model or animation starts it again with a preroll)
    /// and draws the effects.</summary>
    private void DrawEffects(ModelScene scene, ModelAsset a, M2EffectSet fx, OrbitCamera cam, Matrix4x4 viewProj)
    {
        InitEffects();
        var time = scene.AnimTime;
        if (!ReferenceEquals(_fxAsset, a) || _fxSim == null || _fxSequence != scene.Sequence || scene.GlobalMs < _fxLastMs || scene.GlobalMs - _fxLastMs > 1000)
        {
            _fxSim = new M2EffectSimulation(fx, a.Model.GlobalSequences);
            _fxAsset = a;
            _fxSequence = scene.Sequence;
            for (float t = 0; t < EffectPreroll; t += 1f / 30f) _fxSim.Step(time, _bones, 1f / 30f);
        }
        else _fxSim.Step(time, _bones, (float)((scene.GlobalMs - _fxLastMs) / 1000.0));
        _fxLastMs = scene.GlobalMs;

        var (right, up, back) = cam.Basis();
        var m = a.Model;
        GL.UseProgram(_fxProg);
        Mat(U(_fxProg, "uViewProj"), viewProj);
        GL.Uniform1(U(_fxProg, "uTex"), 0);
        GL.BindVertexArray(_fxVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _fxVbo);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.ActiveTexture(TextureUnit.Texture0);
        for (int i = 0; i < fx.Particles.Count; i++)
        {
            var e = fx.Particles[i];
            var list = _fxSim.Particles(i);
            if (list.Count == 0) continue;
            _fxVerts.Clear();
            var bone = e.Bone < _bones.Length ? _bones[e.Bone] : Matrix4x4.Identity;
            var classic = e.Classic;
            Vector3 ax = right, ay = up;
            if (e.XyQuads)
            {
                // the emitter's XY plane; a 1.12 emitter's frame is its bone's turned 90 degrees about Z
                var frame = classic != null ? M2EffectSimulation.ClassicTurn * bone : bone;
                ax = Vector3.TransformNormal(Vector3.UnitX, frame);
                ay = Vector3.TransformNormal(Vector3.UnitY, frame);
                ax = ax.LengthSquared() > 1e-10f ? Vector3.Normalize(ax) : Vector3.UnitX;
                ay = ay.LengthSquared() > 1e-10f ? Vector3.Normalize(ay) : Vector3.UnitY;
            }
            // the 1.12 client only takes rows and columns that are powers of two (CParticleEmitter2_SetTextureTiling);
            // others keep its default of one cell
            int cols = Math.Max(1, e.Cols), rows = Math.Max(1, e.Rows);
            if (classic != null && (!IsPow2(cols) || !IsPow2(rows))) cols = rows = 1;
            float cw = 1f / cols, ch = 1f / rows;
            bool heads = classic?.Head ?? true, tails = classic?.Tail ?? false;
            var fwd = -back;
            foreach (var p in list)
            {
                var look = M2EffectSimulation.Appearance(e, p);
                var (color, size) = (look.Color, look.Size);
                if (!look.Visible || color.W <= 0.002f || size.X <= 0 && size.Y <= 0) continue;
                var pos = e.Local ? Vector3.Transform(p.Position, bone) : p.Position;
                if (heads)
                {
                    float cs = MathF.Cos(p.Rotation), sn = MathF.Sin(p.Rotation);
                    var rx = (ax * cs + ay * sn) * size.X;
                    var ry = (ay * cs - ax * sn) * size.Y;
                    float u0 = look.Cell % cols * cw, v0 = look.Cell / cols % rows * ch;
                    Quad(pos - rx - ry, pos + rx - ry, pos + rx + ry, pos - rx + ry, u0, v0, u0 + cw, v0 + ch, color);
                }
                if (tails)
                {
                    // CParticleEmitter2_BuildVertices: a band from the particle back along its movement for the tail
                    // length (seconds; growing with the age when flag 0x400 says so), its size wide across the screen;
                    // a tail shorter than 1/36 yard on screen is drawn as a quad
                    float len = e.TailLength;
                    if (classic!.TailGrows && p.Age < len) len = p.Age;
                    var vel = e.Local ? Vector3.TransformNormal(p.Velocity, bone) : p.Velocity;
                    var tail = -vel * len;
                    var flat = tail - fwd * Vector3.Dot(tail, fwd);
                    float tu = look.TailCell % cols * cw, tv = look.TailCell / cols % rows * ch;
                    if (flat.LengthSquared() < 1f / (36f * 36f))
                    {
                        var rx = right * size.X;
                        var ry = up * size.Y;
                        Quad(pos - rx - ry, pos + rx - ry, pos + rx + ry, pos - rx + ry, tu, tv, tu + cw, tv + ch, color);
                    }
                    else
                    {
                        var side = Vector3.Normalize(Vector3.Cross(fwd, flat)) * size.X;
                        Quad(pos - side, pos + side, pos + tail + side, pos + tail - side, tu, tv, tu + cw, tv + ch, color);
                    }
                }
            }
            DrawFx(scene, a, e.Textures.Length > 0 ? e.Textures[0] : -1, e.Blend);
        }
        for (int i = 0; i < fx.Ribbons.Count; i++)
        {
            var r = fx.Ribbons[i];
            var edges = _fxSim.Edges(i);
            if (edges.Count < 2) continue;
            _fxVerts.Clear();
            var c = M2EffectSimulation.Eval(r.Color, time, m.GlobalSequences, Vector3.One);
            float alpha = M2EffectSimulation.EvalFixed16(r.Alpha, time, m.GlobalSequences, 1f);
            float life = r.EdgeLifetime > 0 ? r.EdgeLifetime : 0.5f;
            // 1.12 (CRibbonEmitter_UpdateTexCoords): the texture slot picks a cell; U runs across the cell over an
            // edge's life, V from the bottom (cell top) to the top edge
            float cu = 0, cv = 0, cw = 1, ch = 1;
            if (r.Classic)
            {
                int rc = Math.Max(1, r.Cols), rr = Math.Max(1, r.Rows);
                int slot = M2EffectSimulation.Step(r.TextureSlot, time, m.GlobalSequences, (ushort)0);
                cw = 1f / rc;
                ch = 1f / rr;
                cu = slot % rc * cw;
                cv = slot / rc % rr * ch;
            }
            for (int k = 0; k + 1 < edges.Count; k++)
            {
                var e0 = edges[k];
                var e1 = edges[k + 1];
                float u0 = cu + Math.Clamp(e0.Age / life, 0, 1) * cw, u1 = cu + Math.Clamp(e1.Age / life, 0, 1) * cw;
                var col = new Vector4(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1), Math.Clamp(alpha, 0, 1));
                Quad(e0.Top, e1.Top, e1.Bottom, e0.Bottom, u0, cv, u1, cv + ch, col);
            }
            int blend = r.Classic ? r.Blend :
                r.Materials.Length > 0 && r.Materials[0] < (m.Modern?.MaterialBlends.Length ?? 0) ? m.Modern!.MaterialBlends[r.Materials[0]] : 4;
            DrawFx(scene, a, r.Textures.Length > 0 ? r.Textures[0] : -1, blend);
        }
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
    }

    private static bool IsPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>Two triangles (corners counter-clockwise) with the texture rectangle (u0, v0) - (u1, v1).</summary>
    private void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u0, float v0, float u1, float v1, Vector4 c)
    {
        void V(Vector3 p, float u, float v)
        {
            _fxVerts.Add(p.X); _fxVerts.Add(p.Y); _fxVerts.Add(p.Z); _fxVerts.Add(u); _fxVerts.Add(v);
            _fxVerts.Add(c.X); _fxVerts.Add(c.Y); _fxVerts.Add(c.Z); _fxVerts.Add(c.W);
        }
        V(p0, u0, v1); V(p1, u1, v1); V(p2, u1, v0);
        V(p0, u0, v1); V(p2, u1, v0); V(p3, u0, v0);
    }

    private void DrawFx(ModelScene scene, ModelAsset a, int texture, int blend)
    {
        if (_fxVerts.Count == 0) return;
        GL.BindTexture(TextureTarget.Texture2D, TextureFor(scene, a, texture));
        if (texture >= 0 && texture < a.Model.Textures.Length) Wrap(a.Model.Textures[texture]);
        var (en, src, dst, alphaRef) = ModernBlendFactors(blend);
        if (en)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(src, dst);
        }
        else GL.Disable(EnableCap.Blend);
        GL.Uniform1(U(_fxProg, "uAlphaRef"), alphaRef);
        var arr = _fxVerts.ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * 4, arr, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 9);
    }
}
