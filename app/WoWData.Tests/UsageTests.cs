using System.Diagnostics;
using System.IO;
using System.Linq;
using WoWData.Core.Index;
using WoWData.Core.Maps;
using WoWData.Core.Usage;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>"Used in game" (Usage\*): the precomputed tables of the index and the reports of the panel, on the real
/// client and (when it runs) the local VMaNGOS server.</summary>
[Collection(IndexCollection.Name)]
public class UsageTests
{
    private readonly IndexFixture _f;
    private readonly ITestOutputHelper _out;
    private static readonly object Gate = new();
    private static UsageService? _svc;

    public UsageTests(IndexCollection.Handle h, ITestOutputHelper output)
    {
        _f = h.Fixture;
        _out = output;
    }

    private UsageService Svc
    {
        get
        {
            lock (Gate)
            {
                if (_svc == null || _svc.IndexPath != _f.Path) _svc = new UsageService(_f.Path);
                return _svc;
            }
        }
    }

    private void Dump(UsageReport r)
    {
        _out.WriteLine($"{r.Key}: {r.Headline} ({r.Milliseconds:0.0} ms)");
        foreach (var s in r.Sections)
        {
            _out.WriteLine($"  [{s.Id}] {s.Title}: {s.Summary}");
            foreach (var l in s.Lines.Concat(s.Tail)) _out.WriteLine($"     {new string(' ', l.Indent * 2)}{l.Text}{(l.Spans.Any(x => x.Key != null) ? "   -> " + string.Join(" ", l.Spans.Where(x => x.Key != null).Select(x => x.Key)) : "")}");
            if (s.More > 0) _out.WriteLine($"     ... {s.More} more");
        }
        foreach (var n in r.Notes) _out.WriteLine("  note: " + n);
    }

