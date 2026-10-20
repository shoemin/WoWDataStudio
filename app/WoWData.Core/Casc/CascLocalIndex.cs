using System.Buffers.Binary;
using System.Globalization;

namespace WoWData.Core.Casc;

/// <summary>Where an encoded file is: archive <c>data.###</c>, byte offset of its 30-byte entry header, and the size
/// of header plus BLTE frame.</summary>
public readonly record struct CascIndexEntry(int Archive, long Offset, uint Size);

/// <summary>
/// The local index of a CASC store: the <c>.idx</c> files of <c>Data\data</c> (version 7; written from the public
/// format description). Files are named <c>BBVVVVVVVV.idx</c> (bucket, version); only the newest version of each
/// bucket is read. Each file: header-hash size and hash, a header (version, bucket, size/offset/key byte counts,
/// offset bits, archive size limit), padding to 16 bytes, entries size and hash, then entries of a truncated encoded
/// key (9 bytes), a big-endian archive+offset field (archive number above the offset bits) and a little-endian size.
/// Kept as one sorted array of 24-byte structs per bucket (no per-entry objects).
/// </summary>
public sealed class CascLocalIndex
{
    private struct Entry
    {
        public ulong K1;      // key bytes 0-7, big-endian so that numeric order = byte order
        public ulong Loc;     // archive << 32 | offset
        public uint Size;
        public byte K2;       // key byte 8
    }

    private readonly Entry[][] _buckets = new Entry[256][];
    private bool _bucketsTrusted = true;
    private int _keyLength = 9;

    /// <summary>The number of distinct keys.</summary>
    public int Count { get; private set; }

    /// <summary>The index files that were read (newest per bucket).</summary>
    public IReadOnlyList<string> Files { get; private set; } = Array.Empty<string>();

    /// <summary>The idx header version seen (7 for every store measured).</summary>
    public int Version { get; private set; }

    private CascLocalIndex()
    {
        for (int i = 0; i < _buckets.Length; i++) _buckets[i] = Array.Empty<Entry>();
    }

