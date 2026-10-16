using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.M2;

namespace WoWData.Core.Designers;

/// <summary>One part of a spell visual: precast, cast, impact, aura (state), channel, missile or ground area.</summary>
public sealed record VisualSlot(string Id, string Label, string Column, bool IsKit, string Help);

/// <summary>An effect model a visual plays, and where (a kit's Head, Chest... attachment, or the missile).</summary>
public sealed record VisualModel(string Slot, string Where, long EffectNameId, string Name, string File);

/// <summary>A spell visual in words: its slots, the kits and the effect models they play.</summary>
public sealed record VisualInfo(long Id, IReadOnlyList<(VisualSlot Slot, long Value, string Text)> Slots, IReadOnlyList<VisualModel> Models, IReadOnlyList<string> UsedBy);

/// <summary>
/// 0.12.0: spell visuals for the spell designer: pick the precast, cast, missile, impact, aura and channel parts from
/// the visuals of existing spells (SpellVisual and its SpellVisualKit rows, the client's own playing order:
/// Spell_C_StartCastVisuals / HandleSpellGo / Missile_C, see the layouts' evidence), describe them, and duplicate a
/// visual with a tint and a size: the copy gets its own kits and its own effect models (copies of the .m2 with the
/// geometry, bones, particles and ribbons scaled, the model and particle colours and the textures tinted), so the
/// original spells keep their look.
/// </summary>
public static class SpellVisuals
{
    public static readonly IReadOnlyList<VisualSlot> Slots = new[]
    {
        new VisualSlot("precast", "Precast", "PrecastKit", true, "Played on the caster while casting."),
        new VisualSlot("cast", "Cast", "CastKit", true, "Played on the caster when the cast finishes."),
        new VisualSlot("missile", "Missile", "MissileModel", false, "The projectile flying to the target (needs a missile speed)."),
        new VisualSlot("impact", "Impact", "ImpactKit", true, "Played on every target hit."),
        new VisualSlot("state", "Aura", "StateKit", true, "Played on the target while the aura lasts."),
        new VisualSlot("channel", "Channel", "ChannelKit", true, "Played while channelling."),
        new VisualSlot("area", "Ground area", "AreaKit", true, "Played on a persistent ground area (Consecration, Blizzard)."),
    };

    /// <summary>The kit columns that spawn effect models, with the attachment in words.</summary>
    private static readonly (string Column, string Where)[] KitModels =
    {
        ("HeadEffect", "head"), ("ChestEffect", "chest"), ("BaseEffect", "feet"), ("LeftHandEffect", "left hand"), ("RightHandEffect", "right hand"),
        ("BreathEffect", "breath"), ("SpecialEffect1", "special 1"), ("SpecialEffect2", "special 2"), ("SpecialEffect3", "special 3"), ("WorldEffect", "ground"),
    };

    private static readonly string[] MissileColumns = { "HasMissile", "MissileModel", "MissilePathType", "MissileDestinationAttachment", "MissileSound", "AnimEventSoundID" };
    private static readonly string[] AreaColumns = { "HasAreaEffect", "AreaModel", "AreaKit" };

    public static VisualSlot Slot(string id) => Slots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new DesignException($"There is no visual part \"{id}\". The parts: {string.Join(", ", Slots.Select(s => s.Id))}.");

