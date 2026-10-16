using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.M2;

namespace WoWData.Core.ModelIO;

/// <summary>A problem of an edited effects file, in plain words.</summary>
public sealed record EffectsProblem(RuleLevel Level, string Text);

/// <summary>What an edited wowdata_effects.json changes.</summary>
public sealed class EffectsEdit
{
    public List<EffectsProblem> Problems { get; } = new();
    /// <summary>"particle 3: gravity 0 -> 4.5"...</summary>
    public List<string> Changes { get; } = new();
    public bool HasBlocker => Problems.Any(p => p.Level == RuleLevel.Blocker);
    public bool Changed => Changes.Count > 0;
    internal List<Action<M2File>> Writes { get; } = new();

    /// <summary>Write the changes into a parsed model (the model the file was exported from).</summary>
    public void Apply(M2File f)
    {
        if (HasBlocker) throw new InvalidOperationException("the effects file has problems");
        foreach (var w in Writes) w(f);
    }
}

/// <summary>
/// 0.10.0: the particle emitters and ribbons of a model as JSON for advanced editing ("wowdata_effects.json" in an
/// export package with "Skeleton and animations"). By default they are kept byte for byte; when the file comes back
/// edited, every value is checked (types, texture / material / bone numbers, key times) and only the edited records are
/// written: an edited animated value (a track) is written again for every sequence from its keys, the others keep
/// their bytes. The number of emitters and ribbons cannot change.
///
/// Layout: { "particles": [ { "index", "fields": {name: number or [numbers]}, "lists": {...}, "tracks": {name: track} } ],
/// "ribbons": [...] }. A track is {"interpolation", "global_sequence", "sequences": [{"sequence", "name", "keys": [[ms,
/// value...]]}]} (times from the sequence start), or {"interpolation", "global_sequence", "keys": [[ms, value...]]} for a
/// track that is the same in every sequence (absolute times) or runs on a global sequence.
/// </summary>
public static class EffectsJson
{
    public const string FileName = "wowdata_effects.json";
    public const string FormatName = "wowdata-effects";
    private static readonly string[] Arrays = { "particles", "ribbons" };

    public static JsonObject? Export(M2Model m)
    {
        var f = m.File;
        if (f["particles"].Count == 0 && f["ribbons"].Count == 0) return null;
        var names = ModelExporter.ActionNames(m, id => $"Anim{id}");
        var root = new JsonObject
        {
            ["format"] = FormatName, ["format_version"] = 1, ["model"] = m.Name,
            ["note"] = "Advanced: the particle emitters and ribbons of the model. Change values only (the number of emitters, fields and keys per value must stay valid); WoW Data Studio checks everything on import. Track keys are [time in ms, value...]; per-sequence times start at 0.",
            ["sequences"] = new JsonArray(m.Sequences.Select((s, i) => (JsonNode)new JsonObject { ["index"] = i, ["anim_id"] = s.AnimId, ["duration_ms"] = s.Duration }).ToArray()),
            ["global_sequences"] = new JsonArray(m.GlobalSequences.Select(x => (JsonNode)x).ToArray()),
        };
        foreach (var an in Arrays)
        {
            var arr = f[an];
            var list = new JsonArray();
            for (int i = 0; i < arr.Count; i++) list.Add(Record(m, arr, i));
            root[an] = list;
        }
        return root;
    }

    private static JsonNode Number(double v) => JsonValue.Create(v)!;

    private static JsonObject Record(M2Model m, M2Data arr, int i)
    {
        var rec = arr.Rec;
        var fields = new JsonObject();
        foreach (var pf in rec.Plain)
        {
            var c = TrackValues.Components(pf.Type, arr.Bytes.AsSpan(i * rec.Size + pf.Offset, pf.Size).ToArray());
            fields[pf.Name] = c.Length == 1 ? Number(c[0]) : new JsonArray(c.Select(x => Number(x)).ToArray());
        }
        var lists = new JsonObject();
        foreach (var a in rec.Arrays)
        {
            if (a.Name.Contains('.')) continue;     // track blocks
            var d = arr.Child(i, a.Index);
            if (a.Elem.IsChar) lists[a.Name] = d.AsString();
            else
            {
                var l = new JsonArray();
                for (int k = 0; k < d.Count; k++)
                {
                    var c = TrackValues.Components(a.Elem.Type, d.Bytes.AsSpan(k * a.Elem.Size, a.Elem.Size).ToArray());
                    l.Add(c.Length == 1 ? Number(c[0]) : new JsonArray(c.Select(x => Number(x)).ToArray()));
                }
                lists[a.Name] = l;
            }
        }
        var tracks = new JsonObject();
        foreach (var tf in rec.Tracks)
        {
            var raw = RawTrack.Read(new M2TrackView(arr, i, tf));
            tracks[tf.Name] = TrackJson(m, raw);
        }
        return new JsonObject { ["index"] = i, ["fields"] = fields, ["lists"] = lists, ["tracks"] = tracks };
    }

