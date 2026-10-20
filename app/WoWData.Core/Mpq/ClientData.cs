using System.Text.RegularExpressions;

namespace WoWData.Core.Mpq;

public enum SourceKind { Archive, Loose, Deleted, Hidden }

/// <summary>Where a file was found.</summary>
/// <summary>The archive set of a Data folder: 1.x split archives, or the common/locale archives of TBC 2.4.3 and WotLK 3.3.5a.</summary>
public enum ArchiveLayout { Split, TBC, WotLK }

public sealed record FileSource(SourceKind Kind, string Name, MpqArchive? Archive = null, string? LoosePath = null)
{
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Name}";
}

/// <summary>A listed archive path and the archive the client loads it from.</summary>
public readonly record struct ListedFile(string Path, string Archive);

/// <summary>
/// The client's view of its data (port of wowdata/vfs.py): archives in the client's priority order, plus optional
/// loose overlays.
///
/// Mount order (PatchFiles_MountArchives @00403740 in our decompile):
///   1. Data\patch-?.MPQ (exactly one character after the dash), sorted descending;
///   2. Data\patch.MPQ;  3. speech2.MPQ if present;
///   4. model, texture, terrain, wmo, sound, misc, interface, fonts, speech, dbc.
/// A file is taken from the first archive that has it. A delete marker stops the search: the client's
/// SFile_LocateFile @006549a0 reports the file as missing (patch.MPQ holds 5,708 of them).
/// base.MPQ and backup.MPQ are never mounted by the game.
/// </summary>
public sealed class ClientData : IDisposable
{
    public static readonly string[] BaseArchives =
        { "model.MPQ", "texture.MPQ", "terrain.MPQ", "wmo.MPQ", "sound.MPQ", "misc.MPQ", "interface.MPQ", "fonts.MPQ", "speech.MPQ", "dbc.MPQ" };
    public static readonly string[] UnmountedArchives = { "base.MPQ", "backup.MPQ" };
    public static readonly string[] StockArchives = BaseArchives.Concat(UnmountedArchives).Concat(new[] { "patch.MPQ", "patch-2.MPQ" }).ToArray();

