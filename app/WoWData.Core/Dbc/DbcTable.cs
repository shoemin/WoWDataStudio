using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WoWData.Core.Dbc;

/// <summary>One row of an editable table: the raw value of every file field, and the text of the string fields that
/// were edited (the others are read from the original string block).</summary>
public sealed class DbcRow
{
    internal DbcRow(uint[] fields, byte[]?[]? strings = null)
    {
        F = fields;
        S = strings;
    }

    /// <summary>Raw 32-bit value per file field (for a string field: the original offset; for a byte field: the byte).</summary>
    internal readonly uint[] F;
    /// <summary>Edited string bytes per field (null = the original text at F[field]).</summary>
    internal byte[]?[]? S;

    public DbcRow Clone() => new((uint[])F.Clone(), S == null ? null : (byte[]?[])S.Clone());
}

/// <summary>
/// A DBC loaded for editing. Values are kept raw (32 bits per field, floats as their bits), so a table that is written
/// back unchanged gives the same bytes; strings are written as one block in the order the rows first use them, each text
/// once (the empty text at offset 0): that is how every 1.12.1 client file is laid out (checked on all 154), so an
/// unchanged table writes back byte for byte and an edited one stays deduplicated and in a sensible order.
/// </summary>
public sealed class DbcTable
{
    public const int HeaderSize = 20;
    public static readonly string[] LocaleNames = { "enUS", "koKR", "frFR", "deDE", "zhCN", "zhTW", "esES", "esMX" };

    public string Name { get; }
    public DbcLayout Layout { get; }
    public List<DbcRow> Rows { get; }
    public int FieldCount => Layout.FieldCount;
    public int RecordSize => Layout.RecordSize;

    private readonly byte[] _block;
    /// <summary>Per field: true when it holds a string offset.</summary>
    private readonly bool[] _isString;
    private readonly int[] _fieldOffset, _fieldSize;
    private Dictionary<long, DbcRow>? _byKey;
    private int _keyVersion = -1, _version;

    private DbcTable(string name, DbcLayout layout, List<DbcRow> rows, byte[] block)
    {
        Name = name;
        Layout = layout;
        Rows = rows;
        _block = block;
        _isString = new bool[layout.FieldCount];
        _fieldOffset = new int[layout.FieldCount];
        _fieldSize = new int[layout.FieldCount];
        if (layout.Columns.Count == 0)
        {
            // mixed field sizes without a layout: one opaque field per 4 bytes is not possible; the table has no columns
            return;
        }
        foreach (var c in layout.Columns)
            for (int i = 0; i < c.Fields; i++)
            {
                int f = c.Field + i;
                _fieldOffset[f] = c.Offset + i * c.Size;
                _fieldSize[f] = c.Size;
                _isString[f] = c.Storage == DbcStorage.String || (c.Storage == DbcStorage.Loc && i < c.LocaleCount);
            }
    }

    /// <summary>Changes whenever a value or the row list changes (for caches).</summary>
    public int Version => _version;

    public void Touch() => _version++;

    public static DbcTable Load(byte[] data, string name, DbcLayouts? layouts = null)
    {
        if (data.Length < HeaderSize || data[0] != 'W' || data[1] != 'D' || data[2] != 'B' || data[3] != 'C')
            throw new DbcException($"{name}: not a WDBC file");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int fields = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int recSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        int strSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        if (count < 0 || fields < 0 || recSize < 0 || strSize < 0 || (long)HeaderSize + (long)count * recSize + strSize > data.Length)
            throw new DbcException($"{name}: the header does not match the file size (truncated or damaged file)");
        var layout = (layouts ?? DbcLayouts.Default).For(name, fields, recSize);
        if (layout.Columns.Count == 0 && fields > 0)
            throw new DbcException($"{name}: the fields have mixed sizes and no layout is known, so it cannot be edited");
        int start = HeaderSize;
        var block = data.AsSpan(start + count * recSize, strSize).ToArray();
        var t = new DbcTable(name, layout, new List<DbcRow>(count), block);
        for (int r = 0; r < count; r++)
        {
            var rec = data.AsSpan(start + r * recSize, recSize);
            var f = new uint[fields];
            for (int i = 0; i < fields; i++)
            {
                int o = t._fieldOffset[i];
                f[i] = t._fieldSize[i] switch
                {
                    1 => rec[o],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(rec[o..]),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(rec[o..]),
                };
            }
            t.Rows.Add(new DbcRow(f));
        }
        return t;
    }

    /// <summary>An empty table with the same layout (and the same original string block, so cloned rows keep their texts).</summary>
    public DbcTable CloneEmpty() => new(Name, Layout, new List<DbcRow>(), _block);

