using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WoWData.Core.Casc;

/// <summary>
/// The file-name hash of the WoW root file: Bob Jenkins' lookup3 <c>hashlittle2</c> (public domain algorithm,
/// written here from its published description) over the path in upper case with backslashes; the 64-bit value is
/// <c>(c &lt;&lt; 32) | b</c>.
/// </summary>
public static class CascNameHash
{
    /// <summary>The root name hash of a path (case-insensitive, '/' or '\').</summary>
    public static ulong Of(string path)
    {
        var norm = Normalize(path);
        int n = Encoding.UTF8.GetByteCount(norm);
        Span<byte> buf = n <= 512 ? stackalloc byte[n] : new byte[n];
        Encoding.UTF8.GetBytes(norm, buf);
        var (c, b) = HashLittle2(buf);
        return ((ulong)c << 32) | b;
    }

    /// <summary>The form that is hashed: upper case (invariant), backslashes.</summary>
    public static string Normalize(string path) => path.Replace('/', '\\').ToUpperInvariant();

    /// <summary>lookup3 <c>hashlittle2</c>: returns (c, b), the primary and secondary 32-bit values.</summary>
    public static (uint C, uint B) HashLittle2(ReadOnlySpan<byte> data, uint pc = 0, uint pb = 0)
    {
        int length = data.Length;
        uint a, b, c;
        a = b = c = 0xdeadbeef + (uint)length + pc;
        c += pb;
        int i = 0;
        while (length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            b += BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 8)..]);
            a -= c; a ^= BitOperations.RotateLeft(c, 4); c += b;
            b -= a; b ^= BitOperations.RotateLeft(a, 6); a += c;
            c -= b; c ^= BitOperations.RotateLeft(b, 8); b += a;
            a -= c; a ^= BitOperations.RotateLeft(c, 16); c += b;
            b -= a; b ^= BitOperations.RotateLeft(a, 19); a += c;
            c -= b; c ^= BitOperations.RotateLeft(b, 4); b += a;
            length -= 12;
            i += 12;
        }
        if (length == 0) return (c, b);
        Span<byte> tail = stackalloc byte[12];
        tail.Clear();
        data.Slice(i, length).CopyTo(tail);
        a += BinaryPrimitives.ReadUInt32LittleEndian(tail);
        b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        c ^= b; c -= BitOperations.RotateLeft(b, 14);
        a ^= c; a -= BitOperations.RotateLeft(c, 11);
        b ^= a; b -= BitOperations.RotateLeft(a, 25);
        c ^= b; c -= BitOperations.RotateLeft(b, 16);
        a ^= c; a -= BitOperations.RotateLeft(c, 4);
        b ^= a; b -= BitOperations.RotateLeft(a, 14);
        c ^= b; c -= BitOperations.RotateLeft(b, 24);
        return (c, b);
    }
}
