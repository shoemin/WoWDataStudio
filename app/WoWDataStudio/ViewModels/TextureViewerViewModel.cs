using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWDataStudio.Controls;
using WoWDataStudio.Helpers;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>The texture viewer of the details panel: the original or the edited version, a mip level, alpha options and the
/// format facts.</summary>
public sealed partial class TextureViewerViewModel : ObservableObject
{
    private readonly AppSession _session;
    private BlpFile? _original, _edited;
    private string? _originalArchive;
    private int _loadToken;

    public ViewState View { get; } = new();

    [ObservableProperty] private string? _path;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private bool _showEdited = true;
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private int _mipLevel;
    [ObservableProperty] private List<int> _levels = new() { 0 };
    [ObservableProperty] private AlphaView _alphaView = AlphaView.Checkerboard;
    [ObservableProperty] private string _formatInfo = "";
    [ObservableProperty] private string _sizeInfo = "";
    [ObservableProperty] private string _sourceInfo = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _showingLabel = "";

    public bool Checkerboard => AlphaView == AlphaView.Checkerboard;
    public bool HasOriginal => _original != null;

    public TextureViewerViewModel(AppSession session) { _session = session; }

    partial void OnAlphaViewChanged(AlphaView value) { OnPropertyChanged(nameof(Checkerboard)); Render(); }
    partial void OnMipLevelChanged(int value) => Render();
    partial void OnShowEditedChanged(bool value) => Render();

    public void Clear()
    {
        _loadToken++;
        Path = null;
        IsLoaded = false;
        Image = null;
        Error = "";
        FormatInfo = SizeInfo = SourceInfo = ShowingLabel = "";
    }

    public async Task LoadAsync(string path)
    {
        int token = ++_loadToken;
        Path = path;
        Error = "";
        var ts = _session.Textures;
        var ws = _session.Workspace;
        if (ts == null || ws == null) return;
        try
        {
            var (orig, origArchive, edited) = await Task.Run(() =>
            {
                var o = ts.ReadOriginal(path);
                var e = ws.ReadEdited(path);
                return (o == null ? null : BlpFile.Parse(o.Value.Data), o?.Archive, e == null ? null : BlpFile.Parse(e));
            });
            if (token != _loadToken) return;
            _original = orig;
            _originalArchive = origArchive;
            _edited = edited;
            IsModified = edited != null;
            ShowEdited = edited != null;
            View.FitPending = true;
            MipLevel = 0;
            IsLoaded = true;
            OnPropertyChanged(nameof(HasOriginal));
            Render();
        }
        catch (Exception e) when (e is BlpException or IOException or WoWData.Core.Mpq.MpqException)
        {
            if (token != _loadToken) return;
            IsLoaded = false;
            Image = null;
            Error = $"This texture could not be shown: {e.Message}";
        }
    }

    private void Render()
    {
        var blp = ShowEdited && _edited != null ? _edited : _original ?? _edited;
        if (blp == null)
        {
            Image = null;
            FormatInfo = SizeInfo = "";
            SourceInfo = "Not in the game archives.";
            return;
        }
        Levels = Enumerable.Range(0, blp.LevelCount).ToList();
        int lv = Math.Clamp(MipLevel, 0, blp.LevelCount - 1);
        RgbaImage img;
        try { img = blp.Decode(lv); }
        catch (BlpException e) { Error = e.Message; return; }
        Image = ImageSources.ToBitmap(img, AlphaView);
        FormatInfo = blp.FormatName;
        SizeInfo = $"{blp.Width} x {blp.Height} pixels · {blp.LevelCount} mip level{(blp.LevelCount == 1 ? "" : "s")} · {blp.Data.Length / 1024.0:0.#} KB" + (lv > 0 ? $" · showing level {lv} ({img.Width} x {img.Height})" : "");
        bool showingEdit = blp == _edited;
        ShowingLabel = showingEdit ? "Showing: your edit" : IsModified ? "Showing: original (from game)" : "Original (from game)";
        SourceInfo = showingEdit ? "From your workspace (edited)" : $"From {_originalArchive}";
    }
}