    private string WorldModel(string fileName) =>
        _f.Entries.First(e => e.Kind == AssetKind.WorldObject && e.Path != null && e.Path.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase)).Path!;

    [Fact]
    public void SpawnPositionsMapToChunks()
    {
        // a Mottled Boar spawn in the Valley of Trials: Kalimdor_39_32.adt, chunk (13, 10) whose MCNK area id is 363
        var loc = MapScanner.Locate(-349.288f, -4184.41f)!.Value;
        Assert.Equal((39, 32, 10 * 16 + 13), (loc.TileX, loc.TileY, loc.ChunkIndex));
        Assert.True(_f.Result.Counts["map_tiles"] > 2000, $"map tiles: {_f.Result.Counts["map_tiles"]}");
        _out.WriteLine(string.Join(", ", _f.Result.Counts.Where(k => k.Key.StartsWith("map") || k.Key.StartsWith("doodad") || k.Key.StartsWith("wmo") || k.Key.StartsWith("u_") || k.Key.EndsWith("_ms"))
            .Select(k => $"{k.Key}={k.Value}")));
    }

    [ServerFact]
    public void MottledBoarSpawnsInDurotar()
    {
        if (!_f.HasServerData) return;   // covered offline by OfflineFallbackNamesWhatNeedsTheServer
        var r = Svc.Report("creature:3098");
        Dump(r);
        var spawns = r.Section("spawns")!;
        Assert.StartsWith("65 spawns", spawns.Summary);   // MODEL_TOOL_RESEARCH: 65 Mottled Boar spawns
        var zoneLines = spawns.Lines.Where(l => l.Indent == 0 && l.Text.Contains("spawn")).ToList();
        Assert.Equal(65, zoneLines.Sum(l => int.Parse(System.Text.RegularExpressions.Regex.Match(l.Text, @"(\d+) spawns?$").Groups[1].Value)));
        Assert.Contains(zoneLines, l => l.Text.StartsWith("Valley of Trials, Durotar"));
        Assert.All(zoneLines, l => Assert.Contains("Durotar", l.Text));
        Assert.Contains(spawns.Tail, l => l.Text.Contains("1, -349.3, -4184.4"));
        Assert.Contains("Beast", r.Headline.Text);
        // faction template 189 "Creature" has no group masks: neutral (yellow) to both sides
        Assert.Contains(r.Section("npc")!.Lines, l => l.Text.Contains("neutral to Alliance, neutral to Horde"));
        Assert.NotNull(r.Section("quests"));
        Assert.Contains(r.Section("loot")!.Lines, l => l.Spans.Any(s => s.Key != null && s.Key.StartsWith("item:")));
        var look = r.Section("look")!;
        Assert.Contains(look.Lines, l => l.Text.Contains("Boar.m2") && l.Text.Contains("27 creature displays"));
        Assert.Contains(look.Lines, l => l.Text.StartsWith("Also used by") && l.Spans.Any(s => s.Key == "creature:113"));   // Stonetusk Boar
    }

    [ServerFact]
    public void ItemWithDropAndVendorSources()
    {
        if (!_f.HasServerData) return;
        // Weak Flux (2880): sold by blacksmithing suppliers and dropped by creatures
        var r = Svc.Report("item:2880");
        Dump(r);
        Assert.Equal("Weak Flux", r.Headline.Spans[0].Text);
        var drops = r.Section("drops")!;
        Assert.Matches(@"^\d[\d,]* creatures?", drops.Summary);
        Assert.Contains(drops.Lines, l => l.Spans.Any(s => s.Key != null && s.Key.StartsWith("creature:")) && l.Text.Contains('%'));
        var vendors = r.Section("vendors")!;
        Assert.Matches(@"^\d+ vendors?", vendors.Summary);
        Assert.True(vendors.Lines.Count > 0);
        Assert.All(vendors.Lines, l => Assert.StartsWith("creature:", l.Spans[0].Key));
        Assert.NotNull(r.Section("look"));
        // a crafted item: Linen Bandage (1251) is made by the First Aid spell 3275
        var bandage = Svc.Report("item:1251");
        Dump(bandage);
        Assert.Contains(bandage.Section("crafted")!.Lines, l => l.Text.StartsWith("Linen Bandage") && l.Text.Contains("First Aid"));
    }

    [Fact]
    public void TextureAffectsSummaryForBoarSkinIvory()
    {
        var r = Svc.Report(@"file:Creature\Boar\BoarSkinIvory.blp");
        Dump(r);
        var affects = r.Section("affects")!;
        Assert.Contains("6 creature displays", affects.Summary);   // displays 193, 389, 499, 503, 1167, 10015
        var creatures = r.Section("creatures")!;
        Assert.Contains("503", creatures.Summary);
        if (_f.HasServerData)
        {
            Assert.Matches(@"6 creature displays \(\d+ NPCs\)", affects.Summary);
            Assert.Contains(creatures.Lines, l => l.Spans.Any(s => s.Key == "creature:3098"));
        }
        // a world texture: the Durotar palm's texture is used by models placed on the map
        var palm = WorldModel("DurotarPalm01.m2");
        var tex = Svc.Report($"file:{palm}");
        Assert.NotNull(tex.Section("placements"));
    }

    [Fact]
    public void DoodadPlacementsOfDurotarPalm()
    {
        var palm = WorldModel("DurotarPalm01.m2");
        var r = Svc.Report($"file:{palm}");
        Dump(r);
        var p = r.Section("placements")!;
        Assert.Matches(@"^[\d,]+ placements in", p.Summary);
        Assert.Contains(p.Lines, l => l.Text.StartsWith("Echo Isles, Durotar"));
        Assert.Contains(p.Lines, l => l.Text.StartsWith("Southfury River, Durotar"));
        Assert.Contains(p.Tail, l => l.Text.StartsWith("Map tiles (ADT files): Kalimdor_"));
        // MODEL_TOOL_RESEARCH scanned tiles 38-43 x 26-37 and counted durotarpalm01 x353 without removing the placements
        // that two tiles both list (same unique id); deduplicated, the same tiles hold a little fewer. Palms also line
        // the Barrens and Tanaris coasts, so Durotar (area 14 and its subzones) has only part of them.
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_f.Path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT SUM(p.count), SUM(CASE WHEN p.area = 14 OR a.parent = 14 THEN p.count ELSE 0 END), " +
                        "SUM(CASE WHEN p.tx BETWEEN 38 AND 43 AND p.ty BETWEEN 26 AND 37 AND p.parent = '' THEN p.count ELSE 0 END) FROM u_place p LEFT JOIN u_area a ON a.id = p.area WHERE p.model = $m";
        c.Parameters.AddWithValue("$m", palm);
        using var rd = c.ExecuteReader();
        rd.Read();
        long total = rd.GetInt64(0), durotar = rd.GetInt64(1), research = rd.GetInt64(2);
        _out.WriteLine($"DurotarPalm01: {total} placements, {durotar} in Durotar, {research} in tiles 38-43 x 26-37");
        Assert.InRange(research, 300, 353);
        Assert.InRange(durotar, 50, total - 1);
        Assert.Contains(r.Section("placements")!.Tail, l => l.Text.StartsWith("Inside Ogrimmar.wmo"));
    }

    [Fact]
    public void SpellVisualToSpellNames()
    {
        var fx = _f.Entries.First(e => e.Kind == AssetKind.SpellEffect && e.Name == "Fire Precast Hand");
        var r = Svc.Report(fx.Key);
        Dump(r);
        var spells = r.Section("spells")!;
        Assert.Contains(spells.Lines, l => l.Text.StartsWith("Fireball (") && l.Text.Contains("ranks: Rank 1 to Rank") && l.Text.Contains("spells 133, ") && l.Text.Contains("precast"));
        Assert.Contains("visual", spells.Summary);
        if (_f.HasServerData)
        {
            var casters = r.Section("casters")!;
            Assert.Contains(casters.Lines, l => l.Text.Contains("Mage trainers"));
            Assert.Contains(casters.Lines, l => l.Spans.Any(s => s.Key != null && s.Key.StartsWith("creature:")));
        }
    }

    [Fact]
    public void CharacterTextureIsACharacterCreationChoice()
    {
        var r = Svc.Report(@"file:Character\Orc\Male\OrcMaleSkin00_00.blp");
        Dump(r);
        var ch = r.Section("character")!;
        Assert.Contains("Orc Male", ch.Summary);
        Assert.Contains(ch.Lines, l => l.Text.Contains("Skin colour 1 of") && l.Spans.Any(s => s.Key == "race:2:0"));
        var race = Svc.Report("race:2:0");
        Dump(race);
        Assert.Contains(race.Section("character")!.Lines, l => l.Text.StartsWith("Skin colours: "));
    }

    [Fact]
    public void OfflineFallbackNamesWhatNeedsTheServer()
    {
        // a copy of the index with the server part removed, exactly what "Reload names" does when the server is gone
        var dir = TestEnv.TempDir("usage_offline");
        try
        {
            var path = Path.Combine(dir, "index.sqlite");
            File.Copy(_f.Path, path);
            var (ok, _) = IndexBuilder.RefreshServerAsync(path, TestEnv.NoServer).GetAwaiter().GetResult();
            Assert.False(ok);
            using var svc = new UsageService(path);
            var boar = svc.Report("cdisplay:503");
            Dump(boar);
            Assert.True(boar.NeedsServer);
            Assert.Contains(UsageReport.ServerNote, boar.Notes);
            Assert.Contains("server database", boar.Section("npcs")!.Summary);
            Assert.Contains(boar.Section("look")!.Lines, l => l.Text.Contains("Boar.m2") && l.Text.Contains("27 creature displays"));
            // client-only sections still work: texture users, placements, spell names, character choices
            var tex = svc.Report(@"file:Creature\Boar\BoarSkinIvory.blp");
            Assert.Contains("6 creature displays", tex.Section("affects")!.Summary);
            Assert.Contains(tex.Section("creatures")!.Lines, l => l.Spans.Any(s => s.Key == "cdisplay:503"));
            Assert.Contains(UsageReport.ServerNote, tex.Notes);
            var palm = svc.Report($"file:{WorldModel("DurotarPalm01.m2")}");
            Assert.Matches(@"^[\d,]+ placements in", palm.Section("placements")!.Summary);
            var fx = svc.Report(_f.Entries.First(e => e.Kind == AssetKind.SpellEffect && e.Name == "Fire Precast Hand").Key);
            Assert.Contains(fx.Section("spells")!.Lines, l => l.Text.StartsWith("Fireball ("));
            Assert.Contains("server database", fx.Section("casters")!.Summary);
            Assert.Contains(UsageReport.ServerNote, fx.Notes);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>0.6.0, "Show in Browse" of the tile breakdown: the keys of its models, buildings and textures are entries of
    /// the index (the Browse list), so the jump lands on an entry with its "Used in game". (Here to share this class's index.)</summary>
    [Fact]
    public void ShowInBrowseKeysResolveInTheIndex()
    {
        var keys = _f.Entries.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, x, y) in new[] { ("Azeroth", 32, 48), ("Kalimdor", 40, 30) })
        {
            var bd = TerrainBreakdownTests.Breakdown(dir, x, y);
            var items = bd.All().Where(i => i.BrowseKey != null).ToList();
            Assert.Contains(items, i => i.Kind == BreakdownKind.Texture);
            Assert.Contains(items, i => i.Kind == BreakdownKind.ModelGroup);
            var unresolved = items.Where(i => !keys.Contains(i.BrowseKey!)).Select(i => i.BrowseKey).Distinct().ToList();
            _out.WriteLine($"{dir} {x},{y}: {items.Count} entries with a Browse key, unresolved: {string.Join(", ", unresolved)}");
            Assert.Empty(unresolved);
            // what is not a model, building or texture has no Browse entry
            Assert.All(bd.All().Where(i => i.Kind is BreakdownKind.Chunk or BreakdownKind.Water or BreakdownKind.SoundEmitter or BreakdownKind.Area), i => Assert.Null(i.BrowseKey));
        }
    }
}
