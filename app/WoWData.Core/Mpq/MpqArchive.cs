using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace WoWData.Core.Mpq;

public class MpqException : Exception
{
    public MpqException(string message) : base(message) { }
    public MpqException(string message, Exception inner) : base(message, inner) { }
}

public sealed class UnsupportedCompressionException : MpqException
{
    public UnsupportedCompressionException(string message) : base(message) { }
}

public enum MpqEntryState { None, File, Deleted, Invalid }

public readonly record struct MpqBlock(long Position, uint CompressedSize, uint FileSize, uint Flags);

/// <summary>
/// Read-only MPQ archive (port of wowdata/mpq.py). Supports what the 1.12 to 3.3.5a archives use: header format 0 and 1,
/// zlib, bzip2 (<see cref="Bzip2"/>, a few WotLK files) and stored sectors, single-unit files, encrypted files (with or
/// without FIX_KEY), sector CRC tables, delete markers, and files packed with a smaller sector size than the archive's.
/// PKWARE implode, Huffman and ADPCM are not used by any of those archives and raise
/// <see cref="UnsupportedCompressionException"/>. The file is opened read-only with shared read access; nothing
/// in this class can write to an archive.
/// </summary>
public sealed class MpqArchive : IDisposable
{
    public const uint FlagImplode = 0x00000100;
    public const uint FlagCompress = 0x00000200;
    public const uint FlagEncrypted = 0x00010000;
    public const uint FlagFixKey = 0x00020000;
    public const uint FlagSingleUnit = 0x01000000;
    public const uint FlagDeleteMarker = 0x02000000;
    public const uint FlagSectorCrc = 0x04000000;
    public const uint FlagExists = 0x80000000;

    public const byte CompZlib = 0x02;
    public const byte CompBzip2 = 0x10;

    public const uint HashEmpty = 0xFFFFFFFF;
    public const uint HashDeleted = 0xFFFFFFFE;

    public readonly record struct HashEntry(uint NameA, uint NameB, ushort Locale, ushort Platform, uint BlockIndex);

    private readonly SafeFileHandle _handle;
    private Dictionary<ulong, HashEntry[]> _hashIndex = new();
    private List<string>? _listfile;

    public string Path { get; }
    public string Name { get; }
    public long HeaderOffset { get; private set; }
    public uint HeaderSize { get; private set; }
    public ushort FormatVersion { get; private set; }
    public int SectorShift { get; private set; }
    public int SectorSize { get; private set; }
    public HashEntry[] HashTable { get; private set; } = Array.Empty<HashEntry>();
    public MpqBlock[] BlockTable { get; private set; } = Array.Empty<MpqBlock>();
    public int BlockTableSize => BlockTable.Length;
    public long FileLength { get; }

    private long _hashTablePos, _blockTablePos, _hiBlockTablePos;
    private uint _hashTableSize, _blockTableSize;

