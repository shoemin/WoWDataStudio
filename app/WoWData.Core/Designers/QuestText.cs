using System.Text;
using WoWData.Core.Dbc;

namespace WoWData.Core.Designers;

/// <summary>The player a quest text is previewed for: the values of $N, $R, $C and $G.</summary>
public sealed record QuestTextPlayer(string Name, string Race, string Class, bool Female)
{
    public static readonly QuestTextPlayer Default = new("Thrall", "Orc", "Warrior", false);
}

/// <summary>A text of a quest, filled in for a player: the text, the tokens the game does not know, and whether it fits.</summary>
public sealed record QuestTextPreview(string Text, IReadOnlyList<string> UnknownTokens);

/// <summary>A limit of the 1.12 client on a quest text, read from its code.</summary>
public sealed record QuestTextLimit(string Field, string Column, int HardBytes, int ShownChars, string Where);

/// <summary>
/// 0.13.0: the quest texts as the 1.12 client shows them. <see cref="Expand"/> is ported from the client's own
/// ChatText_ExpandDollarTokens (@00506f70) and ChatText_ExpandOneDollarToken (@005070a0), which the quest frame runs on
/// every quest text (QuestFrame_BeginInteraction @00500bd0, QuestFrame_SetObjectiveText @00500dc0):
/// <list type="bullet">
/// <item><c>$B</c> a new line; <c>$N</c> the player's name; <c>$R</c> / <c>$r</c> the race (lower case with r);
/// <c>$C</c> / <c>$c</c> the class; <c>$G male:female;</c> by the player's gender (spaces trimmed); <c>$T male:female;</c>
/// the PvP rank title, else as $G; <c>$W</c>n / <c>$E</c>n a world-state number;</item>
/// <item>any other letter after <c>$</c>: the client prints the <c>$</c> and the letter as they are.</item>
/// </list>
/// <see cref="Limits"/> are the client's buffers: QuestCache_Read (@006dbf60) copies the title (0x200), objectives (0x800),
/// details (0x800), end text (0x200) and the four objective texts (0x100) of SMSG_QUEST_QUERY_RESPONSE into fixed fields
/// WITHOUT a length check (a longer text overwrites the rest of the record); the quest window keeps 63 characters of the
/// title (QuestFrame_SetItemLists @00500ef0, 0x40) and 1023 of the details, progress and completion texts after the $
/// tokens are filled in (0x400); CGPlayer_C_HandleQuestGiverRequestItems (@005dc050) reads the progress text with 0x800.
/// </summary>
public static class QuestText
{
    public static readonly IReadOnlyList<QuestTextLimit> Limits = new List<QuestTextLimit>
    {
        new("title", "Title", 0x200, 63, "the quest cache (QuestCache_Read @006dbf60: 512 bytes) and the quest window (63 characters)"),
        new("objectives", "Objectives", 0x800, 1023, "the quest cache (2048 bytes) and the quest window (1023 characters with $N/$C/$R filled in)"),
        new("details", "Details", 0x800, 1023, "the quest cache (2048 bytes) and the quest window (1023 characters with $N/$C/$R filled in)"),
        new("progress-text", "RequestItemsText", 0x800, 1023, "the progress message (2048 bytes, @005dc050) and the quest window (1023 characters)"),
        new("completion-text", "OfferRewardText", 0x800, 1023, "the reward message (2048 bytes) and the quest window (1023 characters)"),
        new("end-text", "EndText", 0x200, 511, "the quest cache (512 bytes)"),
        new("objective-text1", "ObjectiveText1", 0x100, 255, "the quest cache (256 bytes)"),
        new("objective-text2", "ObjectiveText2", 0x100, 255, "the quest cache (256 bytes)"),
        new("objective-text3", "ObjectiveText3", 0x100, 255, "the quest cache (256 bytes)"),
        new("objective-text4", "ObjectiveText4", 0x100, 255, "the quest cache (256 bytes)"),
    };

