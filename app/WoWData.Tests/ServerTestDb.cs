using System.IO;
using System.Linq;
using MySqlConnector;
using WoWData.Core.Index;
using WoWData.Core.ServerData;

namespace WoWData.Tests;

/// <summary>
/// 0.9.0: the isolated, throwaway world database of the server data tests. The tests NEVER use the user's databases:
///  * one portable MySQL server per test run, started by <see cref="ThrowawayMySql"/> from mysqld.exe (the program next
///    to the local VMaNGOS server, Server\mysql5\bin, only read; or WDS_TEST_MYSQLD), with its own EMPTY data folder in
///    %TEMP% and its own free port on 127.0.0.1, killed and deleted at the end;
///  * loaded from serverdb\world_fixture.sql (the schema of every table of the world database and a small sample of its
///    rows, made by app\tools\ServerFixtureBuilder from a read-only copy of the database files);
///  * every test gets its own copy of that database (<see cref="NewDatabase"/>), so tests never see each other's edits.
/// Without a mysqld the tests that need it are skipped (<see cref="IsolatedDbFactAttribute"/>), never pointed elsewhere.
/// </summary>
public static class ServerTestDb
{
    public const string FixtureDatabase = "wds_fixture";
    private static readonly object Gate = new();
    private static ThrowawayMySql? _server;
    private static string? _error;

    /// <summary>The local server's folder (Server next to WoWDataTool).</summary>
    public static string ServerFolder => Path.Combine(Path.GetDirectoryName(Oracle.ToolDir)!, "Server");

    public static string Fixture => Path.Combine(Oracle.AppDir, "WoWData.Tests", "serverdb", "world_fixture.sql");

    /// <summary>0.13.0: the quest-chain supplement (whole chains of real quests, the Riposte Drill outposts), loaded on top
    /// of the fixture into a second database for the quest-chain tests only.</summary>
    public static string QuestChainsFixture => Path.Combine(Oracle.AppDir, "WoWData.Tests", "serverdb", "quest_chains_fixture.sql");
    public const string QuestFixtureDatabase = "wds_fixture_quests";
    private static bool _questFixture;

    public static string? Mysqld => ThrowawayMySql.FindMysqld(ServerFolder);

    public static string? SkipReason => Mysqld == null
        ? "needs a portable mysqld for the isolated test database (Server\\mysql5\\bin\\mysqld.exe next to WoWDataTool, or set WDS_TEST_MYSQLD); the tests never use the live database"
        : !File.Exists(Fixture) ? $"the test database's SQL file is missing: {Fixture}" : null;

    /// <summary>The shared throwaway server with the fixture database (started on first use).</summary>
    public static ThrowawayMySql Server
    {
        get
        {
            lock (Gate)
            {
                if (_server != null) return _server;
                if (_error != null) throw new InvalidOperationException(_error);
                try
                {
                    var s = ThrowawayMySql.Start(Mysqld ?? throw new InvalidOperationException(SkipReason));
                    Assert.NotEqual(3306, s.Port);
                    Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(s.DataDir), StringComparison.OrdinalIgnoreCase);
                    s.CreateDatabase(FixtureDatabase, Fixture);
                    return _server = s;
                }
                catch (Exception e)
                {
                    _error = "the throwaway MySQL server could not be started: " + e.Message;
                    throw;
                }
            }
        }
    }

    /// <summary>A fresh copy of the fixture database for one test, and its connection settings (never the live server).</summary>
    public static ServerDbSettings NewDatabase(string name, bool questChains = false)
    {
        var db = "t_" + name.ToLowerInvariant().Replace('-', '_') + "_" + Guid.NewGuid().ToString("N")[..6];
        lock (Gate)
        {
            if (questChains && !_questFixture)
            {
                Server.CopyDatabase(FixtureDatabase, QuestFixtureDatabase);
                Server.RunSqlFile(QuestFixtureDatabase, QuestChainsFixture);
                _questFixture = true;
            }
            Server.CopyDatabase(questChains ? QuestFixtureDatabase : FixtureDatabase, db);
        }
        var settings = Server.Settings(db);
        AssertIsolated(settings);
        return settings;
    }

    /// <summary>Settings point at the throwaway server only.</summary>
    public static void AssertIsolated(ServerDbSettings s)
    {
        Assert.Equal("127.0.0.1", s.Host);
        Assert.Equal(Server.Port, s.Port);
        Assert.NotEqual(3306, s.Port);
        Assert.NotEqual("mangos", s.Database);
    }

    public static MySqlConnection Connect(ServerDbSettings s)
    {
        AssertIsolated(s);
        var c = new MySqlConnection($"Server=127.0.0.1;Port={s.Port};User ID=root;Password=;Database={s.Database};SslMode=None;Pooling=False;CharacterSet=utf8;Allow User Variables=true");
        c.Open();
        return c;
    }

    public static void Exec(ServerDbSettings s, string sql)
    {
        using var c = Connect(s);
        using var cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(ServerDbSettings s, string sql)
    {
        using var c = Connect(s);
        using var cmd = new MySqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    /// <summary>CHECKSUM TABLE of tables (the rows' bytes, in any order).</summary>
    public static Dictionary<string, long> Checksums(ServerDbSettings s, params string[] tables)
    {
        using var c = Connect(s);
        using var cmd = new MySqlCommand("CHECKSUM TABLE " + string.Join(", ", tables.Select(t => $"`{t}`")) + " EXTENDED", c);
        using var r = cmd.ExecuteReader();
        var res = new Dictionary<string, long>();
        while (r.Read()) res[r.GetString(0)] = r.IsDBNull(1) ? -1 : Convert.ToInt64(r.GetValue(1));
        return res;
    }

    /// <summary>Every row of a table as text, ordered by all columns (an exact comparison of the content).</summary>
    public static List<string> Dump(ServerDbSettings s, string table)
    {
        using var c = Connect(s);
        using var cmd = new MySqlCommand($"SELECT * FROM `{table}`", c);
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i) is float f ? BitConverter.SingleToInt32Bits(f).ToString() : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>Run an SQL script the way the mysql client does (statements one per line).</summary>
    public static void RunScript(ServerDbSettings s, string script)
    {
        using var c = Connect(s);
        foreach (var line in script.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)))
        {
            using var cmd = new MySqlCommand(line, c);
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>A test that needs the isolated throwaway database: skipped (with the reason) when no mysqld is available.</summary>
public sealed class IsolatedDbFactAttribute : FactAttribute
{
    public IsolatedDbFactAttribute()
    {
        if (ServerTestDb.SkipReason is { } why) Skip = why;
    }
}
