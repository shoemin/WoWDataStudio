using System.Numerics;
using WoWData.Core.Blp;
using WoWData.Core.M2;
using WoWData.Core.Maps;
using WoWData.Core.Mpq;

namespace IngameRunner;

/// <summary>Read-only look-around near a world point: tile pieces (doodads, buildings, ground textures) and model facts.</summary>
public static class Explore
{
    public static void Near(ClientData client, string mapDir, float wx, float wy, float radius)
    {
        var cat = MapCatalog.Load(client, null);
        var map = cat.ByDirectory(mapDir)!;
        int tx = (int)MathF.Floor(32 - wy / TerrainMath.TileSize), ty = (int)MathF.Floor(32 - wx / TerrainMath.TileSize);
        Console.WriteLine($"point {wx},{wy} -> tile {tx}_{ty}");
        var seen = new HashSet<string>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                var path = map.TilePath(tx + dx, ty + dy);
                var data = client.Read(path);
                if (data == null) continue;
                var bd = TileBreakdown.Build(map.Name, map.Directory, tx + dx, ty + dy, data, client.Read, cat.AreaName);
                foreach (var it in bd.All())
                {
                    if (it.WorldPosition is not { } p || it.OnNeighbour) continue;
                    float d = Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(wx, wy));
                    if (d > radius) continue;
                    if (it.Kind is BreakdownKind.Doodad or BreakdownKind.Building)
                    {
                        var key = $"{it.Kind}|{it.Path}|{p}";
                        if (!seen.Add(key)) continue;
                        Console.WriteLine($"{it.Kind,-9} d={d,6:0.0} pos=({p.X:0.0},{p.Y:0.0},{p.Z:0.0}) {it.Path} | {it.Detail}");
                    }
                }
                if (dx == 0 && dy == 0)
                    foreach (var t in bd.Group("textures").Items)
                        Console.WriteLine($"TEXTURE   {t.Path} coverage={t.Coverage:P1}");
            }
    }

    public static void Model(ClientData client, string path)
    {
        var data = client.Read(path);
        if (data == null) { Console.WriteLine($"{path}: not found"); return; }
        if (path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in WmoHeader.Textures(data)) Console.WriteLine($"  wmo texture {t} {Blp(client, t)}");
            return;
        }
        var m = M2Model.Load(data);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var v in m.Vertices) { min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); }
        Console.WriteLine($"{path}: {m.Vertices.Length} vertices, {m.Bones.Length} bones, {m.Sequences.Length} sequences, bounds {min} .. {max}");
        Console.WriteLine("  sequences: " + string.Join(" ", m.Sequences.Select(s => s.AnimId).Distinct()));
        foreach (var t in m.Textures) Console.WriteLine($"  texture type {t.Type} '{t.FileName}' {(string.IsNullOrEmpty(t.FileName) ? "" : Blp(client, t.FileName))}");
        foreach (var tu in m.Views[0].TexUnits)
        {
            var tex = tu.TextureLookup < m.TextureLookup.Length ? m.TextureLookup[tu.TextureLookup] : -1;
            Console.WriteLine($"  texunit submesh {tu.Submesh}: texture {tex} '{(tex >= 0 && tex < m.Textures.Length ? m.Textures[tex].FileName : "")}' blend {m.FlagsOf(tu).Blend} flags {m.FlagsOf(tu).Flags}");
        }
        for (int s = 0; s < m.Views[0].Submeshes.Length; s++)
        {
            var sm = m.Views[0].Submeshes[s];
            Console.WriteLine($"  submesh {s}: geoset {sm.GeosetId} vertices {sm.VertexCount} start {sm.VertexStart}");
        }
    }

    private static string Blp(ClientData client, string path)
    {
        var d = client.Read(path);
        if (d == null) return "(missing)";
        var b = BlpFile.Parse(d);
        return $"[{b.FormatName} {b.Width}x{b.Height} from {client.Find(path)?.Name}]";
    }
}
