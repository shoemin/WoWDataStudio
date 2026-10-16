using System.Numerics;

namespace WoWDataStudio.Rendering;

/// <summary>How the mouse and keys move the camera: around the target (models), or flying through the scene (terrain).</summary>
public enum CameraNavigation { Orbit, Fly }

/// <summary>The fixed views of the camera bar (and the keys 1, 2, 3).</summary>
public enum CameraPreset { Front, Side, Top, Reset }

/// <summary>
/// An orbit camera around a target point, in M2 coordinates (Z up, yards; models face +X). One instance can be shared by
/// several viewports: they then show the same view (the synchronized cameras of the compare view).
/// The same camera also flies (0.5.1): <see cref="Look"/> turns the view around the eye, <see cref="Move"/> moves eye and
/// target together; the target then stays a short "focus distance" in front of the eye, which keeps the near plane close.
/// </summary>
public sealed class OrbitCamera
{
    private float _yaw, _pitch, _distance = 5;
    private Vector3 _target;

    public event EventHandler? Changed;

    /// <summary>Degrees around Z; 0 looks at the model's front (+X side).</summary>
    public float Yaw { get => _yaw; set { _yaw = Wrap(value); Raise(); } }
    /// <summary>Degrees above the horizon (-89..89).</summary>
    public float Pitch { get => _pitch; set { _pitch = Math.Clamp(value, -89f, 89f); Raise(); } }
    public float Distance { get => _distance; set { _distance = Math.Clamp(value, MinDistance, MaxDistance); Raise(); } }
    public Vector3 Target { get => _target; set { _target = value; Raise(); } }
    public float FieldOfView { get; set; } = 45f;
    public float MinDistance { get; private set; } = 0.05f;
    public float MaxDistance { get; private set; } = 5000f;

    /// <summary>The framing chosen by the last <see cref="Frame"/> call ("Reset view" returns to it).</summary>
    public (Vector3 Target, float Distance) Home { get; private set; }
    /// <summary>The direction of the start view (degrees): a 3/4 front view for models, set by the terrain preview to a
    /// view from the south-east, from above.</summary>
    public float HomeYaw { get; set; } = 25f;
    public float HomePitch { get; set; } = 12f;
    /// <summary>The yaw of the "Front" preset: 0 for models (they face +X), 180 for terrain (from the south, north up).</summary>
    public float FrontYaw { get; set; }
    /// <summary>Half the diagonal of the framed box (yards): sets the far plane and the default fly speed.</summary>
    public float SceneRadius { get; private set; } = 1f;
    private float _flySpeed = 2f;
    /// <summary>Fly speed in yards per second (the mouse wheel changes it in fly mode).</summary>
    public float FlySpeed { get => _flySpeed; set { _flySpeed = Math.Clamp(value, 0.1f, 5000f); Raise(); } }
    /// <summary>The default fly speed for the framed scene: about a seventh of its size per second.</summary>
    public float DefaultFlySpeed => Math.Clamp(SceneRadius * 0.15f, 1f, 400f);

    private static float Wrap(float a)
    {
        a %= 360f;
        return a < 0 ? a + 360f : a;
    }

    private bool _batch;

    private void Raise()
    {
        if (!_batch) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Frame a bounding box: target its centre, a 3/4 front view from slightly above, the whole box visible.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        float radius = Math.Max(0.05f, (max - min).Length() * 0.5f);
        float dist = radius / MathF.Sin(FieldOfView * 0.5f * MathF.PI / 180f) * 0.9f;
        MinDistance = Math.Max(0.01f, radius * 0.05f);
        MaxDistance = Math.Max(50f, radius * 60f);
        SceneRadius = radius;
        _flySpeed = DefaultFlySpeed;
        Home = (center, dist);
        Set(HomeYaw, HomePitch, dist, center);
    }

    public void ResetView() => Set(HomeYaw, HomePitch, Home.Distance, Home.Target);

