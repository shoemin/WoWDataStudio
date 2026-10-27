using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Blp;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Imaging;

namespace WoWData.Tests;

/// <summary>
/// Synthetic modern (CASC) clients for the tests, made only from our own bytes (never client data): a CASC install
/// (.build.info, a flavor folder with a stand-in executable that names its tables, Data\config, Data\data with .idx
/// files, data.000, encoding and root files), WDC5 tables shaped by our own layout file (the column count and layout
/// hash of <see cref="Db2Layouts.ForBuild"/>; the values are invented), BLP textures made by <see cref="BlpEncoder"/>, a
/// chunked model (MD21 with a TXID chunk), an encrypted file and a file that is not downloaded
/// (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, section 5.1). Expected values come from how the fixture is built.
/// </summary>
public static class SyntheticClients
{
    // ------------------------------------------------------------------ files of a store

    /// <summary>One file of a synthetic store: FileDataID, an optional name (hashed into the root), its content, whether
    /// it is on this computer, and a byte offset from which it is encrypted (the rest of the file; 0 = all of it).</summary>
    public sealed record StoreFile(uint Fdid, string? Name, byte[] Content, bool Local = true, int? EncryptedFrom = null);

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Chunk(char mode, byte[] payload) => new[] { (byte)mode }.Concat(payload).ToArray();

    /// <summary>An 'E' chunk: key name 0102030405060708 (8 bytes), IV length 4, IV, type 'S', then (not really
    /// encrypted) the payload: the reader never decrypts, it zero-fills or refuses.</summary>
    private static byte[] EncryptedChunk(byte[] payload) => Chunk('E', new byte[] { 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0, 0, 0, 0, (byte)'S' }.Concat(payload).ToArray());

    private static byte[] Frame(params (byte[] Encoded, int DecodedSize)[] chunks) => CascTests.Frame(chunks);

