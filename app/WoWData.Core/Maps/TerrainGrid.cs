using System.Numerics;

namespace WoWData.Core.Maps;

/// <summary>
/// The heights of one tile as one grid, the way an editor sees it: 129 x 129 outer vertices (16 chunks x 8 quads + 1,
/// rows north to south, columns west to east) and 128 x 128 inner vertices (one in the middle of every quad). In the
/// file every chunk stores its own 9 x 9 outer vertices, so the vertices on chunk borders are stored twice (or four times
/// at chunk corners); the game needs those copies to be equal (they are, on every stock tile). A vertex id names one
/// grid vertex: outer (row, col) = row * 129 + col, inner (row, col) = 16641 + row * 128 + col (the Blender package
/// stores id + 1, so 0 means "a vertex Blender added").
/// </summary>
public sealed class TerrainGrid
{
    public const int OuterSize = 129;
    public const int InnerSize = 128;
    public const int OuterCount = OuterSize * OuterSize;   // 16641
    public const int InnerCount = InnerSize * InnerSize;   // 16384
    public const int Count = OuterCount + InnerCount;       // 33025

    /// <summary>Absolute heights (yards), NaN where the tile has no chunk.</summary>
    public float[] Outer { get; } = new float[OuterCount];
    public float[] Inner { get; } = new float[InnerCount];
    /// <summary>Quad (row * 128 + col) is a hole (left out of the ground).</summary>
    public bool[] Holes { get; } = new bool[InnerCount];

    public float O(int row, int col) => Outer[row * OuterSize + col];
    public float I(int row, int col) => Inner[row * InnerSize + col];

    public static int OuterId(int row, int col) => row * OuterSize + col;
    public static int InnerId(int row, int col) => OuterCount + row * InnerSize + col;
    public static bool IsInner(int id) => id >= OuterCount;

    /// <summary>(row, col) of a vertex id in its grid (outer or inner).</summary>
    public static (int Row, int Col) RowCol(int id) =>
        id < OuterCount ? (id / OuterSize, id % OuterSize) : ((id - OuterCount) / InnerSize, (id - OuterCount) % InnerSize);

    /// <summary>Position of a vertex inside the tile in grid units (outer vertices on whole numbers, inner ones on
    /// halves): x = column (east), y = row (south).</summary>
    public static Vector2 GridPosition(int id)
    {
        var (r, c) = RowCol(id);
        return IsInner(id) ? new Vector2(c + 0.5f, r + 0.5f) : new Vector2(c, r);
    }

    /// <summary>The vertex id of value k (0..144) of chunk (index = cy * 16 + cx).</summary>
    public static int IdOf(int chunk, int k)
    {
        int cx = chunk % 16, cy = chunk / 16, row = k / 17, rem = k % 17;
        return rem < 9 ? OuterId(cy * 8 + row, cx * 8 + rem) : InnerId(cy * 8 + row, cx * 8 + rem - 9);
    }

    /// <summary>Every place a vertex is stored in the file: (chunk, value index 0..144). Outer vertices on chunk borders
    /// have 2 copies, on chunk corners 4.</summary>
    public static IEnumerable<(int Chunk, int K)> Copies(int id)
    {
        var (r, c) = RowCol(id);
        if (IsInner(id))
        {
            yield return ((r / 8) * 16 + c / 8, (r % 8) * 17 + 9 + c % 8);
            yield break;
        }
        foreach (var cy in ChunksOf(r))
            foreach (var cx in ChunksOf(c))
                yield return (cy * 16 + cx, (r - cy * 8) * 17 + (c - cx * 8));
    }

    private static IEnumerable<int> ChunksOf(int line)
    {
        if (line % 8 == 0 && line > 0) yield return line / 8 - 1;
        if (line < 128) yield return line / 8;
    }

    /// <summary>The chunk that owns a vertex (the first copy): the one whose height is shown when the copies differ.</summary>
    public static (int Chunk, int K) Owner(int id) => Copies(id).Last();

    public float Height(int id) => IsInner(id) ? Inner[id - OuterCount] : Outer[id];

