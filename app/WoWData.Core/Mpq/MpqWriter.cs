using System.Buffers.Binary;
using System.IO.Compression;

using System.Security.Cryptography;
using System.Text;

namespace WoWData.Core.Mpq;

public sealed class MpqWriteException : Exception
{
    public MpqWriteException(string message) : base(message) { }
}

public sealed record MpqBuildSummary(string Path, int Files, int Blocks, int HashTableSize, long Size);

/// <summary>
/// Writer for patch archives (Data\patch-3.MPQ). Port of wowdata/mpqwrite.py; the output mirrors Blizzard's own
/// 1.12 patch archive patch-2.MPQ field by field:
///   * format version 1 header (44 bytes, extended fields zero), sector size 4096 (shift 3);
///   * every file sector-compressed with zlib (mask 0x02), a sector stored raw when zlib does not help, a whole
///     file stored plain when compression does not help; block flags EXISTS | COMPRESS;
///   * hash and block tables encrypted with the standard keys; locale 0, platform 0;
///   * (listfile) (CRLF separated, sorted case-insensitively) and (attributes) (version 100, flags 7 = CRC32 +
///     FILETIME + MD5, FILETIME zero, the (attributes) entry's own CRC/MD5 zero).
/// No file encryption, no PKWARE implode. Only ever creates new files; refuses the client's stock archive names.
/// </summary>
public static class MpqWriter
{
    public const int SectorShift = 3;
    public const int SectorSize = 512 << SectorShift;
    public const int HeaderSizeV1 = 44;

