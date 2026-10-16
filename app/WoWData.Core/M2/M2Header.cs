using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.M2;

public sealed class M2Exception : Exception
{
    public M2Exception(string message) : base(message) { }
}

/// <summary>A texture entry of an M2: type 0 = a file named in the model; other types are replaceable slots
/// (11/12/13 = creature skins from CreatureDisplayInfo, 1 = character skin, 2 = item/cape, 6 = hair ...).</summary>
public sealed record M2Texture(int Type, uint Flags, string FileName);

/// <summary>
/// Minimal M2 (version 256/257, 1.12) header reader, enough for the index and the details panel (port of
/// wowdata/m2/header.py; offsets from M2Shared_FixupHeader @0071cdf0 in our decompile). The full reader/writer is
/// <see cref="M2File"/> / <see cref="M2Model"/>.
/// </summary>
public sealed class M2Header
{
    private static readonly Dictionary<string, int> ArrayOffsets = new()
    {
        ["global_sequences"] = 0x14, ["sequences"] = 0x1c, ["bones"] = 0x34, ["vertices"] = 0x44, ["views"] = 0x4c,
        ["textures"] = 0x5c, ["attachments"] = 0x104, ["events"] = 0x114, ["particles"] = 0x13c,
    };

    private readonly byte[] _data;
    public uint Version { get; }
    public string Name { get; }

    public M2Header(byte[] data)
    {
        if (data.Length < 0x150 || data[0] != 'M' || data[1] != 'D' || data[2] != '2' || data[3] != '0')
            throw new M2Exception("not an M2 (MD20) file");
        _data = data;
        Version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (Version is not (0x100 or 0x101)) throw new M2Exception($"M2 version 0x{Version:x} (the 1.12 client accepts 0x100/0x101)");
        Name = ReadString(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
    }

    private string ReadString(uint len, uint ofs)
    {
        if (len == 0 || ofs >= _data.Length) return "";
        int n = (int)Math.Min(len, (uint)(_data.Length - ofs));
        return Encoding.Latin1.GetString(_data, (int)ofs, n).TrimEnd('\0');
    }

    public (uint Count, uint Offset) Array(string field)
    {
        int o = ArrayOffsets[field];
        return (BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(o)), BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(o + 4)));
    }

    public Dictionary<string, uint> Counts() => ArrayOffsets.Keys.ToDictionary(k => k, k => Array(k).Count);

    public List<M2Texture> Textures()
    {
        var (n, ofs) = Array("textures");
        var list = new List<M2Texture>();
        for (uint i = 0; i < n; i++)
        {
            long p = ofs + i * 16L;
            if (p + 16 > _data.Length) throw new M2Exception("texture table outside the file");
            var s = _data.AsSpan((int)p);
            list.Add(new M2Texture((int)BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                ReadString(BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]))));
        }
        return list;
    }
}
