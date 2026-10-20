using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using WoWData.Core.Casc;

namespace WoWData.Core.Db2;

/// <summary>One field of a table in the client's own field table (the DB2 meta data in the executable).</summary>
public sealed class Db2ExeField
{
    /// <summary>Byte offset of the field in the client's in-memory record.</summary>
    public int Offset { get; init; }
    /// <summary>Number of elements (1, or the array length).</summary>
    public int ArrayCount { get; init; }
    /// <summary>The client's type code (see <see cref="Db2Schema.TypeOf"/>).</summary>
    public int RawType { get; init; }
    /// <summary>The client's flag bits for the field (see <see cref="Db2Schema"/>).</summary>
    public int RawFlags { get; init; }
}

/// <summary>A table the executable knows: its name, FileDataID, shape and field table.</summary>
public sealed class Db2CatalogEntry
{
    public string Name { get; init; } = "";
    public uint FileDataId { get; init; }
    public int FieldCount { get; init; }
    /// <summary>The client's in-memory record size (not the file's record size).</summary>
    public int RecordSize { get; init; }
    public int HotfixFieldCount { get; init; }
    /// <summary>The field that holds the ID inside the record, or -1 (the ID is not a field: ID list).</summary>
    public int IdField { get; init; }
    /// <summary>The table hash (equals the file header's table hash).</summary>
    public uint TableHash { get; init; }
    /// <summary>The layout hash (equals the file header's layout hash when the file matches this executable).</summary>
    public uint LayoutHash { get; init; }
    /// <summary>File offset of the meta structure in the executable (for diagnostics).</summary>
    public long MetaOffset { get; init; }
    public IReadOnlyList<Db2ExeField> Fields { get; init; } = Array.Empty<Db2ExeField>();
    public IReadOnlyList<Db2ExeField> HotfixFields { get; init; } = Array.Empty<Db2ExeField>();

    /// <summary>The field types for <see cref="Db2ReadOptions.Schema"/>.</summary>
    public Db2Schema Schema => new(Fields.Select(f => new Db2SchemaField(Db2Schema.TypeOf(f.RawType, f.RawFlags), f.ArrayCount, (f.RawFlags & Db2Schema.FlagLocalized) != 0, f.RawType, f.RawFlags)).ToList());

    public override string ToString() => $"{Name} (FDID {FileDataId}, {FieldCount} fields)";
}

/// <summary>A field type from the client's field table.</summary>
public sealed record Db2SchemaField(Db2ValueType Type, int ArrayCount, bool Localized, int RawType, int RawFlags);

/// <summary>The value types of a table's fields, in file order (one entry per field; arrays have ArrayCount > 1).</summary>
public sealed class Db2Schema
{
    public Db2Schema(IReadOnlyList<Db2SchemaField> fields) => Fields = fields;

    public IReadOnlyList<Db2SchemaField> Fields { get; }

    /// <summary>Field flag: the string is localized (the client's "lang" strings).</summary>
    public const int FlagLocalized = 0x8;
    /// <summary>Field flag: unsigned integer.</summary>
    public const int FlagUnsigned = 0x4;

    /// <summary>
    /// The client's type codes (MEASURED on the 1.15.9 and 1.60 executables against the WDC5 files: code 2 fields are
    /// exactly the fields whose values point at strings, 3 the IEEE floats, 0/1/4/5 the 32/64/8/16-bit integers by
    /// the files' element sizes; flag 0x4 = unsigned).
    /// </summary>
    public static Db2ValueType TypeOf(int rawType, int rawFlags)
    {
        bool unsigned = (rawFlags & FlagUnsigned) != 0;
        return rawType switch
        {
            0 => unsigned ? Db2ValueType.UInt32 : Db2ValueType.Int32,
            1 => unsigned ? Db2ValueType.UInt64 : Db2ValueType.Int64,
            2 => Db2ValueType.String,
            3 => Db2ValueType.Float,
            4 => unsigned ? Db2ValueType.UInt8 : Db2ValueType.Int8,
            5 => unsigned ? Db2ValueType.UInt16 : Db2ValueType.Int16,
            _ => Db2ValueType.Unknown,
        };
    }
}

