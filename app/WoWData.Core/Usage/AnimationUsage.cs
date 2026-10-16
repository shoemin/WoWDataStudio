using WoWData.Core.Dbc;
using WoWData.Core.Index;
using WoWData.Core.M2;

namespace WoWData.Core.Usage;

/// <summary>What the game plays one animation for: the client's own situations, emotes and spells.</summary>
public sealed class AnimationUse
{
    public int AnimId { get; init; }
    public string Name { get; init; } = "";
    public List<string> Roles { get; init; } = new();
    /// <summary>Chat commands that play it (/dance...).</summary>
    public List<string> Emotes { get; init; } = new();
    /// <summary>(spell name, when: "while casting", "at the cast"...).</summary>
    public List<(string Spell, string When)> Spells { get; init; } = new();
    public string? Fallback { get; init; }
}

/// <summary>
/// 0.10.0: "Used in game" for animations. The client's own situations come from its animation tests (UnitAnim_Is*),
/// emotes from Emotes.dbc + EmotesText.dbc, spells from Spell.dbc -> SpellVisual.dbc (precast, cast, impact, state,
/// channel kits) -> SpellVisualKit.dbc (the caster's animation).
/// </summary>
public sealed class AnimationUsage
{
    private readonly Dictionary<int, List<string>> _emotes = new();
    private readonly Dictionary<int, List<(string, string)>> _spells = new();
    public AnimationCatalog Animations { get; }

    public AnimationUsage(Catalog catalog, AnimationCatalog animations)
    {
        Animations = animations;
        var layouts = DbcLayouts.Default;
        int Col(string table, string col) => layouts.Get(table)?.Column(col)?.Field ?? -1;
        try
        {
            // emotes: EmotesText (the /command) -> Emotes (its animation)
            var em = catalog.Table("Emotes");
            int emAnim = Col("Emotes", "AnimID");
            var animOfEmote = new Dictionary<int, int>();
            for (int i = 0; i < em.Count && emAnim >= 0; i++) animOfEmote[em.Int(i, 0)] = em.Int(i, emAnim);
            var et = catalog.Table("EmotesText");
            int etEmote = Col("EmotesText", "EmoteID");
            for (int i = 0; i < et.Count && etEmote >= 0; i++)
                if (animOfEmote.TryGetValue(et.Int(i, etEmote), out int a) && a > 0)
                {
                    if (!_emotes.TryGetValue(a, out var l)) _emotes[a] = l = new List<string>();
                    var cmd = "/" + et.String(i, 1).ToLowerInvariant();
                    if (!l.Contains(cmd)) l.Add(cmd);
                }
        }
        catch (DbcException) { }
        try
        {
            var kit = catalog.Table("SpellVisualKit");
            int kitAnim = Col("SpellVisualKit", "AnimID");
            var animOfKit = new Dictionary<int, int>();
            for (int i = 0; i < kit.Count && kitAnim >= 0; i++) animOfKit[kit.Int(i, 0)] = kit.Int(i, kitAnim);
            var vis = catalog.Table("SpellVisual");
            var kitCols = new[] { ("PrecastKit", "while casting"), ("CastKit", "at the cast"), ("ImpactKit", "on its target when hit"), ("StateKit", "while its aura is on"), ("ChannelKit", "while channelling") }
                .Select(x => (Col("SpellVisual", x.Item1), x.Item2)).Where(x => x.Item1 >= 0).ToList();
            var animsOfVisual = new Dictionary<int, List<(int Anim, string When)>>();
            for (int i = 0; i < vis.Count; i++)
                foreach (var (c, when) in kitCols)
                    if (animOfKit.TryGetValue(vis.Int(i, c), out int a) && a > 0)   // 0 and -1: the kit plays no animation
                    {
                        if (!animsOfVisual.TryGetValue(vis.Int(i, 0), out var l)) animsOfVisual[vis.Int(i, 0)] = l = new();
                        l.Add((a, when));
                    }
            var sp = catalog.Table("Spell");
            int spVis = Col("Spell", "SpellVisual"), spName = Col("Spell", "Name");
            for (int i = 0; i < sp.Count && spVis >= 0 && spName >= 0; i++)
            {
                if (!animsOfVisual.TryGetValue(sp.Int(i, spVis), out var l)) continue;
                string name = sp.String(i, spName);
                if (name.Length == 0) continue;
                foreach (var (a, when) in l)
                {
                    if (!_spells.TryGetValue(a, out var sl)) _spells[a] = sl = new();
                    if (!sl.Contains((name, when))) sl.Add((name, when));
                }
            }
        }
        catch (DbcException) { }
    }

    public AnimationUse Of(int animId) => new()
    {
        AnimId = animId, Name = Animations.Name(animId), Roles = AnimationRoles.Of(animId),
        Emotes = _emotes.GetValueOrDefault(animId) ?? new List<string>(),
        Spells = (_spells.GetValueOrDefault(animId) ?? new()).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList(),
        Fallback = Animations.FallbackText(animId),
    };

    /// <summary>The "Animations" section of a "Used in game" report for a model.</summary>
    public void AddSection(UsageReport rep, M2Model m)
    {
        if (m.Sequences.Length == 0) return;
        var info = RigInfo.Read(m, Animations);
        var s = rep.Add("animations", "Animations and what the game plays them for");
        int emotes = 0, spells = 0;
        foreach (var grp in info.Sequences.GroupBy(x => x.AnimId))
        {
            var u = Of(grp.Key);
            var first = grp.First();
            var l = s.Line();
            l.Add(first.AnimName, bold: true);
            l.Muted(grp.Count() > 1 ? $"  {grp.Count()} variations, " + string.Join(" / ", grp.Select(x => $"{x.Duration / 1000.0:0.##} s")) : $"  {first.Duration / 1000.0:0.##} s");
            l.Muted(first.Loops ? ", loops" : ", plays once");
            if (u.Roles.Count > 0) s.Line(1).Muted("the game plays it for " + string.Join("; ", u.Roles));
            if (u.Emotes.Count > 0)
            {
                emotes++;
                s.Line(1).Muted("emotes: " + string.Join(", ", u.Emotes.Take(12)) + (u.Emotes.Count > 12 ? ", ..." : ""));
            }
            if (u.Spells.Count > 0)
            {
                spells++;
                var names = u.Spells.Select(x => x.Spell).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var whens = u.Spells.Select(x => x.When).Distinct().ToList();
                s.Line(1).Muted($"{names.Count} spell{(names.Count == 1 ? "" : "s")} ({string.Join(", ", whens)}): {string.Join(", ", names.Take(6))}{(names.Count > 6 ? ", ..." : "")}");
            }
        }
        var missing = new[] { 0, 1, 4, 5, 16, 26 }.Where(id => !info.Sequences.Any(x => x.AnimId == id) && Animations.Exists(id)).ToList();
        if (missing.Count > 0)
            s.Tail.Add(new UsageLine().Muted("Not in this model (the game falls back): " + string.Join(", ", missing.Select(id => Animations.FallbackText(id) is { } fb ? $"{Animations.Name(id)} -> {fb}" : Animations.Name(id)))));
        int groups = info.Sequences.Select(x => x.AnimId).Distinct().Count();
        s.Summary = $"{info.Sequences.Count} animation{(info.Sequences.Count == 1 ? "" : "s")} ({groups} different){(emotes > 0 ? $", {emotes} used by emotes" : "")}{(spells > 0 ? $", {spells} by spells" : "")}";
    }
}
