using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.M2;

/// <summary>Minimal WMO root reader: the texture names of the MOTX chunk (chunk ids are stored reversed, "XTOM").</summary>
public static class WmoHeader
{
    public static List<string> Textures(byte[] data)
    {
        var list = new List<string>();
        int p = 0;
        while (p + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, p, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
            if (size < 0 || p + 8 + size > data.Length) break;
            if (id == "XTOM")
            {
                var block = data.AsSpan(p + 8, size);
                int s = 0;
                while (s < block.Length)
                {
                    int e = block[s..].IndexOf((byte)0);
                    if (e < 0) e = block.Length - s;
                    if (e > 0) list.Add(Encoding.Latin1.GetString(block.Slice(s, e)));
                    s += e + 1;
                }
                break;
            }
            p += 8 + size;
        }
        return list;
    }
}
