using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWData.Core.Versions.Catalog;

/// <summary>What an update of the catalog is about.</summary>
public enum UpdateKind
{
    ZoneTerrain,
    NewMap,
    SkyLighting,
    Skybox,
    Weather,
    GroundTextures,
    Textures,
    Models,
    Buildings,
    InterfaceArt,
    InterfaceStrings,
    Fonts,
    Sounds,
    DataTables,
    EngineFeatures,
    Other,
}

/// <summary>
/// The user's portability classes (docs\research\DIFF_112_160.md section 6): what it takes to bring an update into the
/// 1.12 game. <see cref="NoVisibleChange"/> marks differences that are listed so the user knows them, but bring nothing
/// (a file only re-encoded or re-saved in the modern format).
/// </summary>
public enum Portability
{
    /// <summary>Data that 1.12 loads as it is, or after a conversion the app already does.</summary>
    Ready,
    /// <summary>Needs a format converter that does not exist yet (models, buildings, sounds, data tables...).</summary>
    NeedsConverter,
    /// <summary>Only meaningful with server data (spells, items, hotfixes, weather state).</summary>
    NeedsServer,
    /// <summary>An engine or interface feature of the modern client: needs work on the 1.12 client itself (not importable).</summary>
    NeedsClientCode,
    /// <summary>A difference without a visible change (re-encoded, re-saved): nothing to bring.</summary>
    NoVisibleChange,
}

/// <summary>Display names and parsing of <see cref="UpdateKind"/> and <see cref="Portability"/> (the app and wds show the same words).</summary>
public static class UpdateNames
{
    public static string Of(UpdateKind k) => k switch
    {
        UpdateKind.ZoneTerrain => "Zone terrain",
        UpdateKind.NewMap => "New map",
        UpdateKind.SkyLighting => "Sky and lighting",
        UpdateKind.Skybox => "Skyboxes",
        UpdateKind.Weather => "Weather",
        UpdateKind.GroundTextures => "Ground textures",
        UpdateKind.Textures => "Textures",
        UpdateKind.Models => "Models",
        UpdateKind.Buildings => "Buildings (WMO)",
        UpdateKind.InterfaceArt => "Interface art",
        UpdateKind.InterfaceStrings => "Interface strings",
        UpdateKind.Fonts => "Fonts",
        UpdateKind.Sounds => "Sounds and music",
        UpdateKind.DataTables => "Data tables",
        UpdateKind.EngineFeatures => "Engine and UI features",
        _ => "Other files",
    };

    public static string Of(Portability p) => p switch
    {
        Portability.Ready => "Ready",
        Portability.NeedsConverter => "Needs converter",
        Portability.NeedsServer => "Needs server",
        Portability.NeedsClientCode => "Needs client code",
        _ => "No visible change",
    };

    /// <summary>The short words wds accepts (and prints in --json): zone-terrain, new-map, ... / ready, needs-converter ...</summary>
    public static string Key(UpdateKind k) => Kebab(k.ToString());
    public static string Key(Portability p) => Kebab(p.ToString());

    private static string Kebab(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append('-');
            sb.Append(char.ToLowerInvariant(s[i]));
        }
        return sb.ToString();
    }

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A kind from what a person types: its words ("zone terrain", "Sky and lighting"), its key ("zone-terrain")
    /// or the start of either ("terrain" also matches, "sky"); null when nothing matches.</summary>
    public static UpdateKind? ParseKind(string text)
    {
        var t = Norm(text);
        if (t.Length == 0) return null;
        var all = Enum.GetValues<UpdateKind>();
        foreach (var k in all) if (Norm(Of(k)) == t || Norm(k.ToString()) == t) return k;
        var hits = all.Where(k => Norm(Of(k)).StartsWith(t, StringComparison.Ordinal) || Norm(k.ToString()).Contains(t, StringComparison.Ordinal)).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    public static Portability? ParseClass(string text)
    {
        var t = Norm(text);
        if (t.Length == 0) return null;
        var all = Enum.GetValues<Portability>();
        foreach (var p in all) if (Norm(Of(p)) == t || Norm(p.ToString()) == t) return p;
        var hits = all.Where(p => Norm(Of(p)).Contains(t, StringComparison.Ordinal)).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>"1.2 MB", "340 KB", "12 bytes".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 0 => "?",
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
    };
}

/// <summary>A file an update brings: the path it gets in the 1.12 game, the FileDataID it is read from (the high-resolution
/// variant when <see cref="HighRes"/>), its size in the source and a note.</summary>
public sealed record UpdateFile(string Path, uint FileDataId, bool HighRes, long Size, string Note);

