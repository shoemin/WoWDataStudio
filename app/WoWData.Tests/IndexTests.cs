using System.IO;
using System.Linq;
using System.Text.Json;
using WoWData.Core.Index;

namespace WoWData.Tests;

/// <summary>The search index and its lookup chains (the examples of MODEL_TOOL_RESEARCH.md section 2).</summary>
[Collection(IndexCollection.Name)]
public class IndexTests
{
    private readonly IndexFixture _f;
    public IndexTests(IndexCollection.Handle h) { _f = h.Fixture; }

    [Fact]
    public void CountsMatchPythonIndex()
    {
        var py = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Oracle.Ensure("mpq"), "index_counts.json")))!;
        foreach (var k in new[] { "files", "creature_display", "gameobject_display", "item_display", "asset" })
            Assert.Equal(py[k].GetInt32(), _f.Result.Counts[k]);
        Assert.Equal(10534, _f.Result.Counts["creature_display"]);
        Assert.Equal(1638, _f.Result.Counts["gameobject_display"]);
        Assert.Equal(29604, _f.Result.Counts["item_display"]);
        Assert.Equal(79733, _f.Result.Counts["files"]);
        Assert.Equal(52793, _f.Entries.Count(e => e.Kind == AssetKind.Texture));
        Assert.True(_f.Entries.Count > 50000);
    }

    [Fact]
    public void BoarDisplay503ToBoarM2AndIvorySkin()
    {
        var d = _f.Index.CreatureDisplay(503)!.Value;
        Assert.Equal(@"Creature\Boar\Boar.m2", d.Model);
        Assert.Equal(new[] { "BoarSkinIvory" }, d.Skins);
        var files = _f.Index.Assets("creature", 503).ToDictionary(a => a.Role);
        Assert.Equal((@"Creature\Boar\Boar.m2", "model.MPQ"), (files["model"].Path, files["model"].Archive));
        Assert.Equal((@"Creature\Boar\BoarSkinIvory.blp", "texture.MPQ"), (files["skin1"].Path, files["skin1"].Archive));
    }

    [Fact]
    public void BoarModelVariantsPerDisplay()
    {
        // every display of the model with its texture slot: 27 displays, 9 distinct skins (slot 11)
        var v = _f.Index.ModelVariants(@"Creature\Boar\Boar.m2").Where(x => x.OwnerKind == "creature").ToList();
        Assert.Equal(27, v.Select(x => x.OwnerId).Distinct().Count());
        Assert.Equal(9, v.Where(x => x.Slot == 11).Select(x => x.Texture.ToUpperInvariant()).Distinct().Count());
        Assert.Contains(v, x => x.OwnerId == 503 && x.Slot == 11 && x.Texture == @"Creature\Boar\BoarSkinIvory.blp");
        Assert.Equal(27, _f.Index.CreatureSkinSets(@"Creature\Boar\Boar.m2").Sum(s => s.Displays.Count));
    }

    [ServerFact]
    public void BoarSearchByName()
    {
        if (!_f.HasServerData) return;   // the snapshot has no complete server data; covered by ServerFallbackTests without the server
        var rows = _f.Index.FindCreatures("mottled boar");
        Assert.Equal(new long[] { 3098, 3099, 3100, 3225 }, rows.Select(r => r.Entry).OrderBy(x => x));
        var boar = rows.Single(r => r.Entry == 3098);
        Assert.Equal(new long[] { 503 }, boar.Displays);
        var e = _f.Entries.Single(x => x.Key == "creature:3098");
        Assert.Equal("Mottled Boar", e.Name);
        var d = _f.Index.Details(e, null);
        Assert.Equal(new[] { "Mottled Boar (creature 3098)", "display 503", @"Creature\Boar\Boar.m2", "skin: BoarSkinIvory" }, d.Chain);
        Assert.Contains(d.Textures, t => t.Path == @"Creature\Boar\BoarSkinIvory.blp" && t.Role == "skin1");
        Assert.Contains(d.Facts, f => f.Contains("27 displays") && f.Contains("9 skins"));
        Assert.Contains("creature:503", e.Links.Split(' '));
    }

    [Fact]
    public void ItemsGameObjectsCharacters()
    {
        var files = _f.Index.Assets("item", 22291).Select(a => a.Path).ToList();
        Assert.Contains(@"Item\ObjectComponents\Weapon\Axe_2H_Battle_A_01.m2", files);
        Assert.Contains(@"Item\ObjectComponents\Weapon\Axe_2H_Battle_A_01Green.blp", files);
        Assert.Contains(@"Interface\Icons\INV_Axe_09.blp", files);
        files = _f.Index.Assets("item", 9995).Select(a => a.Path).ToList();
        Assert.Contains(@"Item\TextureComponents\TorsoUpperTexture\Leather_Horde_A_02Brown_Chest_TU_M.blp", files);
        Assert.Contains(@"Item\TextureComponents\TorsoUpperTexture\Leather_Horde_A_02Brown_Chest_TU_F.blp", files);
        var go = _f.Index.Assets("gameobject", 3813).Single(a => a.Role == "model");
        Assert.EndsWith("MARAUDONSPORETREE.M2", go.Path.ToUpperInvariant());

        var orc = _f.Entries.Single(e => e.Key == "race:2:0");
        Assert.Equal("Orc Male", orc.Name);
        var d = _f.Index.Details(orc, null);
        Assert.Contains(@"Character\Orc\Male\OrcMale.m2", d.Chain);
        Assert.Contains(d.Chain, c => c.StartsWith("248 character-creation textures"));
        Assert.Contains(d.Textures, t => t.Path == @"Character\Orc\Male\OrcMaleSkin00_00.blp");
        // per-variant relation for characters too
        Assert.Contains(_f.Index.ModelVariants(@"Character\Orc\Male\OrcMale.m2"), v => v.OwnerKind == "character" && v.Texture == @"Character\Orc\Male\OrcMaleSkin00_00.blp");

        var palms = _f.Entries.Where(e => e.Kind == AssetKind.WorldObject && e.SearchText.Contains("durotarpalm")).ToList();
        Assert.Equal(3, palms.Count(p => p.Path!.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)));
    }

    [ServerFact]
    public void ServerNamedItemsAndObjects()
    {
        if (!_f.HasServerData) return;
        Assert.Contains(_f.Entries, e => e.Kind == AssetKind.Item && e.Id == 12282 && e.Name == "Worn Battleaxe" && e.Links == "item:22291");
        Assert.Contains(_f.Entries, e => e.Kind == AssetKind.GameObject && e.Id == 178561 && e.Links == "gameobject:3813");
    }

    [Fact]
    public void SpellEffectsLinkedToSpells()
    {
        var precast = _f.Entries.Where(e => e.Kind == AssetKind.SpellEffect && e.Name == "Fire Precast Hand").ToList();
        Assert.NotEmpty(precast);
        var d = _f.Index.Details(precast[0], null);
        Assert.Contains(_f.Index.SpellUses(precast[0].Id), u => u.StartsWith("Fireball (Rank 1) - spell 133"));
        Assert.NotEmpty(d.UsedBy);
        Assert.EndsWith("Fire_Precast_Hand.m2", d.Models.Single(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>0.7.0 regression: the roles of spell effects follow the client's SpellVisualRec (decompile): fields 1-5 are
    /// the precast, cast, impact, state (aura) and channel kits, 7 the missile model (when field 6 says there is a
    /// missile), 12 the model and 13 the kit of a persistent area; 14 is a sound and 15 flags. Before, fields 13-15 were
    /// all read as kits, so Frostbolt (flags 1) showed kit 1's "zzOLD__FireShield_Cast_Base" as a "channel" effect.</summary>
    [Fact]
    public void SpellEffectRolesFollowTheClientsSpellVisualFields()
    {
        // the reader takes its field positions from the layout file, whose names come from the decompile
        var layout = WoWData.Core.Dbc.DbcLayouts.Default.Get("SpellVisual")!;
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 13 }, WoWData.Core.Dbc.SpellVisualRec.KitColumns.Select(c => layout.Column(c)!.Field));
        Assert.Equal("SoundEntries", layout.Columns.Single(c => c.Field == 14).ForeignKey);
        Assert.Equal("Flags", layout.Columns.Single(c => c.Field == 15).Name);
        Assert.Equal(12, layout.Column("AreaModel")!.Field);

        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_f.Path};Mode=ReadOnly;Pooling=False");
        db.Open();
        HashSet<string> Roles(int spell, string effect)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT u.role FROM spell_effect_use u JOIN spell_effect e ON e.id = u.effect_id WHERE u.spell_id = $s AND e.name = $n";
            c.Parameters.AddWithValue("$s", spell);
            c.Parameters.AddWithValue("$n", effect);
            var set = new HashSet<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
        // Shadow Bolt (686): precast and cast kits, the impact kit, the missile
        Assert.Equal(new HashSet<string> { "precast", "cast" }, Roles(686, "Shadow Precast Low Hand"));
        Assert.Equal(new HashSet<string> { "impact" }, Roles(686, "DeathCoil Impact Chest"));
        // Frostbolt (116): cast, impact, the slow's aura kit (field 4); nothing from its flags (field 15 = 1)
        Assert.Equal(new HashSet<string> { "cast" }, Roles(116, "Ice Cast Low Hand"));
        Assert.Equal(new HashSet<string> { "impact" }, Roles(116, "Ice ImpactDD Med Chest"));
        Assert.Equal(new HashSet<string> { "aura" }, Roles(116, "Ice Precast Uber Head"));
        Assert.Empty(Roles(116, "zzOLD__FireShield_Cast_Base"));
        // Blizzard (10): the channel kit (field 5) and the persistent area's model (field 12)
        Assert.Contains("channel", Roles(10, "Ice Precast Low Hand"));
        Assert.Equal(new HashSet<string> { "impact", "area" }, Roles(10, "Blizzard Impact Base"));   // also in its impact kit
        // Consecration (26573): the area model; Arcane Missiles (5143): the channel kit
        Assert.Equal(new HashSet<string> { "area" }, Roles(26573, "Consecration Impact Base"));
        Assert.Equal(new HashSet<string> { "channel" }, Roles(5143, "Magic PreCast Hand"));
        // no role comes from a sound or a flags value any more
        using var all = db.CreateCommand();
        all.CommandText = "SELECT DISTINCT role FROM spell_effect_use ORDER BY role";
        var roles = new List<string>();
        using (var r = all.ExecuteReader()) while (r.Read()) roles.Add(r.GetString(0));
        Assert.Equal(new[] { "area", "aura", "cast", "channel", "impact", "missile", "precast" }, roles);
    }

    [Fact]
    public void TextureUsersAndOwners()
    {
        var users = _f.Index.TextureUsers(@"Creature\Boar\BoarSkinIvory.blp");
        Assert.Equal(6, users.Count);   // displays 193, 389, 499, 503, 1167, 10015
        Assert.Equal(new[] { @"Creature\Boar\Boar.m2" }, _f.Index.ModelsUsingTexture(@"Creature\Boar\BoarSkinIvory.blp"));
        var owners = _f.Index.OwnersOfTextures(new[] { @"Creature\Boar\BoarSkinIvory.blp" });
        Assert.Contains("creature:503", owners);
        Assert.Contains("creature:193", owners);
    }

    [Fact]
    public void IndexIsCurrentUntilArchivesChange()
    {
        Assert.True(AssetIndex.IsCurrent(_f.Path, TestEnv.Client));
        Assert.False(AssetIndex.IsCurrent(Path.Combine(_f.Dir, "missing.sqlite"), TestEnv.Client));
    }
}

