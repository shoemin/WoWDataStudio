using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWData.Cli;

/// <summary>The documented exit codes of wds (docs\manual\command-line.md, "Exit codes").</summary>
public static class ExitCode
{
    /// <summary>Done (also: nothing to do, or a dry run that only showed what would happen).</summary>
    public const int Ok = 0;
    /// <summary>An error: a file could not be read or written, a program could not be started...</summary>
    public const int Error = 1;
    /// <summary>The command line is wrong: unknown command or option, a missing or extra argument.</summary>
    public const int Usage = 2;
    /// <summary>A check failed: a value, file, model, map or import that the game would not load; nothing was changed.</summary>
    public const int ValidationFailed = 3;
    /// <summary>Refused for safety: the world server runs, a file in the game folder is not the app's, a patch-3.MPQ the app
    /// did not create (without --force), a newer import changed a file of the import to revert... Nothing was changed.</summary>
    public const int Refused = 4;
    /// <summary>Confirmation needed: the command changes the game folder, the server or deletes something; it printed what
    /// it would do and changed nothing. Run it again with --yes.</summary>
    public const int NeedsYes = 5;
    /// <summary>Not found: no game folder is set, or the table, row, file, entry, map or tile does not exist.</summary>
    public const int NotFound = 6;

    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [Ok] = "ok", [Error] = "error", [Usage] = "usage", [ValidationFailed] = "validation failed", [Refused] = "refused for safety",
        [NeedsYes] = "confirmation needed (--yes)", [NotFound] = "not found",
    };
}

/// <summary>An error with its exit code (thrown by commands, turned into the output by <see cref="CliApp"/>).</summary>
public sealed class CliError : Exception
{
    public int Code { get; }
    public CliError(int code, string message) : base(message) => Code = code;
}

/// <summary>An option of a command: "--out &lt;file&gt;" (with a value) or "--force" (a flag).</summary>
public sealed record OptionSpec(string Name, string? Value, string Help, bool Repeatable = false)
{
    public bool IsFlag => Value == null;
    public string Synopsis => Value == null ? $"--{Name}" : $"--{Name} <{Value}>";
}

/// <summary>One command of the tree: its path ("dbc set"), its arguments and options, help text and examples.</summary>
public sealed class CommandSpec
{
    public required string Path { get; init; }
    public required string Summary { get; init; }
    /// <summary>Positional arguments: "&lt;table&gt;" required, "[column]" optional, "&lt;ids...&gt;" / "[paths...]" repeated.</summary>
    public string[] Args { get; init; } = Array.Empty<string>();
    public OptionSpec[] Options { get; init; } = Array.Empty<OptionSpec>();
    public string[] Examples { get; init; } = Array.Empty<string>();
    public string Details { get; init; } = "";
    /// <summary>True: the command needs an open game folder (index, archives, workspace).</summary>
    public bool NeedsClient { get; init; } = true;
    public required Func<Invocation, Task<CliResult>> Run { get; init; }

    public string Group => Path.Contains(' ') ? Path[..Path.IndexOf(' ')] : Path;
    public int MinArgs => Args.Count(a => a.StartsWith('<'));
    public bool VarArgs => Args.Any(a => a.EndsWith("...>") || a.EndsWith("...]"));
    public int MaxArgs => VarArgs ? int.MaxValue : Args.Length;
    public string Usage => $"wds {Path}" + (Args.Length > 0 ? " " + string.Join(" ", Args) : "") + (Options.Length > 0 ? " [options]" : "");
}

/// <summary>The output of a command: lines for people, an object for --json, warnings, and the exit code.</summary>
public sealed class CliResult
{
    public int Code { get; set; } = ExitCode.Ok;
    public List<string> Lines { get; } = new();
    public List<string> Warnings { get; } = new();
    public object? Data { get; set; }
    public string? Error { get; set; }

    public CliResult Line(string text)
    {
        Lines.Add(text);
        return this;
    }

    public CliResult With(object? data)
    {
        Data = data;
        return this;
    }

    public static CliResult Fail(int code, string message) => new() { Code = code, Error = message };
}

/// <summary>The parsed arguments and options of one run of a command.</summary>
public sealed class Invocation
{
    private readonly Dictionary<string, List<string>> _opts;

    public Invocation(CommandSpec spec, CliContext ctx, List<string> args, Dictionary<string, List<string>> opts)
    {
        Spec = spec;
        Ctx = ctx;
        Args = args;
        _opts = opts;
    }

    public CommandSpec Spec { get; }
    public CliContext Ctx { get; }
    public List<string> Args { get; }

    public string Arg(int i) => i < Args.Count ? Args[i] : throw new CliError(ExitCode.Usage, $"missing argument {Spec.Args.ElementAtOrDefault(i)}. Usage: {Spec.Usage}");
    public string? ArgOrNull(int i) => i < Args.Count ? Args[i] : null;
    public bool Flag(string name) => _opts.ContainsKey(name);
    public string? Opt(string name) => _opts.TryGetValue(name, out var v) && v.Count > 0 ? v[^1] : null;
    public List<string> Opts(string name) => _opts.TryGetValue(name, out var v) ? v : new List<string>();

    public int Int(int i) => int.TryParse(Arg(i), out var v) ? v : throw new CliError(ExitCode.Usage, $"{Spec.Args.ElementAtOrDefault(i)} must be a whole number (\"{Arg(i)}\" is not).");
    public long Long(int i) => long.TryParse(Arg(i), out var v) ? v : throw new CliError(ExitCode.Usage, $"{Spec.Args.ElementAtOrDefault(i)} must be a whole number (\"{Arg(i)}\" is not).");

    public int? IntOpt(string name)
    {
        var s = Opt(name);
        if (s == null) return null;
        return int.TryParse(s, out var v) ? v : throw new CliError(ExitCode.Usage, $"--{name} must be a whole number (\"{s}\" is not).");
    }

    public bool Yes => Ctx.Yes;
    public bool DryRun => Ctx.DryRun;
}

/// <summary>JSON output: camelCase, indented, enums as text.</summary>
public static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
