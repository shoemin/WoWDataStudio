using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WoWData.Core.Casc;

/// <summary>
/// BLTE, the encoded form of every file in a CASC archive (written from the public format description):
/// <c>"BLTE"</c>, a big-endian header size, then either one chunk (header size 0) or a chunk table
/// (flags byte 0x0F, 24-bit chunk count, per chunk: encoded size, decoded size, MD5 of the encoded chunk).
/// Each chunk starts with a mode byte: <c>N</c> raw, <c>Z</c> zlib, <c>F</c> a nested BLTE frame, <c>E</c> encrypted.
/// Encrypted chunks are never decrypted. Chunk checksums are always checked; a mismatch throws.
/// </summary>
public static class Blte
{
    /// <summary>One chunk of a frame: where its encoded bytes are, its decoded size (-1 when the frame has no chunk
    /// table) and its mode byte.</summary>
    public readonly record struct Chunk(int Offset, int EncodedSize, long DecodedSize, char Mode, byte[]? Md5);

    public static bool IsBlte(ReadOnlySpan<byte> data) => data.Length >= 8 && data[0] == 'B' && data[1] == 'L' && data[2] == 'T' && data[3] == 'E';

    /// <summary>The chunks of a frame (no decoding, no checksum check).</summary>
    public static List<Chunk> Chunks(ReadOnlySpan<byte> data)
    {
        if (!IsBlte(data)) throw new InvalidDataException("not a BLTE frame");
        uint headerSize = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var list = new List<Chunk>();
        if (headerSize == 0)
        {
            if (data.Length < 9) throw new InvalidDataException("BLTE: empty single chunk");
            list.Add(new Chunk(8, data.Length - 8, -1, (char)data[8], null));
            return list;
        }
        if (headerSize < 12 || headerSize > data.Length) throw new InvalidDataException($"BLTE: bad header size {headerSize}");
        byte flags = data[8];
        if (flags != 0x0F) throw new InvalidDataException($"BLTE: unsupported chunk table flags 0x{flags:X2}");
        int count = (data[9] << 16) | (data[10] << 8) | data[11];
        if (count == 0 || 12 + 24L * count > headerSize) throw new InvalidDataException($"BLTE: bad chunk count {count}");
        long pos = headerSize;
        for (int i = 0; i < count; i++)
        {
            var e = data.Slice(12 + 24 * i, 24);
            uint enc = BinaryPrimitives.ReadUInt32BigEndian(e);
            uint dec = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            if (enc == 0 || pos + enc > data.Length) throw new InvalidDataException($"BLTE: chunk {i} runs past the end of the data");
            list.Add(new Chunk((int)pos, (int)enc, dec, (char)data[(int)pos], e.Slice(8, 16).ToArray()));
            pos += enc;
        }
        return list;
    }

