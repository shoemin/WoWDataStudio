using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// `--leaktest &lt;folder&gt; [--cycles N] [--only view,view]` (developers, docs\manual\MAINTAINING.md): opens and closes the
/// app's views many times with the real game data and measures the memory after every cycle, to find leaks (event
/// handlers that keep closed windows alive, GL / Direct3D resources not freed, caches without a bound).
/// Like `--screenshot` it uses its own temporary app folder (settings, index, workspace) in &lt;folder&gt;\_home, never
/// installs anything, keeps the server database off and gives the Server data tab and the designers a throwaway MySQL
/// server loaded from the tests' fixture. Windows are drawn off screen.
/// After every cycle: a full blocking, compacting garbage collection, then the managed heap (GC.GetTotalMemory),
/// private bytes, working set, handles, GDI and USER objects, GL contexts and objects, and how many of the windows and
/// view models closed so far are still alive (weak references). One CSV per view (leak_&lt;view&gt;.csv) and a summary
/// (leak_summary.txt / .csv) with the slope of each measure per cycle, the first 10% of the cycles left out as warm-up.
/// Exit code: 0 = no view grows, 1 = a view looks like it leaks, 2 = the run itself failed.
/// </summary>
public static class LeakTestRunner
{
    /// <summary>The views, in the order they run; the 3D viewer and its pop-out run twice as many cycles.</summary>
    public static readonly string[] Views =
    {
        "model_viewer", "model_popout", "model_compare", "model_browse", "texture_viewer", "maps_tab", "terrain_popout",
        "data_tables", "server_data", "spell_designer", "class_designer", "quest_designer", "other_versions", "versions_window",
    };

    private static readonly HashSet<string> Doubled = new() { "model_viewer", "model_popout", "model_browse" };
    private static readonly HashSet<string> NeedServer = new() { "server_data", "spell_designer", "class_designer", "quest_designer" };

    private static StreamWriter? _log;
    private static readonly List<(WeakReference Ref, string What)> Watched = new();
    private static int _cycle;

    private static void Log(string s)
    {
        _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
        _log?.Flush();
        Console.WriteLine(s);
    }

    /// <summary>Remember an object that must be collected once its window closed (counted after each cycle).</summary>
    private static void Watch(object? o, string what)
    {
        if (o != null) Watched.Add((new WeakReference(o), $"{what} (cycle {_cycle})"));
    }

    // ---------------------------------------------------------------- measuring

