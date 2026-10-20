namespace WoWData.Core.Mpq;

/// <summary>
/// bzip2 decompression (MPQ sector compression mask 0x10), written for this project from the public description of the
/// bzip2 format (Julian Seward's bzip2 1.0 file format: "BZh" header, Huffman-coded MTF/RLE2 symbols, Burrows-Wheeler
/// transform, RLE1). No bzip2 or other library code was used. Every block's CRC and the stream CRC are checked, so a
/// decoding mistake raises <see cref="MpqException"/> instead of returning wrong bytes. The 1.12 archives never use it;
/// a few WotLK 3.3.5a files do (8 files in lichking.MPQ).
/// </summary>
public static class Bzip2
{
    private const int MaxGroups = 6, GroupSize = 50, MaxCodeLen = 20;
    private const ulong BlockMagic = 0x314159265359, EndMagic = 0x177245385090;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            t[i] = c;
        }
        return t;
    }

    private sealed class BitReader
    {
        private readonly byte[] _d;
        private int _pos;
        private ulong _buf;
        private int _bits;

        public BitReader(byte[] d, int start) { _d = d; _pos = start; }

        public uint Bits(int n)
        {
            while (_bits < n)
            {
                if (_pos >= _d.Length) throw new MpqException("bzip2: data ends inside a block");
                _buf = (_buf << 8) | _d[_pos++];
                _bits += 8;
            }
            _bits -= n;
            return (uint)(_buf >> _bits) & (uint)((1UL << n) - 1);
        }

        public bool Bit() => Bits(1) != 0;
        public ulong Bits48() => ((ulong)Bits(24) << 24) | Bits(24);
    }

    /// <summary>Decompresses a whole bzip2 stream starting at <paramref name="start"/>; at most <paramref name="expect"/> bytes are returned.</summary>
    public static byte[] Decompress(byte[] data, int start, int expect)
    {
        if (data.Length - start < 4 || data[start] != (byte)'B' || data[start + 1] != (byte)'Z' || data[start + 2] != (byte)'h'
            || data[start + 3] < (byte)'1' || data[start + 3] > (byte)'9')
            throw new MpqException("bzip2: no BZh header");
        int maxBlock = (data[start + 3] - '0') * 100000;
        var br = new BitReader(data, start + 4);
        var output = new byte[expect];
        int outPos = 0;
        uint combined = 0;
        var tt = new int[maxBlock];
        while (true)
        {
            ulong magic = br.Bits48();
            if (magic == EndMagic)
            {
                uint streamCrc = br.Bits(32);
                if (streamCrc != combined) throw new MpqException("bzip2: stream CRC mismatch");
                break;
            }
            if (magic != BlockMagic) throw new MpqException("bzip2: bad block magic");
            uint blockCrc = br.Bits(32);
            uint crc = DecodeBlock(br, tt, maxBlock, output, ref outPos);
            if (crc != blockCrc) throw new MpqException("bzip2: block CRC mismatch");
            combined = ((combined << 1) | (combined >> 31)) ^ blockCrc;
        }
        return outPos == expect ? output : output.AsSpan(0, outPos).ToArray();
    }

    private static uint DecodeBlock(BitReader br, int[] tt, int maxBlock, byte[] output, ref int outPos)
    {
        if (br.Bit()) throw new MpqException("bzip2: randomised blocks are not supported");
        int origPtr = (int)br.Bits(24);

        // symbol map: which byte values occur in the block
        var seqToUnseq = new byte[256];
        int inUse = 0;
        uint groupsUsed = br.Bits(16);
        for (int g = 0; g < 16; g++)
            if ((groupsUsed & (0x8000u >> g)) != 0)
            {
                uint bits = br.Bits(16);
                for (int j = 0; j < 16; j++)
                    if ((bits & (0x8000u >> j)) != 0) seqToUnseq[inUse++] = (byte)(g * 16 + j);
            }
        if (inUse == 0) throw new MpqException("bzip2: block uses no symbols");
        int alphaSize = inUse + 2;

        int nGroups = (int)br.Bits(3);
        if (nGroups < 2 || nGroups > MaxGroups) throw new MpqException("bzip2: bad number of Huffman tables");
        int nSelectors = (int)br.Bits(15);
        if (nSelectors < 1) throw new MpqException("bzip2: no selectors");
        var selectors = new byte[nSelectors];
        var mtfGroups = new byte[MaxGroups];
        for (int i = 0; i < nGroups; i++) mtfGroups[i] = (byte)i;
        for (int i = 0; i < nSelectors; i++)
        {
            int j = 0;
            while (br.Bit()) if (++j >= nGroups) throw new MpqException("bzip2: bad selector");
            byte v = mtfGroups[j];
            for (; j > 0; j--) mtfGroups[j] = mtfGroups[j - 1];
            mtfGroups[0] = v;
            selectors[i] = v;
        }

        // code lengths (delta coded), then canonical decoding tables per group
        var tables = new HuffTable[nGroups];
        for (int t = 0; t < nGroups; t++)
        {
            var len = new int[alphaSize];
            int cur = (int)br.Bits(5);
            for (int s = 0; s < alphaSize; s++)
            {
                while (true)
                {
                    if (cur < 1 || cur > MaxCodeLen) throw new MpqException("bzip2: bad code length");
                    if (!br.Bit()) break;
                    cur += br.Bit() ? -1 : 1;
                }
                len[s] = cur;
            }
            tables[t] = new HuffTable(len);
        }

        // Huffman symbols -> RLE2/MTF -> byte counts and the BWT vector
        var mtf = new byte[256];
        for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
        var counts = new int[256];
        int n = 0, groupLeft = 0, selIdx = 0, runLen = 0, runBit = 1;
        HuffTable table = tables[0];
        int eob = inUse + 1;
        while (true)
        {
            if (groupLeft == 0)
            {
                if (selIdx >= nSelectors) throw new MpqException("bzip2: selectors exhausted");
                table = tables[selectors[selIdx++]];
                groupLeft = GroupSize;
            }
            groupLeft--;
            int sym = table.Decode(br);
            if (sym <= 1)
            {
                runLen += (sym + 1) * runBit;
                runBit <<= 1;
                if (runLen > maxBlock) throw new MpqException("bzip2: run exceeds the block size");
                continue;
            }
            if (runLen > 0)
            {
                byte b = seqToUnseq[mtf[0]];
                if (n + runLen > maxBlock) throw new MpqException("bzip2: block too long");
                counts[b] += runLen;
                for (; runLen > 0; runLen--) tt[n++] = b;
                runBit = 1;
            }
            if (sym == eob) break;
            int idx = sym - 1;
            byte m = mtf[idx];
            for (int j = idx; j > 0; j--) mtf[j] = mtf[j - 1];
            mtf[0] = m;
            byte val = seqToUnseq[m];
            if (n >= maxBlock) throw new MpqException("bzip2: block too long");
            counts[val]++;
            tt[n++] = val;
        }
        if (origPtr >= n) throw new MpqException("bzip2: bad origin pointer");

        // inverse BWT: tt[i] low byte = symbol, high bits = the next position
        var cum = new int[256];
        for (int i = 0, sum = 0; i < 256; i++) { cum[i] = sum; sum += counts[i]; }
        for (int i = 0; i < n; i++)
        {
            int b = tt[i] & 0xFF;
            tt[cum[b]++] |= i << 8;
        }

        // walk the vector and undo RLE1 (4 equal bytes are followed by a repeat count)
        uint crc = 0xFFFFFFFF;
        int pos = tt[origPtr] >> 8;
        int last = -1, same = 0;
        for (int k = 0; k < n; k++)
        {
            int e = tt[pos];
            byte b = (byte)e;
            pos = e >> 8;
            if (same == 4)
            {
                for (int r = 0; r < b; r++) Put(output, ref outPos, (byte)last, ref crc);
                same = 0;
                last = -1;
                continue;
            }
            if (b == last) same++; else { same = 1; last = b; }
            Put(output, ref outPos, b, ref crc);
        }
        return ~crc;
    }

    private static void Put(byte[] output, ref int outPos, byte b, ref uint crc)
    {
        crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b];
        if (outPos >= output.Length) throw new MpqException("bzip2: output is larger than the file size");
        output[outPos++] = b;
    }

    /// <summary>Canonical Huffman decoding (bzip2 assigns codes in increasing length, then symbol order).</summary>
    private sealed class HuffTable
    {
        private readonly int _minLen, _maxLen;
        private readonly int[] _limit = new int[MaxCodeLen + 2], _base = new int[MaxCodeLen + 2];
        private readonly int[] _perm;

        public HuffTable(int[] len)
        {
            _minLen = len.Min();
            _maxLen = len.Max();
            _perm = new int[len.Length];
            int p = 0;
            for (int l = _minLen; l <= _maxLen; l++)
                for (int s = 0; s < len.Length; s++)
                    if (len[s] == l) _perm[p++] = s;
            int code = 0, idx = 0;
            for (int l = _minLen; l <= _maxLen; l++)
            {
                int count = 0;
                foreach (var x in len) if (x == l) count++;
                _base[l] = idx - code;      // symbol index = _base[l] + code
                code += count;
                idx += count;
                _limit[l] = code - 1;       // largest code of this length
                code <<= 1;
            }
        }

        public int Decode(BitReader br)
        {
            int code = (int)br.Bits(_minLen);
            for (int l = _minLen; l <= _maxLen; l++)
            {
                if (code <= _limit[l]) return _perm[_base[l] + code];
                code = (code << 1) | (int)br.Bits(1);
            }
            throw new MpqException("bzip2: invalid Huffman code");
        }
    }
}
