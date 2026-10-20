using System.IO;
using System.Linq;
using WoWData.Core.Mpq;

namespace WoWData.Tests;

/// <summary>
/// The archive mount order of the TBC 2.4.3 and WotLK 3.3.5a layouts (read from each Wow.exe, see
/// docs\research\MPQ_ERA_DELTA.md section 1), on Data folders of empty files: only names matter for the order.
/// The expected lists are the ones measured on the installs (locale enGB for TBC, enUS for WotLK, with WotLK's
/// non-Blizzard patch-7.mpq), plus the sorting quirks of the full-path comparison.
/// </summary>
public class ArchiveOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wds_order_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeData(string name, params string[] files)
    {
        var data = Path.Combine(_root, name, "Data");
        foreach (var f in files)
        {
            var p = Path.Combine(data, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Array.Empty<byte>());
        }
        return data;
    }

    [Fact]
    public void Tbc243()
    {
        var d = MakeData("tbc", "common.MPQ", "expansion.MPQ", "patch.MPQ", "patch-2.MPQ",
            @"enGB\locale-enGB.MPQ", @"enGB\speech-enGB.MPQ", @"enGB\expansion-locale-enGB.MPQ", @"enGB\expansion-speech-enGB.MPQ",
            @"enGB\patch-enGB.MPQ", @"enGB\patch-enGB-2.MPQ", @"enGB\base-enGB.MPQ", @"enGB\backup-enGB.MPQ");
        Assert.Equal(ArchiveLayout.TBC, ClientData.DetectLayout(d));
        Assert.Equal("enGB", ClientData.DetectLocale(d));
        Assert.Equal(new[]
        {
            "patch-2.MPQ", @"enGB\patch-enGB-2.MPQ", @"enGB\patch-enGB.MPQ", "patch.MPQ", "expansion.MPQ", "common.MPQ",
            @"enGB\locale-enGB.MPQ", @"enGB\speech-enGB.MPQ", @"enGB\expansion-locale-enGB.MPQ", @"enGB\expansion-speech-enGB.MPQ",
        }, ClientData.ClientArchiveOrder(d));
    }

    [Fact]
    public void Wotlk335a()
    {
        var d = MakeData("wotlk", "common.MPQ", "common-2.MPQ", "expansion.MPQ", "lichking.MPQ", "patch.MPQ", "patch-2.MPQ",
            "patch-3.MPQ", "patch-7.mpq",
            @"enUS\locale-enUS.MPQ", @"enUS\speech-enUS.MPQ", @"enUS\expansion-locale-enUS.MPQ", @"enUS\lichking-locale-enUS.MPQ",
            @"enUS\expansion-speech-enUS.MPQ", @"enUS\lichking-speech-enUS.MPQ", @"enUS\patch-enUS.MPQ", @"enUS\patch-enUS-2.MPQ",
            @"enUS\patch-enUS-3.MPQ", @"enUS\base-enUS.MPQ", @"enUS\backup-enUS.MPQ");
        Assert.Equal(ArchiveLayout.WotLK, ClientData.DetectLayout(d));
        Assert.Equal(new[]
        {
            "patch-7.mpq", "patch-3.MPQ", "patch-2.MPQ", @"enUS\patch-enUS-3.MPQ", @"enUS\patch-enUS-2.MPQ", "patch.MPQ",
            @"enUS\patch-enUS.MPQ", "expansion.MPQ", "lichking.MPQ", "common.MPQ", "common-2.MPQ",
            @"enUS\locale-enUS.MPQ", @"enUS\speech-enUS.MPQ", @"enUS\expansion-locale-enUS.MPQ", @"enUS\lichking-locale-enUS.MPQ",
            @"enUS\expansion-speech-enUS.MPQ", @"enUS\lichking-speech-enUS.MPQ",
        }, ClientData.ClientArchiveOrder(d));
    }

    [Fact]
    public void FullPathSortQuirks()
    {
        // one character only (patch-10 is ignored); letters beat digits; a locale that sorts above "patch" (ruRU) puts
        // its numbered patches above the global ones
        var d = MakeData("ru", "common.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-A.MPQ", "patch-10.MPQ",
            @"ruRU\locale-ruRU.MPQ", @"ruRU\patch-ruRU-2.MPQ");
        Assert.Equal(new[] { @"ruRU\patch-ruRU-2.MPQ", "patch-A.MPQ", "patch-2.MPQ", @"ruRU\patch-ruRU.MPQ", "patch.MPQ", "common.MPQ", @"ruRU\locale-ruRU.MPQ" }
            .Where(n => File.Exists(Path.Combine(d, n))), ClientData.ClientArchiveOrder(d));
    }

    [Fact]
    public void SplitLayoutUnchanged()
    {
        var d = MakeData("classic", "patch.MPQ", "patch-2.MPQ", "model.MPQ", "dbc.MPQ", "base.MPQ");
        Assert.Equal(ArchiveLayout.Split, ClientData.DetectLayout(d));
        Assert.Equal(new[] { "patch-2.MPQ", "patch.MPQ", "model.MPQ", "dbc.MPQ" }, ClientData.ClientArchiveOrder(d));
    }
}
