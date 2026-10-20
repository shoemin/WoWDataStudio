using System.Text;
using System.Text.Json;
using WoWData.Core;
using WoWData.Core.Workspace;

namespace WoWData.Cli;

/// <summary>
/// wds: the command line of WoW Data Studio. Parses "wds [global options] &lt;group&gt; &lt;command&gt; [arguments] [options]",
/// prints help, runs the command and writes its result: text by default, one JSON object with --json. Results go to
/// stdout, progress and warnings to stderr. The exit codes are <see cref="ExitCode"/>.
/// </summary>
public static class CliApp
{
    /// <summary>The options every command accepts.</summary>
    public static readonly OptionSpec[] GlobalOptions =
    {
        new("home", "folder", "keep settings, index and edits in this folder instead of %APPDATA%\\WoWDataStudio (scripts and tests run isolated)"),
        new("client", "folder", "use this World of Warcraft folder for this run instead of the one in the settings"),
        new("json", null, "print one JSON object (machine-readable) instead of text"),
        new("quiet", null, "no progress on stderr"),
        new("yes", null, "confirm a command that changes the game folder or the server, or deletes something (without it, wds says what it would do and exits with code 5)"),
        new("help", null, "help for the command"),
    };

    /// <summary>The groups, in the order of the app's tabs, with one line each.</summary>
    public static readonly (string Name, string Summary)[] Groups =
    {
        ("setup", "the World of Warcraft folder and an overview of everything (Welcome screen, File menu)"),
        ("settings", "every setting of the app (File > Settings...)"),
        ("serverdb", "the server database for names (Settings, File > Reload names...)"),
        ("index", "the index of the game data (\"Organizing game data\", File > Rebuild the index)"),
        ("find", "search like the Browse tab, details, Used in game, where a file comes from"),
        ("textures", "export, import (with every check of the app), compare"),
        ("models", "export to and import from Blender (glTF), validate, compare, render to PNG, the add-on"),
        ("maps", "maps, zones, tiles, breakdown, previews, Noggit, terrain in Blender, compare, test, revert"),
        ("server-maps", "the guided update of the server's map data (and its undo)"),
        ("dbc", "the data tables: rows, get / set, add, duplicate, delete, bulk edit, CSV / JSON, copy to server"),
        ("server", "the server's world database: browse, edit, check against the game, SQL patch, apply / undo, CSV / JSON, the game's cache"),
        ("spells", "the spell designer: new and cloned spells, the form's fields, tooltip preview, checks, visuals, trainers and skill lines (game and server in sync)"),
        ("talents", "the talent designer: a class's talent trees and talents (grid, ranks, prerequisites), checked against the 1.12 talent frame"),
        ("classes", "the class designer: a class's spells by level, skill lines, trees, assigning spells to levels and trainers, class masks, a new class (experimental)"),
        ("quests", "the quest-chain designer: chains as flowcharts, the quest editor's fields, new / branched / joined quests, givers and enders, objectives and drop sources, rewards, new quest-giver NPCs on the map, the chain for the other faction, the discipline template, checks, text preview, CSV / JSON, test in game"),
        ("changes", "My changes: list, compare, revert, test in game, release patch, uninstall it"),
        ("versions", "experimental: other World of Warcraft versions (any client folder): find them, their files and archives, extract, data tables, compare a row across versions; read only"),
        ("manual", "the user manual"),
        ("version", "the version and build (the same as the app's Help > About)"),
        ("commands", "the command tree (for tools: --json)"),
        ("features", "every feature of the app and its command"),
    };

    private static List<CommandSpec>? _tree;

    /// <summary>Every command of wds.</summary>
    public static IReadOnlyList<CommandSpec> Tree => _tree ??= Commands.MiscCommands.All()
        .Concat(Commands.SetupCommands.All()).Concat(Commands.FindCommands.All()).Concat(Commands.TextureCommands.All())
        .Concat(Commands.ModelCommands.All()).Concat(Commands.MapCommands.All()).Concat(Commands.DbcCommands.All())
        .Concat(Commands.ServerCommands.All()).Concat(Commands.SpellCommands.All()).Concat(Commands.TalentCommands.All())
        .Concat(Commands.ClassCommands.All()).Concat(Commands.QuestCommands.All()).Concat(Commands.ChangeCommands.All()).Concat(Commands.VersionCommands.All()).ToList();

