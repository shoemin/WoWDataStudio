using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WoWData.Core.M2;

/// <summary>
/// The content of one M2Array (or of the header, a one-element record array): the raw bytes of its elements plus, for
/// records that contain arrays, the arrays of every element (<see cref="Sub"/>[element][array index]). The count and
/// offset fields inside <see cref="Bytes"/> are those of the original file; the writer recomputes them.
/// </summary>
public sealed class M2Data
{
    private static readonly M2Data[][] NoSub = System.Array.Empty<M2Data[]>();

    public M2Elem Elem { get; }
    public int Count { get; private set; }
    public byte[] Bytes { get; private set; }
    public M2Data[][] Sub { get; private set; }
    /// <summary>Where the block was in the original file (-1 for new or empty data).</summary>
    public int Offset { get; }

    public M2Data(M2Elem elem, int count, byte[] bytes, M2Data[][]? sub, int offset)
    {
        Elem = elem;
        Count = count;
        Bytes = bytes;
        Sub = sub ?? NoSub;
        Offset = offset;
    }

    public static M2Data Empty(M2Elem elem) => new(elem, 0, System.Array.Empty<byte>(), elem.Rec is { Arrays.Count: > 0 } ? System.Array.Empty<M2Data[]>() : null, -1);

    /// <summary>Replace the elements (used by edits; the writer lays the file out again).</summary>
    public void Set(int count, byte[] bytes, M2Data[][]? sub = null)
    {
        if (bytes.Length != count * Elem.Size) throw new ArgumentException($"{Elem}: {bytes.Length} bytes for {count} elements");
        Count = count;
        Bytes = bytes;
        Sub = sub ?? (Elem.Rec is { Arrays.Count: > 0 } ? new M2Data[count][] : NoSub);
    }

    public M2Rec Rec => Elem.Rec ?? throw new InvalidOperationException($"{Elem} is not a record array");

    // ---------------------------------------------------------------- element access
    public M2Data Child(int index, string array) => Sub[index][Rec.Array(array).Index];
    public M2Data Child(int index, int arrayIndex) => Sub[index][arrayIndex];

    private int At(int index, string field) => index * Elem.Size + Rec.Field(field).Offset;

    public ushort U16(int index, string field) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(At(index, field)));
    public short I16(int index, string field) => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(At(index, field)));
    public uint U32(int index, string field) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(At(index, field)));
    /// <summary>Component <paramref name="component"/> of a multi-value uint32 field ("2u4").</summary>
    public uint U32(int index, string field, int component) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(At(index, field) + component * 4));
    public void SetU32(int index, string field, uint v, int component = 0) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(At(index, field) + component * 4), v);
    public void SetI16(int index, string field, short v) => BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(At(index, field)), v);
    public void SetI32(int index, string field, int v) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(At(index, field)), v);
    public void SetVec3(int index, string field, Vector3 v)
    {
        var s = Bytes.AsSpan(At(index, field));
        BinaryPrimitives.WriteSingleLittleEndian(s, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(s[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(s[8..], v.Z);
    }
    public int I32(int index, string field) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(At(index, field)));
    public byte U8(int index, string field) => Bytes[At(index, field)];
    public sbyte I8(int index, string field) => unchecked((sbyte)Bytes[At(index, field)]);
    public float F32(int index, string field) => BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(At(index, field)));

    public Vector3 Vec3(int index, string field)
    {
        var s = Bytes.AsSpan(At(index, field));
        return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]));
    }

    public float[] Floats(int index, string field)
    {
        var f = Rec.Field(field);
        var a = new float[f.Size / 4];
        var s = Bytes.AsSpan(index * Elem.Size + f.Offset);
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(s[(i * 4)..]);
        return a;
    }

    public void SetU16(int index, string field, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(At(index, field)), v);
    public void SetF32(int index, string field, float v) => BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(At(index, field)), v);

    /// <summary>A track of element <paramref name="index"/>.</summary>
    public M2TrackView Track(int index, string name) => new(this, index, Rec.Track(name));

    public IEnumerable<M2TrackView> Tracks(int index)
    {
        foreach (var t in Rec.Tracks) yield return new M2TrackView(this, index, t);
    }

    // ---------------------------------------------------------------- primitive arrays
    public ushort[] AsU16()
    {
        var a = new ushort[Bytes.Length / 2];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(i * 2));
        return a;
    }

    public short[] AsI16()
    {
        var a = new short[Bytes.Length / 2];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(i * 2));
        return a;
    }

    public uint[] AsU32()
    {
        var a = new uint[Bytes.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(i * 4));
        return a;
    }

    public float[] AsF32()
    {
        var a = new float[Bytes.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(i * 4));
        return a;
    }

    public Vector3[] AsVec3()
    {
        var a = new Vector3[Count];
        for (int i = 0; i < Count; i++)
        {
            var s = Bytes.AsSpan(i * Elem.Size);
            a[i] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]));
        }
        return a;
    }

    public string AsString() => Encoding.Latin1.GetString(Bytes).TrimEnd('\0');

    public override string ToString() => $"<{Elem} x{Count}>";
}

/// <summary>One animation track of one record (a view into its <see cref="M2Data"/>).</summary>
public readonly struct M2TrackView
{
    private readonly M2Data _owner;
    private readonly int _index;
    public M2TrackField Field { get; }

    public M2TrackView(M2Data owner, int index, M2TrackField field)
    {
        _owner = owner;
        _index = index;
        Field = field;
    }

    public string Name => Field.Name;
    private int Base => _index * _owner.Elem.Size + Field.Offset;
    public ushort Interpolation => BinaryPrimitives.ReadUInt16LittleEndian(_owner.Bytes.AsSpan(Base));
    public short GlobalSequence => BinaryPrimitives.ReadInt16LittleEndian(_owner.Bytes.AsSpan(Base + 2));
    public M2Data Ranges => _owner.Sub[_index][Field.RangesIndex];
    public M2Data Timestamps => _owner.Sub[_index][Field.TimestampsIndex];
    public M2Data? Values => Field.ValuesIndex < 0 ? null : _owner.Sub[_index][Field.ValuesIndex];
    public int KeyCount => Timestamps.Count;

    /// <summary>{first, last} key index per sequence (inclusive for value tracks).</summary>
    public (uint First, uint Last)[] RangePairs()
    {
        var r = Ranges;
        var a = new (uint, uint)[r.Count];
        for (int i = 0; i < r.Count; i++)
            a[i] = (BinaryPrimitives.ReadUInt32LittleEndian(r.Bytes.AsSpan(i * 8)), BinaryPrimitives.ReadUInt32LittleEndian(r.Bytes.AsSpan(i * 8 + 4)));
        return a;
    }

    public override string ToString() => $"<Track {Name} interp={Interpolation} gseq={GlobalSequence} keys={KeyCount} ranges={Ranges.Count}>";
}
