using System.Globalization;
using WoWData.Core.Casc;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions;

/// <summary>Where the name of a modern client's file came from (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 2.6).</summary>
public enum NameSource
{
    /// <summary>No name: shown as <c>#FileDataID.ext</c>.</summary>
    Number = 0,
    /// <summary>A candidate name whose name hash the client's own root has for this FileDataID (verified).</summary>
    Client = 1,
    /// <summary>The client's own list of its interface files (ManifestInterfaceData), checked by the root's name hash.</summary>
    Manifest = 2,
    /// <summary>A name of your 1.12 game found by its hash in a Classic Era root (FileDataIDs are shared between the
    /// flavors; this client's root has no hash for it).</summary>
    Era = 3,
    /// <summary>The listfile you chose; the client has no name hash to check it.</summary>
    Listfile = 4,
}

public static class NameSources
{
    public static string Code(NameSource s) => s switch
    {
        NameSource.Client => "client", NameSource.Manifest => "manifest", NameSource.Era => "era", NameSource.Listfile => "listfile", _ => "number",
    };

    public static NameSource Parse(string? code) => code switch
    {
        "client" => NameSource.Client, "manifest" => NameSource.Manifest, "era" => NameSource.Era, "listfile" => NameSource.Listfile, _ => NameSource.Number,
    };

    /// <summary>The source in words, for the details panel and "wds find info".</summary>
    public static string Describe(NameSource s) => s switch
    {
        NameSource.Client => "name checked by the client's own name hash",
        NameSource.Manifest => "name from the client's list of interface files, checked by its name hash",
        NameSource.Era => "your 1.12 name, found by its hash in Classic Era (not checked by this client)",
        NameSource.Listfile => "name from your listfile (not checked: the client has no name hash for it)",
        _ => "no name known: shown by its FileDataID",
    };

    /// <summary>A short form for lists: "verified", "1.12 name (Era)", "listfile", "no name".</summary>
    public static string Short(NameSource s) => s switch
    {
        NameSource.Client or NameSource.Manifest => "verified", NameSource.Era => "1.12 name (Era)", NameSource.Listfile => "listfile", _ => "no name",
    };
}

/// <summary>What <see cref="GameNames.Build"/> takes the names from (all optional; the user's own files only).</summary>
public sealed class GameNamesInput
{
    /// <summary>The file names of your 1.12 game (its archives' file lists): hashed into this client's root and, when
    /// <see cref="Era"/> is set, into Classic Era's.</summary>
    public IReadOnlyCollection<string>? Names112 { get; init; }
    /// <summary>A Classic Era storage (usually in the same Battle.net folder): its root's name hashes name the 1.12 files.</summary>
    public CascStorage? Era { get; init; }
    /// <summary>The client's own interface file list (ManifestInterfaceData: FileDataID, folder\file).</summary>
    public IEnumerable<(uint Fdid, string Path)>? Manifest { get; init; }
    /// <summary>A listfile you chose (lines <c>fdid;path</c>, or bare paths). Only FileDataIDs of this client are kept.</summary>
    public string? Listfile { get; init; }
}

/// <summary>
/// The names of a modern (CASC) client's files, version-neutral: a CASC client names files only through what the user
/// brings (design section 2.6). In order: candidate names whose hash this client's root has (the 1.12 file list, the
/// client's own interface list, listfile names), then 1.12 names found through Classic Era's root, then the rest of the
/// listfile (kept only for FileDataIDs this client has), else <c>#FileDataID</c>. Built once per index build (the index
/// keeps the result in its <c>fdid</c> table).
/// </summary>
public sealed class GameNames
{
    private readonly Dictionary<uint, (string Name, NameSource Source)> _byId = new();

    /// <summary>Names per source (for the status line and "wds versions open").</summary>
    public Dictionary<NameSource, int> Counts { get; } = new();
    /// <summary>FileDataIDs of the client (every locale).</summary>
    public int ClientFiles { get; private set; }
    /// <summary>Lines of the listfile whose FileDataID is not in this client (left out).</summary>
    public int ListfileOtherClients { get; private set; }
    /// <summary>Listfile names that the client's own hash contradicts (a different FileDataID): left out.</summary>
    public int ListfileContradicted { get; private set; }