/// <summary>
/// The DB2 tables a modern client knows, without a listfile: table name -> FileDataID (and the field table), read
/// from the executable's DB2 meta data by a static byte scan (the executable is never run). Each meta entry is
/// (x64): a pointer to the table name, u32 FileDataID, u32 field count, u32 record size, u32 hotfix field count,
/// i32 ID field, u32, then pointers to the per-field offset / array-size / type / flag arrays and the hotfix field
/// arrays, then the table hash and the layout hash. MEASURED on the 1.15.9, 2.5.x, 5.5.x, 1.60 and 12.x executables
/// (docs\research\CASC_INVESTIGATION.md); a heuristic: a future build could change the layout.
/// <para>WoD 6.x and the Classic flavors also resolve <c>DBFilesClient\&lt;Name&gt;.db2</c> by name hash (<see cref="Resolve"/>).</para>
/// </summary>
public sealed class Db2Catalog
{
    private readonly Dictionary<string, Db2CatalogEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, Db2CatalogEntry> _byFdid = new();

    public Db2Catalog(IEnumerable<Db2CatalogEntry> entries)
    {
        foreach (var e in entries)
        {
            _byName.TryAdd(e.Name, e);
            _byFdid.TryAdd(e.FileDataId, e);
        }
    }

    public static readonly Db2Catalog Empty = new(Array.Empty<Db2CatalogEntry>());

    /// <summary>The executable the catalog was read from (null when built otherwise).</summary>
    public string? ExecutablePath { get; private init; }
    public IReadOnlyCollection<Db2CatalogEntry> Entries => _byName.Values;
    public int Count => _byName.Count;
    public Db2CatalogEntry? Get(string name) => _byName.GetValueOrDefault(name);
    public Db2CatalogEntry? Get(uint fdid) => _byFdid.GetValueOrDefault(fdid);

