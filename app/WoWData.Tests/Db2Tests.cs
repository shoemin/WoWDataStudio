using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using WoWData.Core.Casc;
using WoWData.Core.Db2;

namespace WoWData.Tests;

/// <summary>
/// The DB2 reader (WoWData.Core.Db2) on synthetic files the test writes itself: WDC3/WDC4/WDC5 with every storage
/// kind, two sections one of them "encrypted" (zero-filled), copy table, relationship map, sparse records, WDB2,
/// WDBC, the executable meta-data scan and the layout files. <see cref="Db2RealTests"/> checks the real clients.
/// </summary>
public class Db2Tests
{
    // ------------------------------------------------------------------ a WDC writer for the tests

    private static void PutBits(byte[] rec, int bitOffset, int bits, ulong v)
    {
        for (int i = 0; i < bits; i++)
            if (((v >> i) & 1) != 0) rec[(bitOffset + i) >> 3] |= (byte)(1 << ((bitOffset + i) & 7));
    }

    private sealed class W
    {
        private readonly MemoryStream _ms = new();
        public int Pos => (int)_ms.Position;
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); }
        public void I32(int v) => U32(unchecked((uint)v));
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); }
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _ms.Write(b); }
        public void Bytes(byte[] b) => _ms.Write(b);
        public void Zeros(int n) => _ms.Write(new byte[n]);
        public byte[] ToArray() => _ms.ToArray();
        public void PatchU32(int at, uint v)
        {
            long p = _ms.Position;
            _ms.Position = at;
            U32(v);
            _ms.Position = p;
        }
    }

    private const int RecordSize = 16, TotalRecords = 5;
    private const ulong TactKey = 0x1122334455667788;

    /// <summary>
    /// A table with 8 fields / 11 columns: string, float[2], u16 (plain), 5-bit unsigned, 7-bit signed, pallet,
    /// pallet array[3], common data. Section 0: IDs 10, 20, 30, a copy 900 &lt;- 10, parents 5 (row 0) and 7 (row 2).
    /// Section 1: 2 records, TactKeyLookup set, all zero (IDs 40, 50 in the WDC4/5 encrypted-ID list).
    /// </summary>
    internal static byte[] BuildWdc(string magic)
    {
        var w = new W();
        w.Bytes(Encoding.ASCII.GetBytes(magic));
        if (magic == "WDC5")
        {
            w.U32(5);
            var schema = new byte[128];
            Encoding.ASCII.GetBytes("wowstatic_test").CopyTo(schema, 0);
            w.Bytes(schema);
        }
        // strings of section 0: "Alpha" at 0, "Beta" at 6; section 1: 8 zero bytes
        var strings0 = Encoding.ASCII.GetBytes("Alpha\0Beta\0");
        const int strings1 = 8;
        uint[] pallet = { 1000, 2000, 3000 };
        uint[] palletArr = { 11, 12, 13, 21, 22, 23 };
        (int Id, uint Value)[] common = { (10, 55) };

        w.U32(TotalRecords); w.U32(8); w.U32(RecordSize); w.U32((uint)(strings0.Length + strings1));
        w.U32(0xAABBCCDD); w.U32(0x12345678); w.U32(10); w.U32(50); w.U32(0);
        w.U16(0x4); w.U16(0);                                  // flags: ID list; ID index 0
        w.U32(8); w.U32(12); w.U32(0);                         // total fields, bit-packed offset, lookup columns
        w.U32(8 * 24); w.U32((uint)(common.Length * 8)); w.U32((uint)(4 * (pallet.Length + palletArr.Length))); w.U32(2);
        int sec0Header = w.Pos;
        w.Zeros(80);                                           // 2 section headers, patched below
        // field structures (size = 32 - bits, byte offset)
        (short, ushort)[] structs = { (0, 0), (0, 4), (16, 12), (0, 14), (0, 14), (0, 14), (0, 14), (0, 14) };
        foreach (var (s, o) in structs) { w.U16(unchecked((ushort)s)); w.U16(o); }
        // field storage info: offset bits, size bits, additional data, type, v1, v2, v3
        void Fsi(int off, int size, int add, Db2Compression t, uint v1, uint v2, uint v3)
        {
            w.U16((ushort)off); w.U16((ushort)size); w.U32((uint)add); w.U32((uint)t); w.U32(v1); w.U32(v2); w.U32(v3);
        }
        Fsi(0, 32, 0, Db2Compression.None, 0, 0, 0);
        Fsi(32, 64, 0, Db2Compression.None, 0, 0, 0);
        Fsi(96, 16, 0, Db2Compression.None, 0, 0, 0);
        Fsi(112, 5, 0, Db2Compression.Bitpacked, 0, 5, 0);
        Fsi(117, 7, 0, Db2Compression.BitpackedSigned, 5, 7, 0);
        Fsi(124, 2, 12, Db2Compression.BitpackedIndexed, 12, 2, 0);
        Fsi(126, 1, 24, Db2Compression.BitpackedIndexedArray, 14, 1, 3);
        Fsi(127, 0, 8, Db2Compression.CommonData, 77, 0, 0);
        foreach (var v in pallet) w.U32(v);
        foreach (var v in palletArr) w.U32(v);
        foreach (var (id, v) in common) { w.I32(id); w.U32(v); }
        if (magic != "WDC3") { w.U32(2); w.U32(40); w.U32(50); }   // encrypted-ID list of section 1

        // section 0
        int sec0 = w.Pos;
        (string S, float A, float B, ushort U, uint P5, int S7, int Pal, int PalArr)[] rows =
        {
            ("Alpha", 1.5f, -2.25f, 0xBEEF, 31, -64, 2, 1),
            ("", 0f, 0f, 1, 0, 63, 0, 0),
            ("Beta", 3f, 4f, 0, 17, -1, 1, 1),
        };
        int[] strPos = { 0, -1, 6 };
        for (int r = 0; r < rows.Length; r++)
        {
            var rec = new byte[RecordSize];
            // a string value is relative to the field's own position in the records of all sections
            uint sv = strPos[r] < 0 ? 0u : (uint)(strPos[r] + TotalRecords * RecordSize - (r * RecordSize + 0));
            BinaryPrimitives.WriteUInt32LittleEndian(rec, sv);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(4), rows[r].A);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(8), rows[r].B);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(12), rows[r].U);
            PutBits(rec, 112, 5, rows[r].P5);
            PutBits(rec, 117, 7, unchecked((ulong)rows[r].S7) & 0x7F);
            PutBits(rec, 124, 2, (ulong)rows[r].Pal);
            PutBits(rec, 126, 1, (ulong)rows[r].PalArr);
            w.Bytes(rec);
        }
        w.Bytes(strings0);
        foreach (var id in new[] { 10, 20, 30 }) w.I32(id);   // ID list
        w.I32(900); w.I32(10);                                 // copy table: 900 repeats 10
        w.I32(2); w.I32(5); w.I32(7);                          // relationship map: count, min, max
        w.I32(5); w.I32(0); w.I32(7); w.I32(2);                // (foreign ID, record index)
        // section 1: encrypted, zero-filled
        int sec1 = w.Pos;
        w.Zeros(2 * RecordSize + strings1 + 2 * 4);

        void Section(int at, ulong tact, int offset, int records, int strings, int idList, int rel, int copies)
        {
            w.PatchU32(at, (uint)tact); w.PatchU32(at + 4, (uint)(tact >> 32));
            w.PatchU32(at + 8, (uint)offset); w.PatchU32(at + 12, (uint)records); w.PatchU32(at + 16, (uint)strings);
            w.PatchU32(at + 20, 0); w.PatchU32(at + 24, (uint)idList); w.PatchU32(at + 28, (uint)rel); w.PatchU32(at + 32, 0); w.PatchU32(at + 36, (uint)copies);
        }
        Section(sec0Header, 0, sec0, 3, strings0.Length, 12, 28, 1);
        Section(sec0Header + 40, TactKey, sec1, 2, strings1, 8, 0, 0);
        return w.ToArray();
    }

    private static Db2Schema TestSchema => new(new[]
    {
        new Db2SchemaField(Db2ValueType.String, 1, false, 2, 0),
        new Db2SchemaField(Db2ValueType.Float, 2, false, 3, 0),
        new Db2SchemaField(Db2ValueType.UInt16, 1, false, 5, 4),
        new Db2SchemaField(Db2ValueType.UInt8, 1, false, 4, 6),
        new Db2SchemaField(Db2ValueType.Int8, 1, false, 4, 2),
        new Db2SchemaField(Db2ValueType.UInt32, 1, false, 0, 6),
        new Db2SchemaField(Db2ValueType.UInt32, 3, false, 0, 6),
        new Db2SchemaField(Db2ValueType.Int32, 1, false, 0, 2),
    });

    [Theory]
    [InlineData("WDC3", false)]
    [InlineData("WDC4", false)]
    [InlineData("WDC5", false)]
    [InlineData("WDC5", true)]
    public void WdcRoundTrip(string magic, bool withSchema)
    {
        var bytes = BuildWdc(magic);
        var t = Db2Table.Read(bytes, new Db2ReadOptions { Name = "Test", Schema = withSchema ? TestSchema : null });
        Assert.Equal(magic, t.Magic);
        Assert.Equal(magic == "WDC5" ? "wowstatic_test" : "", t.SchemaString);
        Assert.Equal((5, 8, 16, 0x12345678u), (t.HeaderRecordCount, t.FieldCount, t.RecordSize, t.LayoutHash));
        Assert.Equal(withSchema, t.SchemaApplied);
        Assert.Empty(t.Notes);
        Assert.Equal(11, t.Columns.Count);
        Assert.Equal(new[] { Db2Compression.None, Db2Compression.None, Db2Compression.None, Db2Compression.Bitpacked, Db2Compression.BitpackedSigned,
            Db2Compression.BitpackedIndexed, Db2Compression.BitpackedIndexedArray, Db2Compression.CommonData }, t.Fields.Select(f => f.Compression));

        // sections: the encrypted, zero-filled one is skipped and counted
        Assert.Equal(2, t.Sections.Count);
        Assert.False(t.Sections[0].IsEncrypted);
        Assert.True(t.Sections[1].IsEncrypted && t.Sections[1].IsZeroFilled && t.Sections[1].Skipped);
        Assert.Equal(TactKey, t.Sections[1].TactKeyLookup);
        Assert.Equal(2, t.SkippedRecords);
        Assert.Equal(1, t.SkippedSections);
        Assert.Equal(magic == "WDC3" ? Array.Empty<int>() : new[] { 40, 50 }, t.SkippedIds);

        // 3 records + 1 copy
        Assert.Equal(4, t.Records.Count);
        Assert.Equal(1, t.CopyCount);
        Assert.Equal(new[] { 10, 20, 30, 900 }, t.Records.Select(r => r.Id));
        var a = t.Get(10)!;
        Assert.Equal("Alpha", a.String(0));
        Assert.Equal(1.5f, a.Float(1));
        Assert.Equal(-2.25f, a.Float(2));
        Assert.Equal(0xBEEFu, a.UInt(3));
        Assert.Equal(31u, a.UInt(4));
        Assert.Equal(-64, a.Int(5));
        Assert.Equal(3000u, a.UInt(6));
        Assert.Equal(new uint[] { 21, 22, 23 }, new[] { a.UInt(7), a.UInt(8), a.UInt(9) });
        Assert.Equal(55u, a.UInt(10));                     // common data
        Assert.True(a.HasParent);
        Assert.Equal(5, a.ParentId);

        var b = t.Get(20)!;
        Assert.Equal("", b.String(0));                     // 0 = empty string
        Assert.Equal(63, b.Int(5));
        Assert.Equal(1000u, b.UInt(6));
        Assert.Equal(new uint[] { 11, 12, 13 }, new[] { b.UInt(7), b.UInt(8), b.UInt(9) });
        Assert.Equal(77u, b.UInt(10));                     // common default
        Assert.False(b.HasParent);

        var c = t.Get(30)!;
        Assert.Equal("Beta", c.String(0));
        Assert.Equal(-1, c.Int(5));
        Assert.Equal(2000u, c.UInt(6));
        Assert.Equal(7, c.ParentId);

        var copy = t.Get(900)!;
        Assert.True(copy.IsCopy);
        Assert.Equal(10, copy.CopyOf);
        Assert.Equal("Alpha", copy.String(0));
        Assert.Equal(-64, copy.Int(5));
        Assert.Null(t.Get(40));

        // types: from the schema, or guessed (string by where it points, floats by bit pattern)
        Assert.Equal(Db2ValueType.String, t.Columns[0].Type);
        Assert.Equal(Db2ValueType.Float, t.Columns[1].Type);
        Assert.Equal(Db2ValueType.Float, t.Columns[2].Type);
        Assert.Equal(withSchema, t.Columns[0].TypeKnown);
        Assert.Equal(withSchema ? Db2ValueType.Int8 : Db2ValueType.Int32, t.Columns[5].Type);
        Assert.Equal((sbyte)-64, a.Get(5) is sbyte sb ? sb : (sbyte)(int)a.Get(5)!);
        Assert.Equal("Alpha", a.Get(0));
        Assert.Equal("col_6", t.Columns[6].Name);
        Assert.Equal(6, t.ColumnIndex("COL_6"));
    }

    [Fact]
    public void EncryptedSectionWithDataIsRead()
    {
        // a section marked encrypted whose bytes are present (already decrypted) is read normally
        var bytes = BuildWdc("WDC5");
        var t0 = Db2Table.Read(bytes);
        int sec1 = t0.Sections[1].FileOffset;
        bytes[sec1 + 12] = 0x34; bytes[sec1 + 13] = 0x12;     // u16 field of the first record
        bytes[sec1 + 2 * RecordSize + 8] = 41;                // its ID (first entry of the ID list, after 8 string bytes)
        var t = Db2Table.Read(bytes);
        Assert.False(t.Sections[1].Skipped);
        Assert.Equal(0, t.SkippedRecords);
        Assert.Equal(0x1234u, t.Get(41)!.UInt(3));
        Assert.Equal(6, t.Records.Count);                     // 3 + 2 + 1 copy (the second record has ID 0)
    }

    [Fact]
    public void SparseRecordsWithInlineStrings()
    {
        var w = new W();
        w.Bytes("WDC5"u8.ToArray());
        w.U32(5);
        w.Zeros(128);
        w.U32(2); w.U32(3); w.U32(0); w.U32(0); w.U32(1); w.U32(2); w.U32(100); w.U32(200); w.U32(0);
        w.U16(0x1); w.U16(0);
        w.U32(3); w.U32(0); w.U32(0); w.U32(3 * 24); w.U32(0); w.U32(0); w.U32(1);
        int secHeader = w.Pos;
        w.Zeros(40);
        foreach (var (s, o) in new (short, ushort)[] { (0, 0), (0, 4), (16, 8) }) { w.U16(unchecked((ushort)s)); w.U16(o); }
        foreach (var (o, bits) in new[] { (0, 32), (32, 32), (64, 16) })
        {
            w.U16((ushort)o); w.U16((ushort)bits); w.U32(0); w.U32(0); w.U32(0); w.U32(0); w.U32(0);
        }
        int recStart = w.Pos;
        var r1 = new byte[] { 7, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("hello\0")).Concat(new byte[] { 9, 0 }).ToArray();
        var r2 = new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0, 0x34, 0x12 };
        w.Bytes(r1);
        w.Bytes(r2);
        int recEnd = w.Pos;
        w.U32((uint)recStart); w.U16((ushort)r1.Length);      // offset map
        w.U32((uint)(recStart + r1.Length)); w.U16((ushort)r2.Length);
        w.U32(100); w.U32(200);                               // offset map IDs
        w.PatchU32(secHeader + 8, (uint)recStart);
        w.PatchU32(secHeader + 12, 2);
        w.PatchU32(secHeader + 20, (uint)recEnd);
        w.PatchU32(secHeader + 32, 2);
        var schema = new Db2Schema(new[]
        {
            new Db2SchemaField(Db2ValueType.Int32, 1, false, 0, 0),
            new Db2SchemaField(Db2ValueType.String, 1, false, 2, 0),
            new Db2SchemaField(Db2ValueType.UInt16, 1, false, 5, 4),
        });
        var t = Db2Table.Read(w.ToArray(), new Db2ReadOptions { Name = "Sparse", Schema = schema });
        Assert.True(t.IsSparse);
        Assert.Equal(2, t.Records.Count);
        Assert.Equal((7, "hello", 9u), (t.Get(100)!.Int(0), t.Get(100)!.String(1), t.Get(100)!.UInt(2)));
        Assert.Equal((-2, "", 0x1234u), (t.Get(200)!.Int(0), t.Get(200)!.String(1), t.Get(200)!.UInt(2)));
    }

    [Fact]
    public void Wdb2AndWdbc()
    {
        // WDB2: 48-byte header, ID index + string lengths for min..max, records, string block
        var w = new W();
        w.Bytes("WDB2"u8.ToArray());
        var strings = Encoding.ASCII.GetBytes("\0Foo\0Bar\0");
        w.U32(2); w.U32(3); w.U32(12); w.U32((uint)strings.Length); w.U32(0xCAFE); w.U32(20779); w.U32(0); w.U32(1); w.U32(2); w.U32(0); w.U32(0);
        w.Zeros(2 * 4 + 2 * 2);
        w.U32(1); w.U32(1); w.U32(0x3FC00000);               // 1, "Foo", 1.5f
        w.U32(2); w.U32(5); w.U32(0x40200000);               // 2, "Bar", 2.5f
        w.Bytes(strings);
        var t = Db2Table.Read(w.ToArray(), new Db2ReadOptions { Name = "Wdb2" });
        Assert.Equal(Db2Format.Wdb2, t.Format);
        Assert.Equal((0xCAFEu, 20779, 1, 2), (t.TableHash, t.Build, t.MinId, t.MaxId));
        Assert.Equal(Db2ValueType.String, t.Columns[1].Type);
        Assert.Equal(Db2ValueType.Float, t.Columns[2].Type);
        Assert.Equal(("Foo", 1.5f), (t.Get(1)!.String(1), t.Get(1)!.Float(2)));
        Assert.Equal("Bar", t.Get(2)!.Text(1));
        Assert.Empty(t.Notes);

        // WDBC with a record that is not all 4-byte fields: sizes given, or 4-byte slices with a note
        var c = new W();
        c.Bytes("WDBC"u8.ToArray());
        c.U32(1); c.U32(3); c.U32(8); c.U32(1);
        c.U32(42); c.U16(7); c.Bytes(new byte[] { 3, 4 });
        c.Zeros(1);
        var raw = c.ToArray();
        var sized = Db2Table.Read(raw, new Db2ReadOptions { FieldSizes = new[] { 4, 2, 2 } });
        Assert.Equal((42, 7u, 0x0403u), (sized.Get(42)!.Int(0), sized.Get(42)!.UInt(1), sized.Get(42)!.UInt(2)));
        var guessed = Db2Table.Read(raw);
        Assert.True(guessed.FieldSizesGuessed);
        Assert.Equal(2, guessed.Columns.Count);
        Assert.Single(guessed.Notes);
    }

    [Fact]
    public void BadFilesThrow()
    {
        Assert.Throws<Db2FormatException>(() => Db2Table.Read(new byte[] { 1, 2 }));
        Assert.Throws<Db2FormatException>(() => Db2Table.Read("XXXX1234"u8.ToArray()));
        var b = BuildWdc("WDC5");
        Assert.Throws<Db2FormatException>(() => Db2Table.Read(b.AsSpan(0, b.Length - 60).ToArray()));
        Assert.Equal(("WDC5", 8, 0x12345678u), Db2Table.PeekHeader(b));
    }

    // ------------------------------------------------------------------ executable meta data

    /// <summary>A minimal x64 PE with one .rdata section holding a DB2 meta entry for "Map".</summary>
    internal static byte[] FakeExe()
    {
        const ulong imageBase = 0x140000000;
        const int raw = 0x400, va = 0x1000, size = 0x400;
        var d = new byte[raw + size];
        d[0] = (byte)'M'; d[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(d.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x94), 0xF0);
        int opt = 0x98;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(opt), 0x20B);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(opt + 24), imageBase);
        int sec = opt + 0xF0;
        ".rdata"u8.CopyTo(d.AsSpan(sec));
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sec + 8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sec + 12), va);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sec + 16), size);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(sec + 20), raw);
        ulong Va(int rel) => imageBase + va + (ulong)rel;
        void Ints(int rel, params int[] v) { for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(raw + rel + 4 * i), v[i]); }
        "Map\0"u8.CopyTo(d.AsSpan(raw + 0x300));
        Ints(0x200, 0, 4);           // offsets
        Ints(0x210, 1, 1);           // array sizes
        Ints(0x220, 2, 5);           // types: string, 16-bit
        Ints(0x230, 0, 6);           // flags: -, compressed + unsigned
        int m = raw + 0x40;
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(m), Va(0x300));
        Ints(0x48, 1349477, 2, 8, 2, -1, 0);
        ulong[] ptrs = { Va(0x200), Va(0x210), Va(0x220), Va(0x230), Va(0x210), Va(0x220), Va(0x230) };
        for (int i = 0; i < ptrs.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(m + 32 + 8 * i), ptrs[i]);
        Ints(0x40 + 92, unchecked((int)0xBD84CD62));
        Ints(0x40 + 100, unchecked((int)0xD43AFAC3));
        return d;
    }

    [Fact]
    public void CatalogScansTheExecutable()
    {
        var entries = Db2Catalog.Scan(FakeExe());
        var e = Assert.Single(entries);
        Assert.Equal(("Map", 1349477u, 2, 8, -1), (e.Name, e.FileDataId, e.FieldCount, e.RecordSize, e.IdField));
        Assert.Equal((0xBD84CD62u, 0xD43AFAC3u), (e.TableHash, e.LayoutHash));
        Assert.Equal(new[] { Db2ValueType.String, Db2ValueType.UInt16 }, e.Schema.Fields.Select(f => f.Type));
        Assert.Equal(2, e.HotfixFields.Count);
        var cat = new Db2Catalog(entries);
        Assert.Same(e, cat.Get("map"));
        Assert.Same(e, cat.Get(1349477u));
        Assert.Empty(Db2Catalog.Scan(new byte[4096]));

        // the field type codes of the client
        Assert.Equal(Db2ValueType.UInt32, Db2Schema.TypeOf(0, 6));
        Assert.Equal(Db2ValueType.Int32, Db2Schema.TypeOf(0, 2));
        Assert.Equal(Db2ValueType.UInt64, Db2Schema.TypeOf(1, 4));
        Assert.Equal(Db2ValueType.String, Db2Schema.TypeOf(2, 8));
        Assert.Equal(Db2ValueType.Float, Db2Schema.TypeOf(3, 2));
        Assert.Equal(Db2ValueType.Int8, Db2Schema.TypeOf(4, 2));
        Assert.Equal(Db2ValueType.UInt16, Db2Schema.TypeOf(5, 6));
    }

    // ------------------------------------------------------------------ layouts

    /// <summary>The layout files in the source tree (the tests run from app\WoWData.Tests\bin\...).</summary>
    internal static string LayoutFile(int build)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "app", "WoWData.Core", "Db2", "Layouts", $"db2_layouts_{build}.txt");
            if (File.Exists(p)) return p;
            p = Path.Combine(d.FullName, "WoWData.Core", "Db2", "Layouts", $"db2_layouts_{build}.txt");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"db2_layouts_{build}.txt not found above {AppContext.BaseDirectory}");
    }

    internal static Db2Layouts Layouts70450() => Db2Layouts.ForBuild(70450) ?? Db2Layouts.Load(LayoutFile(70450));

    [Fact]
    public void LayoutNamesColumns()
    {
        const string text = """
            @build 99999 9.9.9
            [Test]
            layout-hash: 12345678
            Name               string
            Pos[2]             float
            Small              uint
            ~Bits              uint
            Signed             int
            col_6              uint
            Arr[3]             uint
            Common             uint
            """;
        var l = Db2Layouts.Parse(text);
        Assert.Equal(0x12345678u, l.LayoutHash("test"));
        var t = Db2Table.Read(BuildWdc("WDC5"), new Db2ReadOptions { Name = "Test", Schema = TestSchema });
        Assert.True(l.Apply(t, out var why), why);
        Assert.Equal(new[] { "Name", "Pos1", "Pos2", "Small", "Bits", "Signed", "col_6", "Arr1", "Arr2", "Arr3", "Common" }, t.Columns.Select(c => c.Name));
        Assert.True(t.Columns[0].NameVerified);
        Assert.False(t.Columns[4].NameVerified);
        Assert.Equal("Alpha", t.Get(10)!.String("Name"));
        Assert.Equal(-64, t.Get(10)!.Int("signed"));
        Assert.Same(l.Get("Test"), t.Layout);

        // another layout hash, another column count, a type conflict: the table keeps col_N
        var other = Db2Table.Read(BuildWdc("WDC5"), new Db2ReadOptions { Name = "Test", Schema = TestSchema });
        Assert.False(Db2Layouts.Parse(text.Replace("12345678", "87654321")).Apply(other, out why));
        Assert.Contains("layout hash", why);
        Assert.False(Db2Layouts.Parse(text.Replace("Common             uint", "")).Apply(other, out why));
        Assert.False(Db2Layouts.Parse(text.Replace("Small              uint", "Small              string")).Apply(other, out why));
        Assert.Equal("col_0", other.Columns[0].Name);
        Assert.Throws<WoWData.Core.Dbc.DbcLayoutException>(() => Db2Layouts.Parse("[X]\nName loc\n"));
    }

    [Fact]
    public void BuiltInLayoutFileParses()
    {
        var l = Layouts70450();
        Assert.Equal(70450, l.Build);
        foreach (var t in new[] { "Map", "AreaTable", "LiquidType", "LiquidObject", "LiquidMaterial", "Light", "WMOAreaTable", "LoadingScreens", "GroundEffectTexture", "GroundEffectDoodad", "ZoneMusic" })
        {
            Assert.NotNull(l.Get(t));
            Assert.NotNull(l.LayoutHash(t));
        }
        Assert.Equal(29, l.Get("Map")!.Columns.Sum(c => c.Fields));
        Assert.Equal(28, l.Get("AreaTable")!.Columns.Sum(c => c.Fields));
        Assert.Equal(76, l.Get("LiquidType")!.Columns.Sum(c => c.Fields));
        if (Db2Layouts.KnownBuilds().Contains(70450)) Assert.NotNull(Db2Layouts.ForBuild(70450));
    }
}

