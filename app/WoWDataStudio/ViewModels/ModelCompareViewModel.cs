using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.M2;
using WoWDataStudio.Rendering;

namespace WoWDataStudio.ViewModels;

public enum ModelCompareMode { SideBySide, Toggle }

/// <summary>0.10.0: one animation of the comparison: its name and its sequence on each side (-1: that side does not
/// have it, e.g. a new animation of your edit).</summary>
public sealed class CompareSequence
{
    public string Label { get; init; } = "";
    public int Left { get; init; } = -1;
    public int Right { get; init; } = -1;
    public override string ToString() => Label;
}

/// <summary>One side of a model comparison: a label ("Original (from game)", "Your edit") and its own scene.</summary>
public sealed class ModelCompareSide
{
    public string Label { get; }
    public string Detail { get; }
    public ModelScene Scene { get; } = new();

    public ModelCompareSide(string label, ModelAsset asset, ModelLook? look, string detail = "")
    {
        Label = label;
        Detail = detail;
        Scene.Asset = asset;
        Scene.Look = look;
    }
}

/// <summary>
/// Two models side by side (or toggled in place) with ONE shared <see cref="OrbitCamera"/>, so turning, moving or
/// zooming either view moves both. The animation of the right side follows the left (same animation id and time) when
/// <see cref="SyncAnimation"/> is on. Phase 3c uses it for "original vs. edited" in the review screen; the demo entry
/// compares two skins of one model.
/// </summary>
public sealed partial class ModelCompareViewModel : ObservableObject
{
    public string Title { get; }
    /// <summary>The line under the title.</summary>
    public string Subtitle { get; init; } = "Both views share one camera: turn, move or zoom either one.";
    public ModelCompareSide Left { get; }
    public ModelCompareSide Right { get; }
    public OrbitCamera Camera { get; } = new();

    [ObservableProperty] private ModelCompareMode _mode = ModelCompareMode.SideBySide;
    [ObservableProperty] private bool _showingLeft;
    [ObservableProperty] private bool _syncAnimation = true;
    [ObservableProperty] private bool _playing = true;
    /// <summary>0.10.0: the animations of both sides (pick one to play it on both).</summary>
    public List<CompareSequence> Sequences { get; } = new();
    [ObservableProperty] private CompareSequence? _selectedSequence;
    [ObservableProperty] private double _timeMs;
    [ObservableProperty] private double _durationMs;
    public bool HasSequences => Sequences.Count > 0;
    public string TimeText => $"{TimeMs / 1000.0:0.00} / {DurationMs / 1000.0:0.00} s";
    private bool _syncingTime;

    public bool IsSideBySide => Mode == ModelCompareMode.SideBySide;
    public bool IsToggle => Mode == ModelCompareMode.Toggle;
    public ModelCompareSide Shown => ShowingLeft ? Left : Right;
    public string ToggleText => ShowingLeft ? $"Showing: {Left.Label}" : $"Showing: {Right.Label}";

    public ModelCompareViewModel(string title, ModelCompareSide left, ModelCompareSide right)
    {
        Title = title;
        Left = left;
        Right = right;
        var a = left.Scene.Asset!;
        var b = right.Scene.Asset ?? a;
        Camera.Frame(System.Numerics.Vector3.Min(a.BoundsMin, b.BoundsMin), System.Numerics.Vector3.Max(a.BoundsMax, b.BoundsMax));
    }

    partial void OnModeChanged(ModelCompareMode value)
    {
        OnPropertyChanged(nameof(IsSideBySide));
        OnPropertyChanged(nameof(IsToggle));
        if (value == ModelCompareMode.SideBySide) Right.Scene.Playing = !SyncAnimation && Playing;
        else if (!ShowingLeft) Right.Scene.Playing = Playing;
    }

    partial void OnShowingLeftChanged(bool value)
    {
        if (IsToggle)
        {
            // the toggle view only runs the clock of the side it shows: hand the animation over so the switch is seamless
            if (value) Copy(Right.Scene, Left.Scene);
            else
            {
                Copy(Left.Scene, Right.Scene);
                Right.Scene.Playing = Playing;
            }
        }
        OnPropertyChanged(nameof(Shown));
        OnPropertyChanged(nameof(ToggleText));
    }

    private static void Copy(ModelScene from, ModelScene to)
    {
        if (from.CurrentSequence is not { } fs || to.Asset == null) return;
        int found = Array.FindIndex(to.Asset.Model.Sequences, s => s.AnimId == fs.AnimId && s.SubId == fs.SubId);
        if (found >= 0 && found != to.Sequence) to.Sequence = found;
        to.SequenceTime = Math.Min(from.SequenceTime, to.Duration);
    }

    partial void OnPlayingChanged(bool value)
    {
        Left.Scene.Playing = value;
        Right.Scene.Playing = value;
    }

    partial void OnSyncAnimationChanged(bool value)
    {
        if (!value) Right.Scene.Playing = Playing;
    }

