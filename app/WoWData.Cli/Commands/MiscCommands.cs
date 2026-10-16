using System.Diagnostics;
using WoWData.Core;
using WoWData.Core.Features;

namespace WoWData.Cli.Commands;

/// <summary>version, commands, features, manual.</summary>
internal static class MiscCommands
{
    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "version",
            Summary = "the version and build (the same as the app's Help > About WoW Data Studio)",
            NeedsClient = false,
            Examples = new[] { "wds version", "wds --version", "wds version --json" },
            Run = inv =>
            {
                var gui = Path.Combine(AppContext.BaseDirectory, StudioInfo.GuiExe);
                string? guiVersion = File.Exists(gui) ? FileVersionInfo.GetVersionInfo(gui).ProductVersion : null;
                var r = new CliResult().With(new { product = StudioInfo.ProductName, version = StudioInfo.Version, build = StudioInfo.Build, display = StudioInfo.Display, app = File.Exists(gui) ? gui : null, appVersion = guiVersion });
                r.Line($"WoW Data Studio {StudioInfo.Display} (command line: wds)");
                if (guiVersion != null) r.Line($"The app next to it: {gui} ({guiVersion})");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "commands",
            Summary = "every command of wds (with --json: the whole tree with arguments, options and examples)",
            NeedsClient = false,
            Examples = new[] { "wds commands", "wds commands --json" },
            Run = inv =>
            {
                var r = new CliResult().With(new { globalOptions = CliApp.GlobalOptions.Select(o => new { name = o.Name, value = o.Value, help = o.Help }), commands = CliApp.Tree.Select(CliApp.CommandJson) });
                foreach (var c in CliApp.Tree) r.Line($"{c.Usage,-70} {c.Summary}");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "features",
            Summary = "every feature of the app and its command (GUI <-> CLI), from the feature registry",
            NeedsClient = false,
            Options = new[]
            {
                new OptionSpec("area", "name", "only one area (Setup, Find, Textures, Models, Maps, Server maps, Data tables, My changes, Help, Command line)"),
                new OptionSpec("markdown", null, "print the coverage table of docs\\manual\\command-line.md (for maintainers)"),
            },
            Examples = new[] { "wds features", "wds features --area \"Data tables\" --json", "wds features --markdown" },
            Run = inv =>
            {
                var area = inv.Opt("area");
                var list = FeatureRegistry.All.Where(f => area == null || string.Equals(f.Area, area, StringComparison.OrdinalIgnoreCase)).ToList();
                if (inv.Flag("markdown"))
                {
                    var md = new CliResult().With(new { markdown = true });
                    md.Line("| Feature | In the app | Command |").Line("|---|---|---|");
                    foreach (var f in list)
                        md.Line($"| {f.Id} | {(f.HasGui ? "**" + f.Gui + "**" : "(command line only)")} | {(f.HasCli ? "`wds " + f.Cli + "`" : "(app only: " + f.Reason + ")")} |");
                    return Task.FromResult(md);
                }
                var r = new CliResult().With(list.Select(f => new { id = f.Id, area = f.Area, gui = f.HasGui ? f.Gui : null, cli = f.HasCli ? "wds " + f.Cli : null, front = f.Front, reason = f.Reason }));
                foreach (var l in Common.Table(new[] { "Feature", "In the app", "Command", "" },
                             list.Select(f => (IReadOnlyList<string>)new[] { f.Id, f.HasGui ? f.Gui : "-", f.HasCli ? "wds " + f.Cli : "-", f.Front == FeatureFront.Both ? "" : f.Front == FeatureFront.GuiOnly ? "app only" : "command line only" }), 70))
                    r.Line(l);
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "manual",
            Summary = "where the user manual (or one chapter) is; --open opens it in the browser",
            Args = new[] { "[chapter]" },
            NeedsClient = false,
            Options = new[] { new OptionSpec("open", null, "open it (in the web browser, or the program for .md files)") },
            Examples = new[] { "wds manual", "wds manual command-line --open", "wds manual dbc-editor" },
            Run = inv =>
            {
                var topic = inv.ArgOrNull(0);
                var file = ManualLocator.Locate(topic) ?? throw new CliError(ExitCode.NotFound, "The user manual was not found: it is installed next to the app (the \"manual\" folder of the installation).");
                var r = new CliResult().With(new { chapter = topic ?? ManualLocator.Contents, file }).Line(file);
                if (inv.Flag("open"))
                {
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                    r.Line("Opened.");
                }
                return Task.FromResult(r);
            },
        };
    }
}
