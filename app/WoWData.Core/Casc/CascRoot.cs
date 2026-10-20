using System.Buffers.Binary;

namespace WoWData.Core.Casc;

/// <summary>
/// The WoW root file (written from the public format description): FileDataID to content key, with locale and content
/// flags per block and (for some or all records) a Jenkins name hash.
/// <list type="bullet">
/// <item>Legacy (no magic, e.g. WoD 6.x): blocks of count, content flags, locale flags, FileDataID deltas, then per
/// record content key (16) + name hash (8).</item>
/// <item><c>TSFM</c> ("MFST") v1: header total/named count; blocks of count, content flags, locale flags, deltas, all
/// content keys, then all name hashes unless the block has the "no name hash" flag.</item>
/// <item>MFST v2 (header size, version 2, total, named): the block header is count, locale flags and three content-flag
/// fields (u32, u32, u8) combined as <c>f1 | f2 | f3 &lt;&lt; 17</c>.</item>
/// </list>
/// <para><b>Which record of a FileDataID is used.</b> A FileDataID usually has several records: one per locale (all
/// 12-13 locales ship in the root; the content of other locales is mostly not downloaded), sometimes a low-violence
/// alternate, a Mac-only variant, or (measured in "1.60") a high-resolution texture variant. The reader keeps exactly
/// one record per FileDataID, the first by these rules, in this order:
/// <list type="number">
/// <item>its locale mask includes enUS (an "all locales" mask does too): enUS is the locale every measured snapshot
/// downloaded;</item>
/// <item>it is not a low-violence alternate (flag 0x80): a normal client loads the other one;</item>
/// <item>it is not Mac-only (0x10 "macOS" without 0x8 "Windows"): those were measured as not downloaded on Windows;</item>
/// <item>it matches <see cref="CascOpenOptions.PreferHighResTextures"/> for flag 0x1. Measured in 1.60.1.70450: 96k
/// textures have two enUS records that differ only in 0x1, and in a 2,000-file sample the 0x1 record was always the
/// larger BLP with 4x the width and height (e.g. 1024x1024 against 256x256). The default takes the standard variant,
/// which is the resolution 1.12 uses;</item>
/// <item>its locale mask has fewer locales (an enUS(+enGB) record beats the all-locale record of the same file,
/// measured in Retail: 474 FileDataIDs);</item>
/// <item>it comes earlier in the file.</item>
/// </list>
/// A FileDataID that has no enUS record keeps its best other-locale record (its content is usually not local, so
/// <see cref="CascStorage.State"/> says NotLocal); <see cref="CascStorage.FileDataIds"/> leaves those out unless asked
/// for all locales.</para>
/// </summary>
public sealed class CascRoot
{
    public const uint LocaleEnUS = 0x2;
    public const uint FlagHighResVariant = 0x1;
    public const uint FlagWindows = 0x8;
    public const uint FlagMacOS = 0x10;
    public const uint FlagLowViolence = 0x80;
    public const uint FlagEncrypted = 0x8000000;
    public const uint FlagNoNameHash = 0x10000000;

    private const byte BitEnUS = 1, BitLowViolence = 2, BitEncryptedFlag = 4;

    private readonly uint[] _fdids;       // sorted
    private readonly byte[] _ckeys;       // 16 per FileDataID
    private readonly byte[] _bits;        // BitEnUS / BitLowViolence / BitEncryptedFlag of the chosen record
    private readonly ulong[] _hashes;     // sorted name hashes
    private readonly uint[] _hashFdids;

    /// <summary>"legacy", "MFST v1" or "MFST v2".</summary>
    public string Format { get; }
    public int RecordCount { get; }
    public int BlockCount { get; }
    public int NamedRecordCount { get; }

    /// <summary>The number of distinct FileDataIDs.</summary>
    public int Count => _fdids.Length;
    public int NameHashCount => _hashes.Length;

    private CascRoot(string format, int records, int blocks, int named, uint[] fdids, byte[] ckeys, byte[] bits, ulong[] hashes, uint[] hashFdids)
    {
        Format = format;
        RecordCount = records;
        BlockCount = blocks;
        NamedRecordCount = named;
        _fdids = fdids;
        _ckeys = ckeys;
        _bits = bits;
        _hashes = hashes;
        _hashFdids = hashFdids;
    }

