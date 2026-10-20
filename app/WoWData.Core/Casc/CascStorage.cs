using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WoWData.Core.Casc;

/// <summary>
/// A local CASC storage of a modern WoW client, read-only (our own code, written from the public format descriptions;
/// see docs\research\CASC_INVESTIGATION.md). Chain: <c>.build.info</c> (or <c>versions</c>) -> build config ->
/// encoding file (found by its encoded key in the local index) -> root file -> FileDataID -> content key -> encoded
/// key -> <c>.idx</c> entry -> <c>data.###</c> -> BLTE. Nothing is ever written, decrypted or downloaded.
/// <para>Thread-safe for reads: archives are read with positional reads on shared handles; the encoding and root
/// files are loaded once, on first use. <see cref="LoadListfile"/> swaps its tables atomically.</para>
/// </summary>
public sealed class CascStorage : IDisposable
{
    private const int EntryHeaderSize = 0x1E;

    private readonly CascLocalIndex _index;
    private readonly Lazy<CascEncoding> _encoding;
    private readonly Lazy<CascRoot> _root;
    private readonly Dictionary<int, SafeFileHandle> _archives = new();
    private readonly object _gate = new();
    private bool _disposed;
    private volatile Listfile _listfile = Listfile.Empty;

    /// <summary>The launcher product code (<c>wow</c>, <c>wow_classic_beta</c>, <c>wowf</c> ...).</summary>
    public string Product { get; }
    /// <summary>The version string, e.g. <c>1.60.1.70450</c>.</summary>
    public string Version { get; }
    public int Build { get; }
    /// <summary>The build config's <c>build-name</c>, e.g. <c>WOW-70450patch1.60.1_ForeverBeta</c>.</summary>
    public string BuildName { get; }
    /// <summary>The CASC data folder (with <c>config</c>, <c>data</c>, <c>indices</c>).</summary>
    public string DataDirectory { get; }
    /// <summary>The build config hash.</summary>
    public string BuildConfigKey { get; }
    public CascConfig BuildConfig { get; }
    public CascLocalIndex LocalIndex => _index;

    /// <summary>The encoding file (loaded on first use).</summary>
    public CascEncoding Encoding => _encoding.Value;
    /// <summary>The root file (loaded on first use).</summary>
    public CascRoot Root => _root.Value;

    /// <summary>True when the root has name hashes for some or all files (all in WoD and the Classic flavors, about
    /// 8-12% in Retail and "1.60").</summary>
    public bool HasNames => Root.NameHashCount > 0;

    /// <summary>The options the storage was opened with.</summary>
    public CascOpenOptions Options { get; }

    private CascStorage(string dataDir, string product, string version, string buildConfigKey, CascConfig buildConfig, CascOpenOptions options)
    {
        Options = options;
        DataDirectory = dataDir;
        Product = product;
        Version = version;
        BuildConfigKey = buildConfigKey;
        BuildConfig = buildConfig;
        BuildName = buildConfig["build-name"] ?? "";
        var last = version.Split('.').LastOrDefault();
        Build = int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) ? b : 0;
        var idxDir = Path.Combine(dataDir, "data");
        _index = CascLocalIndex.Load(idxDir);
        if (_index.Count == 0) throw new InvalidDataException($"no .idx files in {idxDir}");
        _encoding = new Lazy<CascEncoding>(LoadEncoding, LazyThreadSafetyMode.ExecutionAndPublication);
        _root = new Lazy<CascRoot>(LoadRoot, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // ------------------------------------------------------------------ opening

    /// <summary>The launcher's flavor folders and their products (used when a flavor folder has no
    /// <c>.flavor.info</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> FlavorProducts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["_retail_"] = "wow",
        ["_ptr_"] = "wowt",
        ["_xptr_"] = "wowxptr",
        ["_beta_"] = "wow_beta",
        ["_classic_"] = "wow_classic",
        ["_classic_ptr_"] = "wow_classic_ptr",
        ["_classic_beta_"] = "wow_classic_beta",
        ["_classic_era_"] = "wow_classic_era",
        ["_classic_era_ptr_"] = "wow_classic_era_ptr",
        ["_anniversary_"] = "wow_anniversary",
        ["_forever_"] = "wowf",
    };

