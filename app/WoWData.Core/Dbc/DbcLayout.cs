using System.Globalization;

namespace WoWData.Core.Dbc;

/// <summary>How a column's bytes are stored in the file.</summary>
public enum DbcStorage
{
    Int32,
    UInt32,
    Float,
    /// <summary>An offset into the string block.</summary>
    String,
    /// <summary>A localized string: 8 offsets (enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX) and a flags field = 9 fields.</summary>
    Loc,
    UInt8,
    UInt16,
}

/// <summary>What a column means, beyond its storage (drives the editor and the previews).</summary>
public enum DbcSemantic
{
    Plain,
    /// <summary>The row's key (unique).</summary>
    Id,
    Bool,
    Enum,
    Flags,
    /// <summary>An icon name: Interface\Icons\&lt;value&gt;.blp (or a full path without the extension).</summary>
    Icon,
    /// <summary>A texture path (with or without .blp).</summary>
    Texture,
    /// <summary>A model path (.mdx / .m2).</summary>
    Model,
    /// <summary>Another game file path (sounds, music, ...).</summary>
    Path,
}

/// <summary>What the world server does with a table.</summary>
public enum DbcServerUse
{
    /// <summary>Only the game (the client) reads it.</summary>
    ClientOnly,
    /// <summary>The server reads its own copy of the file (its data\dbc folder): copy your edit there too.</summary>
    ServerFile,
    /// <summary>The server reads the same data from its SQL database: edit the database, not the file.</summary>
    ServerDatabase,
}

/// <summary>How much of a layout is named and verified.</summary>
public enum DbcCoverage { Full, Partial, Raw }

/// <summary>The names of the values of an enum column, or of the bits of a flags column.</summary>
public sealed class DbcNamedValues
{
    public DbcNamedValues(string name, bool isFlags, IReadOnlyList<(long Value, string Name)> values)
    {
        Name = name;
        IsFlags = isFlags;
        Values = values;
        foreach (var (v, n) in values) _byValue.TryAdd(v, n);
    }

    private readonly Dictionary<long, string> _byValue = new();

    public string Name { get; }
    public bool IsFlags { get; }
    public IReadOnlyList<(long Value, string Name)> Values { get; }

    public string? NameOf(long value) => _byValue.GetValueOrDefault(value);

    /// <summary>"Ranged, Ability" for a flags value (unknown bits as 0x...).</summary>
    public string Describe(long value)
    {
        if (!IsFlags) return NameOf(value) ?? "";
        ulong v = unchecked((ulong)value) & 0xFFFFFFFF;
        if (v == 0) return NameOf(0) ?? "";
        var parts = new List<string>();
        foreach (var (bit, n) in Values)
        {
            if (bit == 0) continue;
            ulong b = unchecked((ulong)bit);
            if ((v & b) == b)
            {
                parts.Add(n);
                v &= ~b;
            }
        }
        if (v != 0) parts.Add("0x" + v.ToString("X", CultureInfo.InvariantCulture));
        return string.Join(", ", parts);
    }
}

/// <summary>One column of a layout. A localized string is one column covering 9 fields.</summary>
public sealed class DbcColumn
{
    public string Name { get; init; } = "";
    /// <summary>Position of the column in the layout's column list.</summary>
    public int Index { get; init; }
    /// <summary>The first file field of the column (0-based).</summary>
    public int Field { get; init; }
    /// <summary>Number of file fields: 9 for a localized string of 1.12 (8 languages + flags), 17 for TBC/WotLK, else 1.</summary>
    public int Fields => Storage == DbcStorage.Loc ? LocaleCount + 1 : 1;
    /// <summary>The languages of a localized string: 8 in 1.12, 16 in TBC 2.4.3 and WotLK 3.3.5a.</summary>
    public int LocaleCount { get; init; } = DbcFile.Locales;
    /// <summary>Byte offset of the column in a record.</summary>
    public int Offset { get; init; }
    /// <summary>Bytes per field (1, 2 or 4).</summary>
    public int Size { get; init; } = 4;
    public DbcStorage Storage { get; init; }
    public DbcSemantic Semantic { get; init; }
    public DbcNamedValues? Values { get; init; }
    /// <summary>The table whose ID this column holds (a link), or null.</summary>
    public string? ForeignKey { get; init; }
    /// <summary>A server-side thing the value names (item, creature, quest, gameobject), for the hint only.</summary>
    public string? ServerRef { get; init; }
    /// <summary>Plain-language explanation shown in the editor.</summary>
    public string Hint { get; init; } = "";
    /// <summary>False when the meaning of the column is not known or not verified (raw col_N or a best guess).</summary>
    public bool Verified { get; init; } = true;

    public bool IsKey => Semantic == DbcSemantic.Id;
    public bool IsString => Storage is DbcStorage.String or DbcStorage.Loc;
    public bool IsNumber => !IsString;
    public bool IsFloat => Storage == DbcStorage.Float;
    public bool IsSigned => Storage == DbcStorage.Int32;

