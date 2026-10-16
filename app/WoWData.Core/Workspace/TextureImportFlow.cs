using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.Session;

namespace WoWData.Core.Workspace;

/// <summary>
/// The questions of an import, answered by the desktop app (dialogs) or by the command line (its options). Return null
/// (or false where it says so) to cancel the import.
/// </summary>
public interface ITextureImportDecider
{
    /// <summary>The PNG was exported from another texture than the selected one: the texture to replace, or null.</summary>
    string? WhichTarget(string exportedFrom, string selected);

    /// <summary>The size does not match (or is not a power of two): the size to use (the suggested original size, or the
    /// PNG's own when <paramref name="canKeep"/>), or null.</summary>
    (int W, int H)? Resize(ImportPlan plan, (int W, int H) suggested, bool canKeep);

    /// <summary>The PNG has transparency, the game's texture has none: the format to save, or null.</summary>
    ImportFormatChoice? Transparency(ImportPlan plan);

    /// <summary>An additive texture whose black background the PNG brightened: true keeps black areas black, null cancels.</summary>
    bool? KeepBlack(string glowNote, int liftedPixels, int pixels);

    /// <summary>A "_s" file whose alpha (the shine map) the PNG changed: true keeps the original shine map, null cancels.</summary>
    bool? KeepShine(string targetPath);

    /// <summary>A ground texture with two files: true edits both, false only the selected one, null cancels.</summary>
    bool? EditBoth(GroundTexturePair pair, TerrainSpecularSettings game, string target);

    /// <summary>The check before saving (the GUI's "Import edited PNG - check the result" window): true saves.</summary>
    bool Confirm(TextureImportResult preview);
}

public enum TextureImportOutcome { Saved, Previewed, Cancelled, NothingToImport }

/// <summary>What an import found and did: the plans (the texture and, with "Edit both", its partner), the facts, notes
/// and warnings shown in the check window, and the saved records.</summary>
public sealed class TextureImportResult
{
    public TextureImportOutcome Outcome { get; set; }
    public string PngPath { get; init; } = "";
    public string TargetPath { get; set; } = "";
    public List<ImportPlan> Plans { get; } = new();
    public List<string> Facts { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<ChangeRecord> Saved { get; } = new();
    public string Message { get; set; } = "";
    public ImportPlan? Main => Plans.Count > 0 ? Plans[0] : null;
}

/// <summary>
/// 0.8.0 (moved from the Browse tab's view model): the whole "Import edited PNG" flow, shared by the desktop app and
/// <c>wds textures import</c>: read and check the PNG, the size and transparency questions, an additive texture's black
/// background, a "_s" file's shine map, "Edit both" for a ground texture, the facts of the check window, and saving into the
/// workspace as one change set.
/// </summary>
public static class TextureImportFlow
{
    /// <summary>Runs a step (encoding can take a moment): the app shows its busy box, the command line prints the text.</summary>
    public delegate Task<ImportPlan> Work(string text, Func<ImportPlan> step);

    public static Work Direct(Action<string>? progress = null) => (text, step) =>
    {
        progress?.Invoke(text);
        return Task.FromResult(step());
    };

