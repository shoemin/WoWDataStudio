using System.Buffers.Binary;
using System.Globalization;
using WoWData.Core.Blp;
using WoWData.Core.Casc;
using WoWData.Core.Imaging;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Versions.Catalog;

/// <summary>A file an import writes (or, in a dry run, would write) into the workspace.</summary>
public sealed record ImportedFile(string Path, string Item, uint FileDataId, bool IsNew, long Size, string Summary, bool ReplacesYourEdit);

/// <summary>The result of importing picked updates (also of a dry run, which writes nothing).</summary>
public sealed class UpdateImportResult
{
    public bool DryRun { get; init; }
    /// <summary>The updates that were imported (or would be).</summary>
    public List<UpdateItem> Imported { get; } = new();
    /// <summary>The updates that cannot be imported yet, with why.</summary>
    public List<(UpdateItem Item, string Why)> NotYet { get; } = new();
    public List<ImportedFile> Files { get; } = new();
    /// <summary>The map conversions of the terrain updates (one per map).</summary>
    public List<MapConversionResult> Maps { get; } = new();
    /// <summary>Files that could not be read or made loadable (left out).</summary>
    public List<string> FileProblems { get; } = new();
    public List<string> Warnings { get; } = new();
    public ChangeSet? ChangeSet { get; set; }
    public TimeSpan Elapsed { get; set; }

    public bool Ok => Maps.All(m => m.Ok);
    public int FileCount => Files.Count + Maps.Sum(m => m.Files.Count);

    public List<string> Lines()
    {
        var l = new List<string>();
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        l.Add($"{(DryRun ? "Dry run (nothing written)" : "Imported")}: {N(Imported.Count)} update{(Imported.Count == 1 ? "" : "s")} into your workspace" +
              (ChangeSet != null ? $" as one change set (\"{ChangeSet.Label}\")" : DryRun ? " as one change set" : "") + $", {N(FileCount)} files.");
        foreach (var i in Imported) l.Add($"  {i.Id}: {i.Title}");
        if (NotYet.Count > 0)
        {
            l.Add($"Not imported ({N(NotYet.Count)}): not yet possible.");
            foreach (var (i, why) in NotYet) l.Add($"  {i.Id}: {i.Title}: {why}");
        }
        if (Files.Count > 0)
        {
            l.Add($"Files: {N(Files.Count)} ({N(Files.Count(f => f.IsNew))} new to your game, {N(Files.Count - Files.Count(f => f.IsNew))} replacing a game file).");
            foreach (var g in Files.GroupBy(f => f.Summary.Split(':')[0]).OrderByDescending(g => g.Count()).Take(6)) l.Add($"  {N(g.Count())} {g.Key}");
        }
        if (FileProblems.Count > 0)
        {
            l.Add($"Left out ({N(FileProblems.Count)} files that could not be read or made loadable):");
            foreach (var p in FileProblems.Take(10)) l.Add("  " + p);
        }
        foreach (var m in Maps)
        {
            l.Add("");
            l.AddRange(m.Lines());
        }
        foreach (var w in Warnings) l.Add("Warning: " + w);
        if (Maps.Count > 0)
        {
            l.Add("");
            l.Add("Then:");
            foreach (var s in Maps.SelectMany(m => m.ServerSteps).Distinct()) l.Add("  " + s);
        }
        return l;
    }
}

