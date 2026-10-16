using System.Text;
using WoWData.Core.Index;
using WoWData.Core.Session;

namespace WoWData.Cli.Commands;

/// <summary>Helpers shared by the commands: text tables, confirmation (--yes), finding entries and files.</summary>
internal static class Common
{
    public static readonly OptionSpec DryRun = new("dry-run", null, "show what would happen; change nothing");
    public static readonly OptionSpec Limit = new("limit", "n", "at most n results (default 50; 0 = all)");

    /// <summary>A destructive or outward-facing step without --yes: say what would happen, change nothing, exit code 5.</summary>
    public static CliResult NeedsYes(CliResult r, string what)
    {
        r.Code = ExitCode.NeedsYes;
        r.Error = $"{what}\nNothing was changed. Run the command again with --yes to do it (or --dry-run to only look).";
        return r;
    }

    /// <summary>Simple aligned columns for text output.</summary>
    public static IEnumerable<string> Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int maxWidth = 60)
    {
        var all = rows.Select(r => r.Select(c => c.Length > maxWidth ? c[..(maxWidth - 3)] + "..." : c).ToList()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Count == 0 ? 0 : all.Max(r => i < r.Count ? r[i].Length : 0))).ToList();
        string Row(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
        yield return Row(headers);
        yield return Row(widths.Select(w => new string('-', w)).ToList());
        foreach (var r in all) yield return Row(r);
    }

    public static int LimitOf(Invocation inv, int def = 50)
    {
        var l = inv.IntOpt("limit") ?? def;
        return l <= 0 ? int.MaxValue : l;
    }

    /// <summary>An entry of the Browse list by key ("creature:3098"), by file path, or by name (the best match of a search).</summary>
    public static IndexEntry FindEntry(AppSession s, string text, AssetKind? kind = null)
    {
        if (s.FindEntry(text) is { } byKey) return byKey;
        if (s.FindEntry("file:" + text) is { } byFile) return byFile;
        var q = new EntryQuery(text, kind == null ? null : new HashSet<AssetKind> { kind.Value });
        var hits = EntrySearch.Run(s.Entries, q, new HashSet<string>(), new HashSet<string>());
        var exact = hits.FirstOrDefault(h => string.Equals(h.Entry.Name, text, StringComparison.OrdinalIgnoreCase));
        return exact?.Entry ?? hits.FirstOrDefault()?.Entry ?? throw new CliError(ExitCode.NotFound, $"nothing found for \"{text}\". Try \"wds find search {text}\".");
    }

    public static object EntryJson(IndexEntry e, string badge = "") => new
    {
        key = e.Key,
        kind = EntrySearch.CliName(e.Kind),
        kindLabel = e.KindLabel,
        id = e.Id,
        name = e.Name,
        path = e.Path,
        info = e.Info,
        badge,
    };

    /// <summary>A game file path: as given (backslashes), or the path of an entry ("file:..." or a key whose entry has a path).</summary>
    public static string GamePath(AppSession s, string text)
    {
        var p = text.Replace('/', '\\');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        if (s.FileExists(p)) return Core.Mpq.ClientData.ResolveName(p);
        if (s.FindEntry(text) is { Path: { } ep }) return ep;
        throw new CliError(ExitCode.NotFound, $"{text} is not a file of the game (nor of your workspace). Paths look like Creature\\Boar\\BoarSkinIvory.blp; \"wds find files <pattern>\" lists them.");
    }

    public static string Plural(int n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";

    public static string Indent(string text, string prefix = "  ")
    {
        var sb = new StringBuilder();
        foreach (var l in text.Replace("\r", "").Split('\n')) sb.Append(prefix).AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
