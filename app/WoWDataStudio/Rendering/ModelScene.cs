using System.Numerics;
using WoWData.Core.M2;

namespace WoWDataStudio.Rendering;

/// <summary>
/// What a viewport shows: the model asset, its current look (replaceable-slot textures, visible geosets), the level of
/// detail, the animation clock and the display options. Changing anything raises <see cref="Changed"/> so the viewport
/// redraws. Not thread-safe: use it on the UI thread.
/// </summary>
public sealed class ModelScene
{
    private ModelAsset? _asset;
    private ModelLook? _look;
    private HashSet<int>? _visibleGeosets;
    private int _view, _sequence;
    private double _seqTime, _globalMs;
    private bool _playing = true;
    private float _speed = 1f;
    private bool _wireframe, _bones, _bounds, _grid = true, _ground = true, _attachments, _loop = true;
    private Vector3 _background = new(0.17f, 0.19f, 0.22f);

    public event EventHandler? Changed;
    /// <summary>The asset or look changed (the renderer re-uploads buffers / textures).</summary>
    public int Version { get; private set; }

    private void Raise()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ModelAsset? Asset
    {
        get => _asset;
        set
        {
            _asset = value;
            _view = 0;
            _seqTime = 0;
            _sequence = value == null ? 0 : DefaultSequence(value.Model);
            EnsureKeys();
            Version++;
            Raise();
        }
    }

    /// <summary>M3: a sequence of another version's model whose keys are in an .anim file is loaded when it is chosen
    /// (once; <see cref="WoWData.Core.M2.Modern.ModernAnimations"/> keeps the keys within its budget). Your 1.12 game's
    /// models have no such sequences.</summary>
    private void EnsureKeys()
    {
        if (_asset?.Model.Modern?.Animations is { } ext) ext.Ensure(_sequence);
    }

    /// <summary>Swap in a rebuilt asset of the same model (new textures) keeping the animation and detail level.</summary>
    public void ReplaceAsset(ModelAsset asset)
    {
        _asset = asset;
        EnsureKeys();
        Version++;
        Raise();
    }

    public ModelLook? Look
    {
        get => _look;
        set
        {
            _look = value;
            Version++;
            Raise();
        }
    }

    /// <summary>Null = the look's geosets (or all).</summary>
    public HashSet<int>? VisibleGeosets { get => _visibleGeosets; set { _visibleGeosets = value; Raise(); } }

    public bool IsGeosetVisible(int id)
    {
        var set = _visibleGeosets ?? _look?.Geosets;
        return set == null || set.Contains(id);
    }

    public int ViewIndex { get => _view; set { _view = _asset == null ? 0 : Math.Clamp(value, 0, Math.Max(0, _asset.Views.Length - 1)); Raise(); } }

    public int Sequence
    {
        get => _sequence;
        set
        {
            _sequence = _asset == null || _asset.Model.Sequences.Length == 0 ? 0 : Math.Clamp(value, 0, _asset.Model.Sequences.Length - 1);
            EnsureKeys();
            _seqTime = 0;
            Raise();
        }
    }

    public M2Sequence? CurrentSequence => _asset != null && _sequence < _asset.Model.Sequences.Length ? _asset.Model.Sequences[_sequence] : null;
    public double Duration => CurrentSequence?.Duration ?? 0;

    /// <summary>Milliseconds from the start of the current sequence.</summary>
    public double SequenceTime { get => _seqTime; set { _seqTime = Math.Clamp(value, 0, Duration); Raise(); } }
    public double GlobalMs => _globalMs;
    public bool Playing { get => _playing; set { _playing = value; Raise(); } }
    public float Speed { get => _speed; set { _speed = Math.Clamp(value, 0.05f, 4f); Raise(); } }
    public bool Wireframe { get => _wireframe; set { _wireframe = value; Raise(); } }
    public bool ShowBones { get => _bones; set { _bones = value; Raise(); } }
    /// <summary>0.10.0: the attachment points (weapons, helm, spell hands...) moving with the animation.</summary>
    public bool ShowAttachments { get => _attachments; set { _attachments = value; Raise(); } }
    /// <summary>0.10.0: false = the animation plays once and stops on its last frame.</summary>
    public bool Loop { get => _loop; set { _loop = value; Raise(); } }
    public bool ShowBounds { get => _bounds; set { _bounds = value; Raise(); } }
    public bool ShowGrid { get => _grid; set { _grid = value; Raise(); } }
    public bool ShowGround { get => _ground; set { _ground = value; Raise(); } }
    public Vector3 Background { get => _background; set { _background = value; Raise(); } }

    /// <summary>True while something moves (an animation plays, or global sequences run).</summary>
    public bool NeedsContinuousRedraw => _asset != null && _playing && (Duration > 0 || _asset.Model.GlobalSequences.Length > 0 || _asset.Model.TextureTransforms.Length > 0 ||
                                                                       _asset.Model.Modern?.Effects != null);

    /// <summary>Advance the clock by real time (seconds); loops the sequence.</summary>
    public void Advance(double seconds)
    {
        if (!_playing || _asset == null) return;
        double ms = seconds * 1000.0 * _speed;
        _globalMs += ms;
        double d = Duration;
        if (d > 0)
        {
            _seqTime += ms;
            if (_seqTime > d)
            {
                if (_loop) _seqTime %= d;
                else
                {
                    _seqTime = d;
                    _playing = false;
                }
            }
        }
    }

    public M2Anim.Time AnimTime
    {
        get
        {
            var s = CurrentSequence;
            return new M2Anim.Time(_sequence, s?.Start ?? 0, (uint)Math.Max(0, Math.Round(_seqTime)), _globalMs);
        }
    }

    /// <summary>"Stand" (animation id 0, first variant) when the model has it, else the first sequence.</summary>
    public static int DefaultSequence(M2Model m)
    {
        for (int i = 0; i < m.Sequences.Length; i++)
            if (m.Sequences[i].AnimId == 0 && m.Sequences[i].SubId == 0) return i;
        for (int i = 0; i < m.Sequences.Length; i++)
            if (m.Sequences[i].AnimId == 0) return i;
        return 0;
    }

    public void Notify() => Raise();
}