    public static CascRoot Parse(byte[] d, bool preferHighResTextures = false)
    {
        var s = d.AsSpan();
        int pos = 0;
        int version;
        string format;
        bool allowUnnamed = false;
        if (d.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(s) == 0x4D465354)  // "TSFM" on disk
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), b = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
            uint total, namedHdr;
            if (a < 100 && d.Length >= 20)
            {
                // newer header: header size, version, total, named
                version = (int)b;
                total = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
                namedHdr = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
                pos = (int)a;
            }
            else
            {
                version = 1;
                total = a;
                namedHdr = b;
                pos = 12;
            }
            if (version is < 1 or > 2) throw new NotSupportedException($"root: MFST version {version}");
            allowUnnamed = total != namedHdr;
            format = "MFST v" + version;
        }
        else
        {
            version = 0;
            format = "legacy";
        }

        // pass 1: every record as (sort key, offset of its content key); name hashes as (hash, fdid)
        var keys = new List<ulong>(1 << 16);
        var ckeyAt = new List<int>(1 << 16);
        var hashes = new List<ulong>(1 << 16);
        var hashFdids = new List<uint>(1 << 16);
        int blocks = 0, records = 0, named = 0;
        while (pos < d.Length)
        {
            if (pos + 12 > d.Length) throw new InvalidDataException("root: truncated block header");
            int n = BinaryPrimitives.ReadInt32LittleEndian(s[pos..]);
            uint locale, cflags;
            if (version == 2)
            {
                if (pos + 17 > d.Length) throw new InvalidDataException("root: truncated block header");
                locale = BinaryPrimitives.ReadUInt32LittleEndian(s[(pos + 4)..]);
                cflags = BinaryPrimitives.ReadUInt32LittleEndian(s[(pos + 8)..]) | BinaryPrimitives.ReadUInt32LittleEndian(s[(pos + 12)..]) | ((uint)d[pos + 16] << 17);
                pos += 17;
            }
            else
            {
                cflags = BinaryPrimitives.ReadUInt32LittleEndian(s[(pos + 4)..]);
                locale = BinaryPrimitives.ReadUInt32LittleEndian(s[(pos + 8)..]);
                pos += 12;
            }
            if (n < 0) throw new InvalidDataException("root: negative record count");
            bool hasNames = version == 0 || !(allowUnnamed && (cflags & FlagNoNameHash) != 0);
            long need = 4L * n + (version == 0 ? 24L * n : 16L * n + (hasNames ? 8L * n : 0));
            if (pos + need > d.Length) throw new InvalidDataException($"root: block {blocks} runs past the end of the file");
            int deltas = pos;
            pos += 4 * n;
            bool enUS = (locale & LocaleEnUS) != 0;
            bool lowViolence = (cflags & FlagLowViolence) != 0;
            // rank (lower is better), most important bit first; see the class comment
            bool macOnly = (cflags & FlagMacOS) != 0 && (cflags & FlagWindows) == 0;
            bool resolutionMismatch = ((cflags & FlagHighResVariant) != 0) != preferHighResTextures;
            uint rank = (enUS ? 0u : 0x80u) | (lowViolence ? 0x40u : 0u) | (macOnly ? 0x20u : 0u) | (resolutionMismatch ? 0x10u : 0u)
                | (uint)Math.Min(15, System.Numerics.BitOperations.PopCount(locale));
            byte bits = (byte)((enUS ? BitEnUS : 0) | (lowViolence ? BitLowViolence : 0) | ((cflags & FlagEncrypted) != 0 ? BitEncryptedFlag : 0));
            long fdid = -1;
            int ckeys = pos, names = pos + 16 * n;
            for (int i = 0; i < n; i++)
            {
                fdid += 1 + BinaryPrimitives.ReadInt32LittleEndian(s[(deltas + 4 * i)..]);
                if (fdid is < 0 or > uint.MaxValue) throw new InvalidDataException("root: FileDataID out of range");
                int ck = version == 0 ? pos + 24 * i : ckeys + 16 * i;
                // sort key: fdid, rank, block number (earlier wins); the low byte keeps the record's bits
                keys.Add(((ulong)fdid << 32) | ((ulong)rank << 24) | ((ulong)Math.Min(blocks, 0xFFFF) << 8) | bits);
                ckeyAt.Add(ck);
                if (hasNames)
                {
                    int h = version == 0 ? pos + 24 * i + 16 : names + 8 * i;
                    hashes.Add(BinaryPrimitives.ReadUInt64LittleEndian(s[h..]));
                    hashFdids.Add((uint)fdid);
                    named++;
                }
            }
            pos += version == 0 ? 24 * n : 16 * n + (hasNames ? 8 * n : 0);
            records += n;
            blocks++;
        }

        // pass 2: one record per FileDataID (the best ranked)
        var k = keys.ToArray();
        var at = ckeyAt.ToArray();
        keys.Clear();
        ckeyAt.Clear();
        Array.Sort(k, at);
        int unique = 0;
        for (int i = 0; i < k.Length; i++)
            if (i == 0 || (k[i] >> 32) != (k[i - 1] >> 32)) unique++;
        var fdids = new uint[unique];
        var ck16 = new byte[unique * 16];
        var bitsArr = new byte[unique];
        int w = 0;
        for (int i = 0; i < k.Length; i++)
        {
            if (i > 0 && (k[i] >> 32) == (k[i - 1] >> 32)) continue;
            fdids[w] = (uint)(k[i] >> 32);
            bitsArr[w] = (byte)k[i];
            d.AsSpan(at[i], 16).CopyTo(ck16.AsSpan(w * 16));
            w++;
        }

        var hk = hashes.ToArray();
        var hf = hashFdids.ToArray();
        Array.Sort(hk, hf);
        int hw = 0;
        for (int i = 0; i < hk.Length; i++)
        {
            if (hw > 0 && hk[hw - 1] == hk[i]) continue;
            hk[hw] = hk[i];
            hf[hw] = hf[i];
            hw++;
        }
        Array.Resize(ref hk, hw);
        Array.Resize(ref hf, hw);
        return new CascRoot(format, records, blocks, named, fdids, ck16, bitsArr, hk, hf);
    }

    private int IndexOf(uint fdid) => Array.BinarySearch(_fdids, fdid);

    public bool Contains(uint fdid) => IndexOf(fdid) >= 0;

    /// <summary>The content key of the chosen record of a FileDataID.</summary>
    public bool TryGetContentKey(uint fdid, out ReadOnlySpan<byte> ckey)
    {
        int i = IndexOf(fdid);
        ckey = i >= 0 ? _ckeys.AsSpan(i * 16, 16) : default;
        return i >= 0;
    }

    /// <summary>True when the chosen record of a FileDataID is enUS or all-locale.</summary>
    public bool IsEnUS(uint fdid)
    {
        int i = IndexOf(fdid);
        return i >= 0 && (_bits[i] & BitEnUS) != 0;
    }

    /// <summary>True when the chosen record carries the root's "encrypted" content flag.</summary>
    public bool HasEncryptedFlag(uint fdid)
    {
        int i = IndexOf(fdid);
        return i >= 0 && (_bits[i] & BitEncryptedFlag) != 0;
    }

    /// <summary>The FileDataID of a name hash, or null.</summary>
    public uint? FileDataIdOfHash(ulong hash)
    {
        int i = Array.BinarySearch(_hashes, hash);
        return i >= 0 ? _hashFdids[i] : null;
    }

    /// <summary>Every FileDataID in ascending order; with <paramref name="allLocales"/> false only those whose chosen
    /// record is enUS / all-locale.</summary>
    public IEnumerable<uint> FileDataIds(bool allLocales)
    {
        for (int i = 0; i < _fdids.Length; i++)
            if (allLocales || (_bits[i] & BitEnUS) != 0) yield return _fdids[i];
    }

    /// <summary>Every (name hash, FileDataID) pair.</summary>
    public IEnumerable<(ulong Hash, uint FileDataId)> NameHashes()
    {
        for (int i = 0; i < _hashes.Length; i++) yield return (_hashes[i], _hashFdids[i]);
    }

    /// <summary>The number of distinct FileDataIDs that have at least one name hash.</summary>
    public int CountNamedFileDataIds() => _hashFdids.Distinct().Count();
}
