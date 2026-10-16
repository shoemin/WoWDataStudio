using System.Globalization;
using System.Numerics;
using System.Text;
using MySqlConnector;

namespace WoWData.Core.ServerData;

/// <summary>The SQL type of a column, in the groups the editor checks and formats.</summary>
public enum WorldType { Integer, Float, Double, Decimal, Text, Binary, Other }

/// <summary>One column of a world table, as the live database describes it (information_schema).</summary>
public sealed class WorldColumn
{
    public string Name { get; init; } = "";
    public int Ordinal { get; init; }
    /// <summary>"int", "tinyint", "varchar"...</summary>
    public string DataType { get; init; } = "";
    /// <summary>"int(10) unsigned", "varchar(100)"...</summary>
    public string ColumnType { get; init; } = "";
    public bool Unsigned { get; init; }
    public bool Nullable { get; init; }
    /// <summary>The default value as text (null: no default or NULL).</summary>
    public string? Default { get; init; }
    /// <summary>Characters for char / varchar, bytes for text types.</summary>
    public long MaxLength { get; init; }
    public int Precision { get; init; }
    public int Scale { get; init; }
    public string? Charset { get; init; }
    public string Comment { get; init; } = "";
    public bool AutoIncrement { get; init; }

    public WorldType Type => DataType switch
    {
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "bit" or "year" => WorldType.Integer,
        "float" => WorldType.Float,
        "double" or "real" => WorldType.Double,
        "decimal" or "numeric" => WorldType.Decimal,
        "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" => WorldType.Text,
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => WorldType.Binary,
        _ => WorldType.Other,
    };

    public bool IsNumber => Type is WorldType.Integer or WorldType.Float or WorldType.Double or WorldType.Decimal;
    public bool IsText => Type == WorldType.Text;
    /// <summary>char / varchar count characters; the text types count bytes.</summary>
    public bool LengthInBytes => DataType.EndsWith("text", StringComparison.Ordinal);

    /// <summary>The smallest and largest whole number the column holds.</summary>
    public (BigInteger Min, BigInteger Max) IntegerRange
    {
        get
        {
            int bits = DataType switch { "tinyint" => 8, "smallint" => 16, "mediumint" => 24, "bigint" => 64, "year" => 16, "bit" => Math.Max(1, Precision), _ => 32 };
            if (DataType == "bit") return (0, (BigInteger.One << bits) - 1);
            return Unsigned ? (BigInteger.Zero, (BigInteger.One << bits) - 1) : (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1);
        }
    }

    public override string ToString() => $"{Name} {ColumnType}";
}

/// <summary>How the rows of a table are told apart: its primary key, a unique key, or (no key at all) every column
/// that is not a decimal number.</summary>
public enum WorldKeyKind { Primary, Unique, AllColumns }

/// <summary>One table of the world database, as the live database describes it.</summary>
public sealed class WorldTable
{
    public string Name { get; init; } = "";
    public string Engine { get; init; } = "";
    public string Comment { get; init; } = "";
    public long RowsApprox { get; init; }
    public List<WorldColumn> Columns { get; } = new();
    public List<string> Key { get; } = new();
    public WorldKeyKind KeyKind { get; set; }
    private Dictionary<string, int>? _index;

    public int IndexOf(string column)
    {
        _index ??= Columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
        return _index.TryGetValue(column, out var i) ? i : -1;
    }

    public WorldColumn? Column(string name) => IndexOf(name) is var i and >= 0 ? Columns[i] : null;
    public bool Has(string column) => IndexOf(column) >= 0;
    public bool Transactional => Engine.Equals("InnoDB", StringComparison.OrdinalIgnoreCase);
    public int[] KeyIndexes => Key.Select(IndexOf).ToArray();

    /// <summary>The canonical text of a row's key ("entry=25;patch=0").</summary>
    public string KeyText(IReadOnlyList<string?> row) => string.Join(";", Key.Select(k => k + "=" + (row[IndexOf(k)] ?? "\\N")));

