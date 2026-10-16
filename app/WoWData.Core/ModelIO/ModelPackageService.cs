using WoWData.Core.Blp;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace WoWData.Core.ModelIO;

/// <summary>What a model is in the game (decides the looks offered and the warnings).</summary>
public enum ModelSubject { Plain, Creature, Npc, Character, Item }

/// <summary>
/// The model export / import of one opened game folder: collects the looks of a model (creature displays, item
/// displays, a character look) for the export, describes who is affected by a mesh change, and gives the import its
/// view of the game data (your edits first). Used by the app and by the tests.
/// </summary>
public sealed class ModelPackageService
{
    public ClientData Client { get; }
    public Catalog Catalog { get; }
    public AssetIndex? Index { get; }
    public Workspace.Workspace Workspace { get; }
    public ModelResolver Resolver { get; }
    public TextureService Textures { get; }

    public ModelPackageService(ClientData client, Catalog catalog, AssetIndex? index, Workspace.Workspace workspace, ModelResolver resolver, TextureService textures)
    {
        Client = client;
        Catalog = catalog;
        Index = index;
        Workspace = workspace;
        Resolver = resolver;
        Textures = textures;
    }

    /// <summary>The model's bytes as the app uses them (your edit first).</summary>
    public byte[]? ReadModel(string path) => Workspace.ReadEdited(path) ?? Client.Read(path);

    public ModelImportContext ImportContext() => new()
    {
        ReadCurrent = ReadModel,
        ReadGame = p => Textures.ReadOriginal(p),
        Textures = Textures,
        Index = Index,
        Animations = Resolver.Animations,
    };

    public PackageTextureOriginal? DescribeTexture(string path)
    {
        var edited = Workspace.ReadEdited(path);
        var orig = Textures.ReadOriginal(path);
        var data = edited ?? orig?.Data;
        if (data == null) return null;
        try
        {
            var b = BlpFile.Parse(data);
            return new PackageTextureOriginal
            {
                Archive = orig?.Archive, Source = edited != null ? "workspace" : "game", Sha256 = ModelExporter.Sha(data), Format = b.FormatName,
                Width = b.Width, Height = b.Height, Levels = b.LevelCount, AlphaDepth = b.AlphaDepth,
            };
        }
        catch (BlpException)
        {
            return null;
        }
    }