/// <summary>
/// The DB2 reader on the real client copies under G:\WoW Clients and Patches (read-only; skipped when missing).
/// Expected numbers were measured with the reader's probe (see the layout file's evidence and CASC_INVESTIGATION.md).
/// </summary>
public class Db2RealTests
{
    private const string Era = CascRealStoreTests.Bnet + @"\_classic_era_";

    private static readonly object Gate = new();
    private static (CascStorage, Db2Catalog)? _beta;

    private static (CascStorage St, Db2Catalog Cat) Beta()
    {
        lock (Gate)
        {
            if (_beta == null)
            {
                var st = CascStorage.Open(CascRealStoreTests.ClassicBeta);
                _beta = (st, Db2Catalog.ForStorage(st));
            }
            return _beta.Value;
        }
    }

    [CascFact(CascRealStoreTests.ClassicBeta)]
    public void BetaCatalogFromWowB()
    {
        var c = Db2Catalog.FromExecutable(Path.Combine(CascRealStoreTests.ClassicBeta, "WowB.exe"));
        Assert.Equal(1161, c.Count);
        Assert.Equal(1349477u, c.Get("Map")!.FileDataId);
        Assert.Equal(1353545u, c.Get("AreaTable")!.FileDataId);
        Assert.Equal(1371380u, c.Get("LiquidType")!.FileDataId);
        Assert.Equal(1990283u, c.Get("SpellName")!.FileDataId);
        Assert.Equal((26, 0xD43AFAC3u), (c.Get("Map")!.FieldCount, c.Get("Map")!.LayoutHash));
        Assert.Equal(c.Count, c.Entries.Select(e => e.FileDataId).Distinct().Count());
        Assert.Equal(1161, Beta().Cat.Count);
    }