    /// <summary>
    /// Opens a storage. <paramref name="path"/> may be the CASC data folder (<c>Data</c> / <c>data</c>, with
    /// <c>config</c> and <c>data</c> inside), the install folder (with <c>.build.info</c> or <c>versions</c>), or a
    /// Battle.net flavor folder such as <c>_classic_beta_</c> (its product comes from <c>.flavor.info</c>, else from the
    /// folder name). <paramref name="product"/> picks the <c>.build.info</c> row; null = the flavor folder's product, or
    /// the first active row.
    /// </summary>
    public static CascStorage Open(string path, string? product = null, CascOpenOptions? options = null)
    {
        options ??= new CascOpenOptions();
        var dir = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        string installDir, dataDir;
        if (IsDataDir(dir))
        {
            dataDir = dir;
            installDir = Path.GetDirectoryName(dir) ?? dir;
        }
        else
        {
            installDir = dir;
            var flavorInfo = Path.Combine(dir, ".flavor.info");
            var parent = Path.GetDirectoryName(dir);
            bool isFlavor = File.Exists(flavorInfo) || (FlavorProducts.ContainsKey(Path.GetFileName(dir)) && parent != null && File.Exists(Path.Combine(parent, ".build.info")));
            if (isFlavor && parent != null)
            {
                if (product == null)
                {
                    if (File.Exists(flavorInfo))
                    {
                        var t = CascTable.Load(flavorInfo);
                        product = t.Rows.Select(r => r.GetValueOrDefault("Product Flavor") ?? r.Values.FirstOrDefault()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
                    }
                    product ??= FlavorProducts.GetValueOrDefault(Path.GetFileName(dir));
                }
                installDir = parent;
            }
            dataDir = Path.Combine(installDir, "Data");
            if (!IsDataDir(dataDir)) throw new DirectoryNotFoundException($"no CASC data folder (Data\\config + Data\\data) in {installDir}");
        }

        var buildInfo = Path.Combine(installDir, ".build.info");
        var versions = Path.Combine(installDir, "versions");
        if (File.Exists(buildInfo))
        {
            var t = CascTable.Load(buildInfo);
            var rows = t.Rows.ToList();
            IReadOnlyDictionary<string, string>? row;
            if (product != null)
            {
                var match = rows.Where(r => string.Equals(r.GetValueOrDefault("Product"), product, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count == 0)
                    throw new ArgumentException($"product '{product}' is not in {buildInfo} (products: {string.Join(", ", rows.Select(r => r.GetValueOrDefault("Product")))})", nameof(product));
                row = match.FirstOrDefault(r => r.GetValueOrDefault("Active") == "1") ?? match[0];
            }
            else
            {
                row = rows.FirstOrDefault(r => r.GetValueOrDefault("Active") == "1") ?? rows.FirstOrDefault();
            }
            if (row == null) throw new InvalidDataException($"{buildInfo} has no rows");
            var key = row.GetValueOrDefault("Build Key") ?? throw new InvalidDataException($"{buildInfo} has no Build Key column");
            var cfg = LoadBuildConfig(dataDir, key);
            var prod = row.GetValueOrDefault("Product") is { Length: > 0 } p0 ? p0 : product ?? cfg["build-uid"] ?? "wow";
            var ver = row.GetValueOrDefault("Version") is { Length: > 0 } v0 ? v0 : VersionFromBuildName(cfg["build-name"]);
            return new CascStorage(dataDir, prod, ver, key.ToLowerInvariant(), cfg, options);
        }
        if (File.Exists(versions))
        {
            var t = CascTable.Load(versions);
            var row = t.Rows.FirstOrDefault(r => string.Equals(r.GetValueOrDefault("Region"), "us", StringComparison.OrdinalIgnoreCase)) ?? t.Rows.FirstOrDefault()
                ?? throw new InvalidDataException($"{versions} has no rows");
            var key = row.GetValueOrDefault("BuildConfig") ?? throw new InvalidDataException($"{versions} has no BuildConfig column");
            var cfg = LoadBuildConfig(dataDir, key);
            var ver = row.GetValueOrDefault("VersionsName") is { Length: > 0 } v0 ? v0 : VersionFromBuildName(cfg["build-name"]);
            if (!ver.Contains('.') && row.GetValueOrDefault("BuildId") is { Length: > 0 } bid) ver = bid;
            var prod = product ?? cfg["build-uid"] ?? "wow";
            return new CascStorage(dataDir, prod, ver, key.ToLowerInvariant(), cfg, options);
        }
        throw new FileNotFoundException($"neither .build.info nor versions in {installDir}");
    }

    private static bool IsDataDir(string d) => Directory.Exists(Path.Combine(d, "config")) && Directory.Exists(Path.Combine(d, "data"))
        && Directory.EnumerateFiles(Path.Combine(d, "data"), "*.idx").Any();

    private static CascConfig LoadBuildConfig(string dataDir, string key)
    {
        var p = CascConfig.PathOf(dataDir, key.Trim());
        if (!File.Exists(p)) throw new FileNotFoundException($"build config {key} is not in {Path.Combine(dataDir, "config")}", p);
        return CascConfig.Load(p);
    }

    /// <summary>"WOW-20779patch6.2.3_Retail" -> "6.2.3.20779"; "" when the name has another shape.</summary>
    private static string VersionFromBuildName(string? name)
    {
        if (name == null) return "";
        var m = System.Text.RegularExpressions.Regex.Match(name, @"-(\d+)patch([\d.]+)");
        return m.Success ? m.Groups[2].Value + "." + m.Groups[1].Value : "";
    }

    // ------------------------------------------------------------------ encoding / root

    private CascEncoding LoadEncoding()
    {
        var parts = BuildConfig.Parts("encoding");
        if (parts.Length < 2) throw new InvalidDataException("build config has no encoding keys");
        var ekey = Convert.FromHexString(parts[1]);
        var data = ReadEncodedKey(ekey) ?? throw new CascMissingException($"the encoding file ({parts[1]}) is not in the local archives");
        CheckMd5(data, parts[0], "encoding");
        return new CascEncoding(data);
    }

    private CascRoot LoadRoot()
    {
        var ckeyHex = BuildConfig.Parts("root").FirstOrDefault() ?? throw new InvalidDataException("build config has no root key");
        var data = ReadContentKey(Convert.FromHexString(ckeyHex)) ?? throw new CascMissingException($"the root file ({ckeyHex}) is not in the local archives");
        CheckMd5(data, ckeyHex, "root");
        return CascRoot.Parse(data, Options.PreferHighResTextures);
    }

    private static void CheckMd5(byte[] data, string ckeyHex, string what)
    {
        var md5 = MD5.HashData(data);
        if (!Convert.ToHexString(md5).Equals(ckeyHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"the {what} file does not match its content key {ckeyHex}");
    }

    // ------------------------------------------------------------------ raw reads

    private SafeFileHandle Archive(int n)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_archives.TryGetValue(n, out var h)) return h;
            var p = Path.Combine(DataDirectory, "data", $"data.{n:D3}");
            h = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            _archives[n] = h;
            return h;
        }
    }

