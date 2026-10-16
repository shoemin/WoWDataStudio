using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MySqlConnector;
using WoWData.Core.Index;

namespace WoWData.Core.ServerData;

/// <summary>
/// 0.9.0, for the tests, the manual's screenshots and the test fixture only (never for the user's server): a portable,
/// throwaway MySQL / MariaDB server with its own empty data folder in %TEMP% and its own port on 127.0.0.1, started from a
/// mysqld.exe (by default the one next to the local VMaNGOS server: Server\mysql5\bin), loaded from an SQL file, and
/// stopped and deleted afterwards. It never opens the live server's data folder or port: only its program files are used
/// (read only), so the tests can never write the user's databases.
/// </summary>
public sealed class ThrowawayMySql : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private ThrowawayMySql(Process p, string root, int port)
    {
        _process = p;
        Root = root;
        Port = port;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <summary>The temporary folder (data folder and log) that is deleted on <see cref="Dispose"/>.</summary>
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public int Port { get; }

    /// <summary>Connection settings for a database of this server (root without a password: the throwaway server checks
    /// no passwords and listens on 127.0.0.1 only).</summary>
    public ServerDbSettings Settings(string database) => new() { Enabled = true, Host = "127.0.0.1", Port = Port, User = "root", Password = "", Database = database };

    /// <summary>mysqld.exe for a throwaway server: WDS_TEST_MYSQLD, else &lt;serverFolder&gt;\mysql5\bin\mysqld.exe (or
    /// bin\mariadbd.exe / bin\mysqld.exe of a MariaDB / MySQL folder given in WDS_TEST_MYSQL_HOME). Null when none.</summary>
    public static string? FindMysqld(string? serverFolder)
    {
        var env = Environment.GetEnvironmentVariable("WDS_TEST_MYSQLD");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var home = Environment.GetEnvironmentVariable("WDS_TEST_MYSQL_HOME");
        foreach (var root in new[] { home, serverFolder == null ? null : Path.Combine(serverFolder, "mysql5") }.Where(r => !string.IsNullOrWhiteSpace(r)))
            foreach (var exe in new[] { "mariadbd.exe", "mysqld.exe" })
                if (File.Exists(Path.Combine(root!, "bin", exe))) return Path.Combine(root!, "bin", exe);
        return null;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Start a server on an empty temporary data folder and a free port; returns when it answers.
    /// <paramref name="seedDataDir"/> (the fixture builder only): COPY these entries of another data folder into the
    /// temporary one first (the original is only read).</summary>
    public static ThrowawayMySql Start(string mysqld, string? seedDataDir = null, IReadOnlyCollection<string>? seedEntries = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "wds_throwaway_mysql_" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        if (seedDataDir != null)
            foreach (var e in seedEntries ?? Array.Empty<string>())
            {
                var src = Path.Combine(seedDataDir, e);
                if (File.Exists(src)) File.Copy(src, Path.Combine(data, e));
                else if (Directory.Exists(src))
                {
                    Directory.CreateDirectory(Path.Combine(data, e));
                    foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(data, e, Path.GetFileName(f)));
                }
            }
        var basedir = Path.GetDirectoryName(Path.GetDirectoryName(mysqld))!;
        for (int attempt = 0; ; attempt++)
        {
            int port = FreePort();
            var psi = new ProcessStartInfo(mysqld) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[]
                     {
                         "--no-defaults", $"--basedir={basedir}", $"--datadir={data}", $"--port={port.ToString(CultureInfo.InvariantCulture)}", "--bind-address=127.0.0.1",
                         "--skip-grant-tables", "--skip-name-resolve", "--performance_schema=0", $"--log-error={Path.Combine(root, "mysqld.log")}",
                         $"--pid-file={Path.Combine(root, "mysqld.pid")}", "--character-set-server=utf8", "--innodb_buffer_pool_size=32M", "--max_allowed_packet=64M",
                         "--innodb_flush_log_at_trx_commit=2", "--skip-log-bin",
                     })
                psi.ArgumentList.Add(a);
            var p = Process.Start(psi) ?? throw new ServerDataException($"Could not start {mysqld}.");
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var s = new ThrowawayMySql(p, root, port);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until && !p.HasExited)
            {
                try
                {
                    using var c = new MySqlConnection($"Server=127.0.0.1;Port={port};User ID=root;Password=;SslMode=None;Pooling=False;Connection Timeout=2");
                    c.Open();
                    return s;
                }
                catch (MySqlException) { Thread.Sleep(200); }
            }
            var log = File.Exists(Path.Combine(root, "mysqld.log")) ? File.ReadAllText(Path.Combine(root, "mysqld.log")) : "";
            s.Kill();
            if (attempt < 2 && log.Contains("in use", StringComparison.OrdinalIgnoreCase)) continue;
            s.Dispose();
            throw new ServerDataException($"The throwaway MySQL server did not start: {log[^Math.Min(600, log.Length)..]}");
        }
    }

    /// <summary>Create a database and run an SQL file in it (one statement per line, "--" comments).</summary>
    public void CreateDatabase(string database, string? sqlFile)
    {
        using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port};User ID=root;Password=;SslMode=None;Pooling=False;CharacterSet=utf8;Default Command Timeout=600");
        c.Open();
        Exec(c, $"DROP DATABASE IF EXISTS `{database}`");
        Exec(c, $"CREATE DATABASE `{database}` DEFAULT CHARACTER SET utf8 COLLATE utf8_unicode_ci");
        Exec(c, $"USE `{database}`");
        Exec(c, "SET NAMES utf8");
        Exec(c, "SET SESSION sql_mode=''");
        if (sqlFile == null) return;
        var sb = new StringBuilder();
        foreach (var line in File.ReadLines(sqlFile, Encoding.UTF8))
        {
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            Exec(c, line.TrimEnd().TrimEnd(';'));
        }
    }

    /// <summary>0.13.0: run another SQL file in an existing database (a supplement of the test data: one statement per line).</summary>
    public void RunSqlFile(string database, string sqlFile)
    {
        using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port};User ID=root;Password=;Database={database};SslMode=None;Pooling=False;CharacterSet=utf8;Default Command Timeout=600");
        c.Open();
        Exec(c, "SET NAMES utf8");
        Exec(c, "SET SESSION sql_mode=''");
        foreach (var line in File.ReadLines(sqlFile, Encoding.UTF8))
        {
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            Exec(c, line.TrimEnd().TrimEnd(';'));
        }
    }

    private static void Exec(MySqlConnection c, string sql)
    {
        using var cmd = new MySqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Copy a database (schema and rows) inside this server: a fresh copy per test is faster than loading the
    /// SQL file again.</summary>
    public void CopyDatabase(string from, string to)
    {
        using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port};User ID=root;Password=;SslMode=None;Pooling=False;CharacterSet=utf8;Default Command Timeout=600");
        c.Open();
        Exec(c, $"DROP DATABASE IF EXISTS `{to}`");
        Exec(c, $"CREATE DATABASE `{to}` DEFAULT CHARACTER SET utf8 COLLATE utf8_unicode_ci");
        var tables = new List<string>();
        using (var cmd = new MySqlCommand($"SHOW TABLES FROM `{from}`", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) tables.Add(r.GetString(0));
        foreach (var t in tables)
        {
            Exec(c, $"CREATE TABLE `{to}`.`{t}` LIKE `{from}`.`{t}`");
            Exec(c, $"INSERT INTO `{to}`.`{t}` SELECT * FROM `{from}`.`{t}`");
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(10000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MySqlConnection.ClearAllPools();
        Kill();
        _process.Dispose();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
