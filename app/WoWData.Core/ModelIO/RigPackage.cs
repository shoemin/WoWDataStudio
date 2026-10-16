using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WoWData.Core.M2;

namespace WoWData.Core.ModelIO;

// ======================================================================================= the export's rig description

/// <summary>0.10.0: the skeleton and animation part of an export package (manifest "rig"), written by "Export for
/// Blender" with "Skeleton and animations": what the Blender add-on needs to name and tag the actions, markers and
/// empties, and what the import compares the edited file with.</summary>
public sealed class PackageRig
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("bones")] public List<PackageRigBone> Bones { get; set; } = new();
    [JsonPropertyName("sequences")] public List<PackageSequence> Sequences { get; set; } = new();
    [JsonPropertyName("global_sequences")] public List<PackageGlobalSequence> GlobalSequences { get; set; } = new();
    [JsonPropertyName("attachments")] public List<PackageAttachment> Attachments { get; set; } = new();
    [JsonPropertyName("events")] public List<PackageEvent> Events { get; set; } = new();
    /// <summary>AnimationData id -> name (the add-on's animation list).</summary>
    [JsonPropertyName("animation_names")] public Dictionary<string, string> AnimationNames { get; set; } = new();
    [JsonPropertyName("key_bones")] public List<string> KeyBones { get; set; } = new();
    [JsonPropertyName("attachment_names")] public List<string> AttachmentNames { get; set; } = new();
    /// <summary>The particles and ribbons as JSON (advanced editing), or null when the model has none.</summary>
    [JsonPropertyName("effects_file")] public string? EffectsFile { get; set; }
    [JsonPropertyName("effects_sha256")] public string? EffectsSha256 { get; set; }
    [JsonPropertyName("rig_sidecar")] public string RigSidecar { get; set; } = RigSidecarFile.FileName;
}

public sealed class PackageRigBone
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("parent")] public int Parent { get; set; } = -1;
    [JsonPropertyName("key_bone")] public int KeyBone { get; set; } = -1;
    [JsonPropertyName("flags")] public uint Flags { get; set; }
    [JsonPropertyName("pivot")] public float[] Pivot { get; set; } = new float[3];
    [JsonPropertyName("vertices")] public int Vertices { get; set; }
    /// <summary>Channels that run on a global sequence: channel -> global sequence index.</summary>
    [JsonPropertyName("global_channels")] public Dictionary<string, int> GlobalChannels { get; set; } = new();
}

public sealed class PackageSequence
{
    [JsonPropertyName("index")] public int Index { get; set; }
    /// <summary>The name of the glTF animation / Blender action ("Stand", "Stand [2]").</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("anim_id")] public int AnimId { get; set; }
    [JsonPropertyName("anim_name")] public string AnimName { get; set; } = "";
    [JsonPropertyName("sub_id")] public int SubId { get; set; }
    [JsonPropertyName("variation")] public int Variation { get; set; }
    [JsonPropertyName("duration_ms")] public uint DurationMs { get; set; }
    [JsonPropertyName("loop")] public bool Loop { get; set; }
    [JsonPropertyName("flags")] public uint Flags { get; set; }
    [JsonPropertyName("frequency")] public int Frequency { get; set; }
    [JsonPropertyName("chance")] public double Chance { get; set; }
    [JsonPropertyName("blend_ms")] public uint BlendMs { get; set; }
    [JsonPropertyName("move_speed")] public float MoveSpeed { get; set; }
    [JsonPropertyName("repeat_min")] public uint RepeatMin { get; set; }
    [JsonPropertyName("repeat_max")] public uint RepeatMax { get; set; }
    [JsonPropertyName("next_alias")] public int NextAlias { get; set; }
    /// <summary>What the game uses it for, in words (from the client's own animation tests).</summary>
    [JsonPropertyName("used_for")] public List<string> UsedFor { get; set; } = new();
}

public sealed class PackageGlobalSequence
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("duration_ms")] public uint DurationMs { get; set; }
}

public sealed class PackageAttachment
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("bone")] public int Bone { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; } = new float[3];
}

public sealed class PackageEvent
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    /// <summary>The name of its timeline markers in Blender ("$FSD", or "$FSD#2" for a second event with that code).</summary>
    [JsonPropertyName("marker")] public string Marker { get; set; } = "";
    [JsonPropertyName("meaning")] public string Meaning { get; set; } = "";
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("bone")] public int Bone { get; set; }
    [JsonPropertyName("data")] public uint Data { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; } = new float[3];
    /// <summary>(sequence index, time in ms from its start) of every trigger.</summary>
    [JsonPropertyName("triggers")] public List<int[]> Triggers { get; set; } = new();
}

// ======================================================================================= what came back from Blender