    /// <summary>zlib stream of <paramref name="raw"/> at the best compression (Python: zlib.compress(raw, 9)).</summary>
    public static byte[] ZlibCompress(ReadOnlySpan<byte> raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    private static (byte[] Body, uint Flags) CompressFile(byte[] data)
    {
        if (data.Length == 0) return (Array.Empty<byte>(), MpqArchive.FlagExists);
        int nsect = (data.Length + SectorSize - 1) / SectorSize;
        var sectors = new List<byte[]>(nsect);
        for (int i = 0; i < nsect; i++)
        {
            var raw = data.AsSpan(i * SectorSize, Math.Min(SectorSize, data.Length - i * SectorSize));
            var z = ZlibCompress(raw);
            if (z.Length + 1 < raw.Length)
            {
                var s = new byte[z.Length + 1];
                s[0] = MpqArchive.CompZlib;
                z.CopyTo(s, 1);
                sectors.Add(s);
            }
            else sectors.Add(raw.ToArray());
        }
        int tableSize = (nsect + 1) * 4;
        long total = tableSize + sectors.Sum(s => (long)s.Length);
        if (total >= data.Length) return (data, MpqArchive.FlagExists);   // incompressible: store the file plain
        var body = new byte[total];
        int off = tableSize;
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)off);
        for (int i = 0; i < nsect; i++)
        {
            sectors[i].CopyTo(body, off);
            off += sectors[i].Length;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan((i + 1) * 4), (uint)off);
        }
        return (body, MpqArchive.FlagExists | MpqArchive.FlagCompress);
    }

    public static int HashTableSizeFor(int n)
    {
        int size = 16;
        while (size < n * 2) size *= 2;
        return size;
    }

    /// <summary>Upper-case sort key matching Python's str.upper() for the names we write (ASCII paths).</summary>
    private static string Upper(string s) => s.ToUpperInvariant();

    /// <summary>Write a new MPQ with <paramref name="files"/> (archive path -> bytes). Paths use backslashes (forward
    /// slashes are converted). The output must not exist unless <paramref name="overwrite"/>; it may never carry a stock
    /// archive name.</summary>
    public static MpqBuildSummary Build(IReadOnlyDictionary<string, byte[]> files, string outPath, bool attributes = true, bool overwrite = false)
    {
        var baseName = System.IO.Path.GetFileName(outPath);
        if (ClientData.StockArchives.Any(n => string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase)))
            throw new MpqWriteException($"refusing to write {baseName}: that is a stock client archive name");
        if (File.Exists(outPath) && !overwrite)
            throw new MpqWriteException($"{outPath} exists (pass overwrite to replace a file we built)");
        var names = new List<(string Name, byte[] Data)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in files)
        {
            var nn = StormCrypto.Normalize(kv.Key).TrimStart('\\');
            if (!seen.Add(Upper(nn))) throw new MpqWriteException($"duplicate path (case-insensitive): {nn}");
            if (nn.StartsWith('(')) throw new MpqWriteException($"reserved name: {nn}");
            names.Add((nn, kv.Value));
        }
        names.Sort((a, b) => string.CompareOrdinal(Upper(a.Name), Upper(b.Name)));

        var listfile = Encoding.UTF8.GetBytes(string.Concat(names.Select(n => n.Name + "\r\n")));
        var entries = new List<(string Name, byte[]? Data)>(names.Select(n => (n.Name, (byte[]?)n.Data))) { ("(listfile)", listfile) };
        if (attributes) entries.Add(("(attributes)", null));
        int nblocks = entries.Count;

        var blocks = new List<MpqBlock>();
        var tmp = outPath + ".tmp";
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        long pos;
        int hsize;
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(new byte[HeaderSizeV1]);
            pos = HeaderSizeV1;
            var crcs = new List<uint>();
            var md5s = new List<byte[]>();
            foreach (var (name, d0) in entries)
            {
                byte[] data;
                if (name == "(attributes)")
                {
                    crcs.Add(0);
                    md5s.Add(new byte[16]);
                    data = new byte[8 + nblocks * 4 + nblocks * 8 + nblocks * 16];
                    BinaryPrimitives.WriteUInt32LittleEndian(data, 100);
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 7);
                    for (int i = 0; i < nblocks; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + i * 4), crcs[i]);
                    int md5Off = 8 + nblocks * 12;
                    for (int i = 0; i < nblocks; i++) md5s[i].CopyTo(data, md5Off + i * 16);
                }
                else
                {
                    data = d0!;
                    crcs.Add(Crc32.Compute(data));
                    md5s.Add(MD5.HashData(data));
                }
                var (body, flags) = CompressFile(data);
                f.Write(body);
                blocks.Add(new MpqBlock(pos, (uint)body.Length, (uint)data.Length, flags));
                pos += body.Length;
            }
            hsize = HashTableSizeFor(nblocks);
            var table = new MpqArchive.HashEntry[hsize];
            Array.Fill(table, new MpqArchive.HashEntry(0xFFFFFFFF, 0xFFFFFFFF, 0xFFFF, 0xFFFF, 0xFFFFFFFF));
            for (int bi = 0; bi < entries.Count; bi++)
            {
                var name = entries[bi].Name;
                int i = (int)(StormCrypto.HashString(name, StormCrypto.HashTableOffset) & (uint)(hsize - 1));
                while (table[i].BlockIndex != 0xFFFFFFFF) i = (i + 1) & (hsize - 1);
                table[i] = new MpqArchive.HashEntry(StormCrypto.HashString(name, StormCrypto.HashNameA), StormCrypto.HashString(name, StormCrypto.HashNameB), 0, 0, (uint)bi);
            }
            var hraw = new byte[hsize * 16];
            for (int i = 0; i < hsize; i++)
            {
                var s = hraw.AsSpan(i * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(s, table[i].NameA);
                BinaryPrimitives.WriteUInt32LittleEndian(s[4..], table[i].NameB);
                BinaryPrimitives.WriteUInt16LittleEndian(s[8..], table[i].Locale);
                BinaryPrimitives.WriteUInt16LittleEndian(s[10..], table[i].Platform);
                BinaryPrimitives.WriteUInt32LittleEndian(s[12..], table[i].BlockIndex);
            }
            var braw = new byte[blocks.Count * 16];
            for (int i = 0; i < blocks.Count; i++)
            {
                var s = braw.AsSpan(i * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)blocks[i].Position);
                BinaryPrimitives.WriteUInt32LittleEndian(s[4..], blocks[i].CompressedSize);
                BinaryPrimitives.WriteUInt32LittleEndian(s[8..], blocks[i].FileSize);
                BinaryPrimitives.WriteUInt32LittleEndian(s[12..], blocks[i].Flags);
            }
            long hashPos = pos;
            StormCrypto.Encrypt(hraw, StormCrypto.HashTableKey);
            f.Write(hraw);
            pos += hraw.Length;
            long blockPos = pos;
            StormCrypto.Encrypt(braw, StormCrypto.BlockTableKey);
            f.Write(braw);
            pos += braw.Length;
            var header = new byte[HeaderSizeV1];
            header[0] = (byte)'M'; header[1] = (byte)'P'; header[2] = (byte)'Q'; header[3] = 0x1A;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), HeaderSizeV1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)pos);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), SectorShift);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)hashPos);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)blockPos);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)hsize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)nblocks);
            f.Seek(0, SeekOrigin.Begin);
            f.Write(header);
        }
        File.Move(tmp, outPath, overwrite: true);
        return new MpqBuildSummary(outPath, names.Count, nblocks, hsize, pos);
    }

    /// <summary>Read the archive back with our reader and compare every file. Returns the problems (empty = OK).</summary>
    public static List<string> Verify(string path, IReadOnlyDictionary<string, byte[]> files)
    {
        var problems = new List<string>();
        using var a = new MpqArchive(path);
        if (a.FormatVersion != 1 || a.HeaderSize != HeaderSizeV1) problems.Add($"header: version {a.FormatVersion} size {a.HeaderSize}");
        var listed = new HashSet<string>(a.Listfile().Select(n => n.ToUpperInvariant()));
        foreach (var kv in files)
        {
            var nn = StormCrypto.Normalize(kv.Key).TrimStart('\\');
            if (a.StormLookup(nn) < 0) problems.Add($"{nn}: not found by the client-style hash probe");
            var got = a.Read(nn);
            if (got == null || !got.AsSpan().SequenceEqual(kv.Value)) problems.Add($"{nn}: content differs after read-back");
            if (!listed.Contains(nn.ToUpperInvariant())) problems.Add($"{nn}: missing from (listfile)");
        }
        var attr = a.Read("(attributes)");
        if (attr != null && attr.Length >= 8)
        {
            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(attr), fl = BinaryPrimitives.ReadUInt32LittleEndian(attr.AsSpan(4));
            if (ver != 100 || fl != 7) problems.Add($"(attributes) header ({ver}, {fl})");
        }
        return problems;
    }
}
