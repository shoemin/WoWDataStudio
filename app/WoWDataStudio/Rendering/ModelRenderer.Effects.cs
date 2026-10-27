using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using WoWData.Core.M2;

namespace WoWDataStudio.Rendering;

/// <summary>
/// M3 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: the particle emitters and ribbons of a model of another version
/// (<see cref="WoWData.Core.M2.Modern.M2ModernInfo.Effects"/>), simulated by <see cref="M2EffectSimulation"/> and drawn
/// after the model: camera-facing (or XY-plane) quads with their texture cell, colour and opacity, ribbons as bands of
/// their edges. A path of its own: models of your 1.12 game have no <c>Modern</c> info and never reach it, so their
/// drawing is unchanged. The blend modes are the modern numbering (the materials' and the particles'): 0 opaque,
/// 1 alpha key, 2 alpha, 3 add without alpha, 4 add, 5 modulate, 6 modulate 2x, 7 blend add (one, one minus source
/// alpha; FORMAT KNOWLEDGE of the modern client's blend table).
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

        var (right, up, _) = cam.Basis();
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
            Vector3 ax = right, ay = up;
            if (e.XyQuads)
            {
                ax = Vector3.TransformNormal(Vector3.UnitX, bone);
                ay = Vector3.TransformNormal(Vector3.UnitY, bone);
                ax = ax.LengthSquared() > 1e-10f ? Vector3.Normalize(ax) : Vector3.UnitX;
                ay = ay.LengthSquared() > 1e-10f ? Vector3.Normalize(ay) : Vector3.UnitY;
            }
            float cw = 1f / Math.Max(1, e.Cols), ch = 1f / Math.Max(1, e.Rows);
            foreach (var p in list)
            {
                var (color, size, cell) = M2EffectSimulation.Look(e, p);
                if (color.W <= 0.002f || size.X <= 0 && size.Y <= 0) continue;
                var pos = e.Local ? Vector3.Transform(p.Position, bone) : p.Position;
                float cs = MathF.Cos(p.Rotation), sn = MathF.Sin(p.Rotation);
                var rx = (ax * cs + ay * sn) * size.X;
                var ry = (ay * cs - ax * sn) * size.Y;
                float u0 = cell % Math.Max(1, e.Cols) * cw, v0 = cell / Math.Max(1, e.Cols) * ch;
                Quad(pos - rx - ry, pos + rx - ry, pos + rx + ry, pos - rx + ry, u0, v0, u0 + cw, v0 + ch, color);
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
            for (int k = 0; k + 1 < edges.Count; k++)
            {
                var e0 = edges[k];
                var e1 = edges[k + 1];
                float u0 = Math.Clamp(e0.Age / life, 0, 1), u1 = Math.Clamp(e1.Age / life, 0, 1);
                var col = new Vector4(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1), Math.Clamp(alpha, 0, 1));
                Quad(e0.Top, e1.Top, e1.Bottom, e0.Bottom, u0, 0, u1, 1, col);
            }
            int blend = r.Materials.Length > 0 && r.Materials[0] < (m.Modern?.MaterialBlends.Length ?? 0) ? m.Modern!.MaterialBlends[r.Materials[0]] : 4;
            DrawFx(scene, a, r.Textures.Length > 0 ? r.Textures[0] : -1, blend);
        }
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
    }

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
