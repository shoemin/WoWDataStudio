using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions.Catalog;

/// <summary>How a changed texture differs from the 1.12 one (measured pixel by pixel).</summary>
public enum PixelVerdict
{
    /// <summary>Mean difference below 3 of 255 and fewer than 1% of pixels more than 40 off: compression only.</summary>
    ReEncoded,
    /// <summary>Mean difference below 8 and fewer than 5% of pixels more than 40 off.</summary>
    LightEdits,
    Repainted,
    /// <summary>The same picture made larger (more detail): at the 1.12 size it is only re-encoded.</summary>
    Larger,
    /// <summary>Larger and repainted.</summary>
    LargerRepainted,
    /// <summary>Made smaller than the 1.12 one.</summary>
    Smaller,
    Unreadable,
}

public sealed partial class UpdateCatalogScanner
{
    private enum FileStatus { Identical, ChangedLikeEra, Changed, Gone, NoName }

    private sealed class FileCmp
    {
        public string Path = "";
        public string Ext = "";
        public uint Fdid;
        public string? Alt;
        public FileStatus Status;
        public long Size112;
        public long Size160;
        public int Width112, Height112;
        public bool Twin;
        public long TwinSize;
        public bool TwinLocal;
        public string Md5 = "";
        public PixelVerdict? Pixels;
        public string PixelNote = "";
    }

    /// <summary>FileDataIDs the user's 1.12 files have (found by name), for the parts that look for new files.</summary>
    private readonly HashSet<uint> _fdids112 = new();
    private readonly HashSet<uint> _fontFdids = new();
    private int _uiCode112, _shaders112;