    /// <summary>The key's values of a key text ("entry=25;patch=0").</summary>
    public static Dictionary<string, string?> ParseKeyText(string keyText) =>
        keyText.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 && p[1] != "\\N" ? p[1] : null, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 0.9.0: the tables and columns of the server's world database as the live database describes them
/// (information_schema): names, SQL types, NULL, defaults, lengths, character sets, keys and storage engine. The editor
/// takes every column from here, so a column that WoW Data Studio does not know is still shown and checked by its type.
/// </summary>
public sealed class WorldSchema
{
    public string Database { get; init; } = "";
    public Dictionary<string, WorldTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The newest applied VMaNGOS migration (the `migrations` table), or "".</summary>
    public string Migration { get; set; } = "";
    public string ServerVersion { get; set; } = "";

    public WorldTable? Table(string name) => Tables.GetValueOrDefault(name);

    public WorldTable Require(string name) => Table(name) ?? throw new ServerDataException($"The server's database has no table \"{name}\".");

    public bool IsVmangos => Table("creature_template")?.Has("patch") == true && Table("spell_template") != null;

    public string Describe => $"{Database}" + (IsVmangos ? " (VMaNGOS" + (Migration.Length > 0 ? $", schema migration {Migration}" : "") + ")" : "") + (ServerVersion.Length > 0 ? $", MySQL {ServerVersion}" : "");

    /// <summary>Read the schema of the connection's database.</summary>
    public static WorldSchema Load(MySqlConnection c, string database)
    {
        var s = new WorldSchema { Database = database };
        using (var cmd = new MySqlCommand("SELECT TABLE_NAME, ENGINE, IFNULL(TABLE_COMMENT,''), IFNULL(TABLE_ROWS,0) FROM information_schema.TABLES WHERE TABLE_SCHEMA=@d AND TABLE_TYPE='BASE TABLE'", c))
        {
            cmd.Parameters.AddWithValue("@d", database);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                s.Tables[r.GetString(0)] = new WorldTable { Name = r.GetString(0), Engine = r.IsDBNull(1) ? "" : r.GetString(1), Comment = r.GetString(2), RowsApprox = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture) };
        }
        using (var cmd = new MySqlCommand(
                   "SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, CHARACTER_MAXIMUM_LENGTH, " +
                   "NUMERIC_PRECISION, NUMERIC_SCALE, CHARACTER_SET_NAME, IFNULL(COLUMN_COMMENT,''), EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@d ORDER BY TABLE_NAME, ORDINAL_POSITION", c))
        {
            cmd.Parameters.AddWithValue("@d", database);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!s.Tables.TryGetValue(r.GetString(0), out var t)) continue;
                var ct = r.GetString(4);
                t.Columns.Add(new WorldColumn
                {
                    Name = r.GetString(1),
                    Ordinal = Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
                    DataType = r.GetString(3).ToLowerInvariant(),
                    ColumnType = ct,
                    Unsigned = ct.Contains("unsigned", StringComparison.OrdinalIgnoreCase),
                    Nullable = r.GetString(5) == "YES",
                    Default = r.IsDBNull(6) ? null : Convert.ToString(r.GetValue(6), CultureInfo.InvariantCulture),
                    MaxLength = r.IsDBNull(7) ? 0 : Convert.ToInt64(r.GetValue(7), CultureInfo.InvariantCulture),
                    Precision = r.IsDBNull(8) ? 0 : Convert.ToInt32(r.GetValue(8), CultureInfo.InvariantCulture),
                    Scale = r.IsDBNull(9) ? 0 : Convert.ToInt32(r.GetValue(9), CultureInfo.InvariantCulture),
                    Charset = r.IsDBNull(10) ? null : r.GetString(10),
                    Comment = r.GetString(11),
                    AutoIncrement = !r.IsDBNull(12) && r.GetString(12).Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        var keys = new Dictionary<string, Dictionary<string, List<(int Seq, string Col)>>>(StringComparer.OrdinalIgnoreCase);
        var unique = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = new MySqlCommand("SELECT TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=@d ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX", c))
        {
            cmd.Parameters.AddWithValue("@d", database);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture) != 0) continue;
                var t = r.GetString(0);
                if (!keys.TryGetValue(t, out var idx)) keys[t] = idx = new();
                var name = r.GetString(1);
                if (!idx.TryGetValue(name, out var cols)) idx[name] = cols = new();
                cols.Add((Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture), r.GetString(3)));
            }
        }
        foreach (var t in s.Tables.Values)
        {
            t.Columns.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            if (keys.TryGetValue(t.Name, out var idx) && idx.TryGetValue("PRIMARY", out var pk))
            {
                t.Key.AddRange(pk.OrderBy(x => x.Seq).Select(x => x.Col));
                t.KeyKind = WorldKeyKind.Primary;
            }
            else if (keys.TryGetValue(t.Name, out var idx2) && idx2.Count > 0)
            {
                var uk = idx2.OrderBy(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Value;
                t.Key.AddRange(uk.OrderBy(x => x.Seq).Select(x => x.Col));
                t.KeyKind = WorldKeyKind.Unique;
            }
            else
            {
                t.Key.AddRange(t.Columns.Where(x => x.Type is WorldType.Integer or WorldType.Text or WorldType.Decimal).Select(x => x.Name));
                t.KeyKind = WorldKeyKind.AllColumns;
            }
        }
        using (var cmd = new MySqlCommand("SELECT VERSION()", c)) s.ServerVersion = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        if (s.Table("migrations") is { } mig && mig.Columns.Count > 0)
            try
            {
                using var cmd = new MySqlCommand($"SELECT MAX(`{mig.Columns[0].Name}`) FROM `migrations`", c);
                s.Migration = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
            }
            catch (MySqlException) { }
        return s;
    }
}

