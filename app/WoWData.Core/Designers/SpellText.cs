using System.Globalization;
using System.Text;
using WoWData.Core.Dbc;

namespace WoWData.Core.Designers;

/// <summary>One $ variable of a description that the game cannot fill in (it prints a bare "$" instead).</summary>
public sealed record SpellTextProblem(string Token, string Message);

/// <summary>The expanded text of a description and the variables that failed.</summary>
public sealed record SpellTextResult(string Text, IReadOnlyList<SpellTextProblem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// 0.12.0: the live preview of a spell's description and buff text, as the 1.12 client fills in its $ variables. Ported
/// from the client's own code in our decompile: <c>SpellText_ExpandDescription</c> @005075f0 (copies the text, hands every
/// "$" to the variable expander, prints a bare "$" for a variable that fails) and <c>SpellText_ExpandVariable</c> @00507710:
/// <list type="bullet">
/// <item>an optional scale "$/10;" or "$*2;" (divide or multiply), an optional spell ID ("$12345s1" reads another spell),
///   then a letter and an optional effect number 1-3 (no number = effect 1);</item>
/// <item>s / m / M: the effect's points (min "to" max, min, max) from <c>Spell_C_GetSpellEffectPointsRange</c> @006e3800:
///   min = BasePoints + BaseDice + DicePerLevel x L + RealPointsPerLevel x L, max = BasePoints + DieSides x (BaseDice +
///   DicePerLevel x L) + RealPointsPerLevel x L, with L = caster level - BaseLevel (never below 0);</item>
/// <item>o: the points over the whole duration (points x duration / tick period, 5 s when the period is 0);</item>
/// <item>d: the duration (<c>Spell_C_GetSpellDurationMs</c> @006ea000: SpellDuration base + per level, capped), "until
///   cancelled" when not positive, whole units as "%d sec / min / hour / hrs / days" (INT_SPELL_DURATION_*), else "%.2f";</item>
/// <item>t: the tick period in seconds; a: the effect's radius; r: the range ("%.1f"); e: the multiple value ("%.1f");
///   f / F: the chain damage multiplier; h: proc chance; n: proc charges; u: stack amount; x: chain targets; i: max targets;
///   v: max target level; q: misc value; b: points per combo point; c: power cost; p: power per second; z: the home inn;
///   g / l: "$gmale:female;" and "$lsingular:plural;" alternatives.</item>
/// </list>
/// What the preview cannot know (the caster's talents and spell modifiers, the hearthstone's inn, the character's sex)
/// is shown without them: talents that modify a spell change the in-game numbers.
/// </summary>
public static class SpellText
{
    /// <summary>The caster level used when none is given: the spell's level (at least 1).</summary>
    public static int DefaultLevel(DbcTable spell, DbcRow row)
    {
        var lvl = Col(spell, row, "SpellLevel");
        return (int)Math.Max(1, lvl);
    }

    /// <summary>Expand the description (or, with <paramref name="aura"/>, the buff text) of a spell row.</summary>
    public static SpellTextResult Describe(DbcTable spell, DbcRow row, DbcTableLookup lookup, int? casterLevel = null, bool aura = false, int locale = 0, bool female = false)
    {
        var col = spell.Layout.Column(aura ? "AuraDescription" : "Description")!;
        var text = spell.Text(row, col, locale);
        if (aura && text.Length == 0) text = spell.Text(row, spell.Layout.Column("Description")!, locale);
        return Expand(text, spell, row, lookup, casterLevel ?? DefaultLevel(spell, row), female);
    }

    /// <summary>Expand any text with the values of a spell row (the editor's preview while you type).</summary>
    public static SpellTextResult Expand(string text, DbcTable spell, DbcRow row, DbcTableLookup lookup, int casterLevel, bool female = false)
    {
        var sb = new StringBuilder();
        var problems = new List<SpellTextProblem>();
        int i = 0;
        double lastNumber = 0;
        while (i < text.Length)
        {
            int d = text.IndexOf('$', i);
            if (d < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, d - i);
            int start = d;
            i = d + 1;
            var (ok, output, next, why, number) = Variable(text, i, spell, row, lookup, casterLevel, female, lastNumber);
            if (ok)
            {
                sb.Append(output);
                if (number != null) lastNumber = number.Value;
                i = next;
            }
            else
            {
                sb.Append('$');
                int end = Math.Min(text.Length, Math.Max(next, i + 1));
                // a missing spell ID ("$99999s1"): the token goes on with its letter and effect number
                if (end < text.Length && char.IsLetter(text[end]) && char.IsAsciiDigit(text[end - 1]))
                {
                    end++;
                    if (end < text.Length && char.IsAsciiDigit(text[end])) end++;
                }
                problems.Add(new SpellTextProblem(text[start..end], why));
                // the client skips one character after a failed variable
                if (i < text.Length) i++;
            }
        }
        return new SpellTextResult(sb.ToString(), problems);
    }

    private static long Col(DbcTable t, DbcRow r, string name) => t.Layout.Column(name) is { } c ? t.Number(r, c) : 0;
    private static float ColF(DbcTable t, DbcRow r, string name) => t.Layout.Column(name) is { } c ? t.Float(r, c) : 0f;

    private static (bool Ok, string Output, int Next, string Why, double? Number) Variable(string s, int i, DbcTable spell, DbcRow self, DbcTableLookup lookup, int level, bool female, double lastNumber)
    {
        float scale = 1f;
        // "$/10;" or "$*2;"
        if (i < s.Length && (s[i] == '/' || s[i] == '*'))
        {
            int semi = s.IndexOf(';', i);
            if (semi > 0)
            {
                var num = s[(i + 1)..semi];
                if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0)
                    scale = s[i] == '/' ? 1f / n : n;
                i = semi + 1;
            }
        }
        // an optional spell ID
        var row = self;
        int digits = 0;
        while (i + digits < s.Length && char.IsAsciiDigit(s[i + digits])) digits++;
        if (digits > 0)
        {
            if (!long.TryParse(s.AsSpan(i, digits), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return (false, "", i + digits, "the spell ID is too long", null);
            var other = (lookup("Spell") is { } st && !ReferenceEquals(st, spell) ? st : spell).Find(id) ?? spell.Find(id);
            if (other == null) return (false, "", i + digits, $"spell {id} does not exist (the game prints \"$\")", null);
            row = other;
            i += digits;
            long maxLevel = Col(spell, row, "MaxLevel");
            if (maxLevel > 0 && maxLevel <= level) level = (int)maxLevel;
        }
        if (i >= s.Length) return (false, "", i, "a \"$\" at the end of the text", null);
        char c = s[i];
        int idx = 0;
        bool hasIndex = false;
        if (i + 1 < s.Length && s[i + 1] > '0' && s[i + 1] <= '9')
        {
            idx = s[i + 1] - '1';
            hasIndex = true;
            if (idx > 2) idx = 0;
        }
        int after = i + 1 + (hasIndex ? 1 : 0);
        string E(string name) => $"{name}{idx + 1}";
        string Int(double v) => ((long)v).ToString(CultureInfo.InvariantCulture);
        string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        switch (c)
        {
            case 'a':
            case 'A':
            {
                long ri = Col(spell, row, E("EffectRadiusIndex"));
                var radius = lookup("SpellRadius")?.Find(ri);
                double v = radius == null ? 0 : (long)lookup("SpellRadius")!.Float(radius, lookup("SpellRadius")!.Layout.Column("Radius")!);
                return (true, Int(v), after, "", v);
            }
            case 'b':
            case 'B':
            {
                double v = Math.Truncate(ColF(spell, row, E("EffectPointsPerCombo")));
                return (true, Int(v), after, "", v);
            }
            case 'c':
            case 'C':
            {
                long cost = Col(spell, row, "ManaCost") + Col(spell, row, "ManaCostPerLevel") * Math.Max(0, level - Col(spell, row, "BaseLevel"));
                if (Col(spell, row, "PowerType") == 1) cost /= 10;   // rage is stored x10
                return (true, cost.ToString(CultureInfo.InvariantCulture), after, "", cost);
            }
            case 'd':
            case 'D':
            {
                int ms = DurationMs(spell, row, lookup, level);
                return (true, FormatDuration(ms), after, "", ms / 1000.0);
            }
            case 'e':
            case 'E':
            {
                double v = ColF(spell, row, E("EffectMultipleValue"));
                return (true, F1(v), after, "", v);
            }
            case 'F':
            {
                double v = scale * ColF(spell, row, E("EffectChainAmplitude"));
                v = Math.Truncate(v <= 0 ? v - 0.5 : v + 0.5);
                return (true, Int(v), after, "", v);
            }
            case 'f':
            {
                double v = scale * ColF(spell, row, E("EffectChainAmplitude"));
                return (true, F1(v), after, "", v);
            }
            case 'g':
            case 'G':
                return Alternative(s, i + 1, female ? 1 : 0, "$g", lastNumber);
            case 'l':
            case 'L':
                return Alternative(s, i + 1, Math.Abs(lastNumber - 1) < 1e-9 ? 0 : 1, "$l", lastNumber);
            case 'h':
            case 'H':
            {
                long v = Col(spell, row, "ProcChance");
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'i':
            case 'I':
            {
                long v = Col(spell, row, "MaxTargets");
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'n':
            case 'N':
            {
                long v = Col(spell, row, "ProcCharges");
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'p':
            case 'P':
            {
                long v = Col(spell, row, "ManaPerSecond") + Col(spell, row, "ManaPerSecondPerLevel") * Math.Max(0, level - Col(spell, row, "BaseLevel"));
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'q':
            case 'Q':
            {
                long v = Col(spell, row, E("EffectMiscValue"));
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'r':
            case 'R':
            {
                long ri = Col(spell, row, "RangeIndex");
                if (ri < 2) ri = 1;
                var rt = lookup("SpellRange");
                var rr = rt?.Find(ri);
                double v = rr == null ? 0 : rt!.Float(rr, rt.Layout.Column("RangeMax")!);
                return (true, F1(v), after, "", Math.Ceiling(v));
            }
            case 't':
            case 'T':
            {
                long period = (Col(spell, row, "ProcFlags") & 1) != 0 ? 5000 : Col(spell, row, E("EffectAuraPeriod"));
                return (true, (period / 1000).ToString(CultureInfo.InvariantCulture), after, "", period / 1000);
            }
            case 'u':
            case 'U':
            {
                long v = Col(spell, row, "StackAmount");
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'v':
            case 'V':
            {
                long v = Col(spell, row, "MaxTargetLevel");
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'x':
            case 'X':
            {
                long v = Col(spell, row, E("EffectChainTargets"));
                return (true, v.ToString(CultureInfo.InvariantCulture), after, "", v);
            }
            case 'z':
            case 'Z':
                return (true, "your home inn", after, "", null);
            case 's':
            case 'S':
            case 'm':
            case 'M':
            case 'o':
            case 'O':
            {
                var (min, max) = PointsRange(spell, row, idx, level);
                if (c is 'o' or 'O')
                {
                    long period = Col(spell, row, E("EffectAuraPeriod"));
                    if (period == 0) period = 5000;
                    int dur = period > 0 ? DurationMs(spell, row, lookup, level) : 0;
                    if (dur > 0)
                    {
                        min = dur * min / period;
                        max = dur * max / period;
                    }
                    else min = max = 0;
                }
                double lo = Math.Abs(min) * scale, hi = Math.Abs(max) * scale;
                bool loWhole = Whole(lo), hiWhole = Whole(hi);
                long loInt = Floorish(lo), hiInt = Floorish(hi);
                string text;
                double shownNumber;
                if (c == 'm')
                {
                    text = loWhole ? loInt.ToString(CultureInfo.InvariantCulture) : F1(lo);
                    shownNumber = loWhole ? loInt : 2;
                }
                else if (c == 'M')
                {
                    text = hiWhole ? hiInt.ToString(CultureInfo.InvariantCulture) : F1(hi);
                    shownNumber = hiWhole ? hiInt : 2;
                }
                else if (Math.Abs(min - max) < 1e-9)
                {
                    text = loWhole || c != 'S' ? loInt.ToString(CultureInfo.InvariantCulture) : F1(lo);
                    shownNumber = hiWhole ? hiInt : 2;
                }
                else if (loWhole && hiWhole)
                {
                    text = $"{loInt} to {hiInt}";
                    shownNumber = hiInt;
                }
                else if (c == 'S')
                {
                    text = $"{F1(lo)} to {F1(hi)}";
                    shownNumber = 2;
                }
                else
                {
                    long top = hiWhole ? hiInt : (long)Math.Truncate(hi) + 1;
                    text = $"{(long)Math.Truncate(lo)} to {top}";
                    shownNumber = hiWhole ? hiInt : 2;
                }
                return (true, text, after, "", shownNumber);
            }
            default:
                return (false, "", after, $"\"${c}\" is not a variable the 1.12 client knows (it prints \"$\"). Known: s m M o d t a r e f h n u x i v q b c p z g l (with an effect number 1-3).", null);
        }
    }

    private static bool Whole(double v) => v - Math.Floor(v) < 0.001 || Math.Ceiling(v) - v < 0.001;

    /// <summary>The client's rounding: floor, or the next whole number when the value is within 0.001 below it.</summary>
    private static long Floorish(double v) => Math.Ceiling(v) - v < 0.001 ? (long)Math.Ceiling(v) : (long)Math.Floor(v);

    /// <summary>"$gmale:female;" or "$lsingular:plural;": the chosen alternative.</summary>
    private static (bool, string, int, string, double?) Alternative(string s, int i, int choice, string what, double lastNumber)
    {
        int semi = s.IndexOf(';', i);
        if (semi < 0) return (false, "", i, $"{what} needs the form {what}one:other; (the \";\" is missing)", null);
        var parts = s[i..semi].Split(':');
        if (parts.Length < 2) return (false, "", semi + 1, $"{what} needs two words separated by \":\"", null);
        return (true, parts[Math.Min(choice, parts.Length - 1)], semi + 1, "", lastNumber);
    }

    /// <summary>The min and max points of an effect for a caster level (no talents or modifiers).</summary>
    public static (double Min, double Max) PointsRange(DbcTable spell, DbcRow row, int effect, int level)
    {
        string E(string n) => $"{n}{effect + 1}";
        long baseLevel = Col(spell, row, "BaseLevel");
        long l = level;
        if (baseLevel > 0) l -= baseLevel;
        if (l < 0) l = 0;
        long dicePerLevel = Col(spell, row, E("EffectDicePerLevel"));
        long bp = Col(spell, row, E("EffectBasePoints"));
        long baseDice = Col(spell, row, E("EffectBaseDice"));
        long dieSides = Col(spell, row, E("EffectDieSides"));
        double real = ColF(spell, row, E("EffectRealPointsPerLevel"));
        double min = dicePerLevel * l + bp + baseDice + real * l;
        double max = dieSides * (dicePerLevel * l + baseDice) + bp + real * l;
        return (min, max);
    }

    /// <summary>The duration in milliseconds for a caster level (SpellDuration base + per level, capped).</summary>
    public static int DurationMs(DbcTable spell, DbcRow row, DbcTableLookup lookup, int level)
    {
        var dt = lookup("SpellDuration");
        var dr = dt?.Find(Col(spell, row, "DurationIndex"));
        if (dt == null || dr == null) return 0;
        long b = dt.Number(dr, dt.Layout.Column("Duration")!);
        long per = dt.Number(dr, dt.Layout.Column("DurationPerLevel")!);
        long cap = dt.Number(dr, dt.Layout.Column("MaxDuration")!);
        long v = (level - Col(spell, row, "BaseLevel")) * per + b;
        if (cap <= v) v = cap;
        return (int)v;
    }

    /// <summary>A duration as the game writes it in tooltips: "until cancelled", "30 sec", "2 min", "1 hour", "1.50 min".</summary>
    public static string FormatDuration(int ms)
    {
        if (ms < 1) return "until cancelled";
        double sec = ms / 1000.0;
        (double v, string whole, string part) = sec < 60 ? (sec, "{0} sec", "{0:0.00} sec")
            : sec < 3600 ? (sec / 60, "{0} min", "{0:0.00} min")
            : sec < 86400 ? (sec / 3600, "{0} hour", "{0:0.00} hrs")
            : (sec / 86400, "{0} days", "{0:0.00} days");
        bool isWhole = Math.Abs(v - Math.Round(v)) < 0.01;
        if (isWhole)
        {
            long n = (long)Math.Round(v);
            if (whole == "{0} hour" && n != 1) whole = "{0} hrs";
            return string.Format(CultureInfo.InvariantCulture, whole, n);
        }
        return string.Format(CultureInfo.InvariantCulture, part, v);
    }
}
