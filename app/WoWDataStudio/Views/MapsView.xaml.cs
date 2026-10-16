using System.Windows.Controls;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The Maps tab: map list, zones, minimap overview, tile details with the 3D terrain preview and the map
/// workflow (MapsViewModel).</summary>
public partial class MapsView : UserControl
{
    public MapsView()
    {
        InitializeComponent();
        Minimap.TileClicked += (x, y) => (DataContext as MapsViewModel)?.SelectTile(x, y);
        Minimap.PointClicked += (x, y) => (DataContext as MapsViewModel)?.PointPicked(x, y);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MapsViewModel old) { old.CenterRequested -= Center; old.ZoomRequested -= Zoom; }
            if (e.NewValue is MapsViewModel now) { now.CenterRequested += Center; now.ZoomRequested += Zoom; }
        };
    }

    private void Center(int x, int y) => Minimap.CenterOn(x, y);

    private void Zoom(double x, double y) => Minimap.ZoomOn(x, y, 260);

    /// <summary>Fit the overview into the view again (after the window or the picture changed size).</summary>
    public void FitMinimap() => Minimap.Fit();
}