    /// <summary>The raw archive entry of an encoded key: 30-byte header plus BLTE frame; null when not local.</summary>
    public byte[]? ReadRawEntry(ReadOnlySpan<byte> ekey)
    {
        if (!_index.TryGet(ekey, out var e)) return null;
        if (e.Size < EntryHeaderSize + 8) throw new InvalidDataException($"index entry of {Convert.ToHexString(ekey)} is too small ({e.Size} bytes)");
        var buf = new byte[e.Size];
        var h = Archive(e.Archive);
        int got = 0;
        while (got < buf.Length)
        {
            int r = RandomAccess.Read(h, buf.AsSpan(got), e.Offset + got);
            if (r <= 0) throw new InvalidDataException($"data.{e.Archive:D3} ends inside the entry of {Convert.ToHexString(ekey)}");
            got += r;
        }
        // the entry header starts with the encoded key, byte-reversed
        int check = Math.Min(9, ekey.Length);
        for (int i = 0; i < check; i++)
            if (buf[15 - i] != ekey[i])
                throw new InvalidDataException($"data.{e.Archive:D3} @0x{e.Offset:X}: the entry header does not belong to {Convert.ToHexString(ekey)}");
        return buf;
    }

    /// <summary>Reads and decodes a file by its encoded key; null when not local.</summary>
    public byte[]? ReadEncodedKey(ReadOnlySpan<byte> ekey, bool zeroFillEncrypted = false) => ReadEncodedKey(ekey, zeroFillEncrypted, out _);

