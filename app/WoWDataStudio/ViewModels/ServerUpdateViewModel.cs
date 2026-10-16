using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Maps;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// "Update server maps" (guided): 1. check that the map changes are in the installed patch-3.MPQ (the extractors read
/// the archives, not loose files); 2. reextract.ps1 into the staging folder with progress and the change report;
/// 3. the user stops the world server, then swap_in.ps1 installs the changed files with a backup (it refuses while
/// mangosd.exe runs); 4. "Undo server update" = swap_in.ps1 -Restore &lt;backup&gt;. The app never stops or starts the server.
/// 0.8.0: the steps themselves are <see cref="ServerMapsUpdater"/> in Core (shared with "wds server-maps ...").
/// </summary>
public sealed partial class ServerUpdateViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;
    public ServerMapsUpdater Updater { get; }
    public ServerMapsService? Service => Updater.Service;

    public ServerUpdateViewModel(AppSession session, MainViewModel main, MapCatalog catalog)
    {
        _main = main;
        Updater = new ServerMapsUpdater(session, catalog);
        ServerDataDir = Updater.ServerDataDir;
        ToolsDir = Updater.ToolsDir ?? "(not found)";
        MapIds = Updater.MapIds;
        ChangedMaps = Updater.ChangedMapsText;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => CheckServer();
        _timer.Start();
        CheckServer();
        CheckPatch();
        LoadHistory();
        var rep = Updater.Report();
        if (rep != null)
        {
            ReportLines = rep.Lines();
            ReportNote = $"The staging folder already holds an extraction (report: {Path.Combine(Service!.Paths.Staging, "changes.txt")}). Extract again after new map changes.";
        }
    }

    public string ToolsDir { get; }
    public bool ToolsFound => Service != null;
    public bool ToolsMissing => Service == null;
    public string ServerDataDir { get; }
    public string StagingDir => Service?.Paths.Staging ?? "";
    public string BackupDir => Service?.Paths.Backups ?? "";
    public List<int> MapIds { get; }
    public string ChangedMaps { get; }
    public string MapsArgument => MapIds.Count == 0 ? "all maps" : string.Join(",", MapIds);

    [ObservableProperty] private string _patchStatus = "";
    [ObservableProperty] private bool _patchOk;
    [ObservableProperty] private bool _stepMaps = true;
    [ObservableProperty] private bool _stepVmaps = true;
    [ObservableProperty] private bool _stepMmaps = true;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private List<string> _reportLines = new();
    [ObservableProperty] private string _reportNote = "";
    [ObservableProperty] private bool _serverRunning;
    [ObservableProperty] private string _serverStatus = "";
    [ObservableProperty] private string _installResult = "";
    [ObservableProperty] private string _lastBackup = "";
    [ObservableProperty] private string _undoText = "No server update was done through the app yet.";
    [ObservableProperty] private bool _canUndo;

    public bool HasReport => ReportLines.Count > 0;
    partial void OnReportLinesChanged(List<string> value) => OnPropertyChanged(nameof(HasReport));

    public const string StopHowTo = ServerMapsUpdater.StopHowTo;
    public const string MmapsCaveat = ServerMapsUpdater.MmapsCaveat;

    public void CheckServer() => (ServerRunning, ServerStatus) = Updater.ServerState();

    [RelayCommand]
    private void CheckServerNow() => CheckServer();

    [RelayCommand]
    private void CheckPatch() => (PatchOk, PatchStatus) = Updater.PatchState();

    [RelayCommand]
    private void BuildPatch() => _main.Browser?.BuildPatchCommand.Execute(null);

    private void Log(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > 60000) _log.Remove(0, _log.Length - 50000);
        LogText = _log.ToString();
    }

    private List<string> Steps()
    {
        var s = new List<string>();
        if (StepMaps) s.Add("maps");
        if (StepVmaps) s.Add("vmaps");
        if (StepMmaps) s.Add("mmaps");
        return s;
    }

    [RelayCommand]
    private async Task Extract()
    {
        if (Service == null) return;
        var steps = Steps();
        if (steps.Count == 0)
        {
            Dialogs.Info("Nothing selected", "Tick at least one of terrain, collision or pathing.");
            return;
        }
        if (!PatchOk && MapIds.Count > 0 &&
            !Dialogs.Confirm("Your changes are not in patch-3.MPQ", PatchStatus + "\n\nExtract anyway? The server data would then not contain those changes.", "Extract anyway", "Cancel", DialogKind.Warning)) return;
        Busy = true;
        _log.Clear();
        LogText = "";
        Progress = 0;
        ProgressText = "Starting the extraction...";
        _cts = new CancellationTokenSource();
        var progress = new Progress<ServerProgress>(p =>
        {
            Log(p.Line);
            ProgressText = p.Stage + ": " + p.Line.Trim();
            if (p.Fraction >= 0) Progress = p.Fraction * 100;
        });
        try
        {
            var (r, rep, text) = await Updater.ExtractAsync(steps, progress, _cts.Token);
            Progress = 100;
            ReportLines = rep?.Lines() ?? new List<string>();
            ProgressText = text;
            if (r.Ok) ReportNote = rep == null ? "" : rep.ToInstall == 0 ? "The staging folder matches the server's data: there is nothing to install." : $"{rep.ToInstall} file(s) differ from the server's data. Next: stop the world server, then \"Install into the server\".";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled. The server's data was not touched.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ProgressText = "The extraction could not run: " + e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private Task DryRun() => SwapAsync(true);

    [RelayCommand]
    private Task Install() => SwapAsync(false);

    private async Task SwapAsync(bool dryRun)
    {
        if (Service == null) return;
        CheckServer();
        if (!dryRun && ServerRunning)
        {
            Dialogs.Show("Stop the world server first", ServerStatus + "\n\n" + StopHowTo, DialogKind.Warning, new[] { "OK" });
            return;
        }
        Busy = true;
        _log.Clear();
        ProgressText = dryRun ? "Checking what would change..." : "Installing into the server's data (with a backup)...";
        Progress = 0;
        try
        {
            var r = await Updater.InstallAsync(Steps(), dryRun, new Progress<ServerProgress>(p => Log(p.Line)));
            InstallResult = r.Message;
            if (r.RefusedServerRuns) CheckServer();
            if (r.Backup != null) LastBackup = r.Backup;
            LoadHistory();
            Progress = 100;
            ProgressText = dryRun ? "Checked." : "Finished.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            InstallResult = "The install could not run: " + e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void LoadHistory()
    {
        CanUndo = Updater.LastUpdate() != null;
        UndoText = Updater.UndoText;
    }

    [RelayCommand]
    private async Task Undo()
    {
        if (Service == null) return;
        var last = Updater.LastUpdate();
        if (last == null) return;
        CheckServer();
        if (ServerRunning)
        {
            Dialogs.Show("Stop the world server first", "Undoing also needs the world server stopped.\n\n" + StopHowTo, DialogKind.Warning, new[] { "OK" });
            return;
        }
        if (!Dialogs.Confirm("Undo server update", $"Put the server's data back as it was before the update of {last.Time:yyyy-MM-dd HH:mm}? The replaced files are restored from {last.Backup} and the files the update added are removed.", "Undo server update", "Cancel", DialogKind.Warning)) return;
        Busy = true;
        _log.Clear();
        try
        {
            var r = await Updater.UndoAsync(new Progress<ServerProgress>(p => Log(p.Line)));
            InstallResult = r.Message;
            LoadHistory();
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void OpenStaging()
    {
        if (Directory.Exists(StagingDir)) Dialogs.OpenPath(StagingDir);
    }

    [RelayCommand]
    private void OpenReport()
    {
        var f = Path.Combine(StagingDir, "changes.txt");
        if (File.Exists(f)) Dialogs.OpenPath(f);
    }

    [RelayCommand]
    private void OpenBackup()
    {
        if (Directory.Exists(LastBackup)) Dialogs.OpenPath(LastBackup);
        else if (Directory.Exists(BackupDir)) Dialogs.OpenPath(BackupDir);
    }

    [RelayCommand]
    private void OpenReadme()
    {
        var f = Path.Combine(ToolsDir, "README.md");
        if (File.Exists(f)) Dialogs.OpenPath(f);
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
    }
}
