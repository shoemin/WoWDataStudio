using System.Buffers.Binary;
using System.IO;
using System.Linq;
using WoWData.Core.Dbc;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;

namespace WoWData.Tests;

/// <summary>
/// 0.14.0 (experimental) Other versions: finding clients, opening one read-only in its own archive order, "Blizzard
/// archives only", picking a client by what a person types, comparing a row across versions, and the per-build layouts.
/// Built on small archives the test writes itself (a WotLK-style Data folder with a non-Blizzard patch-7.MPQ), so it
/// needs no game copy.
/// </summary>
public class OtherVersionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wds_versions_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A WDBC with 2 fields per row (ID, value).</summary>
    private static byte[] Dbc(params (uint Id, uint Value)[] rows)
    {
        var b = new byte[20 + rows.Length * 8 + 1];
        "WDBC"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1);
        for (int i = 0; i < rows.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20 + i * 8), rows[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24 + i * 8), rows[i].Value);
        }
        return b;
    }

    private void Mpq(string data, string name, params (string Path, byte[] Bytes)[] files)
    {
        var p = Path.Combine(data, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        // the writer refuses stock archive names (patch.MPQ...) on purpose: write under another name, then rename
        MpqWriter.Build(files.ToDictionary(f => f.Path, f => f.Bytes), p + ".tmp.mpq");
        File.Move(p + ".tmp.mpq", p);
    }

    private string MakeWotlk()
    {
        var dir = Path.Combine(_root, "clients", "3.3.5a - test");
        var data = Path.Combine(dir, "Data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(dir, "Wow.exe"), Array.Empty<byte>());
        var tex = new byte[] { 1, 2, 3 };
        Mpq(data, "common.MPQ", (@"World\Generic\a.blp", tex));
        Mpq(data, "common-2.MPQ", (@"World\Generic\b.blp", tex));
        Mpq(data, "expansion.MPQ", (@"World\Maps\Expansion01\Expansion01.wdt", tex));
        Mpq(data, "lichking.MPQ", (@"World\Maps\Northrend\Northrend.wdt", tex));
        Mpq(data, "patch.MPQ", (@"World\Generic\a.blp", new byte[] { 9 }));
        Mpq(data, "patch-7.MPQ", (@"DBFilesClient\Map.dbc", Dbc((0, 777), (1, 778), (900, 1))));
        Mpq(data, @"enUS\locale-enUS.MPQ", (@"DBFilesClient\Map.dbc", Dbc((0, 10), (1, 11))));
        return dir;
    }

    private string MakeCasc()
    {
        var dir = Path.Combine(_root, "clients", "bnet", "_retail_");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Wow.exe"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(_root, "clients", "bnet", "Data", "config"));
        Directory.CreateDirectory(Path.Combine(_root, "clients", "bnet", "Data", "indices"));
        return dir;
    }

    [Fact]
    public void FindsMpqAndCascClients()
    {
        var w = MakeWotlk();
        var c = MakeCasc();
        var found = ClientCopies.Find(Path.Combine(_root, "clients"));
        Assert.Equal(2, found.Count);
        var mpq = found.Single(f => f.Folder == w);
        Assert.Equal(ClientFormat.Mpq, mpq.Format);
        Assert.Equal(ArchiveLayout.WotLK, mpq.Layout);
        Assert.Equal("enUS", mpq.Locale);
        Assert.True(mpq.Readable);
        Assert.Equal(("?", 0), (mpq.Version, mpq.Build));   // an exe without a version resource
        var casc = found.Single(f => f.Folder == c);
        Assert.Equal(ClientFormat.Casc, casc.Format);
        Assert.True(casc.Readable);
        Assert.Equal(Path.Combine(_root, "clients", "bnet", "Data"), casc.DataDir);
        // an empty store (no build info, no data archives) is found but cannot be opened
        Assert.ThrowsAny<Exception>(() => OtherClient.Open(casc));
        // the Data folder and the executable lead to the same client
        Assert.Equal(w, ClientCopies.Inspect(Path.Combine(w, "Data"))!.Folder);
        Assert.Equal(w, ClientCopies.Inspect(Path.Combine(w, "Wow.exe"))!.Folder);
    }

    [Fact]
    public void BlizzardArchivesOnlyLeavesOutTheServersPatch()
    {
        var copy = ClientCopies.Inspect(MakeWotlk())! with { Version = "3.3.5", Build = 12340 };
        using (var all = OtherClient.Open(copy))
        {
            Assert.Equal(new[] { "patch-7.MPQ" }, all.CustomArchives);
            Assert.Equal("patch-7.MPQ", all.Data.Archives[0].Name);
            var map = all.Table("Map")!;
            Assert.True(map.Layout.IsFallback);             // a 2-field Map matches no real layout: raw columns
            Assert.Equal(3, map.Rows.Count);
            Assert.Equal(new byte[] { 9 }, all.Data.Read(@"World\Generic\a.blp"));   // patch.MPQ wins over common.MPQ
        }
        using (var bliz = OtherClient.Open(copy, blizzardOnly: true))
        {
            Assert.True(bliz.BlizzardOnly);
            Assert.DoesNotContain(bliz.Data.Archives, a => a.Name == "patch-7.MPQ");
            Assert.Equal(2, bliz.Table("Map")!.Rows.Count);
            Assert.Contains("Map", bliz.TableNames());
        }
    }

    [Fact]
    public void ComparesARowAcrossVersions()
    {
        var copy = ClientCopies.Inspect(MakeWotlk())! with { Version = "3.3.5", Build = 99999 };
        using var a = OtherClient.Open(copy);
        using var b = OtherClient.Open(copy, blizzardOnly: false);
        var t1 = a.Table("Map");
        var bytes = Dbc((1, 11), (2, 12));
        var t2 = DbcTable.Load(bytes, "Map", DbcLayouts.ForBuild(99998));
        // raw columns are left out unless asked for, with a note
        var plain = VersionCompare.Row("Map", 1, new (string, DbcTable?)[] { ("A", t1), ("B", t2), ("C", null) });
        Assert.Empty(plain.Columns);
        Assert.Contains(plain.Notes, n => n.StartsWith("C: no Map table"));
        var raw = VersionCompare.Row("Map", 1, new (string, DbcTable?)[] { ("A", t1), ("B", t2) }, includeRaw: true);
        Assert.Equal(new[] { "col_0", "col_1" }, raw.Columns.Select(c => c.Column));
        Assert.Equal(new[] { "778", "11" }, raw.Columns[1].Values);
        var missing = VersionCompare.Row("Map", 900, new (string, DbcTable?)[] { ("A", t1), ("B", t2) }, includeRaw: true);
        Assert.Equal(new[] { "1", null }, missing.Columns[1].Values);
        Assert.Contains(missing.Notes, n => n == "B: no row 900.");
    }

    [Fact]
    public void PicksAClientByWhatIsTyped()
    {
        ClientCopy C(string name, string v, int b, ClientFormat f = ClientFormat.Mpq) => new(Path.Combine(_root, name), "", "", v, b, f, ArchiveLayout.Split, null);
        var found = new List<ClientCopy> { C("1.12 A", "1.12.1", 5875), C("1.12 B", "1.12.1", 5875), C("TBC", "2.4.3", 8606), C("Era", "1.15.9", 70003, ClientFormat.Casc) };
        Assert.Equal("TBC", ClientPick.Resolve("2.4", found).Name);
        Assert.Equal("TBC", ClientPick.Resolve("8606", found).Name);
        Assert.Equal("TBC", ClientPick.Resolve("tbc", found).Name);
        Assert.Equal("1.12 A", ClientPick.Resolve("1.12", found).Name);      // two copies of one release: the first by name
        Assert.Equal("Era", ClientPick.Resolve("1.15.9", found).Name);
        Assert.Throws<ArgumentException>(() => ClientPick.Resolve("1", found));   // 1.12.1 and 1.15.9
        Assert.Throws<ArgumentException>(() => ClientPick.Resolve("4.3.4", found));
    }

    [Fact]
    public void GuessesTheTypeOfAFileKnownByNumber()
    {
        static byte[] B(string magic, string? second = null)
        {
            var b = new byte[24];
            System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(b, 0);
            if (second != null) System.Text.Encoding.ASCII.GetBytes(second).CopyTo(b, 12);
            return b;
        }
        Assert.Equal(".blp", ClientFileKinds.GuessExtension(B("BLP2")));
        Assert.Equal(".m2", ClientFileKinds.GuessExtension(B("MD21")));
        Assert.Equal(".db2", ClientFileKinds.GuessExtension(B("WDC5")));
        Assert.Equal(".dbc", ClientFileKinds.GuessExtension(B("WDBC")));
        Assert.Equal(".wdt", ClientFileKinds.GuessExtension(B("REVM", "DHPM")));
        Assert.Equal(".adt", ClientFileKinds.GuessExtension(B("REVM", "RDHM")));
        Assert.Equal(".wmo", ClientFileKinds.GuessExtension(B("REVM", "DHOM")));
        Assert.Equal(".wdl", ClientFileKinds.GuessExtension(B("REVM", "FOAM")));
        Assert.Equal(".bin", ClientFileKinds.GuessExtension(B("XXXX")));
        Assert.Equal(".bin", ClientFileKinds.GuessExtension(new byte[2]));
    }

    /// <summary>The 1.60 beta from the developer's copies (skipped elsewhere): opened through OtherClient, a file by number.</summary>
    [Fact]
    public void OpensAModernClientWhenTheCopyIsPresent()
    {
        var folder = @"G:\WoW Clients and Patches\Clients\BattleNet snapshot 2026-10-20\_classic_beta_";
        if (!Directory.Exists(folder)) return;
        var copy = ClientCopies.Inspect(folder)!;
        Assert.Equal(ClientFormat.Casc, copy.Format);
        Assert.True(copy.Readable);
        using var oc = OtherClient.Open(copy);
        Assert.NotNull(oc.Casc);
        Assert.Null(oc.Data);
        Assert.False(oc.Files.CanList);                   // no listfile
        Assert.Empty(oc.Files.Glob(@"World\Maps\*"));
        Assert.Equal("#775971 local", oc.Files.SourceOf("#775971"));
        var wdt = oc.Files.Read("#775971")!;
        Assert.Equal(".wdt", ClientFileKinds.GuessExtension(wdt));
        Assert.Equal(wdt, oc.Files.Read("775971"));
        Assert.Empty(oc.TableNames());
    }

    [Fact]
    public void LayoutsPerBuild()
    {
        Assert.Same(DbcLayouts.Default, DbcLayouts.ForBuild(5875));
        Assert.Equal(8, DbcLayouts.Default.LocaleCount);
        Assert.Contains(5875, DbcLayouts.KnownBuilds());
        var none = DbcLayouts.ForBuild(99997, "9.9.9");
        Assert.Empty(none.Tables);
        Assert.Equal(16, none.LocaleCount);
        Assert.Equal("9.9.9", none.Version);
        foreach (var b in DbcLayouts.KnownBuilds())
        {
            var l = DbcLayouts.ForBuild(b);
            Assert.Equal(b, l.Build);
            Assert.Equal(b >= 6000 ? 16 : 8, l.LocaleCount);
            Assert.All(l.Tables.SelectMany(t => t.Columns).Where(c => c.Storage == DbcStorage.Loc), c => Assert.Equal(l.LocaleCount + 1, c.Fields));
        }
    }
}
