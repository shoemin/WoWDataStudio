using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WoWData.Core.Dbc;
using WoWData.Core.Mpq;

namespace WoWData.Tests;

public class CryptoTests
{
    [Fact]
    public void KnownTableKeys()
    {
        Assert.Equal(0xC3AF3770u, StormCrypto.HashTableKey);
        Assert.Equal(0xEC83B3A3u, StormCrypto.BlockTableKey);
        Assert.Equal(StormCrypto.HashString("(listfile)", 0), StormCrypto.HashString("(LISTFILE)", 0));
        Assert.Equal(StormCrypto.HashString("a/b", 1), StormCrypto.HashString("A\\B", 1));
    }

    [Fact]
    public void EncryptRoundTrip()
    {
        var data = Enumerable.Range(0, 256 * 3).Select(i => (byte)i).Concat("xyz"u8.ToArray()).ToArray();
        foreach (var key in new uint[] { 0, 1, 0xDEADBEEF, StormCrypto.HashTableKey })
        {
            var d = (byte[])data.Clone();
            StormCrypto.Encrypt(d, key);
            Assert.NotEqual(data, d);
            StormCrypto.Decrypt(d, key);
            Assert.Equal(data, d);
        }
    }

    [Fact]
    public void Crc32MatchesZlib() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
}

/// <summary>The MPQ reader on the real 1.12 archives, compared with the Python reader's results.</summary>
public class MpqReaderTests
{
    private static string O => Oracle.Ensure("mpq");

    private static List<string[]> Json2(string file) => JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(Path.Combine(O, file)))!;

    [Fact]
    public void LookupOrderMatchesClientAndPython()
    {
        var order = ClientData.ClientArchiveOrder(TestEnv.DataDir);
        // PatchFiles_MountArchives: patch-?.MPQ descending, patch.MPQ, then the base list
        Assert.Equal(new[] { "patch-2.MPQ", "patch.MPQ" }, order.Take(2));
        Assert.Equal(ClientData.BaseArchives, order.Skip(2));
        Assert.Equal(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(O, "order.json"))), order);
    }

    [Fact]
    public void FileListMatchesPythonExactly()
    {
        var c = TestEnv.Client;
        var ours = c.Names().OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Value.Path, kv.Value.Archive }).ToList();
        var py = Json2("names.json");
        Assert.Equal(79733, ours.Count);                 // 85,441 listed names minus 5,708 deleted
        Assert.Equal(py.Count, ours.Count);
        for (int i = 0; i < py.Count; i++) Assert.Equal(py[i], ours[i]);
    }

    [Fact]
    public void DeleteMarkersMatchPython()
    {
        var c = TestEnv.Client;
        var ours = c.DeletedNames().OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Value.Path, kv.Value.Archive }).ToList();
        Assert.Equal(5708, ours.Count);
        Assert.Equal(Json2("deleted.json"), ours);
        const string p = @"Creature\OgreMage\OgreMage.m2";
        Assert.Equal(MpqEntryState.Deleted, c.Archive("patch.MPQ")!.EntryState(p));
        Assert.True(c.Archive("model.MPQ")!.HasFile(p));
        Assert.Null(c.Find(p));                           // the client stops at the marker
        Assert.Equal(new[] { SourceKind.Deleted, SourceKind.Hidden }, c.Where(p).Select(s => s.Kind));
    }

    [Fact]
    public void WhereMatchesPython()
    {
        var py = JsonSerializer.Deserialize<Dictionary<string, List<string[]>>>(File.ReadAllText(Path.Combine(O, "where.json")))!;
        foreach (var (path, sources) in py)
        {
            var ours = TestEnv.Client.Where(path).Select(s => new[] { s.Kind.ToString().ToLowerInvariant(), s.Name }).ToList();
            Assert.Equal(sources, ours);
        }
    }

    [Fact]
    public void FileContentsMatchPython()
    {
        var py = JsonSerializer.Deserialize<Dictionary<string, JsonElement[]>>(File.ReadAllText(Path.Combine(O, "hashes.json")))!;
        Assert.True(py.Count > 700);
        foreach (var (path, v) in py)
        {
            var data = TestEnv.Client.Read(path);
            Assert.NotNull(data);
            Assert.Equal(v[0].GetInt64(), data!.LongLength);
            Assert.Equal(v[1].GetString(), Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        }
    }

    [Fact]
    public void BoarFiles()
    {
        var c = TestEnv.Client;
        Assert.Equal("model.MPQ", c.Find(@"Creature\Boar\Boar.mdx")!.Name);     // .mdx -> .m2
        Assert.Equal(266528, c.Read(@"Creature\Boar\Boar.m2")!.Length);
        Assert.Equal("texture.MPQ", c.Find("creature/boar/boarskinivory.blp")!.Name);
        Assert.Equal(44876, c.Read(@"Creature\Boar\BoarSkinIvory.blp")!.Length);
        Assert.Equal(new[] { "patch-2.MPQ", "patch.MPQ", "dbc.MPQ" }, c.Where(@"DBFilesClient\CreatureDisplayInfo.dbc").Select(s => s.Name));
    }

    [Fact]
    public void StormProbeEqualsDictionaryLookup()
    {
        foreach (var arc in new[] { "patch-2.MPQ", "dbc.MPQ", "misc.MPQ" })
        {
            var a = TestEnv.Client.Archive(arc)!;
            foreach (var n in a.Listfile()) Assert.Equal(a.BlockIndex(n), a.StormLookup(n));
        }
    }

    [Fact]
    public void EveryDbcParses()
    {
        foreach (var n in TestEnv.Client.Glob(@"DBFilesClient\*.dbc"))
        {
            var d = TestEnv.Client.Read(n)!;
            Assert.Equal((byte)'W', d[0]);
            _ = new DbcFile(d, n);
        }
        var cdi = DbcTables.CreatureDisplayInfo(new DbcFile(TestEnv.Client.Read(@"DBFilesClient\CreatureDisplayInfo.dbc")!, "CreatureDisplayInfo"));
        var boar = cdi.Single(r => r.Id == 503);
        Assert.Equal("BoarSkinIvory", boar.Skin[0]);
    }
}

