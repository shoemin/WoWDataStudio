using WoWData.Core.Mpq;
using WoWData.Core.Workspace;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Maps;

/// <summary>One file found by "Import map changes".</summary>
public sealed class MapFileChange
{
    public string GamePath { get; init; } = "";
    public string SourceFile { get; init; } = "";
    /// <summary>adt, wdt, wdl, or null for a file the import does not handle.</summary>
    public string? Kind { get; init; }
    public (int X, int Y)? Tile { get; init; }
    public byte[] NewBytes { get; init; } = Array.Empty<byte>();
    /// <summary>What the app has now: your earlier import of this file, else the game's file (null: a new file).</summary>
    public byte[]? BaseBytes { get; init; }
    public string BaseLabel { get; init; } = "";
    /// <summary>The game's own file (null: the game has none).</summary>
    public byte[]? GameBytes { get; init; }
    public string? GameArchive { get; init; }
    public MapReport Validation { get; init; } = new();
    public MapDiffResult? Diff { get; init; }
    public bool Unchanged { get; init; }
    public bool Include { get; set; }
    public List<string> Notes { get; } = new();

    /// <summary>Why this file cannot be imported although the 1.12 check passes (e.g. the tile changed since the export).</summary>
    public string? BlockReason { get; init; }
    /// <summary>What the new file is called in the review ("Noggit's file", "your terrain from Blender").</summary>
    public string NewLabel { get; init; } = "Noggit's file";

    public bool HasErrors => !Validation.Ok || BlockReason != null;
    public bool CanInclude => Kind != null && !HasErrors && !Unchanged;
    public string FileName => Path.GetFileName(GamePath);
    public string Summary => Kind == null ? "Not imported: only map files (.adt, .wdt, .wdl) are imported." :
        Unchanged ? (Diff?.IdenticalBytes == true ? "Unchanged (identical to " + BaseLabel + ")." : "Same content as " + BaseLabel + " (only the file layout differs).") :
        BaseBytes == null ? "A new file (the game has none)." : Diff?.PlainSummary() + ".";
    /// <summary>The validator's errors and warnings in plain words.</summary>
    public List<string> Problems => (BlockReason != null ? new[] { "Cannot be imported: " + BlockReason } : Array.Empty<string>())
        .Concat(Validation.Findings.Where(f => f.Severity != MapSeverity.Info).Select(f => (f.Severity == MapSeverity.Error ? "Error: " : "Warning: ") + f.Plain)).ToList();
}

public sealed class MapImportAnalysis
{
    public MapInfo Map { get; init; } = null!;
    public string SourceDir { get; init; } = "";
    public List<MapFileChange> Changes { get; } = new();
    public List<string> General { get; } = new();
    /// <summary>The review window's heading and source line (null: the Noggit texts).</summary>
    public string? Heading { get; init; }
    public string? SourceText { get; init; }
    /// <summary>The first words of the change set's label ("Map import", "Terrain from Blender").</summary>
    public string ChangeSetPrefix { get; init; } = "Map import";
    /// <summary>"Import edited terrain": the edges that disagree with their neighbour after this import (empty when
    /// they were synced or match), and what was synced.</summary>
    public List<SeamIssue> Seams { get; } = new();
    public List<SeamIssue> SyncedSeams { get; } = new();
    /// <summary>"Import edited terrain": what Blender did that cannot come back (moved sideways, added, deleted vertices).</summary>
    public List<string> Ignored { get; } = new();
    public bool NothingChanged => Changes.All(c => !c.CanInclude && (c.Unchanged || c.Kind == null));
    public int ChangedCount => Changes.Count(c => !c.Unchanged && c.Kind != null);
}

/// <summary>
/// "Import map changes": compares the files Noggit saved for a map (its project folder, World\Maps\&lt;map&gt;\) with what
/// the app has (your earlier import of the file, else the game's file), validates each changed file with the 1.12
/// validator (errors block the file), describes the change in words (semantic diff), and saves the selected files into
/// the workspace as one change set ("Revert this import").
/// </summary>
public static class MapImporter
{
    public static string FormatOf(string kind) => kind switch { "adt" => "ADT terrain tile", "wdt" => "WDT map tile list", "wdl" => "WDL far terrain", _ => kind };

