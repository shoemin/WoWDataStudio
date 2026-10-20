using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Casc;

namespace WoWData.Tests;

/// <summary>
/// The read-only CASC reader (WoWData.Core.Casc): BLTE, the Jenkins name hash, .idx v7, .build.info, and a whole
/// synthetic store the test writes itself (build config, encoding, MFST v2 root, idx, data.000), so none of this needs
/// a game copy. <see cref="CascRealStoreTests"/> checks the real WoD 6.2.3 and Battle.net copies when they exist.
/// </summary>
public class CascTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wds_casc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ BLTE

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>An encoded chunk: mode byte + payload.</summary>
    private static byte[] Chunk(char mode, byte[] payload) => new[] { (byte)mode }.Concat(payload).ToArray();

    /// <summary>A BLTE frame with a chunk table (decoded sizes given per chunk).</summary>
    internal static byte[] Frame(params (byte[] Encoded, int DecodedSize)[] chunks)
    {
        int headerSize = 12 + 24 * chunks.Length;
        var ms = new MemoryStream();
        var h = new byte[headerSize];
        "BLTE"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)headerSize);
        h[8] = 0x0F;
        h[9] = (byte)(chunks.Length >> 16);
        h[10] = (byte)(chunks.Length >> 8);
        h[11] = (byte)chunks.Length;
        for (int i = 0; i < chunks.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12 + 24 * i), (uint)chunks[i].Encoded.Length);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16 + 24 * i), (uint)chunks[i].DecodedSize);
            MD5.HashData(chunks[i].Encoded).CopyTo(h.AsSpan(20 + 24 * i));
        }
        ms.Write(h);
        foreach (var c in chunks) ms.Write(c.Encoded);
        return ms.ToArray();
    }

    /// <summary>A BLTE frame without a chunk table (header size 0).</summary>
    private static byte[] SingleFrame(byte[] encodedChunk)
    {
        var b = new byte[8 + encodedChunk.Length];
        "BLTE"u8.CopyTo(b);
        encodedChunk.CopyTo(b, 8);
        return b;
    }

    private static byte[] Bytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        for (int i = 0; i < n; i += 3) b[i] = (byte)'a';  // compressible
        return b;
    }

    [Fact]
    public void BlteSingleChunkNAndZ()
    {
        var data = Bytes(1000, 1);
        Assert.Equal(data, Blte.Decode(SingleFrame(Chunk('N', data))));
        Assert.Equal(data, Blte.Decode(SingleFrame(Chunk('Z', Zlib(data)))));
    }

    [Fact]
    public void BlteMultiChunkNAndZWithChecksums()
    {
        var a = Bytes(700, 2);
        var b = Bytes(5000, 3);
        var c = Bytes(1, 4);
        var frame = Frame((Chunk('N', a), a.Length), (Chunk('Z', Zlib(b)), b.Length), (Chunk('N', c), c.Length));
        Assert.Equal(a.Concat(b).Concat(c).ToArray(), Blte.Decode(frame));
        Assert.Equal(new[] { 'N', 'Z', 'N' }, Blte.Chunks(frame).Select(x => x.Mode));

        // one flipped bit in the second chunk's data: its MD5 no longer matches
        var bad = (byte[])frame.Clone();
        bad[12 + 24 * 3 + 1 + a.Length + 5] ^= 0x01;
        var e = Assert.Throws<InvalidDataException>(() => Blte.Decode(bad));
        Assert.Contains("checksum", e.Message);

        // a decoded size that does not match the chunk
        var wrong = Frame((Chunk('N', a), a.Length + 1));
        Assert.Throws<InvalidDataException>(() => Blte.Decode(wrong));
        var wrongZ = Frame((Chunk('Z', Zlib(b)), b.Length - 1));
        Assert.Throws<InvalidDataException>(() => Blte.Decode(wrongZ));
    }

    [Fact]
    public void BlteNestedAndEncrypted()
    {
        var a = Bytes(300, 5);
        var inner = SingleFrame(Chunk('Z', Zlib(a)));
        Assert.Equal(a, Blte.Decode(Frame((Chunk('F', inner), a.Length))));

        // E chunk: key-name length 8, key name, IV length 4, IV, type 'S', payload
        var keyName = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var enc = Chunk('E', new byte[] { 8 }.Concat(keyName).Concat(new byte[] { 4, 9, 9, 9, 9, (byte)'S' }).Concat(Bytes(50, 6)).ToArray());
        var frame = Frame((Chunk('N', a), a.Length), (enc, 64));
        var e = Assert.Throws<CascEncryptedException>(() => Blte.Decode(frame));
        Assert.Equal("0807060504030201", e.KeyName);
        var filled = Blte.Decode(frame, true, out long encBytes);
        Assert.Equal(64, encBytes);
        Assert.Equal(a.Concat(new byte[64]).ToArray(), filled);
        // without a chunk table the size is unknown: even zero-filling throws
        Assert.Throws<CascEncryptedException>(() => Blte.Decode(SingleFrame(enc), true, out _));
    }

    // ------------------------------------------------------------------ name hash

    [Theory]
    // expected values computed by the Python probe (casc.py name_hash) on 2026-10-20
    [InlineData(@"World\Maps\Azeroth\Azeroth.wdt", 0x19A3C4CBC40E9FA2UL)]
    [InlineData(@"World\Maps\Kalimdor\Kalimdor.wdt", 0x36F1C33FF9E421B7UL)]
    [InlineData(@"DBFilesClient\Map.dbc", 0x1D86FE23B6D93EB0UL)]
    [InlineData(@"Interface\Icons\INV_Misc_QuestionMark.blp", 0x9EB59E3C76124837UL)]
    [InlineData("dbfilesclient/map.db2", 0x76A46B277AE9D377UL)]
    [InlineData("", 0xDEADBEEFDEADBEEFUL)]
    [InlineData("a", 0x01014BA110786E8CUL)]
    [InlineData("123456789012", 0xB578F5B027F9A1C5UL)]
    [InlineData("1234567890123", 0xDE86D9F5FF79C7AFUL)]
    public void JenkinsNameHash(string name, ulong expected)
    {
        Assert.Equal(expected, CascNameHash.Of(name));
        Assert.Equal(expected, CascNameHash.Of(name.ToLowerInvariant().Replace('\\', '/')));
    }

    [Fact]
    public void Lookup3PublishedVector()
    {
        // the published lookup3 driver value for hashlittle2("Four score and seven years ago", 0, 0)
        var (c, b) = CascNameHash.HashLittle2(Encoding.ASCII.GetBytes("Four score and seven years ago"));
        Assert.Equal(0x17770551u, c);
        Assert.Equal(0xCE7226E6u, b);
    }

    // ------------------------------------------------------------------ .idx v7

    internal static byte[] Idx(int bucket, IEnumerable<(byte[] Key, int Archive, long Offset, uint Size)> entries)
    {
        var list = entries.Select(e => (Key: e.Key.Take(9).ToArray(), e.Archive, e.Offset, e.Size)).OrderBy(e => Convert.ToHexString(e.Key), StringComparer.Ordinal).ToList();
        var b = new byte[0x28 + 18 * list.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x10);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 7);
        b[10] = (byte)bucket;
        b[12] = 4; b[13] = 5; b[14] = 9; b[15] = 30;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 0x4000000000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x20), (uint)(18 * list.Count));
        for (int i = 0; i < list.Count; i++)
        {
            var o = 0x28 + 18 * i;
            list[i].Key.CopyTo(b, o);
            ulong raw = ((ulong)list[i].Archive << 30) | (ulong)list[i].Offset;
            for (int k = 0; k < 5; k++) b[o + 9 + k] = (byte)(raw >> (8 * (4 - k)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 14), list[i].Size);
        }
        return b;
    }

    [Fact]
    public void IdxV7Parse()
    {
        var k1 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var k2 = Convert.FromHexString("FF112233445566778800000000000000");
        var k3 = Convert.FromHexString("0011223344556677FF");
        var idx = CascLocalIndex.Parse(Idx(0, new[] { (k2, 214, 0x3FFFFFFFL, 77u), (k1, 2, 0x1234L, 4096u), (k3, 0, 0L, 30u) }));
        Assert.Equal(3, idx.Count);
        Assert.True(idx.TryGet(k1, out var e1));
        Assert.Equal(new CascIndexEntry(2, 0x1234, 4096), e1);
        Assert.True(idx.TryGet(k2, out var e2));
        Assert.Equal(new CascIndexEntry(214, 0x3FFFFFFF, 77), e2);
        // only the first 9 bytes matter
        Assert.True(idx.TryGet(Convert.FromHexString("00112233445566778899000000000000"), out _));
        Assert.True(idx.TryGet(k3, out var e3));
        Assert.Equal(0, e3.Archive);
        Assert.False(idx.TryGet(Convert.FromHexString("001122334455667799AABBCCDDEEFF00"), out _));
        Assert.Equal(1, CascLocalIndex.BucketOf(Convert.FromHexString("010000000000000000")));
        Assert.Equal(3, CascLocalIndex.BucketOf(Convert.FromHexString("100000000000000002")));
    }

    // ------------------------------------------------------------------ text files

    [Fact]
    public void BuildInfoAndConfigParse()
    {
        var t = CascTable.Parse("Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|Version!STRING:0|Product!STRING:0\r\n"
            + "us|1|0123456789abcdef0123456789abcdef|1.60.1.70450|wow_classic_beta\r\n"
            + "## seqn = 5\r\n"
            + "eu|0|fedcba9876543210fedcba9876543210|12.1.5.70448|wow\r\n");
        Assert.Equal(new[] { "Branch", "Active", "Build Key", "Version", "Product" }, t.Columns);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("wow_classic_beta", t.Rows[0]["Product"]);
        Assert.Equal("0", t.Rows[1]["active"]);
        Assert.Equal("12.1.5.70448", t.Rows[1]["Version"]);

        var v = CascTable.Parse("Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|BuildId!DEC:4|VersionsName!String:0\nus|00647afeb4fe9bfc6b961a0a532a4baa|325095a916ea88d9fc0c522be7260551|20779|6.2.3.20779\n");
        Assert.Equal("20779", v.Rows[0]["BuildId"]);

        var c = CascConfig.Parse("# Build Configuration\n\nroot = 8a0ca94be2f6ace93991c04ca6e19b57\nencoding = c441a5427cb86b23bbfb6ed9c6cf0114 9a0263e06fdace8ab940a4a45a8e28fb\nbuild-name = WOW-70450patch1.60.1_ForeverBeta\n");
        Assert.Equal("8a0ca94be2f6ace93991c04ca6e19b57", c["root"]);
        Assert.Equal(new[] { "c441a5427cb86b23bbfb6ed9c6cf0114", "9a0263e06fdace8ab940a4a45a8e28fb" }, c.Parts("encoding"));
        Assert.Null(c["missing"]);
        Assert.Equal(Path.Combine("D", "config", "8a", "0c", "8a0ca94be2f6ace93991c04ca6e19b57"), CascConfig.PathOf("D", "8A0CA94BE2F6ACE93991C04CA6E19B57"));
    }

    // ------------------------------------------------------------------ a whole synthetic store

    private sealed record FileSpec(byte[] Content, bool Local, bool Encrypted = false);

    private const string WdtName = @"World\Maps\Test\Test.wdt";
    private const string PlainName = @"World\Maps\Test\Test_32_32.adt";

    /// <summary>
    /// Writes a store: <c>Data\config</c>, <c>Data\data</c> (idx + data.000), and <c>.build.info</c> (two products) or a
    /// WoD-style <c>versions</c> file, plus a flavor folder. Root (MFST v2) blocks:
    /// A all locales: 100 wdt, 101 plain, 103 encrypted, 104 not local (all named);
    /// B deDE: 100 German variant, 105 German only;
    /// C all locales, low violence: 100 LV variant;
    /// D all locales, no names, flag 0x1 (high-res): 101 high-res variant.
    /// </summary>
    private (string Install, Dictionary<string, byte[]> Contents) MakeStore(bool versionsFile)
    {
        var install = Path.Combine(_root, versionsFile ? "wod" : "bnet");
        var data = Path.Combine(install, "Data");
        Directory.CreateDirectory(Path.Combine(data, "data"));
        Directory.CreateDirectory(Path.Combine(data, "indices"));
        var c = new Dictionary<string, byte[]>
        {
            ["wdt"] = Encoding.ASCII.GetBytes("REVM\x04\0\0\0\x12\0\0\0").Concat(Bytes(200, 10)).ToArray(),
            ["plain"] = Bytes(3000, 11),
            ["enc"] = Bytes(100, 12),
            ["missing"] = Bytes(100, 13),
            ["de"] = Bytes(150, 14),
            ["deonly"] = Bytes(160, 15),
            ["lv"] = Bytes(170, 16),
            ["hi"] = Bytes(9000, 17),
        };
        var specs = new List<(string Name, FileSpec Spec)>
        {
            ("wdt", new(c["wdt"], true)), ("plain", new(c["plain"], true)), ("enc", new(c["enc"], true, true)),
            ("missing", new(c["missing"], false)), ("de", new(c["de"], true)), ("deonly", new(c["deonly"], true)),
            ("lv", new(c["lv"], true)), ("hi", new(c["hi"], true)),
        };

        // root
        var root = new MemoryStream();
        void W32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); root.Write(b); }
        void Block(uint locale, uint flags, bool names, params (uint Fdid, string File, string Name)[] recs)
        {
            W32((uint)recs.Length);
            W32(locale);
            W32(flags);  // the reader combines the three content-flag fields as f1 | f2 | f3 << 17
            W32(0);
            root.WriteByte(0);
            long prev = -1;
            foreach (var r in recs) { W32((uint)(r.Fdid - prev - 1)); prev = r.Fdid; }
            foreach (var r in recs) root.Write(MD5.HashData(c[r.File]));
            if (names)
                foreach (var r in recs)
                {
                    var b = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(b, CascNameHash.Of(r.Name));
                    root.Write(b);
                }
        }
        root.Write("TSFM"u8);
        W32(24); W32(2); W32(8); W32(7); W32(0);  // header size, version, total, named, padding to the header size
        Block(0x1F3F6, 0, true, (100, "wdt", WdtName), (101, "plain", PlainName), (103, "enc", "Test\\enc.bin"), (104, "missing", "Test\\missing.bin"));
        Block(0x20, 0, true, (100, "de", WdtName), (105, "deonly", "Test\\deonly.bin"));
        Block(0x1F3F6, 0x80, true, (100, "lv", WdtName));
        Block(0x1F3F6, 0x10000001, false, (101, "hi", ""));
        var rootBytes = root.ToArray();
        specs.Add(("root", new(rootBytes, true)));

        // encode every file: BLTE (Z for big ones, N otherwise; E for the encrypted one), ekey = MD5 of the frame
        var frames = new Dictionary<string, (byte[] CKey, byte[] EKey, byte[] Frame, FileSpec Spec)>();
        foreach (var (name, spec) in specs)
        {
            byte[] frame = spec.Encrypted
                ? Frame((Chunk('E', new byte[] { 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0, 0, 0, 0, (byte)'S' }.Concat(spec.Content).ToArray()), spec.Content.Length))
                : spec.Content.Length > 1000 ? Frame((Chunk('Z', Zlib(spec.Content[..1000])), 1000), (Chunk('N', spec.Content[1000..]), spec.Content.Length - 1000))
                : SingleFrame(Chunk('N', spec.Content));
            frames[name] = (MD5.HashData(spec.Content), MD5.HashData(frame), frame, spec);
        }

        // encoding file
        string[] especs = { "z", "b:{*=e:{0102030405060708,00000000,z}}" };
        var specBlock = Encoding.ASCII.GetBytes(string.Join("\0", especs) + "\0");
        var ce = frames.Values.OrderBy(f => Convert.ToHexString(f.CKey), StringComparer.Ordinal).ToList();
        var es = frames.Values.OrderBy(f => Convert.ToHexString(f.EKey), StringComparer.Ordinal).ToList();
        var enc = new MemoryStream();
        var hdr = new byte[22];
        hdr[0] = (byte)'E'; hdr[1] = (byte)'N'; hdr[2] = 1; hdr[3] = 16; hdr[4] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(5), 1);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(9), 1);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(18), (uint)specBlock.Length);
        enc.Write(hdr);
        enc.Write(specBlock);
        enc.Write(ce[0].CKey); enc.Write(new byte[16]);
        var page = new byte[1024];
        int o = 0;
        foreach (var f in ce)
        {
            page[o] = 1;
            long size = f.Spec.Content.Length;
            for (int k = 0; k < 5; k++) page[o + 1 + k] = (byte)(size >> (8 * (4 - k)));
            f.CKey.CopyTo(page, o + 6);
            f.EKey.CopyTo(page, o + 22);
            o += 38;
        }
        enc.Write(page);
        enc.Write(es[0].EKey); enc.Write(new byte[16]);
        page = new byte[1024];
        o = 0;
        foreach (var f in es)
        {
            f.EKey.CopyTo(page, o);
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(o + 16), f.Spec.Encrypted ? 1u : 0u);
            page[o + 24] = (byte)f.Frame.Length;
            o += 25;
        }
        enc.Write(page);
        var encBytes = enc.ToArray();
        var encFrame = Frame((Chunk('Z', Zlib(encBytes)), encBytes.Length));
        var encEKey = MD5.HashData(encFrame);

        // data.000 + idx
        var archive = new MemoryStream();
        var entries = new List<(byte[] Key, int Archive, long Offset, uint Size)>();
        void Store(byte[] ekey, byte[] frame)
        {
            var h = new byte[30];
            for (int i = 0; i < 16; i++) h[i] = ekey[15 - i];
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)(30 + frame.Length));
            entries.Add((ekey, 0, archive.Position, (uint)(30 + frame.Length)));
            archive.Write(h);
            archive.Write(frame);
        }
        Store(encEKey, encFrame);
        foreach (var f in frames.Values.Where(f => f.Spec.Local)) Store(f.EKey, f.Frame);
        File.WriteAllBytes(Path.Combine(data, "data", "data.000"), archive.ToArray());
        foreach (var g in entries.GroupBy(e => CascLocalIndex.BucketOf(e.Key)))
            File.WriteAllBytes(Path.Combine(data, "data", $"{g.Key:x2}00000002.idx"), Idx(g.Key, g));
        // an older generation of the wdt's bucket that points at the wrong place: must be ignored
        var wdtKey = frames["wdt"].EKey;
        int wb = CascLocalIndex.BucketOf(wdtKey);
        File.WriteAllBytes(Path.Combine(data, "data", $"{wb:x2}00000001.idx"), Idx(wb, new[] { (wdtKey, 0, 0L, 50u) }));

        // build config
        var rootCKey = Convert.ToHexString(MD5.HashData(rootBytes)).ToLowerInvariant();
        var bc = $"# Build Configuration\n\nroot = {rootCKey}\nencoding = {Convert.ToHexString(MD5.HashData(encBytes)).ToLowerInvariant()} {Convert.ToHexString(encEKey).ToLowerInvariant()}\nbuild-name = WOW-12345patch9.8.7_Test\nbuild-uid = wow_test\n";
        var bcHash = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(bc))).ToLowerInvariant();
        var bcPath = CascConfig.PathOf(data, bcHash);
        Directory.CreateDirectory(Path.GetDirectoryName(bcPath)!);
        File.WriteAllText(bcPath, bc);

        if (versionsFile)
        {
            File.WriteAllText(Path.Combine(install, "versions"),
                $"Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|BuildId!DEC:4|VersionsName!String:0\nus|{bcHash}|00000000000000000000000000000000|12345|9.8.7.12345\n");
        }
        else
        {
            File.WriteAllText(Path.Combine(install, ".build.info"),
                "Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|CDN Key!HEX:16|Version!STRING:0|Product!STRING:0\n"
                + $"us|1|{bcHash}|00000000000000000000000000000000|9.8.7.11111|wow\n"
                + $"us|1|{bcHash}|00000000000000000000000000000000|1.60.1.12345|wow_classic_beta\n");
            Directory.CreateDirectory(Path.Combine(install, "_classic_beta_"));
            File.WriteAllText(Path.Combine(install, "_classic_beta_", ".flavor.info"), "Product Flavor!STRING:0\nwow_classic_beta\n");
            Directory.CreateDirectory(Path.Combine(install, "_retail_"));  // no .flavor.info: product from the folder name
        }
        return (install, c);
    }

    [Fact]
    public void SyntheticStoreEndToEnd()
    {
        var (install, c) = MakeStore(false);
        using var st = CascStorage.Open(Path.Combine(install, "_classic_beta_"));
        Assert.Equal("wow_classic_beta", st.Product);
        Assert.Equal("1.60.1.12345", st.Version);
        Assert.Equal(12345, st.Build);
        Assert.Equal("WOW-12345patch9.8.7_Test", st.BuildName);
        Assert.Equal("MFST v2", st.Root.Format);
        Assert.Equal(8, st.Root.RecordCount);
        Assert.True(st.HasNames);

        // FileDataID 100: the all-locale record, not the German one (B) or the low-violence one (C)
        Assert.Equal(c["wdt"], st.ReadFileDataId(100));
        // 101: the standard record, not the high-res variant (D)
        Assert.Equal(c["plain"], st.ReadFileDataId(101));
        Assert.Null(st.ReadFileDataId(102));
        Assert.Equal(CascFileState.Unknown, st.State(102));

        Assert.Equal(CascFileState.Local, st.State(100));
        Assert.Equal(CascFileState.Encrypted, st.State(103));
        var ee = Assert.Throws<CascEncryptedException>(() => st.ReadFileDataId(103));
        Assert.Equal("0807060504030201", ee.KeyName);
        Assert.Throws<CascEncryptedException>(() => st.ReadFileDataId(103, zeroFillEncrypted: true));  // every chunk encrypted
        Assert.Equal(CascFileState.NotLocal, st.State(104));
        Assert.Throws<CascMissingException>(() => st.ReadFileDataId(104));
        Assert.Equal(c["missing"].Length, st.SizeOf(104));

        // 105 has only a deDE record: listed only with all locales, and still readable (it is local here)
        Assert.Equal(new uint[] { 100, 101, 103, 104 }, st.FileDataIds().ToArray());
        Assert.Equal(new uint[] { 100, 101, 103, 104, 105 }, st.FileDataIds(allLocales: true).ToArray());
        Assert.Equal(c["deonly"], st.ReadFileDataId(105));

        // names: root hash, case-insensitive, either slash
        Assert.Equal(100u, st.FileDataIdOf(WdtName));
        Assert.Equal(100u, st.FileDataIdOf(WdtName.ToLowerInvariant().Replace('\\', '/')));
        Assert.Equal(c["plain"], st.ReadName(PlainName));
        Assert.Null(st.ReadName(@"World\Maps\Nope.wdt"));
        Assert.Null(st.NameOf(100));

        // listfile: community "fdid;path" lines and bare paths (kept only when the root has the hash)
        var lf = Path.Combine(_root, "listfile.csv");
        File.WriteAllText(lf, "105;world/maps/test/german.bin\n" + PlainName + "\nnot/in/root.blp\n\n");
        Assert.Equal(2, st.LoadListfile(lf));
        Assert.Equal("world/maps/test/german.bin", st.NameOf(105));
        Assert.Equal(PlainName, st.NameOf(101));
        Assert.Equal(105u, st.FileDataIdOf(@"World\Maps\Test\German.bin"));
        Assert.Equal(c["deonly"], st.ReadName("world/maps/test/german.bin"));
        Assert.Null(st.FileDataIdOf("not/in/root.blp"));
    }

    [Fact]
    public void SyntheticStoreOpenVariants()
    {
        var (install, c) = MakeStore(false);
        using (var st = CascStorage.Open(install))  // no product: the first active row
            Assert.Equal(("wow", "9.8.7.11111"), (st.Product, st.Version));
        using (var st = CascStorage.Open(Path.Combine(install, "Data"), "WOW_CLASSIC_BETA"))
            Assert.Equal("1.60.1.12345", st.Version);
        using (var st = CascStorage.Open(Path.Combine(install, "_retail_")))
            Assert.Equal("wow", st.Product);
        Assert.Throws<ArgumentException>(() => CascStorage.Open(install, "wowz"));
        using (var st = CascStorage.Open(install, null, new CascOpenOptions { PreferHighResTextures = true }))
            Assert.Equal(c["hi"], st.ReadFileDataId(101));

        var (wod, _) = MakeStore(true);
        using (var st = CascStorage.Open(Path.Combine(wod, "Data")))
        {
            Assert.Equal(("wow_test", "9.8.7.12345", 12345), (st.Product, st.Version, st.Build));
            Assert.Equal(c["wdt"].Length, st.ReadName(WdtName)!.Length);
        }
    }
}