    /// <summary>Reads the newest <c>.idx</c> of every bucket in a folder (normally <c>Data\data</c>).</summary>
    public static CascLocalIndex Load(string dir)
    {
        var best = new Dictionary<int, (uint Ver, string Path)>();
        foreach (var p in Directory.EnumerateFiles(dir, "*.idx"))
        {
            var n = Path.GetFileNameWithoutExtension(p);
            if (n.Length != 10 || !int.TryParse(n[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)
                || !uint.TryParse(n[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) continue;
            if (!best.TryGetValue(b, out var cur) || v > cur.Ver) best[b] = (v, p);
        }
        var idx = new CascLocalIndex();
        var files = new List<string>();
        foreach (var (b, (_, p)) in best.OrderBy(kv => kv.Key))
        {
            idx.AddFile(ReadShared(p), b);
            files.Add(p);
        }
        idx.Files = files;
        return idx;
    }

    /// <summary>Parses one index file's bytes (exposed for tests).</summary>
    public static CascLocalIndex Parse(byte[] data)
    {
        var idx = new CascLocalIndex();
        idx.AddFile(data, -1);
        return idx;
    }

    internal static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[f.Length];
        f.ReadExactly(b);
        return b;
    }

    /// <summary>The bucket of a key: xor of its first 9 bytes, then the two nibbles xored.</summary>
    public static int BucketOf(ReadOnlySpan<byte> key)
    {
        int x = 0;
        for (int i = 0; i < 9; i++) x ^= key[i];
        return (x & 0xF) ^ (x >> 4);
    }

    private void AddFile(byte[] d, int fileBucket)
    {
        if (d.Length < 0x28) throw new InvalidDataException("idx: file too short");
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(d);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8));
        int bucket = d[10];
        int sizeLen = d[12], offLen = d[13], keyLen = d[14], offBits = d[15];
        if (version != 7) throw new NotSupportedException($"idx version {version} (only version 7 is supported)");
        if (keyLen is < 1 or > 9 || sizeLen is < 1 or > 4 || offLen is < 1 or > 8 || offBits > 32 || offBits >= offLen * 8)
            throw new InvalidDataException($"idx: unexpected field sizes key {keyLen} offset {offLen} size {sizeLen} offset bits {offBits}");
        Version = version;
        _keyLength = keyLen;
        long pos = (8 + headerSize + 0xF) & ~0xFL;
        if (pos + 8 > d.Length) throw new InvalidDataException("idx: header runs past the end");
        uint entriesSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos));
        pos += 8;
        int rec = keyLen + offLen + sizeLen;
        if (pos + entriesSize > d.Length) throw new InvalidDataException("idx: entries run past the end");
        int n = (int)(entriesSize / rec);
        var entries = new Entry[n];
        ulong offMask = (1UL << offBits) - 1;
        Span<byte> key = stackalloc byte[9];
        bool sorted = true;
        for (int i = 0; i < n; i++)
        {
            var e = d.AsSpan((int)pos + i * rec, rec);
            key.Clear();
            e[..keyLen].CopyTo(key);
            ulong raw = 0;
            for (int k = 0; k < offLen; k++) raw = (raw << 8) | e[keyLen + k];
            uint size = 0;
            for (int k = sizeLen - 1; k >= 0; k--) size = (size << 8) | e[keyLen + offLen + k];
            entries[i] = new Entry
            {
                K1 = BinaryPrimitives.ReadUInt64BigEndian(key),
                K2 = key[8],
                Loc = ((raw >> offBits) << 32) | (raw & offMask),
                Size = size,
            };
            if (i > 0 && Compare(entries[i - 1], entries[i]) > 0) sorted = false;
            if (fileBucket >= 0 && keyLen == 9 && BucketOf(key) != fileBucket) _bucketsTrusted = false;
        }
        if (!sorted) Array.Sort(entries, Compare);
        // drop repeated keys (the first one after sorting wins; none were seen in the measured stores)
        int w = 0;
        for (int i = 0; i < entries.Length; i++)
            if (w == 0 || Compare(entries[w - 1], entries[i]) != 0) entries[w++] = entries[i];
        Array.Resize(ref entries, w);
        int slot = fileBucket >= 0 ? fileBucket : bucket;
        if (fileBucket < 0) _bucketsTrusted = false;
        if (_buckets[slot].Length > 0)
        {
            // a second file for the same bucket (only through Parse): merge
            var merged = _buckets[slot].Concat(entries).ToArray();
            Array.Sort(merged, Compare);
            entries = merged;
        }
        Count += w;
        _buckets[slot] = entries;
    }

    private static int Compare(Entry a, Entry b)
    {
        int c = a.K1.CompareTo(b.K1);
        return c != 0 ? c : a.K2.CompareTo(b.K2);
    }

    /// <summary>Looks up an encoded key (only its first 9 bytes are used).</summary>
    public bool TryGet(ReadOnlySpan<byte> ekey, out CascIndexEntry entry)
    {
        Span<byte> key = stackalloc byte[9];
        key.Clear();
        ekey[..Math.Min(_keyLength, ekey.Length)].CopyTo(key);
        var probe = new Entry { K1 = BinaryPrimitives.ReadUInt64BigEndian(key), K2 = key[8] };
        if (_bucketsTrusted)
            return Search(_buckets[BucketOf(key)], probe, out entry);
        foreach (var b in _buckets)
            if (b.Length > 0 && Search(b, probe, out entry)) return true;
        entry = default;
        return false;
    }

    public bool Contains(ReadOnlySpan<byte> ekey) => TryGet(ekey, out _);

    private static bool Search(Entry[] a, Entry probe, out CascIndexEntry entry)
    {
        int lo = 0, hi = a.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = Compare(a[mid], probe);
            if (c == 0)
            {
                ref var e = ref a[mid];
                entry = new CascIndexEntry((int)(e.Loc >> 32), (long)(e.Loc & 0xFFFFFFFF), e.Size);
                return true;
            }
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        entry = default;
        return false;
    }
}
