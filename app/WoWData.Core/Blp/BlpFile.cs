using System.Buffers.Binary;
using System.Text.Json.Serialization;
using WoWData.Core.Imaging;

namespace WoWData.Core.Blp;

/// <summary>
/// A BLP2 texture (port of wowdata/blp.py's BLP class): header fields, mip table, decoding to RGBA.
/// </summary>
/// <remarks>
/// Header (1172 = 0x494 bytes, read by BlpImage_SetData @005a83b0 in the decompile):
///   0x00 'BLP2'  0x04 uint32 type (1)
///   0x08 uint8 encoding (1 = palette, 2 = DXT, 3 = raw BGRA; the 1.12 client handles 1 and 2)
///   0x09 uint8 alphaDepth (0, 1, 4, 8)  0x0a uint8 alphaEncoding (DXT: 0 DXT1, 1 DXT3, 7 DXT5; palette: 8)
///   0x0b uint8 hasMips  0x0c uint32 width  0x10 uint32 height
///   0x14 uint32 mipOffsets[16]  0x54 uint32 mipSizes[16]  0x94 uint32 palette[256] (BGRA)
/// Palette mips: width*height index bytes, then the alpha plane (1 bit LSB-first / 4 bits low nibble first /
/// 8 bits per pixel). DXT mips: 4x4 blocks (a level smaller than 4x4 still uses whole blocks).
/// </remarks>
public sealed class BlpFile
{
    public const int HeaderSize = 0x494;
    public const int PaletteOffset = 0x94;
    public const byte EncPalette = 1, EncDxt = 2, EncRaw = 3;

    public byte[] Data { get; }
    public uint Type { get; }
    public byte Encoding { get; }
    public byte AlphaDepth { get; }
    public byte AlphaEncoding { get; }
    public byte HasMips { get; }
    public int Width { get; }
    public int Height { get; }
    public uint[] MipOffsets { get; }
    public uint[] MipSizes { get; }