/// <summary>A test that needs a real CASC client copy (read-only): skipped when the folder is not there.</summary>
public sealed class CascFactAttribute : FactAttribute
{
    public CascFactAttribute(string folder)
    {
        if (!Directory.Exists(folder)) Skip = $"needs the CASC client copy at {folder}";
    }
}

/// <summary>
/// The CASC reader on the real copies under G:\WoW Clients and Patches (read-only; skipped when they are missing).
/// Expected numbers were measured by the Python probe (docs\research\CASC_INVESTIGATION.md).
/// </summary>
public class CascRealStoreTests
{
    public const string Wod = @"G:\WoW Clients and Patches\Clients\6.2.3.20779 - Warlords of Draenor\World of Warcraft 6.2.3.20779 enUS\World of Warcraft 6.2.3.20779 enUS";
    public const string Bnet = @"G:\WoW Clients and Patches\Clients\BattleNet snapshot 2026-10-20";
    public const string ClassicBeta = Bnet + @"\_classic_beta_";
    public const uint AzerothWdt = 775971, KalimdorWdt = 782779;

    private static List<string> ChunkTags(byte[] d)
    {
        var tags = new List<string>();
        for (int p = 0; p + 8 <= d.Length; p += 8 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)))
            tags.Add(new string(new[] { (char)d[p + 3], (char)d[p + 2], (char)d[p + 1], (char)d[p] }));
        return tags;
    }

    private static (int Pos, int Size) Find(byte[] d, string tag)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)))
            if (new string(new[] { (char)d[p + 3], (char)d[p + 2], (char)d[p + 1], (char)d[p] }) == tag)
                return (p + 8, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4)));
        return (-1, 0);
    }

    [CascFact(Wod)]
    public void WodByName()
    {
        using var st = CascStorage.Open(Wod);
        Assert.Equal("6.2.3.20779", st.Version);
        Assert.Equal(20779, st.Build);
        Assert.Equal("WOW-20779patch6.2.3_Retail", st.BuildName);
        Assert.Equal("legacy", st.Root.Format);
        Assert.Equal(874632, st.Root.RecordCount);
        Assert.Equal(521822, st.Root.Count);
        Assert.True(st.HasNames);

        Assert.Equal(AzerothWdt, st.FileDataIdOf(@"World\Maps\Azeroth\Azeroth.wdt"));
        Assert.Equal(KalimdorWdt, st.FileDataIdOf("world/maps/kalimdor/kalimdor.wdt"));
        var wdt = st.ReadName(@"World\Maps\Azeroth\Azeroth.wdt")!;
        Assert.Equal(new[] { "MVER", "MPHD", "MAIN" }, ChunkTags(wdt));
        var map = st.ReadName(@"DBFilesClient\Map.dbc")!;
        Assert.Equal("WDBC", Encoding.ASCII.GetString(map, 0, 4));
        Assert.Equal(548, BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(4)));
        var cdi = st.ReadName(@"DBFilesClient\CreatureDisplayInfo.db2")!;
        Assert.Equal("WDB2", Encoding.ASCII.GetString(cdi, 0, 4));
        Assert.Equal(cdi.Length, st.SizeOf(st.FileDataIdOf(@"DBFilesClient\CreatureDisplayInfo.db2")!.Value));
    }

    [CascFact(ClassicBeta)]
    public void ClassicBetaByFileDataId()
    {
        var sw = Stopwatch.StartNew();
        using var st = CascStorage.Open(ClassicBeta);
        Assert.Equal("wow_classic_beta", st.Product);
        Assert.Equal("1.60.1.70450", st.Version);
        Assert.Equal("WOW-70450patch1.60.1_ForeverBeta", st.BuildName);

        var wdt = st.ReadFileDataId(AzerothWdt)!;
        var tags = ChunkTags(wdt);
        Assert.Equal(new[] { "MVER", "MPHD", "MAIN", "MAID" }, tags.Take(4));
        Assert.Equal("MFST v2", st.Root.Format);
        Assert.Equal(2433102, st.Root.RecordCount);
        Assert.Equal(1250956, st.Root.Count);
        Assert.Equal(144999, st.Root.NameHashCount);
        Assert.True(st.HasNames);
        // "1.60" does not hash the map names
        Assert.Null(st.FileDataIdOf(@"World\Maps\Azeroth\Azeroth.wdt"));

        // every Kalimdor tile's root/obj0/tex0 is local
        var k = st.ReadFileDataId(KalimdorWdt)!;
        var (main, _) = Find(k, "MAIN");
        var (maid, _) = Find(k, "MAID");
        int tiles = 0;
        for (int i = 0; i < 4096; i++)
        {
            if ((BinaryPrimitives.ReadUInt32LittleEndian(k.AsSpan(main + 8 * i)) & 1) == 0) continue;
            tiles++;
            foreach (int slot in new[] { 0, 1, 3 })
                Assert.Equal(CascFileState.Local, st.State(BinaryPrimitives.ReadUInt32LittleEndian(k.AsSpan(maid + 32 * i + 4 * slot))));
        }
        Assert.Equal(988, tiles);

        // states over every enUS FileDataID
        var counts = new Dictionary<CascFileState, int>();
        uint encrypted = 0, notLocal = 0;
        foreach (var f in st.FileDataIds())
        {
            var s = st.State(f);
            counts[s] = counts.GetValueOrDefault(s) + 1;
            if (s == CascFileState.Encrypted && encrypted == 0) encrypted = f;
            if (s == CascFileState.NotLocal && notLocal == 0) notLocal = f;
        }
        Assert.True(counts[CascFileState.Local] > 1_200_000, $"local {counts[CascFileState.Local]}");
        Assert.InRange(counts[CascFileState.Encrypted], 1000, 10000);
        Assert.InRange(counts[CascFileState.NotLocal], 100, 30000);
        Assert.Throws<CascEncryptedException>(() => st.ReadFileDataId(encrypted));
        Assert.Throws<CascMissingException>(() => st.ReadFileDataId(notLocal));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }
}