    public static MapImportAnalysis Analyze(MapInfo map, string sourceDir, ClientData client, Ws ws)
    {
        var a = new MapImportAnalysis { Map = map, SourceDir = sourceDir };
        if (!Directory.Exists(sourceDir))
        {
            a.General.Add($"Noggit has not saved anything for {map.Name} yet (no folder {sourceDir}). In Noggit, edit the map and save (Ctrl+S), then import again.");
            return a;
        }
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        byte[]? newWdt = null;
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var kind = MapValidator.KindOf(name);
            var rel = Path.GetRelativePath(sourceDir, f);
            var game = $@"World\Maps\{map.Directory}\{rel.Replace('/', '\\')}";
            if (kind == null)
            {
                a.Changes.Add(new MapFileChange { GamePath = game, SourceFile = f, Kind = null, Unchanged = false });
                continue;
            }
            var data = File.ReadAllBytes(f);
            var gameBytes = client.Read(game);
            var archive = client.Find(game)?.Name;
            var edited = ws.IsModified(game) ? ws.ReadEdited(game) : null;
            var baseBytes = edited ?? gameBytes;
            var label = edited != null ? "your earlier import" : "the game's file";
            var report = MapValidator.ValidateBytes(data, name);
            var diff = baseBytes != null ? MapDiff.DiffBytes(baseBytes, data, name, label, "Noggit's file") : null;
            bool unchanged = diff != null && diff.SameContent;
            var ch = new MapFileChange
            {
                GamePath = game, SourceFile = f, Kind = kind, Tile = kind == "adt" ? MapValidator.TileXY(name) : null, NewBytes = data, BaseBytes = baseBytes,
                BaseLabel = label, GameBytes = gameBytes, GameArchive = archive, Validation = report, Diff = diff, Unchanged = unchanged,
            };
            ch.Include = ch.CanInclude;
            if (kind == "wdt" && !unchanged) newWdt = data;
            a.Changes.Add(ch);
        }
        // cross-file: a tile the map's tile list (WDT) does not flag is never loaded by the game
        var wdt = newWdt ?? (ws.IsModified(map.WdtPath) ? ws.ReadEdited(map.WdtPath) : null) ?? client.Read(map.WdtPath);
        if (wdt != null)
        {
            var w = new WdtFile(wdt);
            var flagged = w.Tiles().ToHashSet();
            foreach (var c in a.Changes.Where(c => c.Kind == "adt" && c.Tile != null && !c.Unchanged))
                if (!flagged.Contains(c.Tile!.Value))
                    c.Notes.Add($"Tile {c.Tile.Value.X},{c.Tile.Value.Y} is not in the map's tile list (WDT), so the game never loads it. Save the map in Noggit so it writes the WDT too, and import it with this tile.");
        }
        if (a.Changes.Count == 0) a.General.Add($"The project folder has no files for {map.Name} yet. In Noggit, edit the map and save (Ctrl+S), then import again.");
        else if (a.NothingChanged) a.General.Add("No changes found: every file Noggit saved has the same content as what the game (or your earlier import) has. Nothing will be written.");
        if (a.Changes.Any(c => c.Kind == "adt" && !c.Unchanged && c.Diff?.Stats is { HeightChunks: > 0 } or { HoleChunks: > 0 } or { LiquidChunks: > 0 } or { DoodadsAdded: > 0 } or { DoodadsMoved: > 0 } or { WmosAdded: > 0 } or { WmosMoved: > 0 }))
            a.General.Add("This changes the ground, water or objects the server knows about: after testing in the game, also update the server's maps (Maps tab, \"Update server maps\"), or creatures will walk on the old ground and pathing and line of sight will not match.");
        return a;
    }

    /// <summary>Save the selected files into the workspace (one change set).</summary>
    public static ChangeSet Commit(MapImportAnalysis a, Ws ws)
    {
        var sel = a.Changes.Where(c => c.Include && c.CanInclude).ToList();
        if (sel.Count == 0) throw new WorkspaceException("Nothing is selected to import.");
        var tiles = sel.Where(c => c.Tile != null).Select(c => $"{c.Tile!.Value.X},{c.Tile.Value.Y}").ToList();
        var cs = ws.BeginChangeSet($"{a.ChangeSetPrefix}: {a.Map.Name}" + (tiles.Count > 0 ? $" (tile{(tiles.Count == 1 ? "" : "s")} {string.Join(" ", tiles.Take(6))}{(tiles.Count > 6 ? " ..." : "")})" : ""));
        foreach (var c in sel)
        {
            var origin = new OriginInfo
            {
                Archive = c.GameArchive, Sha256 = c.GameBytes != null ? Ws.Sha256(c.GameBytes) : null, Size = c.GameBytes?.LongLength ?? 0,
                Format = FormatOf(c.Kind!), IsNew = c.GameBytes == null,
                UsedBy = { c.Tile != null ? $"{a.Map.Name} tile {c.Tile.Value.X},{c.Tile.Value.Y}" : $"{a.Map.Name} ({c.Kind!.ToUpperInvariant()})" },
            };
            ws.SaveEdit(c.GamePath, c.NewBytes, origin, cs, c.SourceFile, c.Summary.TrimEnd('.'));
        }
        return cs;
    }
}
