using WoWData.Core.Db2;

namespace WoWData.Core.Versions;

/// <summary>Where the column names of another version's table come from.</summary>
public enum ColumnNameSource
{
    /// <summary>No names: col_N.</summary>
    None,
    /// <summary>Our own measured layout file for the build (<see cref="Db2Layouts"/>, or the DBC layouts of an MPQ client).</summary>
    Measured,
    /// <summary>The WoWDBDefs folder the user chose (<see cref="DbdFolder"/>, setting other-clients.dbd).</summary>
    WoWDBDefs,
}

/// <summary>The name source of a table and, when WoWDBDefs could not name it, why (the fallback reason; "" otherwise).</summary>
public sealed record TableNaming(ColumnNameSource Source, string Fallback = "")
{
    /// <summary>"WoWDBDefs", "measured" or "none" (wds --json).</summary>
    public string Key => Source switch { ColumnNameSource.WoWDBDefs => "WoWDBDefs", ColumnNameSource.Measured => "measured", _ => "none" };

    /// <summary>What the app and wds show per table: "WoWDBDefs", "measured (our layout file)" or "raw (col_N)".</summary>
    public string Text => Source switch
    {
        ColumnNameSource.WoWDBDefs => "WoWDBDefs",
        ColumnNameSource.Measured => "measured (our layout file)",
        _ => "raw (col_N)",
    };

    /// <summary>The text with the fallback reason: "measured (our layout file); WoWDBDefs: Map.dbd has no definition for ...".</summary>
    public string Long => Fallback.Length == 0 ? Text : $"{Text}; not WoWDBDefs: {Fallback}";

    public static readonly TableNaming Raw = new(ColumnNameSource.None);
}

/// <summary>
/// M5 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): how the table views of another version (the Data tables tab, the
/// Other versions tab, "wds dbc ... --version", "wds versions tables / rows / compare") name a DB2 table's columns:
/// WoWDBDefs first when the user chose a folder (setting other-clients.dbd) and it has a definition for that build
/// and layout; else our own measured layout file for the build, with the reason WoWDBDefs did not name it; else
/// col_N. The features (Browse chains, models, maps, the update catalog) always use our measured file
/// (<see cref="Db2Layouts"/>) and are not changed by this.
/// </summary>
public static class ColumnNames
{
    /// <summary>Names the columns of a table that was just read and says where the names came from.</summary>
    public static TableNaming Apply(Db2Table table, int build, string version, DbdFolder? dbd)
    {
        string fallback = "";
        if (dbd != null)
        {
            if (dbd.Apply(table, version, out var why)) return new TableNaming(ColumnNameSource.WoWDBDefs);
            fallback = why;
        }
        if (Db2Layouts.ForBuild(build) is { } l && l.Apply(table, out _)) return new TableNaming(ColumnNameSource.Measured, fallback);
        return new TableNaming(ColumnNameSource.None, fallback);
    }

    /// <summary>The name source a table is expected to get, without reading it (the lists of tables): WoWDBDefs when
    /// its file has a definition for the build (and the executable's layout hash), else measured when our layout file
    /// has the table. Reading the table checks it (<see cref="Apply"/>).</summary>
    public static TableNaming Expected(string table, uint? layoutHash, int build, string version, DbdFolder? dbd)
    {
        string fallback = "";
        if (dbd != null)
        {
            if (dbd.Knows(table, version, layoutHash, out var why)) return new TableNaming(ColumnNameSource.WoWDBDefs);
            fallback = why;
        }
        if (Db2Layouts.ForBuild(build)?.Get(table) != null) return new TableNaming(ColumnNameSource.Measured, fallback);
        return new TableNaming(ColumnNameSource.None, fallback);
    }
}
