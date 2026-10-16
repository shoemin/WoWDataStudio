using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.Mpq;

/// <summary>
/// MPQ hashing and table encryption (the public Storm algorithm). Port of wowdata/crypto.py, which was checked
/// against the client's own SFile_HashString / SFile_Decrypt in our decompile.
/// </summary>
public static class StormCrypto
{
    public const uint HashTableOffset = 0;
    public const uint HashNameA = 1;
    public const uint HashNameB = 2;
    public const uint HashFileKey = 3;

    private static readonly uint[] CryptTable = BuildCryptTable();
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static readonly uint HashTableKey = HashString("(hash table)", HashFileKey);
    public static readonly uint BlockTableKey = HashString("(block table)", HashFileKey);

    private static uint[] BuildCryptTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (int i = 0; i < 0x100; i++)
        {
            int idx = i;
            for (int k = 0; k < 5; k++)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                uint hi = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                uint lo = seed & 0xFFFF;
                table[idx] = hi | lo;
                idx += 0x100;
            }
        }
        return table;
    }

    /// <summary>Archive path form used for hashing: backslashes, case kept.</summary>
    public static string Normalize(string name) => name.Replace('/', '\\');

    /// <summary>Storm string hash; case-insensitive, '/' equals '\'.</summary>
    public static uint HashString(string name, uint hashType)
    {
        uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
        uint baseIdx = hashType << 8;
        foreach (char c in name)
        {
            // latin-1 with '?' for anything outside it (Python: encode('latin-1', 'replace'))
            uint ch = c <= 0xFF ? c : (uint)'?';
            if (ch >= 'a' && ch <= 'z') ch -= 32;
            else if (ch == '/') ch = '\\';
            seed1 = CryptTable[baseIdx + ch] ^ (seed1 + seed2);
            seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    /// <summary>Decrypt in place (whole dwords only; a tail is left as is).</summary>
    public static void Decrypt(Span<byte> data, uint key)
    {
        int n = data.Length / 4;
        uint seed = 0xEEEEEEEE;
        for (int i = 0; i < n; i++)
        {
            seed += CryptTable[0x400 + (key & 0xFF)];
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4)) ^ (key + seed);
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(i * 4), v);
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = v + seed + (seed << 5) + 3;
        }
    }

    /// <summary>Encrypt in place; inverse of <see cref="Decrypt"/>.</summary>
    public static void Encrypt(Span<byte> data, uint key)
    {
        int n = data.Length / 4;
        uint seed = 0xEEEEEEEE;
        for (int i = 0; i < n; i++)
        {
            seed += CryptTable[0x400 + (key & 0xFF)];
            uint plain = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(i * 4), plain ^ (key + seed));
            key = ((~key << 0x15) + 0x11111111) | (key >> 0x0B);
            seed = plain + seed + (seed << 5) + 3;
        }
    }

    /// <summary>Encryption key of a file: hash of its base name; the FIX_KEY variant mixes position and size.</summary>
    public static uint FileKey(string name, uint blockPos = 0, uint fileSize = 0, bool fixKey = false)
    {
        string n = Normalize(name);
        int slash = n.LastIndexOf('\\');
        uint key = HashString(slash >= 0 ? n[(slash + 1)..] : n, HashFileKey);
        if (fixKey) key = (key + blockPos) ^ fileSize;
        return key;
    }

    internal static Encoding Latin1Encoding => Latin1;
}