    private static readonly Regex PatchRe = new(@"^patch-[^.]\.mpq$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record MountedArchive(string Name, MpqArchive Archive, bool Mounted);

    public string DataDir { get; }
    public IReadOnlyList<string> Overlays { get; }
    public List<MountedArchive> Archives { get; } = new();

    private Dictionary<string, ListedFile>? _names;
    private Dictionary<string, ListedFile>? _deleted;
    private readonly object _namesLock = new();

    public ClientData(string dataDir, bool includeUnmounted = false, IEnumerable<string>? overlays = null)
    {
        DataDir = dataDir;
        Overlays = (overlays ?? Array.Empty<string>()).Where(o => !string.IsNullOrEmpty(o) && Directory.Exists(o)).ToList();
        try
        {
            foreach (var n in ClientArchiveOrder(dataDir))
                Archives.Add(new MountedArchive(n, new MpqArchive(System.IO.Path.Combine(dataDir, n)), true));
            if (includeUnmounted)
                foreach (var n in UnmountedArchives)
                {
                    var real = FindCase(dataDir, n);
                    if (real != null) Archives.Add(new MountedArchive(real, new MpqArchive(System.IO.Path.Combine(dataDir, real)), false));
                }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>A view of exactly these archives (names relative to the Data folder), highest priority first: for other
    /// client versions read with some archives left out (experimental reader, "Blizzard archives only").</summary>
    public ClientData(string dataDir, IReadOnlyList<string> archives)
    {
        DataDir = dataDir;
        Overlays = Array.Empty<string>();
        try
        {
            foreach (var n in archives)
                Archives.Add(new MountedArchive(n, new MpqArchive(System.IO.Path.Combine(dataDir, n)), true));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var a in Archives) a.Archive.Dispose();
    }

    /// <summary>Normalized archive path: backslashes, no leading separator, .mdx/.mdl read as .m2 (M2Cache_GetShared @00706a50).</summary>
    public static string ResolveName(string path)
    {
        var p = StormCrypto.Normalize(path).TrimStart('\\');
        if (p.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            p = p[..^4] + ".m2";
        return p;
    }

    /// <summary>
    /// Which archive set a Data folder has: the 1.x split archives (model.MPQ, texture.MPQ, ...) or the TBC/WotLK
    /// common/locale archives. Read from the folder, like the clients do: TBC 2.4.3 tests for common.MPQ (0x402540);
    /// WotLK's base-archive table has the lichking archives.
    /// </summary>
    public static ArchiveLayout DetectLayout(string dataDir)
    {
        if (FindCase(dataDir, "common.MPQ") == null) return ArchiveLayout.Split;
        return FindCase(dataDir, "lichking.MPQ") != null ? ArchiveLayout.WotLK : ArchiveLayout.TBC;
    }

    /// <summary>The locale folder of a TBC/WotLK Data folder (enUS, enGB, ...): the one holding locale-&lt;loc&gt;.MPQ.</summary>
    public static string? DetectLocale(string dataDir)
    {
        foreach (var d in Directory.EnumerateDirectories(dataDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var loc = System.IO.Path.GetFileName(d);
            if (LocaleRe.IsMatch(loc) && FindCase(d, $"locale-{loc}.MPQ") != null) return loc;
        }
        return null;
    }

    /// <summary>Archive file names in the client's lookup order (highest priority first). TBC/WotLK names of locale
    /// archives are relative to the Data folder (<c>enUS\patch-enUS.MPQ</c>).</summary>
    public static List<string> ClientArchiveOrder(string dataDir)
    {
        var layout = DetectLayout(dataDir);
        if (layout != ArchiveLayout.Split) return ExpansionArchiveOrder(dataDir, layout);
        var patches = Directory.EnumerateFiles(dataDir).Select(System.IO.Path.GetFileName).Where(n => n != null && PatchRe.IsMatch(n)).Cast<string>().ToList();
        // Python: sorted(key=str.lower, reverse=True)
        patches.Sort((a, b) => string.CompareOrdinal(b.ToLowerInvariant(), a.ToLowerInvariant()));
        var order = new List<string>(patches) { "patch.MPQ" };
        if (File.Exists(System.IO.Path.Combine(dataDir, "speech2.MPQ"))) order.Add("speech2.MPQ");
        order.AddRange(BaseArchives);
        var outList = new List<string>();
        foreach (var n in order)
        {
            var real = FindCase(dataDir, n);
            if (real != null) outList.Add(real);
        }
        return outList;
    }

    /// <summary>
    /// TBC 2.4.3 / WotLK 3.3.5a mount order, read from each Wow.exe (patch-list builder 0x405260 / 0x405AB0, mount
    /// function 0x4054E0 / 0x405DD0; docs\research\MPQ_ERA_DELTA.md section 1) and confirmed by the data:
    ///   1. every Data\patch-?.MPQ and Data\&lt;loc&gt;\patch-&lt;loc&gt;-?.MPQ (one character), sorted by full path, descending,
    ///      case-insensitive (comparator -strnicmp), the first wins;
    ///   2. the exact names: TBC &lt;loc&gt;\patch-&lt;loc&gt;.MPQ then patch.MPQ, WotLK patch.MPQ then &lt;loc&gt;\patch-&lt;loc&gt;.MPQ;
    ///   3. the base archives in table order (earlier entries win).
    /// base-&lt;loc&gt;.MPQ, backup-&lt;loc&gt;.MPQ and the streaming archives are never mounted.
    /// </summary>
    private static List<string> ExpansionArchiveOrder(string dataDir, ArchiveLayout layout)
    {
        var loc = DetectLocale(dataDir);
        var patches = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dataDir))
        {
            var n = System.IO.Path.GetFileName(f);
            if (PatchRe.IsMatch(n)) patches.Add(n);
        }
        if (loc != null)
        {
            var locRe = new Regex($@"^patch-{Regex.Escape(loc)}-[^.]\.mpq$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var f in Directory.EnumerateFiles(System.IO.Path.Combine(dataDir, loc)))
            {
                var n = System.IO.Path.GetFileName(f);
                if (locRe.IsMatch(n)) patches.Add(loc + "\\" + n);
            }
        }
        patches.Sort((a, b) => string.CompareOrdinal(b.ToLowerInvariant(), a.ToLowerInvariant()));
        var order = new List<string>(patches);
        string L(string n) => loc == null ? n : loc + "\\" + n.Replace("<loc>", loc);
        if (layout == ArchiveLayout.TBC) { if (loc != null) order.Add(L("patch-<loc>.MPQ")); order.Add("patch.MPQ"); }
        else { order.Add("patch.MPQ"); if (loc != null) order.Add(L("patch-<loc>.MPQ")); }
        if (layout == ArchiveLayout.TBC) order.AddRange(new[] { "expansion.MPQ", "common.MPQ" });
        else order.AddRange(new[] { "expansion.MPQ", "lichking.MPQ", "common.MPQ", "common-2.MPQ" });
        if (loc != null)
        {
            var locs = layout == ArchiveLayout.TBC
                ? new[] { "locale-<loc>.MPQ", "speech-<loc>.MPQ", "expansion-locale-<loc>.MPQ", "expansion-speech-<loc>.MPQ" }
                : new[] { "locale-<loc>.MPQ", "speech-<loc>.MPQ", "expansion-locale-<loc>.MPQ", "lichking-locale-<loc>.MPQ",
                          "expansion-speech-<loc>.MPQ", "lichking-speech-<loc>.MPQ" };
            order.AddRange(locs.Select(L));
        }
        var outList = new List<string>();
        foreach (var n in order)
        {
            var dir = System.IO.Path.GetDirectoryName(n);
            var real = FindCase(string.IsNullOrEmpty(dir) ? dataDir : System.IO.Path.Combine(dataDir, dir), System.IO.Path.GetFileName(n));
            if (real != null && !outList.Contains(string.IsNullOrEmpty(dir) ? real : dir + "\\" + real, StringComparer.OrdinalIgnoreCase))
                outList.Add(string.IsNullOrEmpty(dir) ? real : dir + "\\" + real);
        }
        return outList;
    }

    private static readonly Regex LocaleRe = new(@"^[a-z]{2}[A-Z]{2}$", RegexOptions.CultureInvariant);

    private static string? FindCase(string dir, string name)
    {
        if (!File.Exists(System.IO.Path.Combine(dir, name))) return null;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var fn = System.IO.Path.GetFileName(f);
            if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)) return fn;
        }
        return null;
    }

    public MpqArchive? Archive(string name) =>
        Archives.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.Archive;

    // ------------------------------------------------------------------ lookup

    private static string? Loose(string overlay, string name)
    {
        var p = System.IO.Path.Combine(overlay, name.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        if (File.Exists(p)) return p;
        return null;   // NTFS is case-insensitive already
    }

    /// <summary>The first source that has the path (overlays first, then archives in priority order), or null. A delete
    /// marker hides the file in all lower-priority archives.</summary>
    public FileSource? Find(string path)
    {
        var name = ResolveName(path);
        foreach (var o in Overlays)
        {
            var p = Loose(o, name);
            if (p != null) return new FileSource(SourceKind.Loose, o, LoosePath: p);
        }
        foreach (var a in Archives)
        {
            var st = a.Archive.EntryState(name);
            if (st == MpqEntryState.File) return new FileSource(SourceKind.Archive, a.Name, a.Archive);
            if (st != MpqEntryState.None) return null;
        }
        return null;
    }

    /// <summary>Every source of a path, highest priority first; a delete marker is listed as Deleted and the copies
    /// below it as Hidden.</summary>
    public List<FileSource> Where(string path)
    {
        var name = ResolveName(path);
        var outList = new List<FileSource>();
        foreach (var o in Overlays)
        {
            var p = Loose(o, name);
            if (p != null) outList.Add(new FileSource(SourceKind.Loose, o, LoosePath: p));
        }
        bool hidden = false;
        foreach (var a in Archives)
        {
            var st = a.Archive.EntryState(name);
            if (st == MpqEntryState.File) outList.Add(new FileSource(hidden ? SourceKind.Hidden : SourceKind.Archive, a.Name, a.Archive));
            else if (st != MpqEntryState.None) { outList.Add(new FileSource(SourceKind.Deleted, a.Name, a.Archive)); hidden = true; }
        }
        return outList;
    }

    public bool Exists(string path) => Find(path) != null;

    /// <summary>Bytes of the winning copy, or null.</summary>
    public byte[]? Read(string path)
    {
        var src = Find(path);
        if (src == null) return null;
        if (src.Kind == SourceKind.Loose) return File.ReadAllBytes(src.LoosePath!);
        return src.Archive!.Read(ResolveName(path));
    }

    // ------------------------------------------------------------------ listing

    /// <summary>{UPPER path: (path as listed, winning archive)} from every archive's (listfile). Files hidden by a
    /// delete marker are left out (see <see cref="DeletedNames"/>).</summary>
    public IReadOnlyDictionary<string, ListedFile> Names()
    {
        lock (_namesLock)
        {
            if (_names != null) return _names;
            var names = new Dictionary<string, ListedFile>(StringComparer.Ordinal);
            var deleted = new Dictionary<string, ListedFile>(StringComparer.Ordinal);
            foreach (var a in Archives)
            {
                foreach (var nm in a.Archive.Listfile())
                {
                    if (nm.StartsWith('(')) continue;
                    var u = nm.ToUpperInvariant();
                    if (names.ContainsKey(u) || deleted.ContainsKey(u)) continue;
                    var st = a.Archive.EntryState(nm);
                    if (st == MpqEntryState.File) names[u] = new ListedFile(nm, a.Name);
                    else if (st != MpqEntryState.None) deleted[u] = new ListedFile(nm, a.Name);
                }
            }
            _deleted = deleted;
            _names = names;
            return names;
        }
    }

    /// <summary>{UPPER path: (path, archive with the delete marker)}: files the client cannot load.</summary>
    public IReadOnlyDictionary<string, ListedFile> DeletedNames()
    {
        Names();
        return _deleted!;
    }

    /// <summary>Archive paths matching a wildcard pattern (* and ?; case-insensitive; '\' or '/').</summary>
    public List<string> Glob(string pattern)
    {
        var pat = pattern.EndsWith('*') ? StormCrypto.Normalize(pattern).ToUpperInvariant() : ResolveName(pattern).ToUpperInvariant();
        var re = new Regex("^" + Regex.Escape(pat).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return Names().Where(kv => re.IsMatch(kv.Key)).Select(kv => kv.Value.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>A fingerprint of the mounted archives (names, sizes, write times): the cached index is rebuilt when it changes.</summary>
    public string Fingerprint()
    {
        var parts = Archives.Where(a => a.Mounted).Select(a =>
        {
            var fi = new FileInfo(a.Archive.Path);
            return $"{a.Name}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        });
        return string.Join(";", parts);
    }
}
