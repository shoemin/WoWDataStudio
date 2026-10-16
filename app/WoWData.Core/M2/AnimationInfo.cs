using WoWData.Core.Dbc;

namespace WoWData.Core.M2;

/// <summary>One row of AnimationData.dbc as the client uses it for models (M2Model_ResolvePlayableAnimation @00711bf0
/// reads the record's +0x14 as flags and +0x18 as the fallback id: columns 5 and 6 of the file).</summary>
public sealed record AnimationDataRow(int Id, string Name, uint Flags, int Fallback)
{
    /// <summary>Flag 0x10: the fallback is played backwards (Walkbackwards = Walk played backwards).</summary>
    public bool Backwards => (Flags & 0x10) != 0;
    /// <summary>Flag 0x20: the fallback is held (frozen) instead of played (Dead = Death held on its last frame).</summary>
    public bool Hold => (Flags & 0x20) != 0;
}

/// <summary>
/// 0.10.0: the animation names and fallbacks of the game (AnimationData.dbc), the client's own algorithm for the
/// "playable animation" table of a model, and what the game uses each animation for.
/// </summary>
public sealed class AnimationCatalog
{
    /// <summary>The size of the playable-animation table in every 1.12 model (all 9,691 client models).</summary>
    public const int PlayableCount = 203;

    private readonly Dictionary<int, AnimationDataRow> _rows;
    private readonly Dictionary<string, int> _byName;

    public IReadOnlyCollection<AnimationDataRow> Rows => _rows.Values;
    public int MaxId { get; }

    public AnimationCatalog(IEnumerable<AnimationDataRow> rows)
    {
        _rows = new Dictionary<int, AnimationDataRow>();
        foreach (var r in rows) _rows[r.Id] = r;
        _byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows.Values.OrderBy(r => r.Id))
            if (r.Name.Length > 0 && !_byName.ContainsKey(r.Name)) _byName[r.Name] = r.Id;
        MaxId = _rows.Count == 0 ? 0 : _rows.Keys.Max();
    }

    /// <summary>The catalog from AnimationData.dbc (7 fields: id, name, 3 columns, flags, fallback).</summary>
    public static AnimationCatalog FromDbc(DbcFile d)
    {
        var rows = new List<AnimationDataRow>(d.Count);
        for (int i = 0; i < d.Count; i++)
            rows.Add(new AnimationDataRow(d.Int(i, 0), d.String(i, 1), d.FieldCount > 5 ? (uint)d.Int(i, 5) : 0, d.FieldCount > 6 ? d.Int(i, 6) : 0));
        return new AnimationCatalog(rows);
    }

    public static readonly AnimationCatalog Empty = new(Array.Empty<AnimationDataRow>());

    public AnimationDataRow? Row(int id) => _rows.GetValueOrDefault(id);
    public bool Exists(int id) => _rows.ContainsKey(id);
    public string Name(int id) => _rows.TryGetValue(id, out var r) && r.Name.Length > 0 ? r.Name : $"Anim{id}";
    public int? IdOfName(string name) => _byName.TryGetValue(name.Trim(), out var id) ? id : null;

    /// <summary>
    /// What the client plays for animation <paramref name="animId"/> when the model's playable table does not cover it
    /// (M2Model_ResolvePlayableAnimation @00711bf0): follow the AnimationData fallbacks until an animation the model has
    /// (animation_lookup entry not -1); flag 0x10 on the way reverses the direction, flag 0x20 holds it. Returns the
    /// animation id and the playback flags (0 normal, 1 backwards, 2 / 3 held).
    /// </summary>
    public (short AnimId, ushort Flags) Resolve(IReadOnlyList<short> animationLookup, int firstSequenceAnimId, int animId)
    {
        uint fallback;
        if (animationLookup.Count == 0 || (ushort)animationLookup[0] == 0xFFFF)
            fallback = animationLookup.Count <= 0x93 || (ushort)animationLookup[0x93] == 0xFFFF ? (uint)firstSequenceAnimId : 0x93u;
        else fallback = 0;
        uint cur = (uint)animId;
        int dir = 1, held = 0;
        var seen = new HashSet<uint>();
        while (true)
        {
            if (cur < animationLookup.Count && (ushort)animationLookup[(int)cur] != 0xFFFF)
            {
                ushort f = dir >= 1 ? (ushort)0 : dir < 0 ? (ushort)1 : (ushort)((held > 0 ? 1 : 0) + 2);
                return ((short)cur, f);
            }
            if (!_rows.TryGetValue((int)cur, out var rec) || seen.Contains(cur) || (uint)rec.Fallback == cur) break;
            seen.Add(cur);
            if (rec.Backwards)
            {
                held += dir;
                dir = -dir;
            }
            cur = (uint)rec.Fallback;
            if (rec.Hold)
            {
                held += dir;
                dir = 0;
            }
        }
        return ((short)fallback, 0);
    }

    /// <summary>The model's playable-animation table as the client would compute it (see <see cref="Resolve"/>).</summary>
    public List<(short AnimId, ushort Flags)> PlayableTable(IReadOnlyList<short> animationLookup, int firstSequenceAnimId, int count = PlayableCount)
    {
        var l = new List<(short, ushort)>(count);
        for (int id = 0; id < count; id++) l.Add(Resolve(animationLookup, firstSequenceAnimId, id));
        return l;
    }

    /// <summary>The model's animation_lookup: for every animation id up to the largest one the model has, the index of
    /// its first sequence (-1 when absent). All 9,691 client models follow this rule.</summary>
    public static short[] AnimationLookup(IReadOnlyList<int> sequenceAnimIds)
    {
        if (sequenceAnimIds.Count == 0) return Array.Empty<short>();
        var l = new short[sequenceAnimIds.Max() + 1];
        Array.Fill(l, (short)-1);
        for (int i = sequenceAnimIds.Count - 1; i >= 0; i--) l[sequenceAnimIds[i]] = (short)i;
        return l;
    }

    /// <summary>What an animation is played for, in words (null when the game does not play it by itself).</summary>
    public string? FallbackText(int id)
    {
        var r = Row(id);
        if (r == null || r.Fallback == id || id == 0) return null;   // Stand is the base of every fallback
        var how = r.Hold ? " held on its last frame" : r.Backwards ? " played backwards" : "";
        return $"{Name(r.Fallback)}{how}";
    }
}

