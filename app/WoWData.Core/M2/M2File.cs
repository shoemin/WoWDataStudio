using System.Buffers.Binary;

namespace WoWData.Core.M2;

/// <summary>
/// Full M2 (MD20 v256/v257) reader and writer: port of wowdata/m2/model.py.
/// <see cref="Parse"/> reads EVERYTHING the file contains into <see cref="M2Data"/> blocks (the header and every array,
/// depth first), keeping the original bytes and the offset/size of every block. <see cref="Serialize"/> writes the
/// structure back in the layout of Blizzard's exporter (all 9,691 client models follow it):
///   * the header first (0x144 bytes for v256, 0x154 for v257);
///   * then every non-empty array in header-field order, depth first: an array of records is followed by the arrays of
///     its first element (in field order), then those of the second element, and so on;
///   * every block starts on a 16-byte boundary; gaps and the end of the file are zero-filled to 16 bytes;
///   * an empty array is written as {count 0, offset 0}.
/// An unchanged model therefore re-serialises byte-identically.
/// </summary>
public sealed class M2File
{
    public const uint Magic = 0x3032444d;   // 'MD20'

    public M2Data Header { get; }
    public byte[]? Raw { get; }
    /// <summary>(offset, size) of every block read from the file (the header included).</summary>
    public List<(int Offset, int Size)> Blocks { get; }

    private M2File(M2Data header, byte[]? raw, List<(int, int)> blocks)
    {
        Header = header;
        Raw = raw;
        Blocks = blocks;
    }

    public uint Version => BinaryPrimitives.ReadUInt32LittleEndian(Header.Bytes.AsSpan(4));
    public M2Rec HeaderRec => Header.Rec;

    /// <summary>A header array by name ("bones", "views", "vertices"...).</summary>
    public M2Data this[string array] => Header.Child(0, array);

    public string ModelName => this["name"].AsString();

    public Dictionary<string, int> Counts() => HeaderRec.Arrays.ToDictionary(a => a.Name, a => Header.Sub[0][a.Index].Count);

    // ---------------------------------------------------------------- reading

    public static M2File Parse(byte[] data)
    {
        if (data.Length < M2Schema.Header256.Size || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new M2Exception("not an M2 (MD20) file");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version is not (0x100 or 0x101))
            throw new M2Exception($"M2 version 0x{version:x}: the 1.12 client accepts only 0x100 and 0x101");
        var rec = version == 0x101 ? M2Schema.Header257 : M2Schema.Header256;
        if (data.Length < rec.Size) throw new M2Exception($"file shorter than the 0x{rec.Size:x}-byte header");
        var r = new Reader(data);
        r.Blocks.Add((0, rec.Size));
        var bytes = data.AsSpan(0, rec.Size).ToArray();
        var elem = M2Elem.Of(rec);
        var header = new M2Data(elem, 1, bytes, new[] { r.Children(rec, bytes, 0, "") }, 0);
        return new M2File(header, data, r.Blocks);
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        public readonly List<(int, int)> Blocks = new();

        public Reader(byte[] data) { _data = data; }

        public M2Data Array(uint count, uint offset, M2Elem elem, string path)
        {
            if (count == 0) return M2Data.Empty(elem);
            long nbytes = (long)count * elem.Size;
            // the client's check (M2Fixup_*): offset <= size and offset + count * elemsize <= size
            if (offset > _data.Length || offset + nbytes > _data.Length)
                throw new M2Exception($"{path}: {count} x {elem.Size} bytes at 0x{offset:x} run past the end of the file (0x{_data.Length:x})");
            Blocks.Add(((int)offset, (int)nbytes));
            var bytes = _data.AsSpan((int)offset, (int)nbytes).ToArray();
            M2Data[][]? sub = null;
            if (elem.Rec is { Arrays.Count: > 0 } rec)
            {
                sub = new M2Data[count][];
                for (int i = 0; i < count; i++) sub[i] = Children(rec, bytes, i, path);   // element by element, in file order
            }
            return new M2Data(elem, (int)count, bytes, sub, (int)offset);
        }

        public M2Data[] Children(M2Rec rec, byte[] bytes, int index, string path)
        {
            var arrays = rec.Arrays;
            var res = new M2Data[arrays.Count];
            int b = index * rec.Size;
            for (int k = 0; k < arrays.Count; k++)
            {
                var a = arrays[k];
                uint n = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(b + a.Offset));
                uint o = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(b + a.Offset + 4));
                res[k] = n == 0 ? M2Data.Empty(a.Elem) : Array(n, o, a.Elem, path.Length == 0 ? a.Name : $"{path}[{index}].{a.Name}");
            }
            return res;
        }
    }

    /// <summary>(covered bytes, non-zero bytes outside every block): proves nothing in the file is left unread.</summary>
    public (int Covered, int Stray) Coverage()
    {
        if (Raw == null) return (0, 0);
        var mask = new bool[Raw.Length];
        foreach (var (o, n) in Blocks)
            for (int i = o; i < o + n; i++) mask[i] = true;
        int covered = 0, stray = 0;
        for (int i = 0; i < Raw.Length; i++)
        {
            if (mask[i]) covered++;
            else if (Raw[i] != 0) stray++;
        }
        return (covered, stray);
    }

    // ---------------------------------------------------------------- writing

    public byte[] Serialize()
    {
        var w = new Writer();
        var rec = HeaderRec;
        w.Alloc(rec.Size);
        var head = (byte[])Header.Bytes.Clone();
        foreach (var a in rec.Arrays)
        {
            var (n, o) = w.Array(Header.Sub[0][a.Index]);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(a.Offset), n);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(a.Offset + 4), o);
        }
        w.Put(0, head);
        w.Pad();
        return w.ToArray();
    }

    private sealed class Writer
    {
        private const int Align = 16;
        private byte[] _buf = new byte[1 << 16];
        private int _len;

        public int Alloc(int n)
        {
            int pos = (_len + Align - 1) / Align * Align;
            int end = pos + n;
            if (end > _buf.Length)
            {
                int cap = _buf.Length;
                while (cap < end) cap *= 2;
                System.Array.Resize(ref _buf, cap);
            }
            _len = end;   // the gap stays zero (fresh buffer)
            return pos;
        }

        public void Pad() => Alloc(0);

        public void Put(int pos, byte[] b) => Buffer.BlockCopy(b, 0, _buf, pos, b.Length);

        public (uint Count, uint Offset) Array(M2Data d)
        {
            if (d.Count == 0) return (0, 0);
            int pos = Alloc(d.Bytes.Length);
            if (d.Elem.Rec is { Arrays.Count: > 0 } rec)
            {
                var bytes = (byte[])d.Bytes.Clone();
                for (int i = 0; i < d.Count; i++)
                    foreach (var a in rec.Arrays)
                    {
                        var (cn, co) = Array(d.Sub[i][a.Index]);
                        int at = i * rec.Size + a.Offset;
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), cn);
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), co);
                    }
                Put(pos, bytes);
            }
            else Put(pos, d.Bytes);
            return ((uint)d.Count, (uint)pos);
        }

        public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    }
}