/// <summary>
/// 0.14.0 (experimental): imports picked updates of an <see cref="UpdateCatalog"/> into the user's 1.12 workspace as ONE
/// change set ("Revert this import" in My changes takes it all back): terrain and new maps through the map conversion
/// (<see cref="MapConverter"/>, one run per map, all zones of a map together), files (textures, interface art, fonts,
/// MP3 sounds) after <see cref="UpdateFileConvert.Fit"/> made them loadable by 1.12 (the same checks as every saved file).
/// Updates that cannot be imported yet are listed with why. A dry run reports and writes nothing. The game folder and
/// the source client are never written.
/// </summary>
public static class UpdateImporter
{
    public static UpdateImportResult Run(UpdateCatalogSource src, UpdateCatalog catalog, IReadOnlyList<string> ids, MapConvertTarget target, bool dryRun,
                                         IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new UpdateImportResult { DryRun = dryRun };
        var missing = ids.Where(i => catalog.Find(i) == null).ToList();
        if (missing.Count > 0) throw new ArgumentException($"The catalog has no update {string.Join(", ", missing.Select(m => $"\"{m}\""))} (wds versions updates lists them).");
        var items = ids.Select(i => catalog.Find(i)!).Distinct().ToList();
        if (items.Count == 0) throw new ArgumentException("Pick at least one update.");
        foreach (var i in items)
        {
            if (!i.Importable) res.NotYet.Add((i, i.NotYet!));
            else if (i.Terrain == null && (i.FileList == null || i.FileList.Count == 0)) res.NotYet.Add((i, "it has nothing the app can write."));
            else res.Imported.Add(i);
        }
        var fileItems = res.Imported.Where(i => i.Terrain == null).ToList();
        var mapItems = res.Imported.Where(i => i.Terrain != null).GroupBy(i => i.Terrain!.MapId).ToList();

        // ---- the files: read, made loadable, staged
        var staging = dryRun ? null : Path.Combine(target.Workspace.Paths.TempDir, "updates_" + Guid.NewGuid().ToString("N")[..8]);
        if (staging != null) Directory.CreateDirectory(staging);
        try
        {
            var pending = new List<Ws.PendingEdit>();
            var byPath = new Dictionary<string, (UpdateFile File, UpdateItem Item)>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in fileItems)
                foreach (var f in i.FileList!)
                    byPath[ClientData.ResolveName(f.Path)] = (f, i);   // a later pick of the same file wins
            int done = 0;
            var gate = new object();
            Parallel.ForEach(byPath, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1)), CancellationToken = ct }, kv =>
            {
                var (path, (f, item)) = (kv.Key, kv.Value);
                byte[]? data;
                string note;
                try
                {
                    var raw = (f.HighRes ? src.HighRes : src.Casc).ReadFileDataId(f.FileDataId);
                    if (raw == null) throw new ClientFileException($"#{f.FileDataId} is not in {src.Copy.Name}");
                    (data, note) = UpdateFileConvert.Fit(path, raw);
                }
                catch (Exception e) when (e is CascMissingException or CascEncryptedException or ClientFileException or BlpException or IOException or InvalidDataException or ArgumentException)
                {
                    lock (gate) res.FileProblems.Add($"{path} (#{f.FileDataId}): {e.Message}");
                    return;
                }
                var problems = path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? BlpEncoder.Validate(data) : (IReadOnlyList<string>)Array.Empty<string>();
                if (problems.Count > 0)
                {
                    lock (gate) res.FileProblems.Add($"{path}: {string.Join("; ", problems)}");
                    return;
                }
                var game = target.Client.Read(path);
                var origin = new OriginInfo
                {
                    Archive = target.Client.Find(path)?.Name, Sha256 = game != null ? Ws.Sha256(game) : null, Size = game?.LongLength ?? 0,
                    Format = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} from {src.Copy.Name} (#{f.FileDataId}{(f.HighRes ? ", high resolution" : "")})",
                    IsNew = game == null, UsedBy = { item.Title },
                };
                string staged = "";
                if (staging != null)
                {
                    staged = Path.Combine(staging, path.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, data);
                }
                string summary = $"{item.KindText}: {path} from {src.Copy.Name} (#{f.FileDataId}), {note}";
                lock (gate)
                {
                    res.Files.Add(new ImportedFile(path, item.Id, f.FileDataId, game == null, data.LongLength, summary, target.Workspace.IsModified(path)));
                    if (staging != null) pending.Add(new Ws.PendingEdit(path, () => File.ReadAllBytes(staged), origin, summary));
                }
                int n = Interlocked.Increment(ref done);
                if (n % 200 == 0) progress?.Report($"Preparing files: {n:N0} of {byPath.Count:N0}...");
            });
            res.Files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            var replaced = res.Files.Count(f => f.ReplacesYourEdit);
            if (replaced > 0)
                res.Warnings.Add($"{replaced:N0} of these files already have your own edits: the import replaces them; \"Revert this import\" puts your earlier versions back.");

            // ---- the terrain: one map conversion per map (validated in a dry run first when writing)
            var requests = mapItems.Select(g => (Map: g.Key, Req: RequestOf(g.ToList()))).ToList();
            if (!dryRun && requests.Count > 0)
                foreach (var (map, req) in requests)
                {
                    progress?.Report($"Checking the conversion of {req.Map}...");
                    var check = MapConverter.Run(src.Source, req, target, true, progress, ct);
                    if (!check.Ok)
                    {
                        // the 1.12 check refused converted files: nothing is saved (the result says which)
                        res.Maps.Add(check);
                        res.Warnings.Add($"The 1.12 check refused converted files of map {map}: nothing was saved.");
                        res.Elapsed = sw.Elapsed;
                        return res;
                    }
                }
            if (dryRun)
            {
                foreach (var (_, req) in requests) res.Maps.Add(MapConverter.Run(src.Source, req, target, true, progress, ct));
                res.Elapsed = sw.Elapsed;
                return res;
            }

            // ---- writing: the files first, then the maps, all into one change set
            var label = res.Imported.Count == 1 ? $"Update from {src.Copy.Name}: {res.Imported[0].Title}" : $"Updates from {src.Copy.Name}: {res.Imported.Count} picks ({string.Join(", ", res.Imported.Take(3).Select(i => i.Title))}{(res.Imported.Count > 3 ? ", ..." : "")})";
            var cs = target.Workspace.BeginChangeSet(label);
            try
            {
                if (pending.Count > 0)
                {
                    progress?.Report($"Saving {pending.Count:N0} files into your workspace...");
                    target.Workspace.SaveEdits(pending.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList(), cs, (i, n) => progress?.Report($"Saving into your workspace: {i * 100 / Math.Max(1, n)}%..."));
                }
                var t2 = new MapConvertTarget { Client = target.Client, Workspace = target.Workspace, Dbc = target.Dbc, Locale = target.Locale, JoinChangeSet = cs };
                foreach (var (_, req) in requests) res.Maps.Add(MapConverter.Run(src.Source, req, t2, false, progress, ct));
            }
            catch
            {
                // nothing half done: what this import saved is taken back
                if (cs.Files.Count > 0) target.Workspace.RevertChangeSet(cs.Id);
                throw;
            }
            res.ChangeSet = cs;
            res.Elapsed = sw.Elapsed;
            return res;
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The map conversion of the terrain picks of one map: the whole map, one zone, or the tiles of several zones.</summary>
    public static MapConvertRequest RequestOf(List<UpdateItem> picks)
    {
        var t = picks[0].Terrain!;
        var map = t.MapId.ToString(CultureInfo.InvariantCulture);
        if (picks.Any(p => p.Terrain!.WholeMap)) return new MapConvertRequest { Map = map };
        if (picks.Count == 1 && t.ZoneId is { } z) return new MapConvertRequest { Map = map, Zone = z.ToString(CultureInfo.InvariantCulture) };
        var tiles = picks.SelectMany(p => p.Terrain!.Tiles).Distinct().Select(s => MapConverter.ParseTiles(s)[0]).ToList();
        var names = picks.Select(p => p.Terrain!.ZoneName ?? "tiles without a zone").ToList();
        return new MapConvertRequest
        {
            Map = map, Tiles = tiles,
            SelectionLabel = names.Count == 1 ? names[0] : $"the zones {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };
    }
}

