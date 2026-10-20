using System.Globalization;
using WoWData.Core.Designers;
using WoWData.Core.ServerData;
using WoWData.Core.Session;

namespace WoWData.Cli.Commands;

/// <summary>0.12.0: what the designer commands share: opening the design session (the game's tables, the server's
/// database when it is on and reachable), saving it (or, with --dry-run, saying what would be saved), JSON of the
/// problems.</summary>
internal static class DesignerCli
{
    public static readonly OptionSpec SetOpt = new("set", "field=value", "a field of the form (\"wds spells fields\" lists them; friendly units: seconds, yards, \"instant\", an icon file name)", true);

    public sealed record Ctx(AppSession S, DesignSession D, SpellDesigner Spells);

    public static async Task<Ctx> Open(Invocation inv, CliResult? r = null)
    {
        var s = await inv.Ctx.SessionAsync();
        var dbc = s.Dbc ?? throw new CliError(ExitCode.Error, "The data tables could not be read.");
        ServerEditor? ed = null;
        string? note = null;
        if (s.Settings.Server.Enabled && s.ServerData is { } svc)
        {
            try
            {
                _ = svc.Schema;
                ed = svc.NewEditor();
            }
            catch (ServerDataException e)
            {
                note = $"The server database could not be reached ({e.Message}): only the game's tables are edited.";
            }
        }
        else note = "The server database is turned off (Settings): only the game's tables are edited; \"wds spells sync\" brings the server in line later.";
        if (note != null) r?.Warnings.Add(note);
        var d = new DesignSession(dbc, ed, (s.Settings.ServerCustomIdStart, s.Settings.ServerCustomIdEnd), note, (s.Settings.SpellIdStart, s.Settings.SpellIdEnd));
        return new Ctx(s, d, new SpellDesigner(d));
    }

    public static object ProblemsJson(IEnumerable<DesignProblem> p) => p.Select(x => new { severity = x.Severity, field = x.Field, message = x.Message });

    public static void AddProblems(CliResult r, IEnumerable<DesignProblem> problems)
    {
        foreach (var p in problems)
            if (p.Field == "Server" && p.Severity == DesignSeverity.Note) continue;   // said once by Open
            else if (p.Severity == DesignSeverity.Error) r.Line("  " + p.Text);
            else r.Warnings.Add(p.Text);
    }

    /// <summary>Save (or preview) and finish the result.</summary>
    public static CliResult Finish(Invocation inv, CliResult r, Func<DesignSaveResult> save, Func<object?> data, DesignSession d)
    {
        if (inv.DryRun)
        {
            r.Line($"Dry run: nothing was saved (would save: {string.Join(", ", d.DirtyTables.Select(t => t + ".dbc").Concat(d.PendingFiles).Concat(d.Server?.DirtyTables() ?? new List<string>()))}).");
            return r.With(new { saved = false, data = data() });
        }
        try
        {
            var res = save();
            r.Line(res.Message);
            return r.With(new { saved = true, changeSet = res.ChangeSet, clientTables = res.ClientTables, files = res.Files, serverTables = res.ServerTables, serverSkipped = res.ServerSkipped, notes = res.Notes, data = data() });
        }
        catch (DesignException e)
        {
            return new CliResult { Code = ExitCode.ValidationFailed, Error = e.Message }.With(new { saved = false, data = data() });
        }
    }

    public static (string Field, string Value) Pair(string kv, string option = "set")
    {
        int eq = kv.IndexOf('=');
        if (eq <= 0) throw new CliError(ExitCode.Usage, $"--{option} is field=value (not {kv}).");
        return (kv[..eq].Trim(), kv[(eq + 1)..]);
    }

    /// <summary>Apply --set options to a spell; refusals stop the command (exit 3).</summary>
    public static List<string> ApplySets(Invocation inv, SpellDesigner sp, long id)
    {
        var msgs = new List<string>();
        foreach (var kv in inv.Opts("set"))
        {
            var (f, v) = Pair(kv);
            if (SpellForm.Find(f) == null) throw new CliError(ExitCode.Usage, $"there is no spell field \"{f}\" (\"wds spells fields\" lists them).");
            var res = sp.Set(id, f, v);
            if (!res.Ok) throw new CliError(ExitCode.ValidationFailed, res.Message);
            msgs.Add(res.Message);
        }
        return msgs;
    }

    public static long Id(Invocation inv, int i, string what = "spell")
    {
        var t = inv.Arg(i);
        return long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new CliError(ExitCode.Usage, $"the {what} is an ID (not \"{t}\").");
    }

    public static List<long> Ids(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, $"\"{x}\" is not an ID.")).ToList();

    public static T Guard<T>(Func<T> f)
    {
        try { return f(); }
        catch (DesignException e) { throw new CliError(e.Message.StartsWith("There is no", StringComparison.Ordinal) || e.Message.Contains(" has no row ", StringComparison.Ordinal) ? ExitCode.NotFound : ExitCode.ValidationFailed, e.Message); }
    }
}