    /// <summary>The client executables in a flavor folder (Wow*.exe, largest first), e.g. WowB.exe, WowClassic.exe, Wow.exe.</summary>
    public static IReadOnlyList<string> FindExecutables(string flavorFolder) =>
        Directory.Exists(flavorFolder)
            ? Directory.EnumerateFiles(flavorFolder, "Wow*.exe").Where(p => !Path.GetFileName(p).Contains("Launcher", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => new FileInfo(p).Length).ToList()
            : Array.Empty<string>();

    /// <summary>The catalog of the first client executable of a flavor folder that yields tables (empty when none does).</summary>
    public static Db2Catalog FromFolder(string flavorFolder)
    {
        foreach (var exe in FindExecutables(flavorFolder))
        {
            var c = FromExecutable(exe);
            if (c.Count > 0) return c;
        }
        return Empty;
    }

    private static readonly Regex TableName = new("^[A-Z][A-Za-z0-9_]{1,63}$", RegexOptions.Compiled);

    /// <summary>Scans an executable file (read only, not run) for the DB2 meta entries.</summary>
    public static Db2Catalog FromExecutable(string exePath)
    {
        var data = File.ReadAllBytes(exePath);
        return new Db2Catalog(Scan(data)) { ExecutablePath = exePath };
    }

    /// <summary>Scans the bytes of an x64 PE executable for DB2 meta entries (see the class remarks).</summary>
    public static List<Db2CatalogEntry> Scan(byte[] data)
    {
        var res = new List<Db2CatalogEntry>();
        if (data.Length < 0x200 || data[0] != 'M' || data[1] != 'Z') return res;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
        if (pe <= 0 || pe + 24 > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pe)) != 0x00004550) return res;
        int nsec = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 6));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 20));
        int opt = pe + 24;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(opt)) != 0x20B) return res;   // PE32+ (x64) only
        ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(opt + 24));
        var secs = new List<(string Name, uint Va, uint VSize, uint Raw, uint RawSize)>();
        for (int i = 0; i < nsec; i++)
        {
            int o = opt + optSize + 40 * i;
            if (o + 40 > data.Length) break;
            string name = Encoding.ASCII.GetString(data, o, 8).TrimEnd('\0');
            secs.Add((name, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 12)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 20)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 16))));
        }
        long ToOffset(ulong va)
        {
            if (va < imageBase) return -1;
            ulong rva = va - imageBase;
            foreach (var s in secs)
                if (rva >= s.Va && rva < (ulong)s.Va + s.RawSize)
                    return (long)(s.Raw + (rva - s.Va));
            return -1;
        }
        string? NameAt(long o)
        {
            if (o < 0 || o >= data.Length) return null;
            int end = Array.IndexOf(data, (byte)0, (int)o, (int)Math.Min(66, data.Length - o));
            if (end <= o) return null;
            var s = Encoding.ASCII.GetString(data, (int)o, end - (int)o);
            return TableName.IsMatch(s) ? s : null;
        }
        int[]? Ints(ulong ptr, int n)
        {
            long o = ToOffset(ptr);
            if (o < 0 || o + 4L * n > data.Length) return null;
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)o + 4 * i));
            return a;
        }
        var seen = new HashSet<uint>();
        foreach (var s in secs.Where(s => s.Name is ".rdata" or ".data"))
        {
            long end = Math.Min(data.Length, (long)s.Raw + s.RawSize) - 104;
            for (long k = s.Raw; k < end; k += 8)
            {
                int ki = (int)k;
                ulong namePtr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki));
                if (namePtr < imageBase || namePtr - imageBase > 0x10000000) continue;
                uint fdid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ki + 8));
                int fc = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(ki + 12));
                int rs = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(ki + 16));
                int hfc = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(ki + 20));
                int idf = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(ki + 24));
                if (fdid < 1000 || fdid > 0x7FFFFFFF || fc is < 1 or > 1000 || rs is < 1 or > 65536 || hfc is < 1 or > 1000 || idf < -1 || idf >= fc) continue;
                var name = NameAt(ToOffset(namePtr));
                if (name == null) continue;
                var offs = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 32)), fc);
                var counts = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 40)), fc);
                var types = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 48)), fc);
                var flags = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 56)), fc);
                if (offs == null || counts == null || types == null || flags == null) continue;
                if (counts.Any(c => c is < 1 or > 4096) || types.Any(t => t is < 0 or > 16)) continue;
                var hCounts = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 64)), hfc);
                var hTypes = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 72)), hfc);
                var hFlags = Ints(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(ki + 80)), hfc);
                if (!seen.Add(fdid)) continue;
                var hot = new List<Db2ExeField>();
                if (hCounts != null && hTypes != null && hFlags != null)
                    for (int i = 0; i < hfc; i++) hot.Add(new Db2ExeField { Offset = -1, ArrayCount = hCounts[i], RawType = hTypes[i], RawFlags = hFlags[i] });
                res.Add(new Db2CatalogEntry
                {
                    Name = name, FileDataId = fdid, FieldCount = fc, RecordSize = rs, HotfixFieldCount = hfc, IdField = idf, MetaOffset = k,
                    TableHash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ki + 92)),
                    LayoutHash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ki + 100)),
                    Fields = Enumerable.Range(0, fc).Select(i => new Db2ExeField { Offset = offs[i], ArrayCount = counts[i], RawType = types[i], RawFlags = flags[i] }).ToList(),
                    HotfixFields = hot,
                });
            }
        }
        return res;
    }

    private static readonly Regex TablePath = new(@"^DBFilesClient\\([A-Za-z0-9_\-]+)\.(db2|dbc)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The table paths an executable names as strings (<c>DBFilesClient\&lt;Name&gt;.db2</c> / <c>.dbc</c>): the way
    /// to list the tables of WoD 6.x, whose executable has no FileDataIDs (MEASURED: 517 paths in WoD 6.2.3; none in
    /// the modern executables).
    /// </summary>
    public static IReadOnlyList<string> TablePathsInExecutable(string exePath)
    {
        var data = File.ReadAllBytes(exePath);
        var res = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var key = "DBFilesClient\\"u8;
        for (int p = 0; p < data.Length;)
        {
            int i = data.AsSpan(p).IndexOf(key);
            if (i < 0) break;
            p += i;
            int end = Array.IndexOf(data, (byte)0, p, Math.Min(128, data.Length - p));
            if (end > p)
            {
                var s = Encoding.ASCII.GetString(data, p, end - p);
                if (TablePath.IsMatch(s)) res.Add(s);
            }
            p += key.Length;
        }
        return res.ToList();
    }

    /// <summary>A catalog from table names, by name hash (<c>DBFilesClient\&lt;Name&gt;.db2</c>, then <c>.dbc</c>); no field tables.</summary>
    public static Db2Catalog FromNames(CascStorage storage, IEnumerable<string> names)
    {
        var list = new List<Db2CatalogEntry>();
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fdid = storage.FileDataIdOf($@"DBFilesClient\{n}.db2") ?? storage.FileDataIdOf($@"DBFilesClient\{n}.dbc");
            if (fdid != null) list.Add(new Db2CatalogEntry { Name = n, FileDataId = fdid.Value, IdField = -1 });
        }
        return new Db2Catalog(list);
    }

    /// <summary>
    /// The catalog for a storage: the DB2 meta data of the flavor folder's executable (modern clients), else the
    /// table paths named in the executable resolved by name hash (WoD 6.x). <paramref name="folder"/> defaults to
    /// the storage's flavor folder (Battle.net: <c>_classic_beta_</c> ... next to the shared Data folder, found by
    /// the storage's product) or its install folder (the parent of its Data folder).
    /// </summary>
    public static Db2Catalog ForStorage(CascStorage storage, string? folder = null)
    {
        var folders = new List<string>();
        if (folder != null) folders.Add(folder);
        else
        {
            var install = Path.GetDirectoryName(Path.GetFullPath(storage.DataDirectory).TrimEnd('\\', '/')) ?? "";
            foreach (var (dir, product) in CascStorage.FlavorProducts)
                if (string.Equals(product, storage.Product, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(install, dir)))
                    folders.Add(Path.Combine(install, dir));
            folders.Add(install);
        }
        foreach (var f in folders)
        {
            var meta = FromFolder(f);
            if (meta.Count > 0) return meta;
        }
        foreach (var f in folders)
            foreach (var exe in FindExecutables(f))
            {
                var names = TablePathsInExecutable(exe).Select(p => TablePath.Match(p).Groups[1].Value).ToList();
                if (names.Count > 0) return new Db2Catalog(FromNames(storage, names).Entries) { ExecutablePath = exe };
            }
        return Empty;
    }

    /// <summary>
    /// The FileDataID of a table in a storage: the catalog first, else the name hash of
    /// <c>DBFilesClient\&lt;name&gt;.db2</c> and <c>.dbc</c> (WoD and the Classic flavors hash these names).
    /// </summary>
    public uint? Resolve(CascStorage storage, string table)
    {
        if (Get(table) is { } e) return e.FileDataId;
        return storage.FileDataIdOf($@"DBFilesClient\{table}.db2") ?? storage.FileDataIdOf($@"DBFilesClient\{table}.dbc");
    }

    /// <summary>
    /// Opens a table of a CASC storage: finds its FileDataID (<see cref="Resolve"/>), reads it with encrypted chunks
    /// zero-filled, and parses it with the catalog's field types when the layout hash matches. Null when the table is
    /// not found or not local.
    /// </summary>
    public Db2Table? Open(CascStorage storage, string table)
    {
        var fdid = Resolve(storage, table);
        if (fdid == null) return null;
        byte[]? bytes;
        try { bytes = storage.ReadFileDataId(fdid.Value, zeroFillEncrypted: true); }
        catch (CascMissingException) { return null; }
        catch (CascEncryptedException) { return null; }
        if (bytes == null) return null;
        return Read(bytes, table);
    }

    /// <summary>Parses table bytes with the catalog's field types (when the catalog knows the table and its layout hash matches).</summary>
    public Db2Table Read(byte[] bytes, string table)
    {
        var e = Get(table);
        var h = Db2Table.PeekHeader(bytes);
        bool match = e != null && e.Fields.Count > 0 && h.Magic.StartsWith("WDC", StringComparison.Ordinal) && h.LayoutHash == e.LayoutHash && h.FieldCount == e.FieldCount;
        return Db2Table.Read(bytes, new Db2ReadOptions { Name = table, Schema = match ? e!.Schema : null });
    }
}
