using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

/// <summary>Phase 3c: model export / import through Blender (the model actions under the 3D view, File > Import edited
/// model..., Tools > Install the Blender add-on...).</summary>
public sealed partial class BrowserViewModel
{
    public AppSession Session => _session;

    public string ModelsExportDir => Path.Combine(_session.Settings.DefaultExportDir, "Models");

    /// <summary>A free package folder: &lt;dir&gt;\&lt;Model&gt;_export, or _export_2, _3... when it exists.</summary>
    public static string NewPackageFolder(string dir, string model) => ModelPackageService.NewPackageFolder(dir, model);

    private string CurrentLookName()
    {
        var mv = ModelViewer;
        if (mv.Subject == ViewerSubject.Character) return $"skin {mv.SkinColor}, face {mv.Face}, hair {mv.HairStyle}/{mv.HairColor}, facial hair {mv.FacialHair}";
        return mv.SelectedVariant?.Label ?? Path.GetFileNameWithoutExtension(mv.ModelPath);
    }

    public async Task<ModelExportResult?> ExportAsync(ModelExportMode mode, string parentDir, bool animations, bool skeleton = false)
    {
        var svc = _session.ModelPackages!;
        var model = ModelViewer.ModelPath;
        var (subject, display, look, geosets, npc) = ModelViewer.ExportContext();
        var folder = NewPackageFolder(parentDir, model);
        return await _main.RunBusyAsync("Exporting the model for Blender...", () => Task.Run(() =>
        {
            Directory.CreateDirectory(parentDir);
            return svc.Export(model, subject, mode, parentDir, display, look, geosets, animations, npc, folder, skeleton);
        }));
    }