    private static JsonArray Key(uint t, string? type, byte[]? v)
    {
        var a = new JsonArray(Number(t));
        if (type != null && v != null) foreach (var c in TrackValues.Components(type, v)) a.Add(Number(c));
        return a;
    }

    private static JsonObject TrackJson(M2Model m, RawTrack raw)
    {
        var o = new JsonObject { ["interpolation"] = raw.Interpolation, ["global_sequence"] = raw.GlobalSequence };
        if (raw.GlobalSequence >= 0 || raw.Ranges.Count == 0)
        {
            var keys = new JsonArray();
            for (int k = 0; k < raw.KeyCount; k++) keys.Add(Key(raw.Times[k], raw.ValueType, raw.Values?[k]));
            o["keys"] = keys;
            return o;
        }
        var seqs = new JsonArray();
        for (int s = 0; s < m.Sequences.Length; s++)
        {
            var sq = m.Sequences[s];
            var w = raw.Window(s, sq.Start, sq.End);
            var keys = new JsonArray();
            for (int k = 0; k < w.Count; k++) keys.Add(Key(w.Times[k], raw.ValueType, w.Values?[k]));
            seqs.Add(new JsonObject { ["sequence"] = s, ["keys"] = keys });
        }
        o["sequences"] = seqs;
        return o;
    }

    // ================================================================= import

    /// <summary>Compare an edited file with the model it was exported from and prepare the writes.</summary>
    public static EffectsEdit Analyze(M2Model m, string text)
    {
        var e = new EffectsEdit();
        JsonObject root;
        try { root = JsonNode.Parse(text)!.AsObject(); }
        catch (Exception x) when (x is JsonException or InvalidOperationException)
        {
            e.Problems.Add(new(RuleLevel.Blocker, $"{FileName} could not be read as JSON: {x.Message} (a missing comma or bracket?)"));
            return e;
        }
        if ((string?)root["format"] != FormatName)
        {
            e.Problems.Add(new(RuleLevel.Blocker, $"{FileName} is not the effects file of a WoW Data Studio export."));
            return e;
        }
        var f = m.File;
        int ntex = m.Textures.Length, nrf = m.RenderFlags.Length, nb = m.Bones.Length, ngs = m.GlobalSequences.Length;
        foreach (var an in Arrays)
        {
            string what = an == "particles" ? "particle emitter" : "ribbon";
            var arr = f[an];
            var list = root[an] as JsonArray ?? new JsonArray();
            if (list.Count != arr.Count)
            {
                e.Problems.Add(new(RuleLevel.Blocker, $"The model has {arr.Count} {what}s, the file has {list.Count}. Adding or removing {what}s is not supported: change values only."));
                continue;
            }
            for (int i = 0; i < arr.Count; i++)
            {
                if (list[i] is not JsonObject r) { e.Problems.Add(new(RuleLevel.Blocker, $"{what} {i}: not an object.")); continue; }
                Compare(m, arr, i, r, $"{what} {i}", e, ntex, nrf, nb, ngs);
            }
        }
        return e;
    }