    public void SetHeight(int id, float h)
    {
        if (IsInner(id)) Inner[id - OuterCount] = h;
        else Outer[id] = h;
    }

    public TerrainGrid Clone()
    {
        var g = new TerrainGrid();
        Array.Copy(Outer, g.Outer, OuterCount);
        Array.Copy(Inner, g.Inner, InnerCount);
        Array.Copy(Holes, g.Holes, InnerCount);
        return g;
    }

    /// <summary>The grid of a tile: each vertex from its owning chunk (another copy when that chunk is missing).</summary>
    public static TerrainGrid FromTile(TerrainTile t)
    {
        var g = new TerrainGrid();
        Array.Fill(g.Outer, float.NaN);
        Array.Fill(g.Inner, float.NaN);
        for (int id = 0; id < Count; id++)
            foreach (var (chunk, k) in Copies(id).Reverse())
                if (t.Chunks[chunk] is { } ch)
                {
                    g.SetHeight(id, ch.Heights[k]);
                    break;
                }
        foreach (var ch in t.AllChunks)
            for (int qy = 0; qy < 8; qy++)
                for (int qx = 0; qx < 8; qx++)
                    g.Holes[(ch.Y * 8 + qy) * InnerSize + ch.X * 8 + qx] = ch.IsHole(qx, qy);
        return g;
    }
}

/// <summary>
/// Terrain normals (MCNR) computed from the heights the way the stock tiles were made: the normalized sum of the unit
/// normals of the triangles around the vertex (8 for an outer vertex, 4 for an inner one; each quad is 4 triangles
/// around its inner vertex). Checked on the stock tiles (MapTests): mean difference 0.25 degrees from the stored normals,
/// which is the rounding of the stored signed bytes. Vertices on a tile edge use the neighbouring tile's heights when
/// it is given; without it, only the triangles inside the tile.
/// </summary>
public static class TerrainNormals
{
    private const float U = TerrainMath.UnitSize;

    /// <summary>Heights around a tile, by tile-local grid coordinates that may run one step into the neighbouring tiles
    /// (row or column -1, or 129 / 128); NaN where nothing is known.</summary>
    public sealed class Field
    {
        private readonly Func<int, int, TerrainGrid?> _tile;
        public int TileX { get; }
        public int TileY { get; }

        /// <param name="tile">(tile x, tile y) -> its grid, or null</param>
        public Field(int tileX, int tileY, Func<int, int, TerrainGrid?> tile)
        {
            TileX = tileX;
            TileY = tileY;
            _tile = tile;
        }

        public float O(int row, int col)
        {
            int tx = TileX, ty = TileY;
            if (row < 0) { ty--; row += 128; }
            else if (row > 128) { ty++; row -= 128; }
            if (col < 0) { tx--; col += 128; }
            else if (col > 128) { tx++; col -= 128; }
            var g = _tile(tx, ty);
            return g == null ? float.NaN : g.O(row, col);
        }

        public float I(int row, int col)
        {
            int tx = TileX, ty = TileY;
            if (row < 0) { ty--; row += 128; }
            else if (row > 127) { ty++; row -= 128; }
            if (col < 0) { tx--; col += 128; }
            else if (col > 127) { tx++; col -= 128; }
            var g = _tile(tx, ty);
            return g == null ? float.NaN : g.I(row, col);
        }
    }

    private static Vector3 P(float row, float col, float h) => new(-row * U, -col * U, h);   // world axes: rows go -x, columns go -y

    private static void AddFace(ref Vector3 sum, Vector3 a, Vector3 b, Vector3 c)
    {
        if (float.IsNaN(a.Z) || float.IsNaN(b.Z) || float.IsNaN(c.Z)) return;
        var n = Vector3.Cross(b - a, c - a);
        if (n.Z < 0) n = -n;
        float len = n.Length();
        if (len > 0) sum += n / len;
    }