    /// <summary>A fixed view of the framed selection: front, side (the model's left, or terrain from the east), from the
    /// top (north up for terrain), or the start view.</summary>
    public void Preset(CameraPreset p)
    {
        switch (p)
        {
            case CameraPreset.Front: Set(FrontYaw, 0f, Home.Distance, Home.Target); break;
            case CameraPreset.Side: Set(FrontYaw + 90f, 0f, Home.Distance, Home.Target); break;
            case CameraPreset.Top: Set(FrontYaw, 89f, Home.Distance, Home.Target); break;
            default: ResetView(); break;
        }
    }

    /// <summary>"Focus on selection" (F): look at the framed selection from the current direction.</summary>
    public void Focus() => Set(_yaw, _pitch, Home.Distance, Home.Target);

    /// <summary>The fly mode's focus distance: short, so the near plane is close to the eye.</summary>
    public float FlyFocusDistance => Math.Max(MinDistance, SceneRadius * 0.08f);

    /// <summary>Keep the eye where it is and put the target <paramref name="distance"/> in front of it (entering fly mode).</summary>
    public void SetDistanceKeepEye(float distance)
    {
        var eye = Eye;
        float d = Math.Clamp(distance, MinDistance, MaxDistance);
        var back = Vector3.Normalize(eye - _target);
        if (!float.IsFinite(back.X)) return;
        Set(_yaw, _pitch, d, eye - back * d);
    }

    /// <summary>Mouse look: turn the view around the eye (degrees; positive yaw turns left, positive pitch looks down).</summary>
    public void Look(float dYaw, float dPitch)
    {
        var eye = Eye;
        _batch = true;
        Yaw += dYaw;
        Pitch += dPitch;
        float y = _yaw * MathF.PI / 180f, p = _pitch * MathF.PI / 180f;
        Target = eye - new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p)) * _distance;
        _batch = false;
        Raise();
    }

    /// <summary>Move eye and target together (world units).</summary>
    public void Move(Vector3 delta)
    {
        if (delta == Vector3.Zero) return;
        Target = _target + delta;
    }

    /// <summary>The viewing direction (unit vector from the eye to the target).</summary>
    public Vector3 Forward => -Basis().Back;

    public void Set(float yaw, float pitch, float distance, Vector3 target)
    {
        _batch = true;
        Yaw = yaw;
        Pitch = pitch;
        Distance = distance;
        Target = target;
        _batch = false;
        Raise();
    }

    public void CopyFrom(OrbitCamera other) => Set(other.Yaw, other.Pitch, other.Distance, other.Target);

    public void Orbit(float dYaw, float dPitch)
    {
        _batch = true;
        Yaw -= dYaw;
        Pitch += dPitch;
        _batch = false;
        Raise();
    }

    public void Zoom(float factor) => Distance = Distance * factor;

    /// <summary>Move the target in the view plane by a fraction of the visible height.</summary>
    public void Pan(float dxFraction, float dyFraction)
    {
        var (right, up, _) = Basis();
        float h = 2f * Distance * MathF.Tan(FieldOfView * 0.5f * MathF.PI / 180f);
        Target = _target - right * (dxFraction * h) + up * (dyFraction * h);
    }

    public Vector3 Eye
    {
        get
        {
            float y = _yaw * MathF.PI / 180f, p = _pitch * MathF.PI / 180f;
            return _target + new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p)) * _distance;
        }
    }

    /// <summary>(right, up, back) unit vectors of the camera in model space; back points from the target to the eye.</summary>
    public (Vector3 Right, Vector3 Up, Vector3 Back) Basis()
    {
        var back = Vector3.Normalize(Eye - _target);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, back));
        if (!float.IsFinite(right.X)) right = Vector3.UnitY;
        var up = Vector3.Cross(back, right);
        return (right, up, back);
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, _target, Vector3.UnitZ);

    /// <summary>OpenGL projection (clip z in -w..w), row-vector convention like System.Numerics.</summary>
    public Matrix4x4 Projection(float aspect)
    {
        float near = Math.Max(0.01f, _distance * 0.01f), far = Math.Max(100f, Math.Max(_distance * 20f + 50f, _distance + SceneRadius * 5f));
        float f = 1f / MathF.Tan(FieldOfView * 0.5f * MathF.PI / 180f);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
    }
}
