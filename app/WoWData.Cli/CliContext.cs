using WoWData.Core.Index;
using WoWData.Core.Session;
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
