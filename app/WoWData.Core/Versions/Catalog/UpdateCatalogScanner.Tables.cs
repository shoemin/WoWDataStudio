using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Dbc;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions.Catalog;

public sealed partial class UpdateCatalogScanner
{
    /// <summary>Record counts of every source table that could be read (name -> records, records left out as encrypted).</summary>
    private readonly ConcurrentDictionary<string, (int Rows, int Encrypted)> _rows160 = new(StringComparer.OrdinalIgnoreCase);

    private Db2Table? OpenTable(string name, bool named = false)
    {
        try
        {
            var t = _s.Client.Catalog.Open(_s.Casc, name);
            if (t != null && named) Db2Layouts.ForBuild(_s.Copy.Build)?.Apply(t, out _);
            return t;
        }
        catch (Exception e) when (e is Db2FormatException or CascMissingException or CascEncryptedException or InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static int StringColumn(Db2Table t, int nth = 0)
    {
        int k = 0;
        for (int i = 0; i < t.Columns.Count; i++)
            if (t.Columns[i].Type == Db2ValueType.String && k++ == nth) return i;
        return -1;
    }

    // ------------------------------------------------------------------ hotfixes

    private Dictionary<string, List<int>>? _hotfixes;

    /// <summary>
    /// The rows Blizzard's server sent after the client was built, from the client's hotfix cache
    /// (Cache\ADB\enUS\DBCache.bin, magic XFTH): per table, the record IDs. Entries are found by their XFTH marker; the
    /// table is named by the table hash the client's executable lists (<see cref="Db2CatalogEntry.TableHash"/>), at the
    /// entry offset where those hashes match best (MEASURED on 1.60.1: every entry matches a 1.60 table), and the record ID
    /// follows it.
    /// </summary>
    private Dictionary<string, List<int>> Hotfixes()
    {
        if (_hotfixes != null) return _hotfixes;
        _hotfixes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var path = UpdateCatalogSource.HotfixCache(_s.Copy);
        if (path == null) return _hotfixes;
        byte[] d;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            d = new byte[fs.Length];
            fs.ReadExactly(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return _hotfixes; }
        var byHash = _s.Client.Catalog.Entries.Where(e => e.TableHash != 0).GroupBy(e => e.TableHash).ToDictionary(g => g.Key, g => g.First().Name);
        var pos = new List<int>();
        var magic = "XFTH"u8;
        for (int p = 4; p + 4 <= d.Length;)
        {
            int i = d.AsSpan(p).IndexOf(magic);
            if (i < 0) break;
            pos.Add(p + i);
            p += i + 4;
        }
        int best = -1, bestHits = 0;
        for (int off = 4; off <= 28; off += 4)
        {
            int hits = pos.Count(p => p + off + 8 <= d.Length && byHash.ContainsKey(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + off))));
            if (hits > bestHits) { bestHits = hits; best = off; }
        }
        if (best < 0) return _hotfixes;
        foreach (var p in pos)
        {
            if (p + best + 8 > d.Length) continue;
            if (!byHash.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + best)), out var name)) continue;
            if (!_hotfixes.TryGetValue(name, out var l)) _hotfixes[name] = l = new List<int>();
            l.Add(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + best + 4)));
        }
        return _hotfixes;
    }

    // ------------------------------------------------------------------ data tables

    /// <summary>Tables whose rows the server has its own copy of (spells, items, factions, talents, skills, quests,
    /// flight paths...): bringing them over needs the server too.</summary>
    private static bool ServerTable(string n) =>
        Regex.IsMatch(n, "^(Spell|Item|Faction|Talent|SkillLine|SkillRace|Quest|Taxi|CreatureFamily|CreatureSpell|Lock|Emotes|CharStartOutfit|ChrClasses|ChrRaces|Map|AreaTable|AreaTrigger|WorldSafeLocs|GameObject|Creature|Mail|Bank|Durability|AuctionHouse|Vehicle|Achievement)", RegexOptions.CultureInvariant);

    /// <summary>The 1.12 tables 1.60 replaced (DIFF_112_160 5.1; FORMAT KNOWLEDGE / INFERRED by name and content).</summary>
    private static readonly Dictionary<string, string> ReplacedBy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CharSections"] = "the ChrCustomization tables", ["CharHairTextures"] = "the ChrCustomization tables", ["CharVariations"] = "the ChrCustomization tables",
        ["HelmetGeosetVisData"] = "HelmetGeosetData", ["LightIntBand"] = "LightData", ["LightFloatBand"] = "LightData",
        ["SoundEntries"] = "the SoundKit tables", ["SoundWaterType"] = "the SoundKit tables", ["SoundSamplePreferences"] = "the SoundKit tables",
        ["UISoundLookups"] = "the SoundKit tables", ["SheatheSoundLookups"] = "the SoundKit tables", ["SpellIcon"] = "FileDataID columns",
        ["WorldMapArea"] = "the UiMap tables", ["WorldMapContinent"] = "the UiMap tables", ["WorldMapOverlay"] = "the UiMap tables",
    };

    /// <summary>The first word of a table name (Spell, Item, Garr...), for grouping the new tables.</summary>
    public static string TableGroup(string n)
    {
        var m = Regex.Match(n, @"^([A-Z][a-z0-9]*|[A-Z]+(?=[A-Z][a-z]|$|_))");
        var g = m.Success ? m.Value : n;
        return g switch
        {
            "Garr" or "Follower" or "Mission" => "Garrison",
            "Ui" or "UI" => "UI",
            "Chr" => "Chr (characters, classes, races, customization)",
            _ => g,
        };
    }

    private void ScanTables()
    {
        var catalog = _s.Client.Catalog;
        var names160 = catalog.Entries.Select(e => e.Name).ToList();
        Report($"Reading {N(names160.Count)} data tables of {_s.Copy.Name}...");
        Parallel.ForEach(names160, Par, n =>
        {
            var t = OpenTable(n);
            if (t != null) _rows160[n] = (t.Records.Count, t.SkippedRecords);
        });
        var c = _s.Client112;
        var dbcNames = c.Glob(@"DBFilesClient\*.dbc").Select(p => Path.GetFileNameWithoutExtension(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dbc = new DbcService(c, null);
        var layouts160 = Db2Layouts.ForBuild(_s.Copy.Build);
        var hot = Hotfixes();
        foreach (var n in dbcNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = c.Read($@"DBFilesClient\{n}.dbc");
            int rows112 = bytes != null && bytes.Length >= 20 ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) : 0;
            var entry = catalog.Get(n);
            if (entry == null)
            {
                Add(new UpdateItem
                {
                    Id = "table:" + IdOf(n), Kind = UpdateKind.DataTables, Where = n, Title = $"Data table {n}: not in 1.60",
                    Files = rows112, Class = Portability.NoVisibleChange,
                    ClassReason = ReplacedBy.TryGetValue(n, out var by) ? $"1.60 keeps this data in {by}; your 1.12 table stays." : "1.60 has no table of this name; your 1.12 table stays.",
                    Facts = { $"{N(rows112)} rows in your game." },
                    NotYet = "nothing to bring.",
                });
                continue;
            }
            if (!_rows160.TryGetValue(n, out var r160))
            {
                Add(new UpdateItem
                {
                    Id = "table:" + IdOf(n), Kind = UpdateKind.DataTables, Where = n, Title = $"Data table {n}: not readable here",
                    Files = rows112, Class = ServerTable(n) ? Portability.NeedsServer : Portability.NeedsConverter,
                    ClassReason = "the 1.60 table is not on this computer (or is encrypted), so it could not be compared.",
                    NotYet = "the 1.60 table could not be read.",
                });
                continue;
            }
            var item = new UpdateItem
            {
                Id = "table:" + IdOf(n), Kind = UpdateKind.DataTables, Where = n, Title = $"Data table {n}",
                Files = Math.Max(rows112, r160.Rows), Bytes = Size160(entry.FileDataId),
                Class = ServerTable(n) ? Portability.NeedsServer : Portability.NeedsConverter,
                ClassReason = (ServerTable(n) ? "the server has its own copy of these rows, and " : "") +
                              "1.60's table is a modern DB2 file with other columns: bringing rows over needs a converter (DB2 to 1.12's DBC, per table), which does not exist yet.",
                NotYet = "the DB2 to DBC converter does not exist yet.",
            };
            item.Facts.Add($"{N(rows112)} rows in your game, {N(r160.Rows)} in 1.60{(r160.Encrypted > 0 ? $" (plus {N(r160.Encrypted)} encrypted rows that cannot be read)" : "")}.");
            bool compared = false;
            if (layouts160?.Get(n) != null && !n.Equals("Light", StringComparison.OrdinalIgnoreCase))
            {
                try { compared = CompareRows(n, dbc, item); }
                catch (Exception e) when (e is DbcException or Db2FormatException or InvalidDataException or KeyNotFoundException) { item.Facts.Add($"The rows could not be compared: {e.Message}"); }
            }
            if (!compared)
            {
                if (rows112 == r160.Rows) item.Facts.Add("The rows themselves were not compared: the app does not know this table's 1.60 columns yet.");
                item.Changes.Add(r160.Rows > rows112 ? $"With the converter: up to {N(r160.Rows - rows112)} more rows (by count; the rows were not compared)." : "With the converter: the 1.60 rows (not compared row by row).");
            }
            if (n.Equals("Light", StringComparison.OrdinalIgnoreCase)) item.Facts.Add("Compared light zone by light zone under Sky and lighting.");
            if (hot.TryGetValue(n, out var hl)) item.Facts.Add($"{N(hl.Count)} rows were hotfixed by Blizzard's server (see Hotfixed rows).");
            if (!compared && rows112 == r160.Rows && !hot.ContainsKey(n)) item.Title += " (same number of rows)";
            Add(item);
        }

        // tables only 1.60 has, grouped by their first word
        var known = dbcNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = names160.Where(n => !known.Contains(n)).GroupBy(TableGroup).ToList();
        // systems with one or two tables are listed together
        var small = groups.Where(g => g.Count() < 3).SelectMany(g => g).ToList();
        var lists = groups.Where(g => g.Count() >= 3).Select(g => (Key: g.Key, Tables: g.ToList())).ToList();
        if (small.Count > 0) lists.Add(("other systems", small));
        foreach (var (key, tables) in lists.OrderBy(g => g.Key == "other systems" ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var g = (Key: key, Count: tables.Count);
            var list = tables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            int rows = list.Sum(x => _rows160.GetValueOrDefault(x).Rows), empty = list.Count(x => _rows160.GetValueOrDefault(x).Rows == 0);
            // a system 1.12 has (its tables start with the same word: Spell, Item, Creature, Sound...): 1.60 split or extended it
            var word = g.Key.Split(' ')[0];
            bool family = key != "other systems" && word != "UI" && dbcNames.Any(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase));
            var cls = !family ? Portability.NeedsClientCode : ServerTable(word) ? Portability.NeedsServer : Portability.NeedsConverter;
            Add(new UpdateItem
            {
                Id = "tables:new:" + IdOf(word), Kind = UpdateKind.DataTables, Where = g.Key, Title = $"New data tables: {g.Key} ({N(list.Count)})",
                Files = rows, Bytes = list.Sum(x => catalog.Get(x) is { } e ? Size160(e.FileDataId) : 0), Class = cls,
                ClassReason = cls switch
                {
                    Portability.NeedsClientCode => "tables of systems the 1.12 client does not have: the 1.12 game cannot use them without client code (and a converter).",
                    Portability.NeedsServer => $"new tables of a system 1.12 has ({word}): 1.60 keeps more of its data, split into more tables. Bringing it over needs a converter to 1.12's tables, and the server's own copy of the data.",
                    _ => $"new tables of a system 1.12 has ({word}): 1.60 keeps more of its data, split into more tables. Bringing it over needs a converter to 1.12's tables; parts may need client code.",
                },
                Changes = { $"{(cls == Portability.NeedsClientCode ? "With client code" : "With a converter")}: the {g.Key} data of 1.60 ({N(rows)} rows)." },
                Facts =
                {
                    $"{N(list.Count)} tables, {N(rows)} rows{(empty > 0 ? $"; {N(empty)} tables are empty (shipped with the engine, not used by 1.60)" : "")}.",
                    string.Join(", ", list.Take(30).Select(x => $"{x} ({N(_rows160.GetValueOrDefault(x).Rows)})")) + (list.Count > 30 ? ", ..." : ""),
                },
                NotYet = cls == Portability.NeedsClientCode ? "needs client code." : "the DB2 to DBC converter does not exist yet.",
            });
        }

        // hotfixes
        foreach (var (table, ids) in hot.OrderByDescending(kv => kv.Value.Count))
            Add(new UpdateItem
            {
                Id = "hotfix:" + IdOf(table), Kind = UpdateKind.DataTables, Where = table, Title = $"Hotfixed rows: {table} ({N(ids.Count)})",
                Files = ids.Distinct().Count(), Class = Portability.NeedsServer,
                ClassReason = "Blizzard's server sent these rows after the client was built; the client keeps them in its hotfix cache, not in the shipped table. Only a server sends rows like these (a private server sends its own).",
                Facts = { $"{N(ids.Count)} entries in the client's hotfix cache (Cache\\ADB\\enUS\\DBCache.bin) for {N(ids.Distinct().Count())} rows, for example {string.Join(", ", ids.Distinct().Take(8))}." },
                NotYet = "server data: the cache is incomplete and its contents are not decoded.",
            });

        SkyboxItems(dbc);
        LightingTableItems();
        WeatherItem();
        StringItems();
        FontItems();
        MovieItem();
    }

    /// <summary>The rows of a table whose columns are named on both sides: added, removed and changed rows (columns
    /// matched by name; numbers compared as numbers, texts without case).</summary>
    private bool CompareRows(string n, DbcService dbc, UpdateItem item)
    {
        var t160 = OpenTable(n, named: true);
        if (t160 == null || t160.Layout == null) return false;
        var t112 = dbc.LoadGame(n);
        if (!t112.Layout.HasKey) return false;
        var cols = new List<(DbcColumn C112, int C160)>();
        foreach (var c in t112.Layout.Columns)
        {
            if (c.Fields != 1 || c.Name.Equals("ID", StringComparison.OrdinalIgnoreCase) || c.Name.StartsWith("col_", StringComparison.Ordinal)) continue;
            int i = -1;
            for (int k = 0; k < t160.Columns.Count; k++)
                if (t160.Columns[k].NameVerified && string.Equals(t160.Columns[k].Name, c.Name, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
            if (i >= 0) cols.Add((c, i));
        }
        if (cols.Count == 0) return false;
        var ids112 = t112.Rows.Select(r => t112.Key(r) ?? 0).ToHashSet();
        var ids160 = t160.Records.Select(r => (long)r.Id).ToHashSet();
        int added = ids160.Count(i => !ids112.Contains(i)), removed = ids112.Count(i => !ids160.Contains(i)), changed = 0;
        var perCol = new Dictionary<string, int>();
        foreach (var r in t112.Rows)
        {
            if (t112.Key(r) is not { } id || t160.Get((int)id) is not { } r160) continue;
            bool any = false;
            foreach (var (c, i) in cols)
            {
                var a = t112.Format(r, c);
                var b = r160.Text(i);
                if (SameValue(a, b)) continue;
                any = true;
                perCol[c.Name] = perCol.GetValueOrDefault(c.Name) + 1;
            }
            if (any) changed++;
        }
        item.Changes.Add($"With the converter: {N(added)} new rows, {N(changed)} changed rows ({N(removed)} of your rows are not in 1.60).");
        item.Facts.Add($"Compared on {N(cols.Count)} columns both sides name: {string.Join(", ", cols.Select(x => x.C112.Name).Take(12))}{(cols.Count > 12 ? ", ..." : "")}.");
        if (perCol.Count > 0) item.Facts.Add("Changed most: " + string.Join(", ", perCol.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} ({N(kv.Value)} rows)")) + ".");
        if (added > 0)
        {
            var label = t160.Columns.Select((col, k) => (col, k)).FirstOrDefault(x => x.col.Type == Db2ValueType.String && x.col.NameVerified);
            var newIds = t160.Records.Where(r => !ids112.Contains(r.Id)).Take(8).Select(r => label.col != null ? $"{r.Id} {r.String(label.k)}" : r.Id.ToString(CultureInfo.InvariantCulture));
            item.Facts.Add("New rows, for example: " + string.Join(", ", newIds) + ".");
        }
        if (added == 0 && changed == 0 && removed == 0) item.Title += " (no changed rows on the named columns)";
        return true;
    }

    private static bool SameValue(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return Math.Abs(x - y) <= 1e-3 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        return false;
    }

    // ------------------------------------------------------------------ sky, light, weather

    private void SkyboxItems(DbcService dbc)
    {
        var t = OpenTable("LightSkybox");
        if (t == null) return;
        int nameCol = StringColumn(t);
        if (nameCol < 0) return;
        // the model column: the one whose values are files of the client
        int fdidCol = -1;
        for (int i = 0; i < t.Columns.Count && fdidCol < 0; i++)
            if (i != nameCol && t.Records.Count(r => r.Int(i) > 0) > 0 && t.Records.Where(r => r.Int(i) > 0).All(r => _s.Casc.Root.Contains(r.UInt(i)))) fdidCol = i;
        var sky112 = dbc.LoadGame("LightSkybox");
        var nameCol112 = sky112.Layout.Columns.FirstOrDefault(c => c.IsString);
        var have = nameCol112 == null ? new HashSet<string>() : sky112.Rows.Select(r => ClientData.ResolveName(sky112.Text(r, nameCol112))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in t.Records)
        {
            var name = r.String(nameCol) ?? "";
            var path = ClientData.ResolveName(name);
            if (name.Length == 0 || have.Contains(path)) continue;
            uint model = fdidCol >= 0 ? r.UInt(fdidCol) : 0;
            bool have112 = name.Contains('\\') && _s.Client112.Exists(path);
            Add(new UpdateItem
            {
                Id = $"sky:{r.Id}", Kind = UpdateKind.Skybox, Where = name, Title = $"Skybox: {(name.Contains('\\') ? Path.GetFileNameWithoutExtension(name) : name)}",
                Files = model != 0 ? 1 : 0, Bytes = model != 0 ? Size160(model) : 0, Class = Portability.NeedsConverter,
                ClassReason = have112
                    ? "your game has this sky model, but no LightSkybox row for it: the row and the light parameters that show it come from 1.60's light tables, which need the light converter."
                    : "a sky model 1.12 does not have: a modern M2 file (it needs the model converter) and a LightSkybox row, plus the light zones that show it.",
                Changes = { have112 ? "With the light converter: this sky in the light zones that use it." : "With the model converter: a new sky for the light zones that use it." },
                Needs = have112
                    ? new List<string> { "A LightSkybox row and the light parameters that name it (Sky and lighting)." }
                    : new List<string> { "The M2 converter (version 272 to 256) for its model" + (model != 0 ? $" (FileDataID {model})" : "") + ".", "A LightSkybox row and the light parameters that name it (Sky and lighting)." },
                Facts = { $"LightSkybox row {r.Id}: \"{name}\"{(have112 ? "; the model is in your game" : "")}." },
                NotYet = have112 ? "the light converter does not exist yet." : "the model converter does not exist yet.",
            });
        }
    }

    private static readonly (string Table, string Title, Portability Class, string Reason)[] LightTables =
    {
        ("LightData", "Light colours, fog and sky per time of day (LightData)", Portability.NeedsConverter, "1.60 keeps the colours of every light zone per time of day in LightData; 1.12 keeps them in LightIntBand and LightFloatBand. A converter between them is needed (its columns are not decoded yet); the modern fog values (heights, densities) have no 1.12 place."),
        ("LightParams", "Light parameters (LightParams)", Portability.NeedsConverter, "the per-light settings (skybox, glow, water and cloud values) in another layout than 1.12's LightParams: a converter is needed."),
        ("LightDataGlobalVolumeFog", "Volumetric fog (LightDataGlobalVolumeFog)", Portability.NeedsClientCode, "volumetric fog is a feature of the modern renderer."),
        ("LightShaft", "Sun shafts (LightShaft)", Portability.NeedsClientCode, "light shafts are a feature of the modern renderer."),
        ("LightParamsLightShaft", "Sun shafts per light (LightParamsLightShaft)", Portability.NeedsClientCode, "light shafts are a feature of the modern renderer."),
        ("LightWorldShadow", "World shadows per light (LightWorldShadow)", Portability.NeedsClientCode, "the sun's shadows on the world are a feature of the modern renderer."),
        ("WorldShadow", "World shadow settings (WorldShadow)", Portability.NeedsClientCode, "the sun's shadows on the world are a feature of the modern renderer."),
        ("SSAOSettings", "Ambient occlusion settings (SSAOSettings)", Portability.NeedsClientCode, "ambient occlusion is a feature of the modern renderer."),
        ("TerrainColorGradingRamp", "Colour grading (TerrainColorGradingRamp)", Portability.NeedsClientCode, "colour grading is a feature of the modern renderer."),
        ("MapRenderScale", "Render scale per map (MapRenderScale)", Portability.NeedsClientCode, "render scale is a feature of the modern renderer."),
        ("VolumeFogCondition", "Fog volume conditions (VolumeFogCondition)", Portability.NeedsClientCode, "fog volumes are a feature of the modern renderer."),
        ("ZoneLight", "Zone lights (ZoneLight)", Portability.NeedsClientCode, "polygon-shaped light zones; 1.12 knows only spheres (a lossy converter to 1.12 Light rows would be possible)."),
        ("ZoneLightPoint", "Zone light outlines (ZoneLightPoint)", Portability.NeedsClientCode, "the outline points of the polygon-shaped light zones."),
        ("Lightning", "Lightning (Lightning)", Portability.NeedsClientCode, "lightning of the modern weather system."),
    };

    private void LightingTableItems()
    {
        var hot = Hotfixes();
        foreach (var (table, title, cls, reason) in LightTables)
        {
            if (!_rows160.TryGetValue(table, out var r)) continue;
            var item = new UpdateItem
            {
                Id = "lighting:" + IdOf(table), Kind = UpdateKind.SkyLighting, Where = table, Title = title, Files = r.Rows,
                Bytes = _s.Client.Catalog.Get(table) is { } e ? Size160(e.FileDataId) : 0, Class = cls, ClassReason = reason,
                Facts = { $"{N(r.Rows)} rows in 1.60{(r.Encrypted > 0 ? $" (and {N(r.Encrypted)} encrypted)" : "")}; 1.12 has no such table." },
                NotYet = cls == Portability.NeedsClientCode ? "needs client code." : "the light converter does not exist yet.",
            };
            if (hot.TryGetValue(table, out var hl)) item.Facts.Add($"{N(hl.Count)} rows hotfixed by Blizzard's server (the lighting is still being tuned).");
            Add(item);
        }
    }

    private void WeatherItem()
    {
        var parts = new[] { "Weather", "WeatherXParticulate", "Particulate", "ParticulateSound" }.Where(_rows160.ContainsKey).ToList();
        if (parts.Count == 0) return;
        var hot = Hotfixes();
        Add(new UpdateItem
        {
            Id = "weather", Kind = UpdateKind.Weather, Where = "Weather", Title = "Weather types and particle volumes",
            Files = parts.Sum(p => _rows160[p].Rows), Class = Portability.NeedsClientCode,
            ClassReason = "1.60 describes weather in tables (types, particle volumes, sounds) that its engine reads; 1.12's weather is a server message and shaders built into the client (rain, snow, sand). It needs client code, and the server decides when the weather changes.",
            Changes = { "With client code and the server: the 1.60 weather types (storms, particle volumes) in your world." },
            Needs = { "Client code for the weather tables.", "Server weather that uses the new types." },
            Facts = parts.Select(p => $"{p}: {N(_rows160[p].Rows)} rows" + (hot.TryGetValue(p, out var h) ? $", {N(h.Count)} hotfixed" : "")).ToList(),
            NotYet = "needs client code.",
        });
    }

    // ------------------------------------------------------------------ interface strings, fonts, movies

    private static readonly Regex LuaString = new("^\\s*([A-Za-z0-9_]+)\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*;?\\s*$", RegexOptions.CultureInvariant);

    /// <summary>The KEY = "text"; lines of 1.12's GlobalStrings.lua (escapes \" \\ \n undone).</summary>
    public static Dictionary<string, string> ParseGlobalStrings(string lua)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lua.Replace("\r\n", "\n").Split('\n'))
        {
            var m = LuaString.Match(raw);
            if (!m.Success) continue;
            d[m.Groups[1].Value] = Unescape(m.Groups[2].Value);
        }
        return d;
    }

    /// <summary>Lua string escapes undone (\n, \", \\); the client's GlobalStrings table keeps them as in the Lua file.</summary>
    public static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

    private void StringItems()
    {
        var lua = _s.Client112.Read(@"Interface\FrameXML\GlobalStrings.lua");
        var t = OpenTable("GlobalStrings");
        if (lua == null || t == null) return;
        int tag = StringColumn(t, 0), text = StringColumn(t, 1);
        if (tag < 0 || text < 0) return;
        var s112 = ParseGlobalStrings(Encoding.UTF8.GetString(lua));
        var s160 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in t.Records)
            if (r.String(tag) is { Length: > 0 } k) s160.TryAdd(k, Unescape(r.String(text) ?? ""));
        var changed = s112.Where(kv => s160.TryGetValue(kv.Key, out var v) && v != kv.Value).ToList();
        var added = s160.Where(kv => !s112.ContainsKey(kv.Key)).ToList();
        var gone = s112.Keys.Where(k => !s160.ContainsKey(k)).ToList();
        const string why = "the app cannot write your interface's GlobalStrings.lua yet (and 1.60 texts may use markup or format codes 1.12's interface code does not expect).";
        if (changed.Count > 0)
            Add(new UpdateItem
            {
                Id = "strings:changed", Kind = UpdateKind.InterfaceStrings, Where = "GlobalStrings", Title = $"Changed interface texts ({N(changed.Count)})",
                Files = changed.Count, Class = Portability.Ready,
                ClassReason = "plain text: 1.12's interface shows these keys, so new wording works as it is (where the format codes such as %s and %d stay the same).",
                Changes = { $"New wording for {N(changed.Count)} of your interface texts." },
                Facts = changed.Take(6).Select(kv => $"{kv.Key}: \"{Short(kv.Value)}\" -> \"{Short(s160[kv.Key])}\"").ToList(),
                NotYet = why,
            });
        if (added.Count > 0)
            Add(new UpdateItem
            {
                Id = "strings:new", Kind = UpdateKind.InterfaceStrings, Where = "GlobalStrings", Title = $"New interface texts ({N(added.Count)})",
                Files = added.Count, Class = Portability.Ready,
                ClassReason = "plain text; 1.12's interface code does not use these keys, so they matter only with new interface code.",
                Facts = added.Take(6).Select(kv => $"{kv.Key}: \"{Short(kv.Value)}\"").ToList(),
                NotYet = why,
            });
        if (gone.Count > 0)
            Add(new UpdateItem
            {
                Id = "strings:gone", Kind = UpdateKind.InterfaceStrings, Where = "GlobalStrings", Title = $"Interface texts 1.60 no longer has ({N(gone.Count)})",
                Files = gone.Count, Class = Portability.NoVisibleChange, ClassReason = "keys of your interface that 1.60 does not have; your texts stay.",
                Facts = { string.Join(", ", gone.Take(10)) + (gone.Count > 10 ? ", ..." : "") },
                NotYet = "nothing to bring.",
            });
    }

    private static string Short(string s) => (s.Length > 60 ? s[..57] + "..." : s).Replace("\n", " / ");

    /// <summary>The full name of a TrueType font (its 'name' table, name 4, Windows Unicode or Mac Roman), or null.</summary>
    public static string? FontName(byte[] d)
    {
        try
        {
            if (d.Length < 12) return null;
            int tables = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
            for (int i = 0; i < tables; i++)
            {
                int e = 12 + 16 * i;
                if (e + 16 > d.Length) return null;
                if (Encoding.ASCII.GetString(d, e, 4) != "name") continue;
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(e + 8));
                int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off + 2));
                int strings = off + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off + 4));
                string? mac = null;
                for (int k = 0; k < count; k++)
                {
                    int r = off + 6 + 12 * k;
                    int platform = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(r)), nameId = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(r + 6));
                    int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(r + 8)), so = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(r + 10));
                    if (nameId != 4 || strings + so + len > d.Length) continue;
                    if (platform == 3) return Encoding.BigEndianUnicode.GetString(d, strings + so, len);
                    if (platform == 1) mac ??= Encoding.Latin1.GetString(d, strings + so, len);
                }
                return mac;
            }
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    private static bool IsFont(byte[] d) => d.Length > 12 && (BinaryPrimitives.ReadUInt32BigEndian(d) is 0x00010000 or 0x74727565 or 0x4F54544F or 0x74746366);

    /// <summary>New fonts: the TrueType files next to 1.12's own fonts (their FileDataIDs were given out together).</summary>
    private void FontItems()
    {
        var mine = _fontFdids.ToList();
        if (mine.Count == 0) return;
        uint lo = mine.Min() > 64 ? mine.Min() - 64 : 0, hi = mine.Max() + 64;
        var found = new List<(uint Fdid, string Name, long Size)>();
        for (uint f = lo; f <= hi; f++)
        {
            if (_fdids112.Contains(f) || !_s.Casc.Root.Contains(f) || _s.Casc.State(f) != CascFileState.Local) continue;
            if (_s.Casc.SizeOf(f) is < 1000 or > 64_000_000) continue;
            byte[]? d;
            try { d = _s.Casc.ReadFileDataId(f); }
            catch (Exception e) when (e is CascMissingException or CascEncryptedException) { continue; }
            if (d == null || !IsFont(d)) continue;
            found.Add((f, FontName(d) ?? "(no name)", d.LongLength));
        }
        if (found.Count == 0) return;
        Add(new UpdateItem
        {
            Id = "fonts:new", Kind = UpdateKind.Fonts, Where = "Fonts", Title = $"New fonts ({N(found.Count)})",
            Files = found.Count, Bytes = found.Sum(f => f.Size), Class = Portability.Ready,
            ClassReason = "TrueType fonts (Cyrillic and Asian letters, for example), which 1.12 can load as they are.",
            Changes = { "With a file name your interface uses: more letters and languages." },
            Facts = found.Select(f => $"#{f.Fdid}: {f.Name} ({UpdateNames.Size(f.Size)})").ToList(),
            NotYet = "1.12's interface finds fonts by file name, and these have no name in the client (a listfile you choose names them).",
            FileList = found.Select(f => new UpdateFile($@"Fonts\{f.Fdid}.ttf", f.Fdid, false, f.Size, f.Name)).ToList(),
        });
    }

    private void MovieItem()
    {
        var t = OpenTable("MovieFileData");
        if (t == null || t.Records.Count == 0) return;
        var files = t.Records.Select(r => (uint)r.Id).Where(f => _s.Casc.Root.Contains(f)).ToList();
        if (files.Count == 0) return;
        Add(new UpdateItem
        {
            Id = "movies", Kind = UpdateKind.Other, Where = "Movies", Title = $"Movies ({N(files.Count)})",
            Files = files.Count, Bytes = files.Sum(Size160), Class = Portability.NeedsConverter,
            ClassReason = "AVI movies in the client's storage (yours are loose files in Data\\Interface\\Cinematics); whether 1.12's movie player plays their codec was not checked, and the modern Movie tables pick them.",
            Facts = files.Select(f => $"#{f}: {UpdateNames.Size(Size160(f))}{(_s.Era?.Root.Contains(f) == true ? " (also in Classic Era)" : " (only in 1.60)")}").ToList(),
            NotYet = "not verified that 1.12 plays them.",
        });
    }
}