/// <summary>
/// What the 1.12 client itself plays an animation for. The groups are the client's own tests (UnitAnim_Is* @005fcbb0 ..
/// @005fee80 in our decompile), so a model that lacks one of these animations falls back (AnimationData) or shows
/// nothing in that situation.
/// </summary>
public static class AnimationRoles
{
    private static readonly int[] SpellCast = { 2, 0x20, 0x21, 0x35, 0x36 };
    private static readonly int[] Combat = { 10, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x1e, 0x24, 0x39, 0x3a, 0x3b, 0x55, 0x56, 0x57, 0x58, 0x5f, 0x75, 0x76 };
    private static readonly int[] Emote = { 0x3c, 0x3d, 0x3e, 0x3f, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4c, 0x4d, 0x4e, 0x50, 0x51, 0x52, 0x53, 0x54, 0x71, 0x88, 0x89, 0x8a, 0xb2, 0xb9, 0xba, 0xc3 };
    private static readonly int[] Death = { 1, 6, 0x83, 0x84 };
    private static readonly int[] Movement = { 4, 5, 0xb, 0xc, 0xd, 0x25, 0x26, 0x27, 0x2a, 0x2b, 0x2c, 0x2d, 0x87, 0x8f, 0xbb };
    private static readonly int[] MountBody = { 0, 1, 3, 4, 5, 6, 8, 9, 10, 0xb, 0xc, 0xd, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x5c, 0x5d, 0x5e, 0x5f, 0x77, 0x78, 0x7f, 0x83, 0x84, 0x87, 0x8f, 0xbb };
    private static readonly int[] Ranged = { 0x6b, 0x6f, 0x70, 0x2e, 0x69, 0x6d, 0x31, 0x6a, 0x6e };