    /// <summary>A deep copy (rows cloned).</summary>
    public DbcTable Clone()
    {
        var t = CloneEmpty();
        foreach (var r in Rows) t.Rows.Add(r.Clone());
        return t;
    }

    // ------------------------------------------------------------------ raw values

    public bool IsStringField(int field) => _isString[field];

    public uint Raw(DbcRow row, int field) => row.F[field];

    public void SetRaw(DbcRow row, int field, uint value)
    {
        if (_isString[field]) throw new InvalidOperationException($"{Name}: field {field} is a string; use SetText");
        row.F[field] = value;
        _version++;
    }

    /// <summary>The bytes of a string field (without the terminating zero).</summary>
    public byte[] StringBytes(DbcRow row, int field)
    {
        var s = row.S?[field];
        if (s != null) return s;
        return OriginalString(row.F[field]);
    }

    /// <summary>String fields of the loaded file whose offset is outside the string block (the game would read garbage).</summary>
    public int BadStringOffsets()
    {
        int bad = 0;
        foreach (var r in Rows)
            for (int f = 0; f < FieldCount; f++)
                if (_isString[f] && r.S?[f] == null && r.F[f] != 0 && r.F[f] >= _block.Length) bad++;
        return bad;
    }

    private byte[] OriginalString(uint offset)
    {
        if (offset == 0 || offset >= _block.Length) return Array.Empty<byte>();
        var span = _block.AsSpan((int)offset);
        int end = span.IndexOf((byte)0);
        return (end >= 0 ? span[..end] : span).ToArray();
    }

    /// <summary>A string field's text (UTF-8; Latin-1 when the bytes are not valid UTF-8, as the Python reader does).</summary>
    public string Text(DbcRow row, int field) => Decode(StringBytes(row, field));