    [CascFact(CascRealStoreTests.ClassicBeta)]
    public void BetaMapAreaLiquid()
    {
        var (st, cat) = Beta();
        var map = cat.Open(st, "Map")!;
        Assert.Equal(Db2Format.Wdc5, map.Format);
        Assert.True(map.SchemaApplied);
        Assert.Equal((81, 73, 8, 4), (map.HeaderRecordCount, map.Records.Count, map.SkippedRecords, map.Sections.Count));
        Assert.Equal(new[] { 2970, 2974, 2981, 2994, 3001, 3039, 3049, 3052 }, map.SkippedIds.OrderBy(i => i));
        Assert.Equal("Azeroth", map.Get(0)!.String(0));
        Assert.Equal("Kalimdor", map.Get(1)!.String(0));
        Assert.Equal(CascRealStoreTests.AzerothWdt, map.Get(0)!.UInt(22));
        Assert.Equal(-1, map.Get(0)!.Int(13));                // a 16-bit pallet value, sign-extended

        var area = cat.Open(st, "AreaTable")!;
        Assert.Equal((1421, 1375, 46), (area.HeaderRecordCount, area.Records.Count, area.SkippedRecords));
        Assert.Equal("Elwynn Forest", area.Get(12)!.String(1));
        Assert.Equal((0, 0), (area.Get(12)!.Int(2), area.Get(12)!.Int(3)));
        Assert.Equal("Riverglades", area.Get(16591)!.String(1));

        var liquid = cat.Open(st, "LiquidType")!;
        Assert.Equal(57, liquid.Records.Count);
        Assert.Equal(("Ocean", 1), (liquid.Get(2)!.String(0), liquid.Get(2)!.Int(8)));
        Assert.Equal(("Magma", 2), (liquid.Get(3)!.String(0), liquid.Get(3)!.Int(8)));
        Assert.Equal(("Slime", 3), (liquid.Get(4)!.String(0), liquid.Get(4)!.Int(8)));
        Assert.Equal(0x400, liquid.Get(1250)!.Int(7) & 0x400);

        var obj = cat.Open(st, "LiquidObject")!;
        Assert.Equal((57, 1213), (obj.HeaderRecordCount, obj.CopyCount));
        Assert.All(obj.Records, r => Assert.True(liquid.Contains(r.Int(2))));

        var wmo = cat.Open(st, "WMOAreaTable")!;
        Assert.Equal(52643, wmo.Records.Count);
        Assert.All(wmo.Records.Where(r => r.HasParent).Take(2000), r => Assert.Equal(r.Int(2), r.ParentId));
    }