    public static CommandSpec? Find(string path) => Tree.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        return await RunAsync(args, Console.Out, Console.Error, Console.In);
    }

    /// <summary>Runs one command line (in-process; the tests call this too).</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, TextReader? stdin = null)
    {
        await using var ctx = new CliContext(stdout, stderr, stdin ?? TextReader.Null);
        string commandPath = "";
        try
        {
            // global options anywhere on the line; the rest is the command, its arguments and its options
            var rest = new List<string>();
            bool help = false, version = false;
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string? Next(string name) => i + 1 < args.Length ? args[++i] : throw new CliError(ExitCode.Usage, $"--{name} needs a value.");
                switch (a.ToLowerInvariant())
                {
                    case "--home": ctx.Home = Next("home"); break;
                    case "--client": ctx.ClientOverride = Next("client"); break;
                    case "--json": ctx.Json = true; break;
                    case "--quiet": ctx.Quiet = true; break;
                    case "--yes": case "-y": ctx.Yes = true; break;
                    case "--dry-run": ctx.DryRun = true; rest.Add(a); break;
                    case "--help": case "-h": case "-?": case "/?": help = true; break;
                    case "--version": version = true; break;
                    default:
                        if (a.StartsWith("--home=", StringComparison.OrdinalIgnoreCase)) ctx.Home = a[7..];
                        else if (a.StartsWith("--client=", StringComparison.OrdinalIgnoreCase)) ctx.ClientOverride = a[9..];
                        else rest.Add(a);
                        break;
                }
            }
            if (version && rest.Count == 0) rest.Add("version");
            if (rest.Count == 0 || rest[0] == "help" && rest.Count == 1)
            {
                Write(ctx, "", new CliResult().With(new { groups = Groups.Select(g => new { name = g.Name, summary = g.Summary }) }), MainHelp());
                return ExitCode.Ok;
            }
            if (rest[0] == "help") { rest.RemoveAt(0); help = true; }
            // the command: "group command" or a single word
            CommandSpec? spec = null;
            int used = 0;
            if (rest.Count >= 2 && !rest[1].StartsWith("--", StringComparison.Ordinal) && Find(rest[0] + " " + rest[1]) is { } two) { spec = two; used = 2; }
            else if (Find(rest[0]) is { } one) { spec = one; used = 1; }
            if (spec == null)
            {
                var group = Tree.Where(c => string.Equals(c.Group, rest[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (group.Count > 0)
                {
                    if (help || rest.Count == 1)
                    {
                        Write(ctx, rest[0], new CliResult().With(new { group = rest[0], commands = group.Select(c => new { path = c.Path, summary = c.Summary, usage = c.Usage }) }), GroupHelp(rest[0], group));
                        return rest.Count == 1 && !help ? ExitCode.Usage : ExitCode.Ok;
                    }
                    throw new CliError(ExitCode.Usage, $"unknown command \"{rest[0]} {rest[1]}\". The commands of {rest[0]}: {string.Join(", ", group.Select(c => c.Path[(rest[0].Length + 1)..]))}. See \"wds {rest[0]} --help\".");
                }
                throw new CliError(ExitCode.Usage, $"unknown command \"{rest[0]}\". See \"wds --help\".");
            }
            commandPath = spec.Path;
            if (help)
            {
                Write(ctx, spec.Path, new CliResult().With(CommandJson(spec)), CommandHelp(spec));
                return ExitCode.Ok;
            }
            // the command's arguments and options
            var positional = new List<string>();
            var opts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool onlyArgs = false;
            for (int i = used; i < rest.Count; i++)
            {
                var a = rest[i];
                if (onlyArgs || !a.StartsWith("--", StringComparison.Ordinal) || a == "--")
                {
                    if (a == "--" && !onlyArgs) { onlyArgs = true; continue; }
                    positional.Add(a);
                    continue;
                }
                var name = a[2..];
                string? inline = null;
                int eq = name.IndexOf('=');
                if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
                if (name == "dry-run" && !spec.Options.Any(o => o.Name == "dry-run"))
                    throw new CliError(ExitCode.Usage, $"\"wds {spec.Path}\" has no --dry-run (it changes nothing, or it has no preview).");
                var o = spec.Options.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new CliError(ExitCode.Usage, $"unknown option --{name} for \"wds {spec.Path}\". See \"wds {spec.Path} --help\".");
                if (!opts.TryGetValue(o.Name, out var list)) opts[o.Name] = list = new List<string>();
                else if (!o.Repeatable && !o.IsFlag) throw new CliError(ExitCode.Usage, $"--{o.Name} is given twice.");
                if (o.IsFlag)
                {
                    if (inline != null) throw new CliError(ExitCode.Usage, $"--{o.Name} takes no value.");
                    continue;
                }
                var v = inline ?? (i + 1 < rest.Count ? rest[++i] : throw new CliError(ExitCode.Usage, $"--{o.Name} needs a value <{o.Value}>."));
                list.Add(v);
            }
            if (positional.Count < spec.MinArgs)
                throw new CliError(ExitCode.Usage, $"missing argument {spec.Args[positional.Count]}. Usage: {spec.Usage} (see \"wds {spec.Path} --help\").");
            if (positional.Count > spec.MaxArgs)
                throw new CliError(ExitCode.Usage, $"too many arguments ({string.Join(" ", positional.Skip(spec.MaxArgs))}). Usage: {spec.Usage}");
            var inv = new Invocation(spec, ctx, positional, opts);
            CliResult result;
            try
            {
                result = await spec.Run(inv);
            }
            catch (CliError) { throw; }
            catch (Exception e)
            {
                result = CliResult.Fail(Classify(e), e.Message);
            }
            Write(ctx, spec.Path, result);
            return result.Code;
        }
        catch (CliError e)
        {
            Write(ctx, commandPath, CliResult.Fail(e.Code, e.Message));
            return e.Code;
        }
    }

    /// <summary>The exit code of an exception from Core.</summary>
    public static int Classify(Exception e) => e switch
    {
        CliError c => c.Code,
        FileNotFoundException or DirectoryNotFoundException => ExitCode.NotFound,
        WorkspaceException w when w.Message.Contains("would not load", StringComparison.Ordinal) || w.Message.Contains("read-back check", StringComparison.Ordinal) => ExitCode.ValidationFailed,
        WorkspaceException w when w.Message.Contains("not installed by WoW Data Studio", StringComparison.Ordinal) || w.Message.Contains("cannot be proven", StringComparison.Ordinal) => ExitCode.Refused,
        WorkspaceException w when w.Message.Contains("There are no changes", StringComparison.Ordinal) => ExitCode.NotFound,
        Core.Dbc.DbcException => ExitCode.ValidationFailed,
        Core.Designers.DesignException d when d.Message.StartsWith("There is no", StringComparison.Ordinal) || d.Message.Contains(" has no row ", StringComparison.Ordinal) => ExitCode.NotFound,
        Core.Designers.DesignException => ExitCode.ValidationFailed,
        Core.ServerData.ServerDataException s when s.Message.StartsWith("Refused", StringComparison.Ordinal) => ExitCode.Refused,
        Core.ServerData.ServerDataException s when s.Message.StartsWith("Could not connect", StringComparison.Ordinal) || s.Message.Contains("is not a world database", StringComparison.Ordinal) => ExitCode.Error,
        Core.ServerData.ServerDataException s when s.Message.Contains(" has no ", StringComparison.Ordinal) || s.Message.StartsWith("no ", StringComparison.Ordinal) => ExitCode.NotFound,
        Core.ServerData.ServerDataException => ExitCode.ValidationFailed,
        Core.M2.M2Exception or Core.Blp.BlpException or Core.ModelIO.GltfException or Core.Imaging.ImageFormatException or TextureException => ExitCode.ValidationFailed,
        _ => ExitCode.Error,
    };

    // ------------------------------------------------------------------ output

    private static void Write(CliContext ctx, string command, CliResult r, string? text = null)
    {
        if (ctx.Json)
        {
            var env = new Dictionary<string, object?>
            {
                ["command"] = command,
                ["ok"] = r.Code == ExitCode.Ok,
                ["exitCode"] = r.Code,
                ["status"] = ExitCode.Names.GetValueOrDefault(r.Code, "error"),
                ["version"] = StudioInfo.Version,
                ["data"] = r.Data,
                ["messages"] = r.Lines,
                ["warnings"] = r.Warnings,
                ["error"] = r.Error,
            };
            ctx.Out.WriteLine(JsonSerializer.Serialize(env, CliJson.Options));
            return;
        }
        if (text != null) ctx.Out.Write(text);
        foreach (var l in r.Lines) ctx.Out.WriteLine(l);
        foreach (var w in r.Warnings) ctx.Err.WriteLine("warning: " + w);
        if (r.Error != null)
        {
            ctx.Err.WriteLine((r.Code == ExitCode.NeedsYes ? "" : "wds: ") + r.Error);
            if (r.Code == ExitCode.Usage) ctx.Err.WriteLine("Run \"wds --help\" for the commands.");
        }
    }

    // ------------------------------------------------------------------ help

    public static string MainHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"wds {StudioInfo.Display}: the command line of WoW Data Studio. Every feature of the app, for scripts and automation.");
        sb.AppendLine();
        sb.AppendLine("Usage: wds [global options] <group> <command> [arguments] [options]");
        sb.AppendLine("       wds <group> --help        the commands of a group");
        sb.AppendLine("       wds <group> <command> --help   one command, with examples");
        sb.AppendLine();
        sb.AppendLine("Groups:");
        foreach (var (name, summary) in Groups) sb.AppendLine($"  {name,-12} {summary}");
        sb.AppendLine();
        sb.AppendLine("Global options:");
        foreach (var o in GlobalOptions) sb.AppendLine($"  {o.Synopsis,-18} {o.Help}");
        sb.AppendLine($"  {"--dry-run",-18} show what a command would do, change nothing (where the app shows a preview)");
        sb.AppendLine();
        sb.AppendLine("Exit codes: " + string.Join(", ", ExitCode.Names.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        sb.AppendLine("Results go to stdout, progress and warnings to stderr. The manual: wds manual command-line --open");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("  wds setup client \"C:\\Games\\WoW 1.12\"");
        sb.AppendLine("  wds find search mottled boar --type creature");
        sb.AppendLine("  wds textures export Creature\\Boar\\BoarSkinIvory.blp --out boar.png");
        sb.AppendLine("  wds textures import boar.png --dry-run");
        sb.AppendLine("  wds dbc set Spell 133 SpellIconID 184");
        sb.AppendLine("  wds server set item_template 25 dmg_max1 12");
        sb.AppendLine("  wds changes build-patch --install --yes");
        return sb.ToString();
    }

    public static string GroupHelp(string group, IEnumerable<CommandSpec> commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"wds {group}: {Groups.FirstOrDefault(g => g.Name == group).Summary}");
        sb.AppendLine();
        foreach (var c in commands) sb.AppendLine($"  {c.Usage,-62} {c.Summary}");
        sb.AppendLine();
        sb.AppendLine($"\"wds {group} <command> --help\" for the options and examples of a command.");
        return sb.ToString();
    }

    public static string CommandHelp(CommandSpec c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"wds {c.Path}: {c.Summary}");
        sb.AppendLine();
        sb.AppendLine("Usage: " + c.Usage);
        if (c.Details.Length > 0)
        {
            sb.AppendLine();
            foreach (var line in Wrap(c.Details, 110)) sb.AppendLine(line);
        }
        if (c.Options.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Options:");
            foreach (var o in c.Options) sb.AppendLine($"  {o.Synopsis + (o.Repeatable ? " ..." : ""),-28} {o.Help}");
        }
        sb.AppendLine();
        sb.AppendLine("Global options: --home <folder>, --client <folder>, --json, --quiet, --yes (see \"wds --help\").");
        var features = Core.Features.FeatureRegistry.ForCommand(c.Path).Where(f => f.HasGui).Select(f => f.Gui).Distinct().ToList();
        if (features.Count > 0) sb.AppendLine("In the app: " + string.Join("; ", features));
        if (c.Examples.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Examples:");
            foreach (var e in c.Examples) sb.AppendLine("  " + e);
        }
        return sb.ToString();
    }

    public static object CommandJson(CommandSpec c) => new
    {
        path = c.Path,
        group = c.Group,
        summary = c.Summary,
        usage = c.Usage,
        arguments = c.Args,
        options = c.Options.Select(o => new { name = o.Name, value = o.Value, help = o.Help, repeatable = o.Repeatable }),
        examples = c.Examples,
        needsClient = c.NeedsClient,
        features = Core.Features.FeatureRegistry.ForCommand(c.Path).Select(f => f.Id),
    };

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var para in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var w in para.Split(' '))
            {
                if (line.Length + w.Length + 1 > width && line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            yield return line.ToString();
        }
    }
}