    public sealed record Sample(int Cycle, double Seconds, long Managed, long Private, long WorkingSet, int Handles, int Gdi, int User,
        int Threads, int GlContexts, int GlObjects, int Alive, string Item);

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returned);

    /// <summary>This process's handles by kernel object type (Event, Thread, Section, File...): which handles a view
    /// leaks. Empty when Windows does not answer.</summary>
    public static Dictionary<string, int> HandleTypes() => HandleTypes(false);

    /// <summary>With <paramref name="named"/>: the Mutant and Section handles by object name (digits as #), the
    /// types whose handles a GL context leaves behind.</summary>
    public static Dictionary<string, int> HandleTypes(bool named)
    {
        var result = new Dictionary<string, int>();
        if (IntPtr.Size != 8) return result;
        int size = 1 << 22;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (int tries = 0; tries < 6; tries++)
            {
                buf = Marshal.AllocHGlobal(size);
                int st = NtQuerySystemInformation(64, buf, size, out int needed);     // SystemExtendedHandleInformation
                if (st == 0) break;
                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                if (st != unchecked((int)0xC0000004)) return result;                // not STATUS_INFO_LENGTH_MISMATCH
                size = Math.Max(size * 2, needed + (1 << 20));
            }
            if (buf == IntPtr.Zero) return result;
            long count = Marshal.ReadInt64(buf);
            long pid = Environment.ProcessId;
            var byIndex = new Dictionary<int, List<IntPtr>>();
            for (long i = 0; i < count; i++)
            {
                IntPtr e = buf + 16 + (int)(i * 40);
                if (Marshal.ReadInt64(e, 8) != pid) continue;
                int type = (ushort)Marshal.ReadInt16(e, 30);
                var h = (IntPtr)Marshal.ReadInt64(e, 16);
                if (!byIndex.TryGetValue(type, out var list)) byIndex[type] = list = new List<IntPtr>();
                list.Add(h);
            }
            IntPtr info = Marshal.AllocHGlobal(4096);
            // both answers start with a UNICODE_STRING (ObjectTypeInformation = 2, ObjectNameInformation = 1)
            string? Query(IntPtr h, int cls)
            {
                if (NtQueryObject(h, cls, info, 4096, out _) != 0) return null;
                int len = (ushort)Marshal.ReadInt16(info);
                var str = Marshal.ReadIntPtr(info, 8);
                return len > 0 && str != IntPtr.Zero ? Marshal.PtrToStringUni(str, len / 2) : "";
            }
            try
            {
                foreach (var (type, handles) in byIndex)
                {
                    string name = Query(handles[0], 2) is { Length: > 0 } t ? t : "type " + type;
                    if (named && name is "Mutant" or "Section")
                    {
                        // names of these two types never block (unlike files and pipes)
                        foreach (var h in handles)
                        {
                            var n = Query(h, 1) ?? "?";
                            // numbers that change per object (session, counters) as #, GUIDs kept
                            n = System.Text.RegularExpressions.Regex.Replace(n.Length == 0 ? "(unnamed)" : n, @"(?<=\\Sessions\\)[0-9]+|(?<=_v?)[0-9]+(?=$|_)", "#");
                            result[$"{name} {n}"] = result.GetValueOrDefault($"{name} {n}") + 1;
                        }
                    }
                    else result[name] = result.GetValueOrDefault(name) + handles.Count;
                }
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        catch (Exception e) when (e is AccessViolationException or OutOfMemoryException or ArgumentException) { }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        return result;
    }

    private static string HandleDiff(Dictionary<string, int> before, Dictionary<string, int> after) =>
        string.Join(", ", after.Keys.Union(before.Keys).Select(k => (k, d: after.GetValueOrDefault(k) - before.GetValueOrDefault(k)))
            .Where(x => x.d != 0).OrderByDescending(x => Math.Abs(x.d)).Select(x => $"{x.k} {x.d:+0;-0}"));

    /// <summary>Everything that should be gone is collected, then the process is measured.</summary>
    private static async Task<Sample> Measure(int cycle, Stopwatch clock, string item)
    {
        // closed windows are released by WPF's own clean-up on the dispatcher: let it run first
        await ScreenshotRunner.Settle(60);
        for (int i = 0; i < 3; i++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        long managed = GC.GetTotalMemory(true);
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        int alive = 0;
        foreach (var (r, _) in Watched) if (r.IsAlive) alive++;
        return new Sample(cycle, clock.Elapsed.TotalSeconds, managed, p.PrivateMemorySize64, p.WorkingSet64, p.HandleCount,
            (int)GetGuiResources(p.Handle, 0), (int)GetGuiResources(p.Handle, 1), p.Threads.Count, GlTracker.Contexts, GlTracker.Objects, alive, item);
    }

    /// <summary>Least-squares slope of y over x.</summary>
    public static double Slope(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 2) return 0;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n;
        my /= n;
        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
        return sxx == 0 ? 0 : sxy / sxx;
    }

    /// <summary>The verdict of one view: slopes per cycle after the warm-up, and whether something grows.</summary>
    public sealed class Result
    {
        public string View = "";
        public int Cycles, Warmup;
        public double ManagedKb, PrivateKb, WorkingSetKb, Handles, Gdi, User, Threads, GlObjects, ManagedRiseMb, PrivateRiseMb;
        public long ManagedStart, ManagedEnd, PrivateStart, PrivateEnd;
        public int GlContextsStart, GlContextsEnd, GlObjectsStart, GlObjectsEnd, AliveEnd, Watched;
        public double Seconds;
        public List<string> Grows { get; } = new();
        public string? Error;
        public string Verdict => Error != null ? "FAILED: " + Error : Grows.Count == 0 ? "flat" : "GROWS: " + string.Join("; ", Grows);
    }

    // Thresholds of "grows" (per cycle after the warm-up, and in total over the measured cycles, so noise and a slope
    // over a handful of cycles do not count).
    public const double ManagedKbPerCycle = 2, ManagedMbTotal = 1;
    public const double PrivateKbPerCycle = 32, PrivateMbTotal = 16;
    public const double HandlesPerCycle = 0.1, GuiPerCycle = 0.05, GlObjectsPerCycle = 0.2;

    /// <summary>Least-squares slope of y over x and its t value (slope / standard error; large when the points follow
    /// the line, small when they only scatter around it). A perfect line gives a t of 1000.</summary>
    public static (double Slope, double T) Trend(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 3) return (0, 0);
        double mx = x.Take(n).Average(), my = y.Take(n).Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
        if (sxx == 0) return (0, 0);
        double b = sxy / sxx, a = my - b * mx, ss = 0;
        for (int i = 0; i < n; i++) { double e = y[i] - (a + b * x[i]); ss += e * e; }
        double se = Math.Sqrt(ss / (n - 2) / sxx);
        return (b, se < 1e-12 ? (b == 0 ? 0 : 1000 * Math.Sign(b)) : b / se);
    }

    public static Result Analyze(string view, IReadOnlyList<Sample> s, int watched)
    {
        var r = new Result { View = view, Cycles = s.Count, Watched = watched };
        if (s.Count == 0) return r;
        r.Warmup = Math.Max(1, s.Count / 10);
        var m = s.Skip(r.Warmup).ToList();
        if (m.Count < 2) m = s.ToList();
        var x = m.Select(v => (double)v.Cycle).ToList();
        r.ManagedKb = Slope(x, m.Select(v => v.Managed / 1024.0).ToList());
        r.PrivateKb = Slope(x, m.Select(v => v.Private / 1024.0).ToList());
        r.WorkingSetKb = Slope(x, m.Select(v => v.WorkingSet / 1024.0).ToList());
        r.Handles = Slope(x, m.Select(v => (double)v.Handles).ToList());
        r.Gdi = Slope(x, m.Select(v => (double)v.Gdi).ToList());
        r.User = Slope(x, m.Select(v => (double)v.User).ToList());
        r.Threads = Slope(x, m.Select(v => (double)v.Threads).ToList());
        r.GlObjects = Slope(x, m.Select(v => (double)v.GlObjects).ToList());
        r.ManagedStart = m[0].Managed;
        r.ManagedEnd = m[^1].Managed;
        r.PrivateStart = m[0].Private;
        r.PrivateEnd = m[^1].Private;
        r.GlContextsStart = m[0].GlContexts;
        r.GlContextsEnd = m[^1].GlContexts;
        r.GlObjectsStart = m[0].GlObjects;
        r.GlObjectsEnd = m[^1].GlObjects;
        r.AliveEnd = s[^1].Alive;
        r.Seconds = s[^1].Seconds - s[0].Seconds;
        // Something grows when it keeps rising: after the warm-up the slope passes the limit, is clearly more than the noise
        // (t > 4: slope / its standard error) and adds up to more than the total limit; AND the second half still rises
        // (half the limit, t > 2). A cache that fills and then stays (the decoded textures, the 8 built models) rises in
        // the first half only; views that rotate through a few states (two maps, six tables) swing by tens of MB, which a
        // slope alone over a hundred cycles can call growth.
        var h = s.Skip(s.Count / 2).ToList();
        if (h.Count < 3) h = m;
        var hx = h.Select(v => (double)v.Cycle).ToList();
        double span = Math.Max(1, m[^1].Cycle - m[0].Cycle);
        bool Keeps(Func<Sample, double> f, double perCycle, double total)
        {
            var (all, tAll) = Trend(x, m.Select(f).ToList());
            var (second, tSecond) = Trend(hx, h.Select(f).ToList());
            return all > perCycle && tAll > 4 && all * span > total && second > perCycle / 2 && tSecond > 2;
        }
        r.ManagedRiseMb = r.ManagedKb * span / 1024;
        r.PrivateRiseMb = r.PrivateKb * span / 1024;
        if (Keeps(v => v.Managed / 1024.0, ManagedKbPerCycle, ManagedMbTotal * 1024)) r.Grows.Add($"managed heap +{r.ManagedKb:0.#} KB/cycle");
        if (Keeps(v => v.Private / 1024.0, PrivateKbPerCycle, PrivateMbTotal * 1024)) r.Grows.Add($"private bytes +{r.PrivateKb:0.#} KB/cycle");
        if (Keeps(v => v.Handles, HandlesPerCycle, 5)) r.Grows.Add($"handles +{r.Handles:0.##}/cycle");
        if (Keeps(v => v.Threads, GuiPerCycle, 3)) r.Grows.Add($"threads +{r.Threads:0.##}/cycle");
        if (Keeps(v => v.Gdi, GuiPerCycle, 3)) r.Grows.Add($"GDI objects +{r.Gdi:0.##}/cycle");
        if (Keeps(v => v.User, GuiPerCycle, 3)) r.Grows.Add($"USER objects +{r.User:0.##}/cycle");
        if (r.GlContextsEnd > r.GlContextsStart) r.Grows.Add($"GL contexts {r.GlContextsStart} -> {r.GlContextsEnd}");
        if (Keeps(v => v.GlObjects, GlObjectsPerCycle, 6)) r.Grows.Add($"GL objects {r.GlObjectsStart} -> {r.GlObjectsEnd}");
        // a closed window may stay referenced by WPF's input or focus state until the next one replaces it
        if (r.AliveEnd > 4) r.Grows.Add($"{r.AliveEnd} of {watched} closed windows / view models still alive");
        return r;
    }

    private static void WriteCsv(string path, IReadOnlyList<Sample> s)
    {
        var sb = new StringBuilder("cycle,seconds,managed_bytes,private_bytes,working_set_bytes,handles,gdi_objects,user_objects,threads,gl_contexts,gl_objects,alive_closed,item\n");
        foreach (var v in s)
            sb.Append(string.Join(",", v.Cycle, v.Seconds.ToString("0.###", CultureInfo.InvariantCulture), v.Managed, v.Private, v.WorkingSet, v.Handles,
                v.Gdi, v.User, v.Threads, v.GlContexts, v.GlObjects, v.Alive, "\"" + v.Item.Replace("\"", "'") + "\"")).Append('\n');
        File.WriteAllText(path, sb.ToString());
    }

    private static string Mb(long b) => (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
    private static string F(double d, string fmt = "0.00") => d.ToString(fmt, CultureInfo.InvariantCulture);

    private static void WriteSummary(string outDir, IReadOnlyList<Result> results, int baseCycles, string client, bool log = false)
    {
        var csv = new StringBuilder("view,cycles,warmup,managed_kb_per_cycle,private_kb_per_cycle,working_set_kb_per_cycle,handles_per_cycle,gdi_per_cycle,user_per_cycle,gl_objects_per_cycle," +
                                    "managed_mb_start,managed_mb_end,private_mb_start,private_mb_end,gl_contexts_start,gl_contexts_end,gl_objects_start,gl_objects_end,alive_closed,watched,seconds,verdict\n");
        var txt = new StringBuilder();
        txt.AppendLine($"WoW Data Studio leak test, {DateTime.Now:yyyy-MM-dd HH:mm}, {StudioVersion()}");
        txt.AppendLine($"Client: {client}; {baseCycles} cycles per view ({string.Join(", ", Doubled)}: {baseCycles * 2}); slopes per cycle after the first 10% (warm-up).");
        txt.AppendLine($"\"Grows\" when a measure keeps rising (after the warm-up clearly above the noise, and still in the second half): the managed heap > {ManagedKbPerCycle} KB/cycle and");
        txt.AppendLine($"{ManagedMbTotal} MB, private bytes > {PrivateKbPerCycle} KB/cycle and {PrivateMbTotal} MB, handles > {HandlesPerCycle}/cycle, GDI / USER objects or threads > {GuiPerCycle}/cycle,");
        txt.AppendLine($"GL objects > {GlObjectsPerCycle}/cycle; or GL contexts do not come back, or more than 4 closed windows / view models stay alive.");
        txt.AppendLine();
        txt.AppendLine($"{"view",-16}{"cycles",7}{"managed KB/c",14}{"private KB/c",14}{"handles/c",11}{"GDI/c",8}{"USER/c",8}{"managed MB",16}{"private MB",16}{"GL ctx/obj",14}{"alive",8}  verdict");
        foreach (var r in results)
        {
            csv.AppendLine(string.Join(",", r.View, r.Cycles, r.Warmup, F(r.ManagedKb), F(r.PrivateKb), F(r.WorkingSetKb), F(r.Handles, "0.000"), F(r.Gdi, "0.000"), F(r.User, "0.000"), F(r.GlObjects, "0.000"),
                Mb(r.ManagedStart), Mb(r.ManagedEnd), Mb(r.PrivateStart), Mb(r.PrivateEnd), r.GlContextsStart, r.GlContextsEnd, r.GlObjectsStart, r.GlObjectsEnd, r.AliveEnd, r.Watched, F(r.Seconds, "0"),
                "\"" + r.Verdict.Replace("\"", "'") + "\""));
            txt.AppendLine($"{r.View,-16}{r.Cycles,7}{F(r.ManagedKb, "0.0"),14}{F(r.PrivateKb, "0.0"),14}{F(r.Handles, "0.00"),11}{F(r.Gdi, "0.00"),8}{F(r.User, "0.00"),8}" +
                           $"{Mb(r.ManagedStart) + "->" + Mb(r.ManagedEnd),16}{Mb(r.PrivateStart) + "->" + Mb(r.PrivateEnd),16}{$"{r.GlContextsEnd}/{r.GlObjectsEnd}",14}{$"{r.AliveEnd}/{r.Watched}",8}  {r.Verdict}");
        }
        File.WriteAllText(Path.Combine(outDir, "leak_summary.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(outDir, "leak_summary.txt"), txt.ToString());
        if (log) Log(Environment.NewLine + txt);
    }

    private static string StudioVersion()
    {
        try { return "version " + WoWData.Core.StudioInfo.Version; }
        catch (Exception) { return "dev"; }
    }

    // ---------------------------------------------------------------- the run

    private sealed class Ctx
    {
        public MainWindow Win = null!;
        public BrowserViewModel B = null!;
        public AppSession Session = null!;
        public string OutDir = "";
        public List<ResultRow> Models = new();
        public List<ResultRow> Browse = new();
        public List<ResultRow> Textures = new();
        public ThrowawayMySql? Db;
        public ServerDbSettings? KeepServer;
        public MainViewModel Main = null!;
    }

    public static async Task<int> RunAsync(MainWindow win, MainViewModel vm, string outDir, string? clientArg, int cycles, IReadOnlyCollection<string>? only)
    {
        ScreenshotRunner.Running = true;     // no confirmation dialogs; unexpected errors are counted, not shown
        ScreenshotRunner.Main = vm;
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        _log = new StreamWriter(Path.Combine(outDir, "leaktest.log"), false);
        var ctx = new Ctx { Win = win, OutDir = outDir, Session = vm.Session, Main = vm };
        var results = new List<Result>();
        string client = clientArg ?? vm.FolderText;
        try
        {
            var session = vm.Session;
            if (Directory.Exists(session.Paths.Root) && string.Equals(Path.GetFullPath(session.Paths.Root).TrimEnd(Path.DirectorySeparatorChar), Path.Combine(outDir, "_home"), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(session.Paths.Root, true);   // a fresh home each run (only our own temporary one)
                Directory.CreateDirectory(session.Paths.Root);
            }
            var views = Views.Where(v => only == null || only.Count == 0 || only.Contains(v)).ToList();
            Log($"leak test: {views.Count} views ({string.Join(", ", views)}), {cycles} cycles each ({string.Join(", ", Doubled)}: {cycles * 2}), client {client}, home {session.Paths.Root}");
            if (!ClientValidator.Check(client).Ok) throw new InvalidOperationException($"not a 1.12 client folder: {client}");
            ScreenshotRunner.Place(win, 1500, 1100);
            win.Show();
            await vm.OpenClientAsync(client, false);
            if (vm.Screen != Screen.Browser || vm.Browser == null) throw new InvalidOperationException("the main screen did not open: " + vm.WelcomeError);
            var b = vm.Browser;
            ctx.B = b;
            b.HotReloadLocator = HotReloadScreenshots.NoGame;
            Log($"index: {session.Entries.Count} entries; server database: {(session.Settings.Server.Enabled ? "ON" : "off")}");
            PopoutChrome.MonitorsOverride = () =>
            {
                var l = new List<MonitorArea> { new(PopoutScreenshots.ScreenshotMonitor, OffScreen, false, OffScreen) };
                l.AddRange(PopoutChrome.Monitors());
                return l;
            };
            await FindRows(ctx);

            foreach (var view in views)
            {
                int n = Doubled.Contains(view) ? cycles * 2 : cycles;
                var r = await RunView(ctx, view, n);
                results.Add(r);
                WriteSummary(outDir, results, cycles, client);
            }
            WriteSummary(outDir, results, cycles, client, true);
            if (ScreenshotRunner.Failures.Count > 0) Log("UNEXPECTED ERRORS:" + string.Join("", ScreenshotRunner.Failures.Select(f => Environment.NewLine + "  " + f)));
            Log(results.Any(r => r.Error != null) ? "SOME VIEWS FAILED" : results.Any(r => r.Grows.Count > 0) ? "SOME VIEWS GROW" : "ALL VIEWS FLAT");
            return results.Any(r => r.Error != null) || ScreenshotRunner.Failures.Count > 0 ? 2 : results.Any(r => r.Grows.Count > 0) ? 1 : 0;
        }
        catch (Exception e)
        {
            Log("FAILED: " + e);
            return 2;
        }
        finally
        {
            try { ctx.B?.CloseAllPopouts(); } catch (Exception) { }
            StopServer(ctx);
            PopoutChrome.MonitorsOverride = null;
            _log?.Dispose();
            _log = null;
            ScreenshotRunner.Running = false;
        }
    }

    private static readonly PixelRect OffScreen = new(-30000, 0, 1600, 1000);

    /// <summary>The rows the cycles open: a few models of every kind, many different creatures, a few textures.</summary>
    private static async Task FindRows(Ctx ctx)
    {
        var b = ctx.B;
        foreach (var c in b.Chips) c.IsChecked = true;
        // without the server database the creatures and items have no server names: a second search finds them by model
        foreach (var (query, fallback, kind, name) in new[]
                 {
                     ("mottled boar", "boar", AssetKind.Creature, "Mottled Boar"), ("orc male", "orc male", AssetKind.Character, ""),
                     ("durotarpalm01", "durotarpalm01", AssetKind.WorldObject, ""), ("worn battleaxe", "axe", AssetKind.Item, ""),
                     ("ice barrier state", "ice barrier", AssetKind.SpellEffect, ""),
                 })
        {
            ResultRow? row = null;
            foreach (var q in new[] { query, fallback })
            {
                await ScreenshotRunner.Search(b, q);
                row = b.Results.FirstOrDefault(r => r.Entry.Kind == kind && (name.Length == 0 || r.Name == name)) ?? b.Results.FirstOrDefault(r => r.Entry.Kind == kind);
                if (row != null) break;
            }
            if (row != null) ctx.Models.Add(row);
            else Log($"note: no {kind} found for '{query}' or '{fallback}'");
        }
        if (ctx.Models.Count == 0) throw new InvalidOperationException("no model entries found for the 3D viewer cycles");
        await ScreenshotRunner.Search(b, "boarskin");
        ctx.Textures.AddRange(b.Results.Where(r => r.Entry.Kind == AssetKind.Texture).Take(4));
        await ScreenshotRunner.Search(b, "orcmaleskin");
        ctx.Textures.AddRange(b.Results.Where(r => r.Entry.Kind == AssetKind.Texture).Take(2));
        // many different creatures, spread over the whole list (a user browsing): every cycle shows another model
        var creatures = ctx.Session.Entries.Where(e => e.Kind == AssetKind.Creature).ToList();
        int step = Math.Max(1, creatures.Count / 400);
        for (int i = 0; i < creatures.Count; i += step) ctx.Browse.Add(new ResultRow(creatures[i], false, false));
        Log($"rows: {ctx.Models.Count} models ({string.Join(", ", ctx.Models.Select(m => m.Name))}), {ctx.Textures.Count} textures, {ctx.Browse.Count} creatures to browse of {creatures.Count}");
        await ScreenshotRunner.Search(b, "");
    }

    private static async Task<Result> RunView(Ctx ctx, string view, int n)
    {
        var b = ctx.B;
        Watched.Clear();
        var samples = new List<Sample>();
        var clock = Stopwatch.StartNew();
        Log($"---- {view}: {n} cycles");
        Dictionary<string, int>? handlesAfterWarmup = null;
        string? error = null;
        try
        {
            if (NeedServer.Contains(view)) StartServer(ctx);
            Func<int, Task<string>> cycle = view switch
            {
                "model_viewer" => i => ModelViewerCycle(ctx, i),
                "model_popout" => i => ModelPopoutCycle(ctx, i),
                "model_compare" => i => ModelCompareCycle(ctx, i),
                "model_browse" => i => ModelBrowseCycle(ctx, i),
                "texture_viewer" => i => TextureCycle(ctx, i),
                "maps_tab" => i => MapsCycle(ctx, i),
                "terrain_popout" => i => TerrainPopoutCycle(ctx, i),
                "data_tables" => i => TablesCycle(ctx, i),
                "server_data" => i => ServerCycle(ctx, i),
                "spell_designer" => i => SpellCycle(ctx, i),
                "class_designer" => i => ClassCycle(ctx, i),
                "quest_designer" => i => QuestCycle(ctx, i),
                "other_versions" => i => VersionsCycle(ctx, i),
                "versions_window" => i => VersionsWindowCycle(ctx, i),
                _ => throw new ArgumentException("unknown view " + view),
            };
            if (view == "other_versions" && !await PrepareVersions(ctx)) return new Result { View = view, Error = "no folder of other clients (WDS_SCREENSHOT_OTHER_CLIENTS)" };
            if (view == "versions_window" && !await PrepareVersionsWindow()) return new Result { View = view, Error = "no 1.60 client (_classic_beta_) in WDS_SCREENSHOT_OTHER_CLIENTS" };
            for (int i = 0; i < n; i++)
            {
                _cycle = i;
                string item = await cycle(i);
                var s = await Measure(i, clock, item);
                samples.Add(s);
                if (i == Math.Max(1, n / 10) - 1) handlesAfterWarmup = HandleTypes(true);
                if (i % 10 == 0 || i == n - 1)
                    Log($"{view} {i + 1}/{n}: managed {Mb(s.Managed)} MB, private {Mb(s.Private)} MB, handles {s.Handles}, GDI {s.Gdi}, USER {s.User}, GL {s.GlContexts}/{s.GlObjects}, alive {s.Alive}/{Watched.Count} ({item})");
            }
        }
        catch (Exception e)
        {
            error = e.Message;
            Log($"{view} FAILED after {samples.Count} cycles: {e}");
        }
        finally
        {
            try
            {
                b.CloseAllPopouts();
                b.SelectedTab = 0;
                b.SelectedRow = null;
                await ScreenshotRunner.Settle(200);
            }
            catch (Exception) { }
        }
        WriteCsv(Path.Combine(ctx.OutDir, $"leak_{view}.csv"), samples);
        if (handlesAfterWarmup != null) Log($"{view}: handles by type, after the warm-up -> after the last cycle: {HandleDiff(handlesAfterWarmup, HandleTypes(true))}");
        var still = Watched.Where(w => w.Ref.IsAlive).Select(w => w.What).ToList();
        if (still.Count > 0) Log($"{view}: still alive after the last cycle: {string.Join(", ", still)}");
        var r = Analyze(view, samples, Watched.Count);
        r.Error = error;
        Log($"{view}: {r.Verdict} (managed {F(r.ManagedKb, "0.0")} KB/cycle, private {F(r.PrivateKb, "0.0")} KB/cycle, handles {F(r.Handles)}, GDI {F(r.Gdi)}, USER {F(r.User)})");
        Watched.Clear();
        return r;
    }

    // ---------------------------------------------------------------- the 3D viewer

    private static async Task ShowModel(Ctx ctx, ResultRow row)
    {
        var b = ctx.B;
        b.SelectedTab = 0;
        await ScreenshotRunner.Select(b, row);
        await ScreenshotRunner.WaitModel(b.ModelViewer);
    }

    private static async Task WaitFrames(Controls.ModelViewport vp, int before)
    {
        vp.Invalidate();
        await ScreenshotRunner.Until(() => vp.Frames > before, 20000);
    }

    /// <summary>Show a model in the docked viewer (drawn), then select nothing.</summary>
    private static async Task<string> ModelViewerCycle(Ctx ctx, int i)
    {
        var row = ctx.Models[i % ctx.Models.Count];
        var vp = ctx.Win.BrowserScreen.ModelViewport;
        int f = vp.Frames;
        await ShowModel(ctx, row);
        ctx.Win.BrowserScreen.ScrollToModel();
        await WaitFrames(vp, f);
        ctx.B.SelectedRow = null;
        await ScreenshotRunner.Until(() => !ctx.B.ModelViewer.HasModel, 10000);
        return row.Name;
    }

    /// <summary>Open a model pop-out window on a model (it draws), close it.</summary>
    private static async Task<string> ModelPopoutCycle(Ctx ctx, int i)
    {
        var row = ctx.Models[i % ctx.Models.Count];
        if (ctx.B.ModelViewer.Loaded?.Entry != row.Entry) await ShowModel(ctx, row);
        var w = await ctx.B.OpenModelPopoutAsync(x => ScreenshotRunner.Place(x, 1300, 860)) ?? throw new InvalidOperationException("the model pop-out did not open");
        await ScreenshotRunner.WaitModel(w.Vm.Viewer);
        await WaitFrames(w.Viewport, 0);
        Watch(w, nameof(ModelPopoutWindow));
        Watch(w.Vm, nameof(ModelPopoutViewModel));
        Watch(w.Vm.Viewer, "pop-out ModelViewerViewModel");
        Watch(w.Viewport, "pop-out ModelViewport");
        w.Close();
        return row.Name;
    }

    /// <summary>The compare window of two skins of the boar: open (both views draw), close.</summary>
    private static async Task<string> ModelCompareCycle(Ctx ctx, int i)
    {
        var mv = ctx.B.ModelViewer;
        if (!mv.HasModel || mv.Variants.Count < 2) await ShowModel(ctx, ctx.Models[0]);
        var cmp = await mv.MakeCompareAsync() ?? throw new InvalidOperationException("no compare view for " + mv.ModelPath);
        var cw = new ModelCompareWindow(cmp);
        ScreenshotRunner.Place(cw, 1180, 700);
        cw.Show();
        await WaitFrames(cw.Compare.LeftView, 0);
        Watch(cw, nameof(ModelCompareWindow));
        Watch(cmp, nameof(ModelCompareViewModel));
        Watch(cw.Compare.LeftView, "compare ModelViewport");
        cw.Close();
        return Path.GetFileName(mv.ModelPath);
    }

    /// <summary>Another creature every cycle (a user browsing the list): every model, skin and texture is new.</summary>
    private static async Task<string> ModelBrowseCycle(Ctx ctx, int i)
    {
        var row = ctx.Browse[i % ctx.Browse.Count];
        var vp = ctx.Win.BrowserScreen.ModelViewport;
        int f = vp.Frames;
        await ShowModel(ctx, row);
        if (ctx.B.ModelViewer.HasModel) await WaitFrames(vp, f);
        return row.Name + " " + Path.GetFileName(ctx.B.ModelViewer.ModelPath);
    }

    private static async Task<string> TextureCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        if (ctx.Textures.Count == 0) throw new InvalidOperationException("no textures found");
        var row = ctx.Textures[i % ctx.Textures.Count];
        b.SelectedTab = 0;
        await ScreenshotRunner.Select(b, row);
        await ScreenshotRunner.Until(() => b.Viewer.IsLoaded || b.Viewer.Error.Length > 0);
        b.SelectedRow = null;
        await ScreenshotRunner.Until(() => !b.Viewer.IsLoaded, 10000);
        return row.Name;
    }

    // ---------------------------------------------------------------- maps

    private static readonly (string Map, string Zone, int X, int Y)[] MapTiles =
    {
        ("Azeroth", "", 32, 48), ("Kalimdor", "Durotar", -1, -1), ("Azeroth", "", 32, 49), ("Kalimdor", "Mulgore", -1, -1),
    };

    /// <summary>The Maps tab: a map (its overview), a tile with its 3D preview; then back to the Browse tab.</summary>
    private static async Task<string> MapsCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var m = b.Maps;
        b.SelectedTab = BrowserViewModel.MapsTab;
        await m.EnsureLoadedAsync();
        await ScreenshotRunner.Until(() => m.Maps.Count > 0, 60000);
        var (mapName, zone, x, y) = MapTiles[i % MapTiles.Length];
        var map = m.Maps.First(v => v.Directory.Equals(mapName, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(m.SelectedMap, map))
        {
            m.SelectedMap = map;
            await ScreenshotRunner.Until(() => m.Overview != null && m.Zones.Count > 0, 180000);
        }
        if (x < 0)
        {
            var z = m.Zones.FirstOrDefault(v => v.Name == zone) ?? m.Zones.First(v => v.Tiles.Count > 0);
            (x, y) = z.Tiles.First();
        }
        await SelectTile(m, x, y);
        await WaitFrames(ctx.Win.BrowserScreen.MapsScreen.TerrainView, 0);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return $"{mapName} {x},{y}";
    }

    private static async Task SelectTile(MapsViewModel m, int x, int y)
    {
        m.IncludeNeighbours = false;
        m.SelectTile(x, y);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && m.HasTile && m.TileX == x && m.TileY == y, 120000);
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset != null && m.PreviewKey is { } k && k.X == x && k.Y == y, 180000);
    }

    /// <summary>A terrain pop-out window on a tile of the Maps tab (two tiles in turn): open (it draws), close.</summary>
    private static async Task<string> TerrainPopoutCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var m = b.Maps;
        b.SelectedTab = BrowserViewModel.MapsTab;
        await m.EnsureLoadedAsync();
        await ScreenshotRunner.Until(() => m.Maps.Count > 0, 60000);
        var azeroth = m.Maps.First(v => v.Directory.Equals("Azeroth", StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(m.SelectedMap, azeroth))
        {
            m.SelectedMap = azeroth;
            await ScreenshotRunner.Until(() => m.Overview != null && m.Zones.Count > 0, 180000);
        }
        int x = 32, y = 48 + i % 2;
        await SelectTile(m, x, y);
        var w = b.OpenTerrainPopoutWindow(v => ScreenshotRunner.Place(v, 1300, 860)) ?? throw new InvalidOperationException("the terrain pop-out did not open");
        await ScreenshotRunner.Until(() => !w.Vm.Loading && w.Vm.Scene.Asset != null, 120000);
        await WaitFrames(w.Viewport, 0);
        Watch(w, nameof(TerrainPopoutWindow));
        Watch(w.Vm, nameof(TerrainPopoutViewModel));
        Watch(w.Viewport, "terrain ModelViewport");
        w.Close();
        return $"Azeroth {x},{y}";
    }

    // ---------------------------------------------------------------- tables, server, designers

    private static readonly (string Table, long Id)[] TableRows =
    {
        ("Spell", 133), ("CreatureDisplayInfo", 3036), ("ItemDisplayInfo", 1542), ("Map", 1), ("AreaTable", 12), ("Faction", 72),
    };

    private static async Task<string> TablesCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var t = b.Tables;
        var (name, id) = TableRows[i % TableRows.Length];
        b.SelectedTab = BrowserViewModel.TablesTab;
        await t.EnsureLoadedAsync();
        await t.SelectTableAsync(t.AllTables.First(x => x.Name == name));
        await ScreenshotRunner.Until(() => t.Table?.Name == name && !t.IsLoading, 60000);
        t.GoTo(id);
        await ScreenshotRunner.Settle(150);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return $"{name} {id}";
    }

    private static string? Fixture(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "app", "WoWData.Tests", "serverdb", file);
            if (File.Exists(f)) return f;
            f = Path.Combine(d.FullName, "WoWData.Tests", "serverdb", file);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>A throwaway MySQL server with the tests' fixture and the quest-chain supplement (never the user's server).</summary>
    private static void StartServer(Ctx ctx)
    {
        if (ctx.Db != null) return;
        var session = ctx.Session;
        var mysqld = ThrowawayMySql.FindMysqld(session.ServerFolder);
        var fixture = Fixture("world_fixture.sql");
        var supplement = Fixture("quest_chains_fixture.sql");
        if (mysqld == null || fixture == null || supplement == null)
            throw new InvalidOperationException($"no throwaway MySQL server (mysqld: {mysqld ?? "not found"}, fixture: {fixture ?? "not found"}, supplement: {supplement ?? "not found"})");
        var db = ThrowawayMySql.Start(mysqld);
        db.CreateDatabase("wds_leaktest", fixture);
        db.RunSqlFile("wds_leaktest", supplement);
        ctx.Db = db;
        ctx.KeepServer = session.Settings.Server;
        session.Settings.Server = db.Settings("wds_leaktest");
        session.ServerRunningProbe = () => new List<string>();
        session.ResetServerData();
        ctx.B.Spells.Reset();
        if (session.Settings.Server.Port == 3306) throw new InvalidOperationException("the throwaway server must not use the default port");
        Log($"throwaway MySQL server on port {session.Settings.Server.Port} (database wds_leaktest, from {Path.GetFileName(fixture)} + {Path.GetFileName(supplement)})");
    }

    private static void StopServer(Ctx ctx)
    {
        if (ctx.Db == null) return;
        try
        {
            if (ctx.KeepServer != null) ctx.Session.Settings.Server = ctx.KeepServer;
            ctx.Session.ResetServerData();
            ctx.Db.Dispose();
        }
        catch (Exception e) { Log("note: stopping the throwaway server: " + e.Message); }
        ctx.Db = null;
    }

    private static readonly (string View, string Text, string Table, string Key)[] ServerRecords =
    {
        ("items", "worn shortsword", "item_template", "entry=25;patch=0"),
        ("creatures", "mottled boar", "creature_template", "entry=3098;patch=7"),
        ("creatures", "duokna", "creature_template", "entry=3158;patch=0"),
    };

    private static async Task<string> ServerCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var s = b.Server;
        b.SelectedTab = BrowserViewModel.ServerTab;
        if (!s.IsConnected)
        {
            await s.ReconnectAsync();
            await ScreenshotRunner.Until(() => s.IsLoaded && !s.IsLoading, 60000);
            if (!s.IsConnected) throw new InvalidOperationException("not connected to the throwaway database: " + s.ConnectionText);
            if (s.Service!.Database != "wds_leaktest") throw new InvalidOperationException("the Server data tab is not on the throwaway database");
        }
        var (view, text, table, key) = ServerRecords[i % ServerRecords.Length];
        s.SelectedView = s.Views.First(v => v.Def.Id == view);
        s.SearchText = text;
        await s.SearchAsync();
        await ScreenshotRunner.Until(() => !s.IsLoading, 60000);
        await s.OpenRecordAsync(table, key, false);
        await ScreenshotRunner.Until(() => !s.IsLoading && s.HasRecord && s.CurrentKey == key, 60000);
        if (s.HasPreview) await ScreenshotRunner.Until(() => s.Preview.IsLoaded || s.Preview.Error.Length > 0, 30000);
        await ScreenshotRunner.Settle(100);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return $"{table} {key}";
    }

    private static readonly long[] SpellIds = { 133, 686, 116, 172, 2061 };

    /// <summary>The spell designer: open a spell (the session is read again each time, as when nothing was opened),
    /// show its visual's model in the 3D view; back to the Browse tab.</summary>
    private static async Task<string> SpellCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var sp = b.Spells;
        long id = SpellIds[i % SpellIds.Length];
        sp.Reset();
        b.SelectedTab = BrowserViewModel.SpellsTab;
        await sp.EnsureLoadedAsync();
        await sp.OpenAsync(id);
        if (sp.CurrentId != id) throw new InvalidOperationException($"spell {id} did not open: {sp.Status}");
        if (sp.VisualModels.Count > 0)
        {
            sp.SelectedModel = sp.VisualModels[0];
            await sp.ShowModelCommand.ExecuteAsync(null);
            await ScreenshotRunner.Until(() => !sp.Viewer.IsLoading, 30000);
        }
        await ScreenshotRunner.Settle(150);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return $"spell {id}";
    }

    private static readonly int[] ClassIds = { 1, 9, 8, 4 };

    private static async Task<string> ClassCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var ct = b.Classes;
        int id = ClassIds[i % ClassIds.Length];
        b.SelectedTab = BrowserViewModel.ClassesTab;
        await ct.EnsureLoadedAsync();
        ct.SelectedClass = ct.Classes.First(c => c.Id == id);
        await ScreenshotRunner.Settle(100);
        var tree = ct.Trees.Count > 0 ? ct.Trees[i % ct.Trees.Count] : null;
        if (tree != null) ct.SelectTree(tree.Id);
        await ScreenshotRunner.Settle(150);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return $"class {id} tree {tree?.Id}";
    }

    private static async Task<string> QuestCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var q = b.Quests;
        b.SelectedTab = BrowserViewModel.QuestsTab;
        await q.EnsureLoadedAsync();
        if (!q.HasServer || q.Designer!.Ed.Service.Database != "wds_leaktest") throw new InvalidOperationException("the Quest chains tab is not on the throwaway database");
        string what;
        if (i % 2 == 0)
        {
            q.SearchText = "islander";
            await q.SearchCommand.ExecuteAsync(null);
            await q.OpenChainCommand.ExecuteAsync(null);
            what = "the Whirlwind weapon chain";
        }
        else
        {
            await q.OpenAsync(7562);
            what = "the Dreadsteed chain";
        }
        await ScreenshotRunner.Settle(200);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return what;
    }

    // ---------------------------------------------------------------- other versions

    private static List<ClientRow> _versionClients = new();

    private static async Task<bool> PrepareVersions(Ctx ctx)
    {
        var folder = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_OTHER_CLIENTS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Log("other versions: WDS_SCREENSHOT_OTHER_CLIENTS is not set to a folder of client copies; skipped");
            return false;
        }
        var v = ctx.B.Versions;
        ctx.B.SelectedTab = BrowserViewModel.VersionsTab;
        v.Folder = folder;
        await v.FindClientsCommand.ExecuteAsync(null);
        v.BlizzardOnly = false;
        await v.Opening;
        // the MPQ clients of other versions (the tab reads their tables and files)
        _versionClients = v.Clients.Where(c => c.Copy.Readable && c.Copy.Format == WoWData.Core.Versions.ClientFormat.Mpq && c.Build != 5875).GroupBy(c => c.Build).Select(g => g.First()).ToList();
        Log($"other versions: {v.Clients.Count} clients in {folder}; cycling through {string.Join(", ", _versionClients.Select(c => $"{c.Name} ({c.Build})"))}");
        ctx.B.SelectedTab = 0;
        return _versionClients.Count > 0;
    }

    private static async Task<string> VersionsCycle(Ctx ctx, int i)
    {
        var b = ctx.B;
        var v = b.Versions;
        var c = _versionClients[i % _versionClients.Count];
        b.SelectedTab = BrowserViewModel.VersionsTab;
        await v.EnsureLoadedAsync();
        v.SelectedClient = c;
        await v.Opening;
        v.FilePattern = @"Interface\Icons\INV_Sword_0*.blp";
        v.SearchFilesCommand.Execute(null);
        v.SelectedFile = v.Files.FirstOrDefault();
        v.SelectedTable = v.Tables.FirstOrDefault(t => t.Name.Equals("Map", StringComparison.OrdinalIgnoreCase));
        await ScreenshotRunner.Settle(200);
        b.SelectedTab = 0;
        await ScreenshotRunner.Settle(100);
        return $"{c.Name} ({c.Build}): {v.Files.Count} files";
    }

    // ------------------------------------------------------------------ M1: another version opened read only

    private static WoWData.Core.Versions.ClientCopy? _betaCopy;

    private static async Task<bool> PrepareVersionsWindow()
    {
        var folder = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_OTHER_CLIENTS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        var found = await Task.Run(() => WoWData.Core.Versions.ClientCopies.Find(folder));
        _betaCopy = found.Where(c => c.Format == WoWData.Core.Versions.ClientFormat.Casc && c.Version.StartsWith("1.60", StringComparison.Ordinal)).OrderByDescending(c => c.Build).FirstOrDefault();
        Log($"versions window: {(_betaCopy == null ? "no 1.60 client" : $"{_betaCopy.Name} ({_betaCopy.Build})")} in {folder}");
        return _betaCopy != null;
    }

    private static readonly string[] OtherSearches = { "boar", "inv_sword_04", "worn shortsword", "orcbonfire" };

    /// <summary>The 1.60 client opened read only in its own window (its index exists after the first cycle), a search,
    /// an entry with its details and texture (and, M2, its 3D view), then the window closed.</summary>
    private static async Task<string> VersionsWindowCycle(Ctx ctx, int i)
    {
        var w = OtherVersionWindow.Open(ctx.Main, _betaCopy!, null, show: false);
        ScreenshotRunner.Place(w, 1440, 900);
        w.Show();
        var vm = w.Vm;
        await ScreenshotRunner.Until(() => vm.IsReady || vm.Error.Length > 0, 600000);
        if (!vm.IsReady) throw new InvalidOperationException("the read-only window did not open: " + vm.Error);
        var q = OtherSearches[i % OtherSearches.Length];
        vm.SearchText = q;
        await vm.ApplyFilterAsync();
        vm.SelectedRow = vm.Results.FirstOrDefault();
        await ScreenshotRunner.Until(() => vm.SelectedRow == null || vm.DetailTitle.Length > 0, 30000);
        if (vm.SelectedTexture is { Exists: true }) await ScreenshotRunner.Until(() => vm.Viewer.IsLoaded || vm.Viewer.Error.Length > 0, 30000);
        // M2: a model entry opens its 3D view (a GL view: closing it leaks 2 handles, see known-issues.md)
        if (vm.IsModelEntry) await ScreenshotRunner.Until(() => !vm.ModelViewer.IsLoading && (vm.ModelViewer.HasModel || vm.ModelViewer.Message.Length > 0), 60000);
        await ScreenshotRunner.Settle(150);
        string item = $"{q}: {vm.Results.Count} results, {vm.DetailTitle}{(vm.ModelViewer.HasModel ? $", 3D {System.IO.Path.GetFileName(vm.ModelViewer.ModelPath)}" : "")}";
        Watch(w, nameof(OtherVersionWindow));
        Watch(vm, nameof(OtherVersionWindowViewModel));
        Watch(vm.ModelViewer, "read-only window's ModelViewerViewModel");
        Watch(vm.Session, "read-only AppSession");
        w.Close();
        await ScreenshotRunner.Settle(150);
        return item;
    }
}