    public static async Task<TextureImportResult> RunAsync(AppSession session, string png, string target, ITextureImportDecider decide, Work work)
    {
        var ts = session.Textures ?? throw new TextureException("No game folder is open.");
        var result = new TextureImportResult { PngPath = png, TargetPath = target };
        TextureImportResult Cancel()
        {
            result.Outcome = TextureImportOutcome.Cancelled;
            result.Message = "Cancelled: nothing was saved.";
            return result;
        }
        var side = TextureService.ReadSidecar(png);
        if (side != null && !string.Equals(side.ArchivePath, target, StringComparison.OrdinalIgnoreCase))
        {
            var pick = decide.WhichTarget(side.ArchivePath, target);
            if (pick == null) return Cancel();
            target = pick;
        }
        (int W, int H)? size = null;
        var plan = await work("Reading your PNG...", () => ts.Plan(png, target));
        if (plan.NeedsResize)
        {
            bool pow2 = ImageOps.IsPow2(plan.Png.Width) && ImageOps.IsPow2(plan.Png.Height) && plan.Png.Width <= 512 && plan.Png.Height <= 512;
            size = decide.Resize(plan, plan.SuggestedSize, pow2);
            if (size == null) return Cancel();
            var s = size;
            plan = await work(s == plan.SuggestedSize ? "Resizing and converting..." : "Converting...", () => ts.Plan(png, target, s));
        }
        var format = ImportFormatChoice.SameAsOriginal;
        if (plan.Original is { AlphaDepth: 0 } && !plan.Png.IsOpaque())
        {
            var f = decide.Transparency(plan);
            if (f == null) return Cancel();
            format = f.Value;
            if (format != ImportFormatChoice.SameAsOriginal)
            {
                var s = size;
                var fmt = format;
                plan = await work("Converting to keep the transparency...", () => ts.Plan(png, target, s, fmt));
            }
        }
        var extraWarnings = new List<string>();
        var extraFacts = new List<string>();
        // 0.6.1: a texture drawn additively (or as a multiply layer): black is invisible; offer to keep it black
        var tgt = plan.TargetPath;
        var glow = await Task.Run(() => session.GlowNote(tgt));
        bool adjusted = false;
        if (glow != null)
        {
            extraWarnings.Add(glow);
            int lifted = plan.OriginalImage == null ? 0 : M2.MaterialBlends.LiftedBlackPixels(plan.OriginalImage, plan.Png);
            if (lifted > 0)
            {
                var keepBlack = decide.KeepBlack(glow, lifted, plan.Png.Width * plan.Png.Height);
                if (keepBlack == null) return Cancel();
                if (keepBlack.Value)
                {
                    var adjustedImg = M2.MaterialBlends.KeepBlackBlack(plan.OriginalImage!, plan.Png);
                    var fmt = format;
                    plan = await work("Keeping black areas black...", () => ts.PlanImage(adjustedImg, png, target, null, fmt));
                    plan.Notes.Add($"Black areas kept black: {lifted:N0} near-black pixels that your image brightened were faded back (an additive texture shows black as invisible).");
                    adjusted = true;
                }
            }
        }
        // 0.6.1: a ground texture has two files (the base and its "_s" specular variant); the game draws one of them
        var plans = new List<ImportPlan>();
        if (session.GroundPair(plan.TargetPath) is { } pair && pair.Partner(plan.TargetPath) is { } partner)
        {
            var g = session.GameSpecular;
            bool targetIsS = pair.IsSpecular(plan.TargetPath);
            // the "_s" file's alpha is its shine map: keep it unless the user wants their PNG's alpha
            var currentS = targetIsS && ts.ReadCurrent(plan.TargetPath) is { } sb ? BlpFile.Parse(sb).Decode(0) : null;
            if (currentS != null && GroundTextures.AlphaDiffers(currentS, plan.Png.Width == currentS.Width && plan.Png.Height == currentS.Height ? plan.Png : ImageOps.Resize(plan.Png, currentS.Width, currentS.Height)))
            {
                var keepShine = decide.KeepShine(plan.TargetPath);
                if (keepShine == null) return Cancel();
                if (keepShine.Value)
                {
                    var merged = GroundTextures.MergeIntoPartner(plan.Png, currentS, true);
                    var fmt = format;
                    plan = await work("Keeping the shine map...", () => ts.PlanImage(merged, png, target, null, fmt));
                    plan.Notes.Add("The shine (specular) map in the alpha channel was kept; only the colours are yours.");
                    adjusted = true;
                }
                else plan.Notes.Add("Your PNG's alpha channel becomes the shine (specular) map of the ground.");
            }
            var both = decide.EditBoth(pair, g, plan.TargetPath);
            if (both == null) return Cancel();
            if (both.Value)
            {
                var main = plan;
                var p2 = await work($"Converting {Path.GetFileName(partner)}...", () => ts.PlanPartner(main, partner, true));
                if (!p2.Identical) plans.Add(p2);
                extraFacts.Add($"Also saved: {partner} with your colours" + (p2.Diff != null ? $" ({p2.Diff.Headline})" : "") + (targetIsS ? "." : ", its shine map kept."));
            }
            else
            {
                bool drawnNow = string.Equals(pair.Drawn(g.UsesSpecularTextures), plan.TargetPath, StringComparison.OrdinalIgnoreCase);
                if (!drawnNow) extraWarnings.Add($"With {g.Describe()} the game draws {Path.GetFileName(partner)}, not {Path.GetFileName(plan.TargetPath)}: you will not see this change in the game unless you change those settings.");
            }
        }
        result.TargetPath = plan.TargetPath;
        if (plan.Identical)
        {
            result.Outcome = TextureImportOutcome.NothingToImport;
            result.Message = adjusted
                ? "After keeping the black areas and the shine map, your PNG is identical to the game's texture, so there is nothing to change."
                : "Your PNG is identical to the game's texture, so there is nothing to change.";
            result.Plans.Add(plan);
            return result;
        }
        result.Facts.Add($"Will be saved as {plan.FormatName}, {plan.EncodedBlp.Width}x{plan.EncodedBlp.Height}, {plan.EncodedBlp.LevelCount} mip levels (" +
                         (plan.Original != null && plan.FormatName == plan.Original.FormatName ? "same format as the original" : "new format") + ").");
        if (plan.Report != null && plan.Report.PerLevel.Count > 0 && !plan.Report.Resized)
            result.Facts.Add($"Only the parts you changed were re-encoded: {plan.Report.PerLevel.Sum(l => l.ReEncoded):N0} of {plan.Report.PerLevel.Sum(l => l.Blocks ?? l.Pixels ?? 0):N0} blocks/pixels; the rest is kept byte for byte.");
        if (plan.Diff != null) result.Facts.Add(plan.Diff.Summary);
        result.Facts.AddRange(extraFacts);
        if (session.Index != null)
        {
            var models = session.Index.ModelsUsingTexture(plan.TargetPath);
            var users = session.Index.TextureUsers(plan.TargetPath, 400);
            if (users.Count > 1) result.Facts.Add($"This texture is shared: {users.Count} uses ({string.Join(", ", models.Take(3).Select(Path.GetFileName))}{(models.Count > 3 ? ", ..." : "")}). All of them change.");
        }
        result.Warnings.AddRange(plan.Warnings.Concat(extraWarnings));
        result.Notes.AddRange(plan.Notes);
        result.Plans.Add(plan);
        result.Plans.AddRange(plans);
        if (!decide.Confirm(result))
        {
            result.Outcome = TextureImportOutcome.Previewed;
            result.Message = "Checked only: nothing was saved.";
            return result;
        }
        var index = session.Index;
        var all = result.Plans.ToList();
        var saved = await Task.Run(() => ts.Commit(all, index));
        result.Saved.AddRange(saved);
        result.Outcome = TextureImportOutcome.Saved;
        result.Message = $"Saved your edit of {string.Join(" and ", all.Select(p => Path.GetFileName(p.TargetPath)))}. Next: \"Test in game\" or \"Build release patch\".";
        return result;
    }

