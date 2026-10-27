using WoWData.Core.Index;
using WoWData.Core.Mpq;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;

namespace WoWData.Cli;

/// <summary>
/// One run of wds: the global options (--home, --client, --json, --quiet, --yes, --dry-run), the output streams (results
/// on stdout, progress on stderr), the settings, and the game folder opened on demand (archives, index, workspace) through
/// the same <see cref="AppSession"/> the desktop app uses.
/// </summary>
public sealed class CliContext : IAsyncDisposable
{
    public CliContext(TextWriter stdout, TextWriter stderr, TextReader stdin)
    {
        Out = stdout;
        Err = stderr;
        In = stdin;
    }

    public TextWriter Out { get; }
    public TextWriter Err { get; }
    public TextReader In { get; }

    public string? Home { get; set; }
    public string? ClientOverride { get; set; }
    /// <summary>M1: --version &lt;client&gt;: open another version read only instead of your game.</summary>
    public string? VersionOverride { get; set; }
    public bool Json { get; set; }
    public bool Quiet { get; set; }
    public bool Yes { get; set; }
    public bool DryRun { get; set; }

    private AppPaths? _paths;
    private AppSettings? _settings;
    private AppSession? _session;

    /// <summary>The app's folder: --home, else WOWDATASTUDIO_HOME, else %APPDATA%\WoWDataStudio (the desktop app's).</summary>
    public AppPaths Paths => _paths ??= new AppPaths(Home == null ? null : Path.GetFullPath(Home));

    public AppSettings Settings => _settings ??= AppSettings.Load(Paths);

    public void SaveSettings() => Settings.Save(Paths);

    /// <summary>Progress and notes for people (stderr; not shown with --quiet or --json).</summary>
    public void Progress(string text)
    {
        if (!Quiet && !Json) Err.WriteLine(text);
    }

    /// <summary>The game folder of this run: --client, else the one in the settings. Checked (1.12.1, build 5875).</summary>
    public string ClientDir()
    {
        var dir = ClientOverride ?? Settings.ClientDir;
        if (string.IsNullOrWhiteSpace(dir))
            throw new CliError(ExitCode.NotFound, "No World of Warcraft folder is set. Run \"wds setup client <folder>\" (or pass --client <folder>).");
        var check = ClientValidator.Check(dir);
        if (!check.Ok) throw new CliError(ExitCode.NotFound, check.Message);
        return check.ClientDir;
    }

    /// <summary>The open game folder (archives, the index, the workspace). The first time, the index is built ("Organizing
    /// game data"), as the app does when it opens a folder; the progress goes to stderr.</summary>
    public async Task<AppSession> SessionAsync(bool forceRebuild = false)
    {
        if (VersionOverride != null) return await OtherSessionAsync(VersionOverride, forceRebuild);
        if (_session != null && !forceRebuild) return _session;
        var dir = ClientDir();
        _session?.Dispose();
        _session = new AppSession(Paths, Settings);
        var cp = Paths.ForClient(dir);
        if (forceRebuild || !File.Exists(cp.IndexFile)) Progress($"Organizing game data of {dir} (once per game folder; the game files are only read)...");
        string last = "";
        await _session.OpenAsync(dir, forceRebuild, new SyncProgress<BuildProgress>(p =>
        {
            if (p.Stage == last) return;
            last = p.Stage;
            Progress($"  [{p.StageNumber}/{p.StageCount}] {p.Stage}");
        }));
        return _session;
    }

    // ------------------------------------------------------------------ M1: another version, read only

    private List<ClientCopy>? _found;

    /// <summary>The clients in the folder of other versions (the other-clients setting); empty when it is not set.</summary>
    public List<ClientCopy> FoundClients()
    {
        if (_found != null) return _found;
        var root = Settings.OtherClientsDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return _found = new List<ClientCopy>();
        Progress($"Looking for clients in {root}...");
        return _found = ClientCopies.Find(root);
    }

    /// <summary>The client of --version or "wds versions open": its folder, its name in the folder of other versions, a
    /// version (1.60) or a build (70450).</summary>
    public ClientCopy ResolveOther(string text)
    {
        var t = text.Trim().Trim('"');
        bool isPath = Directory.Exists(t) || File.Exists(t);
        if (!isPath && FoundClients().Count == 0)
            throw new CliError(ExitCode.Usage, $"\"{t}\" is not a folder, and no folder of other versions is set: give the client's folder, or set the folder once with \"wds settings set other-clients <folder>\".");
        try { return ClientPick.Resolve(t, isPath ? new List<ClientCopy>() : FoundClients()); }
        catch (ArgumentException e) { throw new CliError(ExitCode.NotFound, e.Message); }
    }

    /// <summary>"1.60.1.70450" for a client (the CASC build when its executable has no version resource).</summary>
    public string OtherVersionText(string text)
    {
        try
        {
            var c = ResolveOther(text);
            if (c.Build > 0) return $"{c.Version}.{c.Build}";
            if (c.Format == ClientFormat.Casc)
            {
                using var st = WoWData.Core.Casc.CascStorage.Open(c.Folder);
                return st.Version;
            }
            return c.Name;
        }
        catch (Exception e) when (e is CliError or IOException or InvalidDataException or ArgumentException) { return text; }
    }

    /// <summary>
    /// Opens another version read only (the GUI's "Open read only"): its index is built the first time ("Organizing
    /// game data"), from its own tables and file names, your 1.12 game's file names (when a game folder is set) and the
    /// listfile you chose (other-clients.listfile, or <paramref name="listfile"/>).
    /// </summary>
    public async Task<AppSession> OtherSessionAsync(string text, bool forceRebuild = false, string? listfile = null)
    {
        if (_session != null && !forceRebuild && _session.IsReadOnly) return _session;
        var copy = ResolveOther(text);
        var lf = listfile ?? Settings.OtherClientsListfile;
        if (!string.IsNullOrWhiteSpace(lf) && !File.Exists(lf)) throw new CliError(ExitCode.NotFound, $"the listfile \"{lf}\" does not exist.");
        ClientData? c112 = null;
        var dir = ClientOverride ?? Settings.ClientDir;
        if (!string.IsNullOrWhiteSpace(dir) && ClientValidator.Check(dir) is { Ok: true } check)
        {
            try { c112 = new ClientData(Path.Combine(check.ClientDir, "Data")); }
            catch (Exception e) when (e is IOException or MpqException) { c112 = null; }
        }
        _session?.Dispose();
        _session = new AppSession(Paths, Settings);
        var cp = Paths.ForClient(copy.Folder);
        if (forceRebuild || !File.Exists(cp.IndexFile)) Progress($"Organizing game data of {copy.Name} (read only; once per client and listfile)...");
        string last = "";
        try
        {
            await _session.OpenOtherAsync(copy, new OtherOpenOptions { Listfile = string.IsNullOrWhiteSpace(lf) ? null : lf, Client112 = c112, Found = FoundClients(), ForceRebuild = forceRebuild },
                new SyncProgress<BuildProgress>(p =>
                {
                    if (p.Stage == last) return;
                    last = p.Stage;
                    Progress($"  [{p.StageNumber}/{p.StageCount}] {p.Stage}");
                }));
        }
        catch (NotSupportedException e) { throw new CliError(ExitCode.Refused, e.Message); }
        catch (Exception e) when (e is WoWData.Core.Casc.CascMissingException or InvalidDataException) { throw new CliError(ExitCode.NotFound, $"{copy.Name} could not be opened: {e.Message}"); }
        finally { c112?.Dispose(); }
        return _session;
    }

    public ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A progress reporter that reports at once on the calling thread (no synchronization context in a console).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public SyncProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}
