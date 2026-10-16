using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.Dbc;

public sealed class DbcException : Exception
{
    public DbcException(string message) : base(message) { }
}

/// <summary>
/// A WDBC table (port of the generic part of wowdata/dbc.py). Layout: 'WDBC', record count, field count, record
/// size, string block size, records, string block. Every 1.12 field is 4 bytes; strings are offsets into the string
/// block (UTF-8, Latin-1 fallback); a localized string is 8 offsets (enUS first) plus a flags mask = 9 fields.
/// A few tables pack 1- or 2-byte fields (CharBaseInfo); those are read with <see cref="FieldSize"/> 1 or 2.
/// </summary>
public sealed class DbcFile
{
    public const int Locales = 8;

    public string Name { get; }
    public int Count { get; }
    public int FieldCount { get; }
    public int RecordSize { get; }
    /// <summary>4 for normal tables, 1 or 2 for packed byte tables, 0 for mixed layouts (only raw record bytes).</summary>
    public int FieldSize { get; }

    private readonly byte[] _data;
    private readonly int _stringStart;
    private readonly int _stringSize;
    private const int RecordStart = 20;

    public DbcFile(byte[] data, string name)
    {
        Name = name;
        if (data.Length < 20 || data[0] != 'W' || data[1] != 'D' || data[2] != 'B' || data[3] != 'C')
            throw new DbcException($"{name}: not a WDBC file");
        _data = data;
        Count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        FieldCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        RecordSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        _stringSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        _stringStart = RecordStart + Count * RecordSize;
        if (_stringStart + _stringSize > data.Length) throw new DbcException($"{name}: truncated file");
        if (RecordSize == FieldCount * 4) FieldSize = 4;
        else if (FieldCount > 0 && RecordSize % FieldCount == 0 && RecordSize / FieldCount is 1 or 2) FieldSize = RecordSize / FieldCount;
        else FieldSize = 0;
    }

    public void RequireFields(int minFields)
    {
        if (FieldSize != 4) throw new DbcException($"{Name}: typed layouts need 4-byte fields");
        if (FieldCount < minFields) throw new DbcException($"{Name}: layout has {minFields} fields, the file has {FieldCount}");
    }

    public int Int(int row, int col)
    {
        if (FieldSize == 4) return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(RecordStart + row * RecordSize + col * 4));
        if (FieldSize == 2) return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(RecordStart + row * RecordSize + col * 2));
        if (FieldSize == 1) return _data[RecordStart + row * RecordSize + col];
        throw new DbcException($"{Name} has mixed field sizes; use RecordBytes()");
    }

    public uint UInt(int row, int col) => unchecked((uint)Int(row, col));

    public float Float(int row, int col) =>
        FieldSize == 4 ? BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(RecordStart + row * RecordSize + col * 4)) : Int(row, col);

    public string String(int row, int col) => StringAt(Int(row, col));

    /// <summary>enUS text of a localized string starting at <paramref name="col"/> (8 locales + flags = 9 fields).</summary>
    public string LocString(int row, int col) => StringAt(Int(row, col));

    public string StringAt(int offset)
    {
        if (offset <= 0 || offset >= _stringSize) return "";
        var block = _data.AsSpan(_stringStart, _stringSize);
        int end = block[offset..].IndexOf((byte)0);
        var raw = end >= 0 ? block.Slice(offset, end) : block[offset..];
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    public ReadOnlySpan<byte> RecordBytes(int row) => _data.AsSpan(RecordStart + row * RecordSize, RecordSize);

    public int[] RawRow(int row)
    {
        var r = new int[FieldCount];
        for (int c = 0; c < FieldCount; c++) r[c] = Int(row, c);
        return r;
    }
}
