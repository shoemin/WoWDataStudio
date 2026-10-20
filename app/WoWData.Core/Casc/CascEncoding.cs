using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.Casc;

/// <summary>
/// The encoding file (written from the public format description): content key (MD5 of the decoded file) to encoded
/// key(s) (the name in the archives), and encoded key to its ESpec (how it is encoded, e.g. <c>z</c>, or
/// <c>e:{KEY,IV,z}</c> when encrypted). Big-endian header <c>"EN"</c>, version, key sizes, page sizes in KiB, page
/// counts, ESpec block size; then the ESpec strings, the content-key page index (first key + MD5 per page), the
/// content-key pages, the ESpec page index and the ESpec pages.
/// <para>The decoded file is kept as one byte array and searched in place (binary search over the page index, then a
/// scan of one page), so millions of entries cost no objects.</para>
/// </summary>
public sealed class CascEncoding
{
    private readonly byte[] _d;
    private readonly int _ckeySize, _ekeySize;
    private readonly int _cePageSize, _cePageCount, _cePageIndex, _cePages;
    private readonly int _esPageSize, _esPageCount, _esPageIndex, _esPages;
    private readonly bool[] _specEncrypted;

    /// <summary>The ESpec strings.</summary>
    public IReadOnlyList<string> ESpecs { get; }

    public int CKeySize => _ckeySize;
    public int EKeySize => _ekeySize;