    private static string SkinShortName(string model, string skin)
    {
        var stem = Path.GetFileNameWithoutExtension(model);
        var s = skin;
        if (s.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) s = s[stem.Length..];
        if (s.StartsWith("Skin", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        s = s.TrimStart('_');
        return s.Length == 0 ? skin : s;
    }

    public ModelSubject SubjectOf(string model)
    {
        if (Resolver.RaceOfModel(model) != null) return ModelSubject.Character;
        if (Index != null && Index.CreatureDisplaysUsingModel(model).Count > 0) return ModelSubject.Creature;
        if (Index != null && Index.ItemDisplaysUsingModel(model).Count > 0) return ModelSubject.Item;
        return ModelSubject.Plain;
    }

    /// <summary>Every look of the model (creature displays / item displays), or the given character look, or the plain
    /// model. <paramref name="currentDisplay"/> marks the one shown in the app.</summary>
    public (List<ExportLook> Looks, int Current) Looks(string model, ModelSubject subject, long? currentDisplay = null, CharacterLook? characterLook = null, List<long>? npcDisplays = null)
    {
        var looks = new List<ExportLook>();
        int current = 0;
        switch (subject)
        {
            case ModelSubject.Character:
            {
                var rs = Resolver.RaceOfModel(model) ?? (0, 0);
                var look = characterLook ?? new CharacterLook();
                looks.Add(new ExportLook
                {
                    Name = $"Character look (skin {look.Skin}, face {look.Face}, hair {look.HairStyle}/{look.HairColor}, facial hair {look.FacialHair})", Kind = "character look",
                    Look = Resolver.Character(rs.Item1, rs.Item2, look), Detail = "the character-creation choices shown in the app",
                });
                break;
            }
            case ModelSubject.Npc:
                foreach (var id in npcDisplays ?? (currentDisplay is long d ? new List<long> { d } : new List<long>()))
                {
                    var names = Index?.CreatureNamesForDisplay(id) ?? new List<string>();
                    looks.Add(new ExportLook { Name = names.Count > 0 ? $"{names[0]} (NPC look)" : $"Display {id} (NPC look)", Kind = "creature display", Ids = { id }, Look = Resolver.CreatureDisplay((int)id) });
                    if (id == currentDisplay) current = looks.Count - 1;
                }
                break;
            case ModelSubject.Creature when Index != null:
            {
                var displays = Index.CreatureDisplaysUsingModel(model);
                var names = Index.HasServer ? Index.CreatureNamesForDisplays(displays.Select(x => x.Id)) : new Dictionary<long, string>();
                foreach (var (id, skins, _) in displays)
                {
                    var skinText = string.Join(" / ", skins.Where(s => s.Length > 0).Select(s => SkinShortName(model, s)));
                    string who = names.TryGetValue(id, out var n) ? n : $"Display {id}";
                    looks.Add(new ExportLook
                    {
                        Name = skinText.Length > 0 ? $"{who} ({skinText})" : who, Kind = "creature display", Ids = { id },
                        Detail = "display " + id, Look = Resolver.CreatureDisplay((int)id),
                    });
                    if (id == currentDisplay) current = looks.Count - 1;
                }
                break;
            }
            case ModelSubject.Item when Index != null:
            {
                var displays = Index.ItemDisplaysUsingModel(model).GroupBy(x => x.Id).Select(g => g.First()).ToList();
                var names = Index.HasServer ? Index.ItemNamesForDisplays(displays.Select(x => x.Id)) : new Dictionary<long, string>();
                foreach (var (id, tex) in displays)
                {
                    string who = names.TryGetValue(id, out var n) ? n : $"Item display {id}";
                    looks.Add(new ExportLook
                    {
                        Name = $"{who} ({Path.GetFileNameWithoutExtension(tex)})", Kind = "item display", Ids = { id }, Detail = $"item display {id}",
                        Look = Resolver.ItemDisplay((int)id, model),
                    });
                    if (id == currentDisplay) current = looks.Count - 1;
                }
                break;
            }
        }
        if (looks.Count == 0) looks.Add(new ExportLook { Name = Path.GetFileNameWithoutExtension(model), Kind = "model", Look = Resolver.DefaultFor(model) });
        return (looks, current);
    }

    /// <summary>(summary, count, warning) of who a change of the model's mesh affects.</summary>
    public (string Summary, int Count, string? Warning) Users(string model, ModelSubject subject)
    {
        string file = Path.GetFileName(model);
        if (subject == ModelSubject.Character || Resolver.RaceOfModel(model) is not null)
        {
            var rs = Resolver.RaceOfModel(model);
            string race = rs == null ? "character" : (Resolver.Races.FirstOrDefault(r => r.Id == rs.Value.Race)?.Name ?? "character").ToLowerInvariant();
            string sex = rs?.Sex == 1 ? "female" : "male";
            return ($"every {race} {sex} player and NPC", 2,
                $"This changes every {race} {sex} player and NPC: {file} is the one model of all of them (and of the NPCs built on it). Character edits are risky: test them in game before you share them.");
        }
        if (Index == null) return ($"everything that uses {file}", 1, null);
        var cd = Index.CreatureDisplaysUsingModel(model);
        var id = Index.ItemDisplaysUsingModel(model).Select(x => x.Id).Distinct().ToList();
        if (cd.Count > 0)
        {
            var names = Index.HasServer ? Index.CreatureNamesForDisplays(cd.Select(x => x.Id)).Values.Distinct().ToList() : new List<string>();
            string who = names.Count > 0 ? $" ({string.Join(", ", names.Take(4))}{(names.Count > 4 ? ", ..." : "")})" : "";
            return ($"{cd.Count} creature display{(cd.Count == 1 ? "" : "s")}{who}", cd.Count,
                cd.Count > 1 ? $"A mesh change affects {cd.Count} creature displays{who}: they all use {file}." : null);
        }
        if (id.Count > 0)
            return ($"{id.Count} item display{(id.Count == 1 ? "" : "s")}", id.Count, id.Count > 1 ? $"A mesh change affects {id.Count} item displays: they all use {file}." : null);
        return ($"every use of {file} (world placements, game objects or spell effects)", 1, $"A mesh change affects every place that shows {file}.");
    }

    /// <summary>0.8.0 (moved from the app): a free package folder, &lt;dir&gt;\&lt;Model&gt;_export, or _export_2, _3... when it exists.</summary>
    public static string NewPackageFolder(string dir, string model)
    {
        var name = ModelExporter.SafeName(Path.GetFileNameWithoutExtension(model)) + "_export";
        var p = Path.Combine(dir, name);
        for (int k = 2; Directory.Exists(p); k++) p = Path.Combine(dir, $"{name}_{k}");
        return p;
    }

    /// <summary>Build and write an export package.</summary>
    public ModelExportResult Export(string model, ModelSubject subject, ModelExportMode mode, string outputDir, long? currentDisplay = null,
        CharacterLook? characterLook = null, HashSet<int>? visibleGeosets = null, bool includeAnimations = true, List<long>? npcDisplays = null, string? packageFolder = null,
        bool includeSkeleton = false)
    {
        var data = ReadModel(model) ?? throw new IOException($"{model} is not in the game archives.");
        var game = Client.Read(model);
        var (looks, current) = Looks(model, subject, currentDisplay, characterLook, npcDisplays);
        var (summary, count, warning) = Users(model, subject);
        if (visibleGeosets == null && looks.Count > 0) visibleGeosets = looks[Math.Clamp(current, 0, looks.Count - 1)].Look.Geosets;
        var req = new ModelExportRequest
        {
            ModelPath = ClientData.ResolveName(model), ModelData = data, Source = Workspace.IsModified(model) ? "workspace" : "game", Archive = Catalog.ArchiveOf(model),
            GameSha256 = game == null ? null : ModelExporter.Sha(game), Looks = looks, CurrentLook = current, Mode = mode, VisibleGeosets = visibleGeosets,
            AnimationName = Resolver.AnimationName, LoadTexture = Resolver.LoadBlp, DescribeTexture = DescribeTexture, IncludeAnimations = includeAnimations,
            OutputDir = outputDir, PackageFolder = packageFolder, UsersSummary = summary, UsersCount = count, UsersWarning = warning, IsCharacter = subject == ModelSubject.Character,
            IncludeSkeleton = includeSkeleton, Animations = Resolver.Animations,
        };
        return ModelExporter.Export(req);
    }
}
