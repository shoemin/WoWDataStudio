using System.Buffers.Binary;

namespace WoWData.Core.Maps;

/// <summary>
/// A WDT (a map's tile list) for editing and writing back. The 1.12 loader reads it positionally (MVER, MPHD, MAIN and,
/// for a single-building map, MWMO + MODF), so the file is kept as its chunks in order and the edits are made in the
/// MAIN entries in place; <see cref="Serialize"/> writes the chunks back as they are (byte-identical when unedited,
/// checked on all 43 client WDTs).
/// </summary>
public sealed class WdtDocument
{
    private readonly byte[] _data;
    public WdtFile Parsed { get; }

    public WdtDocument(byte[] data)
    {
        _data = (byte[])data.Clone();
        Parsed = new WdtFile(data);
    }

    /// <summary>File offset of MAIN entry (x, y), or -1 when the file has no complete MAIN.</summary>
    private long MainEntry(int x, int y)
    {
        var slot = Parsed.Slots.FirstOrDefault(s => s.Expected == "MAIN" && s.Magic == "MAIN");
        if (slot.Magic == null) return -1;
        long p = slot.Position + 8 + (y * 64L + x) * 8;
        return p + 8 <= _data.Length ? p : -1;
    }

    public bool HasTile(int x, int y)
    {
        long p = MainEntry(x, y);
        return p >= 0 && (BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)p)) & 1) != 0;
    }

    /// <summary>Add (flag 1) or remove a tile from the map's tile list.</summary>
    public void SetTile(int x, int y, bool present)
    {
        long p = MainEntry(x, y);
        if (p < 0) throw new InvalidOperationException("the WDT has no complete MAIN table");
        BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan((int)p), present ? 1u : 0u);
    }

    public byte[] Serialize() => (byte[])_data.Clone();
}

/// <summary>
/// A WDL (a map's far-distance terrain) for editing and writing back: the chunks in their order (MVER, MAOF, then one MARE
/// of 17x17 + 16x16 heights per tile, in the order of the original file), the MAOF table recomputed from where each
/// MARE lands. Byte-identical when unedited (all 43 client WDLs); a tile's heights can be replaced and a tile added.
/// </summary>
public sealed class WdlDocument
{
    private sealed class Piece
    {
        public long Offset;
        public string Magic = "";
        public byte[] Bytes = Array.Empty<byte>();
    }

    private readonly List<Piece> _pieces = new();
    private readonly byte[] _original;
    /// <summary>tile index (y * 64 + x) -> MARE piece.</summary>
    private readonly Dictionary<int, Piece> _tiles = new();
    public bool CanRebuild { get; }

    public WdlDocument(byte[] data)
    {
        _original = data;
        var top = MapBytes.Chunks(data);
        bool ok = top.Count >= 2 && top[0].Magic == "MVER" && top[1].Magic == "MAOF" && top[1].Size == MapBytes.WdlMaofSize;
        for (int i = 0; i < top.Count && ok; i++)
        {
            long end = top[i].Offset + 8 + top[i].Size;
            if (end > data.Length) { ok = false; break; }
            long next = i + 1 < top.Count ? top[i + 1].Offset : data.Length;
            _pieces.Add(new Piece { Offset = top[i].Offset, Magic = top[i].Magic ?? "", Bytes = MapBytes.Slice(data, top[i].Offset, next) });
        }
        if (ok)
        {
            var w = new WdlFile(data);
            for (int i = 0; i < w.Offsets.Length; i++)
            {
                if (w.Offsets[i] == 0) continue;
                var p = _pieces.FirstOrDefault(x => x.Offset == w.Offsets[i] && x.Magic == "MARE");
                if (p == null) { ok = false; break; }
                _tiles[i] = p;
            }
        }
        CanRebuild = ok;
    }

    public short[]? GetHeights(int x, int y)
    {
        if (!_tiles.TryGetValue(y * 64 + x, out var p) || p.Bytes.Length < 8 + MapBytes.WdlMareData) return null;
        var h = new short[545];
        for (int i = 0; i < 545; i++) h[i] = BinaryPrimitives.ReadInt16LittleEndian(p.Bytes.AsSpan(8 + i * 2));
        return h;
    }

    /// <summary>Replace a tile's far heights (adds the tile when it has none).</summary>
    public void SetHeights(int x, int y, short[] heights)
    {
        if (!CanRebuild) throw new InvalidOperationException("the WDL layout could not be split");
        if (heights.Length != 545) throw new ArgumentException("545 heights expected");
        if (!_tiles.TryGetValue(y * 64 + x, out var p))
        {
            p = new Piece { Magic = "MARE", Offset = -1, Bytes = new byte[8 + MapBytes.WdlMareData] };
            p.Bytes[0] = (byte)'E'; p.Bytes[1] = (byte)'R'; p.Bytes[2] = (byte)'A'; p.Bytes[3] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(p.Bytes.AsSpan(4), MapBytes.WdlMareData);
            _pieces.Add(p);
            _tiles[y * 64 + x] = p;
        }
        for (int i = 0; i < 545; i++) BinaryPrimitives.WriteInt16LittleEndian(p.Bytes.AsSpan(8 + i * 2), heights[i]);
    }

    public byte[] Serialize()
    {
        if (!CanRebuild) return (byte[])_original.Clone();
        var ms = new MemoryStream();
        var at = new Dictionary<Piece, long>();
        foreach (var p in _pieces)
        {
            at[p] = ms.Position;
            ms.Write(p.Bytes);
        }
        var buf = ms.ToArray();
        long table = at[_pieces[1]] + 8;
        for (int i = 0; i < 4096; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)(table + i * 4)), _tiles.TryGetValue(i, out var p) ? (uint)at[p] : 0u);
        return buf;
    }
}