    /// <summary>The roles of an animation id, in plain words.</summary>
    public static List<string> Of(int id)
    {
        var r = new List<string>();
        if (id == 0) r.Add("standing still (the default animation; every model needs it)");
        if (Array.IndexOf(Movement, id) >= 0) r.Add("moving (the game plays it faster or slower to match the unit's speed, using the animation's move speed)");
        if (Array.IndexOf(Combat, id) >= 0) r.Add("melee combat (attacks, parries, blocks, dodges)");
        if (id is >= 0x19 and <= 0x1d) r.Add("combat ready stance");
        if (Array.IndexOf(SpellCast, id) >= 0) r.Add("spell casting");
        if (id is 0x33 or 0x34) r.Add("readying a spell");
        if (Array.IndexOf(Ranged, id) >= 0) r.Add("ranged weapons (bow, gun, thrown)");
        if (Array.IndexOf(Emote, id) >= 0) r.Add("emotes (chat commands like /dance)");
        if (Array.IndexOf(Death, id) >= 0) r.Add("dying and lying dead");
        if (id is 0x25 or 0x26 or 0x27 or 0xbb) r.Add("jumping");
        if (Array.IndexOf(MountBody, id) >= 0) r.Add("mounts: played by the mount while ridden");
        if (Array.IndexOf(Rider, id) >= 0) r.Add("riders: also played by the rider while mounted");
        if (id == 0x5b) r.Add("riding (the rider's pose on a mount)");
        return r;
    }

    private static readonly int[] Rider =
    {
        2, 8, 9, 10, 0xe, 0xf, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x21,
        0x22, 0x23, 0x24, 0x2e, 0x2f, 0x30, 0x31, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x40, 0x41,
        0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4c, 0x4d, 0x4e, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x75, 0x76, 0x7a, 0x7b, 0x7c, 0x7d, 0x80, 0x81, 0x82, 0x85, 0x86, 0x88,
        0x89, 0x8a, 0xb9, 0xba, 0xc3,
    };

    public static bool IsMovement(int id) => Array.IndexOf(Movement, id) >= 0;
}

/// <summary>
/// Animation events (the 4-character codes of an M2's events) and what the 1.12 client does with them
/// (CGUnit_C_HandleAnimEvent @005ffbd0 for units, the UnitVisual event callback @0061f6f0 for spell effects,
/// CGUnit_C_ReleaseMissiles' $CSL / $CSR / $CST lookup @0060c9b0).
/// </summary>
public static class AnimationEvents
{
    public static string Describe(string code)
    {
        if (code.Length < 4) return "unknown event";
        switch (code)
        {
            case "$CSL": return "spell release from the left hand (missiles start here)";
            case "$CSR": return "spell release from the right hand (missiles start here)";
            case "$CST": return "spell release (missiles start here)";
            case "$BWR": return "bow / gun release (the arrow or bullet leaves)";
            case "$BWP": return "bow pull (combat event)";
            case "$CSD": return "emote sound";
            case "$ESD": return "emote state sound";
            case "$FSD": return "footstep sound";
            case "$TRD": return "spell cast sound (players)";
            case "$WNG": return "wing flap sound";
            case "$WGG": return "event sound";
            case "$BTH": return "breath (the cold-breath effect starts here)";
            case "$DTH": return "death (combat event: the body falls)";
            case "$HIT": return "the hit lands (spell effects: hit reaction)";
            case "$SND": return "sound (spell effects)";
            case "$SHL": return "sheathe / draw a weapon (left)";
            case "$SHR": return "sheathe / draw a weapon (right)";
            case "$CAH": case "$CPP": case "$CSS": return "combat event (weapon swing / hit sound)";
        }
        if (code.StartsWith("$AH", StringComparison.Ordinal)) return "combat event (attack hit)";
        if (code.StartsWith("$FD", StringComparison.Ordinal)) return "foot down (footprint and dust)";
        if (code.Length == 4 && code[0] == '$' && "SFBRW".Contains(code[1]) && code[2] == 'L' && char.IsDigit(code[3])) return "footstep (left foot)";
        if (code.Length == 4 && code[0] == '$' && "SFBRW".Contains(code[1]) && code[2] == 'R' && char.IsDigit(code[3])) return "footstep (right foot)";
        return code.StartsWith('$') ? "an event the game does not handle for units (it may be used by a script or ignored)" : "a special event of this model";
    }

    /// <summary>The 4 characters of an event id record (4 bytes).</summary>
    public static string Code(ReadOnlySpan<byte> id4) => System.Text.Encoding.Latin1.GetString(id4[..4]).TrimEnd('\0');

    public static byte[] CodeBytes(string code)
    {
        var b = new byte[4];
        var s = System.Text.Encoding.Latin1.GetBytes(code);
        Array.Copy(s, b, Math.Min(4, s.Length));
        return b;
    }
}
