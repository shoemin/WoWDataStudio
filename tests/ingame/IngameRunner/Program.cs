using WoWData.Core.Dbc;
using WoWData.Core.Index;
using WoWData.Core.Mpq;

namespace IngameRunner;

/// <summary>
/// In-game visual test of WoW Data Studio's edit features (tests\ingame\README.md):
///   IngameRunner explore near CLIENT MAPDIR X Y RADIUS | model CLIENT PATH | display CLIENT ID | spell CLIENT ID | item CLIENT DISPLAYID
///   IngameRunner prepare CLIENT HOME RUNDIR      (all 11 edits through the Core APIs into the workspace; RUNDIR\app_report.json)
///   IngameRunner deliver CLIENT HOME RUNDIR loose|unloose|patch|unpatch|uninstall|patchstate
///   IngameRunner v010 ... (V010.cs) | IngameRunner designers prepare|server|wdb|dryrun ... (Designers.cs)
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: see Program.cs"); return 2; }
        try
        {
            switch (args[0])
            {
                case "explore": return ExploreCmd(args);
                case "prepare": return Prepare.Run(args[1], args[2], args[3], args.Skip(4).ToArray());
                case "deliver": return Deliver.Run(args[1], args[2], args[3], args[4]);
                case "v010": return V010.Run(args);
                case "designers": return Designers.Run(args);
                default: Console.Error.WriteLine("unknown command " + args[0]); return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e);
            return 1;
        }
    }

    private static int ExploreCmd(string[] a)
    {
        using var client = new ClientData(Path.Combine(a[2], "Data"));
        switch (a[1])
        {
            case "near": Explore.Near(client, a[3], float.Parse(a[4]), float.Parse(a[5]), float.Parse(a[6])); break;
            case "model": Explore.Model(client, a[3]); break;
            case "display":
            {
                var cat = new Catalog(client);
                var d = cat.CreatureDisplay(int.Parse(a[3]))!;
                Console.WriteLine($"display {d.Id}: model {d.Model} scale {d.Scale} skins {string.Join(", ", d.Skins)}");
                foreach (var f in d.Files) Console.WriteLine($"  {f.Role}: {f.Path} ({f.Archive})");
                break;
            }
            case "wmo":
            {
                var g = WoWData.Core.Maps.WmoGeometry.Load(a[3], client.Read)!;
                var tri = new Dictionary<string, (int Out, int In)>();
                foreach (var grp in g.Groups)
                    foreach (var b in grp.Batches)
                    {
                        var t = b.Material < g.MaterialTextures.Count ? g.MaterialTextures[b.Material] : "?";
                        var v = tri.GetValueOrDefault(t);
                        tri[t] = grp.Indoor ? (v.Out, v.In + b.IndexCount / 3) : (v.Out + b.IndexCount / 3, v.In);
                    }
                foreach (var kv in tri.OrderByDescending(k => k.Value.Out)) Console.WriteLine($"  {kv.Key}: outdoor {kv.Value.Out} indoor {kv.Value.In} triangles");
                break;
            }
            case "ground":
            {
                // ground layers of the chunks within RADIUS of the point (with each layer's share)
                float wx = float.Parse(a[4]), wy = float.Parse(a[5]), rad = float.Parse(a[6]);
                var share = new Dictionary<string, double>();
                for (int tx = 38; tx <= 41; tx++) for (int ty = 32; ty <= 34; ty++)
                {
                    var data = client.Read($@"World\Maps\{a[3]}\{a[3]}_{tx}_{ty}.adt");
                    if (data == null) continue;
                    var tile = WoWData.Core.Maps.TerrainTile.Load(data, a[3], tx, ty);
                    foreach (var c in tile.AllChunks)
                    {
                        var cx = c.Position.X - 8 * WoWData.Core.Maps.TerrainMath.UnitSize; var cy = c.Position.Y - 8 * WoWData.Core.Maps.TerrainMath.UnitSize;
                        if (Math.Sqrt((cx - wx) * (cx - wx) + (cy - wy) * (cy - wy)) > rad) continue;
                        for (int l = 0; l < c.Layers.Count; l++)
                        {
                            double w = l == 0 ? 1.0 : (c.Alpha[l] is { } al ? al.Average(b => b / 255.0) : 0);
                            share[c.Layers[l]] = share.GetValueOrDefault(c.Layers[l]) + w;
                        }
                    }
                    if (tx == 39 && ty == 33) Console.WriteLine($"height at point: {tile.HeightAt(wx, wy)}");
                }
                foreach (var kv in share.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key}: {kv.Value:0.00}");
                break;
            }
            case "rgb":
            {
                // a BLP file (game path, or a file on disk) decoded as RGB only (alpha forced to 255), to see the colour under transparent pixels
                var bytes = File.Exists(a[3]) ? File.ReadAllBytes(a[3]) : client.Read(a[3])!;
                var img = WoWData.Core.Blp.BlpFile.Parse(bytes).Decode(0);
                for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
                WoWData.Core.Imaging.Png.Save(img, a[4], false);
                break;
            }
            case "height":
            {
                for (int i = 4; i + 1 < a.Length; i += 2)
                {
                    float wx = float.Parse(a[i]), wy = float.Parse(a[i + 1]);
                    int tx = (int)MathF.Floor(32 - wy / WoWData.Core.Maps.TerrainMath.TileSize), ty = (int)MathF.Floor(32 - wx / WoWData.Core.Maps.TerrainMath.TileSize);
                    var data = client.Read($@"World\Maps\{a[3]}\{a[3]}_{tx}_{ty}.adt");
                    var t = WoWData.Core.Maps.TerrainTile.Load(data!, a[3], tx, ty);
                    Console.WriteLine($"{wx} {wy}: tile {tx}_{ty} height {t.HeightAt(wx, wy)}");
                }
                break;
            }
            case "item":
            {
                var cat = new Catalog(client);
                foreach (var f in cat.ItemDisplay(int.Parse(a[3])) ?? new()) Console.WriteLine($"  {f.Role}: {f.Path} ({f.Archive})");
                break;
            }
            case "spell":
            {
                var cat = new Catalog(client);
                int id = int.Parse(a[3]);
                var spell = DbcTables.Spell(cat.Table("Spell")).First(s => s.Id == id);
                var vis = DbcTables.SpellVisual(cat.Table("SpellVisual")).ToDictionary(v => v.Id);
                var kits = DbcTables.SpellVisualKit(cat.Table("SpellVisualKit")).ToDictionary(k => k.Id);
                var fx = DbcTables.SpellVisualEffectName(cat.Table("SpellVisualEffectName")).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.Last());
                Console.WriteLine($"spell {spell.Id} {spell.Name} {spell.Rank} visuals {string.Join(",", spell.Visuals)}");
                foreach (var v in spell.Visuals.Where(vis.ContainsKey).Select(x => vis[x]))
                {
                    if (fx.TryGetValue(v.MissileModel, out var mm)) Console.WriteLine($"  visual {v.Id} missile: {mm.FileName}");
                    foreach (var k in v.Kits.Where(kits.ContainsKey))
                        foreach (var e in kits[k].Effects.Where(fx.ContainsKey))
                            Console.WriteLine($"  visual {v.Id} kit {k}: {fx[e].FileName}");
                }
                break;
            }
        }
        return 0;
    }
}
