using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Casc;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions.Catalog;

/// <summary>
/// The two sides of an update catalog, opened read only and shared by the app and "wds versions updates": the source
/// client (the 1.60 CASC client through <see cref="MapConversionSource"/>: its files, tables, maps and the names of the
/// user's 1.12 files found by their hashes in a Classic Era copy, plus the listfile the user chose), its high-resolution
/// texture variants (a second view of the same storage), and the user's 1.12 game. Names come only from the user's own
/// files, the listfile they chose and the source client's own files whose names its root confirms by hash (no bundled
/// or downloaded listfile, no keys). Dispose it when done.
/// </summary>
public sealed class UpdateCatalogSource : IDisposable
{
    private readonly Lazy<CascStorage> _highRes;
    private readonly string? _listfile;

    private UpdateCatalogSource(MapConversionSource map, ClientData client112, string cacheDir, string? listfile)
    {
        Map = map;
        Client112 = client112;
        CacheDir = cacheDir;
        _listfile = listfile;
        _highRes = new Lazy<CascStorage>(() => CascStorage.Open(Copy.Folder, null, new CascOpenOptions { PreferHighResTextures = true }), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public MapConversionSource Map { get; }
    public OtherClient Client => Map.Client;
    public ClientCopy Copy => Map.Client.Copy;
    public CascStorage Casc => Map.Client.Casc!;
    /// <summary>The Classic Era copy whose name hashes name the 1.12 files (null when none was found).</summary>
    public CascStorage? Era => Map.Era;
    public IMapSource Source => Map.Source;
    /// <summary>Names: the user's 1.12 files (hashed into Era), the listfile, the source's own root hashes.</summary>
    public IAssetNames Names => Map.Source.Names;
    /// <summary>The user's 1.12 game (read only).</summary>
    public ClientData Client112 { get; }
    /// <summary>Where the catalog and its helper caches are kept (the app's cache folder).</summary>
    public string CacheDir { get; }
    /// <summary>The same storage, choosing the high-resolution variant of each texture (opened on first use).</summary>
    public CascStorage HighRes => _highRes.Value;

    public string Label => $"{Copy.Name} ({Copy.Version}.{Copy.Build})";

    /// <summary>
    /// Opens <paramref name="copy"/> (the 1.60 CASC client; other versions are not a catalog source yet) against the user's
    /// 1.12 game. <paramref name="cacheRoot"/> is the app's cache folder (names in "names", catalogs in "catalog").
    /// </summary>
    public static UpdateCatalogSource Open(ClientCopy copy, string? listfile, ClientData client112, IEnumerable<ClientCopy>? found, string cacheRoot)
    {
        if (copy.Format != ClientFormat.Casc)
            throw new NotSupportedException($"{copy.Name} ({copy.Label}): the update catalog compares the modern 1.60 Classic client (CASC) with your game; other versions come later.");
        var map = MapConversionSource.Open(copy, listfile, client112, found, Path.Combine(cacheRoot, "names"));
        return new UpdateCatalogSource(map, client112, Path.Combine(cacheRoot, "catalog"), listfile);
    }

    private static string FileStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "-";
        var fi = new FileInfo(path);
        return $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>What the source side of a catalog depends on: the build (its configuration key), the Era copy used for
    /// names, the listfile, and the client's hotfix cache.</summary>
    public string SourceFingerprint() => string.Join(";", Copy.Folder.ToLowerInvariant(), Copy.Build.ToString(CultureInfo.InvariantCulture), Casc.BuildConfigKey,
        Era?.BuildConfigKey ?? "no-era", FileStamp(_listfile), FileStamp(HotfixCache(Copy)), "v" + UpdateCatalog.CurrentFormat.ToString(CultureInfo.InvariantCulture));

    /// <summary>What the 1.12 side depends on: the game's Data folder and its archives (names, sizes, times).</summary>
    public string TargetFingerprint() => Client112.DataDir.ToLowerInvariant() + "|" + Client112.Fingerprint();

    /// <summary>The client's hotfix cache (Cache\ADB\enUS\DBCache.bin), or null.</summary>
    public static string? HotfixCache(ClientCopy c)
    {
        var p = Path.Combine(c.Folder, "Cache", "ADB", "enUS", "DBCache.bin");
        return File.Exists(p) ? p : null;
    }

    public void Dispose()
    {
        if (_highRes.IsValueCreated) _highRes.Value.Dispose();
        Map.Dispose();
    }
}

/// <summary>
/// Keeps catalogs in the app's cache folder (one file per pair of fingerprints: a catalog is scanned again when the
/// source client or the user's game changed), plus the helper caches of the scan (the MD5 of every 1.12 file, the
/// pixel comparisons of changed textures).
/// </summary>
public static class UpdateCatalogStore
{
    public static string Key(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>The catalog file for a source and a target.</summary>
    public static string FileFor(UpdateCatalogSource s) => Path.Combine(s.CacheDir, $"catalog_{Key(s.SourceFingerprint() + "||" + s.TargetFingerprint())}.json");

    /// <summary>The cached catalog of these two sides, or null when there is none (or it is from another scan version).</summary>
    public static UpdateCatalog? LoadCached(UpdateCatalogSource s)
    {
        var f = FileFor(s);
        if (!File.Exists(f)) return null;
        try
        {
            var c = UpdateCatalog.Load(f);
            return c.Format == UpdateCatalog.CurrentFormat && c.SourceFingerprint == s.SourceFingerprint() && c.TargetFingerprint == s.TargetFingerprint() ? c : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The cached catalog, or a new scan (saved) when there is none, it is outdated or <paramref name="rescan"/>.</summary>
    public static UpdateCatalog LoadOrScan(UpdateCatalogSource s, bool rescan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!rescan && LoadCached(s) is { } cached) return cached;
        var cat = UpdateCatalogScanner.Scan(s, progress, ct);
        try { cat.Save(FileFor(s)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return cat;
    }
}
