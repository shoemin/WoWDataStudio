namespace WoWData.Core.Imaging;

/// <summary>An 8-bit RGBA image, rows top to bottom, 4 bytes per pixel (R, G, B, A).</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "image size must be positive");
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("pixel buffer size does not match the image size");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public int Offset(int x, int y) => (y * Width + x) * 4;

    /// <summary>True when every pixel has alpha 255.</summary>
    public bool IsOpaque()
    {
        for (int i = 3; i < Pixels.Length; i += 4) if (Pixels[i] != 255) return false;
        return true;
    }

    public bool SameContent(RgbaImage other) =>
        other.Width == Width && other.Height == Height && Pixels.AsSpan().SequenceEqual(other.Pixels);
}
