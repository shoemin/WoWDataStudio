using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.Session;
using WoWData.Core.Workspace;

namespace WoWData.Cli.Commands;

/// <summary>textures: info, export (with variants), import (every question of the app as an option), compare.</summary>
internal static class TextureCommands
{
    private static string TexturePath(AppSession s, string text)
    {
        var p = Common.GamePath(s, text);
        if (!p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) throw new CliError(ExitCode.Usage, $"{p} is not a texture (.blp). \"wds find info {text}\" lists the textures it uses; \"wds textures export-all\" exports them.");
        return p;
    }

    private static object ExportJson(string path, string png, TextureSidecar side) => new { path, png, sidecar = TextureService.SidecarFor(png), source = side.Source, format = side.Blp?.Format, width = side.Blp?.Width, height = side.Blp?.Height };

    private static (string Png, TextureSidecar Side) Export(AppSession s, string path, string? outFile, string? outDir, bool edited, Dictionary<string, string> ctx)
    {
        var png = outFile != null ? Path.GetFullPath(outFile) : TextureService.SuggestedPng(outDir ?? s.Settings.DefaultExportDir, path, edited);
        if (FindCommands.IsInside(png, s.Client!.DataDir)) throw new CliError(ExitCode.Refused, "Refusing to write into the game's Data folder: export somewhere else.");
        return s.Textures!.Export(path, png, ctx, edited);
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "textures info",
            Summary = "a texture's facts: format, size, mip levels, your edit, who uses it, its ground pair (_s) and the additive note",
            Args = new[] { "<texture>" },
            Examples = new[] { "wds textures info Creature\\Boar\\BoarSkinIvory.blp", "wds textures info Tileset\\Durotar\\DurotarDirt.blp --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = TexturePath(s, inv.Arg(0));
                var ts = s.Textures!;
                var orig = ts.ReadOriginal(path);
                var edited = s.Workspace!.ReadEdited(path);
                BlpFile? o = orig == null ? null : BlpFile.Parse(orig.Value.Data), e = edited == null ? null : BlpFile.Parse(edited);
                var pair = s.GroundPair(path);
                var users = s.Index!.TextureUsers(path, 400);
                var advice = s.TextureAdvice(path);
                object? Blp(BlpFile? b, string? archive) => b == null ? null : new { format = b.FormatName, width = b.Width, height = b.Height, mipLevels = b.LevelCount, alphaDepth = (int)b.AlphaDepth, archive };
                var r = new CliResult().With(new
                {
                    path, original = Blp(o, orig?.Archive), edited = Blp(e, "workspace"), modified = edited != null,
                    ground = pair == null ? null : new { @base = pair.Base, specular = pair.Specular, drawn = pair.Drawn(s.GameSpecular.UsesSpecularTextures), game = s.GameSpecular.Describe() },
                    users = users.Count, usedBy = users.Take(50), advice,
                });
                r.Line(path);
                if (o != null) r.Line($"  game:      {o.FormatName}, {o.Width}x{o.Height}, {o.LevelCount} mip levels (in {orig!.Value.Archive})");
                if (e != null) r.Line($"  your edit: {e.FormatName}, {e.Width}x{e.Height}, {e.LevelCount} mip levels (in your workspace)");
                if (pair != null)
                {
                    r.Line($"  ground texture: {pair.Base}" + (pair.Specular != null ? $" + {pair.Specular}" : " (no _s file)"));
                    var maps = s.Index.GroundTextureMaps(pair.Base);
                    if (maps.Count > 0) r.Line("  painted on " + string.Join(", ", maps.Take(6).Select(m => $"{m.Tiles} tile(s) of {m.Name ?? "map " + m.Map}")) + (maps.Count > 6 ? ", ..." : ""));
                }
                foreach (var a in advice) r.Line("  * " + a);
                r.Line($"  used by {Common.Plural(users.Count, "thing")}" + (users.Count > 0 ? ": " + string.Join(", ", users.Take(5)) + (users.Count > 5 ? ", ..." : "") : ""));
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "textures export",
            Summary = "\"1. Export texture as PNG...\": the PNG plus its .blp.json note (keep it next to the PNG); --with-partner also the other file of a ground texture",
            Args = new[] { "<texture>" },
            Options = new[]
            {
                new OptionSpec("out", "file.png", "the PNG to write (default: <exports>\\<game folder>\\<name>.png, as the app suggests)"),
                new OptionSpec("original", null, "the game's texture even when you edited it (default: your edit when there is one, as the viewer shows)"),
                new OptionSpec("with-partner", null, "a ground texture: also export its other file (the _s specular variant or the base), next to the PNG"),
            },
            Examples = new[] { "wds textures export Creature\\Boar\\BoarSkinIvory.blp --out boar.png", "wds textures export Tileset\\Durotar\\DurotarDirt.blp --with-partner", "wds textures export file:Creature\\Boar\\BoarSkinIvory.blp --original --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = TexturePath(s, inv.Arg(0));
                bool edited = !inv.Flag("original") && s.Workspace!.IsModified(path);
                var (png, side) = Export(s, path, inv.Opt("out"), null, edited, new() { ["selected"] = s.FindEntry("file:" + path)?.Key ?? "", ["name"] = Path.GetFileNameWithoutExtension(path) });
                var files = new List<object> { ExportJson(path, png, side) };
                var r = new CliResult().Line($"Exported {path}{(edited ? " (your edit)" : "")} -> {png} (plus {Path.GetFileName(TextureService.SidecarFor(png))}: keep it next to the PNG).");
                if (s.GroundPair(path) is { } pair && pair.IsSpecular(path)) r.Warnings.Add($"{Path.GetFileName(path)} is the _s variant of {Path.GetFileName(pair.Base)}: its alpha channel is the shine map, not transparency. Easier: export {Path.GetFileName(pair.Base)}, edit it and import it (both files are edited).");
                if (inv.Flag("with-partner"))
                {
                    var partner = s.GroundPair(path)?.Partner(path) ?? throw new CliError(ExitCode.NotFound, $"{path} is not a ground texture with two files (--with-partner).");
                    bool pe = !inv.Flag("original") && s.Workspace!.IsModified(partner);
                    var (png2, side2) = Export(s, partner, Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(partner) + (pe ? "_edited" : "") + ".png"), null, pe, new());
                    files.Add(ExportJson(partner, png2, side2));
                    r.Line($"Exported {partner} -> {png2}.");
                }
                return r.With(new { files });
            },
        };
        yield return new CommandSpec
        {
            Path = "textures export-all",
            Summary = "export every texture an entry uses (TEXTURES IT USES); --variants also every skin of its model",
            Args = new[] { "<entry>" },
            Options = new[]
            {
                new OptionSpec("out", "folder", "the folder (each PNG under its game path; default: the exports folder)"),
                new OptionSpec("original", null, "the game's textures even where you edited them"),
                new OptionSpec("variants", null, "also the textures of every display / skin of the entry's models (all looks)"),
            },
            Examples = new[] { "wds textures export-all \"Mottled Boar\" --out exports", "wds textures export-all creature:3098 --variants --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Common.FindEntry(s, inv.Arg(0));
                var d = s.Index!.Details(e, s.Catalog);
                var paths = d.Textures.Where(t => t.Exists || s.Workspace!.IsModified(t.Path)).Select(t => t.Path).ToList();
                if (inv.Flag("variants"))
                    foreach (var m in d.Models)
                        foreach (var v in s.Index.ModelVariants(m))
                            if (v.Texture.Length > 0 && s.FileExists(v.Texture)) paths.Add(v.Texture);
                paths = paths.Where(p => p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (paths.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"{e.Name} uses no texture of the game.");
                var dir = inv.Opt("out") is { } o ? Path.GetFullPath(o) : s.Settings.DefaultExportDir;
                var files = new List<object>();
                var r = new CliResult();
                foreach (var p in paths)
                {
                    bool edited = !inv.Flag("original") && s.Workspace!.IsModified(p);
                    var (png, side) = Export(s, p, null, dir, edited, new() { ["selected"] = e.Key, ["name"] = e.Name });
                    files.Add(ExportJson(p, png, side));
                    r.Line($"{p} -> {png}");
                    inv.Ctx.Progress($"exported {p}");
                }
                r.Line($"{Common.Plural(files.Count, "texture")} of {e.Name} exported into {dir}.");
                return r.With(new { entry = Common.EntryJson(e), folder = dir, files });
            },
        };
        yield return new CommandSpec
        {
            Path = "textures import",
            Summary = "\"2. Import edited PNG...\": check and convert a PNG exactly like the app, show the check, save into your workspace",
            Args = new[] { "<png>" },
            Options = new[]
            {
                new OptionSpec("target", "texture", "the texture to replace (default: the one the PNG was exported from, its .blp.json)"),
                new OptionSpec("keep-size", null, "keep your PNG's size when it is another power of two (default: resize to the original size)"),
                new OptionSpec("format", "same|dxt3|dxt5", "a PNG with transparency for a texture without: keep the original format (default) or save as DXT3 / DXT5"),
                new OptionSpec("only-this", null, "a ground texture: edit only this file (default: \"Edit both (recommended)\")"),
                new OptionSpec("use-png-alpha", null, "a _s ground file: your PNG's alpha becomes the shine map (default: keep the original shine map)"),
                new OptionSpec("as-is", null, "an additive (glow) texture: import as it is (default: \"Keep black areas black (recommended)\")"),
                Common.DryRun,
            },
            Details = "The defaults are the app's recommended answers. --dry-run shows the check (what the app's check window says) and saves nothing. " +
                      "Saved imports are one change set in My changes (\"wds changes revert-set\" undoes it). The game is not changed.",
            Examples = new[] { "wds textures import boar.png --dry-run", "wds textures import boar.png", "wds textures import dirt.png --target Tileset\\Durotar\\DurotarDirt.blp --only-this", "wds textures import glow.png --as-is --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var png = Path.GetFullPath(inv.Arg(0));
                if (!File.Exists(png)) throw new CliError(ExitCode.NotFound, $"{png} does not exist.");
                var side = TextureService.ReadSidecar(png);
                string target = inv.Opt("target") is { } t ? TexturePath(s, t) : side?.ArchivePath ?? throw new CliError(ExitCode.Usage, $"{Path.GetFileName(png)} has no {Path.GetFileName(TextureService.SidecarFor(png))} next to it: say which texture it replaces with --target <texture>.");
                var answers = new FixedImportAnswers
                {
                    TargetFromSidecar = inv.Opt("target") == null,
                    KeepSize = inv.Flag("keep-size"),
                    Format = (inv.Opt("format") ?? "same").ToLowerInvariant() switch
                    {
                        "same" => ImportFormatChoice.SameAsOriginal, "dxt5" => ImportFormatChoice.Dxt5KeepAlpha, "dxt3" => ImportFormatChoice.Dxt3KeepAlpha,
                        var x => throw new CliError(ExitCode.Usage, $"--format is same, dxt3 or dxt5 (not {x})."),
                    },
                    EditBothFiles = !inv.Flag("only-this"),
                    KeepShineMap = !inv.Flag("use-png-alpha"),
                    KeepBlackBlack = !inv.Flag("as-is"),
                    Save = !inv.DryRun,
                };
                var res = await TextureImportFlow.RunAsync(s, png, target, answers, TextureImportFlow.Direct(inv.Ctx.Progress));
                var r = new CliResult().With(new
                {
                    outcome = res.Outcome, png, target = res.TargetPath, questions = answers.Asked, facts = res.Facts, notes = res.Notes, warnings = res.Warnings,
                    files = res.Plans.Select(p => new { path = p.TargetPath, format = p.FormatName, width = p.EncodedBlp?.Width, height = p.EncodedBlp?.Height, mipLevels = p.EncodedBlp?.LevelCount, identical = p.Identical, colourChanged = p.Diff?.ColourChangedPercent, alphaChanged = p.Diff?.AlphaChangedPercent }),
                    saved = res.Saved.Select(x => new { path = x.Path, changeSet = x.ChangeSet }),
                    message = res.Message,
                });
                r.Line($"Import {Path.GetFileName(png)} as {res.TargetPath}" + (inv.DryRun ? " (dry run: the check only)" : ""));
                foreach (var q in answers.Asked) r.Line("  ? " + q);
                foreach (var f in res.Facts) r.Line("  * " + f);
                foreach (var n in res.Notes) r.Line("  - " + n);
                r.Warnings.AddRange(res.Warnings);
                r.Line(res.Message);
                if (res.Outcome == TextureImportOutcome.Saved) r.Line($"Change set: {res.Saved[0].ChangeSet}. Next: \"wds changes install\" (test in game) or \"wds changes build-patch\".");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "textures compare",
            Summary = "\"Compare original vs. edited\": colour and alpha changes of your edit; --heatmap writes the Difference view as a PNG",
            Args = new[] { "<texture>" },
            Options = new[]
            {
                new OptionSpec("mip", "level", "compare the smaller copy of this mip level (default 0, full size)"),
                new OptionSpec("colour-only", null, "ignore the alpha channel (\"Ignore it (colour only)\")"),
                new OptionSpec("heatmap", "file.png", "write the difference heat map (grey unchanged, yellow small, red big, blue alpha only)"),
                new OptionSpec("original-png", "file.png", "also write the original as a PNG"),
                new OptionSpec("edited-png", "file.png", "also write your edit (as the game shows it) as a PNG"),
            },
            Examples = new[] { "wds textures compare Creature\\Boar\\BoarSkinIvory.blp", "wds textures compare Tileset\\Durotar\\DurotarDirt_s.blp --heatmap diff.png --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = TexturePath(s, inv.Arg(0));
                var edited = s.Workspace!.ReadEdited(path) ?? throw new CliError(ExitCode.NotFound, $"{path} has not been changed: nothing to compare.");
                var orig = s.Textures!.ReadOriginal(path);
                var eb = BlpFile.Parse(edited);
                int level = inv.IntOpt("mip") ?? 0;
                if (level < 0 || level >= eb.LevelCount) throw new CliError(ExitCode.Usage, $"--mip is 0 to {eb.LevelCount - 1}.");
                var b = eb.Decode(level);
                RgbaImage a;
                string note = "";
                if (orig != null)
                {
                    var ob = BlpFile.Parse(orig.Value.Data);
                    a = level < ob.LevelCount ? ob.Decode(level) : ob.Decode(ob.LevelCount - 1);
                    if (a.Width != b.Width || a.Height != b.Height) { a = ImageOps.Resize(a, b.Width, b.Height); note = "the sizes differ: the original was scaled for the comparison"; }
                }
                else { a = b; note = "the game has no such texture (new file)"; }
                bool alpha = !inv.Flag("colour-only");
                var st = ImageCompare.Compare(a, b, alpha);
                if (inv.Opt("heatmap") is { } hm) Png.Save(ImageCompare.Heatmap(a, b, alpha), Path.GetFullPath(hm), false);
                if (inv.Opt("original-png") is { } op) Png.Save(a, Path.GetFullPath(op), true);
                if (inv.Opt("edited-png") is { } ep) Png.Save(b, Path.GetFullPath(ep), true);
                var r = new CliResult().With(new
                {
                    path, mipLevel = level, width = b.Width, height = b.Height, changedPixels = st.ChangedPixels, totalPixels = st.TotalPixels,
                    colourChangedPercent = st.ColourChangedPercent, alphaChangedPercent = st.AlphaChangedPercent, hiddenColourPercent = st.HiddenColourPercent,
                    maxDifference = st.MaxDifference, meanDifference = st.MeanDifference, psnrDb = st.PsnrDb, headline = st.Headline, summary = st.Summary, note,
                    heatmap = inv.Opt("heatmap") is { } h ? Path.GetFullPath(h) : null,
                });
                r.Line($"{path}, mip level {level} ({b.Width}x{b.Height}): {st.Summary}");
                if (note.Length > 0) r.Line("Note: " + note);
                return r;
            },
        };
    }
}
