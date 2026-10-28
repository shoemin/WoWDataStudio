using Microsoft.Data.Sqlite;
using WoWData.Core.Index;
using WoWData.Core.Session;

namespace WoWData.Core.Usage;

/// <summary>
/// "Used in game" of another version opened read only (the main window's panel and "wds find used --version"). What
/// the client itself says is shown: the displays that share the entry's model (with the names the client knows) and the
/// models that draw a texture. Everything a world server would tell (spawns, loot, vendors, quests, names) comes from
/// the version's server provider: none is connected yet, so the report says so in its notes instead of guessing.
/// </summary>
public static class ModernUsage
{
    private const int MaxLines = 12;

    public static UsageReport Report(AppSession s, string key)
    {
        var ix = s.Index ?? throw new InvalidOperationException("Nothing is open.");
        var version = s.Profile?.VersionBuild ?? "";
        var server = s.Server;
        var rep = new UsageReport { Key = key, ServerConnected = server?.IsConnected == true };
        var e = s.FindEntry(key);
        rep.Headline.Add(e?.Name ?? key, bold: true).Muted($" · {e?.KindLabel ?? "file"} of {version} (read only)");
        var shorts = new List<string>();

        string? model = null;
        try { model = s.ModelOfEntry(key); }
        catch (Exception x) when (x is SqliteException or InvalidOperationException or IOException) { }
        if (model != null)
        {
            var cd = Safe(() => ix.CreatureDisplaysUsingModel(model)) ?? new();
            var id = (Safe(() => ix.ItemDisplaysUsingModel(model)) ?? new()).GroupBy(x => x.Id).Select(g => g.First()).ToList();
            if (cd.Count + id.Count > 0)
            {
                var sec = rep.Add("users", "Displays using its model");
                sec.Summary = $"{UsageNames.Plural(cd.Count, "creature display")} and {UsageNames.Plural(id.Count, "item display")} use {Path.GetFileName(model)}.";
                var cnames = Safe(() => ix.NamedCreaturesForDisplays(cd.Select(x => x.Id))) ?? new();
                var inames = Safe(() => ix.ItemNamesForDisplays(id.Select(x => x.Id))) ?? new();
                foreach (var (did, skins, _) in cd.Take(MaxLines))
                {
                    var l = sec.Line().Add(cnames.TryGetValue(did, out var n) ? n : $"Creature display {did}", s.FindEntry($"cdisplay:{did}") != null ? $"cdisplay:{did}" : null);
                    var sk = string.Join(" / ", skins.Where(x => x.Length > 0).Select(Path.GetFileNameWithoutExtension));
                    l.Muted($"  display {did}" + (sk.Length > 0 ? $" · {sk}" : ""));
                }
                int left = MaxLines - Math.Min(cd.Count, MaxLines);
                foreach (var (did, tex) in id.Take(left))
                    sec.Line().Add(inames.TryGetValue(did, out var n) ? n : $"Item display {did}", s.FindEntry($"idisplay:{did}") != null ? $"idisplay:{did}" : null)
                        .Muted($"  item display {did} · {Path.GetFileNameWithoutExtension(tex)}");
                sec.More = Math.Max(0, cd.Count + id.Count - MaxLines);
                shorts.Add(UsageNames.Plural(cd.Count + id.Count, "display"));
            }
        }
        if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
        {
            var models = Safe(() => ix.ModelsDrawingTexture(key[5..])) ?? new();
            if (models.Count > 0)
            {
                var sec = rep.Add("models", "Models that draw it");
                sec.Summary = $"{UsageNames.Plural(models.Count, "model")} of {version} name this texture.";
                foreach (var m in models.Take(MaxLines)) sec.Line().Add(Path.GetFileName(m), s.FindEntry("file:" + m) != null ? "file:" + m : null).Muted("  " + m);
                sec.More = Math.Max(0, models.Count - MaxLines);
                shorts.Add(UsageNames.Plural(models.Count, "model"));
            }
        }
        // what only a world server knows: never guessed
        rep.NeedsServer = e?.Kind is AssetKind.Creature or AssetKind.Item or AssetKind.GameObject or AssetKind.SpellEffect;
        if (server is { IsConnected: false }) rep.Notes.Add(server.Detail);
        rep.Notes.Add("Placements on the maps are not counted for other versions yet: the Maps tab's \"On this tile\" lists what is placed on a tile.");
        if (rep.Sections.Count == 0) shorts.Add("nothing the client itself names");
        rep.Short = " · " + string.Join(" · ", shorts) + (server is { IsConnected: false } ? " · no server connected" : "");
        return rep;
    }

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (SqliteException) { return null; }
    }
}
