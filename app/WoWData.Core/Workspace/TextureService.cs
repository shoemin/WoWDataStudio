using System.Text.Json;
using System.Text.Json.Serialization;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.Mpq;

namespace WoWData.Core.Workspace;

public sealed class TextureException : Exception
{
    public TextureException(string message) : base(message) { }
}

/// <summary>The sidecar written next to every exported PNG (&lt;name&gt;.blp.json); same keys as the Python tool's, plus a
/// pixel fingerprint for the later model-package import (Phase 3c).</summary>
public sealed class TextureSidecar
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = "WoW Data Studio";
    [JsonPropertyName("archive_path")] public string ArchivePath { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("source_kind")] public string SourceKind { get; set; } = "archive";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("exported")] public string Exported { get; set; } = "";
    [JsonPropertyName("png")] public string Png { get; set; } = "";
    [JsonPropertyName("png_mode")] public string PngMode { get; set; } = "RGBA";
    [JsonPropertyName("pixel_sha256")] public string PixelSha256 { get; set; } = "";
    [JsonPropertyName("blp")] public BlpInfo? Blp { get; set; }
    [JsonPropertyName("context")] public Dictionary<string, string> Context { get; set; } = new();
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>The result of reading an edited PNG against its target, before anything is saved: the encoded BLP, both images
/// as the game will show them, and the plain-language checks.</summary>
public sealed class ImportPlan
{
    public string PngPath { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public RgbaImage Png { get; init; } = null!;
    public BlpFile? Original { get; init; }
    public byte[]? OriginalData { get; init; }
    public string? OriginalArchive { get; init; }
    public RgbaImage? OriginalImage { get; init; }
    public byte[] Encoded { get; set; } = Array.Empty<byte>();
    public BlpFile EncodedBlp { get; set; } = null!;
    public RgbaImage EditedImage { get; set; } = null!;
    public EncodeReport? Report { get; set; }
    public DiffStats? Diff { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>True when the PNG's size does not match the original (the user must accept a resize or a new size).</summary>
    public bool SizeMismatch { get; init; }
    public bool NeedsResize { get; init; }
    public (int W, int H) SuggestedSize { get; init; }
    public bool Identical => OriginalData != null && Encoded.AsSpan().SequenceEqual(OriginalData);
    public string FormatName => EncodedBlp.FormatName;
}

public enum ImportFormatChoice { SameAsOriginal, Dxt5KeepAlpha, Dxt3KeepAlpha }

/// <summary>Texture export (BLP -> PNG + sidecar) and import (edited PNG -> BLP in the original format, into the workspace).
/// Port of wowdata/texture.py.</summary>
public sealed class TextureService
{
    public const string SidecarSuffix = ".blp.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ClientData _client;
    private readonly Workspace _workspace;

    public TextureService(ClientData client, Workspace workspace)
    {
        _client = client;
        _workspace = workspace;
    }

    public static string SidecarFor(string pngPath) =>
        (pngPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? pngPath[..^4] : pngPath) + SidecarSuffix;

    /// <summary>The game's original (from the archives), or null for a file the game does not have.</summary>
    public (byte[] Data, string Archive)? ReadOriginal(string archivePath)
    {
        var src = _client.Find(archivePath);
        if (src == null) return null;
        var data = _client.Read(archivePath);
        return data == null ? null : (data, src.Name);
    }

    /// <summary>The bytes the user works with: the workspace copy when edited, else the original.</summary>
    public byte[]? ReadCurrent(string archivePath) => _workspace.ReadEdited(archivePath) ?? ReadOriginal(archivePath)?.Data;

    public static string PixelSha(RgbaImage img, bool includeAlpha)
    {
        if (includeAlpha) return Workspace.Sha256(img.Pixels);
        var rgb = new byte[img.Width * img.Height * 3];
        for (int i = 0, j = 0; i < img.Pixels.Length; i += 4, j += 3)
        {
            rgb[j] = img.Pixels[i];
            rgb[j + 1] = img.Pixels[i + 1];
            rgb[j + 2] = img.Pixels[i + 2];
        }
        return Workspace.Sha256(rgb);
    }

    /// <summary>Write &lt;pngPath&gt; and its sidecar. <paramref name="useEdited"/> exports the workspace version when there is one.</summary>
    public (string Png, TextureSidecar Sidecar) Export(string archivePath, string pngPath, Dictionary<string, string>? context = null, bool useEdited = false)
    {
        var name = ClientData.ResolveName(archivePath);
        var orig = ReadOriginal(name);
        byte[]? data = useEdited ? _workspace.ReadEdited(name) : null;
        string source = data != null ? "workspace" : orig?.Archive ?? "";
        data ??= orig?.Data ?? throw new TextureException($"{name} is not in the game archives.");
        var blp = BlpFile.Parse(data);
        var rgba = blp.Decode(0);
        bool alpha = blp.AlphaDepth != 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
        Png.Save(rgba, pngPath, alpha);
        var side = new TextureSidecar
        {
            ArchivePath = name,
            Source = source,
            SourceKind = data == orig?.Data ? "archive" : "workspace",
            Sha256 = Workspace.Sha256(data),
            Size = data.Length,
            Exported = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Png = Path.GetFileName(pngPath),
            PngMode = alpha ? "RGBA" : "RGB",
            PixelSha256 = PixelSha(rgba, alpha),
            Blp = blp.GetInfo(),
            Context = context ?? new(),
            Note = $"Edit the PNG in any image editor (keep its size, {blp.Width}x{blp.Height}), then use \"Import edited PNG...\" in WoW Data Studio.",
        };
        File.WriteAllText(SidecarFor(pngPath), JsonSerializer.Serialize(side, Json));
        return (pngPath, side);
    }

    /// <summary>0.8.0: the PNG an export suggests: &lt;exports&gt;\&lt;the texture's game folder&gt;\&lt;name&gt;[_edited].png (the app's save
    /// dialog and "wds textures export").</summary>
    public static string SuggestedPng(string exportDir, string archivePath, bool edited) =>
        Path.Combine(exportDir, Path.GetDirectoryName(ClientData.ResolveName(archivePath)) ?? "", Path.GetFileNameWithoutExtension(archivePath) + (edited ? "_edited" : "") + ".png");

    public static TextureSidecar? ReadSidecar(string pngPath)
    {
        var p = SidecarFor(pngPath);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<TextureSidecar>(File.ReadAllText(p)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Read an edited PNG and work out what importing it means. Nothing is saved.
    /// <paramref name="targetPath"/>: the texture to replace (default: the sidecar's archive path).
    /// <paramref name="resizeTo"/>: scale the PNG to this size first (the user accepted a resize).</summary>
    public ImportPlan Plan(string pngPath, string? targetPath, (int W, int H)? resizeTo = null, ImportFormatChoice format = ImportFormatChoice.SameAsOriginal)
    {
        if (!File.Exists(pngPath)) throw new TextureException($"The file {pngPath} does not exist.");
        RgbaImage png;
        try { png = Imaging.Png.Load(pngPath); }
        catch (ImageFormatException e) { throw new TextureException($"{Path.GetFileName(pngPath)} could not be read as a PNG image: {e.Message}"); }
        return PlanCore(png, pngPath, ReadSidecar(pngPath), targetPath, resizeTo, format);
    }

    /// <summary>The same as <see cref="Plan"/> for an image already in memory (the model-package import: a texture
    /// of a glTF file). <paramref name="sourceLabel"/> names it in messages and in the change record.</summary>
    public ImportPlan PlanImage(RgbaImage png, string sourceLabel, string targetPath, (int W, int H)? resizeTo = null, ImportFormatChoice format = ImportFormatChoice.SameAsOriginal) =>
        PlanCore(png, sourceLabel, null, targetPath, resizeTo, format);

    private ImportPlan PlanCore(RgbaImage png, string pngPath, TextureSidecar? side, string? targetPath, (int W, int H)? resizeTo, ImportFormatChoice format)
    {
        var target = ClientData.ResolveName(targetPath ?? side?.ArchivePath ?? "");
        if (target.Length == 0)
            throw new TextureException("This PNG has no sidecar file (.blp.json), so it is not known which game texture it replaces. Select the texture in the list first, then import.");
        if (!target.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) throw new TextureException($"{target} is not a texture (.blp).");

        var orig = ReadOriginal(target);
        BlpFile? origBlp = orig == null ? null : BlpFile.Parse(orig.Value.Data);
        var warnings = new List<string>();
        var notes = new List<string>();
        if (side != null && !string.Equals(ClientData.ResolveName(side.ArchivePath), target, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"This PNG was exported from {side.ArchivePath}, but it will replace {target}.");
        if (side != null && orig != null && side.SourceKind == "archive" && side.Sha256.Length > 0 && side.Sha256 != Workspace.Sha256(orig.Value.Data)
            && string.Equals(ClientData.ResolveName(side.ArchivePath), target, StringComparison.OrdinalIgnoreCase))
            warnings.Add("The game's copy of this texture changed since the PNG was exported; it is encoded against the current one.");

        int w = png.Width, h = png.Height;
        bool mismatch = origBlp != null && (w != origBlp.Width || h != origBlp.Height);
        bool pow2 = ImageOps.IsPow2(w) && ImageOps.IsPow2(h) && w <= 512 && h <= 512;
        var suggested = origBlp != null ? (origBlp.Width, origBlp.Height) : (Math.Min(512, ImageOps.NearestPow2(w)), Math.Min(512, ImageOps.NearestPow2(h)));
        if (resizeTo != null)
        {
            // the user chose this size (the original's, or keeping a different power-of-two size)
            if (resizeTo.Value.W != w || resizeTo.Value.H != h)
            {
                png = ImageOps.Resize(png, resizeTo.Value.W, resizeTo.Value.H);
                notes.Add($"Your image was resized from {w}x{h} to {resizeTo.Value.W}x{resizeTo.Value.H}.");
            }
            if (!ImageOps.IsPow2(png.Width) || !ImageOps.IsPow2(png.Height) || png.Width > 512 || png.Height > 512)
                throw new TextureException($"{png.Width}x{png.Height} is not a size the game can load (powers of two up to 512).");
        }
        else if (!pow2 || mismatch)
        {
            // the caller asks the user, then calls Plan again with resizeTo
            return new ImportPlan
            {
                PngPath = pngPath, TargetPath = target, Png = png, Original = origBlp, OriginalData = orig?.Data, OriginalArchive = orig?.Archive,
                SizeMismatch = mismatch, NeedsResize = !pow2 || mismatch, SuggestedSize = suggested,
            };
        }

        var plan = new ImportPlan
        {
            PngPath = pngPath, TargetPath = target, Png = png, Original = origBlp, OriginalData = orig?.Data, OriginalArchive = orig?.Archive,
            OriginalImage = origBlp?.Decode(0), SuggestedSize = suggested,
        };
        plan.Warnings.AddRange(warnings);
        plan.Notes.AddRange(notes);

        bool pngHasAlpha = !png.IsOpaque();
        if (origBlp != null)
        {
            if (origBlp.AlphaDepth == 0 && pngHasAlpha)
            {
                if (format == ImportFormatChoice.Dxt5KeepAlpha)
                    plan.Notes.Add("Saved as DXT5 (8-bit alpha) so your transparency is kept. The texture's file is re-encoded completely.");
                else if (format == ImportFormatChoice.Dxt3KeepAlpha)
                    plan.Notes.Add("Saved as DXT3 (4-bit alpha) so your transparency is kept. The texture's file is re-encoded completely.");
                else
                    plan.Warnings.Add($"The original has no transparency ({origBlp.FormatName}), but your PNG has transparent pixels. They will be ignored (shown opaque in game). To keep them, choose \"Save as DXT5 (keeps transparency)\".");
            }
            else if (origBlp.AlphaDepth == 1 && HasPartialAlpha(png))
                plan.Warnings.Add("The original has on/off transparency (1-bit alpha). Partly transparent pixels in your PNG will become fully transparent (below 50%) or fully opaque.");

            byte[] data;
            if ((format == ImportFormatChoice.Dxt5KeepAlpha || format == ImportFormatChoice.Dxt3KeepAlpha) && origBlp.AlphaDepth == 0 && pngHasAlpha)
            {
                data = BlpEncoder.EncodeNew(png, format == ImportFormatChoice.Dxt3KeepAlpha ? "dxt3" : "dxt5", origBlp.HasMips != 0);
                plan.Report = null;
            }
            else
            {
                var (d, rep) = BlpEncoder.EncodeLike(origBlp, png);
                data = d;
                plan.Report = rep;
                if (rep.Resized) plan.Notes.Add($"The size changes from {origBlp.Width}x{origBlp.Height} to {png.Width}x{png.Height}; the whole texture is re-encoded in the same format.");
            }
            plan.Encoded = data;
        }
        else
        {
            var fmt = BlpEncoder.SuggestFormat(png);
            plan.Encoded = BlpEncoder.EncodeNew(png, fmt, true);
            plan.Warnings.Add($"The game has no texture named {target}; a new {fmt.ToUpperInvariant()} texture will be created. The game only uses it if a model or database row points at it.");
        }
        var problems = BlpEncoder.Validate(plan.Encoded);
        if (problems.Count > 0) throw new TextureException("The encoded texture would not load in the game: " + string.Join("; ", problems));
        plan.EncodedBlp = BlpFile.Parse(plan.Encoded);
        plan.EditedImage = plan.EncodedBlp.Decode(0);
        if (plan.OriginalImage != null && plan.OriginalImage.Width == plan.EditedImage.Width && plan.OriginalImage.Height == plan.EditedImage.Height)
            plan.Diff = ImageCompare.Compare(plan.OriginalImage, plan.EditedImage, plan.EncodedBlp.AlphaDepth > 0 || (origBlp?.AlphaDepth ?? 0) > 0);
        if (plan.Identical) plan.Notes.Add("Your PNG is identical to the game's texture: nothing would change.");
        return plan;
    }

    private static bool HasPartialAlpha(RgbaImage img)
    {
        var p = img.Pixels;
        for (int i = 3; i < p.Length; i += 4) if (p[i] != 0 && p[i] != 255) return true;
        return false;
    }

    /// <summary>"Edit both" for a ground texture: the partner file (the "_s" variant of a base texture, or the base of a
    /// "_s" file) made from the colours of <paramref name="main"/>'s image. Its alpha stays the partner's own (the
    /// specular map) unless <paramref name="keepPartnerAlpha"/> is false. Encoded against the game's partner like any import.</summary>
    public ImportPlan PlanPartner(ImportPlan main, string partnerPath, bool keepPartnerAlpha = true)
    {
        var partner = ClientData.ResolveName(partnerPath);
        var current = ReadCurrent(partner) ?? throw new TextureException($"{partner} is not in the game archives.");
        var partnerImg = BlpFile.Parse(current).Decode(0);
        var merged = Maps.GroundTextures.MergeIntoPartner(main.Png, partnerImg, keepPartnerAlpha);
        var plan = PlanImage(merged, main.PngPath, partner);
        if (plan.NeedsResize) plan = PlanImage(merged, main.PngPath, partner, plan.SuggestedSize);
        plan.Notes.Add(keepPartnerAlpha
            ? $"{System.IO.Path.GetFileName(partner)} gets your colours; its own alpha channel is kept" + (Maps.GroundTextures.BaseNameOfSpecular(partner) != null ? " (the shine / specular map)." : ".")
            : $"{System.IO.Path.GetFileName(partner)} gets your colours and your alpha channel" + (Maps.GroundTextures.BaseNameOfSpecular(partner) != null ? " as its shine (specular) map." : "."));
        return plan;
    }

    /// <summary>Save a planned import into the workspace as one change set.</summary>
    public ChangeRecord Commit(ImportPlan plan, AssetIndex? index, string? label = null) => Commit(new[] { plan }, index, label)[0];

    /// <summary>Save several planned imports (e.g. a ground texture and its "_s" variant) as one change set.</summary>
    public List<ChangeRecord> Commit(IReadOnlyList<ImportPlan> plans, AssetIndex? index, string? label = null)
    {
        if (plans.Count == 0) throw new TextureException("Nothing to save.");
        var cs = _workspace.BeginChangeSet(label ?? $"Imported {Path.GetFileName(plans[0].PngPath)}");
        return plans.Select(p => CommitInto(p, index, cs)).ToList();
    }

    private ChangeRecord CommitInto(ImportPlan plan, AssetIndex? index, ChangeSet cs)
    {
        if (plan.NeedsResize && plan.Encoded.Length == 0) throw new TextureException("The image size must be fixed first.");
        var origin = new OriginInfo
        {
            Archive = plan.OriginalArchive,
            Sha256 = plan.OriginalData == null ? null : Workspace.Sha256(plan.OriginalData),
            Size = plan.OriginalData?.LongLength ?? 0,
            Format = plan.Original?.FormatName,
            Width = plan.Original?.Width ?? 0,
            Height = plan.Original?.Height ?? 0,
            Levels = plan.Original?.LevelCount ?? 0,
            IsNew = plan.Original == null,
        };
        if (index != null)
        {
            origin.Models = index.ModelsUsingTexture(plan.TargetPath);
            origin.UsedBy = index.TextureUsers(plan.TargetPath, 50);
        }
        var summary = plan.Diff?.Summary ?? (plan.Original == null ? "new texture" : "");
        if (plan.Report != null && plan.Report.PerLevel.Count > 0)
            summary += $" Re-encoded {plan.Report.PerLevel.Sum(l => l.ReEncoded):N0} of {plan.Report.PerLevel.Sum(l => l.Blocks ?? l.Pixels ?? 0):N0} blocks/pixels over {plan.Report.Levels} mip levels.";
        return _workspace.SaveEdit(plan.TargetPath, plan.Encoded, origin, cs, plan.PngPath, summary.Trim());
    }
}