    public MpqArchive(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(path);
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        FileLength = RandomAccess.GetLength(_handle);
        try
        {
            ReadHeader();
            LoadTables();
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public void Dispose() => _handle.Dispose();

    public override string ToString() => $"<MpqArchive {Name} v{FormatVersion} {BlockTableSize} files>";

    private byte[] ReadAt(long offset, int count)
    {
        var buf = new byte[count];
        int done = 0;
        while (done < count)
        {
            int n = RandomAccess.Read(_handle, buf.AsSpan(done), offset + done);
            if (n <= 0) break;
            done += n;
        }
        if (done < count) Array.Resize(ref buf, done);
        return buf;
    }

    private void ReadHeader()
    {
        long offset = 0;
        while (true)
        {
            if (offset >= FileLength) throw new MpqException($"{Path}: no MPQ header");
            var magic = ReadAt(offset, 4);
            if (magic.Length == 4 && magic[0] == 'M' && magic[1] == 'P' && magic[2] == 'Q' && magic[3] == 0x1A) break;
            offset += 0x200;
        }
        var hd = ReadAt(offset, 44);
        if (hd.Length < 32) throw new MpqException($"{Path}: truncated MPQ header");
        HeaderOffset = offset;
        HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(4));
        FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(12));
        SectorShift = BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(14));
        SectorSize = 512 << SectorShift;
        _hashTablePos = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(16));
        _blockTablePos = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(20));
        _hashTableSize = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(24));
        _blockTableSize = BinaryPrimitives.ReadUInt32LittleEndian(hd.AsSpan(28));
        if (FormatVersion >= 1 && HeaderSize >= 44 && hd.Length >= 44)
        {
            _hiBlockTablePos = (long)BinaryPrimitives.ReadUInt64LittleEndian(hd.AsSpan(32));
            _hashTablePos |= (long)BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(40)) << 32;
            _blockTablePos |= (long)BinaryPrimitives.ReadUInt16LittleEndian(hd.AsSpan(42)) << 32;
        }
    }

    private void LoadTables()
    {
        var raw = ReadAt(HeaderOffset + _hashTablePos, checked((int)_hashTableSize * 16));
        if (raw.Length != _hashTableSize * 16) throw new MpqException($"{Name}: hash table outside the file");
        StormCrypto.Decrypt(raw, StormCrypto.HashTableKey);
        var ht = new HashEntry[_hashTableSize];
        for (int i = 0; i < ht.Length; i++)
        {
            var s = raw.AsSpan(i * 16);
            ht[i] = new HashEntry(BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
        }
        raw = ReadAt(HeaderOffset + _blockTablePos, checked((int)_blockTableSize * 16));
        if (raw.Length != _blockTableSize * 16) throw new MpqException($"{Name}: block table outside the file");
        StormCrypto.Decrypt(raw, StormCrypto.BlockTableKey);
        ushort[]? hi = null;
        if (_hiBlockTablePos != 0)
        {
            var h = ReadAt(HeaderOffset + _hiBlockTablePos, (int)_blockTableSize * 2);
            hi = new ushort[_blockTableSize];
            for (int i = 0; i < hi.Length; i++) hi[i] = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(i * 2));
        }
        var bt = new MpqBlock[_blockTableSize];
        for (int i = 0; i < bt.Length; i++)
        {
            var s = raw.AsSpan(i * 16);
            long pos = BinaryPrimitives.ReadUInt32LittleEndian(s);
            if (hi != null) pos |= (long)hi[i] << 32;
            bt[i] = new MpqBlock(pos, BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
        }
        HashTable = ht;
        BlockTable = bt;
        var idx = new Dictionary<ulong, HashEntry[]>(ht.Length / 2);
        foreach (var e in ht)
        {
            if (e.BlockIndex == HashEmpty || e.BlockIndex == HashDeleted) continue;
            ulong key = ((ulong)e.NameA << 32) | e.NameB;
            if (idx.TryGetValue(key, out var list))
            {
                Array.Resize(ref list, list.Length + 1);
                list[^1] = e;
                idx[key] = list;
            }
            else idx[key] = new[] { e };
        }
        _hashIndex = idx;
    }

    // ------------------------------------------------------------------ lookup

    /// <summary>Block-table index of <paramref name="name"/> (preferring the locale, then neutral 0), or -1.</summary>
    public int BlockIndex(string name, ushort locale = 0)
    {
        name = StormCrypto.Normalize(name);
        ulong key = ((ulong)StormCrypto.HashString(name, StormCrypto.HashNameA) << 32) | StormCrypto.HashString(name, StormCrypto.HashNameB);
        if (!_hashIndex.TryGetValue(key, out var cands)) return -1;
        long best = -1;
        foreach (var c in cands)
        {
            if (c.Locale == locale) { best = c.BlockIndex; break; }
            if (c.Locale == 0 && best < 0) best = c.BlockIndex;
        }
        if (best < 0) best = cands[0].BlockIndex;
        return best >= BlockTable.Length ? -1 : (int)best;
    }

    /// <summary>Block index found the way the client's Storm does it (SFile_LocateFile @006549a0): start at bucket
    /// hash(name, 0) &amp; (size-1) and probe linearly until the name or an empty entry. Used to verify archives we write.</summary>
    public int StormLookup(string name)
    {
        name = StormCrypto.Normalize(name);
        int size = HashTable.Length;
        if (size == 0) return -1;
        int i = (int)(StormCrypto.HashString(name, StormCrypto.HashTableOffset) & (uint)(size - 1));
        uint a = StormCrypto.HashString(name, StormCrypto.HashNameA), b = StormCrypto.HashString(name, StormCrypto.HashNameB);
        for (int n = 0; n < size; n++)
        {
            var e = HashTable[i];
            if (e.BlockIndex == HashEmpty) return -1;
            if (e.NameA == a && e.NameB == b && e.BlockIndex != HashDeleted) return (int)e.BlockIndex;
            i = (i + 1) & (size - 1);
        }
        return -1;
    }

    /// <summary>File, Deleted (a delete marker hides the file in every lower-priority archive), Invalid or None.</summary>
    public MpqEntryState EntryState(string name)
    {
        int bi = BlockIndex(name);
        if (bi < 0) return MpqEntryState.None;
        uint flags = BlockTable[bi].Flags;
        if ((flags & FlagDeleteMarker) != 0) return MpqEntryState.Deleted;
        if ((flags & FlagExists) != 0) return MpqEntryState.File;
        return MpqEntryState.Invalid;
    }

    /// <summary>The block of an existing file, or null (delete markers count as missing).</summary>
    public MpqBlock? FileEntry(string name)
    {
        int bi = BlockIndex(name);
        if (bi < 0) return null;
        var e = BlockTable[bi];
        if ((e.Flags & FlagExists) == 0 || (e.Flags & FlagDeleteMarker) != 0) return null;
        return e;
    }

    public bool HasFile(string name) => FileEntry(name) != null;

    public long? FileSize(string name) => FileEntry(name)?.FileSize;

    // ------------------------------------------------------------------ reading

    /// <summary>Whole file, or null when the archive does not contain it.</summary>
    public byte[]? Read(string name)
    {
        var e = FileEntry(name);
        return e == null ? null : ReadEntry(StormCrypto.Normalize(name), e.Value);
    }

    private byte[] ReadEntry(string name, MpqBlock e)
    {
        int fsize = (int)e.FileSize;
        if (fsize == 0) return Array.Empty<byte>();
        var raw = ReadAt(HeaderOffset + e.Position, (int)e.CompressedSize);
        uint? key = (e.Flags & FlagEncrypted) != 0
            ? StormCrypto.FileKey(name, (uint)e.Position, e.FileSize, (e.Flags & FlagFixKey) != 0) : null;
        if ((e.Flags & FlagSingleUnit) != 0)
        {
            if (key != null) StormCrypto.Decrypt(raw, key.Value);
            if ((e.Flags & (FlagCompress | FlagImplode)) != 0 && raw.Length < fsize) return Decompress(raw, fsize, e.Flags);
            return raw.Length == fsize ? raw : raw.AsSpan(0, Math.Min(fsize, raw.Length)).ToArray();
        }
        if ((e.Flags & (FlagCompress | FlagImplode)) == 0)
        {
            if (key == null) return raw.Length == fsize ? raw : raw.AsSpan(0, Math.Min(fsize, raw.Length)).ToArray();
            for (int i = 0, s = 0; i < fsize; i += SectorSize, s++)
                StormCrypto.Decrypt(raw.AsSpan(i, Math.Min(SectorSize, raw.Length - i)), key.Value + (uint)s);
            return raw.AsSpan(0, fsize).ToArray();
        }
        int sectorSize = SectorSize;
        var offs = SectorTable(raw, fsize, sectorSize, e.Flags, key, name);
        if (offs[0] != offs.Length * 4)
        {
            // A few files were packed with a smaller sector size than the archive header says (WotLK 3.3.5a
            // backup-enUS.MPQ: 4096-byte sectors in a 16384-byte archive). The sector table's first offset is its own
            // size, which tells the sector count; take the smaller sector size that agrees with it.
            for (int sz = SectorSize / 2; sz >= 512; sz /= 2)
            {
                int[] t;
                try { t = SectorTable(raw, fsize, sz, e.Flags, key, name); } catch (MpqException) { break; }
                if (t[0] == t.Length * 4) { sectorSize = sz; offs = t; break; }
            }
        }
        int nsect = (fsize + sectorSize - 1) / sectorSize;
        var output = new byte[fsize];
        int outPos = 0;
        for (int i = 0; i < nsect; i++)
        {
            int start = offs[i], end = offs[i + 1];
            if (start < 0 || end > raw.Length || end < start) throw new MpqException($"{Name}: {name}: bad sector offsets");
            var d = raw.AsSpan(start, end - start).ToArray();
            if (key != null) StormCrypto.Decrypt(d, key.Value + (uint)i);
            int expect = Math.Min(sectorSize, fsize - i * sectorSize);
            var sector = Decompress(d, expect, e.Flags);
            sector.AsSpan(0, Math.Min(sector.Length, fsize - outPos)).CopyTo(output.AsSpan(outPos));
            outPos += sector.Length;
        }
        return output;
    }

    private int[] SectorTable(byte[] raw, int fsize, int sectorSize, uint flags, uint? key, string name)
    {
        int nsect = (fsize + sectorSize - 1) / sectorSize;
        int ntab = nsect + 1 + ((flags & FlagSectorCrc) != 0 ? 1 : 0);
        if (raw.Length < ntab * 4) throw new MpqException($"{Name}: {name}: sector table outside the file");
        var table = raw.AsSpan(0, ntab * 4).ToArray();
        if (key != null) StormCrypto.Decrypt(table, key.Value - 1);
        var offs = new int[ntab];
        for (int i = 0; i < ntab; i++) offs[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4));
        return offs;
    }

    private static byte[] Decompress(byte[] d, int expect, uint flags)
    {
        if (d.Length >= expect) return d.Length == expect ? d : d.AsSpan(0, expect).ToArray();
        if ((flags & FlagImplode) != 0) throw new UnsupportedCompressionException("PKWARE implode (block flag) is not supported");
        byte mask = d[0];
        if (mask == CompZlib)
        {
            using var ms = new MemoryStream(d, 1, d.Length - 1);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            var outBuf = new byte[expect];
            int got = 0;
            while (got < expect)
            {
                int n = z.Read(outBuf, got, expect - got);
                if (n <= 0) break;
                got += n;
            }
            return got == expect ? outBuf : outBuf.AsSpan(0, got).ToArray();
        }
        if (mask == CompBzip2) return Bzip2.Decompress(d, 1, expect);
        throw new UnsupportedCompressionException($"sector compression mask 0x{mask:x2} is not supported");
    }

    /// <summary>Compression mask byte of every compressed sector (diagnostics).</summary>
    public List<byte> SectorMasks(string name)
    {
        var masks = new List<byte>();
        var ent = FileEntry(name);
        if (ent == null) return masks;
        var e = ent.Value;
        if (e.FileSize == 0 || (e.Flags & FlagCompress) == 0) return masks;
        if ((e.Flags & FlagSingleUnit) != 0)
        {
            if (e.CompressedSize < e.FileSize) masks.Add(ReadAt(HeaderOffset + e.Position, 1)[0]);
            return masks;
        }
        int fsize = (int)e.FileSize;
        int nsect = (fsize + SectorSize - 1) / SectorSize;
        int ntab = nsect + 1 + ((e.Flags & FlagSectorCrc) != 0 ? 1 : 0);
        var table = ReadAt(HeaderOffset + e.Position, ntab * 4);
        uint? key = (e.Flags & FlagEncrypted) != 0 ? StormCrypto.FileKey(name, (uint)e.Position, e.FileSize, (e.Flags & FlagFixKey) != 0) : null;
        if (key != null) StormCrypto.Decrypt(table, key.Value - 1);
        for (int i = 0; i < nsect; i++)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4)), b = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4 + 4));
            int expect = Math.Min(SectorSize, fsize - i * SectorSize);
            if (b - a < expect)
            {
                var head = ReadAt(HeaderOffset + e.Position + a, 4);
                if (key != null) StormCrypto.Decrypt(head, key.Value + (uint)i);
                masks.Add(head[0]);
            }
        }
        return masks;
    }

    // ------------------------------------------------------------------ listfile

    /// <summary>Names from the archive's (listfile), in archive order (case as stored).</summary>
    public IReadOnlyList<string> Listfile()
    {
        if (_listfile != null) return _listfile;
        var data = Read("(listfile)") ?? Array.Empty<byte>();
        var text = System.Text.Encoding.UTF8.GetString(data);
        var names = new List<string>();
        foreach (var part in text.Replace(';', '\n').Replace('\r', '\n').Split('\n'))
        {
            var p = part.Trim().Trim('\0');
            if (p.Length > 0) names.Add(p);
        }
        _listfile = names;
        return names;
    }
}