    private byte[]? ReadEncodedKey(ReadOnlySpan<byte> ekey, bool zeroFillEncrypted, out long encryptedBytes)
    {
        encryptedBytes = 0;
        var raw = ReadRawEntry(ekey);
        if (raw == null) return null;
        var frame = raw.AsSpan(EntryHeaderSize);
        if (!Blte.IsBlte(frame)) throw new InvalidDataException($"the entry of {Convert.ToHexString(ekey)} is not BLTE");
        return Blte.Decode(frame, zeroFillEncrypted, out encryptedBytes);
    }

    /// <summary>Reads a file by its content key (any of its local encoded keys); null when the key is not in the
    /// encoding file or none of its encoded keys is local.</summary>
    public byte[]? ReadContentKey(ReadOnlySpan<byte> ckey, bool zeroFillEncrypted = false)
    {
        var ekey = LocalEKey(ckey, out _);
        return ekey == null ? null : ReadEncodedKey(ekey, zeroFillEncrypted);
    }

    /// <summary>The first local encoded key of a content key, or null; <paramref name="known"/> tells whether the
    /// content key is in the encoding file at all.</summary>
    private byte[]? LocalEKey(ReadOnlySpan<byte> ckey, out bool known)
    {
        var enc = Encoding;
        int entry = enc.FindEntry(ckey);
        known = entry >= 0;
        if (entry < 0) return null;
        int n = enc.EKeyCountAt(entry);
        for (int i = 0; i < n; i++)
        {
            var ek = enc.EKeyAt(entry, i);
            if (_index.Contains(ek)) return ek.ToArray();
        }
        return null;
    }

    // ------------------------------------------------------------------ by FileDataID / name

    /// <summary>
    /// Reads a file by FileDataID (the enUS record, see <see cref="CascRoot"/>). Returns null when the FileDataID is not
    /// in the root. Throws <see cref="CascMissingException"/> when its content is not local, and
    /// <see cref="CascEncryptedException"/> when a chunk is encrypted, unless <paramref name="zeroFillEncrypted"/> is
    /// set: then encrypted chunks come back as zero bytes (e.g. the encrypted tail section of a DB2), and a file whose
    /// chunks are all encrypted still throws.
    /// </summary>
    public byte[]? ReadFileDataId(uint fdid, bool zeroFillEncrypted = false)
    {
        if (!Root.TryGetContentKey(fdid, out var ckey)) return null;
        var ekey = LocalEKey(ckey, out bool known);
        if (!known) throw new CascMissingException($"FileDataID {fdid}: its content key {Convert.ToHexString(ckey)} has no encoding entry");
        if (ekey == null) throw new CascMissingException($"FileDataID {fdid} is not in the local archives (not downloaded)");
        byte[] data;
        long enc;
        try
        {
            data = ReadEncodedKey(ekey, zeroFillEncrypted, out enc)!;
        }
        catch (CascEncryptedException e)
        {
            throw new CascEncryptedException($"FileDataID {fdid} is encrypted (key {e.KeyName ?? "?"})", e.KeyName);
        }
        if (enc > 0 && enc >= data.Length) throw new CascEncryptedException($"FileDataID {fdid} is fully encrypted");
        return data;
    }

    /// <summary>Reads a file by path: the root's name hash, else a loaded listfile. Null when the name is unknown.</summary>
    public byte[]? ReadName(string path, bool zeroFillEncrypted = false)
    {
        var id = FileDataIdOf(path);
        return id == null ? null : ReadFileDataId(id.Value, zeroFillEncrypted);
    }