    public static string Decode(byte[] raw)
    {
        if (raw.Length == 0) return "";
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    public void SetStringBytes(DbcRow row, int field, byte[] value)
    {
        if (!_isString[field]) throw new InvalidOperationException($"{Name}: field {field} is not a string");
        if (Array.IndexOf(value, (byte)0) >= 0) throw new DbcException("a text cannot contain a zero character");
        row.S ??= new byte[]?[row.F.Length];
        row.S[field] = value;
        _version++;
    }

    public void SetText(DbcRow row, int field, string text) => SetStringBytes(row, field, Encoding.UTF8.GetBytes(text));

    /// <summary>The value of a field as a cell value (raw bits, or the string bytes), for undo and copies.</summary>
    public DbcCell GetCell(DbcRow row, int field) => _isString[field] ? DbcCell.Of(StringBytes(row, field)) : DbcCell.Of(row.F[field]);

    public void SetCell(DbcRow row, int field, DbcCell cell)
    {
        if (_isString[field]) SetStringBytes(row, field, cell.Bytes ?? Array.Empty<byte>());
        else SetRaw(row, field, cell.Raw);
    }

    // ------------------------------------------------------------------ typed values

    public long Number(DbcRow row, DbcColumn c) => c.Storage switch
    {
        DbcStorage.Int32 => unchecked((int)row.F[c.Field]),
        DbcStorage.Float => (long)BitConverter.UInt32BitsToSingle(row.F[c.Field]),
        _ => row.F[c.Field],
    };

    public float Float(DbcRow row, DbcColumn c) => BitConverter.UInt32BitsToSingle(row.F[c.Field]);

    /// <summary>The text of a string column, or of one locale of a localized column (0 = enUS).</summary>
    public string Text(DbcRow row, DbcColumn c, int locale = 0) =>
        c.Storage == DbcStorage.Loc ? Text(row, c.Field + Math.Clamp(locale, 0, c.LocaleCount - 1)) : Text(row, c.Field);

    /// <summary>The key of a row (its ID), or null when the table has no ID column.</summary>
    public long? Key(DbcRow row) => Layout.KeyColumn is { } k ? Number(row, k) : null;

    /// <summary>The row with this ID (null if none or the table has no ID column).</summary>
    public DbcRow? Find(long id)
    {
        if (!Layout.HasKey) return null;
        var map = _byKey;
        if (map == null || _keyVersion != _version)
        {
            // 0.13.0: the version is read BEFORE the rows: when another thread reads the table while a row is added, an
            // index built from the old rows is never marked as current (it was, and a new row could not be found)
            int v = _version;
            var d = new Dictionary<long, DbcRow>(Rows.Count);
            foreach (var r in Rows.ToArray()) d.TryAdd(Key(r)!.Value, r);
            _byKey = map = d;
            _keyVersion = v;
        }
        return map.GetValueOrDefault(id);
    }

    /// <summary>The next free ID: one more than the highest.</summary>
    public long SuggestId()
    {
        if (!Layout.HasKey || Rows.Count == 0) return 1;
        return Rows.Max(r => Key(r)!.Value) + 1;
    }

    /// <summary>A short name for a row: its label column (name / path), else "#ID".</summary>
    public string Label(DbcRow row, int locale = 0)
    {
        var lc = Layout.LabelColumn;
        if (lc != null)
        {
            var t = Text(row, lc, locale);
            if (t.Length == 0 && locale != 0 && lc.Storage == DbcStorage.Loc) t = Text(row, lc, 0);
            if (t.Length > 0) return t;
        }
        return Key(row) is { } k ? $"#{k}" : $"row {Rows.IndexOf(row) + 1}";
    }

    /// <summary>A new row: every field 0 (texts empty), and the ID when the table has one.</summary>
    public DbcRow NewRow(long? id = null)
    {
        var r = new DbcRow(new uint[FieldCount]);
        if (Layout.KeyColumn is { } k && id != null) r.F[k.Field] = unchecked((uint)id.Value);
        return r;
    }

    /// <summary>Insert a row: by ID order when the table is sorted by ID, else at the end. Returns the index.</summary>
    public int InsertSorted(DbcRow row)
    {
        int idx = Rows.Count;
        if (Layout.HasKey && Rows.Count > 0)
        {
            long key = Key(row)!.Value;
            bool sorted = true;
            for (int i = 1; i < Rows.Count && sorted; i++) sorted = Key(Rows[i - 1])!.Value <= Key(Rows[i])!.Value;
            if (sorted)
            {
                int lo = 0, hi = Rows.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (Key(Rows[mid])!.Value <= key) lo = mid + 1;
                    else hi = mid;
                }
                idx = lo;
            }
        }
        Rows.Insert(idx, row);
        _version++;
        return idx;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>The table as a WDBC file. The string block is rebuilt: "" at offset 0, then each text once, in the order
    /// the rows (and the fields of a row) first use it.</summary>
    public byte[] Write()
    {
        int count = Rows.Count;
        var block = new MemoryStream();
        block.WriteByte(0);
        var offsets = new Dictionary<byte[], uint>(ByteArrayComparer.Instance) { [Array.Empty<byte>()] = 0 };
        var records = new byte[count * RecordSize];
        for (int r = 0; r < count; r++)
        {
            var row = Rows[r];
            var rec = records.AsSpan(r * RecordSize, RecordSize);
            for (int f = 0; f < FieldCount; f++)
            {
                uint v = row.F[f];
                if (_isString[f])
                {
                    var s = StringBytes(row, f);
                    if (!offsets.TryGetValue(s, out v))
                    {
                        v = checked((uint)block.Length);
                        offsets[s] = v;
                        block.Write(s);
                        block.WriteByte(0);
                    }
                }
                int o = _fieldOffset[f];
                switch (_fieldSize[f])
                {
                    case 1: rec[o] = (byte)v; break;
                    case 2: BinaryPrimitives.WriteUInt16LittleEndian(rec[o..], (ushort)v); break;
                    default: BinaryPrimitives.WriteUInt32LittleEndian(rec[o..], v); break;
                }
            }
        }
        // a fallback layout cannot tell strings apart: keep the original block so every offset stays valid
        var strings = Layout.IsFallback ? _block : block.ToArray();
        var outBytes = new byte[HeaderSize + records.Length + strings.Length];
        "WDBC"u8.CopyTo(outBytes);
        BinaryPrimitives.WriteInt32LittleEndian(outBytes.AsSpan(4), count);
        BinaryPrimitives.WriteInt32LittleEndian(outBytes.AsSpan(8), FieldCount);
        BinaryPrimitives.WriteInt32LittleEndian(outBytes.AsSpan(12), RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(outBytes.AsSpan(16), strings.Length);
        records.CopyTo(outBytes, HeaderSize);
        strings.CopyTo(outBytes, HeaderSize + records.Length);
        return outBytes;
    }

    // ------------------------------------------------------------------ formatting and parsing

    /// <summary>A cell as text: numbers in decimal (floats with the shortest text that reads back to the same value),
    /// texts as they are (one locale of a localized column). The inverse of <see cref="TryParse"/>.</summary>
    public string Format(DbcRow row, DbcColumn c, int locale = 0) =>
        c.IsString ? Text(row, c, locale) : FormatRaw(c, row.F[c.Field]);

    public static string FormatRaw(DbcColumn c, uint raw) => c.Storage switch
    {
        DbcStorage.Int32 => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
        DbcStorage.Float => FormatFloat(raw),
        _ => raw.ToString(CultureInfo.InvariantCulture),
    };

    public static string FormatFloat(uint bits)
    {
        float f = BitConverter.UInt32BitsToSingle(bits);
        if (float.IsNaN(f)) return "raw:0x" + bits.ToString("X8", CultureInfo.InvariantCulture);
        return f.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>The flags-loc field of a localized column (the last of its fields).</summary>
    public static int LocFlagsField(DbcColumn c) => c.Field + c.LocaleCount;

    /// <summary>Parse a number typed for a column (decimal, 0x hex for whole numbers, an enum name, "raw:0x..." for any bits).</summary>
    public static bool TryParse(DbcColumn c, string text, out uint raw, out string? error)
    {
        raw = 0;
        error = null;
        var t = text.Trim();
        if (c.IsString)
        {
            error = "text column";
            return false;
        }
        if (t.StartsWith("raw:0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(t[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw))
            return CheckSize(c, raw, out error);
        if (t.Length == 0)
        {
            error = $"{c.Name} needs a value ({c.TypeText}).";
            return false;
        }
        if (c.Storage == DbcStorage.Float)
        {
            if (!float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || float.IsInfinity(f) || float.IsNaN(f))
            {
                error = $"{c.Name} must be a decimal number, for example 1.5 (\"{t}\" is not).";
                return false;
            }
            raw = BitConverter.SingleToUInt32Bits(f);
            return true;
        }
        long v;
        if (c.Values != null && !c.Values.IsFlags && c.Values.Values.FirstOrDefault(x => string.Equals(x.Name, t, StringComparison.OrdinalIgnoreCase)) is { Name: not null } named
            && !long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            v = named.Value;
        else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) || h > uint.MaxValue)
            {
                error = $"{c.Name}: \"{t}\" is not a hexadecimal number of 8 digits or fewer.";
                return false;
            }
            raw = (uint)h;
            return CheckSize(c, raw, out error);
        }
        else if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
        {
            error = $"{c.Name} must be a whole number (\"{t}\" is not)." + (c.Values != null && !c.Values.IsFlags ? $" Or one of the names of {c.Values.Name}." : "");
            return false;
        }
        switch (c.Storage)
        {
            case DbcStorage.Int32:
                if (v < int.MinValue || v > int.MaxValue) { error = $"{c.Name} must be between {int.MinValue:N0} and {int.MaxValue:N0}."; return false; }
                raw = unchecked((uint)(int)v);
                return true;
            case DbcStorage.UInt32:
                if (v < 0 || v > uint.MaxValue)
                {
                    // -1 is a common "none" value; it is stored as 0xFFFFFFFF in an unsigned column
                    if (v == -1) { raw = uint.MaxValue; return true; }
                    error = $"{c.Name} must be between 0 and {uint.MaxValue:N0}.";
                    return false;
                }
                raw = (uint)v;
                return true;
            case DbcStorage.UInt8:
                if (v is < 0 or > 255) { error = $"{c.Name} must be between 0 and 255."; return false; }
                raw = (uint)v;
                return true;
            case DbcStorage.UInt16:
                if (v is < 0 or > 65535) { error = $"{c.Name} must be between 0 and 65,535."; return false; }
                raw = (uint)v;
                return true;
        }
        error = $"{c.Name}: unsupported column type";
        return false;
    }

    private static bool CheckSize(DbcColumn c, uint raw, out string? error)
    {
        error = null;
        if (c.Storage == DbcStorage.UInt8 && raw > 255) error = $"{c.Name} must be between 0 and 255.";
        if (c.Storage == DbcStorage.UInt16 && raw > 65535) error = $"{c.Name} must be between 0 and 65,535.";
        return error == null;
    }
}

/// <summary>A field value: raw bits for numbers, bytes for texts.</summary>
public readonly record struct DbcCell(uint Raw, byte[]? Bytes)
{
    public static DbcCell Of(uint raw) => new(raw, null);
    public static DbcCell Of(byte[] bytes) => new(0, bytes);
    public static DbcCell Of(string text) => new(0, Encoding.UTF8.GetBytes(text));

    public bool SameAs(DbcCell o) => Bytes == null ? o.Bytes == null && Raw == o.Raw : o.Bytes != null && Bytes.AsSpan().SequenceEqual(o.Bytes);
}

internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x == null ? y == null : y != null && x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj)
    {
        var h = new HashCode();
        h.AddBytes(obj);
        return h.ToHashCode();
    }
}
