using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using WoWData.Core.Casc;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions.Catalog;

/// <summary>
/// 0.14.0 (experimental): builds an <see cref="UpdateCatalog"/>: every difference between the source client (the "1.60"
/// Classic client) and the user's 1.12 game, measured, grouped into updates the user can pick. Read only on both sides.
/// The parts (each in its own file):
/// <list type="bullet">
/// <item>files: every file of the user's 1.12 game against the source (the MD5 of the 1.12 bytes against the source's
/// content key, which is the MD5 of its decoded file; Classic Era's key tells a format update Era already had); changed
/// textures are compared pixel by pixel (re-encoded only, lightly edited, repainted, made larger); high-resolution
/// variants; models, buildings, sounds, fonts; the source's new interface art and the files a listfile names;</item>
/// <item>terrain: every tile of every map with terrain against the 1.12 tile, chunk by chunk, per zone (with its
/// subzones); new maps; the light zones per zone; new ground textures and placed models 1.12 does not have;</item>
/// <item>tables: every data table (rows, and for the tables whose columns are named on both sides the changed rows),
/// hotfixes in the client's cache, skyboxes, weather, interface strings;</item>
/// <item>engine: the modern engine and interface features, from the strings of the client's program.</item>
/// </list>
/// The MD5s of the 1.12 files and the pixel comparisons are cached (by content), so a re-scan after a change on one side
/// takes a fraction of the first scan.
/// </summary>
public sealed partial class UpdateCatalogScanner
{
    private readonly UpdateCatalogSource _s;
    private readonly IProgress<string>? _progress;
    private readonly CancellationToken _ct;
    private readonly List<UpdateItem> _items = new();
    private readonly List<string> _notes = new();
    private readonly object _gate = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private UpdateCatalogScanner(UpdateCatalogSource s, IProgress<string>? progress, CancellationToken ct)
    {
        _s = s;
        _progress = progress;
        _ct = ct;
    }

    /// <summary>Scans both sides (about one to three minutes the first time; later scans reuse the cached MD5s and pixel
    /// comparisons). <paramref name="progress"/> gets one line per step.</summary>
    public static UpdateCatalog Scan(UpdateCatalogSource s, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sc = new UpdateCatalogScanner(s, progress, ct);
        var cat = new UpdateCatalog
        {
            Source = $"{s.Copy.Name}, {s.Copy.Version}.{s.Copy.Build}",
            SourceFolder = s.Copy.Folder,
            Target = s.Client112.DataDir,
            SourceFingerprint = s.SourceFingerprint(),
            TargetFingerprint = s.TargetFingerprint(),
            Scanned = DateTime.Now,
            NamesText = s.Source.NamesText,
        };
        sc.Step("Comparing the files of your 1.12 game with " + s.Copy.Name, sc.ScanFiles);
        sc.Step("Listing the new interface art and the named new files", sc.ScanNewNamedFiles);
        sc.Step("Comparing the terrain of every map, zone by zone", sc.ScanTerrain);
        sc.Step("Comparing the data tables", sc.ScanTables);
        sc.Step("Reading the engine features of " + Path.GetFileName(s.Copy.Exe), sc.ScanEngine);
        sc.Step("Counting the files without a name", sc.ScanUnnamed);
        cat.Items = sc._items.OrderBy(i => i.Kind).ThenBy(i => i.Class).ThenBy(i => i.Where, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        // IDs are unique (a second item of the same ID gets "~2")
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in cat.Items)
        {
            if (seen.TryGetValue(i.Id, out var n)) { seen[i.Id] = n + 1; i.Id += "~" + (n + 1).ToString(CultureInfo.InvariantCulture); }
            else seen[i.Id] = 1;
        }
        cat.Notes = sc._notes;
        cat.Seconds = Math.Round(sc._sw.Elapsed.TotalSeconds, 1);
        sc.Report($"Done: {cat.Items.Count:N0} differences in {cat.Seconds:0} s.");
        return cat;
    }

    private void Step(string what, Action run)
    {
        _ct.ThrowIfCancellationRequested();
        Report(what + "...");
        try { run(); }
        catch (Exception e) when (e is IOException or InvalidDataException or Db2.Db2FormatException or CascMissingException or Blp.BlpException or Dbc.DbcException
                                       or Mpq.MpqException or UnauthorizedAccessException or KeyNotFoundException)
        {
            lock (_gate) _notes.Add($"{what}: stopped early ({e.Message}); the rest of the catalog is complete.");
        }
    }