    /// <summary>The FileDataID of a path: the root's name hash, else a loaded listfile; null when unknown.</summary>
    public uint? FileDataIdOf(string path)
    {
        ulong h = CascNameHash.Of(path);
        return Root.FileDataIdOfHash(h) ?? (_listfile.ByHash.TryGetValue(h, out var id) ? id : null);
    }

    /// <summary>The name of a FileDataID, from a loaded listfile only (the root holds hashes, not names).</summary>
    public string? NameOf(uint fdid) => _listfile.ByFdid.GetValueOrDefault(fdid);

    /// <summary>The number of names known from loaded listfiles.</summary>
    public int ListfileNameCount => _listfile.ByFdid.Count;

    /// <summary>
    /// Loads a listfile the user chose (never bundled). Lines are either <c>fdid;path</c> (the community format) or a
    /// bare <c>path</c> (CascLib-style name lists without IDs): a bare path is hashed and kept only when the root has that
    /// name hash, which also gives its FileDataID. Later files add to earlier ones. Returns the number of names taken.
    /// </summary>
    public int LoadListfile(string path)
    {
        var old = _listfile;
        var byFdid = new Dictionary<uint, string>(old.ByFdid);
        var byHash = new Dictionary<ulong, uint>(old.ByHash);
        int taken = 0;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var l = line.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            int semi = l.IndexOf(';');
            uint fdid;
            string name;
            if (semi > 0 && uint.TryParse(l.AsSpan(0, semi), NumberStyles.None, CultureInfo.InvariantCulture, out fdid))
            {
                name = l[(semi + 1)..].Trim();
                if (name.Length == 0) continue;
            }
            else
            {
                name = l;
                var id = Root.FileDataIdOfHash(CascNameHash.Of(name));
                if (id == null) continue;
                fdid = id.Value;
            }
            byFdid[fdid] = name;
            byHash[CascNameHash.Of(name)] = fdid;
            taken++;
        }
        _listfile = new Listfile(byFdid, byHash);
        return taken;
    }

    /// <summary>Every FileDataID of the root in ascending order: those whose record is enUS or all-locale, or with
    /// <paramref name="allLocales"/> every FileDataID.</summary>
    public IEnumerable<uint> FileDataIds(bool allLocales = false) => Root.FileDataIds(allLocales);

    /// <summary>Whether a FileDataID can be read here (see <see cref="CascFileState"/>). NotLocal wins over Encrypted
    /// (a file that is not downloaded cannot be read either way).</summary>
    public CascFileState State(uint fdid)
    {
        if (!Root.TryGetContentKey(fdid, out var ckey)) return CascFileState.Unknown;
        var enc = Encoding;
        int entry = enc.FindEntry(ckey);
        if (entry < 0) return CascFileState.Unknown;
        int n = enc.EKeyCountAt(entry);
        for (int i = 0; i < n; i++)
        {
            var ek = enc.EKeyAt(entry, i);
            if (_index.Contains(ek)) return enc.IsEncrypted(ek) ? CascFileState.Encrypted : CascFileState.Local;
        }
        return CascFileState.NotLocal;
    }

    /// <summary>The decoded size of a FileDataID's content (from the encoding file), or -1.</summary>
    public long SizeOf(uint fdid)
    {
        if (!Root.TryGetContentKey(fdid, out var ckey)) return -1;
        return Encoding.ContentSize(ckey);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var h in _archives.Values) h.Dispose();
            _archives.Clear();
        }
    }

    private sealed class Listfile
    {
        public static readonly Listfile Empty = new(new Dictionary<uint, string>(), new Dictionary<ulong, uint>());
        public readonly Dictionary<uint, string> ByFdid;
        public readonly Dictionary<ulong, uint> ByHash;

        public Listfile(Dictionary<uint, string> byFdid, Dictionary<ulong, uint> byHash)
        {
            ByFdid = byFdid;
            ByHash = byHash;
        }
    }
}
