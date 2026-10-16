using System.IO;
using System.Windows;
using WoWData.Core.ModelIO;
using WoWDataStudio.Services;

namespace WoWDataStudio.Views;

/// <summary>"Export for Blender": which skins, animations, the package folder, open in Blender afterwards.</summary>
public partial class ModelExportWindow : Window
{
    private readonly string _modelName;

    public ModelExportMode Mode => ModeVariants.IsChecked == true ? ModelExportMode.AllVariants : ModePerSkin.IsChecked == true ? ModelExportMode.OnePerVariant : ModelExportMode.CurrentLook;
    public bool IncludeAnimations => AnimationsBox.IsChecked == true || IncludeSkeleton;
    /// <summary>0.10.0: "Skeleton and animations (for editing)".</summary>
    public bool IncludeSkeleton => SkeletonBox.IsChecked == true;
    public bool OpenInBlender => OpenBlenderBox.IsChecked == true && OpenBlenderBox.IsEnabled;
    public string Folder => FolderBox.Text.Trim();

    /// <param name="skins">How many different skins the model has (1 = the modes with several skins are off).</param>
    public ModelExportWindow(string modelPath, string currentLook, int skins, string users, string? warning, string folder, bool blenderFound, bool isCharacter = false)
    {
        InitializeComponent();
        SkeletonBox.Checked += (_, _) => AnimationsBox.IsChecked = true;
        if (isCharacter)
            SkeletonText.Text += " This is a character model: every player and NPC of its race and sex uses this skeleton and its animations, and the game finds the head, hands, weapons and the camera through its key bones and attachment points. Keep them, and test any change in game.";
        _modelName = Path.GetFileNameWithoutExtension(modelPath);
        ModelText.Text = $"{modelPath}  ·  shown now: {currentLook}  ·  used by {users}.";
        CurrentText.Text = $"One file with the skin shown in the app ({currentLook}). The simplest choice.";
        if (skins <= 1)
        {
            ModeVariants.IsEnabled = ModePerSkin.IsEnabled = false;
            VariantsText.Text = "This model has only one look here, so there is nothing to switch.";
        }
        else VariantsText.Text = $"One file with all {skins} skins as glTF material variants. In Blender, open the sidebar (N) of the 3D view, tab glTF Variants, and click a skin to show it. Paint any of them.";
        FolderBox.Text = folder;
        FolderBox.TextChanged += (_, _) => UpdatePackage();
        UpdatePackage();
        OpenBlenderBox.IsEnabled = blenderFound;
        if (!blenderFound) OpenBlenderBox.IsChecked = false;
        BlenderText.Text = blenderFound
            ? "Blender 4.5 starts with the model imported by the WoW add-on (the app's copy is used if the add-on is not installed in Blender)."
            : "Blender was not found on this computer. Install Blender 4.5 LTS (blender.org or the Microsoft Store), or open the .gltf in Blender yourself.";
        if (!string.IsNullOrEmpty(warning))
        {
            WarnText.Text = warning + " Exporting is always safe: nothing changes until you import an edit and keep it.";
            WarnBox.Visibility = Visibility.Visible;
        }
    }

    private void UpdatePackage() =>
        PackageText.Text = $"The package is the folder {_modelName}_export (a new one with a number if it exists): {_modelName}.gltf + .bin, textures\\*.png and wowdata_manifest.json. Keep the files together.";

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var d = Dialogs.PickFolder("Where to save the export package", Folder);
        if (d != null) FolderBox.Text = d;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Folder.Length == 0)
        {
            Dialogs.Error("Choose a folder", "Choose where to save the export package.");
            return;
        }
        DialogResult = true;
    }
}