    [CascFact(CascRealStoreTests.ClassicBeta)]
    public void BetaLayoutsApply()
    {
        var (st, cat) = Beta();
        var l = Db2RealLayouts();
        foreach (var lay in l.Tables)
        {
            var t = cat.Open(st, lay.Name)!;
            Assert.True(l.Apply(t, out var why), why);
        }
        var map = cat.Open(st, "Map")!;
        Assert.True(l.Apply(map, out _));
        var row = map.Records.Single(r => r.UInt("WdtFileDataID") == 7898491u);
        Assert.Equal((3104, "3104", "nothing to see here"), (row.Id, row.String("Directory"), row.String("MapName")));
        Assert.Equal(1250, map.Get(0)!.Int("OceanLiquidTypeID"));
        var area = cat.Open(st, "AreaTable")!;
        Assert.True(l.Apply(area, out _));
        Assert.Equal(16591, area.Get(16820)!.Int("ParentAreaID"));
    }

    private static Db2Layouts Db2RealLayouts() => Db2Tests.Layouts70450();

    [CascFact(CascRealStoreTests.ClassicBeta)]
    public void BetaEveryCatalogTableReads()
    {
        var (st, cat) = Beta();
        int read = 0, skippedTables = 0;
        foreach (var e in cat.Entries)
        {
            var t = cat.Open(st, e.Name);
            Assert.NotNull(t);
            Assert.True(t!.SchemaApplied, e.Name);
            read++;
            if (t.SkippedRecords > 0) skippedTables++;
        }
        Assert.Equal(1161, read);
        Assert.Equal(106, skippedTables);
    }