    /// <summary>Fill in the $ tokens of a quest text as the 1.12 client does for this player.</summary>
    public static QuestTextPreview Expand(string? text, QuestTextPlayer p)
    {
        var unknown = new List<string>();
        if (string.IsNullOrEmpty(text)) return new QuestTextPreview("", unknown);
        var o = new StringBuilder();
        int src = 0;
        int cur = text.IndexOf('$');
        while (cur >= 0 && cur < text.Length)
        {
            o.Append(text, src, cur - src);
            cur++;   // after the $
            int digits = cur;
            while (cur < text.Length && char.IsAsciiDigit(text[cur])) cur++;
            string number = text[digits..cur];
            char k = cur < text.Length ? text[cur] : '\0';
            switch (k)
            {
                case 'B': case 'b':
                    o.Append('\n');
                    cur++;
                    break;
                case 'N': case 'n':
                    o.Append(p.Name);
                    cur++;
                    break;
                case 'C': case 'c':
                    o.Append(k == 'c' ? p.Class.ToLowerInvariant() : p.Class);
                    cur++;
                    break;
                case 'R': case 'r':
                    o.Append(k == 'r' ? p.Race.ToLowerInvariant() : p.Race);
                    cur++;
                    break;
                case 'W': case 'w': case 'E': case 'e':
                    o.Append('0');   // a world-state value: 0 in a preview
                    cur++;
                    break;
                case 'G': case 'g': case 'T': case 't':
                {
                    cur++;
                    while (cur < text.Length && text[cur] == ' ') cur++;
                    int colon = text.IndexOf(':', cur);
                    int semi = colon < 0 ? -1 : text.IndexOf(';', colon);
                    if (colon < 0 || semi < 0)
                    {
                        unknown.Add("$" + k + " without \"male:female;\"");
                        break;   // the client prints the rest as it is
                    }
                    string part;
                    if (p.Female)
                    {
                        int s = colon + 1;
                        while (s < semi && text[s] == ' ') s++;
                        part = text[s..semi];
                    }
                    else part = text[cur..colon];
                    o.Append(part.TrimEnd(' '));
                    cur = semi + 1;
                    break;
                }
                default:
                    // the client prints "$" and goes on after it
                    o.Append('$').Append(number);
                    unknown.Add(k == '\0' ? "$ at the end" : "$" + number + k);
                    break;
            }
            src = cur;
            cur = cur < text.Length ? text.IndexOf('$', cur) : -1;
        }
        if (src < text.Length) o.Append(text, src, text.Length - src);
        return new QuestTextPreview(o.ToString(), unknown);
    }

    /// <summary>The longest text a quest text can become (the longest name, race and class; both genders tried).</summary>
    public static int LongestExpansion(string? text, IEnumerable<string> races, IEnumerable<string> classes)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('$')) return text?.Length ?? 0;
        string race = races.DefaultIfEmpty("Night Elf").OrderByDescending(r => r.Length).First();
        string cls = classes.DefaultIfEmpty("Warrior").OrderByDescending(c => c.Length).First();
        var name = new string('W', 12);   // the longest character name the 1.12 client accepts
        return Math.Max(Expand(text, new QuestTextPlayer(name, race, cls, false)).Text.Length, Expand(text, new QuestTextPlayer(name, race, cls, true)).Text.Length);
    }

    /// <summary>The bytes a text takes in the client (UTF-8).</summary>
    public static int Bytes(string? text) => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>The checks of one text against the client's limits, in plain words.</summary>
    public static List<DesignProblem> Check(string field, string? text, IEnumerable<string> races, IEnumerable<string> classes)
    {
        var res = new List<DesignProblem>();
        var lim = Limits.FirstOrDefault(l => l.Field == field);
        if (lim == null || string.IsNullOrEmpty(text)) return res;
        int bytes = Bytes(text);
        if (bytes >= lim.HardBytes)
            res.Add(new DesignProblem(DesignSeverity.Error, field, $"{bytes} bytes: the 1.12 client keeps at most {lim.HardBytes - 1} in {lim.Where}; a longer text overwrites the rest of the quest in the game's memory. Shorten it."));
        else
        {
            int shown = field == "title" ? text.Length : LongestExpansion(text, races, classes);
            if (shown > lim.ShownChars)
                res.Add(new DesignProblem(DesignSeverity.Warning, field, field == "title"
                    ? $"{shown} characters: the quest window shows only the first {lim.ShownChars}."
                    : $"Up to {shown} characters once $N, $C and $R are filled in: the quest window cuts it after {lim.ShownChars}."));
        }
        var unknown = Expand(text, QuestTextPlayer.Default).UnknownTokens;
        if (unknown.Count > 0)
            res.Add(new DesignProblem(DesignSeverity.Warning, field, $"The game does not know {string.Join(", ", unknown.Distinct().Take(4))}: it shows the $ as it is. Known: $N name, $R race, $C class, $G male:female; and $B new line."));
        return res;
    }

    /// <summary>The playable races' and classes' names (for previews and the longest text), from the game's tables.</summary>
    public static (List<string> Races, List<string> Classes) Names(DbcTableLookup lookup)
    {
        var races = new List<string>();
        var classes = new List<string>();
        try
        {
            if (lookup("ChrRaces") is { } r && r.Layout.Column("Name") is { } rn)
                foreach (var row in r.Rows) if (r.Key(row) is >= 1 and <= 8) races.Add(r.Text(row, rn, 0));
            if (lookup("ChrClasses") is { } c && c.Layout.Column("Name") is { } cn)
                foreach (var row in c.Rows) classes.Add(c.Text(row, cn, 0));
        }
        catch (Exception e) when (e is DbcException or IOException) { }
        if (races.Count == 0) races.AddRange(new[] { "Human", "Orc", "Dwarf", "Night Elf", "Undead", "Tauren", "Gnome", "Troll" });
        if (classes.Count == 0) classes.AddRange(new[] { "Warrior", "Paladin", "Hunter", "Rogue", "Priest", "Shaman", "Mage", "Warlock", "Druid" });
        return (races.Where(x => x.Length > 0).ToList(), classes.Where(x => x.Length > 0).ToList());
    }
}
