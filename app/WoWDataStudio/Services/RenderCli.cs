using System.IO;
using System.Windows;
using System.Windows.Threading;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Mpq;
using WoWDataStudio.Controls;
using WoWDataStudio.Rendering;

namespace WoWDataStudio.Services;

/// <summary>
/// `--render &lt;model.m2&gt; &lt;out.png&gt; [--display N] [--sequence N] [--time ms] [--yaw deg] [--size WxH] [--original] [--no-particles]`: renders
/// one model offscreen with its default look (or a creature display's skins) and exits. A developer tool for checking the
/// renderer against many models quickly. 0.6.1: like the app's own views it shows your workspace version (your edited
/// model and textures, from the app folder of --home or %APPDATA%\WoWDataStudio) when there is one; --original renders
/// the game's files only. It never writes anything but the PNG.
/// </summary>
public static class RenderCli
{
    /// <summary>
    /// M2: `--render &lt;#FileDataID&gt; &lt;png&gt; --version &lt;client folder&gt; [--display N | --item-display N]`: a model of
    /// another version (read only), read by the modern reader, with a creature display's skins, an item display's
    /// texture or the model's default look. "wds models render --version" resolves the model and the animation.
    /// </summary>
    private static async Task<int> RenderOtherAsync(string folder, string model, string outFile, int display, int itemDisplay, int sequence, double time, float yaw, int w, int h, bool particles)
    {
        var copy = WoWData.Core.Versions.ClientPick.Resolve(folder, Array.Empty<WoWData.Core.Versions.ClientCopy>());
        using var oc = WoWData.Core.Versions.OtherClient.Open(copy);
        var storage = oc.Casc ?? throw new NotSupportedException($"{copy.Name} is not a CASC client.");
        var cat = new Catalog160(storage, oc.Catalog, copy.Build > 0 ? copy.Build : storage.Build);
        var files = new WoWData.Core.Versions.ModernClientFiles(storage);
        var res = new WoWData.Core.M2.Modern.ModernModelResolver(cat, files);
        var m = new WoWData.Core.M2.Modern.ModernModelSource(files).Load(model);
        var asset = ModelAsset.Build(model, m, res.LoadBlp);
        var look = display > 0 ? res.CreatureDisplay(display) : itemDisplay > 0 ? res.ItemDisplay(itemDisplay, model) : res.DefaultFor(model);
        var scene = new ModelScene { Asset = asset, Look = look, Playing = false, ShowParticles = particles };
        if (sequence >= asset.Model.Sequences.Length) throw new ArgumentException($"the model has {asset.Model.Sequences.Length} animations (0-{asset.Model.Sequences.Length - 1})");
        if (sequence >= 0) scene.Sequence = sequence;
        scene.SequenceTime = time;
        var cam = new OrbitCamera();
        cam.Frame(asset.BoundsMin, asset.BoundsMax);
        if (!float.IsNaN(yaw)) cam.Yaw = yaw;
        var vp = new ModelViewport { Scene = scene, Camera = cam };
        var win = new Window { Content = vp, Width = 400, Height = 300, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        win.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var img = await vp.CaptureAsync(w, h);
        Png.Save(img, outFile, false);
        Console.WriteLine($"{model}: {asset.Model.Vertices.Length} vertices, view 0 {asset.TriangleCount(0)} triangles" +
                          (asset.Model.Sequences.Length == 0 ? "" : $", animation {scene.Sequence} at {scene.SequenceTime:0} ms") +
                          $" -> {outFile} ({copy.Name} {cat.Build}, read only; {m.Modern?.FormatText})");
        win.Close();
        return 0;
    }

    public static async Task<int> RunAsync(string clientDir, string[] args, string? home = null)
    {
        try
        {
            string model = args[0], outFile = Path.GetFullPath(args[1]);
            int display = 0, sequence = -1, w = 900, h = 700;
            string? sequenceName = null;
            double time = 0;
            bool original = args.Any(a => a.Equals("--original", StringComparison.OrdinalIgnoreCase));
            bool particles = !args.Any(a => a.Equals("--no-particles", StringComparison.OrdinalIgnoreCase));
            float yaw = float.NaN;
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--original": break;
                    case "--display": display = int.Parse(args[++i]); break;
                    case "--sequence":
                        // 0.10.0: a number or a name ("Walk", "Stand [2]")
                        if (!int.TryParse(args[++i], out sequence)) { sequence = -1; sequenceName = args[i]; }
                        break;
                    case "--time": time = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--yaw": yaw = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--size":
                        var p = args[++i].Split('x');
                        w = int.Parse(p[0]);
                        h = int.Parse(p[1]);
                        break;
                }
            }
            int vat = Array.FindIndex(args, a => a.Equals("--version", StringComparison.OrdinalIgnoreCase));
            if (vat >= 0 && vat + 1 < args.Length)
            {
                int itemDisplay = 0;
                int iat = Array.FindIndex(args, a => a.Equals("--item-display", StringComparison.OrdinalIgnoreCase));
                if (iat >= 0 && iat + 1 < args.Length) itemDisplay = int.Parse(args[iat + 1]);
                return await RenderOtherAsync(args[vat + 1], model, outFile, display, itemDisplay, sequence, time, yaw, w, h, particles);
            }
            using var client = new ClientData(Path.Combine(clientDir, "Data"));
            var cat = new Catalog(client);
            // the workspace of this game folder (read only): your edited model and textures win, as in the app
            WoWData.Core.Workspace.Workspace? ws = null;
            if (!original)
            {
                var cp = new WoWData.Core.Workspace.AppPaths(home).ForClient(clientDir);
                if (File.Exists(cp.ChangesFile)) ws = new WoWData.Core.Workspace.Workspace(cp);
            }
            byte[]? Read(string p) => (ws != null && ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? client.Read(p);
            var res = ws != null ? new ModelResolver(cat, Read) : new ModelResolver(cat);
            model = cat.Canonical(model);
            bool editedModel = ws?.IsModified(model) == true;
            var data = Read(model) ?? throw new FileNotFoundException(model);
            var asset = ModelAsset.Build(model, data, res.LoadBlp);
            var scene = new ModelScene { Asset = asset, Look = display > 0 ? res.CreatureDisplay(display) : res.DefaultFor(model), Playing = false, ShowParticles = particles };
            if (sequenceName != null)
            {
                var names = WoWData.Core.ModelIO.ModelExporter.ActionNames(asset.Model, res.AnimationName);
                sequence = Array.FindIndex(names, n => string.Equals(n, sequenceName, StringComparison.OrdinalIgnoreCase));
                if (sequence < 0) throw new ArgumentException($"the model has no animation \"{sequenceName}\" (it has: {string.Join(", ", names.Take(30))}{(names.Length > 30 ? ", ..." : "")})");
            }
            if (sequence >= asset.Model.Sequences.Length) throw new ArgumentException($"the model has {asset.Model.Sequences.Length} animations (0-{asset.Model.Sequences.Length - 1})");
            if (sequence >= 0) scene.Sequence = sequence;
            scene.SequenceTime = time;
            var cam = new OrbitCamera();
            cam.Frame(asset.BoundsMin, asset.BoundsMax);
            if (!float.IsNaN(yaw)) cam.Yaw = yaw;
            var vp = new ModelViewport { Scene = scene, Camera = cam };
            var win = new Window { Content = vp, Width = 400, Height = 300, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            win.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var img = await vp.CaptureAsync(w, h);
            Png.Save(img, outFile, false);
            int editedTextures = ws == null ? 0 : ws.ModifiedPaths().Count(p => p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase));
            string animText = asset.Model.Sequences.Length == 0 ? "" : $", animation {WoWData.Core.ModelIO.ModelExporter.ActionNames(asset.Model, res.AnimationName)[scene.Sequence]} at {scene.SequenceTime:0} ms";
            Console.WriteLine($"{model}: {asset.Model.Vertices.Length} vertices, view 0 {asset.TriangleCount(0)} triangles{animText} -> {outFile}" +
                              (original ? " (game files only, --original)" : ws == null ? " (no workspace: game files)" : $" (workspace: model {(editedModel ? "edited" : "original")}, {editedTextures} edited texture(s) available)"));
            win.Close();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            File.WriteAllText(Path.ChangeExtension(Path.GetFullPath(args.Length > 1 ? args[1] : "render"), ".error.txt"), e.ToString());
            return 2;
        }
    }
}
