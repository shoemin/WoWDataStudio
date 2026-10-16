using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWDataStudio.Controls;
using WoWDataStudio.Helpers;

namespace WoWDataStudio.ViewModels;

public enum CompareMode { SideBySide, Wipe, Flicker, Difference }

/// <summary>
/// One side of a before/after texture comparison. Models have their own comparison since Phase 3b
/// (<see cref="ModelCompareViewModel"/>: two 3D viewports sharing one camera, with the same labels).
/// </summary>
public interface ICompareSide
{
    string Label { get; }
    string Description { get; }
    int LevelCount { get; }
    RgbaImage? Image(int level);
}

public sealed class TextureCompareSide : ICompareSide
{
    private readonly BlpFile? _blp;
    private readonly RgbaImage? _plain;
    private readonly Dictionary<int, RgbaImage> _cache = new();

    public TextureCompareSide(string label, byte[] blpData)
    {
        Label = label;
        _blp = BlpFile.Parse(blpData);
        Description = $"{_blp.FormatName}, {_blp.Width}x{_blp.Height}, {_blp.LevelCount} mip level{(_blp.LevelCount == 1 ? "" : "s")}";
    }

    /// <summary>A side from a plain image (e.g. the PNG before encoding).</summary>
    public TextureCompareSide(string label, RgbaImage image, string description)
    {
        Label = label;
        _plain = image;
        Description = description;
    }

    public string Label { get; }
    public string Description { get; }
    public int LevelCount => _blp?.LevelCount ?? 1;

    public RgbaImage? Image(int level)
    {
        if (_plain != null) return level == 0 ? _plain : null;
        if (_blp == null || level >= _blp.LevelCount) return null;
        if (!_cache.TryGetValue(level, out var img)) _cache[level] = img = _blp.Decode(level);
        return img;
    }
}

public sealed partial class CompareViewModel : ObservableObject, IDisposable
{
    public const string OriginalLabel = "Original (from game)";
    public const string EditLabel = "Your edit";

    public ICompareSide Left { get; }
    public ICompareSide Right { get; }
    public string Title { get; }
    public ViewState View { get; } = new();

    [ObservableProperty] private CompareMode _mode = CompareMode.SideBySide;
    [ObservableProperty] private AlphaView _alphaView = AlphaView.Checkerboard;
    [ObservableProperty] private int _mipLevel;
    [ObservableProperty] private ImageSource? _leftImage;
    [ObservableProperty] private ImageSource? _rightImage;
    [ObservableProperty] private ImageSource? _diffImage;
    [ObservableProperty] private string _diffSummary = "";
    [ObservableProperty] private double _changedPercent;
    /// <summary>"Colour changed in X % of pixels; alpha changed in Y %." (0.6.1: colour and alpha counted separately.)</summary>
    [ObservableProperty] private string _diffHeadline = "";
    public DiffStats? Stats { get; private set; }
    [ObservableProperty] private bool _flickerShowsOriginal;
    [ObservableProperty] private bool _autoFlicker;
    [ObservableProperty] private double _wipeFraction = 0.5;
    [ObservableProperty] private string _levelInfo = "";

    public List<int> Levels { get; }
    public bool Checkerboard => AlphaView == AlphaView.Checkerboard;
    public ImageSource? FlickerImage => FlickerShowsOriginal ? LeftImage : RightImage;
    public string FlickerLabel => FlickerShowsOriginal ? OriginalLabel : EditLabel;
    public string FlickerButtonText => FlickerShowsOriginal ? "Show your edit" : "Show the original";

    private readonly DispatcherTimer _timer;

    public CompareViewModel(string title, ICompareSide left, ICompareSide right)
    {
        Title = title;
        Left = left;
        Right = right;
        Levels = Enumerable.Range(0, Math.Max(1, Math.Min(left.LevelCount, right.LevelCount))).ToList();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => FlickerShowsOriginal = !FlickerShowsOriginal;
        Refresh();
    }

    partial void OnAlphaViewChanged(AlphaView value) { OnPropertyChanged(nameof(Checkerboard)); Refresh(); }
    partial void OnMipLevelChanged(int value) { View.FitPending = true; Refresh(); }
    partial void OnModeChanged(CompareMode value) { View.FitPending = true; if (value != CompareMode.Flicker) AutoFlicker = false; }
    partial void OnFlickerShowsOriginalChanged(bool value)
    {
        OnPropertyChanged(nameof(FlickerImage));
        OnPropertyChanged(nameof(FlickerLabel));
        OnPropertyChanged(nameof(FlickerButtonText));
    }
    partial void OnAutoFlickerChanged(bool value) { if (value) _timer.Start(); else _timer.Stop(); }

    [RelayCommand]
    private void ToggleFlicker() => FlickerShowsOriginal = !FlickerShowsOriginal;

    private void Refresh()
    {
        var a = Left.Image(MipLevel);
        var b = Right.Image(MipLevel);
        LeftImage = a == null ? null : ImageSources.ToBitmap(a, AlphaView);
        RightImage = b == null ? null : ImageSources.ToBitmap(b, AlphaView);
        OnPropertyChanged(nameof(FlickerImage));
        if (a != null && b != null && a.Width == b.Width && a.Height == b.Height)
        {
            bool alpha = AlphaView != AlphaView.IgnoreAlpha;
            var st = ImageCompare.Compare(a, b, alpha);
            DiffImage = ImageSources.ToBitmap(ImageCompare.Heatmap(a, b, alpha), AlphaView.IgnoreAlpha);
            Stats = st;
            ChangedPercent = st.ChangedPercent;
            DiffHeadline = st.Headline;
            DiffSummary = st.Summary + (st.PsnrDb is { } p ? $" Similarity (PSNR): {p:0.0} dB." : "") +
                          (alpha ? "" : " Alpha is not compared while \"Ignore alpha\" is chosen.");
        }
        else
        {
            DiffImage = null;
            Stats = null;
            ChangedPercent = 100;
            DiffHeadline = "Cannot compare pixel by pixel";
            DiffSummary = a != null && b != null
                ? $"The sizes differ ({a.Width}x{a.Height} vs {b.Width}x{b.Height}), so a pixel difference cannot be shown."
                : "One of the images has no data at this mip level.";
        }
        LevelInfo = a == null ? "" : $"Mip level {MipLevel}: {a.Width}x{a.Height}" + (MipLevel == 0 ? " (full size, what you edit)" : " (smaller copy the game uses at a distance)");
    }

    public void Dispose() => _timer.Stop();
}
