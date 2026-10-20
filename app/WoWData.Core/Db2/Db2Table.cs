using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.Db2;

public sealed class Db2FormatException : Exception
{
    public Db2FormatException(string message) : base(message) { }
}

/// <summary>Options for <see cref="Db2Table.Read(byte[], Db2ReadOptions?)"/>.</summary>
public sealed class Db2ReadOptions
{
    /// <summary>A name for messages (the table name).</summary>
    public string? Name { get; init; }
    /// <summary>The client's own field table for this table (types, array sizes): see <see cref="Db2Catalog"/>.</summary>
    public Db2Schema? Schema { get; init; }
    /// <summary>
    /// WDBC/WDB2 only: the byte size of every field (1, 2, 4 or 8) when the record is not all 4-byte fields
    /// (record size != field count x 4). Without it such a record is shown as 4-byte columns (<see cref="Db2Table.FieldSizesGuessed"/>).
    /// </summary>
    public IReadOnlyList<int>? FieldSizes { get; init; }
}

/// <summary>
/// A read-only reader for client database tables of the modern clients: WDC3, WDC4 and WDC5 (sectioned, bit-packed,
/// pallet and common data, per-section string tables, ID lists, copy tables, relationship maps, sparse records,
/// encrypted sections) and the older WDB2 (WoD 6.x) and WDBC. Our own code, written from public format knowledge and
/// checked against the real files (docs\research\CASC_INVESTIGATION.md).
/// <para>
/// The file has no column names. The table is a list of records with an ID, a parent ID and raw values for every
/// flattened column (an array field gives one column per element), named <c>col_0</c>, <c>col_1</c>, ...
/// Column types come from <see cref="Db2ReadOptions.Schema"/> (the client's own field table, <see cref="Db2Catalog"/>)
/// or are guessed from the storage and the values. Names come from a layout (<see cref="Db2Layouts"/>).
/// </para>
/// <para>
/// Encrypted sections (TactKeyLookup != 0) whose bytes are all zero (CASC read with zeroFillEncrypted because the
/// key is not known) are skipped and counted in <see cref="SkippedRecords"/>; WDC4/WDC5 still list their IDs
/// (<see cref="SkippedIds"/>). An encrypted section whose bytes are not zero is read normally.
/// </para>
/// </summary>
public sealed class Db2Table
{
    private byte[] _data = Array.Empty<byte>();
    private byte[] _strings = Array.Empty<byte>();
    private int _stringBase;           // file offset of the string block (WDBC/WDB2)
    private readonly List<Db2Record> _records = new();
    private readonly Dictionary<int, Db2Record> _byId = new();
    private readonly List<Db2Column> _columns = new();
    private readonly List<Db2FieldInfo> _fields = new();
    private readonly List<Db2Section> _sections = new();
    private readonly List<int> _skippedIds = new();
    private readonly List<string> _notes = new();
    private Dictionary<string, int>? _nameIndex;

    private Db2Table() { }

    public string Name { get; private set; } = "";
    public Db2Format Format { get; private set; }
    public string Magic { get; private set; } = "";
    /// <summary>WDC5: the header's version number.</summary>
    public uint Version { get; private set; }
    /// <summary>WDC5: the header's schema string (for example "wowstatic_camelot_1_60_1").</summary>
    public string SchemaString { get; private set; } = "";
    /// <summary>The record count in the header (all sections, encrypted ones included).</summary>
    public int HeaderRecordCount { get; private set; }
    /// <summary>Field count in the header.</summary>
    public int FieldCount { get; private set; }
    public int RecordSize { get; private set; }
    public uint TableHash { get; private set; }
    /// <summary>WDB5+: the layout hash (changes when the column layout changes). WDB2: 0.</summary>
    public uint LayoutHash { get; private set; }
    /// <summary>WDB2: the build in the header.</summary>
    public int Build { get; private set; }
    public int MinId { get; private set; }
    public int MaxId { get; private set; }
    public int Locale { get; private set; }
    public int Flags { get; private set; }
    /// <summary>WDC: the field that holds the ID when the table has no ID list.</summary>
    public int IdIndex { get; private set; }
    /// <summary>WDC flag 0x1: variable-length records found through an offset map.</summary>
    public bool IsSparse => Format >= Db2Format.Wdc3 && (Flags & 1) != 0;
    /// <summary>WDC flag 0x4: IDs come from the per-section ID lists.</summary>
    public bool HasIdList => Format >= Db2Format.Wdc3 && (Flags & 4) != 0;
    /// <summary>WDBC/WDB2: the record is not 4-byte fields and no <see cref="Db2ReadOptions.FieldSizes"/> were given: columns are 4-byte slices.</summary>
    public bool FieldSizesGuessed { get; private set; }
    /// <summary>True when column types come from the client's field table (<see cref="Db2ReadOptions.Schema"/>).</summary>
    public bool SchemaApplied { get; private set; }
    /// <summary>The layout that named the columns (<see cref="Db2Layouts.Apply"/>), or null (raw col_N names).</summary>
    public WoWData.Core.Dbc.DbcLayout? Layout { get; internal set; }

