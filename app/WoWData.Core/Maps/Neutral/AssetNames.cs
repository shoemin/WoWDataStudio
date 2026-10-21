using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using WoWData.Core.Casc;
using WoWData.Core.Mpq;

namespace WoWData.Core.Maps.Neutral;

/// <summary>
/// Names of assets in both directions: a file path to its FileDataID and back. The modern formats name files by
/// FileDataID only; the readers and writers of the map converter use this to fill <see cref="AssetRef.Path"/> (reading)
/// and <see cref="AssetRef.FileDataId"/> (writing). The user's rule: names come from the user's own files (their 1.12
/// client, a listfile they choose), never from a bundled listfile.
/// </summary>
public interface IAssetNames
{
    /// <summary>The FileDataID of a path, or null when unknown.</summary>
    uint? FileDataIdOf(string path);
    /// <summary>The path of a FileDataID, or null when unknown.</summary>
    string? PathOf(uint fileDataId);
}

/// <summary>A CASC storage's own names: the root's name hashes (path to FileDataID only) and a listfile the user loaded
/// into it (both directions).</summary>
public sealed class CascAssetNames : IAssetNames
{
    private readonly CascStorage _storage;

    public CascAssetNames(CascStorage storage) => _storage = storage;

    public uint? FileDataIdOf(string path) => _storage.FileDataIdOf(ClientData.ResolveName(path));
    public string? PathOf(uint fileDataId) => _storage.NameOf(fileDataId);
}

/// <summary>Several name sources; the first that knows a name wins.</summary>
public sealed class CompositeAssetNames : IAssetNames
{
    private readonly IAssetNames[] _sources;

    public CompositeAssetNames(params IAssetNames?[] sources) => _sources = sources.Where(s => s != null).ToArray()!;

    public uint? FileDataIdOf(string path)
    {
        foreach (var s in _sources)
            if (s.FileDataIdOf(path) is { } id) return id;
        return null;
    }

    public string? PathOf(uint fileDataId)
    {
        foreach (var s in _sources)
            if (s.PathOf(fileDataId) is { } p) return p;
        return null;
    }
}

/// <summary>
/// "1.12 paths hashed into Classic Era's root" (MAP_SURVEY_160 section 1): every name of the user's 1.12 client file
/// list (its archives' (listfile)s) is hashed (Jenkins lookup3, upper case, backslashes; <c>.mdx</c> read as <c>.m2</c>)
/// and looked up in the name hashes of a Classic Era (1.15.x) storage, whose FileDataIDs are the same as 1.60's
/// (MEASURED: 0 disagreements on 56,561 names). Measured coverage: 70.9% of all 1.12 names, 99-100% of what maps use.
/// <para>Path to FileDataID also answers paths that are not in the 1.12 list (any name Era's root hashes, for example
/// <c>..._obj0.adt</c>). FileDataID to path answers only the 1.12 names (the root holds hashes, not names) and returns
/// the 1.12 list's spelling.</para>
/// <para>Building the table hashes every 1.12 name (about 6 s for 79,733 names); <see cref="Save"/> / <see cref="Load"/>
/// keep it in a cache file (<c>fdid TAB path</c> per line) so later runs skip that.</para>
/// </summary>
public sealed class ClassicPathResolver : IAssetNames
{
    private readonly Dictionary<uint, string> _byId;
    private readonly Dictionary<string, uint> _byPath;
    private readonly CascStorage? _era;

    private ClassicPathResolver(Dictionary<uint, string> byId, CascStorage? era)
    {
        _byId = byId;
        _byPath = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (id, p) in byId) _byPath.TryAdd(Key(p), id);
        _era = era;
    }

    /// <summary>Number of 1.12 paths with a FileDataID.</summary>
    public int Count => _byId.Count;

    private static string Key(string path) => CascNameHash.Normalize(ClientData.ResolveName(path));

    /// <summary>Hashes every name of <paramref name="client112"/>'s file list into <paramref name="era"/>'s root.</summary>
    public static ClassicPathResolver Build(ClientData client112, CascStorage era)
    {
        var byId = new ConcurrentDictionary<uint, string>();
        var names = client112.Names().Values.Select(v => v.Path).ToList();
        Parallel.ForEach(names, p =>
        {
            if (era.Root.FileDataIdOfHash(CascNameHash.Of(ClientData.ResolveName(p))) is { } id)
                byId.AddOrUpdate(id, p, (_, old) => string.CompareOrdinal(old, p) <= 0 ? old : p);   // deterministic on a collision
        });
        return new ClassicPathResolver(new Dictionary<uint, string>(byId), era);
    }

    /// <summary>The cached table, or a fresh one built and saved when the cache is missing or unreadable.</summary>
    public static ClassicPathResolver LoadOrBuild(string cacheFile, ClientData client112, CascStorage era)
    {
        try
        {
            if (File.Exists(cacheFile)) return Load(cacheFile, era);
        }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException) { }
        var r = Build(client112, era);
        try { r.Save(cacheFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return r;
    }

    public void Save(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir != null) Directory.CreateDirectory(dir);
        using var w = new StreamWriter(file, false, new UTF8Encoding(false));
        foreach (var (id, p) in _byId.OrderBy(k => k.Key)) w.WriteLine($"{id.ToString(CultureInfo.InvariantCulture)}\t{p}");
    }

    /// <summary>Reads a cache file; <paramref name="era"/> (optional) answers paths outside the 1.12 list.</summary>
    public static ClassicPathResolver Load(string file, CascStorage? era = null)
    {
        var byId = new Dictionary<uint, string>();
        foreach (var line in File.ReadLines(file))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (!uint.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                throw new FormatException($"{file}: bad line '{line}'");
            byId[id] = line[(tab + 1)..];
        }
        return new ClassicPathResolver(byId, era);
    }

    public uint? FileDataIdOf(string path)
    {
        if (_byPath.TryGetValue(Key(path), out var id)) return id;
        return _era?.Root.FileDataIdOfHash(CascNameHash.Of(ClientData.ResolveName(path)));
    }

    public string? PathOf(uint fileDataId) => _byId.GetValueOrDefault(fileDataId);
}