    /// <summary>Writes a store into <paramref name="install"/> with one product (its flavor folder named
    /// <paramref name="flavor"/>, with an executable that holds <paramref name="exeStrings"/>). Returns the flavor folder.</summary>
    public static string WriteStore(string install, string product, string version, string flavor, string exeName, IEnumerable<string> exeStrings, IReadOnlyList<StoreFile> files)
    {
        var data = Path.Combine(install, "Data");
        Directory.CreateDirectory(Path.Combine(data, "data"));
        Directory.CreateDirectory(Path.Combine(data, "indices"));

        // root (MFST v2): block A named files, block B files without a name hash
        var root = new MemoryStream();
        void W32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); root.Write(b); }
        void Block(uint flags, bool names, IReadOnlyList<StoreFile> recs)
        {
            W32((uint)recs.Count);
            W32(0x1F3F6);
            W32(flags);
            W32(0);
            root.WriteByte(0);
            long prev = -1;
            foreach (var r in recs) { W32((uint)(r.Fdid - prev - 1)); prev = r.Fdid; }
            foreach (var r in recs) root.Write(MD5.HashData(r.Content));
            if (names)
                foreach (var r in recs)
                {
                    var b = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(b, CascNameHash.Of(r.Name!));
                    root.Write(b);
                }
        }
        var named = files.Where(f => f.Name != null).OrderBy(f => f.Fdid).ToList();
        var unnamed = files.Where(f => f.Name == null).OrderBy(f => f.Fdid).ToList();
        root.Write("TSFM"u8);
        W32(24); W32(2); W32((uint)files.Count); W32((uint)named.Count); W32(0);
        if (named.Count > 0) Block(0, true, named);
        if (unnamed.Count > 0) Block(0x10000000, false, unnamed);
        var rootBytes = root.ToArray();

        // every file as BLTE: E chunks where encrypted, Z for the rest
        var all = files.Select(f => (Content: f.Content, f.Local, f.EncryptedFrom)).Append((Content: rootBytes, Local: true, EncryptedFrom: (int?)null)).ToList();
        var frames = new List<(byte[] CKey, byte[] EKey, byte[] Frame, int Size, bool Local, bool Encrypted)>();
        foreach (var (content, local, encFrom) in all)
        {
            byte[] frame;
            if (encFrom is { } from)
            {
                var chunks = new List<(byte[], int)>();
                if (from > 0) chunks.Add((Chunk('Z', Zlib(content[..from])), from));
                chunks.Add((EncryptedChunk(content[from..]), content.Length - from));
                frame = Frame(chunks.ToArray());
            }
            else frame = Frame((Chunk('Z', Zlib(content)), content.Length));
            if (frames.Any(f => f.CKey.AsSpan().SequenceEqual(MD5.HashData(content)))) continue;   // the same content once
            frames.Add((MD5.HashData(content), MD5.HashData(frame), frame, content.Length, local, encFrom != null));
        }

        // encoding file: content-key pages (38 bytes per entry) and ESpec pages (25 bytes per entry), 1 KB pages
        var specBlock = Encoding.ASCII.GetBytes("z\0b:{*=e:{0102030405060708,00000000,z}}\0");
        var ce = frames.OrderBy(f => Convert.ToHexString(f.CKey), StringComparer.Ordinal).ToList();
        var es = frames.OrderBy(f => Convert.ToHexString(f.EKey), StringComparer.Ordinal).ToList();
        var cePages = ce.Chunk(26).ToList();
        var esPages = es.Chunk(40).ToList();
        var enc = new MemoryStream();
        var hdr = new byte[22];
        hdr[0] = (byte)'E'; hdr[1] = (byte)'N'; hdr[2] = 1; hdr[3] = 16; hdr[4] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(5), 1);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(9), (uint)cePages.Count);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(13), (uint)esPages.Count);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(18), (uint)specBlock.Length);
        enc.Write(hdr);
        enc.Write(specBlock);
        var pages = new List<byte[]>();
        foreach (var p in cePages)
        {
            var page = new byte[1024];
            int o = 0;
            foreach (var f in p)
            {
                page[o] = 1;
                long size = f.Size;
                for (int k = 0; k < 5; k++) page[o + 1 + k] = (byte)(size >> (8 * (4 - k)));
                f.CKey.CopyTo(page, o + 6);
                f.EKey.CopyTo(page, o + 22);
                o += 38;
            }
            pages.Add(page);
        }
        for (int i = 0; i < cePages.Count; i++) { enc.Write(cePages[i][0].CKey); enc.Write(MD5.HashData(pages[i])); }
        foreach (var page in pages) enc.Write(page);
        pages.Clear();
        foreach (var p in esPages)
        {
            var page = new byte[1024];
            int o = 0;
            foreach (var f in p)
            {
                f.EKey.CopyTo(page, o);
                BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(o + 16), f.Encrypted ? 1u : 0u);
                BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(o + 21), (uint)f.Frame.Length);
                o += 25;
            }
            pages.Add(page);
        }
        for (int i = 0; i < esPages.Count; i++) { enc.Write(esPages[i][0].EKey); enc.Write(MD5.HashData(pages[i])); }
        foreach (var page in pages) enc.Write(page);
        var encBytes = enc.ToArray();
        var encFrame = Frame((Chunk('Z', Zlib(encBytes)), encBytes.Length));
        var encEKey = MD5.HashData(encFrame);

        // data.000 + .idx
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
        foreach (var f in frames.Where(f => f.Local)) Store(f.EKey, f.Frame);
        File.WriteAllBytes(Path.Combine(data, "data", "data.000"), archive.ToArray());
        foreach (var g in entries.GroupBy(e => CascLocalIndex.BucketOf(e.Key)))
            File.WriteAllBytes(Path.Combine(data, "data", $"{g.Key:x2}00000002.idx"), CascTests.Idx(g.Key, g));

        // build config, .build.info, the flavor folder
        var rootCKey = Convert.ToHexString(MD5.HashData(rootBytes)).ToLowerInvariant();
        var buildNo = version.Split('.').Last();
        var bc = $"# Build Configuration\n\nroot = {rootCKey}\nencoding = {Convert.ToHexString(MD5.HashData(encBytes)).ToLowerInvariant()} {Convert.ToHexString(encEKey).ToLowerInvariant()}\nbuild-name = WOW-{buildNo}patch{version[..version.LastIndexOf('.')]}_Synthetic\nbuild-uid = {product}\n";
        var bcHash = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(bc))).ToLowerInvariant();
        var bcPath = CascConfig.PathOf(data, bcHash);
        Directory.CreateDirectory(Path.GetDirectoryName(bcPath)!);
        File.WriteAllText(bcPath, bc);
        File.WriteAllText(Path.Combine(install, ".build.info"),
            "Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|CDN Key!HEX:16|Version!STRING:0|Product!STRING:0\n"
            + $"us|1|{bcHash}|00000000000000000000000000000000|{version}|{product}\n");
        var flavorDir = Path.Combine(install, flavor);
        Directory.CreateDirectory(flavorDir);
        File.WriteAllText(Path.Combine(flavorDir, ".flavor.info"), $"Product Flavor!STRING:0\n{product}\n");
        // a stand-in executable: no version resource, no DB2 meta data, only the table paths as strings (as WoD's executable)
        var exe = new MemoryStream();
        exe.Write("MZ"u8);
        exe.Write(new byte[62]);
        foreach (var s in exeStrings) { exe.Write(Encoding.ASCII.GetBytes(s)); exe.WriteByte(0); }
        File.WriteAllBytes(Path.Combine(flavorDir, exeName), exe.ToArray());
        return flavorDir;
    }

    // ------------------------------------------------------------------ WDC5 tables shaped by our layout file

    /// <summary>One row of a synthetic table: its ID, an optional parent ID, and values by column name (int, uint,
    /// float or string; unnamed columns are 0).</summary>
    public sealed record Row(int Id, Dictionary<string, object> Values, int? Parent = null);

    public static Row R(int id, params (string Column, object Value)[] values) => new(id, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase));
    public static Row RP(int id, int parent, params (string Column, object Value)[] values) => new(id, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase), parent);

    /// <summary>
    /// A WDC5 table with the layout hash and the flattened columns of <paramref name="table"/> in our layout file for
    /// <paramref name="build"/>: one plain 32-bit field per column. The ID is the layout's "ID" column when it has one,
    /// else an ID list. <paramref name="encrypted"/> rows go into a second section with a TACT key lookup, written as
    /// zeros (their IDs in the encrypted-ID list); <see cref="EncryptedFrom"/> is where that section starts.
    /// </summary>
    public static (byte[] Bytes, int EncryptedFrom) Wdc5(string table, IReadOnlyList<Row> rows, IReadOnlyList<int>? encrypted = null, int build = 70450)
    {
        var layouts = Db2Layouts.ForBuild(build) ?? throw new InvalidOperationException($"no layout file for {build}");
        var l = layouts.Get(table) ?? throw new InvalidOperationException($"no layout for {table}");
        uint hash = layouts.LayoutHash(table) ?? 0;
        // one entry per flattened column (the layout file's Name[N] is already Name1..NameN)
        var names = l.Columns.Select(c => c.Name).ToList();
        int nCols = names.Count;
        int idCol = names.FindIndex(n => n == "ID");
        int recordSize = nCols * 4;
        var enc = encrypted ?? Array.Empty<int>();
        int sec1Count = enc.Count;
        int total = rows.Count + sec1Count;

        // strings of section 0
        var strings = new MemoryStream();
        var strPos = new Dictionary<string, int>(StringComparer.Ordinal);
        int StringAt(string s)
        {
            if (s.Length == 0) return -1;
            if (strPos.TryGetValue(s, out var p)) return p;
            p = (int)strings.Position;
            strings.Write(Encoding.UTF8.GetBytes(s));
            strings.WriteByte(0);
            return strPos[s] = p;
        }
        var recs = new byte[rows.Count * recordSize];
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (int c = 0; c < nCols; c++)
            {
                uint v = 0;
                if (c == idCol) v = (uint)row.Id;
                else if (row.Values.TryGetValue(names[c], out var obj))
                {
                    if (obj is string s)
                    {
                        int p = StringAt(s);
                        // relative to the field's position in the records of all sections; the string tables follow them
                        v = p < 0 ? 0u : (uint)(total * recordSize + p - (r * recordSize + c * 4));
                    }
                    else if (obj is float f) v = BitConverter.SingleToUInt32Bits(f);
                    else v = unchecked((uint)Convert.ToInt64(obj));
                }
                BinaryPrimitives.WriteUInt32LittleEndian(recs.AsSpan(r * recordSize + c * 4), v);
            }
        }
        var strings0 = strings.ToArray();
        var rel = rows.Select((row, i) => (row, i)).Where(x => x.row.Parent != null).ToList();

        var w = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); w.Write(b); }
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); w.Write(b); }
        w.Write("WDC5"u8);
        U32(5);
        var schema = new byte[128];
        Encoding.ASCII.GetBytes("wowstatic_synthetic").CopyTo(schema, 0);
        w.Write(schema);
        var ids = rows.Select(r => r.Id).Concat(enc).ToList();
        U32((uint)total); U32((uint)nCols); U32((uint)recordSize); U32((uint)(strings0.Length + (sec1Count > 0 ? 4 : 0)));
        U32(0x5EED0000u ^ (uint)table.Length); U32(hash); U32((uint)(ids.Count > 0 ? ids.Min() : 0)); U32((uint)(ids.Count > 0 ? ids.Max() : 0)); U32(0);
        U16((ushort)(idCol >= 0 ? 0 : 4)); U16((ushort)Math.Max(0, idCol));
        U32((uint)nCols); U32((uint)recordSize); U32(0);
        U32((uint)(24 * nCols)); U32(0); U32(0); U32(sec1Count > 0 ? 2u : 1u);
        int secHeaders = (int)w.Position;
        w.Write(new byte[sec1Count > 0 ? 80 : 40]);
        for (int c = 0; c < nCols; c++) { U16(0); U16((ushort)(c * 4)); }
        for (int c = 0; c < nCols; c++) { U16((ushort)(c * 32)); U16(32); U32(0); U32((uint)Db2Compression.None); U32(0); U32(0); U32(0); }
        if (sec1Count > 0) { U32((uint)sec1Count); foreach (var id in enc) U32((uint)id); }
        int sec0 = (int)w.Position;
        w.Write(recs);
        w.Write(strings0);
        if (idCol < 0) foreach (var r in rows) U32((uint)r.Id);
        if (rel.Count > 0)
        {
            U32((uint)rel.Count); U32((uint)rel.Min(x => x.row.Parent!.Value)); U32((uint)rel.Max(x => x.row.Parent!.Value));
            foreach (var (row, i) in rel) { U32((uint)row.Parent!.Value); U32((uint)i); }
        }
        int sec0End = (int)w.Position;
        int sec1 = sec0End;
        if (sec1Count > 0) w.Write(new byte[sec1Count * recordSize + 4 + (idCol < 0 ? 4 * sec1Count : 0)]);
        var bytes = w.ToArray();
        void Section(int at, ulong tact, int offset, int records, int str, int idList, int relSize)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), tact);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 12), (uint)records);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 16), (uint)str);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 24), (uint)idList);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 28), (uint)relSize);
        }
        Section(secHeaders, 0, sec0, rows.Count, strings0.Length, idCol < 0 ? 4 * rows.Count : 0, rel.Count > 0 ? 12 + 8 * rel.Count : 0);
        if (sec1Count > 0) Section(secHeaders + 40, 0x1122334455667788, sec1, sec1Count, 4, idCol < 0 ? 4 * sec1Count : 0, 0);
        return (bytes, sec1Count > 0 ? sec1 : 0);
    }

    // ------------------------------------------------------------------ the "1.60" client of the M1 tests

    public const uint CubeModel = 1000001, SkinTexture = 1000002, IconTexture = 1000003, ModelTexture = 1000004, LockedSkin = 1000005, MissingTexture = 1000006, HdBake = 1000007;
    public const string CubeName = @"Creature\TestCube\TestCube.m2";
    public const string SkinName = @"Creature\TestCube\TestCubeSkin.blp";
    public const string IconName = @"Interface\Icons\INV_Test_01.blp";

    /// <summary>A small BLP (DXT1, 16 x 16, mip levels) of one colour.</summary>
    public static byte[] Blp(byte r, byte g, byte b)
    {
        var img = new RgbaImage(16, 16);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = r; img.Pixels[i + 1] = g; img.Pixels[i + 2] = b; img.Pixels[i + 3] = 255; }
        return BlpEncoder.EncodeNew(img, "dxt1");
    }

    /// <summary>A chunked model: MD21 (an MD20 header stub, version 274) and TXID (texture FileDataIDs, 0 = a run-time slot).</summary>
    public static byte[] Md21(params uint[] textureIds)
    {
        var ms = new MemoryStream();
        void Chunk(string magic, byte[] payload)
        {
            ms.Write(Encoding.ASCII.GetBytes(magic));
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, payload.Length);
            ms.Write(b);
            ms.Write(payload);
        }
        var md20 = new byte[64];
        "MD20"u8.CopyTo(md20);
        BinaryPrimitives.WriteUInt32LittleEndian(md20.AsSpan(4), 274);
        Chunk("MD21", md20);
        var tx = new byte[4 * textureIds.Length];
        for (int i = 0; i < textureIds.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(tx.AsSpan(4 * i), textureIds[i]);
        Chunk("TXID", tx);
        return ms.ToArray();
    }

    /// <summary>The tables of the test client (rows by column name of our layout file).</summary>
    public static Dictionary<string, (IReadOnlyList<Row> Rows, IReadOnlyList<int>? Locked)> Tables() => new()
    {
        ["CreatureDisplayInfo"] = (new[]
        {
            R(1, ("ModelID", 10), ("CreatureModelScale", 1.5f), ("TextureVariationFileDataID1", SkinTexture), ("TextureVariationFileDataID2", LockedSkin)),
            R(2, ("ModelID", 10), ("CreatureModelScale", 1f), ("ExtendedDisplayInfoID", 5), ("TextureVariationFileDataID1", SkinTexture)),
        }, new[] { 3, 4 }),
        ["CreatureModelData"] = (new[] { R(10, ("FileDataID", CubeModel)) }, null),
        ["CreatureDisplayInfoExtra"] = (new[] { R(5, ("DisplayRaceID", 2), ("DisplaySexID", 1), ("BakeMaterialResourcesID", 70), ("HDBakeMaterialResourcesID", 71)) }, null),
        ["TextureFileData"] = (new[] { R((int)IconTexture, ("MaterialResourcesID", 70)), R((int)HdBake, ("MaterialResourcesID", 71)), R((int)SkinTexture, ("MaterialResourcesID", 80)) }, null),
        ["ModelFileData"] = (new[] { R((int)CubeModel, ("ModelResourcesID", 90)) }, null),
        ["ComponentModelFileData"] = (new[] { R((int)CubeModel, ("RaceID", 1), ("GenderIndex", 0)) }, null),
        ["ComponentTextureFileData"] = (new[] { R((int)IconTexture, ("GenderIndex", 3)) }, null),
        ["ItemDisplayInfo"] = (new[] { R(20, ("ModelResourcesID1", 90), ("ModelMaterialResourcesID1", 80)) }, new[] { 21 }),
        ["ItemDisplayInfoMaterialRes"] = (new[] { RP(1, 20, ("ComponentSection", 3), ("MaterialResourcesID", 70)) }, null),
        ["ItemAppearance"] = (new[] { R(30, ("ItemDisplayInfoID", 20), ("DefaultIconFileDataID", HdBake)) }, null),
        ["ItemModifiedAppearance"] = (new[] { RP(40, 25, ("ItemID", 25), ("ItemAppearanceID", 30)) }, null),
        ["Item"] = (new[] { R(25) }, null),
        ["ItemSparse"] = (new[] { R(25, ("Display", "Test Sword")) }, new[] { 26 }),
        ["Creature"] = (new[] { R(50, ("Name", "Test Pet"), ("DisplayID1", 2)) }, null),
        ["GameObjects"] = (new[] { R(60, ("Name", "Test Sign"), ("DisplayID", 70)) }, null),
        ["GameObjectDisplayInfo"] = (new[] { R(70, ("FileDataID", CubeModel)), R(71, ("FileDataID", MissingTexture)) }, null),
        ["SpellVisualEffectName"] = (new[] { R(80, ("ModelFileDataID", CubeModel)) }, null),
        ["SpellName"] = (new[] { R(133, ("Name", "Test Bolt")) }, null),
        ["SpellXSpellVisual"] = (new[] { RP(900, 133, ("SpellVisualID", 85)) }, null),
        ["SpellVisual"] = (new[] { R(85) }, null),
        ["SpellVisualEvent"] = (new[] { RP(86, 85, ("SpellVisualKitID", 87)) }, null),
        ["SpellVisualKitModelAttach"] = (new[] { RP(88, 87, ("SpellVisualEffectNameID", 80)) }, null),
        ["Map"] = (new[] { R(0, ("Directory", "TestMap"), ("MapName", "Test Continent")) }, null),
        ["ManifestInterfaceData"] = (new[] { R((int)IconTexture, ("FilePath", @"Interface\Icons\"), ("FileName", "INV_Test_01.blp")) }, null),
        ["ChrRaces"] = (new[] { R(2, ("Name", "Orc"), ("ClientPrefix", "Or"), ("ClientFileString", "Orc")) }, null),
    };

    /// <summary>
    /// Writes the M1 test client: a Battle.net-style install with a "_classic_beta_" flavor (product
    /// wow_classic_beta, 1.60.1.70450), its tables, a model, textures (one encrypted, one not downloaded) and a
    /// three-line listfile. Returns (flavor folder, listfile).
    /// </summary>
    public static (string Flavor, string Listfile) WriteClassicBeta(string install)
    {
        var files = new List<StoreFile>();
        uint next = 2000000;
        var paths = new List<string>();
        foreach (var (name, (rows, locked)) in Tables())
        {
            var (bytes, encFrom) = Wdc5(name, rows, locked);
            var path = $@"DBFilesClient\{name}.db2";
            paths.Add(path);
            files.Add(new StoreFile(next++, path, bytes, true, encFrom > 0 ? encFrom : null));
        }
        files.Add(new StoreFile(CubeModel, CubeName, Md21(ModelTexture, 0, SkinTexture)));
        files.Add(new StoreFile(SkinTexture, null, Blp(200, 40, 40)));          // named only by the listfile
        files.Add(new StoreFile(IconTexture, IconName, Blp(40, 200, 40)));       // named by the root and the interface list
        files.Add(new StoreFile(ModelTexture, null, Blp(40, 40, 200)));          // no name at all
        files.Add(new StoreFile(LockedSkin, null, Blp(10, 10, 10), true, 0));   // encrypted
        files.Add(new StoreFile(MissingTexture, null, Blp(20, 20, 20), false));  // not downloaded
        files.Add(new StoreFile(HdBake, null, Blp(30, 30, 30)));
        var flavor = WriteStore(install, "wow_classic_beta", "1.60.1.70450", "_classic_beta_", "WowB.exe", paths, files);
        var listfile = Path.Combine(install, "test-listfile.csv");
        File.WriteAllText(listfile, $"{SkinTexture};{SkinName.Replace('\\', '/')}\n{CubeModel};{CubeName}\n999999999;Other/Product/Only.blp\n");
        return (flavor, listfile);
    }
}
