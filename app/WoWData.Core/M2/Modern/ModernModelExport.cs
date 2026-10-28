using WoWData.Core.ModelIO;
using WoWData.Core.Session;

namespace WoWData.Core.M2.Modern;

/// <summary>
/// "Export for Blender" of a model of another version opened read only (the main window's model actions and
/// "wds models export --version"): the same package as for your 1.12 game (<see cref="ModelExporter"/>: glTF, PNG
/// textures, manifest), made from the model the modern reader read. The looks are the version's creature and item
/// displays of the model (<see cref="ModernModelResolver"/>); animations kept in .anim files are read for the export.
/// The package goes to a folder you choose, never into the client's folder, and cannot be imported back into that
/// version (nothing is written into another version's client).
/// </summary>
public static class ModernModelExport
{
    /// <summary>At most this many displays become looks (a humanoid NPC model can have thousands).</summary>
    public const int MaxLooks = 200;

    /// <summary>Refuses a folder inside the client's own folders (other versions are only read).</summary>
    public static void CheckOutsideClient(AppSession s, string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var dirs = new[] { s.Profile?.Copy?.Folder, s.Files?.Storage.DataDirectory };
        foreach (var d in dirs)
            if (d != null && full.StartsWith(Path.GetFullPath(d).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{path} is inside {s.Profile?.Copy?.Name ?? "the client"}: other versions are only read. Pick a folder outside it.");
    }

    /// <summary>The looks of a model: every creature display, then every item display using it; <paramref name="display"/>
    /// (a creature display, or an item display with <paramref name="item"/>) is the current one.</summary>
    public static (List<ExportLook> Looks, int Current) Looks(AppSession s, string model, long? display = null, bool item = false)
    {
        var ix = s.Index ?? throw new InvalidOperationException("Nothing is open.");
        var looks = s.Looks ?? throw new InvalidOperationException("Nothing is open.");
        var list = new List<ExportLook>();
        int current = 0;
        var cd = ix.CreatureDisplaysUsingModel(model);
        var cnames = ix.NamedCreaturesForDisplays(cd.Select(x => x.Id));
        foreach (var (id, skins, _) in cd.Take(MaxLooks))
        {
            if (!item && display == id) current = list.Count;
            var sk = string.Join(" / ", skins.Where(x => x.Length > 0).Select(Path.GetFileNameWithoutExtension));
            list.Add(new ExportLook
            {
                Name = (cnames.TryGetValue(id, out var n) ? n : $"Display {id}") + (sk.Length > 0 ? $" ({sk})" : ""), Kind = "creature display", Ids = { id },
                Detail = $"creature display {id}", Look = looks.CreatureDisplay((int)id),
            });
        }
        var idd = ix.ItemDisplaysUsingModel(model).GroupBy(x => x.Id).Select(g => g.First()).ToList();
        var inames = ix.ItemNamesForDisplays(idd.Select(x => x.Id));
        foreach (var (id, tex) in idd.Take(Math.Max(0, MaxLooks - list.Count)))
        {
            if (item && display == id) current = list.Count;
            list.Add(new ExportLook
            {
                Name = (inames.TryGetValue(id, out var n) ? n : $"Item display {id}") + $" ({Path.GetFileNameWithoutExtension(tex)})", Kind = "item display", Ids = { id },
                Detail = $"item display {id}", Look = looks.ItemDisplay((int)id, model),
            });
        }
        if (list.Count == 0) list.Add(new ExportLook { Name = Path.GetFileNameWithoutExtension(model), Kind = "model", Look = looks.DefaultFor(model) });
        return (list, current);
    }

    /// <summary>
    /// Writes the package. <paramref name="shownLook"/> (the 3D view's look) replaces the looks of the index for
    /// <see cref="ModelExportMode.CurrentLook"/>, so the export matches what is on screen.
    /// </summary>
    public static ModelExportResult Export(AppSession s, string model, ModelExportMode mode, string outputDir, long? display = null, bool item = false,
        ModelLook? shownLook = null, string? shownName = null, HashSet<int>? visibleGeosets = null, bool includeAnimations = true, string? packageFolder = null,
        bool includeSkeleton = false)
    {
        if (!s.IsReadOnly || s.Files == null || s.ModelSource == null || s.Looks == null) throw new InvalidOperationException("No other version is open.");
        CheckOutsideClient(s, packageFolder ?? outputDir);
        byte[]? data;
        try { data = s.Files.Read(model); }
        catch (WoWData.Core.Versions.ClientFileException e) { throw new IOException(e.Message, e); }
        if (data == null) throw new IOException($"{model} is not a file of {s.Profile!.VersionBuild}.");
        var m = s.ModelSource.Load(model);
        var notes = new List<string>();
        if (includeAnimations && m.Modern?.Animations is { } ext && ext.ExternalCount > 0)
        {
            // the export needs every animation at once: read every .anim file (no budget for this one model)
            ext.Budget = long.MaxValue;
            int missing = 0;
            for (int i = 0; i < m.Sequences.Length; i++)
                if (ext.IsExternal(i) && ext.Ensure(i).State != ExternalState.Loaded) missing++;
            if (missing > 0) notes.Add($"{missing} animation{(missing == 1 ? "" : "s")} kept in .anim files have no keys here (no file named, locked or not on this computer): they stand still");
        }
        var (looks, current) = Looks(s, model, display, item);
        if (shownLook != null && mode == ModelExportMode.CurrentLook)
        {
            looks = new List<ExportLook> { new() { Name = shownName ?? looks[current].Name, Kind = looks[current].Kind, Ids = looks[current].Ids.ToList(), Detail = looks[current].Detail, Look = shownLook } };
            current = 0;
        }
        var ix = s.Index!;
        int creatures = ix.CreatureDisplaysUsingModel(model).Count, items = ix.ItemDisplaysUsingModel(model).Select(x => x.Id).Distinct().Count();
        var res = ModelExporter.Export(new ModelExportRequest
        {
            ModelPath = model, ModelData = data, Model = m, Source = $"{s.Profile!.VersionBuild} (read only)", Archive = "CASC",
            Looks = looks, CurrentLook = current, Mode = mode, VisibleGeosets = visibleGeosets ?? looks[current].Look.Geosets,
            AnimationName = s.Looks.AnimationName, LoadTexture = s.Looks.LoadBlp, IncludeAnimations = includeAnimations, OutputDir = outputDir, PackageFolder = packageFolder,
            UsersSummary = $"{creatures} creature display{(creatures == 1 ? "" : "s")} and {items} item display{(items == 1 ? "" : "s")} of {s.Profile.VersionBuild} use this model",
            UsersCount = creatures + items, IncludeSkeleton = includeSkeleton, Animations = s.Looks.Animations,
        });
        res.Notes.AddRange(notes);
        res.Notes.Add($"read from {s.Profile.VersionBuild}, which is open read only: the package is for Blender and other programs; it cannot be imported back into that version");
        if (looks.Count >= MaxLooks) res.Notes.Add($"only the first {MaxLooks} displays became looks");
        return res;
    }
}