/// <summary>The one exception type of the server data features: a refused value, a missing row, a database that answered
/// with an error...</summary>
public sealed class ServerDataException : Exception
{
    public ServerDataException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The values of the world database as exact texts: every value is kept as its canonical text (whole numbers in decimal,
/// FLOAT columns as the shortest text that gives the same 32-bit number, texts as they are, NULL as null), so edits,
/// the workspace files, CSV / JSON and the SQL scripts never lose a bit.
/// </summary>
public static class WorldValue
{
    /// <summary>The text that means NULL in edit boxes, CSV files and on the command line.</summary>
    public const string NullText = "\\N";

    /// <summary>The canonical text of a value read from the database.</summary>
    public static string? FromDb(object? v, WorldColumn c)
    {
        if (v == null || v is DBNull) return null;
        switch (v)
        {
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case double d: return c.Type == WorldType.Float ? ((float)d).ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString(CultureInfo.InvariantCulture);
            case byte[] b: return "0x" + Convert.ToHexString(b);
            case DateTime t: return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case bool b: return b ? "1" : "0";
            case IFormattable x: return x.ToString(null, CultureInfo.InvariantCulture);
            default: return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Check a typed text against the column's SQL type and give its canonical text (null = NULL). Returns the
    /// problem in words, or null when the value fits.</summary>
    public static string? Normalize(WorldColumn c, string? text, out string? canonical)
    {
        canonical = null;
        if (text == null || text == NullText)
        {
            if (c.Nullable) return null;
            return $"{c.Name} cannot be empty (NULL); it needs a value.";
        }
        switch (c.Type)
        {
            case WorldType.Integer:
            {
                var t = text.Trim();
                BigInteger v;
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!BigInteger.TryParse("0" + t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return $"\"{text}\" is not a whole number.";
                }
                else if (!BigInteger.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) return $"\"{text}\" is not a whole number.";
                var (min, max) = c.IntegerRange;
                if (v < min || v > max) return $"{v} does not fit in {c.Name} ({c.ColumnType}: {min} to {max}).";
                canonical = v.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            case WorldType.Float:
            {
                if (!float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || float.IsNaN(f) || float.IsInfinity(f))
                    return $"\"{text}\" is not a number (use a dot for decimals: 1.5).";
                if (c.Unsigned && f < 0) return $"{c.Name} cannot be negative.";
                canonical = f.ToString("R", CultureInfo.InvariantCulture);
                return null;
            }
            case WorldType.Double:
            {
                if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d))
                    return $"\"{text}\" is not a number (use a dot for decimals: 1.5).";
                canonical = d.ToString("R", CultureInfo.InvariantCulture);
                return null;
            }
            case WorldType.Decimal:
            {
                if (!decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return $"\"{text}\" is not a number.";
                canonical = Math.Round(m, c.Scale).ToString(CultureInfo.InvariantCulture);
                return null;
            }
            case WorldType.Text:
            {
                if (c.MaxLength > 0)
                {
                    long len = c.LengthInBytes ? Encoding.UTF8.GetByteCount(text) : new StringInfo(text).LengthInTextElements;
                    if (len > c.MaxLength) return $"The text is too long for {c.Name}: {len} {(c.LengthInBytes ? "bytes" : "characters")}, at most {c.MaxLength} ({c.ColumnType}).";
                }
                if (c.Charset is "utf8" or "utf8mb3" && text.Any(char.IsSurrogate))
                    return $"{c.Name} cannot store characters outside the basic set (emoji and similar): the database column is utf8 (3 bytes).";
                if (c.Charset == "latin1" && text.Any(ch => ch > 0xFF))
                    return $"{c.Name} stores Western European characters only (latin1): \"{text.First(ch => ch > 0xFF)}\" would turn into \"?\".";
                // MySQL drops the spaces at the end of a CHAR value when it reads it: keep the value as it will read back
                canonical = c.DataType == "char" ? text.TrimEnd(' ') : text;
                return null;
            }
            default:
                canonical = text;
                return null;
        }
    }

    /// <summary>The SQL literal of a canonical value (FLOAT values with 9 significant digits, which MySQL turns back into
    /// exactly the same 32-bit number; texts quoted and escaped).</summary>
    public static string Sql(WorldColumn c, string? v)
    {
        if (v == null) return "NULL";
        switch (c.Type)
        {
            case WorldType.Integer:
            case WorldType.Decimal:
                return v;
            case WorldType.Float:
                return float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("G9", CultureInfo.InvariantCulture);
            case WorldType.Double:
                return double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            case WorldType.Binary when v.StartsWith("0x", StringComparison.OrdinalIgnoreCase):
                return v.Length == 2 ? "''" : v;
            default:
                return Quote(v);
        }
    }

    /// <summary>A quoted SQL text (backslash escapes, as MySQL reads them by default).</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '"' => "\\\"",
                '\0' => "\\0",
                '\n' => "\\n",
                '\r' => "\\r",
                '\x1a' => "\\Z",
                _ => ch.ToString(),
            });
        return sb.Append('\'').ToString();
    }

    public static string Name(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    /// <summary>The number of a canonical value (0 for NULL or text).</summary>
    public static double Number(string? v) => v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    public static long Long(string? v) => v != null && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l
        : v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;

    /// <summary>Two canonical values of a column are equal (numbers by value: "1.0" = "1").</summary>
    public static bool Same(WorldColumn c, string? a, string? b)
    {
        if (a == null || b == null) return a == b;
        if (c.Type == WorldType.Float)
            return float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);
        if (c.IsNumber) return Number(a) == Number(b) && (c.Type != WorldType.Integer || a.TrimStart('+') == b.TrimStart('+'));
        return string.Equals(a, b, StringComparison.Ordinal);
    }
}
