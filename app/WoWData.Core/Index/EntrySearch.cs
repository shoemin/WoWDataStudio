namespace WoWData.Core.Index;

/// <summary>The sort orders of the Browse list.</summary>
public enum EntrySort { Name, Type, Id, Path }

/// <summary>One result of <see cref="EntrySearch"/>: the entry and its badge (your edit, or uses your edit).</summary>
public sealed record EntryHit(IndexEntry Entry, bool IsModified, bool UsesModified)
{
    public string Badge => IsModified ? "Modified" : UsesModified ? "Uses edit" : "";
}

/// <summary>What to search for: the Browse tab's search box, type filters, "Show only my changes" and sorting.</summary>
public sealed record EntryQuery(
    string Text = "",
    IReadOnlySet<AssetKind>? Kinds = null,
    bool OnlyMyChanges = false,
    EntrySort Sort = EntrySort.Name,
    bool Ascending = true,
    IReadOnlySet<string>? Keys = null);

/// <summary>
/// 0.8.0 (moved from the Browse tab's view model): the search of the Browse list. Every word must be in the entry's
/// name, path, id or keywords; the type filters; "Show only my changes" (your edited files and what uses them); sorting,
/// with the best matches first for a name search (exact name, then names starting with the text, then whole words). The
/// desktop app and <c>wds find search</c> both call <see cref="Run"/>, so they list the same entries in the same order.
/// </summary>
public static class EntrySearch
{
    /// <summary>The type names used by the command line (and accepted in any case): creature, character, item,
    /// gameobject, worldobject, spelleffect, texture.</summary>
    public static readonly IReadOnlyDictionary<string, AssetKind> KindNames = new Dictionary<string, AssetKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["creature"] = AssetKind.Creature, ["creatures"] = AssetKind.Creature,
        ["character"] = AssetKind.Character, ["characters"] = AssetKind.Character,
        ["item"] = AssetKind.Item, ["items"] = AssetKind.Item,
        ["gameobject"] = AssetKind.GameObject, ["gameobjects"] = AssetKind.GameObject, ["go"] = AssetKind.GameObject,
        ["worldobject"] = AssetKind.WorldObject, ["worldobjects"] = AssetKind.WorldObject,
        ["spelleffect"] = AssetKind.SpellEffect, ["spelleffects"] = AssetKind.SpellEffect, ["spell"] = AssetKind.SpellEffect,
        ["texture"] = AssetKind.Texture, ["textures"] = AssetKind.Texture,
    };

    /// <summary>The command-line name of a kind ("creature", "gameobject"...).</summary>
    public static string CliName(AssetKind k) => k switch
    {
        AssetKind.GameObject => "gameobject",
        AssetKind.WorldObject => "worldobject",
        AssetKind.SpellEffect => "spelleffect",
        _ => k.ToString().ToLowerInvariant(),
    };

    /// <summary>Search the entries. <paramref name="modifiedPaths"/>: the workspace's edited files;
    /// <paramref name="modifiedOwners"/>: the link tokens of what uses them (<see cref="AssetIndex.OwnersOfTextures"/>).</summary>
    public static List<EntryHit> Run(IReadOnlyList<IndexEntry> entries, EntryQuery q, IReadOnlySet<string> modifiedPaths, IReadOnlySet<string> modifiedOwners)
    {
        var words = q.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<EntryHit>();
        foreach (var e in entries)
        {
            if (q.Kinds != null && !q.Kinds.Contains(e.Kind)) continue;
            if (q.Keys != null && !q.Keys.Contains(e.Key)) continue;
            bool match = true;
            foreach (var w in words)
                if (!e.SearchText.Contains(w, StringComparison.Ordinal)) { match = false; break; }
            if (!match) continue;
            bool mod = e.Path != null && modifiedPaths.Contains(e.Path);
            bool uses = !mod && e.Links.Length > 0 && modifiedOwners.Count > 0 && e.Links.Split(' ').Any(modifiedOwners.Contains);
            if (q.OnlyMyChanges && !mod && !uses) continue;
            list.Add(new EntryHit(e, mod, uses));
        }
        Comparison<EntryHit> cmp = q.Sort switch
        {
            EntrySort.Type => (a, b) => a.Entry.Kind != b.Entry.Kind ? a.Entry.Kind.CompareTo(b.Entry.Kind) : string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase),
            EntrySort.Id => (a, b) => a.Entry.Id.CompareTo(b.Entry.Id),
            EntrySort.Path => (a, b) => string.Compare(a.Entry.Path ?? "", b.Entry.Path ?? "", StringComparison.OrdinalIgnoreCase),
            _ => (a, b) =>
            {
                int c = string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Entry.Kind.CompareTo(b.Entry.Kind);
            },
        };
        if (q.Sort == EntrySort.Name && q.Ascending && words.Length > 0)
        {
            // best matches first: the exact name, then names starting with the text, then names containing every word
            // as a whole word, then names containing the words, then matches on the path/keywords only
            var query = string.Join(' ', words);
            var scores = new Dictionary<EntryHit, int>(list.Count, ReferenceEqualityComparer.Instance);
            foreach (var r in list) scores[r] = Relevance(r.Entry.Name.ToLowerInvariant(), query, words);
            list.Sort((a, b) =>
            {
                int d = scores[a].CompareTo(scores[b]);
                return d != 0 ? d : cmp(a, b);
            });
        }
        else list.Sort(q.Ascending ? cmp : (a, b) => cmp(b, a));
        return list;
    }

    public static int Relevance(string name, string query, string[] words)
    {
        if (name == query) return 0;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 1;
        var nameWords = name.Split(new[] { ' ', '-', '_', '\'', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => nameWords.Contains(w))) return 2;
        if (words.All(w => name.Contains(w, StringComparison.Ordinal))) return 3;
        return 4;
    }
}
