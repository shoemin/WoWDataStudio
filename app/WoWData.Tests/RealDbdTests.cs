using System.IO;
using System.Linq;
using WoWData.Core.Db2;
using WoWData.Core.Versions;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>A test on a real client copy and the user's own WoWDBDefs folder (opt-in: WOWDATA_CLIENT_COPIES and
/// WOWDATA_DBD_DIR, the folder of .dbd files or a copy of the repository); skipped when either is not set. Both are only
/// read; nothing of them is copied anywhere.</summary>
public sealed class DbdCopyFactAttribute : FactAttribute
{
    public static string? Dbd => Environment.GetEnvironmentVariable("WOWDATA_DBD_DIR") is { Length: > 0 } d ? d : null;

    public DbdCopyFactAttribute(string relative)
    {
        var p = ClientCopyFactAttribute.PathOf(relative);
        if (p == null) Skip = "set WOWDATA_CLIENT_COPIES to the folder of client copies to run this test";
        else if (!Directory.Exists(p)) Skip = $"needs the client copy at {p}";
        else if (DbdFolder.Resolve(Dbd) == null) Skip = "set WOWDATA_DBD_DIR to a folder of WoWDBDefs definitions to run this test";
    }
}

/// <summary>
/// M5 on the real "1.60" Classic beta copy (1.60.1.70450) with the user's WoWDBDefs folder (read only, opt-in): every
/// table of the build has a definition with the executable's layout hash, every table read is named by it except the
/// ones whose definition does not fit (said with the reason), and the disagreement report against our measured layout
/// file (printed for a human to look at; it never changes names). Numbers MEASURED on the copy of 2026-10-20 and the
/// WoWDBDefs copy next to it (2026-10-28).
/// </summary>
[Collection(PerformanceCollection.Name)]
public class RealDbdTests
{
    private readonly ITestOutputHelper _out;
    public RealDbdTests(ITestOutputHelper output) => _out = output;

    [DbdCopyFact(RealCopyOpenTests.Beta)]
    public void EveryTableOf70450IsNamedByWoWDBDefsOrSaysWhyNot()
    {
        var dbd = DbdFolder.Open(DbdCopyFactAttribute.Dbd)!;
        // our parser reads every definition file of the folder
        var unreadable = dbd.Tables.Where(t => dbd.Get(t, out _) == null).ToList();
        Assert.True(unreadable.Count == 0, "not read: " + string.Join(", ", unreadable.Take(10).Select(t => { dbd.Get(t, out var e); return e; })));

        var copy = ClientCopies.Inspect(ClientCopyFactAttribute.PathOf(RealCopyOpenTests.Beta)!)!;
        using var oc = OtherClient.Open(copy);
        oc.Dbd = dbd;
        Assert.Equal("1.60.1.70450", oc.VersionOf());
        // the 1,161 tables of the executable: each has a definition for the build with the executable's layout hash
        var names = oc.TableNames();
        Assert.Equal(1161, names.Count);
        var notMatched = names.Where(n => !dbd.Knows(n, "1.60.1.70450", oc.Catalog.Get(n)!.LayoutHash, out _)).ToList();
        Assert.True(notMatched.Count == 0, "no definition with the layout hash: " + string.Join(", ", notMatched.Take(20)));
        Assert.All(names, n => Assert.Equal(ColumnNameSource.WoWDBDefs, oc.ExpectedNaming(n).Source));

        // read every table: named by WoWDBDefs, or our measured names / col_N with the reason
        var sources = new Dictionary<ColumnNameSource, int>();
        var fallbacks = new List<string>();
        foreach (var n in names)
        {
            var t = oc.TableView(n);
            if (t == null) continue;
            sources[t.Naming.Source] = sources.GetValueOrDefault(t.Naming.Source) + 1;
            if (t.Naming.Source != ColumnNameSource.WoWDBDefs)
            {
                Assert.NotEqual("", t.Naming.Fallback);
                fallbacks.Add($"{n}: {t.Naming.Long}");
            }
        }
        foreach (var f in fallbacks) _out.WriteLine(f);
        Assert.True(sources.GetValueOrDefault(ColumnNameSource.WoWDBDefs) >= 1160, string.Join(", ", sources.Select(kv => $"{kv.Key} {kv.Value}")));
        // MEASURED: the one table whose definition does not fit is ExteriorComponentGroup (no rows; the record has one
        // column more than the definition)
        Assert.All(fallbacks, f => Assert.StartsWith("ExteriorComponentGroup: raw (col_N); not WoWDBDefs: ExteriorComponentGroup.dbd's definition for 1.60.1.70450 has 4 columns in the record, the file 5", f));

        // a table both name: the same Map rows, columns by WoWDBDefs' names (ours are kept for the features)
        var map = oc.TableView("Map")!;
        Assert.Equal(ColumnNameSource.WoWDBDefs, map.Naming.Source);
        Assert.Equal(81 - 8, map.RowCount);   // 8 encrypted rows left out, as with our names
        Assert.Equal(VersionTableExport.Column(map, "MapName_lang"), VersionTableExport.Column(map, "MapName"));
    }

    [DbdCopyFact(RealCopyOpenTests.Beta)]
    public void TheDisagreementReportAgainstOurMeasuredLayoutFile()
    {
        var dbd = DbdFolder.Open(DbdCopyFactAttribute.Dbd)!;
        var ours = Db2Layouts.ForBuild(70450)!;
        var report = DbdDisagreement.Report(ours, dbd, "1.60.1.70450");
        foreach (var l in report) _out.WriteLine($"{l.Table} {l.Kind}: {l.Text}");
        // MEASURED: every table of our file has a WoWDBDefs definition for the build with the same layout hash and the
        // same column count; only names differ (spelling, or a different reading of a column: for a human to look at)
        Assert.DoesNotContain(report, l => l.Kind is DbdDisagreement.Kinds.NoDefinition or DbdDisagreement.Kinds.LayoutHash or DbdDisagreement.Kinds.ColumnCount);
        Assert.Equal(ours.Tables.Count, ours.Tables.Count(t => dbd.Has(t.Name)));
    }
}
