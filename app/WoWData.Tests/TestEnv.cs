using System.IO;
using System.Linq;
using WoWData.Core.Index;
using WoWData.Core.Mpq;

namespace WoWData.Tests;

/// <summary>The real 1.12 client (read-only) and a temporary index shared by the tests.</summary>
public static class TestEnv
{
    private static ClientData? _client;
    private static readonly object Gate = new();

    // WOWDATA_CLIENT_DIR first; then a "Client3" test copy next to the tool when there is one (the maintainer's machine keeps its
    // playable "Client" folder out of every test run: the tests only read, but they never need the player's own copy); else "Client".
    public static string ClientDir
    {
        get
        {
            if (Environment.GetEnvironmentVariable("WOWDATA_CLIENT_DIR") is { Length: > 0 } d) return d;
            var parent = Path.GetDirectoryName(Oracle.ToolDir)!;
            var testCopy = Path.Combine(parent, "Client3");
            return File.Exists(Path.Combine(testCopy, "Data", "model.MPQ")) ? testCopy : Path.Combine(parent, "Client");
        }
    }

    public static string DataDir => Path.Combine(ClientDir, "Data");

    public static ClientData Client
    {
        get
        {
            lock (Gate)
            {
                if (!File.Exists(Path.Combine(DataDir, "model.MPQ")))
                    throw new InvalidOperationException($"the tests need the 1.12 client at {ClientDir} (or set WOWDATA_CLIENT_DIR)");
                return _client ??= new ClientData(DataDir);
            }
        }
    }

    public static string TempDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), "wowdatastudio_test_" + prefix + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>(relative path, size, mtime) of every file below Client\Data: proves the tests never write into the client.</summary>
    public static List<(string, long, DateTime)> DataSnapshot() =>
        Directory.EnumerateFiles(DataDir, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => (Path.GetRelativePath(DataDir, f.FullName), f.Length, f.LastWriteTimeUtc))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();

    public static ServerDbSettings LocalServer => new();

    /// <summary>A settings object for a server that is certainly not there (port 1).</summary>
    public static ServerDbSettings NoServer => new() { Host = "127.0.0.1", Port = 1 };

    private static bool? _serverUp;

    public static bool ServerAvailable
    {
        get
        {
            lock (Gate)
                return _serverUp ??= new ServerDb(LocalServer).TestAsync().GetAwaiter().GetResult().Ok;
        }
    }
}

/// <summary>
/// The one index of a test run (with the server's names when the server database answered): built once per process,
/// the first time a test asks for it, and shared by every class that needs an index (<see cref="IndexCollection"/>), so
/// no two index builds of the fixture ever run at the same time. The server data in it is one snapshot: a test that needs
/// server data checks <see cref="HasServerData"/> (and is a <see cref="ServerFactAttribute"/>), so a server that is down,
/// or answered with empty tables while the index was built, skips those checks instead of failing them.
/// </summary>
public sealed class IndexFixture
{
    private static readonly Lazy<IndexFixture> Shared = new(() => new IndexFixture(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared index (built on first use).</summary>
    public static IndexFixture Instance => Shared.Value;

    public string Dir { get; } = TestEnv.TempDir("index");
    public string Path => System.IO.Path.Combine(Dir, "index.sqlite");
    public IndexBuildResult Result { get; }
    public AssetIndex Index { get; }
    public List<IndexEntry> Entries { get; }

    /// <summary>True when the snapshot has the server's names, spawns and loot (the server answered completely).</summary>
    public bool HasServerData =>
        Result.ServerUsed && Result.Counts.GetValueOrDefault("creature") > 1000 && Result.Counts.GetValueOrDefault("u_spawn") > 1000 && Result.Counts.GetValueOrDefault("u_loot") > 1000;

    private IndexFixture()
    {
        Result = IndexBuilder.BuildAsync(TestEnv.Client, Path, TestEnv.ServerAvailable ? TestEnv.LocalServer : TestEnv.NoServer).GetAwaiter().GetResult();
        if (TestEnv.ServerAvailable && !HasServerData)
        {
            // the server answered with partial data (busy or restarting): read its part once more, as "Reload names" does
            for (int attempt = 0; attempt < 2 && !HasServerData; attempt++)
            {
                Thread.Sleep(2000);
                var (ok, _) = IndexBuilder.RefreshServerAsync(Path, TestEnv.LocalServer).GetAwaiter().GetResult();
                if (ok) Result = RefreshedResult(Result);
            }
        }
        Index = new AssetIndex(Path);
        Entries = Index.LoadEntries();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    /// <summary>The counts of the server part after a refresh (from the index itself).</summary>
    private IndexBuildResult RefreshedResult(IndexBuildResult r)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
        db.Open();
        long Count(string t)
        {
            using var c = db.CreateCommand();
            c.CommandText = $"SELECT COUNT(*) FROM {t}";
            try { return (long)c.ExecuteScalar()!; }
            catch (Microsoft.Data.Sqlite.SqliteException) { return 0; }
        }
        r.Counts["creature"] = (int)Count("creature");
        r.Counts["u_spawn"] = (int)Count("u_spawn");
        r.Counts["u_loot"] = (int)Count("u_loot");
        r.ServerUsed = r.Counts["creature"] > 0;
        return r;
    }

    private void Cleanup()
    {
        try
        {
            Index.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }
}

/// <summary>Every test class that uses the shared index, builds an index of its own or runs the app (which builds one):
/// they run one after another, never two index builds at once. Other classes still run in parallel with them.</summary>
[CollectionDefinition(Name)]
public sealed class IndexCollection : ICollectionFixture<IndexCollection.Handle>
{
    public const string Name = "Index (one build at a time)";

    /// <summary>Hands the shared index to the classes of the collection.</summary>
    public sealed class Handle
    {
        public IndexFixture Fixture => IndexFixture.Instance;
    }
}

/// <summary>Timing checks: run alone, after every other test (no parallel load), with tolerant limits.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance (alone)";
}

/// <summary>A test that needs the local server database: skipped (with this reason) when the server does not answer.</summary>
public sealed class ServerFactAttribute : FactAttribute
{
    public ServerFactAttribute()
    {
        if (!TestEnv.ServerAvailable) Skip = "needs the local server database (127.0.0.1), which did not answer";
    }
}