    /// <summary>The type in words (for the column header tooltip and the details panel).</summary>
    public string TypeText
    {
        get
        {
            if (ForeignKey != null) return $"link to {ForeignKey}";
            return Semantic switch
            {
                DbcSemantic.Id => "ID (unique number)",
                DbcSemantic.Bool => "yes / no (1 / 0)",
                DbcSemantic.Enum => Values != null ? $"choice ({Values.Name})" : "choice",
                DbcSemantic.Flags => Values != null ? $"flags ({Values.Name})" : "flags (bit mask)",
                DbcSemantic.Icon => "icon name",
                DbcSemantic.Texture => "texture path",
                DbcSemantic.Model => "model path",
                DbcSemantic.Path => "file path",
                _ => Storage switch
                {
                    DbcStorage.Int32 => "whole number",
                    DbcStorage.UInt32 => "whole number (0 or more)",
                    DbcStorage.Float => "decimal number",
                    DbcStorage.String => "text",
                    DbcStorage.Loc => $"text in {LocaleCount} languages",
                    DbcStorage.UInt8 => "small number (0-255)",
                    DbcStorage.UInt16 => "number (0-65535)",
                    _ => "",
                },
            };
        }
    }

    public override string ToString() => $"{Name} ({TypeText}, field {Field})";
}

/// <summary>The layout of one DBC of the 1.12.1 client: its columns, what it is for and what the server does with it.</summary>
public sealed class DbcLayout
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Other";
    /// <summary>What the table controls in the game, in plain words.</summary>
    public string About { get; init; } = "";
    public DbcServerUse ServerUse { get; init; }
    /// <summary>For <see cref="DbcServerUse.ServerDatabase"/>: the server's table (spell_template, ...).</summary>
    public string? ServerTable { get; init; }
    /// <summary>Extra server advice (e.g. "the map extractor reads it too").</summary>
    public string ServerNote { get; init; } = "";
    /// <summary>Where the layout comes from (the client's record reader, the server's format, checks on the file).</summary>
    public string Evidence { get; init; } = "";
    public IReadOnlyList<DbcColumn> Columns { get; init; } = Array.Empty<DbcColumn>();
    public int FieldCount { get; init; }
    public int RecordSize { get; init; }
    /// <summary>The column used to name a row in links and lists (a name or a path), or null.</summary>
    public DbcColumn? LabelColumn { get; init; }
    /// <summary>True for the generic layout made for a file whose layout is unknown or does not match.</summary>
    public bool IsFallback { get; init; }

    public DbcColumn? KeyColumn => Columns.FirstOrDefault(c => c.IsKey);
    public bool HasKey => KeyColumn != null;

    public int NamedColumns => Columns.Count(c => c.Verified);
    public int UnverifiedColumns => Columns.Count(c => !c.Verified);

    public DbcCoverage Coverage => UnverifiedColumns == 0 ? DbcCoverage.Full
        : Columns.Count(c => c.Verified && !c.IsKey) == 0 ? DbcCoverage.Raw : DbcCoverage.Partial;

    public string CoverageText => Coverage == DbcCoverage.Full ? "All columns named"
        : Columns.Count(c => c.Verified && !c.IsKey) == 0 && Columns.All(c => c.Verified || c.Name.StartsWith("col_", StringComparison.Ordinal)) ? "Raw columns only"
        : $"{NamedColumns} of {Columns.Count} columns named";

    public string ServerText => ServerUse switch
    {
        DbcServerUse.ServerFile => "Server also needs this file",
        DbcServerUse.ServerDatabase => "Server uses its database instead (edit there)",
        _ => "Client only",
    };

    public string ServerExplanation => ServerUse switch
    {
        DbcServerUse.ServerFile => "The world server (mangosd) reads its own copy of this file from its dbc folder. After you change it, " +
                                   "copy it to the server too (\"Copy to server...\"), or the server and the game disagree.",
        DbcServerUse.ServerDatabase => $"The world server does not read this file: it reads the same data from its database (table {ServerTable}). " +
                                       "Your edit changes what the game shows; to change how the server behaves, edit that table in the database.",
        _ => "Only the game reads this table; the server does not need it. Test in game or build the release patch.",
    } + (ServerNote.Length > 0 ? " " + ServerNote : "");

    public DbcColumn? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A layout that only knows the file's shape: col_N per field, raw numbers (strings cannot be told apart).</summary>
    public static DbcLayout Fallback(string name, int fieldCount, int recordSize, string reason)
    {
        var cols = new List<DbcColumn>();
        int size = fieldCount > 0 && recordSize % fieldCount == 0 && recordSize / fieldCount is 1 or 2 or 4 ? recordSize / fieldCount : 4;
        bool mixed = fieldCount * size != recordSize;
        if (!mixed)
            for (int i = 0; i < fieldCount; i++)
                cols.Add(new DbcColumn
                {
                    Name = $"col_{i}", Index = i, Field = i, Offset = i * size, Size = size,
                    Storage = size == 1 ? DbcStorage.UInt8 : size == 2 ? DbcStorage.UInt16 : DbcStorage.Int32,
                    Verified = false, Hint = "Unknown column: shown as a raw number.",
                });
        return new DbcLayout
        {
            Name = name, Category = "Other", About = reason, Evidence = reason, Columns = cols, FieldCount = fieldCount, RecordSize = recordSize,
            IsFallback = true,
        };
    }
}