    public int Count => _byId.Count;
    public IEnumerable<(uint Fdid, string Name, NameSource Source)> All => _byId.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Source));

    public string? NameOf(uint fdid) => _byId.TryGetValue(fdid, out var n) ? n.Name : null;
    public NameSource SourceOf(uint fdid) => _byId.TryGetValue(fdid, out var n) ? n.Source : NameSource.Number;

    /// <summary>The FileDataID of a name this table knows, else the root's name hash.</summary>
    public uint? IdOf(CascStorage storage, string path)
    {
        var p = path.Replace('/', '\\');
        return storage.Root.FileDataIdOfHash(CascNameHash.Of(p)) ?? _byPath?.GetValueOrDefault(CascNameHash.Normalize(p));
    }

    private Dictionary<string, uint>? _byPath;

    private bool Add(uint fdid, string name, NameSource src)
    {
        if (_byId.ContainsKey(fdid)) return false;
        _byId[fdid] = (name, src);
        Counts[src] = Counts.GetValueOrDefault(src) + 1;
        return true;
    }

    public static GameNames Build(CascStorage storage, GameNamesInput input, CancellationToken ct = default)
    {
        var g = new GameNames();
        var root = storage.Root;
        g.ClientFiles = root.Count;

        // 1. candidates whose hash this client's root has: the 1.12 names, the client's interface list
        var eraCandidates = new List<(string Name, ulong Hash)>();
        if (input.Names112 != null)
        {
            foreach (var raw in input.Names112)
            {
                var n = ClientData.ResolveName(raw.Replace('/', '\\'));
                ulong h = CascNameHash.Of(n);
                if (root.FileDataIdOfHash(h) is { } id) g.Add(id, n, NameSource.Client);
                else eraCandidates.Add((n, h));
            }
            ct.ThrowIfCancellationRequested();
        }
        if (input.Manifest != null)
            foreach (var (fdid, path) in input.Manifest)
            {
                var p = path.Replace('/', '\\');
                if (root.FileDataIdOfHash(CascNameHash.Of(p)) == fdid) g.Add(fdid, p, NameSource.Manifest);
            }

        // 2. the 1.12 names through Classic Era's root (FileDataIDs are shared between the flavors)
        if (input.Era != null)
        {
            var eraRoot = input.Era.Root;
            foreach (var (n, h) in eraCandidates)
                if (eraRoot.FileDataIdOfHash(h) is { } id && root.Contains(id)) g.Add(id, n, NameSource.Era);
            ct.ThrowIfCancellationRequested();
        }

        // 3. the listfile, filtered to this client; a name the root's hash confirms is verified
        if (!string.IsNullOrWhiteSpace(input.Listfile))
        {
            using var reader = new StreamReader(new FileStream(input.Listfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            string? line;
            int n = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (++n % 200000 == 0) ct.ThrowIfCancellationRequested();
                var l = line.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                int semi = l.IndexOf(';');
                if (semi > 0 && uint.TryParse(l.AsSpan(0, semi), NumberStyles.None, CultureInfo.InvariantCulture, out var fdid))
                {
                    if (!root.Contains(fdid)) { g.ListfileOtherClients++; continue; }
                    if (g._byId.ContainsKey(fdid)) continue;
                    var name = l[(semi + 1)..].Trim().Replace('/', '\\');
                    if (name.Length == 0) continue;
                    var byHash = root.FileDataIdOfHash(CascNameHash.Of(name));
                    if (byHash == fdid) g.Add(fdid, name, NameSource.Client);
                    else if (byHash != null) g.ListfileContradicted++;
                    else g.Add(fdid, name, NameSource.Listfile);
                }
                else
                {
                    var name = l.Replace('/', '\\');
                    if (root.FileDataIdOfHash(CascNameHash.Of(name)) is { } id) g.Add(id, name, NameSource.Client);
                }
            }
        }
        g._byPath = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (id, v) in g._byId) g._byPath.TryAdd(CascNameHash.Normalize(v.Name), id);
        return g;
    }
}
