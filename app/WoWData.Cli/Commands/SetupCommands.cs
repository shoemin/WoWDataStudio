using WoWData.Core;
using WoWData.Core.Index;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace WoWData.Cli.Commands;

/// <summary>setup, settings, serverdb, index.</summary>
internal static class SetupCommands
{
    /// <summary>The game folder of a per-folder setting (hot-reload): --client when given, else null (the one in the settings).</summary>
    private static string? GameFolder(Invocation inv) => inv.Ctx.ClientOverride != null ? inv.Ctx.ClientDir() : null;

    public static IEnumerable<CommandSpec> All()
    {
        // ------------------------------------------------------------------ setup
        yield return new CommandSpec
        {
            Path = "setup client",
            Summary = "set the World of Warcraft folder (checked: 1.12.1, build 5875) and organize its data (the index), or show it",
            Args = new[] { "[folder]" },
            NeedsClient = false,
            Options = new[] { new OptionSpec("no-index", null, "only save the folder; build the index later (wds index build)") },
            Details = "The folder with WoW.exe and Data (the Data folder or WoW.exe itself work too). It is only read. Like the app's Welcome " +
                      "screen and File > Change World of Warcraft folder..., each game folder has its own index and its own changes.",
            Examples = new[] { "wds setup client \"C:\\Games\\World of Warcraft\"", "wds setup client", "wds --home D:\\wds-test setup client D:\\WoW --no-index" },
            Run = async inv =>
            {
                var ctx = inv.Ctx;
                var folder = inv.ArgOrNull(0);
                if (folder == null)
                {
                    var cur = ctx.Settings.ClientDir;
                    if (cur == null) return CliResult.Fail(ExitCode.NotFound, "No World of Warcraft folder is set. Run \"wds setup client <folder>\".");
                    var c0 = ClientValidator.Check(cur);
                    return new CliResult { Code = c0.Ok ? ExitCode.Ok : ExitCode.NotFound }.With(new { client = cur, ok = c0.Ok, message = c0.Message, version = c0.Version })
                        .Line($"World of Warcraft folder: {cur}").Line(c0.Message);
                }
                var check = ClientValidator.Check(folder);
                if (!check.Ok) return new CliResult { Code = ExitCode.NotFound, Error = check.Message }.With(new { client = check.ClientDir, ok = false, message = check.Message, version = check.Version });
                if (ctx.Settings.ClientDir != null && !string.Equals(Path.GetFullPath(ctx.Settings.ClientDir), check.ClientDir, StringComparison.OrdinalIgnoreCase))
                {
                    var old = new AppPaths(ctx.Home == null ? null : Path.GetFullPath(ctx.Home)).ForClient(ctx.Settings.ClientDir);
                    if (File.Exists(old.ChangesFile) && new Workspace(old).HasLooseInstalls())
                        ctx.Err.WriteLine("note: you have loose files installed in the previous game folder. They stay installed; open that folder again later to remove them.");
                }
                ctx.Settings.ClientDir = check.ClientDir;
                ctx.SaveSettings();
                var r = new CliResult().Line($"World of Warcraft folder: {check.ClientDir}").Line(check.Message);
                int entries = 0;
                if (!inv.Flag("no-index"))
                {
                    ctx.ClientOverride = null;
                    var s = await ctx.SessionAsync();
                    entries = s.Entries.Count;
                    r.Line($"Game data organized: {entries:N0} entries. Names from the server database: {(s.ServerConnected ? "connected" : "not connected")} ({s.ServerMessage}).");
                }
                return r.With(new { client = check.ClientDir, ok = true, message = check.Message, version = check.Version, entries });
            },
        };
        yield return new CommandSpec
        {
            Path = "setup check",
            Summary = "check whether a folder is a World of Warcraft 1.12.1 (build 5875) game folder (nothing is saved)",
            Args = new[] { "<folder>" },
            NeedsClient = false,
            Examples = new[] { "wds setup check \"C:\\Games\\World of Warcraft\"" },
            Run = inv =>
            {
                var c = ClientValidator.Check(inv.Arg(0));
                return Task.FromResult(new CliResult { Code = c.Ok ? ExitCode.Ok : ExitCode.NotFound, Error = c.Ok ? null : c.Message }
                    .With(new { client = c.ClientDir, ok = c.Ok, message = c.Message, version = c.Version }).Line(c.Ok ? c.Message : ""));
            },
        };
        yield return new CommandSpec
        {
            Path = "setup status",
            Summary = "everything at a glance: version, folders, index, server database, workspace, release patch",
            NeedsClient = false,
            Examples = new[] { "wds setup status", "wds setup status --json" },
            Run = inv =>
            {
                var ctx = inv.Ctx;
                var st = ctx.Settings;
                var dir = ctx.ClientOverride ?? st.ClientDir;
                var check = dir == null ? null : ClientValidator.Check(dir);
                var r = new CliResult();
                r.Line($"WoW Data Studio {StudioInfo.Display}");
                r.Line($"App folder (--home): {ctx.Paths.Root}");
                r.Line($"World of Warcraft folder: {dir ?? "(not set: wds setup client <folder>)"}" + (check != null ? $" - {check.Message}" : ""));
                object? index = null, workspace = null, patch = null;
                if (check is { Ok: true })
                {
                    var cp = ctx.Paths.ForClient(check.ClientDir);
                    bool exists = File.Exists(cp.IndexFile);
                    bool current = false;
                    Dictionary<string, string> meta = new();
                    if (exists)
                    {
                        using var data = new ClientData(cp.DataDir);
                        current = AssetIndex.IsCurrent(cp.IndexFile, data);
                        using var ix = new AssetIndex(cp.IndexFile);
                        meta = ix.Meta();
                    }
                    index = new { file = cp.IndexFile, exists, current, built = meta.GetValueOrDefault("built"), server = meta.GetValueOrDefault("server") == "True", serverMessage = meta.GetValueOrDefault("server_message") };
                    r.Line($"Index: {(exists ? (current ? "up to date" : "out of date (rebuilt on the next command)") + $", built {meta.GetValueOrDefault("built")}" : "not built yet (wds index build)")}");
                    var ws = new Workspace(cp);
                    var list = ws.List();
                    var ps = ws.GetPatchState();
                    workspace = new { folder = cp.WorkspaceDir, changes = list.Count, changeSets = ws.Manifest.ChangeSets.Count, looseInstalled = ws.HasLooseInstalls() };
                    patch = new { path = ps.Path, owner = ps.Owner, text = ps.Text };
                    r.Line($"My changes: {Common.Plural(list.Count, "file")} in {Common.Plural(ws.Manifest.ChangeSets.Count, "change set")}; loose files installed: {(ws.HasLooseInstalls() ? "yes" : "no")}");
                    r.Line("Release patch: " + ps.Text);
                }
                r.Line($"Server database: {(st.Server.Enabled ? st.Server.Describe() : "not used")}");
                r.With(new
                {
                    version = StudioInfo.Version, build = StudioInfo.Build, home = ctx.Paths.Root, client = dir, clientOk = check?.Ok ?? false, clientMessage = check?.Message,
                    index, workspace, patch, serverDb = new { enabled = st.Server.Enabled, server = st.Server.Describe() },
                });
                return Task.FromResult(r);
            },
        };

        // ------------------------------------------------------------------ settings
        yield return new CommandSpec
        {
            Path = "settings list",
            Summary = "every setting with its value (the password only says whether it is set)",
            NeedsClient = false,
            Examples = new[] { "wds settings list", "wds settings list --json" },
            Run = inv =>
            {
                var s = inv.Ctx.Settings;
                var (noggit, tools, data) = SettingsCatalog.MapsChecks(s, s.ClientDir);
                var list = SettingsCatalog.All.Select(i => new { key = i.Key, value = SettingsCatalog.Get(s, i.Key, GameFolder(inv)), label = i.Label, help = i.Help, secret = i.Secret }).ToList();
                var r = new CliResult().With(new { settings = list, file = inv.Ctx.Paths.SettingsFile, checks = new { noggit, serverTools = tools, serverData = data } });
                foreach (var l in Common.Table(new[] { "Setting", "Value", "In the Settings window" }, list.Select(x => (IReadOnlyList<string>)new[] { x.key, x.value.Length == 0 ? "(empty)" : x.value, x.label }), 70)) r.Line(l);
                r.Line("").Line($"Noggit: {noggit}").Line($"Server tools: {tools}").Line($"Server data: {data}").Line($"File: {inv.Ctx.Paths.SettingsFile}");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "settings get",
            Summary = "the value of one setting",
            Args = new[] { "<key>" },
            NeedsClient = false,
            Examples = new[] { "wds settings get client", "wds settings get server.host" },
            Run = inv =>
            {
                var info = SettingsCatalog.Info(inv.Arg(0)) ?? throw new CliError(ExitCode.NotFound, $"unknown setting \"{inv.Arg(0)}\". The settings: {string.Join(", ", SettingsCatalog.All.Select(a => a.Key))}.");
                var v = SettingsCatalog.Get(inv.Ctx.Settings, info.Key, GameFolder(inv));
                return Task.FromResult(new CliResult().With(new { key = info.Key, value = v, secret = info.Secret }).Line(v));
            },
        };
        yield return new CommandSpec
        {
            Path = "settings set",
            Summary = "change one setting (checked like the Settings window); --stdin reads the value (use it for the password)",
            Args = new[] { "<key>", "[value]" },
            NeedsClient = false,
            Options = new[] { new OptionSpec("stdin", null, "read the value from the first line of stdin (not visible in the shell history)") },
            Details = "Keys: " + string.Join(", ", SettingsCatalog.All.Select(a => a.Key)) + ". The password is stored encrypted for your Windows user and never printed.",
            Examples = new[] { "wds settings set server.port 3310", "wds settings set ground-textures specular", "wds settings set server.password --stdin < password.txt", "wds settings set dbc-locale deDE", "wds settings set hot-reload on --yes" },
            Run = inv =>
            {
                var info = SettingsCatalog.Info(inv.Arg(0)) ?? throw new CliError(ExitCode.NotFound, $"unknown setting \"{inv.Arg(0)}\". The settings: {string.Join(", ", SettingsCatalog.All.Select(a => a.Key))}.");
                string? value = inv.Flag("stdin") ? inv.Ctx.In.ReadLine() : inv.ArgOrNull(1);
                if (value == null) throw new CliError(ExitCode.Usage, $"give the value of {info.Key} (or --stdin).");
                if (info.Key == "hot-reload" && !inv.Yes)
                    return Task.FromResult(Common.NeedsYes(new CliResult().Line($"hot-reload = {value} for {GameFolder(inv) ?? inv.Ctx.Settings.ClientDir}"),
                        "This writes (on) or deletes (off) wowhotreload.txt in the game folder."));
                try { SettingsCatalog.Set(inv.Ctx.Settings, info.Key, value, GameFolder(inv)); }
                catch (ArgumentException e) { return Task.FromResult(CliResult.Fail(ExitCode.ValidationFailed, e.Message)); }
                inv.Ctx.SaveSettings();
                var shown = SettingsCatalog.Get(inv.Ctx.Settings, info.Key, GameFolder(inv));
                var r = new CliResult().With(new { key = info.Key, value = shown }).Line($"{info.Key} = {(shown.Length == 0 ? "(empty)" : shown)}");
                if (info.Key.StartsWith("server.", StringComparison.Ordinal)) r.Line("Names are read again with \"wds serverdb reload\".");
                if (info.Key == "server.password" && !inv.Flag("stdin")) r.Warnings.Add("the password was typed on the command line (it may stay in the shell's history): --stdin avoids that.");
                if (info.Key == "hot-reload") r.Line(shown == "on" ? "The game reads it when it starts: restart the game once, then \"wds changes reload\" (or \"Reload in running game\") works." : "Hot reload is off for this game folder (a running game keeps what it started with until it is restarted).");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "settings unset",
            Summary = "put one setting back to its default (folders: found automatically)",
            Args = new[] { "<key>" },
            NeedsClient = false,
            Examples = new[] { "wds settings unset noggit", "wds settings unset export-dir" },
            Run = inv =>
            {
                var info = SettingsCatalog.Info(inv.Arg(0)) ?? throw new CliError(ExitCode.NotFound, $"unknown setting \"{inv.Arg(0)}\".");
                if (info.Key == "hot-reload" && !inv.Yes)
                    return Task.FromResult(Common.NeedsYes(new CliResult().Line("hot-reload = off"), "This deletes wowhotreload.txt in the game folder."));
                SettingsCatalog.Unset(inv.Ctx.Settings, info.Key, GameFolder(inv));
                inv.Ctx.SaveSettings();
                var shown = SettingsCatalog.Get(inv.Ctx.Settings, info.Key, GameFolder(inv));
                return Task.FromResult(new CliResult().With(new { key = info.Key, value = shown }).Line($"{info.Key} = {(shown.Length == 0 ? "(empty)" : shown)}"));
            },
        };

        // ------------------------------------------------------------------ server database
        yield return new CommandSpec
        {
            Path = "serverdb test",
            Summary = "try to connect to the server database with the saved settings (\"Test connection\"); it is only read",
            NeedsClient = false,
            Examples = new[] { "wds serverdb test" },
            Run = async inv =>
            {
                var s = inv.Ctx.Settings.Server;
                var (ok, msg) = await new ServerDb(s).TestAsync();
                return new CliResult { Code = ok ? ExitCode.Ok : ExitCode.Error, Error = ok ? null : "Not connected: " + msg }
                    .With(new { connected = ok, message = msg, server = s.Describe() }).Line(ok ? "Connected: " + msg : "");
            },
        };
        yield return new CommandSpec
        {
            Path = "serverdb defaults",
            Summary = "use the defaults of a local test server (127.0.0.1:3306, root/root, mangos)",
            NeedsClient = false,
            Examples = new[] { "wds serverdb defaults" },
            Run = inv =>
            {
                SettingsCatalog.UseServerDefaults(inv.Ctx.Settings);
                inv.Ctx.SaveSettings();
                return Task.FromResult(new CliResult().With(new { server = inv.Ctx.Settings.Server.Describe() }).Line($"Server database: {inv.Ctx.Settings.Server.Describe()}"));
            },
        };
        yield return new CommandSpec
        {
            Path = "serverdb reload",
            Summary = "read the names, spawns, loot and quests from the server database again (File > Reload names from the server database)",
            Examples = new[] { "wds serverdb reload" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                inv.Ctx.Progress("Loading names from the server database...");
                var (ok, msg) = await s.RefreshServerAsync();
                return new CliResult { Code = ok ? ExitCode.Ok : ExitCode.Error, Error = ok ? null : $"The names could not be loaded: {msg}. Creatures, items and game objects are listed by their model and display id instead." }
                    .With(new { connected = ok, message = msg, entries = s.Entries.Count }).Line(ok ? "Names from the server database: connected. " + msg : "");
            },
        };

        // ------------------------------------------------------------------ index
        yield return new CommandSpec
        {
            Path = "index build",
            Summary = "organize the game data (build the index) when it is missing or out of date; --rebuild always (File > Rebuild the index)",
            Options = new[] { new OptionSpec("rebuild", null, "build it again even when it is up to date (your changes are kept)") },
            Examples = new[] { "wds index build", "wds index build --rebuild" },
            Run = async inv =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var s = await inv.Ctx.SessionAsync(inv.Flag("rebuild"));
                var counts = s.Entries.GroupBy(e => EntrySearch.CliName(e.Kind)).ToDictionary(g => g.Key, g => g.Count());
                return new CliResult().With(new { index = s.Client!.IndexFile, entries = s.Entries.Count, kinds = counts, built = s.IndexBuilt, server = s.ServerConnected, serverMessage = s.ServerMessage, seconds = sw.Elapsed.TotalSeconds })
                    .Line($"Index: {s.Client.IndexFile}").Line($"{s.Entries.Count:N0} entries ({string.Join(", ", counts.Select(kv => $"{kv.Value:N0} {kv.Key}"))}), built {s.IndexBuilt}.")
                    .Line($"Names from the server database: {(s.ServerConnected ? "connected" : "not connected")} ({s.ServerMessage}).");
            },
        };
        yield return new CommandSpec
        {
            Path = "index status",
            Summary = "whether the index is built and up to date, when, and whether it has the server's names",
            Examples = new[] { "wds index status --json" },
            Run = inv =>
            {
                var dir = inv.Ctx.ClientDir();
                var cp = inv.Ctx.Paths.ForClient(dir);
                bool exists = File.Exists(cp.IndexFile), current = false;
                Dictionary<string, string> meta = new();
                if (exists)
                {
                    using var data = new ClientData(cp.DataDir);
                    current = AssetIndex.IsCurrent(cp.IndexFile, data);
                    using var ix = new AssetIndex(cp.IndexFile);
                    meta = ix.Meta();
                }
                bool server = meta.GetValueOrDefault("server") == "True";
                var r = new CliResult().With(new { file = cp.IndexFile, exists, current, built = meta.GetValueOrDefault("built"), schema = meta.GetValueOrDefault("schema_version"), server, serverMessage = meta.GetValueOrDefault("server_message") });
                r.Line(!exists ? "The index is not built yet: \"wds index build\" (or any command that needs it) builds it." : $"Index {(current ? "up to date" : "out of date")}, built {meta.GetValueOrDefault("built")}: {cp.IndexFile}");
                if (exists) r.Line(server ? "Names from server database: connected" : "Names from server database: not connected" + $" ({meta.GetValueOrDefault("server_message")})");
                return Task.FromResult(r);
            },
        };
    }
}
