using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.HotReload;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>0.11.0: "Reload in running game" (hot reload): the state line of My changes, the button, and the offer after an
/// install. The logic is WoWData.Core.HotReload (the same as "wds changes reload").</summary>
public sealed partial class BrowserViewModel
{
    [ObservableProperty] private string _hotReloadText = "";
    [ObservableProperty] private bool _hotReloadReady;
    private DispatcherTimer? _hotReloadTimer;
    private bool _hotReloadBusy;

    /// <summary>Screenshots and tests: a stand-in for the running game (else the real one of the game folder is looked for).</summary>
    internal IGameLocator? HotReloadLocator { get; set; }

    private HotReloadService? HotReloadFor() => string.IsNullOrEmpty(ClientDir) ? null : new HotReloadService(ClientDir, HotReloadLocator);

    /// <summary>Starts the quick state check (process + pipe, no question to the game) every 4 seconds.</summary>
    private void InitHotReload()
    {
        _hotReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hotReloadTimer.Tick += (_, _) => _ = RefreshHotReloadAsync();
        _hotReloadTimer.Start();
        _ = RefreshHotReloadAsync();
    }

    internal async Task RefreshHotReloadAsync()
    {
        var hr = HotReloadFor();
        if (hr == null || _hotReloadBusy) return;
        try
        {
            var probe = await Task.Run(() => hr.ProbeAsync(false));
            HotReloadReady = probe.Ready;
            HotReloadText = probe.Short;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HotReloadReady = false;
            HotReloadText = "";
        }
    }

    /// <summary>"Reload in running game": every change of the workspace (and the loose files removed since) is read again by the game.</summary>
    [RelayCommand]
    private async Task ReloadInGame()
    {
        var ws = _session.Workspace;
        if (ws == null) return;
        await ReloadInGameAsync(HotReloadService.DefaultPaths(ws), askFirst: false);
    }

    /// <summary>Reloads files in the running game and shows the result. With <paramref name="askFirst"/> only when the game takes
    /// reloads (the offer after an install); otherwise the reason it cannot is shown.</summary>
    internal async Task<HotReloadOutcome?> ReloadInGameAsync(IReadOnlyCollection<string> paths, bool askFirst)
    {
        var hr = HotReloadFor();
        if (hr == null) return null;
        _hotReloadBusy = true;
        try
        {
            var probe = await _main.RunBusyAsync("Asking the game...", () => Task.Run(() => hr.ProbeAsync(true)));
            HotReloadReady = probe.Ready;
            HotReloadText = probe.Short;
            if (!probe.Ready)
            {
                if (!askFirst) Dialogs.Show("Reload in running game", probe.Message, probe.State == HotReloadState.NoGame ? DialogKind.Info : DialogKind.Warning, new[] { "OK" },
                    null, "How hot reload works (HOT_RELOAD.md)", _main.HotReloadDoc);
                return null;
            }
            if (paths.Count == 0)
            {
                Dialogs.Info("Reload in running game", "There is nothing to reload: you have no changes yet.");
                return null;
            }
            var outcome = await _main.RunBusyAsync("Reloading in the running game...", () => Task.Run(() => hr.ReloadAsync(paths)));
            ShowReloadOutcome(outcome);
            return outcome;
        }
        finally
        {
            _hotReloadBusy = false;
        }
    }

    /// <summary>The result dialog: what is live now, what needs a relog or a restart; one line per file in the details.</summary>
    internal static void ShowReloadOutcome(HotReloadOutcome o)
    {
        if (!o.Done || o.Report == null)
        {
            Dialogs.Show("Reload in running game", o.Message, DialogKind.Warning, new[] { "OK" });
            return;
        }
        var r = o.Report;
        var kind = r.Problems.Any() ? DialogKind.Warning : r.Relog.Any() || r.Restart.Any() ? DialogKind.Info : DialogKind.Success;
        Dialogs.Show("Reloaded in the running game", o.Text, kind, new[] { "OK" }, HotReloadService.Details(r));
    }

    /// <summary>After a revert that took loose files out of the game folder: the offer only when the game takes reloads (otherwise the
    /// revert's own message is enough).</summary>
    internal async Task OfferReloadIfReadyAsync(IReadOnlyCollection<string> paths, string title)
    {
        if (paths.Count == 0) return;
        await RefreshHotReloadAsync();
        if (!HotReloadReady) return;
        if (Dialogs.Show(title, "The loose copy was taken out of the game folder. The game is running with hot reload: it can go back to the original now, without a restart.",
                DialogKind.Question, new[] { "Reload in running game", "Not now" }, string.Join("\n", paths), "How hot reload works (HOT_RELOAD.md)", _main.HotReloadDoc) == 0)
            await ReloadInGameAsync(paths, askFirst: true);
    }

    /// <summary>After an install or a removal: when the game runs with hot reload, offer to reload the files now; otherwise the
    /// usual message (restart the game).</summary>
    internal async Task AfterGameFilesChangedAsync(string title, string message, string details, IReadOnlyCollection<string> paths)
    {
        await RefreshHotReloadAsync();
        if (!HotReloadReady || paths.Count == 0)
        {
            Dialogs.Success(title, message, details);
            return;
        }
        var rest = message.Replace(" Restart the game to see the change.", "").Replace("Restart the game to see the original again.", "").Trim();
        var text = (rest.Length > 0 ? rest + "\n\n" : "") + "The game is running with hot reload: it can read the changed files now, without a restart.";
        if (Dialogs.Show(title, text, DialogKind.Success, new[] { "Reload in running game", "Not now" }, details, "How hot reload works (HOT_RELOAD.md)", _main.HotReloadDoc) == 0)
            await ReloadInGameAsync(paths, askFirst: true);
    }
}
