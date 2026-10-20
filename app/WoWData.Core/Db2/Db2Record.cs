using System.Globalization;

namespace WoWData.Core.Db2;

/// <summary>
/// One record of a <see cref="Db2Table"/>: its ID, its parent ID (relationship map), and the raw value of every
/// flattened column (<see cref="Db2Table.Columns"/>). Values are stored as 64-bit bit patterns: unsigned fields
/// zero-extended, bit-packed signed fields sign-extended, floats as their IEEE bits (<see cref="Float"/>).
/// </summary>
public sealed class Db2Record
{
    internal Db2Record(Db2Table table, int id, ulong[] values, int section, int indexInSection, int globalIndex, string?[]? inline)
    {
        Table = table;
        Id = id;
        Values = values;
        Section = section;
        IndexInSection = indexInSection;
        GlobalIndex = globalIndex;
        InlineStrings = inline;
    }

    public Db2Table Table { get; }
    public int Id { get; internal set; }
    /// <summary>The foreign key from the section's relationship map (WDC), or 0.</summary>
    public int ParentId { get; internal set; }
    /// <summary>True when the relationship map has an entry for this record.</summary>
    public bool HasParent { get; internal set; }
    /// <summary>The section the record's bytes are in (WDBC/WDB2: 0).</summary>
    public int Section { get; }
    public int IndexInSection { get; }
    /// <summary>Index of the record among all records of all sections (used to resolve WDC string offsets).</summary>
    internal int GlobalIndex { get; }
    /// <summary>Sparse tables: the strings read inline, per column (null where the column is not a string).</summary>
    internal string?[]? InlineStrings { get; }
    /// <summary>Raw values per flattened column.</summary>
    public IReadOnlyList<ulong> Values { get; }
    /// <summary>For a row of the copy table: the ID whose values it repeats (else 0).</summary>
    public int CopyOf { get; internal set; }
    public bool IsCopy => CopyOf != 0;

    public ulong Raw(int column) => Values[column];
    public int Int(int column) => unchecked((int)Values[column]);
    public uint UInt(int column) => unchecked((uint)Values[column]);
    public long Long(int column) => unchecked((long)Values[column]);
    public float Float(int column) => BitConverter.Int32BitsToSingle(unchecked((int)Values[column]));

    /// <summary>The column read as a string reference (whatever its type); null when the value does not point at a string start.</summary>
    public string? String(int column) => Table.StringOf(this, column);

    public ulong Raw(string column) => Values[Table.ColumnIndex(column)];
    public int Int(string column) => Int(Table.ColumnIndex(column));
    public uint UInt(string column) => UInt(Table.ColumnIndex(column));
    public float Float(string column) => Float(Table.ColumnIndex(column));
    public string? String(string column) => String(Table.ColumnIndex(column));

    /// <summary>The value typed by the column's <see cref="Db2Column.Type"/> (int, uint, long, float, string ...).</summary>
    public object? Get(int column)
    {
        ulong v = Values[column];
        return Table.Columns[column].Type switch
        {
            Db2ValueType.Int8 => (sbyte)v,
            Db2ValueType.UInt8 => (byte)v,
            Db2ValueType.Int16 => (short)v,
            Db2ValueType.UInt16 => (ushort)v,
            Db2ValueType.Int32 => unchecked((int)v),
            Db2ValueType.Int64 => unchecked((long)v),
            Db2ValueType.UInt64 => v,
            Db2ValueType.Float => Float(column),
            Db2ValueType.String => String(column),
            _ => unchecked((uint)v),
        };
    }

    /// <summary>The value as text (strings as they are, floats round-trip, integers in decimal).</summary>
    public string Text(int column) => Get(column) switch
    {
        null => "",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        var o => o.ToString() ?? "",
    };

    public override string ToString() => $"#{Id}" + (HasParent ? $" (parent {ParentId})" : "");
}