    /// <summary>
    /// Decodes a whole frame. An encrypted chunk throws <see cref="CascEncryptedException"/>, unless
    /// <paramref name="zeroFillEncrypted"/> is set: then it becomes zero bytes of its decoded size (possible only in a
    /// frame with a chunk table) and <paramref name="encryptedBytes"/> counts them.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, bool zeroFillEncrypted, out long encryptedBytes)
    {
        encryptedBytes = 0;
        var chunks = Chunks(data);
        if (chunks[0].DecodedSize < 0)
        {
            var c = chunks[0];
            return DecodeUnsized(data.Slice(c.Offset, c.EncodedSize), zeroFillEncrypted, ref encryptedBytes);
        }
        long total = 0;
        foreach (var c in chunks) total += c.DecodedSize;
        if (total > Array.MaxLength) throw new InvalidDataException("BLTE: decoded size too large");
        var output = new byte[total];
        long at = 0;
        Span<byte> md5 = stackalloc byte[16];
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var enc = data.Slice(c.Offset, c.EncodedSize);
            MD5.HashData(enc, md5);
            if (!md5.SequenceEqual(c.Md5)) throw new InvalidDataException($"BLTE: checksum mismatch in chunk {i} of {chunks.Count}");
            DecodeChunk(enc, output.AsSpan((int)at, (int)c.DecodedSize), zeroFillEncrypted, ref encryptedBytes, i);
            at += c.DecodedSize;
        }
        return output;
    }

    /// <summary>Decodes a whole frame; any encrypted chunk throws <see cref="CascEncryptedException"/>.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data) => Decode(data, false, out _);

    private static void DecodeChunk(ReadOnlySpan<byte> enc, Span<byte> dest, bool zeroFill, ref long encryptedBytes, int index)
    {
        var body = enc[1..];
        switch ((char)enc[0])
        {
            case 'N':
                if (body.Length != dest.Length) throw new InvalidDataException($"BLTE: raw chunk {index} has {body.Length} bytes, the table says {dest.Length}");
                body.CopyTo(dest);
                break;
            case 'Z':
                Inflate(body, dest, index);
                break;
            case 'F':
            {
                var inner = Decode(body, zeroFill, out long innerEnc);
                encryptedBytes += innerEnc;
                if (inner.Length != dest.Length) throw new InvalidDataException($"BLTE: nested chunk {index} decodes to {inner.Length} bytes, the table says {dest.Length}");
                inner.CopyTo(dest);
                break;
            }
            case 'E':
                if (!zeroFill) throw new CascEncryptedException($"BLTE chunk {index} is encrypted (key {KeyName(enc) ?? "?"})", KeyName(enc));
                dest.Clear();
                encryptedBytes += dest.Length;
                break;
            default:
                throw new InvalidDataException($"BLTE: unsupported chunk mode '{(char)enc[0]}' (0x{enc[0]:X2})");
        }
    }

    private static byte[] DecodeUnsized(ReadOnlySpan<byte> enc, bool zeroFill, ref long encryptedBytes)
    {
        var body = enc[1..];
        switch ((char)enc[0])
        {
            case 'N':
                return body.ToArray();
            case 'Z':
                return InflateUnsized(body);
            case 'F':
            {
                var r = Decode(body, zeroFill, out long innerEnc);
                encryptedBytes += innerEnc;
                return r;
            }
            case 'E':
                // without a chunk table the decoded size is unknown, so even zero-filling is impossible
                throw new CascEncryptedException($"BLTE frame is encrypted (key {KeyName(enc) ?? "?"})", KeyName(enc));
            default:
                throw new InvalidDataException($"BLTE: unsupported chunk mode '{(char)enc[0]}' (0x{enc[0]:X2})");
        }
    }

    /// <summary>The key name of an encrypted chunk (<c>E</c>, key-name length, key name, IV length, IV, type, payload),
    /// as 16 hex digits in the usual (byte-reversed) form; null when the chunk is too short.</summary>
    public static string? KeyName(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 2 || chunk[0] != 'E') return null;
        int len = chunk[1];
        if (len == 0 || chunk.Length < 2 + len) return null;
        var k = chunk.Slice(2, len).ToArray();
        Array.Reverse(k);
        return Convert.ToHexString(k);
    }

    private static unsafe void Inflate(ReadOnlySpan<byte> src, Span<byte> dest, int index)
    {
        fixed (byte* p = src)
        {
            using var ms = new UnmanagedMemoryStream(p, src.Length);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            try
            {
                z.ReadExactly(dest);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"BLTE: zlib chunk {index} decodes to fewer bytes than the table says ({dest.Length})");
            }
            Span<byte> one = stackalloc byte[1];
            if (z.Read(one) != 0) throw new InvalidDataException($"BLTE: zlib chunk {index} decodes to more bytes than the table says ({dest.Length})");
        }
    }

    private static unsafe byte[] InflateUnsized(ReadOnlySpan<byte> src)
    {
        fixed (byte* p = src)
        {
            using var ms = new UnmanagedMemoryStream(p, src.Length);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var output = new MemoryStream(src.Length * 4);
            z.CopyTo(output);
            return output.ToArray();
        }
    }
}