/// <summary>The patch-3.MPQ writer: the Python writer's output for the same input, and a read-back by the Python reader.</summary>
public class MpqWriterTests : IDisposable
{
    private readonly string _dir = TestEnv.TempDir("mpqwrite");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static Dictionary<string, byte[]> OracleInput(out string inputDir)
    {
        var o = Oracle.Ensure("mpq");
        inputDir = Path.Combine(o, "writer_input");
        var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(o, "writer_input.json")))!;
        var d = new Dictionary<string, byte[]>();
        foreach (var p in paths) d[p] = File.ReadAllBytes(Path.Combine(inputDir, p.Replace('\\', Path.DirectorySeparatorChar)));
        return d;
    }

    [Fact]
    public void SameStructureAsPythonWriter()
    {
        var files = OracleInput(out _);
        var ours = Path.Combine(_dir, "patch-3.MPQ");
        MpqWriter.Build(files, ours);
        Assert.Empty(MpqWriter.Verify(ours, files));
        var pyPath = Path.Combine(Oracle.Ensure("mpq"), "py_patch-3.MPQ");
        using var a = new MpqArchive(ours);
        using var b = new MpqArchive(pyPath);
        Assert.Equal(b.FormatVersion, a.FormatVersion);
        Assert.Equal(b.HeaderSize, a.HeaderSize);
        Assert.Equal(b.SectorSize, a.SectorSize);
        Assert.Equal(b.HashTable.Length, a.HashTable.Length);
        Assert.Equal(b.BlockTable.Length, a.BlockTable.Length);
        Assert.Equal(b.Listfile(), a.Listfile());
        // the same hash table (bucket, name hashes, block index) and the same file order and flags
        Assert.Equal(b.HashTable, a.HashTable);
        for (int i = 0; i < a.BlockTable.Length; i++)
        {
            Assert.Equal(b.BlockTable[i].FileSize, a.BlockTable[i].FileSize);
            Assert.Equal(b.BlockTable[i].Flags, a.BlockTable[i].Flags);
        }
        foreach (var n in a.Listfile().Append("(listfile)").Append("(attributes)")) Assert.Equal(b.Read(n), a.Read(n));
        // .NET 8's zlib and CPython's zlib (1.3.x) produce the same level-9 deflate stream, so the archive is
        // byte-identical to the Python writer's. Should a runtime ever ship a different deflate, this fails on the zlib
        // check first, which says why.
        var dbc = files[@"DBFilesClient\CreatureDisplayInfo.dbc"].AsSpan(0, 4096).ToArray();
        Assert.Equal(PyZlib(dbc), MpqWriter.ZlibCompress(dbc));
        Assert.Equal(File.ReadAllBytes(pyPath), File.ReadAllBytes(ours));
    }

    /// <summary>Python's zlib.compress(data, 9) of a buffer.</summary>
    private byte[] PyZlib(byte[] data)
    {
        var inp = Path.Combine(_dir, "z.in");
        var outp = Path.Combine(_dir, "z.out");
        File.WriteAllBytes(inp, data);
        Oracle.RunPython("-c", "import sys,zlib;open(sys.argv[2],'wb').write(zlib.compress(open(sys.argv[1],'rb').read(),9))", inp, outp);
        return File.ReadAllBytes(outp);
    }

    [Fact]
    public void PythonReaderReadsOurArchive()
    {
        var files = OracleInput(out var inputDir);
        var ours = Path.Combine(_dir, "patch-3.MPQ");
        MpqWriter.Build(files, ours);
        var json = Oracle.RunPython(Path.Combine(Oracle.AppDir, "WoWData.Tests", "oracle", "make_mpq.py"), "--verify", ours, inputDir);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("problems").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("info").GetProperty("format_version").GetInt32());
        Assert.Equal(files.Count, doc.RootElement.GetProperty("info").GetProperty("listfile").GetArrayLength());
    }

    [Fact]
    public void EdgeCases()
    {
        var rnd = new Random(5);
        var randomBytes = new byte[3 * 4096 + 17];
        rnd.NextBytes(randomBytes);
        var files = new Dictionary<string, byte[]>
        {
            ["empty.txt"] = Array.Empty<byte>(),
            [@"Interface\tiny.txt"] = "abc"u8.ToArray(),
            ["random.bin"] = randomBytes,
            ["zeros.bin"] = new byte[50000],
            ["forward/slash.txt"] = "slashes become backslashes"u8.ToArray(),
        };
        var outp = Path.Combine(_dir, "patch-3.MPQ");
        var s = MpqWriter.Build(files, outp);
        Assert.Equal(5, s.Files);
        var norm = files.ToDictionary(k => k.Key.Replace('/', '\\'), k => k.Value);
        Assert.Empty(MpqWriter.Verify(outp, norm));
        using var a = new MpqArchive(outp);
        Assert.Equal(MpqArchive.FlagExists, a.BlockTable[a.BlockIndex("random.bin")].Flags);
        Assert.Equal(MpqArchive.FlagExists | MpqArchive.FlagCompress, a.BlockTable[a.BlockIndex("zeros.bin")].Flags);
        Assert.Equal(norm.Keys.OrderBy(k => k), a.Listfile().OrderBy(k => k));
    }

    [Fact]
    public void Refuses()
    {
        Assert.Throws<MpqWriteException>(() => MpqWriter.Build(new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 } }, Path.Combine(_dir, "patch-2.MPQ")));
        Assert.Throws<MpqWriteException>(() => MpqWriter.Build(new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 }, ["A"] = new byte[] { 2 } }, Path.Combine(_dir, "patch-5.MPQ")));
        var outp = Path.Combine(_dir, "patch-6.MPQ");
        MpqWriter.Build(new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 } }, outp);
        Assert.Throws<MpqWriteException>(() => MpqWriter.Build(new Dictionary<string, byte[]> { ["a"] = new byte[] { 1 } }, outp));
    }
}
