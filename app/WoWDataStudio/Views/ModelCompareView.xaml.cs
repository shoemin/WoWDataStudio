using System.Windows.Controls;
using WoWData.Core.Imaging;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>Two synchronized 3D views of a <see cref="ModelCompareViewModel"/> (side by side, or toggled in place).
/// Reusable: Phase 3c puts it into the review screen (original vs. edited).</summary>
public partial class ModelCompareView : UserControl
{
    public ModelCompareView()
    {
        InitializeComponent();
        LeftView.Rendered += (_, _) => (DataContext as ModelCompareViewModel)?.FollowLeft();
    }

    /// <summary>Offscreen renders of both sides at the given size (screenshots, tests).</summary>
    public async Task<(RgbaImage Left, RgbaImage Right)> CaptureBothAsync(int width, int height) =>
        (await LeftView.CaptureAsync(width, height), await RightView.CaptureAsync(width, height));
}
