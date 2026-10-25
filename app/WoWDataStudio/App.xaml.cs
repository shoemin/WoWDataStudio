using System.Windows;
using System.Windows.Threading;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio;

/// <summary>
/// Start-up. Command-line options:
///   --client &lt;folder&gt;      use this World of Warcraft folder (skips the welcome screen when valid)
///   --home &lt;folder&gt;        keep settings, index and workspace there instead of %APPDATA%\WoWDataStudio
///   --screenshot &lt;folder&gt;  render the main screens to PNG files in that folder and exit (uses a temporary home
///                         unless --home is given; never installs anything into the game folder)
///   --render &lt;model&gt; &lt;png&gt; [--display N] [--sequence N] [--time ms] [--yaw deg] [--size WxH] [--original]
///                         developer tool: render one model offscreen to a PNG and exit (needs --client); shows your
///                         workspace version (edited model / textures) unless --original
///   --leaktest &lt;folder&gt; [--cycles N] [--only view,view]
///                         developer tool: open and close the views many times, measure the memory after each cycle,
///                         write leak_*.csv and leak_summary.txt into the folder and exit (a temporary home like
///                         --screenshot, the server database off; see LeakTestRunner and MAINTAINING.md)
/// </summary>
public partial class App : Application
{
    public static string LogFile => Path.Combine(new AppPaths().Root, "error.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Manual.RegisterF1();
        string? client = null, home = null, shots = null, leak = null, only = null;
        int cycles = 100;
        int render = Array.FindIndex(e.Args, a => a.Equals("--render", StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < e.Args.Length; i++)
        {
            string a = e.Args[i];
            string? Next() => i + 1 < e.Args.Length ? e.Args[++i] : null;
            if (a.Equals("--client", StringComparison.OrdinalIgnoreCase)) client = Next();
            else if (a.Equals("--home", StringComparison.OrdinalIgnoreCase)) home = Next();
            else if (a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase)) shots = Next();
            else if (a.Equals("--leaktest", StringComparison.OrdinalIgnoreCase)) leak = Next();
            else if (a.Equals("--cycles", StringComparison.OrdinalIgnoreCase) && int.TryParse(Next(), out var n) && n > 1) cycles = n;
            else if (a.Equals("--only", StringComparison.OrdinalIgnoreCase)) only = Next();
        }
        if (leak != null)
        {
            // always its own home (never the user's settings, index or workspace), and the error log goes there too
            home = Path.Combine(Path.GetFullPath(leak), "_home");
            Environment.SetEnvironmentVariable("WOWDATASTUDIO_HOME", home);
            var lpaths = new AppPaths(home);
            var lsettings = AppSettings.Load(lpaths);
            if (client != null) lsettings.ClientDir = client;
            lsettings.Server.Enabled = false;     // the Server data tab and the designers get a throwaway server
            var lvm = new MainViewModel(new AppSession(lpaths, lsettings));
            var lwin = new MainWindow(lvm);
            MainWindow = lwin;
            var views = only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Shutdown(await LeakTestRunner.RunAsync(lwin, lvm, leak, client, cycles, views));
            return;
        }
        if (render >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RenderCli.RunAsync(client ?? AppSettings.Load(new AppPaths(home)).ClientDir ?? "", e.Args.Skip(render + 1).ToArray(), home));
            return;
        }
        if (shots != null && home == null) home = Path.Combine(Path.GetFullPath(shots), "_home");
        var paths = new AppPaths(home);
        var settings = AppSettings.Load(paths);
        if (client != null) settings.ClientDir = client;
        var session = new AppSession(paths, settings);
        var vm = new MainViewModel(session);
        var win = new MainWindow(vm);
        MainWindow = win;
        if (shots != null)
        {
            int code = await ScreenshotRunner.RunAsync(win, vm, shots, client);
            Shutdown(code);
            return;
        }
        win.Show();
        await vm.StartAsync();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch (IOException) { }
        if (ScreenshotRunner.Running)
        {
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            ScreenshotRunner.Failures.Add(e.Exception.Message);
            return;
        }
        Dialogs.Error("Something went wrong",
            "WoW Data Studio ran into an unexpected problem. Your files are safe; you can keep working or restart the app.",
            $"{e.Exception.Message}\n\nDetails were written to {LogFile}");
        e.Handled = true;
    }
}