/// <summary>The terrain an update converts: a map, and a zone of it (with its subzones) or a list of tiles (x_y), or the
/// whole map (a new map).</summary>
public sealed record TerrainPick(int MapId, string MapName, string MapDirectory, int? ZoneId, string? ZoneName, List<string> Tiles, bool WholeMap);

/// <summary>
/// One difference between the source client and the user's 1.12 game, grouped so it can be picked on its own: a zone's
/// terrain, the changed textures of a folder, a data table, an engine feature. Everything in it was measured by the scan
/// (<see cref="UpdateCatalogScanner"/>), except the engine features' descriptions, which name the exe strings found.
/// </summary>
public sealed class UpdateItem
{
    /// <summary>Stable ID ("terrain:0:12", "tex:Creature", "table:Spell"): the same for the same difference in every scan.</summary>
    public string Id { get; set; } = "";
    public UpdateKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>The zone, map, folder or table it is about.</summary>
    public string Where { get; set; } = "";
    /// <summary>Files it brings (or rows, for a table; tiles for terrain): the count shown in the list.</summary>
    public int Files { get; set; }
    /// <summary>Size in the source (decoded bytes).</summary>
    public long Bytes { get; set; }
    public Portability Class { get; set; }
    /// <summary>Why it has that class, in plain words.</summary>
    public string ClassReason { get; set; } = "";
    /// <summary>What it would change in the user's game.</summary>
    public List<string> Changes { get; set; } = new();
    /// <summary>What it needs (other updates, converters, server steps).</summary>
    public List<string> Needs { get; set; } = new();
    /// <summary>Measured details.</summary>
    public List<string> Facts { get; set; } = new();
    /// <summary>Null when the app can import it; else why not yet.</summary>
    public string? NotYet { get; set; }
    /// <summary>For terrain updates: what the map conversion converts.</summary>
    public TerrainPick? Terrain { get; set; }
    /// <summary>For file updates: the files.</summary>
    public List<UpdateFile>? FileList { get; set; }

    [JsonIgnore] public bool Importable => NotYet == null;
    [JsonIgnore] public string KindText => UpdateNames.Of(Kind);
    [JsonIgnore] public string ClassText => UpdateNames.Of(Class);
    [JsonIgnore] public string SizeText => UpdateNames.Size(Bytes);

    /// <summary>The item in plain lines (the details pane and "wds versions update-info").</summary>
    public List<string> Lines(int maxFiles = 20)
    {
        var l = new List<string>
        {
            Title,
            $"{KindText} · {Where} · {Files:N0} {(Terrain != null ? "tile" : Kind == UpdateKind.DataTables ? "row" : "file")}{(Files == 1 ? "" : "s")} · {SizeText} · {ClassText}",
            "",
            $"{ClassText}: {ClassReason}",
        };
        if (Changes.Count > 0) { l.Add(""); l.Add("What it changes in your game:"); l.AddRange(Changes.Select(c => "  " + c)); }
        if (Needs.Count > 0) { l.Add(""); l.Add("What it needs:"); l.AddRange(Needs.Select(c => "  " + c)); }
        if (Facts.Count > 0) { l.Add(""); l.Add("Measured:"); l.AddRange(Facts.Select(c => "  " + c)); }
        l.Add("");
        l.Add(NotYet == null ? "Import: yes (Preview import shows what it writes)." : "Import: not yet. " + NotYet);
        if (FileList is { Count: > 0 } files && maxFiles > 0)
        {
            l.Add("");
            l.Add($"Files ({files.Count:N0}{(files.Count > maxFiles ? $", the first {maxFiles}" : "")}):");
            foreach (var f in files.Take(maxFiles)) l.Add($"  {f.Path}  (#{f.FileDataId}{(f.HighRes ? ", high resolution" : "")}, {UpdateNames.Size(f.Size)}){(f.Note.Length > 0 ? "  " + f.Note : "")}");
        }
        return l;
    }
}

/// <summary>What to show of a catalog: a kind, a class, a zone/map/folder text, a search text.</summary>
public sealed class UpdateFilter
{
    public UpdateKind? Kind { get; init; }
    public Portability? Class { get; init; }
    /// <summary>Part of the zone, map, folder or table name (case-insensitive).</summary>
    public string? Where { get; init; }
    /// <summary>Words that must all appear in the title, the place, the ID or the facts.</summary>
    public string? Search { get; init; }
    /// <summary>Leave out the differences without a visible change.</summary>
    public bool HideNoChange { get; init; }
    /// <summary>Only what the app can import now.</summary>
    public bool OnlyImportable { get; init; }

