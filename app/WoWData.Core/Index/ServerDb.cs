using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WoWData.Core.Index;

public sealed class ServerDbException : Exception
{
    public ServerDbException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Connection settings for the local server's world database (read-only use). In settings.json the password
/// is stored encrypted with Windows DPAPI for the current user ("PasswordProtected"); a plain-text "Password" written by
/// version 0.3 is read once, flagged <see cref="MigratedFromPlainText"/> and replaced by the encrypted form when the
/// settings are saved (AppSettings.Load does that immediately).</summary>
public sealed class ServerDbSettings
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WoWDataStudio.ServerDb.v1");

    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";

    /// <summary>The password in memory (never written to settings.json as text).</summary>
    [JsonIgnore]
    public string Password { get; set; } = "root";

    public string Database { get; set; } = "mangos";

    /// <summary>The password encrypted with DPAPI (current user, base64), as written to settings.json.</summary>
    [JsonPropertyName("PasswordProtected")]
    public string? PasswordProtected
    {
        get => Protect(Password);
        set
        {
            if (value == null) return;
            var p = Unprotect(value);
            PasswordUnreadable = p == null;
            Password = p ?? "";
        }
    }

    /// <summary>The plain-text password of an old settings.json: read for the migration, never written.</summary>
    [JsonPropertyName("Password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPassword
    {
        get => null;
        set
        {
            if (value == null) return;
            Password = value;
            MigratedFromPlainText = true;
        }
    }

    /// <summary>True when the settings held the password as plain text (they must be saved again).</summary>
    [JsonIgnore]
    public bool MigratedFromPlainText { get; private set; }

    /// <summary>True when the stored password could not be decrypted (another Windows user or computer).</summary>
    [JsonIgnore]
    public bool PasswordUnreadable { get; private set; }

    public static string? Protect(string? plain)
    {
        if (plain == null) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the password is protected with Windows DPAPI");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string? Unprotect(string base64)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public ServerDbSettings Clone() => (ServerDbSettings)MemberwiseClone();

    public string Describe() => $"{User}@{Host}:{Port}/{Database}";
}

public sealed record ServerCreature(int Entry, string Name, string? SubName, int LevelMin, int LevelMax, int[] Displays);
public sealed record ServerGameObject(int Entry, string Name, int Type, int DisplayId);
public sealed record ServerItem(int Entry, string Name, int DisplayId, int InventoryType, int Quality, int Class, int SubClass);
public sealed record ServerSpawn(int Entry, int Map, int Count);

public sealed class ServerData
{
    public string Schema { get; init; } = "";
    public List<ServerCreature> Creatures { get; init; } = new();
    public List<ServerGameObject> GameObjects { get; init; } = new();
    public List<ServerItem> Items { get; init; } = new();
    public List<ServerSpawn> CreatureSpawns { get; init; } = new();
    public List<ServerSpawn> GameObjectSpawns { get; init; } = new();
}

/// <summary>
/// Read-only access to the server's world database. Supports both schemas used with 1.12 clients:
///  * VMaNGOS (this machine's server): one template row per content patch (`patch` column); the row with the highest
///    patch per entry is used, which is what a 1.12 server loads. Columns name, subname, level_min, display_id1..4,
///    gameobject_template.displayId, item_template.display_id.
///  * CMaNGOS / MaNGOS classic: creature_template Name, SubName, MinLevel, MaxLevel, ModelId1..4 (or modelid_1..),
///    item_template.displayid.
/// The schema is detected from the columns. Only SELECT/SHOW statements are ever sent (checked before running).
/// </summary>
public sealed class ServerDb
{
    private static readonly Regex SelectOnly = new(@"^\s*(SELECT|SHOW)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly ServerDbSettings _s;

    public ServerDb(ServerDbSettings settings) { _s = settings; }

    private string ConnectionString(int timeoutSeconds) => new MySqlConnectionStringBuilder
    {
        Server = _s.Host,
        Port = (uint)_s.Port,
        UserID = _s.User,
        Password = _s.Password,
        Database = _s.Database,
        ConnectionTimeout = (uint)timeoutSeconds,
        DefaultCommandTimeout = 300,
        CharacterSet = "utf8",
        SslMode = MySqlSslMode.None,
        AllowPublicKeyRetrieval = true,
        Pooling = false,
    }.ConnectionString;

    private async Task<MySqlConnection> OpenAsync(int timeoutSeconds, CancellationToken ct)
    {
        var c = new MySqlConnection(ConnectionString(timeoutSeconds));
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            return c;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException)
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw new ServerDbException($"could not connect to {_s.Describe()}: {e.Message}", e);
        }
    }

    /// <summary>Rows of one SELECT, as object arrays (DBNull -> null).</summary>
    public async Task<List<object?[]>> QueryAsync(MySqlConnection c, string sql, CancellationToken ct)
    {
        foreach (var stmt in sql.Split(';').Where(s => s.Trim().Length > 0))
            if (!SelectOnly.IsMatch(stmt)) throw new ServerDbException($"only SELECT/SHOW statements are allowed: {stmt.Trim()[..Math.Min(60, stmt.Trim().Length)]}");
        try
        {
            await using var cmd = new MySqlCommand(sql, c);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<object?[]>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[r.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (MySqlException e)
        {
            throw new ServerDbException($"query failed: {e.Message}", e);
        }
    }

    /// <summary>True when the database answers within a few seconds.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        if (!_s.Enabled) return (false, "turned off in Settings");
        try
        {
            await using var c = await OpenAsync(4, ct).ConfigureAwait(false);
            var cols = await ColumnsAsync(c, "creature_template", ct).ConfigureAwait(false);
            if (cols.Count == 0) return (false, $"connected to {_s.Describe()}, but it has no creature_template table (is it the world database?)");
            var schema = cols.Contains("patch") ? "VMaNGOS" : "CMaNGOS/MaNGOS";
            return (true, $"connected to {_s.Describe()} ({schema} schema)");
        }
        catch (ServerDbException e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>An open connection for further read-only queries (<see cref="QueryAsync"/> checks every statement).
    /// Used by the "Used in game" tables (Usage\UsageServerLoader).</summary>
    public Task<MySqlConnection> ConnectAsync(CancellationToken ct = default) => OpenAsync(5, ct);

    /// <summary>The lower-case column names of a table (empty when the table does not exist).</summary>
    public Task<HashSet<string>> TableColumnsAsync(MySqlConnection c, string table, CancellationToken ct = default) => ColumnsAsync(c, table, ct);

    private async Task<HashSet<string>> ColumnsAsync(MySqlConnection c, string table, CancellationToken ct)
    {
        try
        {
            var rows = await QueryAsync(c, $"SHOW COLUMNS FROM `{table}`", ct).ConfigureAwait(false);
            return new HashSet<string>(rows.Select(r => Convert.ToString(r[0])!.ToLowerInvariant()));
        }
        catch (ServerDbException)
        {
            return new HashSet<string>();
        }
    }

    private static string Pick(HashSet<string> cols, params string[] options)
    {
        foreach (var o in options) if (cols.Contains(o.ToLowerInvariant())) return o;
        return "";
    }

    private static string Col(string name, string alias) => name.Length == 0 ? $"NULL AS {alias}" : $"t.`{name}` AS {alias}";

    private static string LatestFrom(string table, bool patched) => patched
        ? $"FROM `{table}` t JOIN (SELECT entry, MAX(patch) p FROM `{table}` GROUP BY entry) m ON t.entry = m.entry AND t.patch = m.p"
        : $"FROM `{table}` t";

    private static int I(object? v) => v == null ? 0 : Convert.ToInt32(v);
    private static string S(object? v) => v == null ? "" : Convert.ToString(v) ?? "";

    /// <summary>Everything the index needs from the server, or a <see cref="ServerDbException"/>.</summary>
    public async Task<ServerData> LoadAsync(CancellationToken ct = default)
    {
        if (!_s.Enabled) throw new ServerDbException("the server database is turned off in Settings");
        await using var c = await OpenAsync(5, ct).ConfigureAwait(false);
        var ct1 = await ColumnsAsync(c, "creature_template", ct).ConfigureAwait(false);
        if (ct1.Count == 0) throw new ServerDbException($"{_s.Describe()} has no creature_template table");
        bool patched = ct1.Contains("patch");
        var entry = Pick(ct1, "entry", "Entry");
        var name = Pick(ct1, "name", "Name");
        var sub = Pick(ct1, "subname", "SubName");
        var lmin = Pick(ct1, "level_min", "MinLevel", "minlevel");
        var lmax = Pick(ct1, "level_max", "MaxLevel", "maxlevel");
        var disp = new[]
        {
            Pick(ct1, "display_id1", "ModelId1", "modelid_1", "modelid_A"), Pick(ct1, "display_id2", "ModelId2", "modelid_2", "modelid_A2"),
            Pick(ct1, "display_id3", "ModelId3", "modelid_3", "modelid_H"), Pick(ct1, "display_id4", "ModelId4", "modelid_4", "modelid_H2"),
        };
        var sql = $"SELECT {Col(entry, "e")}, {Col(name, "n")}, {Col(sub, "s")}, {Col(lmin, "l1")}, {Col(lmax, "l2")}, " +
                  string.Join(", ", disp.Select((d, i) => Col(d, "d" + i))) + " " + LatestFrom("creature_template", patched);
        var creatures = (await QueryAsync(c, sql, ct).ConfigureAwait(false))
            .Select(r => new ServerCreature(I(r[0]), S(r[1]), r[2] == null ? null : S(r[2]), I(r[3]), I(r[4]), new[] { I(r[5]), I(r[6]), I(r[7]), I(r[8]) }))
            .ToList();

        var gc = await ColumnsAsync(c, "gameobject_template", ct).ConfigureAwait(false);
        var gameobjects = new List<ServerGameObject>();
        if (gc.Count > 0)
        {
            sql = $"SELECT {Col(Pick(gc, "entry"), "e")}, {Col(Pick(gc, "name"), "n")}, {Col(Pick(gc, "type"), "t")}, {Col(Pick(gc, "displayId", "display_id"), "d")} " +
                  LatestFrom("gameobject_template", gc.Contains("patch"));
            gameobjects = (await QueryAsync(c, sql, ct).ConfigureAwait(false)).Select(r => new ServerGameObject(I(r[0]), S(r[1]), I(r[2]), I(r[3]))).ToList();
        }

        var ic = await ColumnsAsync(c, "item_template", ct).ConfigureAwait(false);
        var items = new List<ServerItem>();
        if (ic.Count > 0)
        {
            sql = $"SELECT {Col(Pick(ic, "entry"), "e")}, {Col(Pick(ic, "name"), "n")}, {Col(Pick(ic, "display_id", "displayid"), "d")}, " +
                  $"{Col(Pick(ic, "inventory_type", "InventoryType"), "it")}, {Col(Pick(ic, "quality", "Quality"), "q")}, {Col(Pick(ic, "class"), "c")}, {Col(Pick(ic, "subclass"), "sc")} " +
                  LatestFrom("item_template", ic.Contains("patch"));
            items = (await QueryAsync(c, sql, ct).ConfigureAwait(false)).Select(r => new ServerItem(I(r[0]), S(r[1]), I(r[2]), I(r[3]), I(r[4]), I(r[5]), I(r[6]))).ToList();
        }

        async Task<List<ServerSpawn>> Spawns(string table)
        {
            var cols = await ColumnsAsync(c, table, ct).ConfigureAwait(false);
            if (!cols.Contains("id") || !cols.Contains("map")) return new List<ServerSpawn>();
            return (await QueryAsync(c, $"SELECT id, map, COUNT(*) FROM `{table}` GROUP BY id, map", ct).ConfigureAwait(false))
                .Select(r => new ServerSpawn(I(r[0]), I(r[1]), I(r[2]))).ToList();
        }

        return new ServerData
        {
            Schema = patched ? "VMaNGOS" : "CMaNGOS/MaNGOS",
            Creatures = creatures,
            GameObjects = gameobjects,
            Items = items,
            CreatureSpawns = await Spawns("creature").ConfigureAwait(false),
            GameObjectSpawns = await Spawns("gameobject").ConfigureAwait(false),
        };
    }
}