    /// <summary>The normal of a vertex (world x, y, z, unit length).</summary>
    public static Vector3 Normal(Field f, int id)
    {
        var (r, c) = TerrainGrid.RowCol(id);
        var sum = Vector3.Zero;
        if (TerrainGrid.IsInner(id))
        {
            var v = P(r + 0.5f, c + 0.5f, f.I(r, c));
            var ring = new[] { (r, c), (r, c + 1), (r + 1, c + 1), (r + 1, c) };
            for (int q = 0; q < 4; q++)
            {
                var (ar, ac) = ring[q];
                var (br, bc) = ring[(q + 1) % 4];
                AddFace(ref sum, v, P(ar, ac, f.O(ar, ac)), P(br, bc, f.O(br, bc)));
            }
        }
        else
        {
            var v = P(r, c, f.O(r, c));
            foreach (var (qr, qc) in new[] { (r - 1, c - 1), (r - 1, c), (r, c - 1), (r, c) })
            {
                var mid = P(qr + 0.5f, qc + 0.5f, f.I(qr, qc));
                var ring = new[] { (qr, qc), (qr, qc + 1), (qr + 1, qc + 1), (qr + 1, qc) };
                int at = Array.IndexOf(ring, (r, c));
                foreach (var (nr, nc) in new[] { ring[(at + 3) % 4], ring[(at + 1) % 4] })
                    AddFace(ref sum, v, P(nr, nc, f.O(nr, nc)), mid);
            }
        }
        return sum.LengthSquared() > 0 ? Vector3.Normalize(sum) : Vector3.UnitZ;
    }

    /// <summary>The normal as stored in MCNR (signed bytes, x y z).</summary>
    public static (sbyte X, sbyte Y, sbyte Z) Quantize(Vector3 n) =>
        ((sbyte)Math.Clamp(MathF.Round(n.X * 127), -127, 127), (sbyte)Math.Clamp(MathF.Round(n.Y * 127), -127, 127), (sbyte)Math.Clamp(MathF.Round(n.Z * 127), -127, 127));

    /// <summary>The vertices whose normal depends on the height of vertex <paramref name="id"/> (tile-local grid ids;
    /// ids that fall outside the tile are returned as (tile offset, id) pairs).</summary>
    public static IEnumerable<(int Dx, int Dy, int Id)> Affected(int id)
    {
        var (r, c) = TerrainGrid.RowCol(id);
        if (TerrainGrid.IsInner(id))
        {
            yield return (0, 0, id);
            foreach (var (rr, cc) in new[] { (r, c), (r, c + 1), (r + 1, c), (r + 1, c + 1) }) foreach (var x in OuterAt(rr, cc)) yield return x;
            yield break;
        }
        foreach (var (rr, cc) in new[] { (r, c), (r - 1, c), (r + 1, c), (r, c - 1), (r, c + 1) }) foreach (var x in OuterAt(rr, cc)) yield return x;
        foreach (var (rr, cc) in new[] { (r - 1, c - 1), (r - 1, c), (r, c - 1), (r, c) }) foreach (var x in InnerAt(rr, cc)) yield return x;
    }

    /// <summary>An outer vertex by tile-local coordinates (which may be outside the tile): every tile that stores it.</summary>
    private static IEnumerable<(int Dx, int Dy, int Id)> OuterAt(int r, int c)
    {
        foreach (var (dy, rr) in Split(r, 128))
            foreach (var (dx, cc) in Split(c, 128))
                yield return (dx, dy, TerrainGrid.OuterId(rr, cc));
    }

    private static IEnumerable<(int D, int Local)> Split(int v, int size)
    {
        if (v < 0) { yield return (-1, v + size); yield break; }
        if (v > size) { yield return (1, v - size); yield break; }
        yield return (0, v);
        if (v == 0) yield return (-1, size);
        if (v == size) yield return (1, 0);
    }

    private static IEnumerable<(int Dx, int Dy, int Id)> InnerAt(int r, int c)
    {
        int dy = r < 0 ? -1 : r > 127 ? 1 : 0, dx = c < 0 ? -1 : c > 127 ? 1 : 0;
        yield return (dx, dy, TerrainGrid.InnerId(r - dy * 128, c - dx * 128));
    }
}
