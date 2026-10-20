using System.Text.RegularExpressions;
using WoWData.Core.Casc;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions;

/// <summary>A file found in another client: its path (or #FileDataID when it has no known name) and where it is read from.</summary>
public readonly record struct ClientFileHit(string Path, string Source);

/// <summary>
/// The files of another client, the same way for MPQ and CASC clients (0.14.0, experimental, read only). A path is
/// <c>World\Maps\Azeroth\Azeroth.wdt</c>; a CASC file can also be named by its FileDataID: <c>#775971</c> (or just the
/// number). MPQ clients list every file; a CASC client lists only the names it knows (a listfile the user chose).
/// </summary>
public interface IClientFiles : IDisposable
{
    /// <summary>True when wildcards can be searched (MPQ: always; CASC: with a listfile).</summary>
    bool CanList { get; }
    /// <summary>Number of files (MPQ: listed files; CASC: FileDataIDs of the English / all-language content).</summary>
    int FileCount { get; }
    /// <summary>The file's bytes, or null when the client has no such file. A CASC file that is not downloaded or is
    /// encrypted throws <see cref="ClientFileException"/> with the reason.</summary>
    byte[]? Read(string pathOrId);
    /// <summary>Files matching a wildcard (* and ?), sorted; empty when the client cannot list (see <see cref="CanList"/>).</summary>
    IReadOnlyList<ClientFileHit> Glob(string pattern);
    /// <summary>Where a file is read from (MPQ: the archive; CASC: local / not downloaded / encrypted), or null.</summary>
    string? SourceOf(string pathOrId);
}

public static class ClientFileKinds
{
    /// <summary>
    /// The extension of a file known only by its FileDataID, from its first bytes: BLP2 .blp, MD20/MD21 .m2, SKIN .skin,
    /// WDBC .dbc, WDB2-WDB6/WDC1-WDC5 .db2, and the chunked map files by their second chunk (MPHD .wdt, MHDR .adt,
    /// MAOF .wdl, MOHD and MOGP .wmo; MVER + texture or placement chunks: the split .adt files); else .bin.
    /// </summary>
    public static string GuessExtension(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4) return ".bin";
        var m = System.Text.Encoding.ASCII.GetString(d[..4]);
        switch (m)
        {
            case "BLP2": case "BLP1": return ".blp";
            case "MD20": case "MD21": return ".m2";
            case "SKIN": return ".skin";
            case "WDBC": return ".dbc";
            case "WDB2": case "WDB3": case "WDB4": case "WDB5": case "WDB6": case "WDC1": case "WDC2": case "WDC3": case "WDC4": case "WDC5": case "1SDC": return ".db2";
        }
        // chunked files store the magic reversed: "REVM" = MVER
        if (m == "REVM" && d.Length >= 16)
        {
            var next = System.Text.Encoding.ASCII.GetString(d.Slice(12, 4));
            return next switch
            {
                "DHPM" => ".wdt",
                "RDHM" => ".adt",
                "FOAM" => ".wdl",
                "DHOM" or "PGOM" => ".wmo",
                "PMAM" or "XETM" or "DIDM" or "XDMM" or "FDDM" or "FDOM" or "OMWM" or "DIMM" => ".adt",
                _ => ".bin",
            };
        }
        return ".bin";
    }
}

public sealed class ClientFileException : Exception
{
    public ClientFileException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>An MPQ client's files (<see cref="ClientData"/>, in the client's own archive order).</summary>
public sealed class MpqClientFiles : IClientFiles
{
    public ClientData Data { get; }
    public MpqClientFiles(ClientData data) => Data = data;

    public bool CanList => true;
    public int FileCount => Data.Names().Count;
    public byte[]? Read(string pathOrId) => Data.Read(pathOrId);

    public IReadOnlyList<ClientFileHit> Glob(string pattern)
    {
        var names = Data.Names();
        return Data.Glob(pattern.Replace('/', '\\')).Select(p => new ClientFileHit(p, names.TryGetValue(p.ToUpperInvariant(), out var lf) ? lf.Archive : "")).ToList();
    }

    public string? SourceOf(string pathOrId) => Data.Find(pathOrId)?.Name;
    public void Dispose() => Data.Dispose();
}

/// <summary>A CASC client's files (<see cref="CascStorage"/>): by FileDataID, by a name the root hashes, or by a listfile name.</summary>
public sealed class CascClientFiles : IClientFiles
{
    private static readonly Regex IdRe = new(@"^#?(\d+)$", RegexOptions.CultureInvariant);
    private List<ClientFileHit>? _named;

    public CascStorage Storage { get; }
    public CascClientFiles(CascStorage storage) => Storage = storage;

    public bool CanList => Storage.ListfileNameCount > 0;
    public int FileCount => Storage.FileDataIds().Count();

    /// <summary>The FileDataID of a path or "#id", or null when the name is not known.</summary>
    public uint? IdOf(string pathOrId)
    {
        var t = pathOrId.Trim();
        var m = IdRe.Match(t);
        if (m.Success && uint.TryParse(m.Groups[1].Value, out var id)) return id;
        return Storage.FileDataIdOf(t.Replace('/', '\\'));
    }

    public byte[]? Read(string pathOrId)
    {
        var id = IdOf(pathOrId);
        if (id == null) return null;
        try { return Storage.ReadFileDataId(id.Value); }
        catch (CascMissingException e) { throw new ClientFileException($"{pathOrId} (#{id}) is not on this computer: {e.Message}", e); }
        catch (CascEncryptedException e) { throw new ClientFileException($"{pathOrId} (#{id}) is encrypted and cannot be read{(e.KeyName != null ? $" (key {e.KeyName})" : "")}.", e); }
    }

    public IReadOnlyList<ClientFileHit> Glob(string pattern)
    {
        if (!CanList) return Array.Empty<ClientFileHit>();
        _named ??= Storage.FileDataIds().Select(id => (id, name: Storage.NameOf(id))).Where(x => x.name != null)
            .Select(x => new ClientFileHit(x.name!, "#" + x.id)).OrderBy(h => h.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var re = new Regex("^" + Regex.Escape(pattern.Replace('/', '\\')).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return _named.Where(h => re.IsMatch(h.Path)).ToList();
    }

    public string? SourceOf(string pathOrId)
    {
        var id = IdOf(pathOrId);
        if (id == null) return null;
        return Storage.State(id.Value) switch
        {
            CascFileState.Local => $"#{id} local",
            CascFileState.NotLocal => $"#{id} not downloaded",
            CascFileState.Encrypted => $"#{id} encrypted",
            _ => null,
        };
    }

    /// <summary>Loads the user's listfile (names for the FileDataIDs); returns the number of names taken.</summary>
    public int LoadListfile(string path)
    {
        _named = null;
        return Storage.LoadListfile(path);
    }

    public void Dispose() => Storage.Dispose();
}