    public CascEncoding(byte[] data)
    {
        _d = data;
        if (data.Length < 22 || data[0] != 'E' || data[1] != 'N') throw new InvalidDataException("encoding: no EN header");
        var s = data.AsSpan();
        _ckeySize = data[3];
        _ekeySize = data[4];
        _cePageSize = BinaryPrimitives.ReadUInt16BigEndian(s[5..]) * 1024;
        _esPageSize = BinaryPrimitives.ReadUInt16BigEndian(s[7..]) * 1024;
        _cePageCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(s[9..]));
        _esPageCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(s[13..]));
        int specSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(s[18..]));
        if (_ckeySize is < 1 or > 16 || _ekeySize is < 9 or > 16) throw new InvalidDataException($"encoding: key sizes {_ckeySize}/{_ekeySize}");
        int pos = 22;
        var specs = Encoding.ASCII.GetString(data, pos, specSize).Split('\0');
        ESpecs = specs;
        _specEncrypted = specs.Select(x => x.Contains("e:{", StringComparison.Ordinal)).ToArray();
        pos += specSize;
        _cePageIndex = pos;
        pos = checked(pos + _cePageCount * (_ckeySize + 16));
        _cePages = pos;
        pos = checked(pos + _cePageCount * _cePageSize);
        _esPageIndex = pos;
        pos = checked(pos + _esPageCount * (_ekeySize + 16));
        _esPages = pos;
        if (checked(pos + _esPageCount * _esPageSize) > data.Length) throw new InvalidDataException("encoding: pages run past the end of the file");
    }

    /// <summary>Finds a content key. Returns the offset of its entry (key count byte), or -1.</summary>
    private int FindCKey(ReadOnlySpan<byte> ckey)
    {
        ckey = ckey[..Math.Min(ckey.Length, _ckeySize)];
        int page = FindPage(_cePageIndex, _cePageCount, _ckeySize, ckey);
        if (page < 0) return -1;
        int o = _cePages + page * _cePageSize, end = o + _cePageSize;
        while (o + 6 + _ckeySize <= end)
        {
            int kc = _d[o];
            if (kc == 0) break;
            int c = CompareKey(_d.AsSpan(o + 6, _ckeySize), ckey);
            if (c == 0) return o;
            if (c > 0) break;  // pages are sorted
            o += 6 + _ckeySize + kc * _ekeySize;
        }
        return -1;
    }

    /// <summary>Byte-order comparison, with the first 8 bytes compared as one big-endian number.</summary>
    private static int CompareKey(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length >= 8 && b.Length >= 8)
        {
            ulong x = BinaryPrimitives.ReadUInt64BigEndian(a), y = BinaryPrimitives.ReadUInt64BigEndian(b);
            if (x != y) return x < y ? -1 : 1;
            return a[8..].SequenceCompareTo(b[8..]);
        }
        return a.SequenceCompareTo(b);
    }

    /// <summary>The last page whose first key is &lt;= key, or -1.</summary>
    private int FindPage(int indexStart, int count, int keySize, ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = count - 1, found = -1;
        int stride = keySize + 16;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = CompareKey(_d.AsSpan(indexStart + mid * stride, keySize), key);
            if (c <= 0)
            {
                found = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>The number of encoded keys of a content key (0 when unknown).</summary>
    public int EKeyCount(ReadOnlySpan<byte> ckey)
    {
        int o = FindCKey(ckey);
        return o < 0 ? 0 : _d[o];
    }

    /// <summary>The i-th encoded key of a content key (a view into the encoding file).</summary>
    public ReadOnlySpan<byte> EKey(ReadOnlySpan<byte> ckey, int i)
    {
        int o = FindCKey(ckey);
        if (o < 0 || i < 0 || i >= _d[o]) throw new ArgumentOutOfRangeException(nameof(i));
        return _d.AsSpan(o + 6 + _ckeySize + i * _ekeySize, _ekeySize);
    }

    /// <summary>The entry of a content key (for <see cref="EKeyCountAt"/> / <see cref="EKeyAt"/>), or -1.</summary>
    public int FindEntry(ReadOnlySpan<byte> ckey) => FindCKey(ckey);

    /// <summary>The number of encoded keys of an entry from <see cref="FindEntry"/>.</summary>
    public int EKeyCountAt(int entry) => _d[entry];

    /// <summary>The i-th encoded key of an entry from <see cref="FindEntry"/> (a view into the encoding file).</summary>
    public ReadOnlySpan<byte> EKeyAt(int entry, int i) => _d.AsSpan(entry + 6 + _ckeySize + i * _ekeySize, _ekeySize);

    /// <summary>The decoded size of a content key, or -1.</summary>
    public long ContentSize(ReadOnlySpan<byte> ckey)
    {
        int o = FindCKey(ckey);
        if (o < 0) return -1;
        long v = 0;
        for (int i = 1; i <= 5; i++) v = (v << 8) | _d[o + i];
        return v;
    }

    /// <summary>The ESpec index of an encoded key, or -1 when the key has no ESpec entry.</summary>
    public int ESpecIndex(ReadOnlySpan<byte> ekey)
    {
        ekey = ekey[..Math.Min(ekey.Length, _ekeySize)];
        int page = FindPage(_esPageIndex, _esPageCount, _ekeySize, ekey);
        if (page < 0) return -1;
        int stride = _ekeySize + 9;
        int o = _esPages + page * _esPageSize, end = o + _esPageSize;
        while (o + stride <= end)
        {
            var k = _d.AsSpan(o, _ekeySize);
            if (k.IndexOfAnyExcept((byte)0) < 0) break;  // padding
            int c = CompareKey(k, ekey);
            if (c == 0) return (int)BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(o + _ekeySize));
            if (c > 0) break;
            o += stride;
        }
        return -1;
    }

    /// <summary>True when the ESpec of an encoded key contains an encrypted part (<c>e:{...}</c>).</summary>
    public bool IsEncrypted(ReadOnlySpan<byte> ekey)
    {
        int i = ESpecIndex(ekey);
        return i >= 0 && i < _specEncrypted.Length && _specEncrypted[i];
    }

    /// <summary>The number of content keys (one pass over all pages).</summary>
    public int CountCKeys()
    {
        int n = 0;
        for (int p = 0; p < _cePageCount; p++)
        {
            int o = _cePages + p * _cePageSize, end = o + _cePageSize;
            while (o + 6 + _ckeySize <= end && _d[o] != 0)
            {
                n++;
                o += 6 + _ckeySize + _d[o] * _ekeySize;
            }
        }
        return n;
    }
}
