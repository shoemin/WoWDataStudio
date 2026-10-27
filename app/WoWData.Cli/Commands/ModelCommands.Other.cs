using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.M2.Modern;
using WoWData.Core.ModelIO;
using WoWData.Core.Session;

namespace WoWData.Cli.Commands;

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: "wds models info / anims / skeleton / render --version": the
/// models of another version opened read only, read by the modern reader (MD21, .skin files by FileDataID). The 1.12
/// commands are unchanged; these branches run only when the session is read only.
/// </summary>
internal static partial class ModelCommandsOther
{
    /// <summary>The model of a path ("Creature\Boar\Boar.m2", "#123456") or an entry of another version.</summary>
    public static string ModelPath(AppSession s, string text)
    {
        var p = text.Trim().Replace('/', '\\');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        if ((p.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || p.StartsWith('#')) && s.FileExists(p))
            return s.Files!.Info(p) is { Source: not WoWData.Core.Versions.NameSource.Number } info ? info.Path : p;
        var e = Common.FindEntry(s, text);
        if (e.Path != null && e.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) return e.Path;
        var d = s.Details(e);
        return d.Models.FirstOrDefault(m => m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
               ?? throw new CliError(ExitCode.NotFound, d.Models.Any(m => m.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
                   ? $"{e.Name} ({e.KindLabel}) is a building (WMO): buildings of other versions are shown later, with the maps."
                   : $"{e.Name} ({e.KindLabel}) has no model (.m2).");
    }

    /// <summary>The model read by the modern reader; a missing, locked or unreadable file is "not found" with its reason.</summary>
    public static M2Model Load(AppSession s, string model)
    {
        try { return s.ModelSource!.Load(model); }
        catch (IOException e) { throw new CliError(ExitCode.NotFound, e.Message); }
        catch (M2Exception e) { throw new CliError(ExitCode.NotFound, $"{model} cannot be read: {e.Message}"); }
    }

    /// <summary>The creature display of an entry of another version (for its skins), or null.</summary>
    public static long? DisplayOf(AppSession s, string text)
    {
        var e = s.FindEntry(text);
        if (e == null) return null;
        if (e.Key.StartsWith("cdisplay:", StringComparison.Ordinal)) return e.Id;
        if (e.Key.StartsWith("creature:", StringComparison.Ordinal)) return s.Index!.NamedCreatureDisplayIds(e.Id).Cast<long?>().FirstOrDefault();
        return null;
    }

    /// <summary>The item display of an entry of another version (for its texture), or null.</summary>
    public static long? ItemDisplayOf(AppSession s, string text)
    {
        var e = s.FindEntry(text);
        if (e == null) return null;
        if (e.Key.StartsWith("idisplay:", StringComparison.Ordinal)) return e.Id;
        if (e.Key.StartsWith("item:", StringComparison.Ordinal)) return s.Index!.ItemDisplayOf(e.Id);
        return null;
    }

    /// <summary>Where a sequence's keys are: "model" (or its skeleton), ".anim #id" (M3: read when the sequence is
    /// chosen), ".anim (none named)", "alias of n".</summary>
    public static string Where(M2Model m, int i)
    {
        var q = m.Sequences[i];
        if ((q.Flags & 0x40) != 0) return $"alias of {q.NextAlias}";
        if (m.Modern?.Animations is { } ext && ext.IsExternal(i)) return ext.FileOf(i) is var f and not 0 ? $".anim #{f}" : ".anim (none named)";
        return m.Modern?.SkeletonRead == true ? "skeleton" : "model";
    }

    /// <summary>M3: an external sequence loaded now (its .anim file read): "loads (120 keys)", or why not.</summary>
    public static (bool Loads, int Keys, string Text) Loads(M2Model m, int i)
    {
        if (m.Modern?.Animations is not { } ext || !ext.IsExternal(i)) return (true, 0, "");
        var st = ext.Ensure(i);
        return st.State switch
        {
            ExternalState.Loaded => (true, st.Keys, $"loads ({st.Keys:N0} keys)"),
            ExternalState.NoFile => (false, 0, "no .anim file named: no keys"),
            ExternalState.NoKeys => (false, 0, "no keys for this model in the file"),
            _ => (false, 0, $"cannot be read: {st.Why}"),
        };
    }

    public static CliResult Info(CliContext ctx, AppSession s, string model)
    {
        var m = Load(s, model);
        var mi = m.Modern!;
        var looks = s.Looks!;
        var creatures = s.Index!.CreatureDisplaysUsingModel(model);
        var items = s.Index.ItemDisplaysUsingModel(model).GroupBy(x => x.Id).Select(g => g.First()).ToList();
        var lookList = creatures.Select(c => (Name: $"display {c.Id}" + (c.Skins.Any(x => x.Length > 0) ? " (" + string.Join(" / ", c.Skins.Where(x => x.Length > 0).Select(Path.GetFileNameWithoutExtension)) + ")" : ""), Kind: "creature"))
            .Concat(items.Select(i => (Name: $"item display {i.Id} ({Path.GetFileNameWithoutExtension(i.Texture)})", Kind: "item"))).ToList();
        var names = ModelExporter.ActionNames(m, looks.AnimationName);
        int tri0 = m.Views.Length > 0 ? m.Views[0].Triangles.Length / 3 : 0;
        var textures = m.Textures.Select((t, i) => new
        {
            index = i, type = t.Type, slot = M2Schema.TextureTypes.TryGetValue(t.Type, out var tn) ? tn : $"slot {t.Type}",
            fileDataId = i < mi.TextureFiles.Length ? mi.TextureFiles[i] : 0u, name = t.FileName,
        }).ToList();
        var r = new CliResult().With(new
        {
            model, readOnly = true, version = s.Profile!.VersionBuild,
            format = new
            {
                m2Version = mi.Version, chunks = mi.Chunks.Select(c => new { magic = c.Magic, size = c.Size, used = c.Used, meaning = c.Meaning }),
                skinProfiles = mi.SkinProfiles, viewsRead = mi.ViewsRead, skinFiles = mi.SkinFiles, viewTriangles = mi.ViewTriangles,
                textureFiles = mi.TextureFiles, animFiles = mi.AnimFiles.Select(a => new { animId = a.AnimId, subId = a.SubId, fileDataId = a.File }),
                skeletonFile = mi.SkeletonFile, skeletonParentFile = mi.SkeletonParentFile, skeletonRead = mi.SkeletonRead, parentSequences = mi.ParentSequences,
                inlineSequences = mi.InlineSequences, externalSequences = mi.ExternalSequences, aliasSequences = mi.AliasSequences,
            },
            effects = new
            {
                particles = mi.Effects?.Particles.Count ?? 0, ribbons = mi.Effects?.Ribbons.Count ?? 0,
                particleBlends = mi.Effects?.Particles.GroupBy(p => p.Blend).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()) ?? new Dictionary<string, int>(),
                emitterTypes = mi.Effects?.Particles.GroupBy(p => p.EmitterType).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()) ?? new Dictionary<string, int>(),
                approximated = mi.Effects?.Approximated() ?? new List<string>(),
            },
            stats = new
            {
                vertices = m.Vertices.Length, triangles = tri0, views = m.Views.Length, submeshes = m.Views.Length > 0 ? m.Views[0].Submeshes.Length : 0,
                bones = m.Bones.Length, sequences = m.Sequences.Length, textures = m.Textures.Length, attachments = m.Attachments.Length,
                geosets = m.Views.Length > 0 ? m.Views[0].Submeshes.Select(x => x.GeosetId).Distinct().Count() : 0,
            },
            textures,
            animations = m.Sequences.Select((q, i) => new { name = names[i], variant = q.SubId, seconds = q.Duration / 1000.0, where = Where(m, i) }),
            looks = lookList.Select(l => new { name = l.Name, kind = l.Kind }),
            ignored = mi.Ignored, notShown = mi.NotShown, approximated = mi.Approximated, notes = mi.Notes,
        });
        r.Line($"{model}  ({s.Profile.VersionBuild}, read only; {mi.FormatText})");
        r.Line($"  statistics: {m.Vertices.Length:N0} vertices, {tri0:N0} triangles, {m.Bones.Length} bones, {m.Sequences.Length} animations, {m.Textures.Length} textures, " +
               $"{(m.Views.Length > 0 ? m.Views[0].Submeshes.Length : 0)} parts, {m.Attachments.Length} attachment points");
        r.Line($"  views: {mi.ViewsRead} of {mi.SkinProfiles} read: " + string.Join(", ", mi.ViewTriangles.Select((t, i) => $"skin file #{mi.SkinFiles[i]} {t:N0} triangles")));
        foreach (var t in textures) r.Line($"  texture {t.index}: {t.slot}" + (t.type == 0 ? $" #{t.fileDataId} {t.name}" : " (chosen by the game: the display's skins)"));
        if (mi.SkeletonFile != 0)
            r.Line($"  skeleton: #{mi.SkeletonFile}" + (mi.SkeletonRead ? $" ({m.Bones.Length} bones)" : " (cannot be read)") +
                   (mi.SkeletonParentFile != 0 ? $", parent #{mi.SkeletonParentFile}" + (mi.ParentSequences > 0 ? $" ({mi.ParentSequences} animations from it)" : "") : ""));
        r.Line($"  animations: {mi.InlineSequences} in the model" + (mi.SkeletonRead ? " and its skeleton" : "") + (mi.AliasSequences > 0 ? $", {mi.AliasSequences} sharing another one's keys" : "") +
               (mi.ExternalSequences > 0 ? $", {mi.ExternalSequences} in .anim files (read when chosen; wds models anims lists them)" : ""));
        if (mi.Effects is { } fx) r.Line($"  effects: {Common.Plural(fx.Particles.Count, "particle emitter")}, {Common.Plural(fx.Ribbons.Count, "ribbon")} (drawn approximately)");
        r.Line($"  looks ({lookList.Count}):");
        foreach (var l in lookList.Take(30)) r.Line("    " + l.Name);
        if (lookList.Count > 30) r.Line($"    ... and {lookList.Count - 30} more");
        r.Line("  chunks: " + string.Join(", ", mi.Chunks.Select(c => $"{c.Magic}{(c.Used ? "" : " (not used)")}")));
        foreach (var x in mi.Ignored) r.Line("  left out: " + x);
        foreach (var x in mi.NotShown) r.Line("  not shown: " + x);
        foreach (var x in mi.Approximated) r.Line("  drawn approximately: " + x);
        foreach (var x in mi.Notes) r.Line("  note: " + x);
        return r;
    }