    private static void Compare(M2Model m, M2Data arr, int i, JsonObject r, string who, EffectsEdit e, int ntex, int nrf, int nb, int ngs)
    {
        var rec = arr.Rec;
        int problems = e.Problems.Count;
        var fields = r["fields"] as JsonObject ?? new JsonObject();
        var newBytes = arr.Bytes.AsSpan(i * rec.Size, rec.Size).ToArray();
        var changedFields = new List<string>();
        foreach (var kv in fields)
        {
            if (!rec.HasPlain(kv.Key)) { e.Problems.Add(new(RuleLevel.Warning, $"{who}: \"{kv.Key}\" is not a field of a {rec.Name}; it is ignored.")); continue; }
            var pf = rec.Field(kv.Key);
            var (n, _, kind) = M2Elem.Shape(pf.Type);
            var vals = Numbers(kv.Value);
            if (vals == null || vals.Count != n) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: {kv.Key} needs {(n == 1 ? "a number" : $"{n} numbers")}.")); continue; }
            var bad = vals.FirstOrDefault(v => !InRange(pf.Type, v));
            if (vals.Any(v => !InRange(pf.Type, v))) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: {kv.Key} = {bad.ToString(CultureInfo.InvariantCulture)} is outside what the field can hold ({RangeText(pf.Type)}).")); continue; }
            var b = TrackValues.FromComponents(pf.Type, vals);
            var old = newBytes.AsSpan(pf.Offset, pf.Size);
            if (!old.SequenceEqual(b))
            {
                changedFields.Add($"{kv.Key} {Fmt(TrackValues.Components(pf.Type, old.ToArray()))} -> {Fmt(vals)}");
                Buffer.BlockCopy(b, 0, newBytes, pf.Offset, pf.Size);
            }
        }
        // the numbers the game looks up
        double Field(string name) => TrackValues.Components(rec.Field(name).Type, newBytes.AsSpan(rec.Field(name).Offset, rec.Field(name).Size).ToArray())[0];
        if (rec.HasPlain("bone") && Field("bone") >= Math.Max(nb, 1)) e.Problems.Add(new(RuleLevel.Blocker, $"{who}: bone {Field("bone")} does not exist (the model has {nb} bones)."));
        if (rec.Name == "M2Particle")
        {
            if (Field("texture") >= Math.Max(ntex, 1)) e.Problems.Add(new(RuleLevel.Blocker, $"{who}: texture {Field("texture")} does not exist (the model has {ntex} textures)."));
            if (Field("blend") is < 1 or > 5) e.Problems.Add(new(RuleLevel.Warning, $"{who}: blend mode {Field("blend")} is unusual (the game's 7,860 emitters use 1-5)."));
            if (Field("emitter_type") is < 1 or > 3) e.Problems.Add(new(RuleLevel.Warning, $"{who}: emitter type {Field("emitter_type")} is unusual (the game's emitters use 1 = plane, 2 = sphere, 3 = spline)."));
            if (Field("particle_type") > 2) e.Problems.Add(new(RuleLevel.Warning, $"{who}: particle type {Field("particle_type")} is unusual (the game's emitters use 0-2)."));
            if (Field("texture_rows") < 1 || Field("texture_cols") < 1) e.Problems.Add(new(RuleLevel.Blocker, $"{who}: texture_rows and texture_cols must be at least 1."));
        }
        if (rec.Name == "M2Ribbon" && (Field("texture_rows") < 1 || Field("texture_cols") < 1))
            e.Problems.Add(new(RuleLevel.Blocker, $"{who}: texture_rows and texture_cols must be at least 1."));
        // lists
        var lists = r["lists"] as JsonObject ?? new JsonObject();
        var listWrites = new List<(int Index, M2Data Data)>();
        foreach (var a in rec.Arrays)
        {
            if (a.Name.Contains('.') || !lists.ContainsKey(a.Name)) continue;
            var orig = arr.Child(i, a.Index);
            var node = lists[a.Name];
            if (a.Elem.IsChar)
            {
                var s = (string?)node ?? "";
                if (s == orig.AsString()) continue;
                if (s.Length > 0 && !s.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) && !s.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
                    e.Problems.Add(new(RuleLevel.Warning, $"{who}: {a.Name} \"{s}\" is not a model file name (.mdx / .m2)."));
                var bytes = s.Length == 0 ? Array.Empty<byte>() : Encoding.Latin1.GetBytes(s + "\0");
                listWrites.Add((a.Index, bytes.Length == 0 ? M2Data.Empty(a.Elem) : new M2Data(a.Elem, bytes.Length, bytes, null, -1)));
                changedFields.Add($"{a.Name} \"{orig.AsString()}\" -> \"{s}\"");
                continue;
            }
            if (node is not JsonArray la) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: {a.Name} must be a list.")); continue; }
            var nb2 = new List<byte>();
            bool okList = true;
            foreach (var item in la)
            {
                var vals = Numbers(item);
                int comps = M2Elem.Shape(a.Elem.Type).Count;
                if (vals == null || vals.Count != comps || vals.Any(v => !InRange(a.Elem.Type, v))) { okList = false; break; }
                nb2.AddRange(TrackValues.FromComponents(a.Elem.Type, vals));
            }
            if (!okList) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: {a.Name} has an entry that is not valid.")); continue; }
            var arrBytes = nb2.ToArray();
            if (arrBytes.AsSpan().SequenceEqual(orig.Bytes)) continue;
            int cnt = arrBytes.Length / a.Elem.Size;
            if (rec.Name == "M2Ribbon" && a.Name == "textures" && Enumerable.Range(0, cnt).Any(k => BitConverter.ToUInt16(arrBytes, k * 2) >= ntex))
                e.Problems.Add(new(RuleLevel.Blocker, $"{who}: a texture number in textures does not exist (the model has {ntex} textures)."));
            if (rec.Name == "M2Ribbon" && a.Name == "materials" && Enumerable.Range(0, cnt).Any(k => BitConverter.ToUInt16(arrBytes, k * 2) >= nrf))
                e.Problems.Add(new(RuleLevel.Blocker, $"{who}: a material number in materials does not exist (the model has {nrf} materials / render flags)."));
            listWrites.Add((a.Index, cnt == 0 ? M2Data.Empty(a.Elem) : new M2Data(a.Elem, cnt, arrBytes, null, -1)));
            changedFields.Add($"{a.Name} ({cnt} entries)");
        }
        // tracks
        var tracks = r["tracks"] as JsonObject ?? new JsonObject();
        var trackWrites = new List<(M2TrackField Field, RawTrack Track)>();
        var starts = m.Sequences.Select(s => s.Start).ToArray();
        var durs = m.Sequences.Select(s => s.Duration).ToArray();
        foreach (var tf in rec.Tracks)
        {
            if (tracks[tf.Name] is not JsonObject tj) continue;
            var raw = RawTrack.Read(new M2TrackView(arr, i, tf));
            var t = ReadTrack(m, raw, tj, $"{who}: {tf.Name}", e, ngs, starts, durs);
            if (t != null && !t.SameAs(raw))
            {
                trackWrites.Add((tf, t));
                changedFields.Add($"{tf.Name} (animated value)");
            }
        }
        if (e.Problems.Count > problems && e.Problems.Skip(problems).Any(p => p.Level == RuleLevel.Blocker)) return;
        if (changedFields.Count == 0) return;
        e.Changes.Add($"{who}: " + string.Join(", ", changedFields));
        string name = arr.Rec.Name == "M2Particle" ? "particles" : "ribbons";
        e.Writes.Add(file =>
        {
            var d = file[name];
            // keep the array fields' original count / offset words: the writer recomputes them
            Buffer.BlockCopy(newBytes, 0, d.Bytes, i * rec.Size, rec.Size);
            foreach (var (idx, data) in listWrites) d.Sub[i][idx] = data;
            foreach (var (tf, t) in trackWrites) t.Write(d, i, tf);
        });
    }