    [RelayCommand]
    private async Task ExportModel()
    {
        var svc = _session.ModelPackages;
        if (svc == null || !ModelViewer.HasModel) return;
        var model = ModelViewer.ModelPath;
        var (subject, _, _, _, _) = ModelViewer.ExportContext();
        int skins;
        string users;
        string? warning;
        try
        {
            (skins, users, warning) = await Task.Run(() =>
            {
                var (looks, _) = svc.Looks(model, subject);
                int n = looks.Select(l => string.Join("|", l.Look.Slots.OrderBy(kv => kv.Key).Select(kv => kv.Value.Path ?? kv.Value.Source))).Distinct().Count();
                var u = svc.Users(model, subject);
                return (n, u.Summary, u.Warning);
            });
        }
        catch (Exception e) when (e is IOException or M2Exception)
        {
            Dialogs.Error("Could not export the model", e.Message);
            return;
        }
        var blender = BlenderTools.FindBlender();
        var win = new ModelExportWindow(model, CurrentLookName(), subject == ModelSubject.Character ? 1 : skins, users, warning, ModelsExportDir, blender != null, subject == ModelSubject.Character)
        {
            Owner = Application.Current.MainWindow,
        };
        if (win.ShowDialog() != true) return;
        try
        {
            var res = await ExportAsync(win.Mode, win.Folder, win.IncludeAnimations, win.IncludeSkeleton);
            if (res == null) return;
            _main.Toast($"Exported {Path.GetFileName(res.Folder)}: {res.GltfFiles.Count} glTF file{(res.GltfFiles.Count == 1 ? "" : "s")}, {res.TextureCount} textures. Edit it in Blender, then \"Import edited model...\".");
            if (win.OpenInBlender && blender != null) OpenInBlender(blender, res.GltfFiles[0]);
            else
            {
                int pick = Dialogs.Show("Exported for Blender",
                    $"The package is in\n{res.Folder}\n\n{res.GltfFiles.Count} glTF file(s), {res.TextureCount} PNG textures and wowdata_manifest.json. " +
                    "Open the .gltf in Blender with File > Import > WoW 1.12 model (the add-on), edit, then File > Export > WoW 1.12 model for WoW Data Studio. " +
                    "You can also just edit the PNGs in any image editor. Then use \"Import edited model...\" here." +
                    (res.Notes.Count > 0 ? "\n\nNotes: " + string.Join("; ", res.Notes) : ""),
                    DialogKind.Success, blender != null ? new[] { "Open the folder", "Open in Blender", "OK" } : new[] { "Open the folder", "OK" });
                if (pick == 0) Dialogs.OpenPath(res.Folder);
                else if (pick == 1 && blender != null) OpenInBlender(blender, res.GltfFiles[0]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or M2Exception or GltfException or InvalidOperationException)
        {
            Dialogs.Error("Could not export the model", e.Message);
        }
    }

    /// <summary>"Open in Blender" on a doodad of the tile breakdown: export the model with its default look (plain
    /// world model) and start Blender with it, like the Browse tab's button.</summary>
    public async Task OpenModelFileInBlenderAsync(string model)
    {
        var svc = _session.ModelPackages;
        var blender = BlenderTools.FindBlender();
        if (svc == null) return;
        if (blender == null)
        {
            Dialogs.Info("Blender was not found", "Install Blender 4.5 LTS (from blender.org or the Microsoft Store), then try again.");
            return;
        }
        try
        {
            var res = await _main.RunBusyAsync("Exporting the model for Blender...", () => Task.Run(() =>
                svc.Export(model, svc.SubjectOf(model), ModelExportMode.CurrentLook, ModelsExportDir)));
            OpenInBlender(blender, res.GltfFiles[0]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or M2Exception or GltfException or InvalidOperationException)
        {
            Dialogs.Error("Could not export the model", e.Message);
        }
    }

    /// <summary>Start Blender with a file the add-on imports (a model's .gltf or a terrain package's wowdata_terrain.json).</summary>
    public bool OpenFileInBlender(string file, string toast)
    {
        var blender = BlenderTools.FindBlender();
        var root = BlenderTools.AddonRoot();
        if (blender == null)
        {
            Dialogs.Info("Blender was not found", "Install Blender 4.5 LTS (from blender.org or the Microsoft Store), then try again. The package was written; you can open it later with File > Import in Blender (with the add-on).");
            return false;
        }
        if (root == null)
        {
            Dialogs.Error("The Blender add-on is missing", "The app's copy of the Blender add-on (blender_addon folder next to the app) was not found. Repair the installation.");
            return false;
        }
        try
        {
            BlenderTools.OpenInBlender(blender, root, file);
            _main.Toast(toast);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Error("Could not start Blender", e.Message);
            return false;
        }
    }

    private void OpenInBlender(string blender, string gltf)
    {
        var root = BlenderTools.AddonRoot();
        if (root == null)
        {
            Dialogs.Error("The Blender add-on is missing", "The app's copy of the Blender add-on (blender_addon folder next to the app) was not found. Repair the installation.");
            return;
        }
        try
        {
            BlenderTools.OpenInBlender(blender, root, gltf);
            _main.Toast("Blender is starting with the model. When you are done: File > Export > WoW 1.12 model for WoW Data Studio, then \"Import edited model...\" here.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Error("Could not start Blender", e.Message);
        }
    }

    /// <summary>"Open in Blender": export the look shown now (with animations) and start Blender with it.</summary>
    [RelayCommand]
    private async Task OpenModelInBlender()
    {
        if (!ModelViewer.HasModel || _session.ModelPackages == null) return;
        var blender = BlenderTools.FindBlender();
        if (blender == null)
        {
            Dialogs.Info("Blender was not found", "Install Blender 4.5 LTS (from blender.org or the Microsoft Store), then try again. You can still use \"Export for Blender...\" and open the file in any glTF program.");
            return;
        }
        try
        {
            var res = await ExportAsync(ModelExportMode.CurrentLook, ModelsExportDir, true);
            if (res != null) OpenInBlender(blender, res.GltfFiles[0]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or M2Exception or GltfException)
        {
            Dialogs.Error("Could not export the model", e.Message);
        }
    }

    [RelayCommand]
    private async Task ImportEditedModel()
    {
        if (_session.ModelPackages == null) return;
        int pick = Dialogs.Show("Import edited model",
            "Pick what you want to import:\n\n" +
            "• the package folder made by \"Export for Blender\" (it finds the file Blender exported into its blender_export folder, or your edited PNGs);\n" +
            "• or the .gltf / .glb file you exported from Blender.",
            DialogKind.Question, new[] { "Pick the folder...", "Pick a .gltf / .glb file...", "Cancel" });
        string? path = pick switch
        {
            0 => Dialogs.PickFolder("The export package folder (or its blender_export folder)", _session.Settings.LastImportDir ?? ModelsExportDir),
            1 => Dialogs.PickOpenFile("The edited model", "glTF model (*.gltf;*.glb)|*.gltf;*.glb", _session.Settings.LastImportDir ?? ModelsExportDir),
            _ => null,
        };
        if (path == null) return;
        _session.Settings.LastImportDir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        _main.SaveSettings();
        await ImportModelAsync(path);
    }

    /// <summary>The whole import: read, analyze, review screen, save as one change set.</summary>
    public async Task<bool> ImportModelAsync(string path, bool interactive = true, Action<ModelImportWindow>? onShown = null)
    {
        var svc = _session.ModelPackages!;
        ModelImportAnalysis a;
        try
        {
            a = await _main.RunBusyAsync("Reading your edited model...", () => Task.Run(() => ModelImportAnalyzer.Analyze(path, svc.ImportContext())));
        }
        catch (Exception e) when (e is GltfException or IOException or UnauthorizedAccessException or M2Exception or System.Text.Json.JsonException or WoWData.Core.Imaging.ImageFormatException)
        {
            Dialogs.Error("Could not read the edited model", e.Message);
            return false;
        }
        var vm = new ModelImportViewModel(a);
        vm.CompareRequested = row => _ = CompareImportRowAsync(vm, row);
        if (!interactive) return Commit(vm);
        var win = new ModelImportWindow(vm) { Owner = Application.Current.MainWindow };
        onShown?.Invoke(win);
        if (win.ShowDialog() != true) return false;
        return Commit(vm);
    }

    private bool Commit(ModelImportViewModel vm)
    {
        if (!vm.Analysis.AnythingToImport) return false;
        try
        {
            var cs = vm.Commit(_session.Workspace!, _session.Index);
            _main.Toast($"Saved {cs.Files.Count} file{(cs.Files.Count == 1 ? "" : "s")} as one import (\"{cs.Label}\"). Next: \"Test in game\" or \"Build release patch\"; \"Revert this import\" in My changes undoes it.");
            return true;
        }
        catch (WorkspaceException e)
        {
            Dialogs.Error("Could not save the import", e.Message);
            return false;
        }
    }

    private async Task CompareImportRowAsync(ModelImportViewModel vm, ModelChangeRow row)
    {
        try
        {
            if (row.Change.Kind == ModelChangeKind.Texture)
            {
                var cmp = vm.TextureCompare(row);
                if (cmp != null) new CompareWindow(cmp) { Owner = Application.Current.Windows.OfType<ModelImportWindow>().FirstOrDefault() ?? Application.Current.MainWindow }.Show();
                return;
            }
            var cvm = await MeshCompareAsync(vm, row);
            if (cvm != null) new ModelCompareWindow(cvm) { Owner = Application.Current.Windows.OfType<ModelImportWindow>().FirstOrDefault() ?? Application.Current.MainWindow }.Show();
        }
        catch (Exception e) when (e is IOException or M2Exception or WoWData.Core.Blp.BlpException)
        {
            Dialogs.Error("Could not open the comparison", e.Message);
        }
    }

    /// <summary>The 3D comparison of a mesh row (original vs. edited, the import's textures on the right).</summary>
    public Task<ModelCompareViewModel?> MeshCompareAsync(ModelImportViewModel vm, ModelChangeRow row)
    {
        var c = row.Change;
        var res = _session.Models!;
        var model = vm.Analysis.ModelPath;
        var shownLook = string.Equals(ModelViewer.ModelPath, model, StringComparison.OrdinalIgnoreCase) ? ModelViewer.CurrentLook : null;
        return Task.Run(() =>
        {
            if (c.NewBytes == null || c.OriginalBytes == null) return null;
            var look = shownLook ?? res.DefaultFor(model);
            return (ModelCompareViewModel?)ModelImportViewModel.MeshCompare(model, c.OriginalBytes, c.NewBytes, look, res.LoadBlp, vm.Analysis.Changes, $"Compare original vs. edited: {Path.GetFileName(model)}", res.AnimationName);
        });
    }

    /// <summary>Original (from game) vs. your edit of a model in the workspace (My changes, the model actions).</summary>
    public async Task<ModelCompareViewModel?> ModelCompareForAsync(string model)
    {
        var ws = _session.Workspace;
        var edited = ws?.ReadEdited(model);
        var orig = _session.Data?.Read(model);
        if (edited == null || orig == null) return null;
        var res = _session.Models!;
        var shownLook = string.Equals(ModelViewer.ModelPath, model, StringComparison.OrdinalIgnoreCase) ? ModelViewer.CurrentLook : null;
        return await Task.Run(() =>
        {
            var look = shownLook ?? res.DefaultFor(model);
            var vm = ModelImportViewModel.MeshCompare(model, orig, edited, look, res.LoadBlp, Array.Empty<ModelChange>(), $"Compare original vs. edited: {Path.GetFileName(model)}", res.AnimationName);
            return vm;
        });
    }

    [RelayCommand]
    private async Task CompareModel()
    {
        var model = ModelViewer.ModelPath;
        try
        {
            var vm = model.Length == 0 ? null : await ModelCompareForAsync(model);
            if (vm == null)
            {
                Dialogs.Info("Nothing to compare", "This model has not been changed. Import an edited model first (\"Import edited model...\").");
                return;
            }
            new ModelCompareWindow(vm) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception e) when (e is IOException or M2Exception)
        {
            Dialogs.Error("Could not open the comparison", e.Message);
        }
    }

    [RelayCommand]
    private Task RevertModel() => RevertAsync(ModelViewer.ModelPath.Length == 0 ? null : ModelViewer.ModelPath, null);

    /// <summary>Tools > Install the Blender add-on...: build the zip, then install it into Blender 4.5 (with the user's
    /// go-ahead) or show where it is for a manual install.</summary>
    [RelayCommand]
    private async Task InstallBlenderAddon()
    {
        var root = BlenderTools.AddonRoot();
        if (root == null)
        {
            Dialogs.Error("The Blender add-on is missing", "The app's copy of the add-on (blender_addon folder next to the app) was not found. Repair the installation.");
            return;
        }
        string zip;
        try { zip = await Task.Run(() => BlenderTools.BuildZip(root, BlenderTools.DefaultZipPath(_session.Settings.DefaultExportDir))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not write the add-on zip", e.Message);
            return;
        }
        var blender = BlenderTools.FindBlender();
        string version = BlenderTools.AddonVersion(root);
        string manual = BlenderTools.ManualInstallText;
        if (blender == null)
        {
            if (Dialogs.Show("Install the Blender add-on", $"The add-on (version {version}) is ready:\n{zip}\n\nBlender was not found on this computer. Install Blender 4.5 LTS first (blender.org or the Microsoft Store).\n\n{manual}",
                    DialogKind.Info, new[] { "Show the zip", "OK" }) == 0) Dialogs.ShowInExplorer(zip);
            return;
        }
        int pick = Dialogs.Show("Install the Blender add-on",
            $"The add-on \"WoW 1.12 model (wowdata)\" version {version} is ready:\n{zip}\n\n" +
            $"Install it into your Blender now? WoW Data Studio runs Blender in the background ({blender}) to install and enable the add-on with Blender's own installer, and saves Blender's preferences so it stays enabled (into {BlenderTools.UserAddonsDir()}). Close Blender first if it is open.\n\n{manual}",
            DialogKind.Question, new[] { "Install it into Blender", "Show the zip (install it myself)", "Cancel" });
        if (pick == 1)
        {
            Dialogs.ShowInExplorer(zip);
            return;
        }
        if (pick != 0) return;
        var r = await _main.RunBusyAsync("Installing the add-on into Blender...", () => Task.Run(() => BlenderTools.Install(blender, root, zip, Path.Combine(_session.Paths.Root, "temp"))));
        if (r.Ok) Dialogs.Success("Blender add-on installed", r.Message + "\n\nIn Blender: File > Import > WoW 1.12 model, and File > Export > WoW 1.12 model for WoW Data Studio.", r.AddonFile);
        else Dialogs.Error("The add-on was not installed", r.Message + "\n\n" + manual, r.Details);
    }
}