    public static CliResult Anims(AppSession s, string model, string? sequence)
    {
        var m = Load(s, model);
        var looks = s.Looks!;
        var info = RigInfo.Read(m, looks.Animations);
        var names = ModelExporter.ActionNames(m, looks.AnimationName);
        var seqs = info.Sequences.AsEnumerable();
        if (sequence != null)
        {
            int idx = int.TryParse(sequence, out int n) ? n : Array.FindIndex(names, x => string.Equals(x, sequence, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx >= info.Sequences.Count) throw new CliError(ExitCode.NotFound, $"{model} has no animation \"{sequence}\". It has: {string.Join(", ", names.Take(40))}{(names.Length > 40 ? ", ..." : "")}.");
            seqs = new[] { info.Sequences[idx] };
        }
        var list = seqs.Select(q =>
        {
            var ev = info.Events.SelectMany(e => e.Triggers.Where(t => t.Sequence == q.Index).Select(t => new { code = e.Code, meaning = e.Meaning, ms = t.Time })).OrderBy(x => x.ms).ToList();
            var ext = m.Modern?.Animations;
            bool external = ext?.IsExternal(q.Index) == true;
            // M3: an external sequence is loaded here (its .anim file read) to say whether it plays
            var (loads, keys, loadText) = Loads(m, q.Index);
            return new
            {
                index = q.Index, name = names[q.Index], animId = q.AnimId, animName = q.AnimName, variation = q.Variation + 1, variations = q.Variations, subId = q.SubId,
                durationMs = q.Duration, loops = q.Loops, flags = q.Flags, chancePercent = Math.Round(q.ChancePercent, 2), blendMs = q.BlendTime, moveSpeed = q.MoveSpeed,
                repeatMin = q.ReplayMin, repeatMax = q.ReplayMax, where = Where(m, q.Index), events = ev,
                external, animFile = external ? ext!.FileOf(q.Index) : 0u, loads, keys, load = loadText,
            };
        }).ToList();
        int extCount = list.Count(x => x.external), extLoads = list.Count(x => x.external && x.loads);
        var r = new CliResult().With(new
        {
            model, readOnly = true, version = s.Profile!.VersionBuild, animations = list, globalSequences = info.GlobalSequences, namesFrom112 = looks.AnimationNames.Count > 0,
            external = new { sequences = extCount, load = extLoads, skeletonFile = m.Modern?.SkeletonFile ?? 0, skeletonParentFile = m.Modern?.SkeletonParentFile ?? 0 },
        });
        r.Line($"{model} ({s.Profile.VersionBuild}, read only): {Common.Plural(info.Sequences.Count, "animation")}" +
               (info.GlobalSequences.Count > 0 ? $", {Common.Plural(info.GlobalSequences.Count, "global sequence")}" : "") +
               (looks.AnimationNames.Count > 0 ? "; names from your 1.12 game's AnimationData" : "; no 1.12 game set: animations are named Anim<id>"));
        if (extCount > 0) r.Line($"  {extCount} kept in .anim files, {extLoads} of them load and play" + (extLoads < extCount ? $"; {extCount - extLoads} have no keys here (see Loads)" : ""));
        if (m.Modern is { SkeletonFile: not 0 } sk)
            r.Line($"  bones and animations from skeleton file #{sk.SkeletonFile}" + (sk.SkeletonParentFile != 0 ? $" and its parent #{sk.SkeletonParentFile}" + (sk.ParentSequences > 0 ? $" ({sk.ParentSequences} animations)" : " (not read)") : "") +
                   (sk.SkeletonRead ? "" : " (cannot be read: the model has no bones here)"));
        foreach (var l in Common.Table(new[] { "#", "Name", "Id", "Length", "Plays", "Chance", "Blend", "Speed", "Events", "Keys", "Loads" },
                     list.Select(x => (IReadOnlyList<string>)new[]
                     {
                         x.index.ToString(), x.name, x.animId.ToString(), $"{x.durationMs / 1000.0:0.###} s", x.loops ? "loops" : "once",
                         x.variations > 1 ? $"{x.chancePercent:0.#} %" : "", $"{x.blendMs} ms", x.moveSpeed != 0 ? $"{x.moveSpeed:0.##}" : "", x.events.Count.ToString(),
                         x.where, x.load,
                     }), 70))
            r.Line("  " + l);
        if (list.Count == 1)
        {
            var x = list[0];
            r.Line($"  {x.name}: animation {x.animId} ({x.animName}), variation {x.variation} of {x.variations}, sub id {x.subId}, flags 0x{x.flags:x}, repeats {x.repeatMin}-{x.repeatMax}, keys: {x.where}" +
                   (x.external ? $" ({x.load})" : "") + ".");
            foreach (var e in x.events) r.Line($"    event {e.code} at {e.ms / 1000.0:0.###} s: {e.meaning}");
        }
        return r;
    }
}