/// <summary>A bone of the edited armature, in M2 coordinates.</summary>
public sealed class EditedBone
{
    public string Name { get; set; } = "";
    /// <summary>The original bone index (the custom property wow_bone), null for a bone you added.</summary>
    public int? WowBone { get; set; }
    public string? Parent { get; set; }
    /// <summary>The bone's head (its pivot) in M2 coordinates.</summary>
    public Vector3 Head { get; set; }
    public int? KeyBoneId { get; set; }
    public uint? Flags { get; set; }
}

/// <summary>Sampled local transforms of one bone over an action: the bone's animation matrix relative to its parent
/// (the client's T(pivot) T R S T(-pivot)), in System.Numerics row-vector form, M2 coordinates.</summary>
public sealed class BoneSamples
{
    public List<double> TimesMs { get; } = new();
    public List<Matrix4x4> Local { get; } = new();
    /// <summary>Channels that have keys in Blender ("translation", "rotation", "scale"); the others are the rest pose.</summary>
    public HashSet<string> Channels { get; } = new();
}

/// <summary>An action (one animation) of the edited file.</summary>
public sealed class EditedAction
{
    public string Name { get; set; } = "";
    /// <summary>The original sequence (custom property wow_sequence; a copied action keeps its source's).</summary>
    public int? WowSequence { get; set; }
    /// <summary>The add-on found the action's keys exactly as imported (nothing was sampled).</summary>
    public bool Unchanged { get; set; }
    public double DurationMs { get; set; }
    public Dictionary<string, JsonNode?> Props { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BoneSamples> Bones { get; } = new(StringComparer.Ordinal);
    public List<(string Marker, double TimeMs)> Markers { get; } = new();
    /// <summary>For a global sequence's action: its index.</summary>
    public int? GlobalSequence { get; set; }

    public int? IntProp(string k) => Props.TryGetValue(k, out var n) && n is JsonValue v && v.TryGetValue(out double d) ? (int)Math.Round(d) : Props.TryGetValue(k, out n) && n is JsonValue v2 && v2.TryGetValue(out bool b) ? (b ? 1 : 0) : null;
    public double? DoubleProp(string k) => Props.TryGetValue(k, out var n) && n is JsonValue v && v.TryGetValue(out double d) ? d : null;
    public bool? BoolProp(string k) => Props.TryGetValue(k, out var n) && n is JsonValue v ? v.TryGetValue(out bool b) ? b : v.TryGetValue(out double d) ? d != 0 : null : null;
}

/// <summary>An attachment point or event empty of the edited file.</summary>
public sealed class EditedPoint
{
    public string Name { get; set; } = "";
    /// <summary>"attachment" or "event".</summary>
    public string Kind { get; set; } = "attachment";
    public int? WowIndex { get; set; }
    public int? Id { get; set; }
    public string? Code { get; set; }
    public string? Bone { get; set; }
    /// <summary>The rest position in model space (M2 coordinates).</summary>
    public Vector3 Position { get; set; }
}

/// <summary>The skeleton, animations, attachment points and events that came back (from the add-on's sidecar, or read
/// from the glTF itself).</summary>
public sealed class EditedRig
{
    public string Source { get; set; } = "";
    public double Fps { get; set; } = 24;
    public List<EditedBone> Bones { get; } = new();
    public List<EditedAction> Actions { get; } = new();
    public List<EditedAction> Globals { get; } = new();
    public List<EditedPoint> Points { get; } = new();
    public string? EffectsJson { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// wowdata_rig.json: written by the Blender add-on (0.5.0) next to its glTF on "Export for WoW Data Studio" when the
/// package has a rig. Everything is in M2 coordinates (Blender's armature space with the add-on's import rotation and
/// scale undone). Matrices are 16 numbers, row by row, in Blender's column-vector form (M @ v).
/// </summary>
public static class RigSidecarFile
{
    public const string FileName = "wowdata_rig.json";
    public const string FormatName = "wowdata-rig";

    public static EditedRig Read(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new GltfException($"{Path.GetFileName(path)} is empty");
        if ((string?)root["format"] != FormatName) throw new GltfException($"{Path.GetFileName(path)} is not a WoW Data Studio rig file");
        var rig = new EditedRig { Source = $"the Blender add-on {(string?)root["addon"] ?? "?"} ({Path.GetFileName(path)})", Fps = (double?)root["fps"] ?? 24 };
        foreach (var b in root["bones"]?.AsArray() ?? new JsonArray())
        {
            var o = b!.AsObject();
            rig.Bones.Add(new EditedBone
            {
                Name = (string?)o["name"] ?? "", WowBone = IntOrNull(o["wow_bone"]), Parent = (string?)o["parent"], Head = Vec(o["head"]),
                KeyBoneId = IntOrNull(o["key_bone_id"]), Flags = IntOrNull(o["flags"]) is int fl ? (uint)fl : null,
            });
        }
        foreach (var (list, key) in new[] { (rig.Actions, "actions"), (rig.Globals, "globals") })
            foreach (var a in root[key]?.AsArray() ?? new JsonArray())
                list.Add(ReadAction(a!.AsObject()));
        foreach (var p in root["points"]?.AsArray() ?? new JsonArray())
        {
            var o = p!.AsObject();
            rig.Points.Add(new EditedPoint
            {
                Name = (string?)o["name"] ?? "", Kind = (string?)o["kind"] ?? "attachment", WowIndex = IntOrNull(o["wow_index"]), Id = IntOrNull(o["id"]),
                Code = (string?)o["code"], Bone = (string?)o["bone"], Position = Vec(o["position"]),
            });
        }
        foreach (var n in root["notes"]?.AsArray() ?? new JsonArray()) if ((string?)n is { } s) rig.Notes.Add(s);
        return rig;
    }

    private static EditedAction ReadAction(JsonObject o)
    {
        var a = new EditedAction
        {
            Name = (string?)o["name"] ?? "", WowSequence = IntOrNull(o["wow_sequence"]), Unchanged = (bool?)o["unchanged"] ?? false,
            DurationMs = (double?)o["duration_ms"] ?? 0, GlobalSequence = IntOrNull(o["global_sequence"]),
        };
        if (o["props"] is JsonObject props)
            foreach (var kv in props) a.Props[kv.Key] = kv.Value?.DeepClone();
        foreach (var m in o["markers"]?.AsArray() ?? new JsonArray())
            a.Markers.Add(((string?)m!["name"] ?? "", (double?)m["time_ms"] ?? 0));
        if (o["bones"] is JsonObject bones)
            foreach (var kv in bones)
            {
                var bs = new BoneSamples();
                var bo = kv.Value!.AsObject();
                foreach (var t in bo["times_ms"]?.AsArray() ?? new JsonArray()) bs.TimesMs.Add((double)t!);
                foreach (var mm in bo["matrices"]?.AsArray() ?? new JsonArray())
                {
                    var f = mm!.AsArray().Select(x => (float)(double)x!).ToArray();
                    // Blender's column-vector matrix, row by row -> System.Numerics row-vector form (the transpose)
                    bs.Local.Add(new Matrix4x4(f[0], f[4], f[8], f[12], f[1], f[5], f[9], f[13], f[2], f[6], f[10], f[14], f[3], f[7], f[11], f[15]));
                }
                foreach (var c in bo["channels"]?.AsArray() ?? new JsonArray()) if ((string?)c is { } cs) bs.Channels.Add(cs);
                a.Bones[kv.Key] = bs;
            }
        return a;
    }

    /// <summary>Write a rig in the add-on's format (scripted edits without Blender: tests, the manual's pictures).</summary>
    public static void Write(EditedRig rig, string path, string addon = "WoW Data Studio")
    {
        static JsonArray V(Vector3 v) => new(v.X, v.Y, v.Z);
        JsonObject Act(EditedAction a)
        {
            var o = new JsonObject { ["name"] = a.Name, ["unchanged"] = a.Unchanged, ["duration_ms"] = a.DurationMs };
            if (a.WowSequence is int ws) o["wow_sequence"] = ws;
            if (a.GlobalSequence is int gs) o["global_sequence"] = gs;
            var props = new JsonObject();
            foreach (var kv in a.Props) props[kv.Key] = kv.Value?.DeepClone();
            o["props"] = props;
            o["markers"] = new JsonArray(a.Markers.Select(mk => (JsonNode)new JsonObject { ["name"] = mk.Marker, ["time_ms"] = mk.TimeMs }).ToArray());
            var bones = new JsonObject();
            foreach (var (name, bs) in a.Bones)
            {
                // System.Numerics row-vector -> Blender's column-vector form, row by row
                var mats = new JsonArray(bs.Local.Select(m => (JsonNode)new JsonArray(
                    m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43, m.M14, m.M24, m.M34, m.M44)).ToArray());
                bones[name] = new JsonObject
                {
                    ["times_ms"] = new JsonArray(bs.TimesMs.Select(t => (JsonNode)t).ToArray()), ["matrices"] = mats,
                    ["channels"] = new JsonArray(bs.Channels.Select(c => (JsonNode)c).ToArray()),
                };
            }
            o["bones"] = bones;
            return o;
        }
        var root = new JsonObject
        {
            ["format"] = FormatName, ["format_version"] = 1, ["addon"] = addon, ["fps"] = rig.Fps,
            ["bones"] = new JsonArray(rig.Bones.Select(b =>
            {
                var o = new JsonObject { ["name"] = b.Name, ["parent"] = b.Parent, ["head"] = V(b.Head) };
                if (b.WowBone is int w) o["wow_bone"] = w;
                if (b.KeyBoneId is int k) o["key_bone_id"] = k;
                if (b.Flags is uint f) o["flags"] = f;
                return (JsonNode)o;
            }).ToArray()),
            ["actions"] = new JsonArray(rig.Actions.Select(a => (JsonNode)Act(a)).ToArray()),
            ["globals"] = new JsonArray(rig.Globals.Select(a => (JsonNode)Act(a)).ToArray()),
            ["points"] = new JsonArray(rig.Points.Select(p =>
            {
                var o = new JsonObject { ["name"] = p.Name, ["kind"] = p.Kind, ["bone"] = p.Bone, ["position"] = V(p.Position) };
                if (p.WowIndex is int w) o["wow_index"] = w;
                if (p.Id is int id) o["id"] = id;
                if (p.Code != null) o["code"] = p.Code;
                return (JsonNode)o;
            }).ToArray()),
        };
        File.WriteAllText(path, root.ToJsonString());
    }

    public static int? IntOrNull(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? (int)Math.Round(d) : null;

    public static Vector3 Vec(JsonNode? n) => n is JsonArray a && a.Count >= 3 ? new Vector3((float)(double)a[0]!, (float)(double)a[1]!, (float)(double)a[2]!) : Vector3.Zero;
}

/// <summary>0.10.0: builds the manifest's rig description from a model.</summary>
public static class RigExport
{
    public static PackageRig Describe(M2Model m, AnimationCatalog? catalog, Func<int, string> animName)
    {
        catalog ??= AnimationCatalog.Empty;
        string Name(int id) => catalog.Rows.Count > 0 ? catalog.Name(id) : animName(id);
        var info = RigInfo.Read(m, catalog.Rows.Count > 0 ? catalog : null);
        var actions = ModelExporter.ActionNames(m, Name);
        var rig = new PackageRig
        {
            KeyBones = M2Schema.KeyBones.ToList(),
            AttachmentNames = M2Schema.AttachmentNames.ToList(),
        };
        if (catalog.Rows.Count > 0)
            foreach (var r in catalog.Rows.OrderBy(r => r.Id)) rig.AnimationNames[r.Id.ToString()] = r.Name;
        foreach (var b in info.Bones)
            rig.Bones.Add(new PackageRigBone
            {
                Index = b.Index, Name = b.Name, Parent = b.Parent, KeyBone = b.KeyBoneId, Flags = b.Flags, Pivot = new[] { b.Pivot.X, b.Pivot.Y, b.Pivot.Z },
                Vertices = b.Vertices, GlobalChannels = new Dictionary<string, int>(b.GlobalChannels),
            });
        foreach (var s in info.Sequences)
            rig.Sequences.Add(new PackageSequence
            {
                Index = s.Index, Action = actions[s.Index], AnimId = s.AnimId, AnimName = Name(s.AnimId), SubId = s.SubId, Variation = s.Variation,
                DurationMs = s.Duration, Loop = s.Loops, Flags = s.Flags, Frequency = s.Frequency, Chance = Math.Round(s.ChancePercent, 2), BlendMs = s.BlendTime,
                MoveSpeed = s.MoveSpeed, RepeatMin = s.ReplayMin, RepeatMax = s.ReplayMax, NextAlias = s.NextAlias, UsedFor = AnimationRoles.Of(s.AnimId),
            });
        for (int g = 0; g < info.GlobalSequences.Count; g++)
            rig.GlobalSequences.Add(new PackageGlobalSequence { Index = g, DurationMs = info.GlobalSequences[g], Action = GlobalActionName(g, info.GlobalSequences[g]) });
        foreach (var a in info.Attachments)
            rig.Attachments.Add(new PackageAttachment { Index = a.Index, Id = a.Id, Name = a.Name, Node = $"ATT_{a.Id:00}_{a.Name}", Bone = a.Bone, Position = new[] { a.Position.X, a.Position.Y, a.Position.Z } });
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in info.Events)
        {
            int k = seen.GetValueOrDefault(e.Code) + 1;
            seen[e.Code] = k;
            rig.Events.Add(new PackageEvent
            {
                Index = e.Index, Code = e.Code, Marker = k == 1 ? e.Code : $"{e.Code}#{k}", Meaning = e.Meaning, Node = $"EVT_{e.Index:00}_{e.Code}",
                Bone = e.Bone, Data = e.Data, Position = new[] { e.Position.X, e.Position.Y, e.Position.Z },
                Triggers = e.Triggers.Select(t => new[] { t.Sequence, (int)t.Time }).ToList(),
            });
        }
        return rig;
    }

    public static string GlobalActionName(int index, uint ms) => FormattableString.Invariant($"Global sequence {index + 1} ({ms / 1000.0:0.00} s)");
}
