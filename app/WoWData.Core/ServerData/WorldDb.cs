using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using WoWData.Core.Index;

namespace WoWData.Core.ServerData;

/// <summary>One condition of a query: a column compared with a value ("entry = 25", "name contains boar").</summary>
public enum FilterOp { Eq, Ne, Lt, Le, Gt, Ge, Contains, In, BitsSet }

public sealed record FilterTerm(string Column, FilterOp Op, string? Value, IReadOnlyList<string>? Values = null);

/// <summary>
/// The rows a view asks for: OR of AND groups of <see cref="FilterTerm"/>s. The same filter becomes a WHERE clause for the
/// database and is evaluated in memory on the rows of your workspace (added and changed rows), so what you see is the
/// server's rows with your edits on top.
/// </summary>
public sealed class RowFilter
{
    public List<List<FilterTerm>> Any { get; } = new();

    public static RowFilter All() => new();

    public static RowFilter Where(params FilterTerm[] all)
    {
        var f = new RowFilter();
        f.Any.Add(all.ToList());
        return f;
    }

    public RowFilter Or(params FilterTerm[] all)
    {
        Any.Add(all.ToList());
        return this;
    }

    public bool IsAll => Any.Count == 0;

    public static RowFilter Eq(string column, object value) => Where(new FilterTerm(column, FilterOp.Eq, Convert.ToString(value, CultureInfo.InvariantCulture)));