    /// <summary>The "edit both" question for a ground texture (the app's dialog and the screenshots).</summary>
    public static (string Title, string Message, string[] Buttons) EditBothQuestion(GroundTexturePair pair, TerrainSpecularSettings game, string target)
    {
        var other = Path.GetFileName(pair.Partner(target) ?? "");
        bool targetIsS = pair.IsSpecular(target);
        return ("A ground texture has two files",
            GroundTextures.WhichIsDrawn(pair, game) + "\n\n" +
            $"\"Edit both\" gives {other} your colours too, so your change shows whatever the game's settings are" +
            (targetIsS ? "." : $"; {other} keeps its own alpha channel (the shine map)."),
            new[] { "Edit both (recommended)", $"Only {Path.GetFileName(target)}", "Cancel" });
    }

    /// <summary>The "keep black areas black" question for an additive texture (the app's dialog and the screenshots).</summary>
    public static (string Title, string Message, string[] Buttons) KeepBlackQuestion(string glowNote, int lifted, int pixels) =>
        ("Keep the black background black?",
         glowNote + $"\n\nYour image makes {lifted:N0} near-black pixels brighter ({100.0 * lifted / Math.Max(1, pixels):0.#} % of the texture). " +
         "\"Keep black areas black\" fades your change out towards black, so a tint colours the glow but not its invisible background.",
         new[] { "Keep black areas black (recommended)", "Import as it is", "Cancel" });
}

/// <summary>Fixed answers to the import questions (the command line's options; tests and screenshots). The defaults are
/// the app's recommended choices: replace where the PNG came from, resize to the original size, keep the original format,
/// keep black areas black, keep the shine map, edit both files of a ground texture.</summary>
public sealed class FixedImportAnswers : ITextureImportDecider
{
    public bool TargetFromSidecar { get; init; } = true;
    /// <summary>Keep the PNG's own (power-of-two) size instead of resizing to the original's.</summary>
    public bool KeepSize { get; init; }
    public ImportFormatChoice Format { get; init; } = ImportFormatChoice.SameAsOriginal;
    public bool KeepBlackBlack { get; init; } = true;
    public bool KeepShineMap { get; init; } = true;
    public bool EditBothFiles { get; init; } = true;
    /// <summary>False: check only (dry run), nothing is saved.</summary>
    public bool Save { get; init; } = true;