/// <summary>Without the server database: names fall back to file and model names, and the UI says "not connected". It builds
/// an index of its own, so it is in the index collection (never two builds at once).</summary>
[Collection(IndexCollection.Name)]
public class ServerFallbackTests : IDisposable
{
    private readonly string _dir = TestEnv.TempDir("noserver");
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task UnreachableServerFallsBackToFileNames()
    {
        var (ok, msg) = await new ServerDb(TestEnv.NoServer).TestAsync();
        Assert.False(ok);
        Assert.Contains("could not connect", msg);

        var path = Path.Combine(_dir, "index.sqlite");
        var res = await IndexBuilder.BuildAsync(TestEnv.Client, path, TestEnv.NoServer);
        Assert.False(res.ServerUsed);
        Assert.Contains("could not connect", res.ServerMessage);
        using (var ix = new AssetIndex(path))
        {
            Assert.False(ix.HasServer);
            var entries = ix.LoadEntries();
            Assert.Equal(10534, entries.Count(e => e.Kind == AssetKind.Creature));        // one per display
            var boar = entries.Single(e => e.Key == "cdisplay:503");
            Assert.Equal("Boar - BoarSkinIvory", boar.Name);
            Assert.Equal(@"Creature\Boar\Boar.m2", boar.Path);
            var d = ix.Details(boar, null);
            Assert.Equal(new[] { "display 503", @"Creature\Boar\Boar.m2", "skin: BoarSkinIvory" }, d.Chain);
            Assert.Contains(d.Facts, f => f.Contains("not available"));
            Assert.Equal(29604, entries.Count(e => e.Kind == AssetKind.Item));             // one per item display
            Assert.Contains(entries, e => e.Key == "idisplay:22291" && e.Name.StartsWith("Axe_2H_Battle_A_01"));
        }

        // refreshing with a still unreachable server keeps the file names
        var (ok2, msg2) = await IndexBuilder.RefreshServerAsync(path, TestEnv.NoServer);
        Assert.False(ok2);
        Assert.False(string.IsNullOrEmpty(msg2));
        using (var ix = new AssetIndex(path))
            Assert.Contains(ix.LoadEntries(), e => e.Key == "cdisplay:503");

        // and with the real server (when it runs), the names arrive without a full rebuild
        if (TestEnv.ServerAvailable)
        {
            var (ok3, _) = await IndexBuilder.RefreshServerAsync(path, TestEnv.LocalServer);
            Assert.True(ok3);
            using var ix = new AssetIndex(path);
            Assert.True(ix.HasServer);
            Assert.Contains(ix.LoadEntries(), e => e.Key == "creature:3098" && e.Name == "Mottled Boar");
        }
    }

    [Fact]
    public async Task DisabledServerIsNotContacted()
    {
        var s = TestEnv.LocalServer;
        s.Enabled = false;
        var (ok, msg) = await new ServerDb(s).TestAsync();
        Assert.False(ok);
        Assert.Contains("turned off", msg);
    }

    [Fact]
    public async Task OnlySelectStatementsAreSent()
    {
        await using var conn = new MySqlConnector.MySqlConnection();
        var db = new ServerDb(TestEnv.NoServer);
        await Assert.ThrowsAsync<ServerDbException>(() => db.QueryAsync(conn, "DELETE FROM creature_template", default));
        await Assert.ThrowsAsync<ServerDbException>(() => db.QueryAsync(conn, "SELECT 1; DROP TABLE x", default));
    }
}