    /// <summary>The WHERE clause (with parameters).</summary>
    public string Sql(WorldTable t, MySqlCommand cmd)
    {
        if (IsAll) return "1=1";
        int p = cmd.Parameters.Count;
        var ors = new List<string>();
        foreach (var group in Any)
        {
            var ands = new List<string>();
            foreach (var term in group)
            {
                var c = t.Column(term.Column) ?? throw new ServerDataException($"{t.Name} has no column {term.Column}.");
                var col = WorldValue.Name(c.Name);
                string Param(string? v)
                {
                    var name = "@p" + p++;
                    cmd.Parameters.AddWithValue(name, v);
                    return name;
                }
                ands.Add(term.Op switch
                {
                    FilterOp.Eq when term.Value == null => $"{col} IS NULL",
                    FilterOp.Eq => $"{col} = {Param(term.Value)}",
                    FilterOp.Ne => $"{col} <> {Param(term.Value)}",
                    FilterOp.Lt => $"{col} < {Param(term.Value)}",
                    FilterOp.Le => $"{col} <= {Param(term.Value)}",
                    FilterOp.Gt => $"{col} > {Param(term.Value)}",
                    FilterOp.Ge => $"{col} >= {Param(term.Value)}",
                    FilterOp.Contains => $"{col} LIKE {Param("%" + Escape(term.Value ?? "") + "%")}",
                    FilterOp.In when term.Values is not { Count: > 0 } => "1=0",
                    FilterOp.In => $"{col} IN ({string.Join(",", term.Values!.Select(Param))})",
                    FilterOp.BitsSet => $"({col} & {Param(term.Value)}) <> 0",
                    _ => "1=1",
                });
            }
            ors.Add("(" + string.Join(" AND ", ands) + ")");
        }
        return string.Join(" OR ", ors);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Does a row (canonical values) match?</summary>
    public bool Matches(WorldTable t, IReadOnlyList<string?> row)
    {
        if (IsAll) return true;
        foreach (var group in Any)
        {
            bool ok = true;
            foreach (var term in group)
            {
                int i = t.IndexOf(term.Column);
                if (i < 0) { ok = false; break; }
                if (!Test(t.Columns[i], row[i], term)) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    private static bool Test(WorldColumn c, string? v, FilterTerm term)
    {
        int Cmp(string? x)
        {
            if (c.IsNumber) return WorldValue.Number(v).CompareTo(WorldValue.Number(x));
            return string.Compare(v, x, StringComparison.OrdinalIgnoreCase);
        }
        return term.Op switch
        {
            FilterOp.Eq => term.Value == null ? v == null : v != null && Cmp(term.Value) == 0,
            FilterOp.Ne => v == null || Cmp(term.Value) != 0,
            FilterOp.Lt => v != null && Cmp(term.Value) < 0,
            FilterOp.Le => v != null && Cmp(term.Value) <= 0,
            FilterOp.Gt => v != null && Cmp(term.Value) > 0,
            FilterOp.Ge => v != null && Cmp(term.Value) >= 0,
            FilterOp.Contains => v != null && v.Contains(term.Value ?? "", StringComparison.OrdinalIgnoreCase),
            FilterOp.In => v != null && term.Values != null && term.Values.Any(x => Cmp(x) == 0),
            FilterOp.BitsSet => (WorldValue.Long(v) & WorldValue.Long(term.Value)) != 0,
            _ => true,
        };
    }

    /// <summary>A filter as typed: "column=value" (=, !=, &gt;, &gt;=, &lt;, &lt;=, ~ contains).</summary>
    public static FilterTerm ParseTerm(string text)
    {
        var m = Regex.Match(text, @"^\s*(\w+)\s*(!=|>=|<=|=|>|<|~)\s*(.*)$");
        if (!m.Success) throw new ServerDataException($"\"{text}\" is not a filter: write column=value (also !=, >, >=, <, <=, ~ for \"contains\").");
        var v = m.Groups[3].Value;
        var op = m.Groups[2].Value switch
        {
            "!=" => FilterOp.Ne, ">=" => FilterOp.Ge, "<=" => FilterOp.Le, ">" => FilterOp.Gt, "<" => FilterOp.Lt, "~" => FilterOp.Contains,
            _ => v.Contains(',') ? FilterOp.In : FilterOp.Eq,
        };
        return op == FilterOp.In
            ? new FilterTerm(m.Groups[1].Value, op, null, v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : new FilterTerm(m.Groups[1].Value, op, v == WorldValue.NullText ? null : v);
    }
}

/// <summary>
/// 0.9.0: the connection to the server's world database for the Server data tab and <c>wds server</c>. Reading uses
/// SELECT / SHOW only (checked before running); writing happens only in <see cref="ServerApplier"/> ("Apply to server",
/// "Undo last apply"), and only into a database that is a VMaNGOS world database (it has creature_template and
/// quest_template, and is not the characters or realm database). The password stays in memory (settings: DPAPI) and is
/// never written into logs, scripts or messages.
/// </summary>
public sealed class WorldDb
{
    private static readonly Regex ReadOnly = new(@"^\s*(SELECT|SHOW)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly ServerDbSettings _s;
    private WorldSchema? _schema;

    public WorldDb(ServerDbSettings settings) { _s = settings; }

    public ServerDbSettings Settings => _s;

    /// <summary>"root@127.0.0.1:3306/mangos" (never the password).</summary>
    public string Describe => _s.Describe();

    public MySqlConnection Open(int timeoutSeconds = 5)
    {
        if (!_s.Enabled) throw new ServerDataException("The server database is turned off in Settings (Use the server database for names).");
        var c = new MySqlConnection(new MySqlConnectionStringBuilder
        {
            Server = _s.Host,
            Port = (uint)_s.Port,
            UserID = _s.User,
            Password = _s.Password,
            Database = _s.Database,
            ConnectionTimeout = (uint)timeoutSeconds,
            DefaultCommandTimeout = 600,
            CharacterSet = "utf8",
            SslMode = MySqlSslMode.None,
            AllowPublicKeyRetrieval = true,
            Pooling = true,
            MaximumPoolSize = 8,
            ConvertZeroDateTime = true,
        }.ConnectionString);
        try
        {
            c.Open();
            return c;
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException)
        {
            c.Dispose();
            throw new ServerDataException($"Could not connect to the server's database {Describe}: {e.Message}", e);
        }
    }

    /// <summary>The schema of the world database (read once; <see cref="Invalidate"/> reads it again).</summary>
    public WorldSchema Schema
    {
        get
        {
            if (_schema != null) return _schema;
            using var c = Open();
            var s = WorldSchema.Load(c, _s.Database);
            if (s.Table("creature_template") == null)
                throw new ServerDataException($"{Describe} is not a world database: it has no creature_template table. Settings > Server > World database must name the world database (usually mangos).");
            return _schema = s;
        }
    }

    public void Invalidate() => _schema = null;

    /// <summary>True when the database is the server's world database (and not the characters or realm database), so
    /// that writing into it is allowed.</summary>
    public bool IsWorldDatabase(out string why)
    {
        var s = Schema;
        why = "";
        var name = _s.Database.ToLowerInvariant();
        if (name.Contains("character") || name.Contains("realm") || name.Contains("logon") || name.Contains("auth"))
            why = $"\"{_s.Database}\" looks like the characters or realm database. WoW Data Studio only ever writes the world database.";
        else if (s.Table("characters") != null || s.Table("account") != null)
            why = $"\"{_s.Database}\" has player tables (characters / account): it is not the world database. Nothing is written there.";
        else if (s.Table("quest_template") == null || s.Table("item_template") == null)
            why = $"\"{_s.Database}\" does not look like a world database (no quest_template / item_template).";
        return why.Length == 0;
    }

    private static void CheckReadOnly(string sql)
    {
        if (!ReadOnly.IsMatch(sql)) throw new ServerDataException("Only SELECT and SHOW are used for reading the server's database.");
    }

    /// <summary>Rows of a table that match a filter, as canonical values in column order.</summary>
    public List<string?[]> Rows(WorldTable t, RowFilter filter, int limit = 0, string? orderBy = null, MySqlConnection? conn = null)
    {
        var res = new List<string?[]>();
        var own = conn == null;
        var c = conn ?? Open();
        try
        {
            using var cmd = new MySqlCommand { Connection = c };
            var where = filter.Sql(t, cmd);
            var order = orderBy ?? string.Join(", ", t.Key.Select(WorldValue.Name));
            cmd.CommandText = $"SELECT {string.Join(", ", t.Columns.Select(x => WorldValue.Name(x.Name)))} FROM {WorldValue.Name(t.Name)} WHERE {where}" +
                              (order.Length > 0 ? " ORDER BY " + order : "") + (limit > 0 ? " LIMIT " + limit.ToString(CultureInfo.InvariantCulture) : "");
            CheckReadOnly(cmd.CommandText);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new string?[t.Columns.Count];
                for (int i = 0; i < row.Length; i++) row[i] = WorldValue.FromDb(r.IsDBNull(i) ? null : r.GetValue(i), t.Columns[i]);
                res.Add(row);
            }
            return res;
        }
        catch (MySqlException e)
        {
            throw new ServerDataException($"Reading {t.Name} failed: {e.Message}", e);
        }
        finally
        {
            if (own) c.Dispose();
        }
    }

    /// <summary>The rows of one key (usually one row).</summary>
    public string?[]? Row(WorldTable t, string keyText, MySqlConnection? conn = null) => Rows(t, KeyFilter(t, keyText), 2, null, conn).FirstOrDefault();

    public static RowFilter KeyFilter(WorldTable t, string keyText)
    {
        var k = WorldTable.ParseKeyText(keyText);
        return RowFilter.Where(t.Key.Select(c => new FilterTerm(c, FilterOp.Eq, k.GetValueOrDefault(c))).ToArray());
    }

    /// <summary>One value (the first column of the first row) of a read-only query.</summary>
    public object? Scalar(string sql, params (string, object?)[] args)
    {
        CheckReadOnly(sql);
        using var c = Open();
        using var cmd = new MySqlCommand(sql, c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        try { return cmd.ExecuteScalar(); }
        catch (MySqlException e) { throw new ServerDataException($"The query failed: {e.Message}", e); }
    }

    /// <summary>Rows of a read-only query as raw values.</summary>
    public List<object?[]> Query(string sql, params (string, object?)[] args)
    {
        CheckReadOnly(sql);
        using var c = Open();
        using var cmd = new MySqlCommand(sql, c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var res = new List<object?[]>();
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                res.Add(row);
            }
        }
        catch (MySqlException e) { throw new ServerDataException($"The query failed: {e.Message}", e); }
        return res;
    }

    /// <summary>A table as INSERT statements (with its CREATE TABLE), for the full backup before an apply.</summary>
    public void DumpTable(WorldTable t, TextWriter w)
    {
        using var c = Open();
        using (var cmd = new MySqlCommand($"SHOW CREATE TABLE {WorldValue.Name(t.Name)}", c))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) w.WriteLine($"-- {t.Name} as it was:\n-- {r.GetString(1).Replace("\n", "\n-- ")};");
        w.WriteLine($"DELETE FROM {WorldValue.Name(t.Name)};");
        var cols = string.Join(",", t.Columns.Select(x => WorldValue.Name(x.Name)));
        using var q = new MySqlCommand($"SELECT {cols} FROM {WorldValue.Name(t.Name)}", c);
        using var rr = q.ExecuteReader();
        var sb = new StringBuilder();
        int n = 0;
        while (rr.Read())
        {
            if (n == 0) sb.Append($"INSERT INTO {WorldValue.Name(t.Name)} ({cols}) VALUES ");
            else sb.Append(',');
            sb.Append('(');
            for (int i = 0; i < t.Columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(WorldValue.Sql(t.Columns[i], WorldValue.FromDb(rr.IsDBNull(i) ? null : rr.GetValue(i), t.Columns[i])));
            }
            sb.Append(')');
            if (++n == 200)
            {
                w.Write(sb.Append(";\n"));
                sb.Clear();
                n = 0;
            }
        }
        if (n > 0) w.Write(sb.Append(";\n"));
    }
}