    /// <summary>The visual's parts, models and the spells using it.</summary>
    public static VisualInfo Describe(DesignSession s, long visual)
    {
        var vt = s.Table("SpellVisual");
        var vr = vt.Find(visual) ?? throw new DesignException($"There is no SpellVisual {visual}.");
        var slots = new List<(VisualSlot, long, string)>();
        var models = new List<VisualModel>();
        var kits = s.Lookup("SpellVisualKit");
        var names = s.Lookup("SpellVisualEffectName");
        long V(string c) => vt.Number(vr, vt.Layout.Column(c)!);
        foreach (var slot in Slots)
        {
            long v = V(slot.Column);
            if (slot.Id == "missile" && V("HasMissile") == 0) v = 0;
            string text = v == 0 ? "none" : slot.IsKit ? $"kit {v}" : $"model {v}";
            if (v != 0 && slot.IsKit && kits?.Find(v) is { } kr)
            {
                var parts = new List<string>();
                foreach (var (col, where) in KitModels)
                {
                    long e = kits.Number(kr, kits.Layout.Column(col)!);
                    if (e <= 0 || names?.Find(e) is not { } nr) continue;
                    var file = names.Text(nr, names.Layout.Column("FileName")!, 0);
                    models.Add(new VisualModel(slot.Id, where, e, names.Text(nr, names.Layout.Column("Name")!, 0), file));
                    parts.Add($"{where}: {Path.GetFileNameWithoutExtension(file)}");
                }
                text += parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : " (animation or sound only)";
            }
            else if (v != 0 && !slot.IsKit && names?.Find(v) is { } mr)
            {
                var file = names.Text(mr, names.Layout.Column("FileName")!, 0);
                models.Add(new VisualModel(slot.Id, "missile", v, names.Text(mr, names.Layout.Column("Name")!, 0), file));
                text += $" ({Path.GetFileNameWithoutExtension(file)})";
            }
            slots.Add((slot, v, text));
        }
        var st = s.Table("Spell");
        var vc = st.Layout.Column("SpellVisual")!;
        var used = st.Rows.Where(r => st.Number(r, vc) == visual).Take(12).Select(r => $"{st.Label(r)} [{st.Key(r)}]").ToList();
        return new VisualInfo(visual, slots, models, used);
    }

    /// <summary>The visuals of spells whose name has these words (the picker): visual ID and the spells using it.</summary>
    public static List<(long Visual, string Spells)> Find(DesignSession s, string words, int max = 40)
    {
        var st = s.Table("Spell");
        var vc = st.Layout.Column("SpellVisual")!;
        var nc = st.Layout.Column("Name")!;
        var w = words.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return st.Rows.Where(r => st.Number(r, vc) > 0 && w.All(st.Text(r, nc, 0).ToLowerInvariant().Contains))
            .GroupBy(r => st.Number(r, vc)).Take(max)
            .Select(g => (g.Key, string.Join(", ", g.Select(r => st.Text(r, nc, 0)).Distinct().Take(4)))).ToList();
    }