/// <summary>Where a FileDataID of <see cref="AssetIdMap"/> came from.</summary>
public enum AssetIdOrigin
{
    /// <summary>The asset reference already had it.</summary>
    Model,
    /// <summary>Found by the name resolver (path to FileDataID).</summary>
    Resolved,
    /// <summary>A new local ID from the allocator.</summary>
    Allocated,
}

/// <summary>One asset as a writer stored it: the path it had in the model and the FileDataID written.</summary>
public sealed record AssetIdEntry(uint FileDataId, string? Path, string Kind, AssetIdOrigin Origin);

/// <summary>Hands out new FileDataIDs for files a modern target's root does not have.</summary>
public interface IFileDataIdAllocator
{
    uint Allocate(string path, string kind);
}

/// <summary>
/// Sequential local FileDataIDs from <see cref="First"/>. The default range starts at 0x7F000000 (2,130,706,432): far
/// above every ID in the clients measured (1.60.1.70450's largest is below 8,000,000), and the 1.60 output is meant for
/// our own native client and tools, which read the name map, not for Blizzard's client (MAP_CONVERSION_DESIGN.md).
/// </summary>
public sealed class LocalIdAllocator : IFileDataIdAllocator
{
    public const uint DefaultFirst = 0x7F000000;
    private long _next;

    public LocalIdAllocator(uint first = DefaultFirst)
    {
        First = first;
        _next = first;
    }

    public uint First { get; }

    public uint Allocate(string path, string kind) => (uint)(Interlocked.Increment(ref _next) - 1);
}

/// <summary>
/// The name map of a modern write: every asset (texture, model, tile file, minimap) the writer gave a FileDataID, with
/// the path it had in the model and where the ID came from. A path that is asked for twice gets the same ID. The map is
/// also an <see cref="IAssetNames"/>, so a reader of the written files finds the paths again (FileDataID to the model's
/// path; for ground textures that is the plain diffuse name although the ID is the <c>_s.blp</c> file's, see
/// <see cref="Adt160Writer"/>).
/// </summary>
public sealed class AssetIdMap : IAssetNames
{
    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, AssetIdEntry> _byId = new();
    private readonly List<AssetIdEntry> _entries = new();

    public AssetIdMap(IAssetNames? resolver = null, IFileDataIdAllocator? allocator = null)
    {
        Resolver = resolver;
        Allocator = allocator ?? new LocalIdAllocator();
    }

    public IAssetNames? Resolver { get; }
    public IFileDataIdAllocator Allocator { get; }

    public IReadOnlyList<AssetIdEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public IEnumerable<AssetIdEntry> Allocated => Entries.Where(e => e.Origin == AssetIdOrigin.Allocated);

    private static string Key(string path) => CascNameHash.Normalize(ClientData.ResolveName(path));

    /// <summary>Records an ID the model already had (so a reader of the output finds its path).</summary>
    public void Record(uint fileDataId, string? path, string kind)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(fileDataId)) return;
            var e = new AssetIdEntry(fileDataId, path, kind, AssetIdOrigin.Model);
            _byId[fileDataId] = e;
            _entries.Add(e);
            if (path != null) _byPath.TryAdd(Key(path), fileDataId);
        }
    }

    /// <summary>
    /// The ID of a path: one this map handed out before, else the resolver's (<paramref name="lookupPaths"/> are tried in
    /// order: for a ground texture the <c>_s.blp</c> name first), else a new local ID. <paramref name="origin"/> says
    /// which.
    /// </summary>
    public uint IdOf(string path, string kind, out AssetIdOrigin origin, params string[] lookupPaths)
    {
        var key = Key(path);
        lock (_gate)
        {
            if (_byPath.TryGetValue(key, out var known))
            {
                origin = _byId[known].Origin;
                return known;
            }
            uint id = 0;
            origin = AssetIdOrigin.Allocated;
            foreach (var p in lookupPaths.Length > 0 ? lookupPaths : new[] { path })
                if (Resolver?.FileDataIdOf(p) is { } r)
                {
                    id = r;
                    origin = AssetIdOrigin.Resolved;
                    break;
                }
            if (origin == AssetIdOrigin.Allocated)
                do id = Allocator.Allocate(path, kind); while (_byId.ContainsKey(id));
            var e = new AssetIdEntry(id, path, kind, origin);
            _byPath[key] = id;
            if (_byId.TryAdd(id, e)) _entries.Add(e);
            return id;
        }
    }

    public uint? FileDataIdOf(string path)
    {
        lock (_gate) return _byPath.TryGetValue(Key(path), out var id) ? id : null;
    }

    public string? PathOf(uint fileDataId)
    {
        lock (_gate) return _byId.TryGetValue(fileDataId, out var e) ? e.Path : null;
    }

    /// <summary>The map as text: <c>fdid TAB origin TAB kind TAB path</c> per line.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries.OrderBy(e => e.FileDataId))
            sb.Append(e.FileDataId.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(e.Origin).Append('\t').Append(e.Kind).Append('\t').AppendLine(e.Path);
        return sb.ToString();
    }
}
