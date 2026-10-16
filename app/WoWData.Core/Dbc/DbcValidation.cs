using System.Text;

namespace WoWData.Core.Dbc;

/// <summary>A problem with a value: an error blocks the edit, a warning is shown and allowed.</summary>
public sealed record DbcProblem(bool IsError, string Message)
{
    public override string ToString() => (IsError ? "Error: " : "Warning: ") + Message;
}

/// <summary>Finds the tables that links point at (the editor's open tables first, then the game's).</summary>
public delegate DbcTable? DbcTableLookup(string table);

/// <summary>
/// The checks of a typed value before it goes into a table: the number fits the column (whole number, 0-255, decimal...),
/// a link points at a row that exists, an ID stays unique, a text has no zero character and is not too long (file paths:
/// at most 259 characters, the Windows limit the game has too), a yes / no column holds 0 or 1, a choice is a known value.
/// </summary>
public static class DbcValidation
{
    /// <summary>Longest text in bytes (the game has no fixed limit; this catches pasted garbage).</summary>
    public const int MaxTextBytes = 4096;
    /// <summary>Longest file path (MAX_PATH - 1).</summary>
    public const int MaxPathChars = 259;

    /// <summary>Check a typed text for a column of a row (row null = a new row). Returns the problems; when there is no
    /// error, <paramref name="cell"/> is the value to store (for a localized column: the text of <paramref name="locale"/>).</summary>
    public static List<DbcProblem> Check(DbcTable table, DbcRow? row, DbcColumn c, string text, DbcTableLookup? lookup, out DbcCell cell)
    {
        var problems = new List<DbcProblem>();
        cell = default;
        if (c.IsString)
        {
            if (text.Contains('\0'))
            {
                problems.Add(new(true, $"{c.Name}: a text cannot contain a zero character."));
                return problems;
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > MaxTextBytes) problems.Add(new(true, $"{c.Name}: the text is too long ({bytes.Length:N0} bytes; at most {MaxTextBytes:N0})."));
            if (c.Semantic is DbcSemantic.Icon or DbcSemantic.Texture or DbcSemantic.Model or DbcSemantic.Path)
            {
                if (text.Length > MaxPathChars) problems.Add(new(true, $"{c.Name}: a file path can have at most {MaxPathChars} characters (this one has {text.Length})."));
                if (text.Contains('/')) problems.Add(new(false, $"{c.Name}: the game's paths use backslashes (\\), not slashes (/)."));
                if (text.StartsWith(' ') || text.EndsWith(' ')) problems.Add(new(false, $"{c.Name}: the path starts or ends with a space."));
                if (c.Semantic == DbcSemantic.Icon && text.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                    problems.Add(new(false, $"{c.Name}: icon names are written without \".blp\" (the game adds it)."));
            }
            cell = DbcCell.Of(bytes);
            return problems;
        }
        if (!DbcTable.TryParse(c, text, out uint raw, out string? err))
        {
            problems.Add(new(true, err!));
            return problems;
        }
        cell = DbcCell.Of(raw);
        problems.AddRange(CheckNumber(table, row, c, raw, lookup));
        return problems;
    }

    /// <summary>The checks of a number already parsed (also used by bulk edits and imports).</summary>
    public static List<DbcProblem> CheckNumber(DbcTable table, DbcRow? row, DbcColumn c, uint raw, DbcTableLookup? lookup, IReadOnlySet<long>? extraKeys = null)
    {
        var problems = new List<DbcProblem>();
        long v = c.Storage == DbcStorage.Int32 ? unchecked((int)raw) : raw;
        if (c.Storage == DbcStorage.Float)
        {
            float f = BitConverter.UInt32BitsToSingle(raw);
            if (float.IsNaN(f) || float.IsInfinity(f)) problems.Add(new(false, $"{c.Name}: the value is not a finite number."));
            return problems;
        }
        if (c.IsKey)
        {
            var other = table.Find(v);
            if (other != null && !ReferenceEquals(other, row))
                problems.Add(new(true, $"ID {v} is already used by {table.Label(other)}. Each row needs its own ID (the next free one is {table.SuggestId()})."));
            if (v < 0) problems.Add(new(true, "An ID cannot be negative."));
        }
        if (c.Semantic == DbcSemantic.Bool && raw > 1)
            problems.Add(new(true, $"{c.Name} is a yes / no value: 1 (yes) or 0 (no)."));
        if (c.Semantic == DbcSemantic.Enum && c.Values != null && c.Values.NameOf(v) == null)
            problems.Add(new(false, $"{c.Name}: {v} is not one of the known values of {c.Values.Name}."));
        if (c.ForeignKey != null && v != 0 && v != -1 && raw != uint.MaxValue)
        {
            var target = lookup?.Invoke(c.ForeignKey);
            if (target != null && target.Layout.HasKey && target.Find(v) == null && !(extraKeys != null && c.ForeignKey == table.Name && extraKeys.Contains(v)))
            {
                var msg = $"There is no {c.ForeignKey} with ID {v}. Pick an existing one (or add that row to {c.ForeignKey} first); 0 means none.";
                // a link of a column whose meaning is not verified is only a warning
                problems.Add(new(c.Verified, c.Verified ? msg : msg + " (This column's meaning is not verified, so this is only a warning.)"));
            }
        }
        return problems;
    }

    /// <summary>Errors that would make the game misread the file (used before saving and before building the patch).</summary>
    public static List<string> FileProblems(byte[] data, string name, DbcLayouts? layouts = null)
    {
        var res = new List<string>();
        DbcTable t;
        try { t = DbcTable.Load(data, name, layouts); }
        catch (DbcException e)
        {
            res.Add(e.Message);
            return res;
        }
        var known = (layouts ?? DbcLayouts.Default).Get(name);
        if (known != null && t.Layout.IsFallback)
            res.Add($"{name}.dbc has {t.Layout.FieldCount} fields and {t.Layout.RecordSize} bytes per row; the 1.12.1 game expects {known.FieldCount} and {known.RecordSize} (it refuses the file).");
        if (!t.Layout.IsFallback && t.BadStringOffsets() is var bad and > 0)
            res.Add($"{name}.dbc: {bad} text field(s) point outside the file's text block.");
        if (t.Layout.KeyColumn is { } k)
        {
            var seen = new HashSet<long>();
            foreach (var r in t.Rows)
                if (!seen.Add(t.Number(r, k)))
                {
                    res.Add($"{name}.dbc: ID {t.Number(r, k)} is used by two rows.");
                    break;
                }
        }
        return res;
    }
}