    private BlpFile(byte[] data)
    {
        Data = data;
        Type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        Encoding = data[8];
        AlphaDepth = data[9];
        AlphaEncoding = data[10];
        HasMips = data[11];
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x0C));
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));
        // Python accepts any size here (validate() then reports it); only sizes that cannot be addressed are refused
        if (w > (1u << 24) || h > (1u << 24)) throw new BlpException($"BLP size {w}x{h} is not a usable image size");
        Width = (int)w;
        Height = (int)h;
        MipOffsets = new uint[16];
        MipSizes = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            MipOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x14 + 4 * i));
            MipSizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x54 + 4 * i));
        }
    }

    /// <summary>Parses a BLP2 file (the array is kept, not copied). Throws <see cref="BlpException"/> ("not a BLP2 file").</summary>
    public static BlpFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize || data[0] != (byte)'B' || data[1] != (byte)'L' || data[2] != (byte)'P' || data[3] != (byte)'2')
            throw new BlpException("not a BLP2 file");
        return new BlpFile(data);
    }

    // ------------------------------------------------------------------ description

    /// <summary>"dxt1"/"dxt3"/"dxt5" for DXT files with a known alphaEncoding, else null.</summary>
    public string? DxtKind => Encoding == EncDxt ? KindOf(AlphaEncoding) : null;

    internal static string? KindOf(int alphaEncoding) => alphaEncoding switch { 0 => "dxt1", 1 => "dxt3", 7 => "dxt5", _ => null };

    public string FormatName
    {
        get
        {
            if (Encoding == EncDxt)
            {
                string k = DxtKind ?? "?";
                if (k == "dxt1") return "DXT1" + (AlphaDepth != 0 ? " + 1-bit alpha" : " (no alpha)");
                return k.ToUpperInvariant() + $" ({AlphaDepth}-bit alpha)";
            }
            if (Encoding == EncPalette)
                return "palette 256" + (AlphaDepth != 0 ? $" + {AlphaDepth}-bit alpha" : " (no alpha)");
            if (Encoding == EncRaw) return "raw BGRA (not supported by the 1.12 client)";
            return $"unknown encoding {Encoding}";
        }
    }

    /// <summary>Number of levels of a full mip chain for this size (1x1 included).</summary>
    public static int LevelsFor(int width, int height)
    {
        int n = 1, w = width, h = height;
        while (w > 1 || h > 1) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); n++; }
        return n;
    }

    public static (int W, int H) LevelDims(int width, int height, int level) =>
        (Math.Max(1, width >> level), Math.Max(1, height >> level));

    /// <summary>Number of mip levels stored in the file.</summary>
    public int LevelCount
    {
        get
        {
            if (HasMips == 0) return 1;
            int n = 0, max = Math.Min(16, LevelsFor(Width, Height));
            for (int i = 0; i < max; i++)
            {
                if (MipOffsets[i] != 0 && MipSizes[i] != 0) n++;
                else break;
            }
            return Math.Max(1, n);
        }
    }

    public BlpInfo GetInfo()
    {
        int n = LevelCount;
        return new BlpInfo
        {
            Type = Type, Encoding = Encoding, AlphaDepth = AlphaDepth, AlphaEncoding = AlphaEncoding, HasMips = HasMips,
            Width = Width, Height = Height, Levels = n,
            MipOffsets = MipOffsets.Take(n).ToArray(), MipSizes = MipSizes.Take(n).ToArray(),
            Format = FormatName, FileSize = Data.Length,
        };
    }

    /// <summary>The bytes of a mip level as stored (offset/size from the header, clamped to the file like a Python slice).</summary>
    public ReadOnlySpan<byte> MipData(int level)
    {
        if (level < 0 || level >= 16) throw new ArgumentOutOfRangeException(nameof(level));
        return Slice(MipOffsets[level], MipSizes[level]);
    }

    private ReadOnlySpan<byte> Slice(long offset, long size)
    {
        if (offset >= Data.Length) return ReadOnlySpan<byte>.Empty;
        long end = Math.Min(Data.Length, offset + size);
        return Data.AsSpan((int)offset, (int)(end - offset));
    }

    /// <summary>The 1024-byte BGRA palette block of the header.</summary>
    public ReadOnlySpan<byte> PaletteRaw => Data.AsSpan(PaletteOffset, 1024);

    // ------------------------------------------------------------------ decoding

    /// <summary>RGBA image of one mip level (same pixels as Python BLP.decode: 565 expanded by bit replication).</summary>
    public RgbaImage Decode(int level = 0)
    {
        if (level < 0 || level >= 16) throw new ArgumentOutOfRangeException(nameof(level), "mip level must be 0..15");
        var (w, h) = LevelDims(Width, Height, level);
        var raw = MipData(level);
        if (Encoding == EncDxt)
        {
            string kind = DxtKind ?? throw new BlpException($"unknown DXT alpha encoding {AlphaEncoding}");
            int need = Dxt.BlockCount(w, h) * Dxt.BlockSize(kind);
            if (raw.Length < need)
            {
                // thin levels stored short by Blizzard's converter: like the client (which hands the pointer to the
                // GPU), read on into the bytes that follow; zeros past the end of the file
                var buf = new byte[need];
                Slice(MipOffsets[level], need).CopyTo(buf);
                raw = buf;
            }
            return Dxt.Decode(raw, w, h, kind, useAlpha: AlphaDepth > 0);
        }
        if (Encoding == EncPalette)
        {
            var (idx, alpha) = PalettePlanes(raw, w, h);
            var pal = PaletteRaw;
            var img = new RgbaImage(w, h);
            var px = img.Pixels;
            for (int i = 0; i < idx.Length; i++)
            {
                int p = idx[i] * 4;
                px[i * 4] = pal[p + 2]; px[i * 4 + 1] = pal[p + 1]; px[i * 4 + 2] = pal[p]; px[i * 4 + 3] = alpha[i];
            }
            return img;
        }
        if (Encoding == EncRaw)
        {
            long n = (long)w * h * 4;
            if (raw.Length < n) throw new BlpException($"level {level} ({w}x{h}) has {raw.Length} bytes, needs {n}");
            var img = new RgbaImage(w, h);
            var px = img.Pixels;
            for (int i = 0; i < n; i += 4)
            {
                px[i] = raw[i + 2]; px[i + 1] = raw[i + 1]; px[i + 2] = raw[i];
                px[i + 3] = AlphaDepth != 0 ? raw[i + 3] : (byte)255;
            }
            return img;
        }
        throw new BlpException($"unknown encoding {Encoding}");
    }

    /// <summary>Index plane and decoded alpha plane (one byte per pixel) of a palette mip.</summary>
    internal (byte[] Index, byte[] Alpha) PalettePlanes(ReadOnlySpan<byte> raw, int w, int h)
    {
        int n = w * h;
        if (raw.Length < n) throw new BlpException($"palette level {w}x{h} has {raw.Length} bytes, needs at least {n}");
        var idx = raw.Slice(0, n).ToArray();
        var ab = raw.Slice(n);
        var alpha = new byte[n];
        switch (AlphaDepth)
        {
            case 0:
                Array.Fill(alpha, (byte)255);
                break;
            case 1:
                if (ab.Length < (n + 7) / 8) throw new BlpException($"palette level {w}x{h}: the 1-bit alpha plane is cut short");
                for (int i = 0; i < n; i++) alpha[i] = ((ab[i >> 3] >> (i & 7)) & 1) != 0 ? (byte)255 : (byte)0;
                break;
            case 4:
                if (ab.Length < (n + 1) / 2) throw new BlpException($"palette level {w}x{h}: the 4-bit alpha plane is cut short");
                for (int i = 0; i < n; i++) alpha[i] = (byte)(((ab[i >> 1] >> (4 * (i & 1))) & 0xF) * 17);
                break;
            case 8:
                if (ab.Length < n) throw new BlpException($"palette level {w}x{h}: the 8-bit alpha plane is cut short");
                ab.Slice(0, n).CopyTo(alpha);
                break;
            default:
                throw new BlpException($"unsupported palette alpha depth {AlphaDepth}");
        }
        return (idx, alpha);
    }
}

/// <summary>The Python BLP.info() dict (same JSON keys).</summary>
public sealed class BlpInfo
{
    [JsonPropertyName("type")] public uint Type { get; init; }
    [JsonPropertyName("encoding")] public int Encoding { get; init; }
    [JsonPropertyName("alpha_depth")] public int AlphaDepth { get; init; }
    [JsonPropertyName("alpha_encoding")] public int AlphaEncoding { get; init; }
    [JsonPropertyName("has_mips")] public int HasMips { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("levels")] public int Levels { get; init; }
    [JsonPropertyName("mip_offsets")] public uint[] MipOffsets { get; init; } = Array.Empty<uint>();
    [JsonPropertyName("mip_sizes")] public uint[] MipSizes { get; init; } = Array.Empty<uint>();
    [JsonPropertyName("format")] public string Format { get; init; } = "";
    [JsonPropertyName("file_size")] public int FileSize { get; init; }
}