    private void Report(string text) => _progress?.Report($"[{_sw.Elapsed.TotalSeconds:0}s] {text}");

    private void Add(UpdateItem item)
    {
        lock (_gate) _items.Add(item);
    }

    private void Note(string text)
    {
        lock (_gate) _notes.Add(text);
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private ParallelOptions Par => new() { MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1)), CancellationToken = _ct };

    /// <summary>An item ID part from a path: lower case, '/' for '\', no spaces.</summary>
    public static string IdOf(string text) => text.Replace('\\', '/').Replace(' ', '_').ToLowerInvariant();

    // ------------------------------------------------------------------ folders

    /// <summary>The folder a path is grouped under at first: the top folder (Creature, Character, Item, Spells...),
    /// two levels for World, Interface and Sound (World\Azeroth, Interface\Icons, Sound\Creature).</summary>
    public static string BaseGroup(string path)
    {
        var parts = path.Split('\\');
        if (parts.Length <= 1) return "(top folder)";
        var top = parts[0];
        int depth = top.Equals("World", StringComparison.OrdinalIgnoreCase) || top.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                    || top.Equals("Sound", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        return string.Join('\\', parts.Take(Math.Min(depth, parts.Length - 1)));
    }

    private static string Prefix(string path, int depth)
    {
        var parts = path.Split('\\');
        return string.Join('\\', parts.Take(Math.Min(depth, parts.Length - 1)));
    }

    /// <summary>
    /// Files grouped by folder for picking: <see cref="BaseGroup"/> first; a group of more than <paramref name="maxGroup"/>
    /// files is split by one more folder level (again while it is too big), and subfolders with fewer than
    /// <paramref name="minSub"/> files stay together as "&lt;folder&gt; (other folders)".
    /// </summary>
    public static List<(string Key, List<T> Items)> Groups<T>(IEnumerable<T> items, Func<T, string> pathOf, int maxGroup = 800, int minSub = 40)
    {
        var res = new List<(string, List<T>)>();
        void Add(string key, List<T> list, bool rest)
        {
            int depth = key.Split('\\').Length;
            var shown = Pretty(list.Select(x => Prefix(pathOf(x), depth)).Where(p => p.Split('\\').Length == depth).DefaultIfEmpty(key));
            res.Add((rest ? shown + " (other folders)" : shown, list));
        }
        void Split(string key, List<T> list, int depth)
        {
            if (list.Count <= maxGroup || depth > 6)
            {
                Add(key, list, false);
                return;
            }
            var subs = list.GroupBy(x => Prefix(pathOf(x), depth + 1), StringComparer.OrdinalIgnoreCase).ToList();
            if (subs.Count <= 1)
            {
                // all in one folder: nothing to split by
                if (subs.Count == 1 && !string.Equals(subs[0].Key, key, StringComparison.OrdinalIgnoreCase) && subs[0].Key.Split('\\').Length > depth) Split(subs[0].Key, list, depth + 1);
                else Add(key, list, false);
                return;
            }
            var rest = new List<T>();
            foreach (var g in subs)
            {
                if (g.Count() < minSub || string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase)) rest.AddRange(g);
                else Split(g.Key, g.ToList(), depth + 1);
            }
            if (rest.Count > 0) Add(key, rest, true);
        }
        foreach (var g in items.GroupBy(x => BaseGroup(pathOf(x)), StringComparer.OrdinalIgnoreCase))
            Split(g.Key, g.ToList(), g.Key.Split('\\').Length);
        return res.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The nicest spelling among variants of one name that differ in case ("Tileset" rather than "TILESET"):
    /// one with lower-case letters, then the most common.</summary>
    public static string Pretty(IEnumerable<string> variants) =>
        variants.GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Key.Any(char.IsLower) ? 1 : 0).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault() ?? "";

    /// <summary>The decoded size of a source file (0 when unknown).</summary>
    private long Size160(uint fdid) => Math.Max(0, _s.Casc.SizeOf(fdid));
}

internal static class CatalogGrouping
{
    /// <summary>Grouped by a folder name without regard to case (the 1.12 file list spells many folders several ways),
    /// shown in its nicest spelling.</summary>
    public static List<(string Key, List<T> Items)> ByFolder<T>(this IEnumerable<T> items, Func<T, string> keyOf) =>
        items.GroupBy(keyOf, StringComparer.OrdinalIgnoreCase).Select(g => (UpdateCatalogScanner.Pretty(g.Select(keyOf)), g.ToList())).ToList();
}