    /// <summary>Character skins and armour pieces: 1.12 composes them at a fixed size (DIFF_112_160 2.6, INFERRED), so a
    /// larger picture needs client code.</summary>
    public static bool IsComposited(string path) =>
        path.StartsWith(@"Character\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"Item\TextureComponents\", StringComparison.OrdinalIgnoreCase);

    private static UpdateKind TextureKind(string path) =>
        path.StartsWith(@"Tileset\", StringComparison.OrdinalIgnoreCase) ? UpdateKind.GroundTextures
        : path.StartsWith(@"Environments\", StringComparison.OrdinalIgnoreCase) ? UpdateKind.Skybox
        : UpdateKind.Textures;

    private static string TopArea(string path)
    {
        int i = path.IndexOf('\\');
        return i <= 0 ? "(top folder)" : path[..i];
    }

    // ------------------------------------------------------------------ the comparison

    private void ScanFiles()
    {
        var c = _s.Client112;
        var casc = _s.Casc;
        var list = new List<FileCmp>();
        foreach (var p in c.Names().Values.Select(v => v.Path))
        {
            var u = p.ToUpperInvariant();
            if (u.StartsWith(@"WORLD\MAPS\", StringComparison.Ordinal) || u.StartsWith(@"DBFILESCLIENT\", StringComparison.Ordinal) || u.StartsWith(@"TEXTURES\MINIMAP\", StringComparison.Ordinal))
                continue;   // terrain, tables, minimaps: compared by their own parts
            var ext = Path.GetExtension(u);
            if (u.StartsWith(@"INTERFACE\", StringComparison.Ordinal) && ext is ".LUA" or ".XML" or ".TOC") { _uiCode112++; continue; }
            if (ext == ".BLS") { _shaders112++; continue; }
            var fc = new FileCmp { Path = ClientData.ResolveName(p), Ext = ext == ".MDX" ? ".M2" : ext };
            uint? id = _s.Names.FileDataIdOf(p);
            if (id == null && ext == ".WAV")
                foreach (var alt in new[] { p[..^4] + ".ogg", p[..^4] + ".mp3" })
                    if (_s.Names.FileDataIdOf(alt) is { } a) { id = a; fc.Alt = alt; break; }
            if (id == null) fc.Status = FileStatus.NoName;
            else if (!casc.Root.Contains(id.Value)) { fc.Fdid = id.Value; fc.Status = FileStatus.Gone; }
            else { fc.Fdid = id.Value; fc.Size160 = Size160(id.Value); }
            list.Add(fc);
        }
        foreach (var f in list.Where(f => f.Fdid != 0)) _fdids112.Add(f.Fdid);
        foreach (var f in list.Where(f => f.Fdid != 0 && f.Ext == ".TTF")) _fontFdids.Add(f.Fdid);

        // the MD5 of every 1.12 file the source has (cached by the game's fingerprint)
        var todo = list.Where(f => f.Fdid != 0 && f.Status != FileStatus.Gone).ToList();
        var md5 = LoadMd5Cache(out var cacheFile);
        var need = todo.Where(f => !md5.ContainsKey(f.Path.ToUpperInvariant())).ToList();
        if (need.Count > 0)
        {
            Report($"Reading {N(need.Count)} files of your 1.12 game once (their MD5 is kept for later scans)...");
            int done = 0;
            var fresh = new ConcurrentDictionary<string, (string, long, int, int)>();
            Parallel.ForEach(need, Par, f =>
            {
                var d = c.Read(f.Path);
                if (d == null) return;
                int w = 0, h = 0;
                if (d.Length >= 20 && d[0] == 'B' && d[1] == 'L' && d[2] == 'P' && d[3] == '2')
                {
                    w = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12));
                    h = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
                }
                fresh[f.Path.ToUpperInvariant()] = (Convert.ToHexString(MD5.HashData(d)), d.LongLength, w, h);
                int n = Interlocked.Increment(ref done);
                if (n % 5000 == 0) Report($"Reading your 1.12 files: {N(n)} of {N(need.Count)}...");
            });
            foreach (var (k, v) in fresh) md5[k] = v;
            SaveMd5Cache(cacheFile, md5);
        }
        var era = _s.Era;
        HashSet<uint>? twins = null;
        try
        {
            var hi = _s.HighRes;
            twins = new HashSet<uint>();
            foreach (var f in todo)
                if (casc.Root.TryGetContentKey(f.Fdid, out var a) && hi.Root.TryGetContentKey(f.Fdid, out var b) && !a.SequenceEqual(b))
                {
                    twins.Add(f.Fdid);
                    f.Twin = true;
                    f.TwinSize = Math.Max(0, hi.SizeOf(f.Fdid));
                    f.TwinLocal = hi.State(f.Fdid) == Casc.CascFileState.Local;
                }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Casc.CascMissingException)
        {
            Note($"The high-resolution textures could not be listed: {e.Message}");
        }
        foreach (var f in todo)
        {
            if (!md5.TryGetValue(f.Path.ToUpperInvariant(), out var m)) { f.Status = FileStatus.NoName; continue; }
            f.Md5 = m.Md5;
            f.Size112 = m.Size;
            f.Width112 = m.W;
            f.Height112 = m.H;
            casc.Root.TryGetContentKey(f.Fdid, out var ck);
            if (Convert.ToHexString(ck).Equals(m.Md5, StringComparison.OrdinalIgnoreCase)) f.Status = FileStatus.Identical;
            else if (era != null && era.Root.TryGetContentKey(f.Fdid, out var ce) && ce.SequenceEqual(ck)) f.Status = FileStatus.ChangedLikeEra;
            else f.Status = FileStatus.Changed;
        }
        int identical = list.Count(f => f.Status == FileStatus.Identical), likeEra = list.Count(f => f.Status == FileStatus.ChangedLikeEra),
            changed = list.Count(f => f.Status == FileStatus.Changed), gone = list.Count(f => f.Status == FileStatus.Gone), noName = list.Count(f => f.Status == FileStatus.NoName);
        Note($"Your 1.12 files against {_s.Copy.Name} (terrain, tables, minimaps and interface code compared separately): {N(list.Count)} files, {N(identical)} identical, " +
             $"{N(likeEra)} changed as in Classic Era already, {N(changed)} changed in {_s.Copy.Version}, {N(gone)} no longer there, {N(noName)} not found by name.");

        ComparePixels(list.Where(f => f.Ext == ".BLP" && f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra).ToList());
        TextureItems(list);
        ModelItems(list);
        SoundItems(list);
        OtherFileItems(list);
    }

    // ------------------------------------------------------------------ caches

    private Dictionary<string, (string Md5, long Size, int W, int H)> LoadMd5Cache(out string file)
    {
        var c = _s.Client112;
        file = Path.Combine(_s.CacheDir, $"md5_{UpdateCatalogStore.Key(c.DataDir.ToLowerInvariant() + "|" + c.Fingerprint())}.tsv");
        var d = new Dictionary<string, (string, long, int, int)>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file))
                {
                    var p = line.Split('\t');
                    if (p.Length < 5) continue;
                    d[p[0]] = (p[1], long.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture));
                }
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { d.Clear(); }
        return d;
    }

    private static void SaveMd5Cache(string file, Dictionary<string, (string Md5, long Size, int W, int H)> d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sb = new StringBuilder();
            foreach (var (k, v) in d.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(k).Append('\t').Append(v.Md5).Append('\t').Append(v.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(v.W.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(v.H.ToString(CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(file + ".tmp", sb.ToString());
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ pixels

    /// <summary>
    /// Compares two BLP pictures (the rule of DIFF_112_160 2.6: mean absolute RGB difference and the share of pixels more
    /// than 40 off). A larger source picture is compared at the 1.12 size (its own mip level of that size, else resized).
    /// </summary>
    public static (PixelVerdict Verdict, string Note) ComparePictures(byte[] blp112, byte[] blp160)
    {
        var a = BlpFile.Parse(blp112);
        var b = BlpFile.Parse(blp160);
        if (b.Width <= a.Width && b.Height <= a.Height && (b.Width < a.Width || b.Height < a.Height))
            return (PixelVerdict.Smaller, $"{a.Width} x {a.Height} made {b.Width} x {b.Height}");
        bool larger = b.Width > a.Width || b.Height > a.Height;
        var ia = a.Decode(0);
        RgbaImage ib;
        if (!larger) ib = b.Decode(0);
        else
        {
            int level = -1;
            for (int k = 1; k < b.LevelCount; k++)
                if (Math.Max(1, b.Width >> k) == a.Width && Math.Max(1, b.Height >> k) == a.Height) { level = k; break; }
            ib = level > 0 ? b.Decode(level) : ImageOps.Resize(b.Decode(0), a.Width, a.Height);
        }
        if (ib.Width != ia.Width || ib.Height != ia.Height) ib = ImageOps.Resize(ib, ia.Width, ia.Height);
        var pa = ia.Pixels;
        var pb = ib.Pixels;
        double sum = 0;
        int big = 0, n = ia.Width * ia.Height;
        for (int i = 0; i < n; i++)
        {
            int dr = Math.Abs(pa[4 * i] - pb[4 * i]), dg = Math.Abs(pa[4 * i + 1] - pb[4 * i + 1]), db = Math.Abs(pa[4 * i + 2] - pb[4 * i + 2]);
            sum += (dr + dg + db) / 3.0;
            if (Math.Max(dr, Math.Max(dg, db)) > 40) big++;
        }
        double mad = n == 0 ? 0 : sum / n, pBig = n == 0 ? 0 : 100.0 * big / n;
        var verdict = mad < 3 && pBig < 1 ? PixelVerdict.ReEncoded : mad < 8 && pBig < 5 ? PixelVerdict.LightEdits : PixelVerdict.Repainted;
        string note = string.Create(CultureInfo.InvariantCulture, $"mean difference {mad:0.0}, {pBig:0.0}% of pixels more than 40 off");
        if (larger)
        {
            note = $"{a.Width} x {a.Height} made {b.Width} x {b.Height}; at the 1.12 size: " + note;
            verdict = verdict == PixelVerdict.ReEncoded ? PixelVerdict.Larger : PixelVerdict.LargerRepainted;
        }
        return (verdict, note);
    }

    private void ComparePixels(List<FileCmp> changed)
    {
        var cacheFile = Path.Combine(_s.CacheDir, "pixels.tsv");
        var cache = new ConcurrentDictionary<string, (PixelVerdict, string)>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(cacheFile))
                foreach (var line in File.ReadLines(cacheFile))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 3 && Enum.TryParse<PixelVerdict>(p[1], out var v)) cache[p[0]] = (v, p[2]);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { cache.Clear(); }
        string KeyOf(FileCmp f)
        {
            _s.Casc.Root.TryGetContentKey(f.Fdid, out var ck);
            return f.Md5 + ":" + Convert.ToHexString(ck);
        }
        var need = changed.Where(f => !cache.ContainsKey(KeyOf(f))).ToList();
        if (need.Count > 0) Report($"Comparing {N(need.Count)} changed textures pixel by pixel (kept for later scans)...");
        int done = 0;
        Parallel.ForEach(need, Par, f =>
        {
            (PixelVerdict, string) r;
            try
            {
                var a = _s.Client112.Read(f.Path);
                var b = _s.Casc.ReadFileDataId(f.Fdid);
                r = a == null || b == null ? (PixelVerdict.Unreadable, "not readable") : ComparePictures(a, b);
            }
            catch (Exception e) when (e is BlpException or Casc.CascMissingException or Casc.CascEncryptedException or IOException or IndexOutOfRangeException or ArgumentException)
            {
                r = (PixelVerdict.Unreadable, e.Message.Replace('\t', ' ').Replace('\n', ' '));
            }
            cache[KeyOf(f)] = r;
            int n = Interlocked.Increment(ref done);
            if (n % 2000 == 0) Report($"Comparing textures: {N(n)} of {N(need.Count)}...");
        });
        foreach (var f in changed)
            if (cache.TryGetValue(KeyOf(f), out var v)) { f.Pixels = v.Item1; f.PixelNote = v.Item2; }
        if (need.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(_s.CacheDir);
                File.WriteAllLines(cacheFile + ".tmp", cache.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}\t{kv.Value.Item1}\t{kv.Value.Item2}"));
                File.Move(cacheFile + ".tmp", cacheFile, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ items: textures

    private const string ReEncodedRule = "measured pixel by pixel: a mean difference below 3 of 255 and fewer than 1% of the pixels more than 40 off";

    private void TextureItems(List<FileCmp> list)
    {
        var tex = list.Where(f => f.Ext == ".BLP" && f.Status is not (FileStatus.Gone or FileStatus.NoName)).ToList();
        var changed = tex.Where(f => f.Pixels is PixelVerdict.Repainted or PixelVerdict.LightEdits).ToList();
        // larger in 1.60: a gain only below the 1.12 limit (512) and not for composited character textures
        bool Gains(FileCmp f) => f.Width112 > 0 && Math.Max(f.Width112, f.Height112) < ConvertTextures.MaxSize;
        var larger = tex.Where(f => f.Pixels is PixelVerdict.Larger or PixelVerdict.LargerRepainted).ToList();
        var hd = tex.Where(f => f.Twin && f.TwinLocal && f.Pixels is not (PixelVerdict.Larger or PixelVerdict.LargerRepainted)).ToList();
        int hdNotLocal = tex.Count(f => f.Twin && !f.TwinLocal);
        if (hdNotLocal > 0) Note($"{N(hdNotLocal)} high-resolution versions of your textures are not downloaded (Battle.net fetches them when the game needs them); they are left out.");
        var same = tex.Where(f => f.Pixels is PixelVerdict.ReEncoded or PixelVerdict.Smaller or PixelVerdict.Unreadable).ToList();

        foreach (var kindGroup in changed.GroupBy(f => TextureKind(f.Path)))
            foreach (var (key, files) in Groups(kindGroup, f => f.Path))
            {
                int rep = files.Count(f => f.Pixels == PixelVerdict.Repainted), light = files.Count - rep;
                Add(new UpdateItem
                {
                    Id = "tex:changed:" + IdOf(key), Kind = kindGroup.Key, Where = key,
                    Title = $"{(kindGroup.Key == UpdateKind.GroundTextures ? "Changed ground textures" : kindGroup.Key == UpdateKind.Skybox ? "Changed sky textures" : "Changed textures")}: {key}",
                    Files = files.Count, Bytes = files.Sum(f => f.Size160), Class = Portability.Ready,
                    ClassReason = "the 1.60 pictures differ visibly from yours (compared pixel by pixel); 1.12 loads them as they are, or made at most 512 x 512.",
                    Changes = { $"Replaces {N(files.Count)} textures of your game with the 1.60 pictures ({N(rep)} repainted, {N(light)} lightly edited)." },
                    Facts = files.OrderBy(f => f.Pixels).Take(6).Select(f => $"{f.Path}: {(f.Pixels == PixelVerdict.Repainted ? "repainted" : "lightly edited")} ({f.PixelNote})").ToList(),
                    FileList = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => new UpdateFile(f.Path, f.Fdid, false, f.Size160, f.Pixels == PixelVerdict.Repainted ? "repainted" : "lightly edited")).ToList(),
                });
            }

        void SharperItems(List<FileCmp> files, bool hdVariant)
        {
            string what = hdVariant ? "High-resolution textures" : "Sharper textures (larger in 1.60)";
            string idp = hdVariant ? "tex:hd:" : "tex:larger:";
            var gain = files.Where(f => Gains(f) && !IsComposited(f.Path)).ToList();
            var comp = files.Where(f => Gains(f) && IsComposited(f.Path)).ToList();
            var noGain = files.Where(f => !Gains(f)).ToList();
            foreach (var kindGroup in gain.GroupBy(f => TextureKind(f.Path)))
                foreach (var (key, g) in Groups(kindGroup, f => f.Path))
                    Add(new UpdateItem
                    {
                        Id = idp + IdOf(key), Kind = kindGroup.Key, Where = key, Title = $"{what}: {key}",
                        Files = g.Count, Bytes = g.Sum(f => hdVariant ? f.TwinSize : f.Size160), Class = Portability.Ready,
                        ClassReason = hdVariant
                            ? "1.60 has a high-resolution version of these textures (usually 4 times as wide). 1.12 loads at most 512 x 512, so they are made 512 x 512 or smaller: still sharper than yours."
                            : "1.60 made these pictures larger (the same picture with more detail). Made at most 512 x 512, 1.12 loads them.",
                        Changes = { $"Replaces {N(g.Count)} textures of your game with sharper ones (at most 512 x 512; yours are smaller)." },
                        Facts = g.Take(5).Select(f => $"{f.Path}: yours {f.Width112} x {f.Height112}{(hdVariant ? $", high-resolution file {UpdateNames.Size(f.TwinSize)}" : ", " + f.PixelNote)}").ToList(),
                        FileList = g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => new UpdateFile(f.Path, f.Fdid, hdVariant, hdVariant ? f.TwinSize : f.Size160, "made at most 512 x 512")).ToList(),
                    });
            foreach (var (area, g) in comp.ByFolder(f => BaseGroup(f.Path)))
                Add(new UpdateItem
                {
                    Id = idp + "composited:" + IdOf(area), Kind = UpdateKind.Textures, Where = area, Title = $"{what}: {area} (character skins and armour)",
                    Files = g.Count, Bytes = g.Sum(f => hdVariant ? f.TwinSize : f.Size160), Class = Portability.NeedsClientCode,
                    ClassReason = "1.12 builds a character's skin, face, hair and armour into one texture of a fixed size, so larger pieces do not fit (inferred from the 1.12 format); they need client code that composes at a larger size.",
                    Changes = { $"With client code: sharper character skins, faces, hair or armour ({N(g.Count)} textures)." },
                    Facts = g.Take(4).Select(f => $"{f.Path}: yours {f.Width112} x {f.Height112}").ToList(),
                    NotYet = "the 1.12 game composes these at a fixed size; larger ones need work on the client.",
                    FileList = g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => new UpdateFile(f.Path, f.Fdid, hdVariant, hdVariant ? f.TwinSize : f.Size160, "")).ToList(),
                });
            foreach (var (area, g) in noGain.ByFolder(f => TopArea(f.Path)))
                Add(new UpdateItem
                {
                    Id = idp + "beyond512:" + IdOf(area), Kind = UpdateKind.Textures, Where = area, Title = $"{what} beyond 1.12's limit: {area}",
                    Files = g.Count, Bytes = g.Sum(f => hdVariant ? f.TwinSize : f.Size160), Class = Portability.NeedsClientCode,
                    ClassReason = "your textures are already 512 x 512 (or of unknown size); 1.12 loads at most 512 x 512, so the larger 1.60 pictures bring nothing without client code that loads larger textures.",
                    Changes = { $"With client code for larger textures: {N(g.Count)} sharper textures." },
                    NotYet = "1.12 loads at most 512 x 512 (BlpImage_IsValidDimension).",
                });
        }
        SharperItems(larger, false);
        SharperItems(hd, true);

        foreach (var (area, g) in same.ByFolder(f => TopArea(f.Path)))
            Add(new UpdateItem
            {
                Id = "tex:same:" + IdOf(area), Kind = TextureKind(g[0].Path), Where = area, Title = $"Re-encoded textures (no visible change): {area}",
                Files = g.Count, Bytes = g.Sum(f => f.Size160), Class = Portability.NoVisibleChange,
                ClassReason = $"the files differ, but the pictures are the same ({ReEncodedRule}){(g.Any(f => f.Pixels == PixelVerdict.Smaller) ? $"; {N(g.Count(f => f.Pixels == PixelVerdict.Smaller))} were made smaller in 1.60" : "")}.",
                Facts = { $"{N(g.Count(f => f.Pixels == PixelVerdict.ReEncoded))} re-encoded, {N(g.Count(f => f.Pixels == PixelVerdict.Smaller))} smaller, {N(g.Count(f => f.Pixels == PixelVerdict.Unreadable))} could not be compared." },
                NotYet = "nothing to bring: your pictures are the same.",
                FileList = g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => new UpdateFile(f.Path, f.Fdid, false, f.Size160, f.Pixels?.ToString() ?? "")).ToList(),
            });
    }

    // ------------------------------------------------------------------ items: models and buildings

    private static readonly System.Text.RegularExpressions.Regex PlayerModel =
        new(@"^Character\\[^\\]+\\(Male|Female)\\[^\\]+\.m2$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void ModelItems(List<FileCmp> list)
    {
        var m2 = list.Where(f => f.Ext == ".M2").ToList();
        var player = m2.Where(f => f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra && PlayerModel.IsMatch(f.Path) && f.Size160 > 2 * f.Size112).ToList();
        if (player.Count > 0)
            Add(new UpdateItem
            {
                Id = "models:player-hd", Kind = UpdateKind.Models, Where = "Character", Title = $"High-resolution player models ({N(player.Count)})",
                Files = player.Count, Bytes = player.Sum(f => f.Size160), Class = Portability.NeedsConverter,
                ClassReason = "the 1.60 player models are the modern high-resolution models (M2 version 272/274, external skin and animation files); 1.12 reads version 256. They need the model converter, and their looks the modern customization tables (client code).",
                Changes = { "With the model converter: the modern player models for every race and sex." },
                Needs = { "The M2 converter (version 272 to 256, with its .skin and .anim files).", "Client code for the character customization tables (ChrCustomization*) for more than one look." },
                Facts = player.OrderBy(f => f.Path).Take(6).Select(f => $"{f.Path}: {UpdateNames.Size(f.Size112)} in 1.12, {UpdateNames.Size(f.Size160)} in 1.60").ToList(),
                NotYet = "the model converter does not exist yet.",
                FileList = player.Select(f => new UpdateFile(f.Path, f.Fdid, false, f.Size160, "")).ToList(),
            });
        foreach (var (area, g) in m2.Where(f => f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra && !player.Contains(f)).ByFolder(f => TopArea(f.Path)))
            Add(new UpdateItem
            {
                Id = "models:resaved:" + IdOf(area), Kind = UpdateKind.Models, Where = area, Title = $"Models re-saved in the modern format: {area}",
                Files = g.Count, Bytes = g.Sum(f => f.Size160), Class = Portability.NoVisibleChange,
                ClassReason = "1.60 stores your models in the modern format (M2 version 272/274); their sizes stay close, so they are most likely the same models re-saved (the geometry was not compared).",
                Facts = { $"{N(g.Count)} models: {UpdateNames.Size(g.Sum(f => f.Size112))} in 1.12, {UpdateNames.Size(g.Sum(f => f.Size160))} in 1.60." },
                NotYet = "nothing to bring (and it would need the model converter).",
            });
        var wmo = list.Where(f => f.Ext == ".WMO").ToList();
        foreach (var (area, g) in wmo.Where(f => f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra).ByFolder(f => BaseGroup(f.Path)))
            Add(new UpdateItem
            {
                Id = "wmo:resaved:" + IdOf(area), Kind = UpdateKind.Buildings, Where = area, Title = $"Buildings re-saved in the modern format: {area}",
                Files = g.Count, Bytes = g.Sum(f => f.Size160), Class = Portability.NoVisibleChange,
                ClassReason = "1.60 stores these buildings of yours in the modern WMO format (version 17 with FileDataIDs); the landmarks 1.60 really replaced are new buildings (see Replaced landmarks).",
                Facts = { $"{N(g.Count)} files: {UpdateNames.Size(g.Sum(f => f.Size112))} in 1.12, {UpdateNames.Size(g.Sum(f => f.Size160))} in 1.60." },
                NotYet = "nothing to bring (and it would need the building converter).",
            });
        foreach (var (area, g) in list.Where(f => f.Status == FileStatus.Gone).ByFolder(f => TopArea(f.Path)))
            Add(new UpdateItem
            {
                Id = "gone:" + IdOf(area), Kind = g.All(f => f.Ext == ".WMO") ? UpdateKind.Buildings : g.All(f => f.Ext == ".M2") ? UpdateKind.Models : UpdateKind.Other,
                Where = area, Title = $"Files 1.60 no longer has: {area}", Files = g.Count, Bytes = g.Sum(f => f.Size112), Class = Portability.NoVisibleChange,
                ClassReason = "1.60 removed them (mostly the old pieces of landmarks it replaced); your 1.12 files stay as they are.",
                Facts = g.GroupBy(f => f.Ext).OrderByDescending(x => x.Count()).Select(x => $"{N(x.Count())} {x.Key.TrimStart('.').ToLowerInvariant()} files").Append("For example " + string.Join(", ", g.Take(4).Select(f => f.Path))).ToList(),
                NotYet = "nothing to bring.",
            });
    }

    // ------------------------------------------------------------------ items: sounds, fonts, other files

    private void SoundItems(List<FileCmp> list)
    {
        var snd = list.Where(f => f.Ext is ".WAV" or ".OGG" or ".MP3").ToList();
        foreach (var (area, g) in snd.Where(f => f.Status == FileStatus.ChangedLikeEra && f.Alt != null).ByFolder(f => BaseGroup(f.Path)))
            Add(new UpdateItem
            {
                Id = "sound:ogg:" + IdOf(area), Kind = UpdateKind.Sounds, Where = area, Title = $"Sounds stored as OGG (as in Classic Era): {area}",
                Files = g.Count, Bytes = g.Sum(f => f.Size160), Class = Portability.NoVisibleChange,
                ClassReason = "1.60 has your WAV sounds as OGG files, the same files as Classic Era: a format change; your WAV files stay.",
                Facts = { $"{N(g.Count)} sounds: {UpdateNames.Size(g.Sum(f => f.Size112))} as WAV, {UpdateNames.Size(g.Sum(f => f.Size160))} as OGG." },
                NotYet = "nothing to bring (1.12 plays WAV and MP3; an OGG to WAV converter does not exist yet).",
            });
        var changedOgg = snd.Where(f => f.Status == FileStatus.Changed && (f.Alt?.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ?? f.Ext == ".OGG")).ToList();
        foreach (var (area, g) in changedOgg.ByFolder(f => BaseGroup(f.Path)))
            Add(new UpdateItem
            {
                Id = "sound:changed:" + IdOf(area), Kind = UpdateKind.Sounds, Where = area, Title = $"Changed sounds: {area}",
                Files = g.Count, Bytes = g.Sum(f => f.Size160), Class = Portability.NeedsConverter,
                ClassReason = "1.60 changed these sounds (they differ from Classic Era's too); they are OGG files, which 1.12 cannot play (not verified in the game): an OGG to WAV converter is needed.",
                Changes = { $"With the converter: {N(g.Count)} new versions of your sounds." },
                Needs = { "An OGG to WAV converter (does not exist yet)." },
                Facts = g.Take(5).Select(f => f.Path).ToList(),
                NotYet = "1.12 plays WAV and MP3; the OGG converter does not exist yet.",
                FileList = g.Select(f => new UpdateFile(f.Path, f.Fdid, false, f.Size160, "OGG")).ToList(),
            });
        var mp3 = snd.Where(f => f.Ext == ".MP3" && f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra && f.Alt == null).ToList();
        foreach (var (area, g) in mp3.ByFolder(f => BaseGroup(f.Path)))
            Add(new UpdateItem
            {
                Id = "sound:mp3:" + IdOf(area), Kind = UpdateKind.Sounds, Where = area, Title = $"Changed music and voices (MP3): {area}",
                Files = g.Count, Bytes = g.Sum(f => f.Size160), Class = Portability.Ready,
                ClassReason = "MP3 files, which 1.12 plays as they are.",
                Changes = { $"Replaces {N(g.Count)} MP3 files of your game with the 1.60 ones." },
                Facts = g.Take(5).Select(f => $"{f.Path}: {UpdateNames.Size(f.Size112)} in 1.12, {UpdateNames.Size(f.Size160)} in 1.60").ToList(),
                FileList = g.Select(f => new UpdateFile(f.Path, f.Fdid, false, f.Size160, "MP3")).ToList(),
            });
    }

    private void OtherFileItems(List<FileCmp> list)
    {
        var fonts = list.Where(f => f.Ext == ".TTF" && f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra).ToList();
        if (fonts.Count > 0)
            Add(new UpdateItem
            {
                Id = "fonts:changed", Kind = UpdateKind.Fonts, Where = "Fonts", Title = $"Changed fonts ({N(fonts.Count)})",
                Files = fonts.Count, Bytes = fonts.Sum(f => f.Size160), Class = Portability.Ready,
                ClassReason = "TrueType fonts, which 1.12 loads as they are (" + (fonts.All(f => f.Status == FileStatus.ChangedLikeEra) ? "the same files as Classic Era" : "changed in 1.60") + ").",
                Changes = { "Replaces " + string.Join(", ", fonts.Select(f => Path.GetFileName(f.Path))) + " with the 1.60 versions (more letters, for example)." },
                Facts = fonts.Select(f => $"{f.Path}: {UpdateNames.Size(f.Size112)} in 1.12, {UpdateNames.Size(f.Size160)} in 1.60{(f.Status == FileStatus.ChangedLikeEra ? " (as in Classic Era)" : "")}").ToList(),
                FileList = fonts.Select(f => new UpdateFile(f.Path, f.Fdid, false, f.Size160, "TrueType")).ToList(),
            });
        var handled = new HashSet<string> { ".BLP", ".M2", ".WMO", ".WAV", ".OGG", ".MP3", ".TTF" };
        foreach (var g in list.Where(f => !handled.Contains(f.Ext) && f.Status is FileStatus.Changed or FileStatus.ChangedLikeEra).GroupBy(f => f.Ext))
        {
            var files = g.ToList();
            Add(new UpdateItem
            {
                Id = "other:changed:" + IdOf(g.Key.TrimStart('.')), Kind = UpdateKind.Other, Where = g.Key.ToLowerInvariant() + " files",
                Title = $"Other changed files ({g.Key.TrimStart('.').ToLowerInvariant()})", Files = files.Count, Bytes = files.Sum(f => f.Size160),
                Class = Portability.NeedsConverter,
                ClassReason = "the app does not know what changed inside these files or whether 1.12 reads the 1.60 version.",
                Facts = files.Take(6).Select(f => f.Path + (f.Status == FileStatus.ChangedLikeEra ? " (as in Classic Era)" : "")).ToList(),
                NotYet = "not known whether 1.12 reads the 1.60 version.",
            });
        }
    }
}