    /// <summary>Fill the animation list: the sequences of the right side paired with the left side's by animation and
    /// variation ("Stand [2]" with "Stand [2]").</summary>
    public void InitSequences(Func<int, string> animName)
    {
        Sequences.Clear();
        var l = Left.Scene.Asset?.Model.Sequences ?? Array.Empty<WoWData.Core.M2.M2Sequence>();
        var r = Right.Scene.Asset?.Model.Sequences ?? Array.Empty<WoWData.Core.M2.M2Sequence>();
        static List<(int Index, int Anim, int Var)> Keys(WoWData.Core.M2.M2Sequence[] s)
        {
            var rank = new Dictionary<int, int>();
            var list = new List<(int, int, int)>();
            for (int i = 0; i < s.Length; i++)
            {
                int v = rank.GetValueOrDefault(s[i].AnimId);
                rank[s[i].AnimId] = v + 1;
                list.Add((i, s[i].AnimId, v));
            }
            return list;
        }
        var lk = Keys(l);
        var used = new HashSet<int>();
        foreach (var (ri, anim, v) in Keys(r))
        {
            var match = lk.FirstOrDefault(x => x.Anim == anim && x.Var == v);
            int li = lk.Contains(match) && match.Anim == anim && match.Var == v ? match.Index : -1;
            if (li >= 0) used.Add(li);
            string name = WoWData.Core.M2.RigInfo.SequenceName(animName(anim), v);
            Sequences.Add(new CompareSequence { Label = li < 0 ? name + "  (new in your edit)" : name, Left = li, Right = ri });
        }
        foreach (var (li, anim, v) in lk.Where(x => !used.Contains(x.Index)))
            Sequences.Add(new CompareSequence { Label = WoWData.Core.M2.RigInfo.SequenceName(animName(anim), v) + "  (removed in your edit)", Left = li, Right = -1 });
        OnPropertyChanged(nameof(HasSequences));
        _syncingTime = true;
        SelectedSequence = Sequences.FirstOrDefault(s => s.Left == Left.Scene.Sequence) ?? Sequences.FirstOrDefault();
        _syncingTime = false;
        ApplySequence();
    }

    partial void OnSelectedSequenceChanged(CompareSequence? value)
    {
        if (!_syncingTime) ApplySequence();
    }

    private void ApplySequence()
    {
        var s = SelectedSequence;
        if (s == null) return;
        if (s.Left >= 0 && s.Left != Left.Scene.Sequence) Left.Scene.Sequence = s.Left;
        if (s.Right >= 0 && s.Right != Right.Scene.Sequence) Right.Scene.Sequence = s.Right;
        Right.Scene.Playing = Playing && (s.Left < 0 || !SyncAnimation);
        Left.Scene.Playing = Playing;
        DurationMs = Math.Max(s.Left >= 0 ? Left.Scene.Duration : 0, s.Right >= 0 ? Right.Scene.Duration : 0);
    }

    partial void OnTimeMsChanged(double value)
    {
        OnPropertyChanged(nameof(TimeText));
        if (_syncingTime) return;
        Playing = false;
        Left.Scene.SequenceTime = Math.Min(value, Left.Scene.Duration);
        Right.Scene.SequenceTime = Math.Min(value, Right.Scene.Duration);
    }

    partial void OnDurationMsChanged(double value) => OnPropertyChanged(nameof(TimeText));

    [RelayCommand]
    private void Toggle() => ShowingLeft = !ShowingLeft;

    [RelayCommand]
    private void ResetView() => Camera.ResetView();

    /// <summary>Called after the left view rendered a frame: the right side follows its animation.</summary>
    public void FollowLeft()
    {
        var sel = SelectedSequence;
        if (sel != null)
        {
            // the timeline follows the side that plays the chosen animation
            var driver = sel.Left >= 0 ? Left.Scene : Right.Scene;
            if (Math.Abs(driver.SequenceTime - TimeMs) >= 10 || driver.SequenceTime == 0 && TimeMs != 0)
            {
                _syncingTime = true;
                TimeMs = driver.SequenceTime;
                _syncingTime = false;
            }
            if (sel.Left < 0 || sel.Right < 0) return;      // only one side has it: it plays on its own
        }
        if (!SyncAnimation || !IsSideBySide) return;
        var l = Left.Scene;
        var r = Right.Scene;
        if (l.Asset == null || r.Asset == null || l.CurrentSequence == null) return;
        var ls = l.CurrentSequence;
        if (sel != null && sel.Right >= 0)
        {
            if (r.Sequence != sel.Right) r.Sequence = sel.Right;
            r.Playing = false;
            if (Math.Abs(r.SequenceTime - l.SequenceTime) > 0.5) r.SequenceTime = Math.Min(l.SequenceTime, r.Duration);
            return;
        }
        int target = r.Sequence;
        var rs = r.Asset.Model.Sequences;
        if (target >= rs.Length || rs[target].AnimId != ls.AnimId || rs[target].SubId != ls.SubId)
        {
            int found = Array.FindIndex(rs, s => s.AnimId == ls.AnimId && s.SubId == ls.SubId);
            if (found >= 0) r.Sequence = found;
        }
        r.Playing = false;    // driven by the left clock
        if (Math.Abs(r.SequenceTime - l.SequenceTime) > 0.5) r.SequenceTime = Math.Min(l.SequenceTime, r.Duration);
    }
}