    private static RawTrack? ReadTrack(M2Model m, RawTrack raw, JsonObject tj, string who, EffectsEdit e, int ngs, uint[] starts, uint[] durs)
    {
        int interp = (int?)(double?)tj["interpolation"] ?? raw.Interpolation;
        int gs = (int?)(double?)tj["global_sequence"] ?? raw.GlobalSequence;
        if (interp is < 0 or > 3) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: interpolation {interp} (0 = step, 1 = linear; 2 / 3 only in camera tracks).")); return null; }
        if (interp >= 2 && raw.Interpolation < 2) e.Problems.Add(new(RuleLevel.Warning, $"{who}: interpolation {interp} (hermite / bezier) is played as linear by the 1.12 client outside cameras."));
        if (gs != -1 && (gs < 0 || gs >= ngs)) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: global sequence {gs} does not exist (the model has {ngs}).")); return null; }
        if ((gs >= 0) != (raw.GlobalSequence >= 0) || (gs < 0 && (tj["sequences"] != null) != (raw.Ranges.Count > 0)))
        {
            e.Problems.Add(new(RuleLevel.Blocker, $"{who}: changing whether a value runs per sequence, on a global sequence or the same everywhere is not supported; keep \"global_sequence\" and the \"sequences\" / \"keys\" form."));
            return null;
        }
        int comps = raw.ValueType == null ? 0 : M2Elem.Shape(raw.ValueType).Count;
        SeqKeys? Keys(JsonNode? node, uint maxTime, string where)
        {
            var k = new SeqKeys(raw.HasValues);
            if (node is not JsonArray a) { e.Problems.Add(new(RuleLevel.Blocker, $"{where}: keys must be a list of [time, value...].")); return null; }
            long last = -1;
            foreach (var item in a)
            {
                var vals = Numbers(item);
                if (vals == null || vals.Count != 1 + comps) { e.Problems.Add(new(RuleLevel.Blocker, $"{where}: every key is [time in ms{(comps > 0 ? $", {comps} value{(comps > 1 ? "s" : "")}" : "")}].")); return null; }
                if (vals[0] < 0 || vals[0] > maxTime || vals[0] != Math.Floor(vals[0])) { e.Problems.Add(new(RuleLevel.Blocker, $"{where}: key time {vals[0]} is not a whole number of ms within 0 .. {maxTime}.")); return null; }
                if (vals[0] <= last) { e.Problems.Add(new(RuleLevel.Blocker, $"{where}: key times must increase ({vals[0]} after {last}).")); return null; }
                last = (long)vals[0];
                byte[]? v = null;
                if (raw.HasValues)
                {
                    var c = vals.Skip(1).ToList();
                    if (c.Any(x => !InRange(raw.ValueType!, x))) { e.Problems.Add(new(RuleLevel.Blocker, $"{where}: a value is outside what the field can hold ({RangeText(raw.ValueType!)}).")); return null; }
                    v = TrackValues.FromComponents(raw.ValueType!, c);
                }
                k.Add((uint)vals[0], v);
            }
            if (raw.HasValues && k.Count == 0 && tj["sequences"] != null) { e.Problems.Add(new(RuleLevel.Blocker, $"{where}: every sequence needs at least one key.")); return null; }
            return k;
        }
        if (gs >= 0 || raw.Ranges.Count == 0)
        {
            var k = Keys(tj["keys"], gs >= 0 ? m.GlobalSequences[gs] : uint.MaxValue, who);
            if (k == null) return null;
            var t = raw.EmptyLike();
            t.Interpolation = (ushort)interp;
            t.GlobalSequence = (short)gs;
            for (int q = 0; q < k.Count; q++)
            {
                t.Times.Add(k.Times[q]);
                t.Values?.Add(k.Values![q]);
            }
            return t;
        }
        var seqs = tj["sequences"] as JsonArray ?? new JsonArray();
        if (seqs.Count != m.Sequences.Length) { e.Problems.Add(new(RuleLevel.Blocker, $"{who}: {seqs.Count} sequences in the file, the model has {m.Sequences.Length}.")); return null; }
        var wins = new List<SeqKeys>();
        bool changed = interp != raw.Interpolation;
        for (int s = 0; s < seqs.Count; s++)
        {
            var sq = m.Sequences[s];
            var orig = raw.Window(s, sq.Start, sq.End);
            var k = Keys(seqs[s]?["keys"], sq.Duration, $"{who}, sequence {s}");
            if (k == null) return null;
            if (!k.SameAs(orig)) changed = true;
            wins.Add(k);
        }
        if (!changed) return raw;
        var like = raw.EmptyLike();
        like.Interpolation = (ushort)interp;
        like.Ranges.Add((0, 0));
        return TrackEncoder.Fresh(like, wins, starts, durs, raw.HasValues && raw.KeyCount > 0 ? raw.Values![0] : null);
    }