    /// <summary>
    /// Compose the spell's visual from parts: <paramref name="parts"/> maps a slot ("cast", "missile"...) to "visual:N"
    /// (that part of visual N), "spell:N" (of spell N's visual), "kit:N" (a kit), "model:N" (a missile or area model of
    /// SpellVisualEffectName) or "none". The spell's own visual is changed in place when no other spell uses it, else a
    /// new SpellVisual row is made (the other spells keep theirs).
    /// </summary>
    public static string Compose(DesignSession s, SpellDesigner d, long spell, IReadOnlyDictionary<string, string> parts)
    {
        var st = s.Table("Spell");
        var row = d.Row(spell);
        var vt = s.Table("SpellVisual");
        long current = st.Number(row, st.Layout.Column("SpellVisual")!);
        var target = OwnVisual(s, d, spell, out bool made);
        foreach (var (slotId, spec) in parts)
        {
            var slot = Slot(slotId);
            var x = spec.Trim().ToLowerInvariant();
            if (x == "none")
            {
                s.SetNumber("SpellVisual", target, slot.Column, 0);
                if (slot.Id == "missile") s.SetNumber("SpellVisual", target, "HasMissile", 0);
                if (slot.Id == "area") { s.SetNumber("SpellVisual", target, "HasAreaEffect", 0); s.SetNumber("SpellVisual", target, "AreaModel", 0); }
                continue;
            }
            var kv = x.Split(':', 2);
            if (kv.Length != 2 || !long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                throw new DesignException($"\"{spec}\" is not a visual part: visual:N, spell:N, kit:N, model:N or none.");
            switch (kv[0])
            {
                case "spell":
                case "visual":
                {
                    long from = n;
                    if (kv[0] == "spell") from = st.Number(d.Row(n), st.Layout.Column("SpellVisual")!);
                    var src = vt.Find(from) ?? throw new DesignException($"There is no SpellVisual {from}.");
                    var cols = slot.Id == "missile" ? MissileColumns : slot.Id == "area" ? AreaColumns : new[] { slot.Column };
                    foreach (var c in cols) s.SetNumber("SpellVisual", target, c, vt.Number(src, vt.Layout.Column(c)!));
                    break;
                }
                case "kit":
                    if (!slot.IsKit) throw new DesignException($"The {slot.Label} part is a model, not a kit (model:N).");
                    if (s.Lookup("SpellVisualKit")?.Find(n) == null) throw new DesignException($"There is no SpellVisualKit {n}.");
                    s.SetNumber("SpellVisual", target, slot.Column, n);
                    if (slot.Id == "area") s.SetNumber("SpellVisual", target, "HasAreaEffect", 1);
                    break;
                case "model":
                    if (slot.Id is not ("missile" or "area")) throw new DesignException($"The {slot.Label} part is a kit (kit:N).");
                    if (s.Lookup("SpellVisualEffectName")?.Find(n) == null) throw new DesignException($"There is no SpellVisualEffectName {n}.");
                    if (slot.Id == "missile")
                    {
                        s.SetNumber("SpellVisual", target, "MissileModel", n);
                        s.SetNumber("SpellVisual", target, "HasMissile", 1);
                    }
                    else
                    {
                        s.SetNumber("SpellVisual", target, "AreaModel", n);
                        s.SetNumber("SpellVisual", target, "HasAreaEffect", 1);
                    }
                    break;
                default:
                    throw new DesignException($"\"{spec}\" is not a visual part: visual:N, spell:N, kit:N, model:N or none.");
            }
        }
        long id = vt.Key(target)!.Value;
        d.Touch(spell);
        return made ? $"Made SpellVisual {id} for {d.Name(spell)} (visual {current} is used by other spells and stays as it is)." : $"Changed SpellVisual {id} of {d.Name(spell)}.";
    }

    /// <summary>The spell's own visual row: its current one if no other spell uses it, else a new copy.</summary>
    private static DbcRow OwnVisual(DesignSession s, SpellDesigner d, long spell, out bool made)
    {
        var st = s.Table("Spell");
        var vc = st.Layout.Column("SpellVisual")!;
        var row = d.Row(spell);
        long current = st.Number(row, vc);
        var vt = s.Table("SpellVisual");
        var cur = current > 0 ? vt.Find(current) : null;
        bool shared = cur == null || st.Rows.Any(r => st.Number(r, vc) == current && st.Key(r) != spell);
        if (!shared)
        {
            made = false;
            return cur!;
        }
        var nr = cur != null ? s.Session("SpellVisual").Duplicate(cur).Row : s.AddRow("SpellVisual");
        s.SetNumber("Spell", row, "SpellVisual", vt.Key(nr)!.Value);
        made = true;
        return nr;
    }

    /// <summary>
    /// Duplicate the spell's visual with its own kits and effect models: every model is copied (new .m2 and textures
    /// under Spells\WDS\), scaled by <paramref name="scale"/> and tinted by <paramref name="tint"/> (null = keep the
    /// colours). Other spells keep the original.
    /// </summary>
    public static string Duplicate(DesignSession s, SpellDesigner d, long spell, float scale, Vector3? tint, string tag)
    {
        if (scale <= 0.05f || scale > 20f) throw new DesignException("The size must be between 0.05 and 20 (1 = unchanged).");
        var st = s.Table("Spell");
        var row = d.Row(spell);
        long current = st.Number(row, st.Layout.Column("SpellVisual")!);
        var vt = s.Table("SpellVisual");
        var src = vt.Find(current) ?? throw new DesignException($"{d.Name(spell)} has no visual to duplicate (SpellVisual {current}).");
        var (nv, _) = s.Session("SpellVisual").Duplicate(src);
        long newVisual = vt.Key(nv)!.Value;
        s.SetNumber("Spell", row, "SpellVisual", newVisual);
        var kt = s.Table("SpellVisualKit");
        var nt = s.Table("SpellVisualEffectName");
        var copiedNames = new Dictionary<long, long>();
        var notes = new List<string>();
        long CopyName(long nameId)
        {
            if (copiedNames.TryGetValue(nameId, out var have)) return have;
            var nr0 = nt.Find(nameId);
            if (nr0 == null) return nameId;
            var file = nt.Text(nr0, nt.Layout.Column("FileName")!, 0);
            var newFile = CopyModel(s, file, scale, tint, tag, notes);
            var (nr, _) = s.Session("SpellVisualEffectName").Duplicate(nr0);
            s.Set("SpellVisualEffectName", nr, "FileName", newFile);
            s.Set("SpellVisualEffectName", nr, "Name", nt.Text(nr0, nt.Layout.Column("Name")!, 0) + "_" + tag);
            if (scale != 1f) s.SetFloat("SpellVisualEffectName", nr, "AreaEffectSize", nt.Float(nr0, nt.Layout.Column("AreaEffectSize")!) * scale);
            long id = nt.Key(nr)!.Value;
            copiedNames[nameId] = id;
            return id;
        }
        foreach (var slot in Slots)
        {
            long v = vt.Number(nv, vt.Layout.Column(slot.Column)!);
            if (v <= 0) continue;
            if (slot.IsKit)
            {
                var kr0 = kt.Find(v);
                if (kr0 == null) continue;
                var (kr, _) = s.Session("SpellVisualKit").Duplicate(kr0);
                foreach (var (col, _) in KitModels)
                {
                    long e = kt.Number(kr, kt.Layout.Column(col)!);
                    if (e > 0) s.SetNumber("SpellVisualKit", kr, col, CopyName(e));
                }
                s.SetNumber("SpellVisual", nv, slot.Column, kt.Key(kr)!.Value);
            }
            else if (slot.Id == "missile" && vt.Number(nv, vt.Layout.Column("HasMissile")!) != 0)
                s.SetNumber("SpellVisual", nv, "MissileModel", CopyName(v));
        }
        long area = vt.Number(nv, vt.Layout.Column("AreaModel")!);
        if (area > 0 && vt.Number(nv, vt.Layout.Column("HasAreaEffect")!) != 0) s.SetNumber("SpellVisual", nv, "AreaModel", CopyName(area));
        d.Touch(spell);
        return $"Made SpellVisual {newVisual} for {d.Name(spell)}: {copiedNames.Count} effect model(s) copied" +
               (scale != 1f ? $", size x{scale.ToString("0.##", CultureInfo.InvariantCulture)}" : "") + (tint != null ? $", tinted {TintText(tint.Value)}" : "") + "." +
               (notes.Count > 0 ? " " + string.Join(" ", notes.Distinct()) : "");
    }

    public static string TintText(Vector3 t) => $"#{(int)Math.Round(Math.Clamp(t.X, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(t.Y, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(t.Z, 0, 1) * 255):X2}";

    /// <summary>"#8040FF", "8040FF" or "0.5,0.25,1": a tint colour (each part 0-1).</summary>
    public static Vector3 ParseTint(string text)
    {
        var t = text.Trim().TrimStart('#');
        if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return new Vector3(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f);
        var parts = t.Split(',');
        if (parts.Length == 3 && parts.All(p => float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return new Vector3(parts.Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray());
        throw new DesignException($"\"{text}\" is not a colour: #RRGGBB (#8040FF) or r,g,b from 0 to 1.");
    }

    /// <summary>Copy an effect model with its textures (scaled, tinted) into Spells\WDS\; returns the new path as the
    /// table writes it (the original's extension, .mdx in most rows: the client loads the .m2).</summary>
    private static string CopyModel(DesignSession s, string file, float scale, Vector3? tint, string tag, List<string> notes)
    {
        var m2Path = Path.ChangeExtension(file.Replace('/', '\\'), ".m2");
        var data = s.ReadFile(m2Path) ?? throw new DesignException($"The effect model {m2Path} is not in the game's files.");
        var stem = Path.GetFileNameWithoutExtension(m2Path);
        var newM2 = $"Spells\\WDS\\{stem}_{tag}.m2";
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = Variant(data, scale, tint, tex =>
        {
            if (tint == null) return null;
            if (renamed.TryGetValue(tex, out var have)) return have;
            var blp = s.ReadFile(tex);
            if (blp == null) return null;
            var nb = TintTexture(blp, tint.Value);
            var np = $"Spells\\WDS\\{Path.GetFileNameWithoutExtension(tex)}_{tag}.blp";
            s.AddFile(np, nb, new Workspace.OriginInfo { Format = "BLP (tinted copy)", IsNew = true, UsedBy = { $"copy of {tex}" } }, $"Tinted copy of {Path.GetFileName(tex)}");
            renamed[tex] = np;
            return np;
        });
        var problems = Workspace.Workspace.ModelProblems(bytes);
        if (problems.Count > 0) throw new DesignException($"The copy of {m2Path} would not load in the game: {string.Join("; ", problems.Take(3))}");
        s.AddFile(newM2, bytes, new Workspace.OriginInfo { Format = "M2 (spell effect copy)", IsNew = true, UsedBy = { $"copy of {m2Path}" } },
            $"Copy of {Path.GetFileName(m2Path)}" + (scale != 1f ? $", size x{scale.ToString("0.##", CultureInfo.InvariantCulture)}" : "") + (tint != null ? $", tint {TintText(tint.Value)}" : ""));
        var ext = Path.GetExtension(file);
        return Path.ChangeExtension(newM2, ext.Length > 0 ? ext : ".m2");
    }

    /// <summary>A tinted copy of a texture in the same BLP format (colour multiplied, alpha kept).</summary>
    public static byte[] TintTexture(byte[] blp, Vector3 tint)
    {
        var b = BlpFile.Parse(blp);
        var img = b.Decode(0);
        var px = img.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)Math.Clamp((int)Math.Round(px[i] * tint.X), 0, 255);
            px[i + 1] = (byte)Math.Clamp((int)Math.Round(px[i + 1] * tint.Y), 0, 255);
            px[i + 2] = (byte)Math.Clamp((int)Math.Round(px[i + 2] * tint.Z), 0, 255);
        }
        return BlpEncoder.EncodeLike(b, img).Data;
    }

    /// <summary>
    /// A scaled and tinted variant of an M2: positions (vertices, bone pivots and moves, attachments, events, lights,
    /// particle and ribbon emitters, bounds, collision) and sizes (particle sizes, speeds, areas, gravity; ribbon heights)
    /// times <paramref name="scale"/>; the model's colour tracks and the particle and ribbon colours times
    /// <paramref name="tint"/>; texture file names replaced by <paramref name="renameTexture"/> (null = keep).
    /// </summary>
    public static byte[] Variant(byte[] data, float scale, Vector3? tint, Func<string, string?> renameTexture)
    {
        var f = M2File.Parse(data);
        if (scale != 1f)
        {
            var verts = f["vertices"];
            for (int i = 0; i < verts.Count; i++) ScaleFloats(verts.Bytes, i * verts.Elem.Size, 3, scale);
            var bones = f["bones"];
            for (int i = 0; i < bones.Count; i++)
            {
                bones.SetVec3(i, "pivot", bones.Vec3(i, "pivot") * scale);
                ScaleTrack(bones.Track(i, "translation"), scale);
            }
            foreach (var (arr, field) in new[] { ("attachments", "position"), ("events", "position"), ("lights", "position"), ("ribbons", "position"), ("particles", "position") })
            {
                var a = f[arr];
                for (int i = 0; i < a.Count; i++) a.SetVec3(i, field, a.Vec3(i, field) * scale);
            }
            var parts = f["particles"];
            for (int i = 0; i < parts.Count; i++)
            {
                foreach (var tr in new[] { "emission_speed", "speed_variation", "gravity", "emission_area_length", "emission_area_width", "z_source" })
                    ScaleTrack(parts.Track(i, tr), scale);
                var sc = parts.Floats(i, "scales");
                for (int k = 0; k < sc.Length; k++) BinaryPrimitives.WriteSingleLittleEndian(parts.Bytes.AsSpan(i * parts.Elem.Size + parts.Rec.Field("scales").Offset + k * 4), sc[k] * scale);
                var spline = parts.Child(i, "spline_points");
                for (int k = 0; k < spline.Count; k++) ScaleFloats(spline.Bytes, k * 12, 3, scale);
            }
            var ribs = f["ribbons"];
            for (int i = 0; i < ribs.Count; i++)
            {
                ScaleTrack(ribs.Track(i, "height_above"), scale);
                ScaleTrack(ribs.Track(i, "height_below"), scale);
            }
            foreach (var p in new[] { "bounds_min", "bounds_max", "collision_min", "collision_max" })
                f.Header.SetVec3(0, p, f.Header.Vec3(0, p) * scale);
            foreach (var p in new[] { "bounds_radius", "collision_radius" })
                f.Header.SetF32(0, p, f.Header.F32(0, p) * scale);
            var cv = f["collision_vertices"];
            for (int i = 0; i < cv.Count; i++) ScaleFloats(cv.Bytes, i * 12, 3, scale);
            var seqs = f["sequences"];
            for (int i = 0; i < seqs.Count; i++)
            {
                seqs.SetVec3(i, "bounds_min", seqs.Vec3(i, "bounds_min") * scale);
                seqs.SetVec3(i, "bounds_max", seqs.Vec3(i, "bounds_max") * scale);
                seqs.SetF32(i, "bounds_radius", seqs.F32(i, "bounds_radius") * scale);
            }
        }
        if (tint is { } t)
        {
            var colors = f["colors"];
            for (int i = 0; i < colors.Count; i++)
            {
                var vals = colors.Track(i, "color").Values;
                if (vals == null) continue;
                for (int k = 0; k + 12 <= vals.Bytes.Length; k += 12)
                    for (int c = 0; c < 3; c++)
                    {
                        float v = BinaryPrimitives.ReadSingleLittleEndian(vals.Bytes.AsSpan(k + c * 4));
                        BinaryPrimitives.WriteSingleLittleEndian(vals.Bytes.AsSpan(k + c * 4), v * (c == 0 ? t.X : c == 1 ? t.Y : t.Z));
                    }
            }
            var parts = f["particles"];
            for (int i = 0; i < parts.Count; i++)
            {
                int at = i * parts.Elem.Size + parts.Rec.Field("colors").Offset;
                for (int k = 0; k < 3; k++)
                {
                    // a CImVector: B, G, R, A bytes
                    int o = at + k * 4;
                    parts.Bytes[o] = (byte)Math.Clamp((int)Math.Round(parts.Bytes[o] * t.Z), 0, 255);
                    parts.Bytes[o + 1] = (byte)Math.Clamp((int)Math.Round(parts.Bytes[o + 1] * t.Y), 0, 255);
                    parts.Bytes[o + 2] = (byte)Math.Clamp((int)Math.Round(parts.Bytes[o + 2] * t.X), 0, 255);
                }
            }
            var ribs = f["ribbons"];
            for (int i = 0; i < ribs.Count; i++)
            {
                var vals = ribs.Track(i, "color").Values;
                if (vals == null) continue;
                for (int k = 0; k + 12 <= vals.Bytes.Length; k += 12)
                    for (int c = 0; c < 3; c++)
                    {
                        float v = BinaryPrimitives.ReadSingleLittleEndian(vals.Bytes.AsSpan(k + c * 4));
                        BinaryPrimitives.WriteSingleLittleEndian(vals.Bytes.AsSpan(k + c * 4), v * (c == 0 ? t.X : c == 1 ? t.Y : t.Z));
                    }
            }
        }
        var texs = f["textures"];
        for (int i = 0; i < texs.Count; i++)
        {
            var name = texs.Child(i, "filename");
            var old = name.AsString();
            if (old.Length == 0) continue;
            var nn = renameTexture(old);
            if (nn == null) continue;
            var nb = Encoding.ASCII.GetBytes(nn + "\0");
            name.Set(nb.Length, nb);
        }
        return f.Serialize();
    }

    private static void ScaleFloats(byte[] bytes, int offset, int count, float scale)
    {
        for (int k = 0; k < count; k++)
        {
            var span = bytes.AsSpan(offset + k * 4);
            BinaryPrimitives.WriteSingleLittleEndian(span, BinaryPrimitives.ReadSingleLittleEndian(span) * scale);
        }
    }

    private static void ScaleTrack(M2TrackView track, float scale)
    {
        if (track.Values is not { } vals) return;
        for (int k = 0; k + 4 <= vals.Bytes.Length; k += 4)
        {
            var span = vals.Bytes.AsSpan(k);
            BinaryPrimitives.WriteSingleLittleEndian(span, BinaryPrimitives.ReadSingleLittleEndian(span) * scale);
        }
    }
}
