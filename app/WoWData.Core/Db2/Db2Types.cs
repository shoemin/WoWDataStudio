namespace WoWData.Core.Db2;

/// <summary>The table formats <see cref="Db2Table"/> reads.</summary>
public enum Db2Format
{
    /// <summary>Classic fixed-record DBC (1.x-6.x .dbc files): 4-byte fields, one string block.</summary>
    Wdbc,
    /// <summary>WoD 6.x .db2: a WDBC-like body with a longer header and an optional ID index.</summary>
    Wdb2,
    /// <summary>Sectioned, bit-packed format with field storage info (no encrypted-ID lists). FORMAT KNOWLEDGE: no local client has WDC3 files; covered by the synthetic tests only.</summary>
    Wdc3,
    /// <summary>WDC3 plus a list of record IDs for every encrypted section (FORMAT KNOWLEDGE, same header as WDC3; not seen in the local clients).</summary>
    Wdc4,
    /// <summary>WDC4 plus a version number and a schema string in the header (MEASURED: every DB2 of the six Battle.net flavors in the 2026-10-20 snapshot).</summary>
    Wdc5,
}

/// <summary>How a WDC field is stored (the "storage type" of its field storage info).</summary>
public enum Db2Compression
{
    /// <summary>Plain bytes in the record: elements of 8/16/32/64 bits (strings are offsets, see <see cref="Db2Table"/>).</summary>
    None = 0,
    /// <summary>An unsigned value of N bits at a bit offset in the record.</summary>
    Bitpacked = 1,
    /// <summary>Not in the record: a per-ID value in the common-data block, else a default.</summary>
    CommonData = 2,
    /// <summary>An N-bit index into the field's pallet (a list of 32-bit values).</summary>
    BitpackedIndexed = 3,
    /// <summary>An N-bit index into the field's pallet of fixed-size arrays (Val3 = array length).</summary>
    BitpackedIndexedArray = 4,
    /// <summary>A signed (sign-extended) value of N bits at a bit offset in the record.</summary>
    BitpackedSigned = 5,
}

/// <summary>The value type of a column: from the client's own field table (<see cref="Db2Schema"/>) or guessed.</summary>
public enum Db2ValueType
{
    Unknown,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    /// <summary>A string (WDC: an offset relative to the field; WDBC/WDB2: an offset into the string block; sparse: inline).</summary>
    String,
}

/// <summary>One field of a WDC table, as its field storage info describes it. A field can be an array.</summary>
public sealed class Db2FieldInfo
{
    public int Index { get; init; }
    /// <summary>Bit offset of the field in the record (bit-packed part included).</summary>
    public int OffsetBits { get; init; }
    /// <summary>Bits the field takes in the record (all elements).</summary>
    public int SizeBits { get; init; }
    /// <summary>Bytes of pallet or common data this field owns.</summary>
    public int AdditionalDataSize { get; init; }
    public Db2Compression Compression { get; init; }
    public uint Val1 { get; init; }
    public uint Val2 { get; init; }
    public uint Val3 { get; init; }
    /// <summary>Bits of one element for <see cref="Db2Compression.None"/> fields (from the field structure: 32 - size).</summary>
    public int ElementBits { get; init; }
    /// <summary>Number of elements (1 unless an array).</summary>
    public int ArrayCount { get; init; } = 1;
    /// <summary>Byte offset of this field's block in the pallet data (pallet types only).</summary>
    public int PalletOffset { get; init; }
    /// <summary>Byte offset of this field's block in the common data (common-data type only).</summary>
    public int CommonOffset { get; init; }
    /// <summary>The default value of a common-data field.</summary>
    public uint CommonDefault => Val1;
    public bool IsSigned => Compression == Db2Compression.BitpackedSigned;

    public override string ToString() => $"field {Index}: {Compression} @{OffsetBits} {SizeBits} bits x{ArrayCount}";
}

/// <summary>A section of a WDC file (records + string table + ID list + copy table + ...).</summary>
public sealed class Db2Section
{
    public int Index { get; init; }
    /// <summary>Non-zero: the section is encrypted with the TACT key of that lookup hash.</summary>
    public ulong TactKeyLookup { get; init; }
    public int FileOffset { get; init; }
    public int RecordCount { get; init; }
    public int StringTableSize { get; init; }
    /// <summary>Sparse tables: the file offset where the variable-length records end.</summary>
    public int OffsetRecordsEnd { get; init; }
    public int IdListSize { get; init; }
    public int RelationshipDataSize { get; init; }
    public int OffsetMapIdCount { get; init; }
    public int CopyTableCount { get; init; }

    public bool IsEncrypted => TactKeyLookup != 0;
    /// <summary>The section's record bytes are all zero (an encrypted section whose key is missing, read zero-filled).</summary>
    public bool IsZeroFilled { get; internal set; }
    /// <summary>The section was not decoded (encrypted and zero-filled): its records are counted in <see cref="Db2Table.SkippedRecords"/>.</summary>
    public bool Skipped { get; internal set; }
    /// <summary>WDC4/WDC5: the record IDs the file lists for this encrypted section (readable even when the data is not).</summary>
    public IReadOnlyList<int> EncryptedIds { get; internal set; } = Array.Empty<int>();

    public override string ToString() => $"section {Index}: {RecordCount} records{(IsEncrypted ? $", key {TactKeyLookup:X16}" : "")}{(Skipped ? ", skipped" : "")}";
}

/// <summary>One column of the flattened table: an element of a field (arrays give one column per element).</summary>
public sealed class Db2Column
{
    /// <summary>Position in <see cref="Db2Table.Columns"/> and in <see cref="Db2Record.Values"/>.</summary>
    public int Index { get; init; }
    public int Field { get; init; }
    public int Element { get; init; }
    /// <summary>col_N (N = <see cref="Index"/>) unless a layout named it.</summary>
    public string Name { get; internal set; } = "";
    public Db2ValueType Type { get; internal set; }
    /// <summary>True when <see cref="Type"/> comes from the client's field table or a layout, false when guessed from the values.</summary>
    public bool TypeKnown { get; internal set; }
    /// <summary>True when a layout names the column with evidence (false for col_N and "~" best guesses).</summary>
    public bool NameVerified { get; internal set; }
    /// <summary>Byte offset of the element in a fixed-size record (WDC None-compressed fields, WDBC, WDB2), else -1.</summary>
    public int ByteOffset { get; init; } = -1;
    /// <summary>Bytes of the element in a fixed-size record (1, 2, 4, 8), or 0 when bit-packed/pallet/common.</summary>
    public int ByteSize { get; init; }
    public Db2Compression Compression { get; init; }

    public bool IsString => Type == Db2ValueType.String;

    public override string ToString() => $"{Name} ({Type}{(TypeKnown ? "" : "?")})";
}
