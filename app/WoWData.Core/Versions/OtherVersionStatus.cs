using System.Globalization;
using WoWData.Core.Index;
using WoWData.Core.Session;

namespace WoWData.Core.Versions;

/// <summary>
/// M1: what the main window's status line (another version open read only) and "wds versions open" show about another version: entries, file
/// names and where they come from, readable and locked records of the tables Browse reads (design section 3.1:
/// "14,204 of 14,229 creature displays readable, 25 locked (encrypted, no key)").
/// </summary>
public sealed class OtherVersionStatus
{
    /// <summary>The tables the status line names, with what their rows are.</summary>
    public static readonly (string Table, string What)[] MainTables =
    {
        ("CreatureDisplayInfo", "creature displays"), ("ItemDisplayInfo", "item displays"), ("ItemSparse", "item names"),
        ("GameObjectDisplayInfo", "game object displays"), ("SpellVisualEffectName", "spell effects"),
    };

    public string Version { get; init; } = "";
    public string Label { get; init; } = "";
    public string Built { get; init; } = "";
    public double Seconds { get; init; }
    public Dictionary<string, int> EntriesByKind { get; init; } = new();
    public int Entries { get; init; }
    public Dictionary<string, int> Names { get; init; } = new();
    public int ClientFiles { get; init; }
    public int FileEntriesLeftOut { get; init; }
    public string Listfile { get; init; } = "";
    public string Era { get; init; } = "";
    public List<LockedCount> Locked { get; init; } = new();
    public List<string> Problems { get; init; } = new();

    /// <summary>Named FileDataIDs whose name the client's own hash confirms (root hashes and its interface list).</summary>
    public int VerifiedNames => Names.GetValueOrDefault("client") + Names.GetValueOrDefault("manifest");
    public int AllNames => Names.GetValueOrDefault("client") + Names.GetValueOrDefault("manifest") + Names.GetValueOrDefault("era") + Names.GetValueOrDefault("listfile");

    public static OtherVersionStatus Of(AppSession s)
    {
        var ix = s.Index ?? throw new InvalidOperationException("Nothing is open.");
        var meta = ix.Meta();
        var counts = ix.Counts();
        var names = counts.Where(k => k.Key.StartsWith("names_", StringComparison.Ordinal) && k.Key != "names_unnamed_referenced")
            .ToDictionary(k => k.Key[6..], k => k.Value);
        List<string> problems;
        try { problems = System.Text.Json.JsonSerializer.Deserialize<List<string>>(meta.GetValueOrDefault("problems") ?? "[]") ?? new(); }
        catch (System.Text.Json.JsonException) { problems = new(); }
        return new OtherVersionStatus
        {
            Version = s.Profile?.VersionBuild ?? meta.GetValueOrDefault("version") ?? "",
            Label = s.Profile?.Label ?? "",
            Built = meta.GetValueOrDefault("built") ?? "",
            Seconds = double.TryParse(meta.GetValueOrDefault("seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 0,
            EntriesByKind = s.Entries.GroupBy(e => e.KindLabel).ToDictionary(g => g.Key, g => g.Count()),
            Entries = s.Entries.Count,
            Names = names,
            ClientFiles = int.TryParse(meta.GetValueOrDefault("client_files"), out var cf) ? cf : 0,
            FileEntriesLeftOut = counts.GetValueOrDefault("file_entries_left_out"),
            Listfile = meta.GetValueOrDefault("listfile") ?? "",
            Era = meta.GetValueOrDefault("era") ?? "",
            Locked = ix.LockedCounts(),
            Problems = problems,
        };
    }

    /// <summary>"14,204 of 14,229 creature displays readable, 25 locked (encrypted, no key)" per main table.</summary>
    public IEnumerable<string> LockedLines()
    {
        foreach (var (table, what) in MainTables)
            if (Locked.FirstOrDefault(l => l.Table.Equals(table, StringComparison.OrdinalIgnoreCase)) is { } l)
                yield return l.Locked > 0 ? $"{l.Readable:N0} of {l.Total:N0} {what} readable, {l.Locked:N0} locked (encrypted, no key)" : $"{l.Readable:N0} {what}, all readable";
    }

    /// <summary>The status line of the main window while another version is open read only.</summary>
    public string StatusLine => $"{Version} (read only): " + string.Join(" · ", LockedLines());

    /// <summary>"names: 183,632 of 1,245,465 files (134,232 checked by the client's own hashes, 49,400 your 1.12 names
    /// through Classic Era, 0 from your listfile)".</summary>
    public string NamesLine =>
        $"names: {AllNames:N0} of {ClientFiles:N0} files ({VerifiedNames:N0} checked by the client's own hashes, {Names.GetValueOrDefault("era"):N0} your 1.12 names through Classic Era, " +
        $"{Names.GetValueOrDefault("listfile"):N0} from your listfile){(Listfile.Length == 0 ? "; no listfile chosen" : "")}";
}