    [CascFact(Era)]
    public void EraCatalogAndMap()
    {
        using var st = CascStorage.Open(Era);
        var cat = Db2Catalog.ForStorage(st);
        Assert.Equal(944, cat.Count);
        Assert.EndsWith("WowClassic.exe", cat.ExecutablePath);
        var map = cat.Open(st, "Map")!;
        Assert.Equal("Azeroth", map.Get(0)!.String(0));
        Assert.Equal(1349477u, cat.Resolve(st, "Map"));
        // the Classic flavors also hash the table names
        Assert.Equal(1349477u, st.FileDataIdOf(@"DBFilesClient\Map.db2"));
    }

    [CascFact(CascRealStoreTests.Wod)]
    public void WodTablesByName()
    {
        using var st = CascStorage.Open(CascRealStoreTests.Wod);
        var cat = Db2Catalog.ForStorage(st);
        Assert.InRange(cat.Count, 500, 520);                // the WoD exe has no FileDataID meta data: paths + name hash
        var map = cat.Open(st, "Map")!;
        Assert.Equal(Db2Format.Wdbc, map.Format);
        Assert.Equal(548, map.Records.Count);
        Assert.Equal("Azeroth", map.Get(0)!.String(1));
        var cdi = cat.Open(st, "CreatureDisplayInfo")!;
        Assert.Equal(Db2Format.Wdb2, cdi.Format);
        Assert.Equal((52657, 20726), (cdi.Records.Count, cdi.Build));   // the build that last wrote the file
        Assert.Empty(cdi.Notes);
        var liquid = cat.Open(st, "LiquidType")!;
        Assert.Equal("Ocean", liquid.Get(2)!.String(1));
    }
}