    public IReadOnlyList<Db2FieldInfo> Fields => _fields;
    public IReadOnlyList<Db2Section> Sections => _sections;
    public IReadOnlyList<Db2Column> Columns => _columns;
    /// <summary>All readable records in file order (sections in order, then copy-table rows).</summary>
    public IReadOnlyList<Db2Record> Records => _records;
    /// <summary>Records in encrypted, zero-filled sections that were not decoded.</summary>
    public int SkippedRecords { get; private set; }
    public int SkippedSections => _sections.Count(s => s.Skipped);
    /// <summary>The IDs of the skipped records when the file lists them (WDC4/WDC5 encrypted-ID lists).</summary>
    public IReadOnlyList<int> SkippedIds => _skippedIds;
    /// <summary>Rows added from copy tables.</summary>
    public int CopyCount { get; private set; }
    /// <summary>Records whose ID was already used by an earlier record (the later one wins in <see cref="Get"/>).</summary>
    public int DuplicateIds { get; private set; }
    /// <summary>Remarks about the read (schema not applied, sizes guessed ...).</summary>
    public IReadOnlyList<string> Notes => _notes;

    public Db2Record? Get(int id) => _byId.GetValueOrDefault(id);
    public bool Contains(int id) => _byId.ContainsKey(id);

    /// <summary>The index of a column by name (col_N or a layout name), case-insensitive.</summary>
    public int ColumnIndex(string name)
    {
        _nameIndex ??= _columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Index, StringComparer.OrdinalIgnoreCase);
        if (_nameIndex.TryGetValue(name, out var i)) return i;
        throw new KeyNotFoundException($"{Name} has no column {name}");
    }

    internal void RenameColumns() => _nameIndex = null;

    public override string ToString() => $"{Name} ({Magic}, {_records.Count} records, {_columns.Count} columns)";

    // ------------------------------------------------------------------ entry points

    public static Db2Table Read(string path, Db2ReadOptions? options = null) =>
        Read(File.ReadAllBytes(path), options ?? new Db2ReadOptions { Name = Path.GetFileNameWithoutExtension(path) });

    public static Db2Table Read(byte[] data, Db2ReadOptions? options = null)
    {
        options ??= new Db2ReadOptions();
        if (data.Length < 4) throw new Db2FormatException("the file is too short for a table header");
        var t = new Db2Table { _data = data, Name = options.Name ?? "", Magic = Encoding.ASCII.GetString(data, 0, 4) };
        switch (t.Magic)
        {
            case "WDBC": t.Format = Db2Format.Wdbc; t.ReadClassic(options); break;
            case "WDB2": t.Format = Db2Format.Wdb2; t.ReadClassic(options); break;
            case "WDC3": t.Format = Db2Format.Wdc3; t.ReadWdc(options); break;
            case "WDC4": t.Format = Db2Format.Wdc4; t.ReadWdc(options); break;
            case "WDC5": t.Format = Db2Format.Wdc5; t.ReadWdc(options); break;
            default: throw new Db2FormatException($"'{t.Magic}' is not a table format this reader knows (WDBC, WDB2, WDC3, WDC4, WDC5)");
        }
        t.GuessTypes();
        return t;
    }

    /// <summary>The magic, field count and layout hash of a table file without reading it (layout hash 0 for WDBC/WDB2).</summary>
    public static (string Magic, int FieldCount, uint LayoutHash) PeekHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return ("", 0, 0);
        var magic = Encoding.ASCII.GetString(data[..4]);
        int o = magic == "WDC5" ? 136 : 4;
        if (magic is "WDBC" or "WDB2") return data.Length >= 12 ? (magic, BinaryPrimitives.ReadInt32LittleEndian(data[8..]), 0u) : (magic, 0, 0u);
        if (data.Length < o + 24) return (magic, 0, 0);
        return (magic, BinaryPrimitives.ReadInt32LittleEndian(data[(o + 4)..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 20)..]));
    }

    // ------------------------------------------------------------------ helpers

    private int I32(int o) => o + 4 <= _data.Length ? BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(o)) : throw Short(o);
    private uint U32(int o) => o + 4 <= _data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(o)) : throw Short(o);
    private ushort U16(int o) => o + 2 <= _data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(o)) : throw Short(o);
    private Db2FormatException Short(int o) => new($"{Name}: the file ends at {_data.Length} bytes, before offset {o}");

    /// <summary>Reads <paramref name="bits"/> (1..64) little-endian bits starting at a bit offset of a byte span (bytes past the end read as 0).</summary>
    internal static ulong ReadBits(ReadOnlySpan<byte> rec, int bitOffset, int bits)
    {
        if (bits <= 0) return 0;
        int b = bitOffset >> 3, shift = bitOffset & 7;
        int need = (shift + bits + 7) >> 3;
        ulong v = 0;
        for (int i = 0; i < need && i < 8; i++)
            if (b + i < rec.Length) v |= (ulong)rec[b + i] << (8 * i);
        v >>= shift;
        if (need > 8 && b + 8 < rec.Length) v |= (ulong)rec[b + 8] << (64 - shift);
        return bits == 64 ? v : v & ((1UL << bits) - 1);
    }

    private static ulong SignExtend(ulong v, int bits) => bits is <= 0 or >= 64 ? v : unchecked((ulong)((long)(v << (64 - bits)) >> (64 - bits)));

    private static bool AllZero(ReadOnlySpan<byte> s) => s.IndexOfAnyExcept((byte)0) < 0;

    // ------------------------------------------------------------------ WDBC / WDB2

    private void ReadClassic(Db2ReadOptions options)
    {
        int rc = I32(4), fc = I32(8), rs = I32(12), ss = I32(16);
        int o = 20;
        if (Format == Db2Format.Wdb2)
        {
            TableHash = U32(20);
            Build = I32(24);
            // 28: timestamp
            MinId = I32(32);
            MaxId = I32(36);
            Locale = I32(40);
            // 44: copy table size (0 in every WoD file measured)
            o = 48;
            if (MaxId != 0) o += (MaxId - MinId + 1) * 6;   // ID index (int32 each) + string lengths (int16 each)
        }
        HeaderRecordCount = rc;
        FieldCount = fc;
        RecordSize = rs;
        if (rc < 0 || fc < 0 || rs < 0 || ss < 0 || (long)o + (long)rc * rs + ss > _data.Length)
            throw new Db2FormatException($"{Name}: {Magic} header says {rc} records x {rs} bytes + {ss} string bytes, more than the {_data.Length}-byte file");
        _stringBase = o + rc * rs;
        _strings = _data.AsSpan(_stringBase, ss).ToArray();
        if (_stringBase + ss != _data.Length) _notes.Add($"{_data.Length - _stringBase - ss} bytes follow the string block.");

        // field sizes: all 4 bytes when they add up, else the caller's sizes, else 4-byte slices
        var sizes = new List<int>();
        if (fc * 4 == rs) sizes.AddRange(Enumerable.Repeat(4, fc));
        else if (options.FieldSizes != null && options.FieldSizes.Count == fc && options.FieldSizes.Sum() == rs) sizes.AddRange(options.FieldSizes);
        else
        {
            FieldSizesGuessed = true;
            sizes.AddRange(Enumerable.Repeat(4, rs / 4));
            if (rs % 4 != 0) sizes.Add(rs % 4);
            _notes.Add($"{Magic} record of {rs} bytes is not {fc} 4-byte fields: shown as {sizes.Count} slices of 4 bytes (field sizes unknown).");
        }
        int off = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            int sz = sizes[i];
            _columns.Add(new Db2Column
            {
                Index = i, Field = i, Element = 0, Name = "col_" + i, ByteOffset = off, ByteSize = sz,
                Type = sz switch { 1 => Db2ValueType.UInt8, 2 => Db2ValueType.UInt16, 8 => Db2ValueType.UInt64, _ => Db2ValueType.UInt32 },
            });
            _fields.Add(new Db2FieldInfo { Index = i, OffsetBits = off * 8, SizeBits = sz * 8, ElementBits = sz * 8, Compression = Db2Compression.None });
            off += sz;
        }
        _sections.Add(new Db2Section { Index = 0, FileOffset = o, RecordCount = rc, StringTableSize = ss });
        for (int r = 0; r < rc; r++)
        {
            var rec = _data.AsSpan(o + r * rs, rs);
            var vals = new ulong[_columns.Count];
            for (int c = 0; c < _columns.Count; c++)
            {
                var col = _columns[c];
                vals[c] = ReadBits(rec, col.ByteOffset * 8, Math.Min(64, col.ByteSize * 8));
            }
            int id = _columns.Count > 0 ? unchecked((int)vals[0]) : r;
            Add(new Db2Record(this, id, vals, 0, r, r, null));
        }
    }

    // ------------------------------------------------------------------ WDC3 / WDC4 / WDC5

    private void ReadWdc(Db2ReadOptions options)
    {
        int o = 4;
        if (Format == Db2Format.Wdc5)
        {
            Version = U32(4);
            int end = Array.IndexOf(_data, (byte)0, 8, 128);
            SchemaString = Encoding.ASCII.GetString(_data, 8, (end < 0 ? 136 : end) - 8);
            o = 136;
        }
        HeaderRecordCount = I32(o);
        FieldCount = I32(o + 4);
        RecordSize = I32(o + 8);
        int stringTableTotal = I32(o + 12);
        TableHash = U32(o + 16);
        LayoutHash = U32(o + 20);
        MinId = I32(o + 24);
        MaxId = I32(o + 28);
        Locale = I32(o + 32);
        Flags = U16(o + 36);
        IdIndex = U16(o + 38);
        // o+40 total field count, o+44 bit-packed data offset, o+48 lookup column count
        int fsiSize = I32(o + 52), commonSize = I32(o + 56), palletSize = I32(o + 60), sectionCount = I32(o + 64);
        o += 68;
        if (FieldCount < 0 || FieldCount > 4096 || sectionCount < 0 || sectionCount > 4096 || RecordSize < 0)
            throw new Db2FormatException($"{Name}: implausible {Magic} header ({FieldCount} fields, {sectionCount} sections)");

        for (int i = 0; i < sectionCount; i++, o += 40)
        {
            _sections.Add(new Db2Section
            {
                Index = i,
                TactKeyLookup = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(o)),
                FileOffset = I32(o + 8), RecordCount = I32(o + 12), StringTableSize = I32(o + 16), OffsetRecordsEnd = I32(o + 20),
                IdListSize = I32(o + 24), RelationshipDataSize = I32(o + 28), OffsetMapIdCount = I32(o + 32), CopyTableCount = I32(o + 36),
            });
        }
        var structSize = new int[FieldCount];
        var structOffset = new int[FieldCount];
        for (int i = 0; i < FieldCount; i++, o += 4)
        {
            structSize[i] = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(o));
            structOffset[i] = U16(o + 2);
        }

        int fsiCount = fsiSize / 24;
        int palletCursor = 0, commonCursor = 0;
        for (int i = 0; i < fsiCount; i++, o += 24)
        {
            var comp = (Db2Compression)U32(o + 8);
            int sizeBits = U16(o + 2), add = I32(o + 4);
            uint v1 = U32(o + 12), v2 = U32(o + 16), v3 = U32(o + 20);
            int elementBits = 32 - (i < FieldCount ? structSize[i] : 0);
            if (elementBits <= 0 || elementBits > 64) elementBits = 32;
            int count = comp switch
            {
                Db2Compression.None => Math.Max(1, sizeBits / elementBits),
                Db2Compression.BitpackedIndexedArray => (int)Math.Max(1u, v3),
                _ => 1,
            };
            _fields.Add(new Db2FieldInfo
            {
                Index = i, OffsetBits = U16(o), SizeBits = sizeBits, AdditionalDataSize = add, Compression = comp, Val1 = v1, Val2 = v2, Val3 = v3,
                ElementBits = elementBits, ArrayCount = count,
                PalletOffset = comp is Db2Compression.BitpackedIndexed or Db2Compression.BitpackedIndexedArray ? palletCursor : 0,
                CommonOffset = comp == Db2Compression.CommonData ? commonCursor : 0,
            });
            if (comp is Db2Compression.BitpackedIndexed or Db2Compression.BitpackedIndexedArray) palletCursor += add;
            else if (comp == Db2Compression.CommonData) commonCursor += add;
            else if (comp is not (Db2Compression.None or Db2Compression.Bitpacked or Db2Compression.BitpackedSigned))
                throw new Db2FormatException($"{Name}: field {i} has an unknown storage type {(int)comp}");
        }
        if (fsiCount == 0 && FieldCount > 0)
        {
            // no field storage info (MEASURED in some 1.60 tables): plain fields at the field structures' byte offsets
            // (the empty tables measured have no records and meaningless offsets: the schema's array sizes win when given)
            var offsets = structOffset;
            var sch = options.Schema != null && options.Schema.Fields.Count == FieldCount ? options.Schema : null;
            for (int i = 0; i < FieldCount; i++)
            {
                int eb = 32 - structSize[i];
                if (eb <= 0 || eb > 64) eb = 32;
                int next = i + 1 < FieldCount ? offsets[i + 1] : (IsSparse ? offsets[i] + eb / 8 : RecordSize);
                int bits = Math.Max(eb, (next - offsets[i]) * 8);
                int count = sch != null ? sch.Fields[i].ArrayCount : Math.Max(1, bits / eb);
                _fields.Add(new Db2FieldInfo { Index = i, OffsetBits = offsets[i] * 8, SizeBits = eb * count, Compression = Db2Compression.None, ElementBits = eb, ArrayCount = count });
            }
        }
        else if (fsiCount != FieldCount) _notes.Add($"field storage info lists {fsiCount} fields, the header {FieldCount}.");

        int palletStart = o;
        int commonStart = palletStart + palletSize;
        o = commonStart + commonSize;
        if (o > _data.Length) throw Short(o);

        // WDC4/WDC5: for every encrypted section, the IDs of its records
        if (Format >= Db2Format.Wdc4)
            foreach (var s in _sections.Where(s => s.IsEncrypted))
            {
                int n = I32(o);
                o += 4;
                if (n < 0 || o + 4L * n > _data.Length) throw new Db2FormatException($"{Name}: bad encrypted-ID list ({n}) for section {s.Index}");
                var ids = new int[n];
                for (int k = 0; k < n; k++) ids[k] = I32(o + 4 * k);
                o += 4 * n;
                s.EncryptedIds = ids;
            }

        // columns
        foreach (var f in _fields)
            for (int e = 0; e < f.ArrayCount; e++)
            {
                bool plain = f.Compression == Db2Compression.None;
                int eb = f.ElementBits;
                _columns.Add(new Db2Column
                {
                    Index = _columns.Count, Field = f.Index, Element = e, Name = "col_" + _columns.Count, Compression = f.Compression,
                    ByteOffset = plain && !IsSparse ? f.OffsetBits / 8 + e * eb / 8 : -1, ByteSize = plain ? eb / 8 : 0,
                    Type = f.Compression switch
                    {
                        Db2Compression.None => eb switch { 8 => Db2ValueType.UInt8, 16 => Db2ValueType.UInt16, 64 => Db2ValueType.UInt64, _ => Db2ValueType.UInt32 },
                        Db2Compression.BitpackedSigned => Db2ValueType.Int32,
                        _ => Db2ValueType.UInt32,
                    },
                });
            }
        ApplySchema(options.Schema);

        // string tables, concatenated in section order (WDC string offsets are relative to the field and run across sections)
        var sb = new MemoryStream();
        foreach (var s in _sections)
        {
            if (IsSparse || s.StringTableSize <= 0) continue;
            long start = (long)s.FileOffset + (long)s.RecordCount * RecordSize;
            if (start + s.StringTableSize > _data.Length) throw Short((int)Math.Min(int.MaxValue, start + s.StringTableSize));
            sb.Write(_data, (int)start, s.StringTableSize);
        }
        _strings = sb.ToArray();
        if (!IsSparse && _strings.Length != stringTableTotal && stringTableTotal != 0)
            _notes.Add($"string tables add up to {_strings.Length} bytes, the header says {stringTableTotal}.");

        int globalBase = 0;
        var copies = new List<(int NewId, int SourceId)>();
        foreach (var s in _sections)
        {
            ReadSection(s, globalBase, palletStart, commonStart, commonSize, copies);
            globalBase += s.RecordCount;
        }
        foreach (var (newId, srcId) in copies)
        {
            if (!_byId.TryGetValue(srcId, out var src)) continue;
            var c = new Db2Record(this, newId, (ulong[])src.Values, src.Section, src.IndexInSection, src.GlobalIndex, src.InlineStrings)
            {
                CopyOf = srcId, ParentId = src.ParentId, HasParent = src.HasParent,
            };
            Add(c);
            CopyCount++;
        }
    }

    private void ReadSection(Db2Section s, int globalBase, int palletStart, int commonStart, int commonSize, List<(int, int)> copies)
    {
        if (s.RecordCount == 0) return;
        int recordsStart = s.FileOffset;
        int recordsEnd = IsSparse ? s.OffsetRecordsEnd : s.FileOffset + s.RecordCount * RecordSize;
        if (recordsStart < 0 || recordsEnd > _data.Length || recordsEnd < recordsStart)
            throw new Db2FormatException($"{Name}: section {s.Index} ({recordsStart}..{recordsEnd}) is outside the {_data.Length}-byte file");
        s.IsZeroFilled = AllZero(_data.AsSpan(recordsStart, recordsEnd - recordsStart));
        if (s.IsEncrypted && s.IsZeroFilled)
        {
            s.Skipped = true;
            SkippedRecords += s.RecordCount;
            _skippedIds.AddRange(s.EncryptedIds);
            return;
        }

        int p = IsSparse ? recordsEnd : recordsEnd + s.StringTableSize;
        // ID list
        int[]? idList = null;
        if (s.IdListSize > 0)
        {
            int n = s.IdListSize / 4;
            idList = new int[n];
            for (int k = 0; k < n; k++) idList[k] = I32(p + 4 * k);
            p += s.IdListSize;
        }
        // copy table
        for (int k = 0; k < s.CopyTableCount; k++, p += 8) copies.Add((I32(p), I32(p + 4)));
        // offset map (sparse): (u32 offset, u16 size) per record
        (int Offset, int Size)[]? offsetMap = null;
        if (s.OffsetMapIdCount > 0)
        {
            offsetMap = new (int, int)[s.OffsetMapIdCount];
            for (int k = 0; k < s.OffsetMapIdCount; k++, p += 6) offsetMap[k] = (I32(p), U16(p + 4));
        }
        // offset map IDs (sparse; MEASURED: before the relationship map)
        int[]? offsetIds = null;
        if (s.OffsetMapIdCount > 0)
        {
            offsetIds = new int[s.OffsetMapIdCount];
            for (int k = 0; k < s.OffsetMapIdCount; k++) offsetIds[k] = I32(p + 4 * k);
            p += 4 * s.OffsetMapIdCount;
        }
        // relationship map: count, min ID, max ID, then (foreign ID, record index) pairs
        var parents = new Dictionary<int, int>();
        if (s.RelationshipDataSize >= 12)
        {
            int n = Math.Min(I32(p), (s.RelationshipDataSize - 12) / 8);
            int q = p + 12;
            for (int k = 0; k < n; k++, q += 8) parents[I32(q + 4)] = I32(q);
            p += s.RelationshipDataSize;
        }
        // the second number is the record index in the section; sparse tables were MEASURED to store the record ID there
        bool parentsById = parents.Count > 0 && parents.Keys.Any(k => k < 0 || k >= s.RecordCount);

        // common-data fields: their (ID -> value) maps and their column
        var common = new List<(Dictionary<int, uint> Map, uint Default, int Column)>();
        foreach (var f in _fields.Where(f => f.Compression == Db2Compression.CommonData))
        {
            var map = new Dictionary<int, uint>();
            int start = commonStart + f.CommonOffset, n = Math.Max(0, Math.Min(f.AdditionalDataSize, commonSize - f.CommonOffset)) / 8;
            for (int k = 0; k < n; k++) map[I32(start + 8 * k)] = U32(start + 8 * k + 4);
            common.Add((map, f.CommonDefault, _columns.FindIndex(x => x.Field == f.Index)));
        }
        int idColumn = IdIndex < _fields.Count ? _columns.FindIndex(c => c.Field == IdIndex) : -1;
        for (int r = 0; r < s.RecordCount; r++)
        {
            ReadOnlySpan<byte> rec;
            string?[]? inline = null;
            ulong[] vals;
            if (IsSparse)
            {
                if (offsetMap == null || r >= offsetMap.Length) break;
                var (ofs, size) = offsetMap[r];
                if (size == 0 || ofs <= 0 || ofs + size > _data.Length) continue;
                rec = _data.AsSpan(ofs, size);
                vals = ReadSparse(rec, out inline);
            }
            else
            {
                rec = _data.AsSpan(recordsStart + r * RecordSize, RecordSize);
                vals = ReadFixed(rec, palletStart);
            }
            int id = idList != null && r < idList.Length ? idList[r]
                : offsetIds != null && r < offsetIds.Length ? offsetIds[r]
                : idColumn >= 0 ? unchecked((int)vals[idColumn]) : globalBase + r;
            // common data needs the ID
            foreach (var (map, def, col) in common)
                if (col >= 0) vals[col] = map.TryGetValue(id, out var v) ? v : def;
            if (SchemaApplied) Normalize(vals);
            var record = new Db2Record(this, id, vals, s.Index, r, globalBase + r, inline);
            if (parents.TryGetValue(parentsById ? id : r, out var parent))
            {
                record.ParentId = parent;
                record.HasParent = true;
            }
            Add(record);
        }
    }

    private ulong[] ReadFixed(ReadOnlySpan<byte> rec, int palletStart)
    {
        var vals = new ulong[_columns.Count];
        int c = 0;
        foreach (var f in _fields)
        {
            switch (f.Compression)
            {
                case Db2Compression.None:
                    for (int e = 0; e < f.ArrayCount; e++) vals[c++] = ReadBits(rec, f.OffsetBits + e * f.ElementBits, f.ElementBits);
                    break;
                case Db2Compression.Bitpacked:
                    vals[c++] = ReadBits(rec, f.OffsetBits, f.SizeBits);
                    break;
                case Db2Compression.BitpackedSigned:
                    vals[c++] = SignExtend(ReadBits(rec, f.OffsetBits, f.SizeBits), f.SizeBits);
                    break;
                case Db2Compression.CommonData:
                    c++;   // filled in once the ID is known
                    break;
                case Db2Compression.BitpackedIndexed:
                {
                    int idx = (int)ReadBits(rec, f.OffsetBits, f.SizeBits);
                    vals[c++] = PalletValue(palletStart, f, idx * 4);
                    break;
                }
                case Db2Compression.BitpackedIndexedArray:
                {
                    int idx = (int)ReadBits(rec, f.OffsetBits, f.SizeBits);
                    for (int e = 0; e < f.ArrayCount; e++) vals[c++] = PalletValue(palletStart, f, (idx * f.ArrayCount + e) * 4);
                    break;
                }
            }
        }
        return vals;
    }

    /// <summary>
    /// Cuts every value to its schema type's width and sign: pallet and common-data entries of 8- and 16-bit fields
    /// carry unrelated bytes in their upper half (MEASURED: LiquidObject's 16-bit LiquidType ID stored as 0x3D4C0508).
    /// </summary>
    private void Normalize(ulong[] vals)
    {
        for (int i = 0; i < vals.Length; i++)
        {
            ulong v = vals[i];
            vals[i] = _columns[i].Type switch
            {
                Db2ValueType.Int8 => SignExtend(v & 0xFF, 8),
                Db2ValueType.UInt8 => v & 0xFF,
                Db2ValueType.Int16 => SignExtend(v & 0xFFFF, 16),
                Db2ValueType.UInt16 => v & 0xFFFF,
                Db2ValueType.Int32 => SignExtend(v & 0xFFFFFFFF, 32),
                Db2ValueType.UInt32 or Db2ValueType.Float or Db2ValueType.String => v & 0xFFFFFFFF,
                _ => v,
            };
        }
    }

    private uint PalletValue(int palletStart, Db2FieldInfo f, int byteOffset) =>
        byteOffset + 4 <= f.AdditionalDataSize ? U32(palletStart + f.PalletOffset + byteOffset) : 0;

    /// <summary>A sparse record: fields one after the other in their natural size, strings inline (needs the schema to know which).</summary>
    private ulong[] ReadSparse(ReadOnlySpan<byte> rec, out string?[]? inline)
    {
        var vals = new ulong[_columns.Count];
        inline = null;
        int p = 0;
        foreach (var col in _columns)
        {
            if (col.Type == Db2ValueType.String && col.TypeKnown)
            {
                int end = p < rec.Length ? rec[p..].IndexOf((byte)0) : -1;
                if (end < 0) end = Math.Max(0, rec.Length - p);
                inline ??= new string?[_columns.Count];
                inline[col.Index] = p < rec.Length ? Encoding.UTF8.GetString(rec.Slice(p, end)) : "";
                vals[col.Index] = (ulong)p;
                p += end + 1;
                continue;
            }
            int bytes = Math.Max(1, col.ByteSize);
            if (col.Type is Db2ValueType.Int8 or Db2ValueType.UInt8) bytes = 1;
            else if (col.Type is Db2ValueType.Int16 or Db2ValueType.UInt16) bytes = 2;
            else if (col.Type is Db2ValueType.Int64 or Db2ValueType.UInt64) bytes = 8;
            else if (col.TypeKnown) bytes = 4;
            vals[col.Index] = ReadBits(rec, p * 8, bytes * 8);
            if (col.Type is Db2ValueType.Int8 or Db2ValueType.Int16 or Db2ValueType.Int32) vals[col.Index] = SignExtend(vals[col.Index], bytes * 8);
            p += bytes;
        }
        return vals;
    }

    private void Add(Db2Record r)
    {
        _records.Add(r);
        if (_byId.ContainsKey(r.Id)) DuplicateIds++;
        _byId[r.Id] = r;
    }

    // ------------------------------------------------------------------ strings

    /// <summary>The string a column's value points at, or null when it does not point at the start of a string.</summary>
    internal string? StringOf(Db2Record r, int column)
    {
        if (r.InlineStrings != null) return r.InlineStrings[column];
        // WDC writes 0 for an empty string (MEASURED: every string value either points at a string start or is 0)
        if (Format >= Db2Format.Wdc3 && unchecked((uint)r.Values[column]) == 0) return "";
        long pos = StringPosition(r, column);
        if (pos < 0 || pos >= _strings.Length) return null;
        if (pos > 0 && _strings[pos - 1] != 0) return null;
        int end = Array.IndexOf(_strings, (byte)0, (int)pos);
        if (end < 0) return null;
        return Encoding.UTF8.GetString(_strings, (int)pos, end - (int)pos);
    }

    /// <summary>The string blocks (WDC: the per-section string tables concatenated in section order).</summary>
    public ReadOnlySpan<byte> StringBlock => _strings;

    /// <summary>Where a column's value points in <see cref="StringBlock"/> when read as a string reference (-1 when it cannot be one).</summary>
    public long StringPosition(Db2Record r, int column)
    {
        var col = _columns[column];
        uint v = unchecked((uint)r.Values[column]);
        if (Format is Db2Format.Wdbc or Db2Format.Wdb2) return v;
        if (col.ByteOffset < 0 || col.ByteSize != 4) return -1;
        // the value is relative to the field's own position in the records of all sections; the string tables follow them
        long total = (long)_sections.Sum(s => s.RecordCount) * RecordSize;
        return (long)r.GlobalIndex * RecordSize + col.ByteOffset + v - total;
    }

    /// <summary>True when every readable record's value of this column points at the start of a string.</summary>
    public bool LooksLikeStrings(int column)
    {
        var col = _columns[column];
        if (_strings.Length == 0 || col.ByteSize != 4 && Format >= Db2Format.Wdc3) return false;
        bool anyNonEmpty = false;
        int n = 0;
        foreach (var r in _records)
        {
            if (r.IsCopy) continue;
            uint v = unchecked((uint)r.Values[column]);
            if (v == 0) continue;
            var s = StringOf(r, column);
            if (s == null) return false;
            if (s.Length > 0) anyNonEmpty = true;
            n++;
        }
        return n > 0 && anyNonEmpty;
    }

    // ------------------------------------------------------------------ types

    private void ApplySchema(Db2Schema? schema)
    {
        if (schema == null) return;
        if (schema.Fields.Count != _fields.Count)
        {
            _notes.Add($"the client's field table has {schema.Fields.Count} fields, the file {_fields.Count}: types are guessed.");
            return;
        }
        for (int i = 0; i < _fields.Count; i++)
            if (schema.Fields[i].ArrayCount != _fields[i].ArrayCount)
            {
                _notes.Add($"field {i}: the client's field table says {schema.Fields[i].ArrayCount} elements, the file {_fields[i].ArrayCount}: types are guessed.");
                return;
            }
        foreach (var c in _columns)
        {
            c.Type = schema.Fields[c.Field].Type;
            c.TypeKnown = true;
        }
        SchemaApplied = true;
    }

    /// <summary>Without a schema: strings are found by where their values point, floats by their bit patterns.</summary>
    private void GuessTypes()
    {
        if (SchemaApplied) return;
        foreach (var c in _columns)
        {
            bool fourBytes = Format is Db2Format.Wdbc or Db2Format.Wdb2 ? c.ByteSize == 4 : c.Compression == Db2Compression.None && c.ByteSize == 4;
            if (fourBytes && !IsSparse && LooksLikeStrings(c.Index)) c.Type = Db2ValueType.String;
            else if (c.ByteSize == 4 || c.Compression is Db2Compression.BitpackedIndexed or Db2Compression.BitpackedIndexedArray or Db2Compression.CommonData)
            {
                if (LooksLikeFloats(c.Index)) c.Type = Db2ValueType.Float;
                else if (c.Compression != Db2Compression.BitpackedSigned && _records.Any(r => unchecked((int)r.Values[c.Index]) < 0)) c.Type = Db2ValueType.Int32;
            }
        }
    }

    private bool LooksLikeFloats(int column)
    {
        int n = 0;
        foreach (var r in _records)
        {
            uint v = unchecked((uint)r.Values[column]);
            if (v == 0) continue;
            if (v < 0x00800000 || v is >= 0xFF000000) return false;   // small integers, or -1..-16M
            float f = BitConverter.UInt32BitsToSingle(v);
            if (!float.IsFinite(f) || Math.Abs(f) < 1e-6f || Math.Abs(f) > 1e9f) return false;
            n++;
        }
        return n > 0;
    }
}