    public bool Matches(UpdateItem i)
    {
        if (Kind is { } k && i.Kind != k) return false;
        if (Class is { } c && i.Class != c) return false;
        if (HideNoChange && i.Class == Portability.NoVisibleChange) return false;
        if (OnlyImportable && !i.Importable) return false;
        if (!string.IsNullOrWhiteSpace(Where) && !i.Where.Contains(Where.Trim(), StringComparison.OrdinalIgnoreCase)
            && !(i.Terrain?.ZoneName?.Contains(Where.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)
            && !(i.Terrain?.MapName.Contains(Where.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            foreach (var w in Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) && !i.Where.Contains(w, StringComparison.OrdinalIgnoreCase)
                    && !i.Id.Contains(w, StringComparison.OrdinalIgnoreCase) && !i.Facts.Any(f => f.Contains(w, StringComparison.OrdinalIgnoreCase))
                    && !i.KindText.Contains(w, StringComparison.OrdinalIgnoreCase))
                    return false;
        }
        return true;
    }
}

/// <summary>
/// 0.14.0 (experimental): the update catalog: every measured difference between another client (the "1.60" Classic
/// client first) and the user's 1.12 game, grouped into updates the user can pick (<see cref="UpdateItem"/>). Built by
/// <see cref="UpdateCatalogScanner"/>, kept in the app's folder by <see cref="UpdateCatalogStore"/> (rebuilt when either side
/// changes), imported by <see cref="UpdateImporter"/>.
/// </summary>
public sealed class UpdateCatalog
{
    /// <summary>Bumped when the scan changes: a cached catalog of another version is scanned again.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    /// <summary>The source client's folder name and version ("_classic_beta_, 1.60.1.70450").</summary>
    public string Source { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    /// <summary>The user's 1.12 game (its Data folder).</summary>
    public string Target { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string TargetFingerprint { get; set; } = "";
    public DateTime Scanned { get; set; }
    public double Seconds { get; set; }
    /// <summary>Where the names came from, in words.</summary>
    public string NamesText { get; set; } = "";
    public List<UpdateItem> Items { get; set; } = new();
    /// <summary>Notes about the scan (what could not be measured).</summary>
    public List<string> Notes { get; set; } = new();

    private Dictionary<string, UpdateItem>? _byId;

    public UpdateItem? Find(string id)
    {
        _byId ??= Items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byId.GetValueOrDefault(id.Trim());
    }

    public IEnumerable<UpdateItem> Filter(UpdateFilter f) => Items.Where(f.Matches);

    /// <summary>Items per kind and class.</summary>
    public SortedDictionary<UpdateKind, SortedDictionary<Portability, int>> Counts()
    {
        var res = new SortedDictionary<UpdateKind, SortedDictionary<Portability, int>>();
        foreach (var i in Items)
        {
            if (!res.TryGetValue(i.Kind, out var d)) res[i.Kind] = d = new SortedDictionary<Portability, int>();
            d[i.Class] = d.GetValueOrDefault(i.Class) + 1;
        }
        return res;
    }

    /// <summary>"412 updates: 120 Ready, ..." and one line per kind.</summary>
    public List<string> SummaryLines()
    {
        var l = new List<string>();
        var byClass = Items.GroupBy(i => i.Class).OrderBy(g => g.Key).Select(g => $"{g.Count():N0} {UpdateNames.Of(g.Key)}");
        l.Add($"{Items.Count:N0} differences between {Source} and your 1.12 game: {string.Join(", ", byClass)}; {Items.Count(i => i.Importable):N0} can be imported now.");
        foreach (var (k, d) in Counts())
            l.Add($"  {UpdateNames.Of(k)}: {d.Values.Sum():N0} ({string.Join(", ", d.Select(kv => $"{kv.Value:N0} {UpdateNames.Of(kv.Key)}"))})");
        return l;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir != null) Directory.CreateDirectory(dir);
        var tmp = file + ".tmp";
        using (var s = File.Create(tmp)) JsonSerializer.Serialize(s, this, Json);
        File.Move(tmp, file, true);
    }

    public static UpdateCatalog Load(string file)
    {
        using var s = File.OpenRead(file);
        return JsonSerializer.Deserialize<UpdateCatalog>(s, Json) ?? throw new InvalidDataException($"{file} is empty.");
    }
}