/// <summary>Files of another client made loadable by 1.12.</summary>
public static class UpdateFileConvert
{
    /// <summary>
    /// The bytes 1.12 loads, and what was done. A BLP that passes the 1.12 checks (<see cref="BlpEncoder.Validate"/>:
    /// palette or DXT1/3/5, a power of two up to 512, the full mip chain) is kept as it is; a larger one with mip levels
    /// keeps its own smaller levels (nothing re-encoded); anything else is decoded, made at most 512 x 512 (a power of
    /// two) and encoded again in its format (or the format its pixels need). Other files are kept as they are.
    /// </summary>
    public static (byte[] Data, string Note) Fit(string path, byte[] src)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) return (src, "as it is");
        if (BlpEncoder.Validate(src).Count == 0) return (src, "as it is");
        var b = BlpFile.Parse(src);
        int max = ConvertTextures.MaxSize;
        if (b.HasMips != 0 && ImageOps.IsPow2(b.Width) && ImageOps.IsPow2(b.Height) && b.Encoding is BlpFile.EncDxt or BlpFile.EncPalette)
        {
            int k = 0;
            while (Math.Max(b.Width >> k, b.Height >> k) > max) k++;
            if (k > 0 && k < b.LevelCount)
            {
                var t = TrimLevels(b, k);
                if (BlpEncoder.Validate(t).Count == 0)
                    return (t, $"{b.Width} x {b.Height} made {Math.Max(1, b.Width >> k)} x {Math.Max(1, b.Height >> k)} (its own smaller picture)");
            }
        }
        var img = ConvertTextures.Picture(src, max, out int w, out int h, out _);
        string fmt;
        try { fmt = b.Encoding == BlpFile.EncRaw ? BlpEncoder.SuggestFormat(img) : BlpEncoder.FormatOf(b); }
        catch (BlpException) { fmt = BlpEncoder.SuggestFormat(img); }
        var data = BlpEncoder.EncodeNew(img, fmt, mips: true);
        return (data, w != img.Width || h != img.Height ? $"{w} x {h} made {img.Width} x {img.Height} ({fmt})" : $"encoded again as {fmt}");
    }

    /// <summary>A BLP2 file from level <paramref name="k"/> of <paramref name="b"/> on: the header with the smaller size
    /// and the mip table moved up, the levels' bytes as they are (lossless).</summary>
    public static byte[] TrimLevels(BlpFile b, int k)
    {
        int levels = b.LevelCount;
        var parts = new List<byte[]>();
        for (int lv = k; lv < levels; lv++) parts.Add(b.MipData(lv).ToArray());
        int total = BlpFile.HeaderSize + parts.Sum(p => p.Length);
        var d = new byte[total];
        b.Data.AsSpan(0, BlpFile.HeaderSize).CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)Math.Max(1, b.Width >> k));
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)Math.Max(1, b.Height >> k));
        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x14 + 4 * i), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x54 + 4 * i), 0);
        }
        int pos = BlpFile.HeaderSize;
        for (int i = 0; i < parts.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x14 + 4 * i), (uint)pos);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x54 + 4 * i), (uint)parts[i].Length);
            parts[i].CopyTo(d, pos);
            pos += parts[i].Length;
        }
        return d;
    }
}