    private static List<double>? Numbers(JsonNode? n)
    {
        if (n is JsonValue v) return v.TryGetValue(out double d) ? new List<double> { d } : null;
        if (n is JsonArray a)
        {
            var l = new List<double>();
            foreach (var x in a)
            {
                if (x is not JsonValue xv || !xv.TryGetValue(out double d)) return null;
                l.Add(d);
            }
            return l;
        }
        return null;
    }

    private static bool InRange(string type, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        var (_, size, kind) = M2Elem.Shape(type);
        return (kind, size) switch
        {
            ('f', 4) => Math.Abs(v) <= float.MaxValue,
            ('u', 1) => v >= 0 && v <= 255 && v == Math.Floor(v),
            ('i', 1) => v >= -128 && v <= 127 && v == Math.Floor(v),
            ('u', 2) => v >= 0 && v <= 65535 && v == Math.Floor(v),
            ('i', 2) => v >= -32768 && v <= 32767 && v == Math.Floor(v),
            ('u', 4) => v >= 0 && v <= uint.MaxValue && v == Math.Floor(v),
            ('i', 4) => v >= int.MinValue && v <= int.MaxValue && v == Math.Floor(v),
            _ => true,
        };
    }

    private static string RangeText(string type)
    {
        var (_, size, kind) = M2Elem.Shape(type);
        return (kind, size) switch
        {
            ('f', 4) => "a number",
            ('u', 1) => "a whole number 0-255",
            ('i', 1) => "a whole number -128-127",
            ('u', 2) => "a whole number 0-65535",
            ('i', 2) => "a whole number -32768-32767",
            ('u', 4) => "a whole number 0-4294967295",
            _ => "a whole number",
        };
    }

    private static string Fmt(IEnumerable<double> v)
    {
        var l = v.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture)).ToList();
        return l.Count == 1 ? l[0] : "[" + string.Join(", ", l) + "]";
    }
}
