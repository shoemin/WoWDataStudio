using System.Globalization;
using System.Text;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Dbc;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions.Catalog;

public sealed partial class UpdateCatalogScanner
{
    /// <summary>Names from the source client's own files, each confirmed by the source root's name hash for that
    /// FileDataID (ManifestInterfaceData: the client's list of its interface files).</summary>
    private readonly Dictionary<uint, string> _ownNames = new();
    private readonly HashSet<uint> _listfileNamed = new();

    // ------------------------------------------------------------------ light zones per zone

    private (int Zone, bool Found) ZoneAtWorld(int map, float a, float b, IReadOnlyDictionary<int, SourceArea> areas)
    {
        int tx = (int)MathF.Floor(a / TerrainMath.TileSize), ty = (int)MathF.Floor(b / TerrainMath.TileSize);
        if (tx is < 0 or > 63 || ty is < 0 or > 63 || !_tileAreas.TryGetValue((map, tx, ty), out var ar)) return (NoZone, false);
        int cx = Math.Clamp((int)((a / TerrainMath.TileSize - tx) * 16), 0, 15), cy = Math.Clamp((int)((b / TerrainMath.TileSize - ty) * 16), 0, 15);
        return (TopZone(areas, ar[cy * 16 + cx]), true);
    }

    /// <summary>
    /// The light zones (Light rows: a sphere with a falloff) of every compared map, zone by zone. Positions: 1.60 stores
    /// world coordinates in yards; 1.12 stores X = (17066.67 - worldY) x 36, Y = height x 36, Z = (17066.67 - worldX) x 36
    /// (db2_layouts_70450.txt [Light], 339 of 361 rows); the zone is the one of the ground chunk under the centre.
    /// </summary>
    private void LightItems(DbcService dbc, IReadOnlyDictionary<int, SourceArea> areas)
    {
        var t = OpenTable("Light", named: true);
        if (t?.Layout == null) { Note("Light: the 1.60 Light table has no named columns here, so the light zones were not compared."); return; }
        int cx = t.ColumnIndex("X"), cy = t.ColumnIndex("Y"), cz = t.ColumnIndex("Z"), cfe = t.ColumnIndex("FalloffEnd"), cm = t.ColumnIndex("ContinentID");
        var l112 = dbc.LoadGame("Light");
        DbcColumn C(string n) => l112.Layout.Column(n) ?? throw new KeyNotFoundException($"Light.dbc has no column {n}");
        var lx = C("X"); var ly = C("Y"); var lz = C("Z"); var lfe = C("FalloffEnd"); var lm = C("ContinentID");
        const float Half = TerrainMath.MapHalf;
        var l60 = t.Records.Select(r => (Id: r.Id, Map: r.Int(cm), A: Half - r.Float(cy), B: Half - r.Float(cx), H: r.Float(cz), R: r.Float(cfe),
            Default: r.Float(cx) == 0 && r.Float(cy) == 0 && r.Float(cz) == 0)).Where(l => !l.Default && _comparedMaps.ContainsKey(l.Map)).ToList();
        var l12 = l112.Rows.Select(r => (Id: (int)(l112.Key(r) ?? 0), Map: (int)l112.Number(r, lm), A: l112.Float(r, lx) / 36f, B: l112.Float(r, lz) / 36f, H: l112.Float(r, ly) / 36f,
            R: l112.Float(r, lfe) / 36f, Default: l112.Float(r, lx) == 0 && l112.Float(r, ly) == 0 && l112.Float(r, lz) == 0)).Where(l => !l.Default && _comparedMaps.ContainsKey(l.Map)).ToList();
        var acc = new Dictionary<(int Map, int Zone), (int Now, int Same, int Resized, int Added, int Removed, int Hot, List<string> ZoneLights)>();
        (int, int, int, int, int, int, List<string>) Get((int, int) k) => acc.TryGetValue(k, out var v) ? v : (0, 0, 0, 0, 0, 0, new List<string>());
        var used = new HashSet<int>();
        var hot = Hotfixes().GetValueOrDefault("Light")?.ToHashSet() ?? new HashSet<int>();
        foreach (var l in l60)
        {
            var (zone, _) = ZoneAtWorld(l.Map, l.A, l.B, areas);
            var k = (l.Map, zone);
            var v = Get(k);
            v.Item1++;
            int best = -1;
            float bestD = 2f;
            for (int i = 0; i < l12.Count; i++)
            {
                if (l12[i].Map != l.Map || used.Contains(i)) continue;
                float d = MathF.Sqrt((l12[i].A - l.A) * (l12[i].A - l.A) + (l12[i].B - l.B) * (l12[i].B - l.B) + (l12[i].H - l.H) * (l12[i].H - l.H));
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0)
            {
                used.Add(best);
                if (MathF.Abs(l12[best].R - l.R) > 1f) v.Item3++;
                else v.Item2++;
            }
            else v.Item4++;
            if (hot.Contains(l.Id)) v.Item6++;
            acc[k] = v;
        }
        for (int i = 0; i < l12.Count; i++)
        {
            if (used.Contains(i)) continue;
            var (zone, _) = ZoneAtWorld(l12[i].Map, l12[i].A, l12[i].B, areas);
            var v = Get((l12[i].Map, zone));
            v.Item5++;
            acc[(l12[i].Map, zone)] = v;
        }
        // zone lights (polygons), by the zone named in them
        var zl = OpenTable("ZoneLight");
        if (zl != null && StringColumn(zl) is var zn and >= 0)
        {
            int zm = Enumerable.Range(0, zl.Columns.Count).FirstOrDefault(i => i != zn && zl.Columns[i].Type != Db2ValueType.String && zl.Columns[i].Type != Db2ValueType.Float);
            foreach (var r in zl.Records)
            {
                var name = (r.String(zn) ?? "").Trim();
                int map = r.Int(zm);
                if (!_comparedMaps.ContainsKey(map)) continue;
                var zone = areas.Values.Where(a => a.MapId == map && a.ParentId == 0 && a.Name.Length > 3 && name.Contains(a.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Name.Length).FirstOrDefault();
                var k = (map, zone?.Id ?? NoZone);
                var v = Get(k);
                v.Item7.Add(name);
                acc[k] = v;
            }
        }
        foreach (var ((map, zone), v) in acc.OrderBy(kv => kv.Key.Map).ThenBy(kv => kv.Key.Zone))
        {
            if (v.Resized + v.Added + v.Removed + v.Hot + v.ZoneLights.Count == 0) continue;
            string mapName = _comparedMaps[map].Name;
            string zoneName = zone == NoZone ? "outside the zones" : areas.TryGetValue(zone, out var za) ? za.Name : $"area {zone}";
            var item = new UpdateItem
            {
                Id = $"light:{map}:{(zone == NoZone ? "none" : zone.ToString(CultureInfo.InvariantCulture))}", Kind = UpdateKind.SkyLighting,
                Where = $"{zoneName} ({mapName})", Title = $"{zoneName}: sky and light zones", Files = v.Now, Class = Portability.NeedsConverter,
                ClassReason = "1.12's Light table can take the light zones (where a sky and light setting applies, and how far); their colours, fog and sky are in 1.60's LightParams and LightData tables, whose columns are not decoded yet: a converter to 1.12's LightParams, LightIntBand and LightFloatBand is needed. Polygon-shaped zone lights and volumetric fog need client code.",
                NotYet = "the light converter (LightData to LightIntBand and LightFloatBand) does not exist yet.",
            };
            item.Changes.Add($"1.60 has {N(v.Now)} light zone{(v.Now == 1 ? "" : "s")} here: {N(v.Added)} new, {N(v.Resized)} with another size, {N(v.Same)} at the same place and size; {N(v.Removed)} of yours are not in 1.60.");
            item.Changes.Add("The colours of the light zones were not compared (they are in tables whose columns are not decoded yet): the zones 1.60 re-lit carry new colours even where the zone kept its place.");
            if (v.ZoneLights.Count > 0) item.Facts.Add($"Zone lights (polygons, client code): {string.Join(", ", v.ZoneLights.Distinct().Take(6))}.");
            if (v.Hot > 0) item.Facts.Add($"{N(v.Hot)} of these light rows were hotfixed by Blizzard's server.");
            item.Needs.Add("The light converter for the colours; the Light rows themselves fit 1.12's table.");
            Add(item);
        }
    }

    // ------------------------------------------------------------------ new named files

    private void ScanNewNamedFiles()
    {
        var casc = _s.Casc;
        var t = OpenTable("ManifestInterfaceData");
        if (t != null && StringColumn(t, 0) is var dc and >= 0 && StringColumn(t, 1) is var fc and >= 0)
        {
            int confirmed = 0, other = 0;
            foreach (var r in t.Records)
            {
                var dir = (r.String(dc) ?? "").Replace('/', '\\').TrimEnd('\\');
                var file = r.String(fc) ?? "";
                if (file.Length == 0) continue;
                var path = dir.Length > 0 ? dir + "\\" + file : file;
                if (casc.Root.FileDataIdOfHash(CascNameHash.Of(path)) == (uint)r.Id) { _ownNames[(uint)r.Id] = path; confirmed++; }
                else other++;
            }
            Note($"Interface file names from the client's own list (ManifestInterfaceData): {N(confirmed)} confirmed by the client's name hashes{(other > 0 ? $", {N(other)} not confirmed and not used" : "")}.");
        }
        var art = _ownNames.Where(kv => !_fdids112.Contains(kv.Key) && kv.Value.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) && !_s.Client112.Exists(kv.Value)
                                        && casc.State(kv.Key) == CascFileState.Local)
            .Select(kv => (Path: kv.Value, Fdid: kv.Key, Size: Size160(kv.Key))).ToList();
        foreach (var (key, g) in Groups(art, a => a.Path, 4000, 800))
            Add(new UpdateItem
            {
                Id = "ui:new:" + IdOf(key), Kind = UpdateKind.InterfaceArt, Where = key, Title = $"New interface art: {key}",
                Files = g.Count, Bytes = g.Sum(a => a.Size), Class = Portability.Ready,
                ClassReason = "interface textures (BLP) that 1.12 loads as they are (made at most 512 x 512); your interface shows them only where its code names them (new frames, icons in data tables).",
                Changes = { $"Adds {N(g.Count)} interface textures to your game (they show only where your interface or tables name them)." },
                Facts = { "For example: " + string.Join(", ", g.Take(5).Select(a => Path.GetFileName(a.Path))) + "." },
                FileList = g.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Select(a => new UpdateFile(a.Path, a.Fdid, false, a.Size, "")).ToList(),
            });

        if (casc.ListfileNameCount == 0) return;
        // a listfile the user chose names the rest: new files by type and folder
        var named = new List<(string Path, uint Fdid, long Size)>();
        foreach (var f in casc.FileDataIds())
        {
            if (_fdids112.Contains(f) || _ownNames.ContainsKey(f)) continue;
            var n = casc.NameOf(f);
            if (n == null) continue;
            _listfileNamed.Add(f);
            var u = n.ToUpperInvariant();
            if (u.StartsWith(@"WORLD\MAPS\", StringComparison.Ordinal) || u.StartsWith(@"WORLD\MINIMAPS\", StringComparison.Ordinal) || u.StartsWith(@"DBFILESCLIENT\", StringComparison.Ordinal)) continue;
            if (_s.Client112.Exists(n)) continue;
            named.Add((n.Replace('/', '\\'), f, Size160(f)));
        }
        foreach (var byType in named.GroupBy(x => Path.GetExtension(x.Path).ToLowerInvariant()))
        {
            var (kind, cls, what, notYet) = byType.Key switch
            {
                ".blp" => (UpdateKind.Textures, Portability.Ready, "New textures", (string?)null),
                ".m2" => (UpdateKind.Models, Portability.NeedsConverter, "New models", "the model converter does not exist yet."),
                ".skin" or ".anim" or ".bone" or ".skel" => (UpdateKind.Models, Portability.NeedsConverter, "Model parts", "the model converter does not exist yet."),
                ".wmo" => (UpdateKind.Buildings, Portability.NeedsConverter, "New buildings", "the building converter does not exist yet."),
                ".ogg" => (UpdateKind.Sounds, Portability.NeedsConverter, "New sounds (OGG)", "1.12 plays WAV and MP3; the OGG converter does not exist yet."),
                ".mp3" => (UpdateKind.Sounds, Portability.Ready, "New music and voices (MP3)", null),
                ".ttf" => (UpdateKind.Fonts, Portability.Ready, "New fonts", null),
                ".lua" or ".xml" or ".toc" => (UpdateKind.EngineFeatures, Portability.NeedsClientCode, "Interface code", "the modern interface code needs the modern client API."),
                _ => (UpdateKind.Other, Portability.NeedsConverter, "New files (" + byType.Key.TrimStart('.') + ")", "not known whether 1.12 can use them."),
            };
            foreach (var (key, g) in Groups(byType, x => x.Path, 2500, 150))
                Add(new UpdateItem
                {
                    Id = $"listfile:{IdOf(byType.Key.TrimStart('.'))}:{IdOf(key)}", Kind = kind, Where = key, Title = $"{what}: {key}",
                    Files = g.Count, Bytes = g.Sum(x => x.Size), Class = cls,
                    ClassReason = (cls == Portability.Ready ? "files 1.12 loads as they are (textures made at most 512 x 512)" : $"files of the modern client: {notYet}") + "; named by your listfile.",
                    Changes = { $"Adds {N(g.Count)} files your game does not have (they show only where your game names them)." },
                    Facts = { "For example: " + string.Join(", ", g.Take(5).Select(x => Path.GetFileName(x.Path))) + "." },
                    NotYet = notYet,
                    FileList = g.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => new UpdateFile(x.Path, x.Fdid, false, x.Size, "")).ToList(),
                });
        }
    }

    // ------------------------------------------------------------------ files without a name, encrypted files

    private void ScanUnnamed()
    {
        var casc = _s.Casc;
        int unnamed = 0, inEra = 0, encrypted = 0, notLocal = 0, twins = 0;
        long bytes = 0, encBytes = 0, twinBytes = 0;
        CascStorage? hi = null;
        try { hi = _s.HighRes; }
        catch (Exception e) when (e is IOException or InvalidDataException or CascMissingException) { }
        foreach (var f in casc.FileDataIds())
        {
            var st = casc.State(f);
            if (st == CascFileState.Encrypted) { encrypted++; encBytes += Size160(f); continue; }
            if (st == CascFileState.NotLocal) { notLocal++; continue; }
            if (_fdids112.Contains(f) || _ownNames.ContainsKey(f) || _listfileNamed.Contains(f)) continue;
            unnamed++;
            long s = Size160(f);
            bytes += s;
            if (_s.Era?.Root.Contains(f) == true) inEra++;
            if (hi != null && casc.Root.TryGetContentKey(f, out var a) && hi.Root.TryGetContentKey(f, out var b) && !a.SequenceEqual(b)) { twins++; twinBytes += Math.Max(0, hi.SizeOf(f)); }
        }
        if (unnamed > 0)
            Add(new UpdateItem
            {
                Id = "other:unnamed", Kind = UpdateKind.Other, Where = "(no names)", Title = $"New files without a name ({N(unnamed)})",
                Files = unnamed, Bytes = bytes, Class = Portability.NeedsConverter,
                ClassReason = "files of 1.60 that neither your 1.12 game nor your listfile names: models, textures, sounds and tables the modern client finds by number. Without a name the app cannot tell where 1.12 would look for them; most are modern formats.",
                Changes = { "With names (a listfile you choose) the catalog lists them by folder." },
                Facts =
                {
                    $"{N(unnamed)} files ({UpdateNames.Size(bytes)}): {N(inEra)} of them are also in Classic Era (with the same number), {N(unnamed - inEra)} are not.",
                    $"{N(twins)} of them are textures with a high-resolution version ({UpdateNames.Size(twinBytes)} more).",
                    "Some of them are what the terrain, the tables and the interface of 1.60 use (they are counted under those updates too).",
                },
                NotYet = "no name: choose a listfile (Choose listfile...) to have them listed by folder.",
            });
        if (encrypted > 0)
            Add(new UpdateItem
            {
                Id = "other:encrypted", Kind = UpdateKind.Other, Where = "(encrypted)", Title = $"Encrypted files ({N(encrypted)})",
                Files = encrypted, Bytes = encBytes, Class = Portability.NeedsServer,
                ClassReason = "content Blizzard has not released yet: the files are encrypted, and the key comes from Blizzard's servers with a later update. The app never decrypts.",
                NotYet = "encrypted.",
            });
        if (notLocal > 0) Note($"{N(notLocal)} files of {_s.Copy.Name} are not downloaded (Battle.net fetches them when the game needs them); they are left out.");
    }

    // ------------------------------------------------------------------ engine features

    private static readonly (string Id, string Title, string[] Needles, string What)[] EngineList =
    {
        ("renderer", "A modern renderer (Direct3D 11 and 12)", new[] { "D3D12", "D3D11" }, "the 1.12 renderer is Direct3D 9 / OpenGL with fixed-function and assembly shaders; 1.60 draws with Direct3D 11/12 and compiled modern shaders."),
        ("ssao", "Ambient occlusion (SSAO)", new[] { "ssao", "SSAO" }, "shading in corners and creases, computed on screen."),
        ("shadows-rt", "Ray-traced shadows", new[] { "shadowrt", "raytracingSupport" }, "shadows from the sun traced with the graphics card's ray tracing."),
        ("shadows", "Soft cascaded shadows of the world", new[] { "shadowNumCascades", "CascadedShadowMap" }, "real-time shadows of the world (1.12 has baked ground shadows and blob shadows)."),
        ("sunshafts", "Sun shafts", new[] { "sunShafts" }, "light rays through trees and clouds."),
        ("fog", "Volumetric fog", new[] { "volumeFog", "FogVolume" }, "fog with depth and height that light shines through."),
        ("sky", "Procedural sky", new[] { "skyNoiseStrength" }, "a computed sky that skybox models are blended with."),
        ("water", "PBR water", new[] { "pbrLiquidDetail" }, "physically based water with simulated waves (FFT ocean)."),
        ("reflections", "Water reflections and refraction", new[] { "reflectionMode" }, "the world reflected in water."),
        ("aa", "Anti-aliasing (MSAA, CMAA2, FXAA)", new[] { "MSAAQuality", "ffxAntiAliasingMode", "CMAA2" }, "smoothed edges."),
        ("renderscale", "Render scale and resampling", new[] { "RenderScale", "ResampleQuality" }, "drawing at another resolution than the window."),
        ("vrs", "Variable-rate shading", new[] { "vrsValar" }, "less shading work where the eye does not notice."),
        ("hdr", "HDR output", new[] { "useHDRBrightness" }, "high dynamic range screens."),
        ("multithread", "Multithreaded rendering", new[] { "gxMT" }, "drawing on several processor cores."),
        ("colourgrading", "Colour grading", new[] { "TerrainColorGrading" }, "a colour look per zone."),
        ("lua51", "Lua 5.1 for the interface", new[] { "Lua 5.1" }, "1.12's interface runs on Lua 5.0."),
        ("slug", "Slug font rendering", new[] { ".slug", "Slug" }, "fonts drawn on the graphics card (the .slug caches of the TTF files)."),
        ("fmod", "The modern sound engine (FMOD Studio)", new[] { "FMOD" }, "1.12 uses FMOD 3; 1.60's sound engine plays OGG and sound kits."),
        ("customization", "Character customization", new[] { "ChrCustomization", "C_CharacterCreation" }, "the modern character creation (choices, materials, geosets) instead of 1.12's CharSections."),
        ("editmode", "Edit mode (moving the interface)", new[] { "C_EditMode" }, "players move and resize the interface frames."),
        ("ui-api", "The modern interface API (C_ namespaces, mixins)", new[] { "C_TradeSkillUI", "Mixin" }, "the modern interface (auction house, professions, collections, calendar, communities...) is written against an API the 1.12 client does not have: none of its add-ons runs on 1.12 as it is."),
        ("collections", "Collections and transmogrification", new[] { "C_Transmog", "C_MountJournal" }, "mount, pet and appearance collections."),
        ("social", "Communities and voice chat", new[] { "C_Club", "C_VoiceChat" }, "guild communities, Battle.net chat and voice."),
    };

    private static int CountOf(byte[] hay, byte[] needle, int max = 1000)
    {
        int n = 0, p = 0;
        while (n < max)
        {
            int i = hay.AsSpan(p).IndexOf(needle);
            if (i < 0) break;
            n++;
            p += i + 1;
        }
        return n;
    }

    private static byte[]? ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[fs.Length];
            fs.ReadExactly(b);
            return b;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void ScanEngine()
    {
        var exe160 = ReadShared(_s.Copy.Exe);
        var exe112 = ReadShared(Path.Combine(Path.GetDirectoryName(_s.Client112.DataDir.TrimEnd('\\', '/')) ?? "", "WoW.exe")) ?? Array.Empty<byte>();
        if (exe160 == null) { Note($"{_s.Copy.Exe} could not be read: the engine features are not listed."); return; }
        string exeName = Path.GetFileName(_s.Copy.Exe);
        foreach (var (id, title, needles, what) in EngineList)
        {
            var found = needles.Select(n => (n, c160: CountOf(exe160, Encoding.ASCII.GetBytes(n)), c112: CountOf(exe112, Encoding.ASCII.GetBytes(n)))).ToList();
            if (found.All(f => f.c160 == 0) || found.Any(f => f.c160 > 0 && f.c112 > 0 && f.n is not "FMOD")) continue;
            Add(new UpdateItem
            {
                Id = "engine:" + id, Kind = UpdateKind.EngineFeatures, Where = exeName, Title = title, Class = Portability.NeedsClientCode,
                ClassReason = "a feature of the modern client's program: " + what + " It needs work on the 1.12 client itself (the decomp project); listed so you know, it cannot be imported.",
                Facts = { $"Found in {exeName}: " + string.Join(", ", found.Where(f => f.c160 > 0).Select(f => $"\"{f.n}\" ({f.c160}x)")) + (exe112.Length > 0 ? "; not in your WoW.exe" + (found.Any(f => f.c112 > 0) ? " (FMOD 3 only)" : "") + "." : ".") },
                NotYet = "an engine feature (client code).",
            });
        }
        // the interface API: the C_ namespaces the program offers
        var ns = new HashSet<string>(StringComparer.Ordinal);
        var cu = "C_"u8;
        for (int p = 0; p < exe160.Length;)
        {
            int i = exe160.AsSpan(p).IndexOf(cu);
            if (i < 0) break;
            int s = p + i;
            p = s + 2;
            if (s > 0 && (char.IsAsciiLetterOrDigit((char)exe160[s - 1]) || exe160[s - 1] == '_')) continue;
            int e = s + 2;
            while (e < exe160.Length && e - s < 48 && char.IsAsciiLetterOrDigit((char)exe160[e])) e++;
            if (e - s >= 5 && char.IsAsciiLetterUpper((char)exe160[s + 2])) ns.Add(Encoding.ASCII.GetString(exe160, s, e - s));
        }
        if (ns.Count > 0)
            Add(new UpdateItem
            {
                Id = "engine:ui-namespaces", Kind = UpdateKind.EngineFeatures, Where = exeName, Title = $"Interface API namespaces ({N(ns.Count)})",
                Files = ns.Count, Class = Portability.NeedsClientCode,
                ClassReason = "the modern interface calls the game through these namespaces (C_Item, C_QuestLog...); 1.12 has none. A 1.60 add-on runs on 1.12 only after these exist in the client or the add-on is rewritten against the 1.12 API.",
                Facts = { string.Join(", ", ns.OrderBy(x => x, StringComparer.Ordinal).Take(60)) + (ns.Count > 60 ? ", ..." : "") },
                NotYet = "an engine feature (client code).",
            });
        if (_uiCode112 > 0)
            Add(new UpdateItem
            {
                Id = "engine:ui-code", Kind = UpdateKind.EngineFeatures, Where = "Interface", Title = "The interface code (FrameXML, GlueXML and add-ons)",
                Files = _uiCode112, Class = Portability.NeedsClientCode,
                ClassReason = "1.60 ships the modern (Retail) interface, not a Classic one: its Lua and XML need the modern client API, and your 1.12 interface code (FrameXML, GlueXML, the Blizzard add-ons) stays as it is.",
                Facts = { $"Your game has {N(_uiCode112)} interface code files (Lua, XML, TOC)." },
                NotYet = "needs client code or a rewrite against the 1.12 API.",
            });
        if (_shaders112 > 0)
            Add(new UpdateItem
            {
                Id = "engine:shaders", Kind = UpdateKind.EngineFeatures, Where = "Shaders", Title = "Shaders",
                Files = _shaders112, Class = Portability.NeedsClientCode,
                ClassReason = "your game's shaders (Shaders\\*.bls) are ARB/NV assembly programs; 1.60 has compiled shader containers for its modern renderer, which 1.12 cannot use.",
                Facts = { $"Your game has {N(_shaders112)} shader files; 1.60 has none of these names." },
                NotYet = "an engine feature (client code).",
            });
    }
}