    /// <summary>The questions that came up, in words (for the command line's report).</summary>
    public List<string> Asked { get; } = new();

    public string? WhichTarget(string exportedFrom, string selected)
    {
        Asked.Add($"The PNG was exported from {exportedFrom}; replacing {(TargetFromSidecar ? exportedFrom : selected)}.");
        return TargetFromSidecar ? exportedFrom : selected;
    }

    public (int W, int H)? Resize(ImportPlan plan, (int W, int H) suggested, bool canKeep)
    {
        var keep = KeepSize && canKeep;
        Asked.Add($"The image is {plan.Png.Width}x{plan.Png.Height}, the game's texture {suggested.W}x{suggested.H}: " + (keep ? "kept your size." : $"resized to {suggested.W}x{suggested.H}."));
        return keep ? (plan.Png.Width, plan.Png.Height) : suggested;
    }

    public ImportFormatChoice? Transparency(ImportPlan plan)
    {
        Asked.Add($"Your PNG has transparency, the game's texture ({plan.Original?.FormatName}) has none: " + (Format == ImportFormatChoice.SameAsOriginal ? "kept the original format." : $"saved as {(Format == ImportFormatChoice.Dxt3KeepAlpha ? "DXT3" : "DXT5")}."));
        return Format;
    }

    public bool? KeepBlack(string glowNote, int liftedPixels, int pixels)
    {
        Asked.Add($"An additive texture: {liftedPixels:N0} near-black pixels were brightened; " + (KeepBlackBlack ? "black areas kept black." : "imported as it is."));
        return KeepBlackBlack;
    }

    public bool? KeepShine(string targetPath)
    {
        Asked.Add($"The alpha (shine map) of {Path.GetFileName(targetPath)} differs: " + (KeepShineMap ? "the original shine map was kept." : "your PNG's alpha becomes the shine map."));
        return KeepShineMap;
    }

    public bool? EditBoth(GroundTexturePair pair, TerrainSpecularSettings game, string target)
    {
        Asked.Add($"A ground texture with two files: " + (EditBothFiles ? "both edited." : $"only {Path.GetFileName(target)}."));
        return EditBothFiles;
    }

    public bool Confirm(TextureImportResult preview) => Save;
}
