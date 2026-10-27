using System.Text.Json;
using Microsoft.Data.Sqlite;
using WoWData.Core.Casc;
using WoWData.Core.Versions;

namespace WoWData.Core.Index;

/// <summary>
/// M1 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): the queries of an index built by <see cref="ModernIndexBuilder"/>
/// for another version opened read only: file names with their FileDataID, name source and state (the <c>fdid</c>
/// table), the readable and locked records per table, and the details panel with FileDataIDs. A 1.12 index has none
/// of these tables; <see cref="IsModern"/> is false for it and the 1.12 queries are unchanged.
/// </summary>
public sealed partial class AssetIndex
{
    private bool? _modern;

    /// <summary>True for an index of another version (built by <see cref="ModernIndexBuilder"/>).</summary>
    public bool IsModern => _modern ??= Meta().GetValueOrDefault("kind") == "modern";

    /// <summary>Readable and locked (encrypted) records of every table the index was built from.</summary>
    public List<LockedCount> LockedCounts()
    {
        try { return Rows("SELECT tbl, readable, locked, copies FROM locked ORDER BY tbl", r => new LockedCount(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3))); }
        catch (SqliteException) { return new(); }
    }

    /// <summary>The build counts of the index (meta "counts").</summary>
    public Dictionary<string, int> Counts()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(Meta().GetValueOrDefault("counts") ?? "{}") ?? new(); }
        catch (JsonException) { return new(); }
    }

    private ModernFileInfo? FileRow(string where, object arg, CascStorage? storage)
    {
        try
        {
            var row = Rows($"SELECT fdid, path, source, state FROM fdid WHERE {where} LIMIT 1",
                r => (Fdid: (uint)r.GetInt64(0), Path: S(r, 1) ?? "", Source: NameSources.Parse(S(r, 2)), State: S(r, 3)), arg).FirstOrDefault();
            if (row.Path == null || row.Path.Length == 0) return null;
            var state = row.State != null ? ModernFileInfo.ParseState(row.State) : storage?.State(row.Fdid) ?? CascFileState.Unknown;
            return new ModernFileInfo(row.Fdid, row.Path, row.Source, state);
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>The names table of this index as <see cref="IModernNames"/> (states of files the index did not
    /// record are asked from <paramref name="storage"/>).</summary>
    public IModernNames ModernNames(CascStorage storage) => new IndexNames(this, storage);

    private sealed class IndexNames : IModernNames
    {
        private readonly AssetIndex _ix;
        private readonly CascStorage _s;
        private int? _count;

        public IndexNames(AssetIndex ix, CascStorage s)
        {
            _ix = ix;
            _s = s;
        }

        /// <summary>The index's row of a FileDataID, else (a file the index did not record, such as a texture a model
        /// names itself) "#id" with the client's state; null when the client has no such FileDataID.</summary>
        public ModernFileInfo? ByFileDataId(uint fdid) =>
            _ix.FileRow("fdid=$0", (long)fdid, _s) ?? (_s.Root.Contains(fdid) ? new ModernFileInfo(fdid, $"#{fdid}", NameSource.Number, _s.State(fdid)) : null);

        public ModernFileInfo? ByPath(string path)
        {
            var p = path.Replace('/', '\\');
            var hit = _ix.FileRow("path=$0", p, _s);
            if (hit != null) return hit;
            var m = NumberName.Match(p);
            if (m.Success && uint.TryParse(m.Groups[1].Value, out var id) && ByFileDataId(id) is { } byId)
                return byId.Source == NameSource.Number ? byId with { Path = p } : byId;
            return _s.FileDataIdOf(p) is { } h ? ByFileDataId(h) : null;
        }

        private static readonly System.Text.RegularExpressions.Regex NumberName = new(@"^#(\d+)(\.[A-Za-z0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public int Count => _count ??= (int)_ix.Rows("SELECT COUNT(*) FROM fdid WHERE source <> 'number'", r => r.GetInt64(0)).FirstOrDefault();

        public IReadOnlyList<ModernFileInfo> Glob(string pattern, int limit = int.MaxValue)
        {
            // * and ? to LIKE (with \ as the escape for the LIKE wildcards in names)
            var p = pattern.Replace('/', '\\');
            var like = new System.Text.StringBuilder();
            foreach (var ch in p)
                like.Append(ch switch { '*' => "%", '?' => "_", '%' => "\\%", '_' => "\\_", '\\' => "\\\\", _ => ch.ToString() });
            try
            {
                return _ix.Rows("SELECT fdid, path, source, state FROM fdid WHERE path LIKE $0 ESCAPE '\\' AND source <> 'number' ORDER BY path COLLATE NOCASE LIMIT $1",
                    r => new ModernFileInfo((uint)r.GetInt64(0), r.GetString(1), NameSources.Parse(S(r, 2)),
                        S(r, 3) is { } st ? ModernFileInfo.ParseState(st) : _s.State((uint)r.GetInt64(0))), like.ToString(), (long)Math.Min(limit, int.MaxValue));
            }
            catch (SqliteException) { return Array.Empty<ModernFileInfo>(); }
        }
    }

    // ------------------------------------------------------------------ details

    /// <summary>The details panel of an entry of another version: the source chain, the files with FileDataID, name
    /// source and state (encrypted ones with their key name), the textures, who uses them, locked records.</summary>
    public EntryDetails DetailsModern(IndexEntry e, Catalog160 cat, IModernNames names)
    {
        var d = new EntryDetails { Title = e.Name, Subtitle = $"{e.KindLabel}" + (e.Id > 0 ? $" {e.Id}" : "") };
        var parts = e.Key.Split(':', 2);
        var kind = parts[0];
        var arg = parts.Length > 1 ? parts[1] : "";
        long.TryParse(arg, out var id);
        switch (kind)
        {
            case "cdisplay":
                ModernCreatureDisplay(d, id, null, cat, names);
                Locked(d, "CreatureDisplayInfo", "creature displays");
                break;
            case "creature":
            {
                var c = Rows("SELECT name, title, display1, display2, display3, display4 FROM named_creature WHERE entry=$0",
                    r => (Name: r.GetString(0), Title: S(r, 1) ?? "", Displays: new[] { L(r, 2), L(r, 3), L(r, 4), L(r, 5) }.Where(x => x > 0).Distinct().ToList()), id).FirstOrDefault();
                if (c.Name == null) break;
                d.Title = c.Name;
                d.Subtitle = $"Creature {id}" + (c.Title.Length > 0 ? $" <{c.Title}>" : "") + " · named by the client (Creature table)";
                bool first = true;
                foreach (var disp in c.Displays)
                {
                    ModernCreatureDisplay(d, disp, first ? $"{c.Name} (creature {id})" : null, cat, names);
                    first = false;
                }
                if (c.Displays.Count > 1) d.Facts.Add($"This creature picks one of {c.Displays.Count} displays: {string.Join(", ", c.Displays)}.");
                d.Facts.Add("The client names only a few creatures itself; the others are named by the server, so they are listed by their display.");
                Locked(d, "Creature", "named creatures");
                break;
            }
            case "idisplay":
                ModernItemDisplay(d, id, null, names);
                Locked(d, "ItemDisplayInfo", "item displays");
                break;
            case "item":
            {
                var it = Rows("SELECT name, display_id FROM item WHERE entry=$0", r => (Name: r.GetString(0), Display: r.GetInt64(1)), id).FirstOrDefault();
                if (it.Name == null) break;
                d.Title = it.Name;
                d.Subtitle = $"Item {id} · named by the client (ItemSparse)";
                if (it.Display > 0) ModernItemDisplay(d, it.Display, $"{it.Name} (item {id})", names);
                else d.Facts.Add("No item appearance of this item is readable (ItemModifiedAppearance / ItemAppearance).");
                Locked(d, "ItemSparse", "item names");
                break;
            }
            case "godisplay":
                ModernGameObjectDisplay(d, id, null, cat, names);
                Locked(d, "GameObjectDisplayInfo", "game object displays");
                break;
            case "go":
            {
                var g = Rows("SELECT name, display_id FROM gameobject WHERE entry=$0", r => (Name: r.GetString(0), Display: r.GetInt64(1)), id).FirstOrDefault();
                if (g.Name == null) break;
                if (g.Name.Length > 0) d.Title = g.Name;
                d.Subtitle = $"Game object {id} · placed by the client (GameObjects table)";
                ModernGameObjectDisplay(d, g.Display, $"{d.Title} (game object {id})", cat, names);
                break;
            }
            case "spell":
            {
                var s = Rows("SELECT name, model FROM spell_effect WHERE id=$0", r => (Name: r.GetString(0), Model: S(r, 1)), id).FirstOrDefault();
                if (s.Name == null) break;
                d.Title = s.Name;
                d.Subtitle = $"Spell effect {id}";
                d.Chain.Add($"SpellVisualEffectName {id}");
                if (s.Model != null)
                {
                    d.Chain.Add(s.Model);
                    d.Models.Add(s.Model);
                    AddFile(d, s.Model, "model", names);
                    ModernEmbedded(d, s.Model, cat, names);
                }
                var uses = Rows("SELECT spell_id, spell_name, group_concat(role, ', ') FROM spell_effect_use WHERE effect_id=$0 GROUP BY spell_id ORDER BY spell_name, spell_id LIMIT 60",
                    r => $"{r.GetString(1)} - spell {r.GetInt64(0)}, {r.GetString(2)}", id);
                d.UsedBy.AddRange(uses);
                if (uses.Count == 0) d.Facts.Add("No readable spell shows this effect (SpellXSpellVisual -> SpellVisual -> kits and missiles).");
                Locked(d, "SpellVisualEffectName", "spell effects");
                break;
            }
            case "file":
            {
                var path = arg;
                d.Title = System.IO.Path.GetFileName(path);
                d.Subtitle = e.KindLabel;
                d.Chain.Add(path);
                var f = AddFile(d, path, e.Kind == AssetKind.Texture ? "this texture" : "this model", names);
                if (e.Kind == AssetKind.Texture)
                {
                    d.Textures.Add(new TextureRef(path, "this texture", f?.Readable == true ? "CASC" : null, f == null ? "" : f.Facts));
                    d.UsedBy.AddRange(TextureUsers(path));
                    var models = ModelsUsingTexture(path);
                    if (models.Count > 0) d.Facts.Add($"Used by {models.Count} model{(models.Count == 1 ? "" : "s")}: {string.Join(", ", models.Take(5).Select(System.IO.Path.GetFileName))}{(models.Count > 5 ? ", ..." : "")}.");
                }
                else
                {
                    d.Models.Add(path);
                    ModernEmbedded(d, path, cat, names);
                    foreach (var u in Rows("SELECT id, name FROM spell_effect WHERE model=$0", r => $"spell effect {r.GetInt64(0)} {r.GetString(1)}", path)) d.UsedBy.Add(u);
                }
                break;
            }
        }
        foreach (var f in d.Files.Where(f => f.State == "encrypted"))
            if (KeyName(cat.Storage, f.FileDataId) is { } key) d.Facts.Add($"{System.IO.Path.GetFileName(f.Path)} is encrypted (key {key}): it cannot be shown.");
        foreach (var f in d.Files.Where(f => f.State == "not downloaded"))
            d.Facts.Add($"{System.IO.Path.GetFileName(f.Path)} is not on this computer (Battle.net downloads it when the game needs it).");
        return d;
    }

    /// <summary>The key name of an encrypted file (never its value), or null.</summary>
    private static string? KeyName(CascStorage s, uint fdid)
    {
        try
        {
            s.ReadFileDataId(fdid);
            return null;
        }
        catch (CascEncryptedException e) { return e.KeyName ?? "unknown"; }
        catch (Exception e) when (e is CascMissingException or InvalidDataException) { return null; }
    }

    private void Locked(EntryDetails d, string table, string what)
    {
        var l = LockedCounts().FirstOrDefault(x => x.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
        if (l != null && l.Locked > 0) d.Facts.Add($"{table}: {l.Readable:N0} of {l.Total:N0} {what} readable, {l.Locked:N0} locked (encrypted, no key).");
    }

    private ModernFileRef? AddFile(EntryDetails d, string path, string role, IModernNames names)
    {
        if (d.Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return d.Files.First(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        var info = names.ByPath(path);
        if (info == null) return null;
        var r = new ModernFileRef(info.Path, role, info.FileDataId, info.Source, info.StateText);
        d.Files.Add(r);
        return r;
    }

    private void AddTexture(EntryDetails d, string path, string role, IModernNames names)
    {
        if (d.Textures.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var f = AddFile(d, path, role, names);
        d.Textures.Add(new TextureRef(path, role, f?.Readable == true ? "CASC" : null, f?.Facts ?? "not in this client"));
    }

    private void ModernEmbedded(EntryDetails d, string model, Catalog160 cat, IModernNames names)
    {
        var mf = names.ByPath(model);
        if (mf == null || !mf.Readable || !model.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) return;
        var ids = cat.EmbeddedTextures(mf.FileDataId);
        int runtime = ids.Count(i => i == 0);
        foreach (var t in ids.Where(i => i != 0).Distinct())
        {
            var info = names.ByFileDataId(t);
            AddTexture(d, info is null or { Source: NameSource.Number } ? $"#{t}.blp" : info.Path, "model texture", names);
        }
        if (runtime > 0) d.Facts.Add($"{runtime} texture{(runtime == 1 ? " is" : "s are")} chosen by the game at run time (the display's skins).");
    }

    private void ModernCreatureDisplay(EntryDetails d, long disp, string? head, Catalog160 cat, IModernNames names)
    {
        var cd = Rows("SELECT model, extra_id, race, sex FROM creature_display WHERE id=$0", r => (Model: S(r, 0), Extra: L(r, 1), Race: L(r, 2), Sex: L(r, 3)), disp);
        if (cd.Count == 0)
        {
            d.Facts.Add($"Display {disp} is not readable in CreatureDisplayInfo (encrypted, or not in this client).");
            return;
        }
        var (model, extra, race, sex) = cd[0];
        var files = Assets("creature", disp);
        var skins = files.Where(f => f.Role.StartsWith("skin", StringComparison.Ordinal)).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)).ToList();
        var chain = new List<string>();
        if (head != null) chain.Add(head);
        chain.Add($"display {disp}");
        if (model != null) chain.Add(model);
        if (skins.Count > 0) chain.Add((skins.Count == 1 ? "skin: " : "skins: ") + string.Join(", ", skins));
        if (d.Chain.Count == 0) d.Chain.AddRange(chain);
        else d.Facts.Add("Also: " + string.Join(" → ", chain));
        if (model != null && !d.Models.Contains(model))
        {
            d.Models.Add(model);
            AddFile(d, model, "model", names);
        }
        foreach (var f in files.Where(f => f.Role != "model")) AddTexture(d, f.Path, f.Role, names);
        if (model == null && cat.CreatureDisplay((int)disp) is { } raw)
            d.Facts.Add(raw.Model == 0
                ? $"The display's model (CreatureModelData {raw.ModelId}) names no model file."
                : $"The display's model (CreatureModelData {raw.ModelId}) is FileDataID {raw.Model}, which this client does not have.");
        if (extra > 0) d.Facts.Add($"A humanoid NPC look (CreatureDisplayInfoExtra {extra}): race {race}, {(sex == 1 ? "female" : "male")}; its face, hair and clothes are in the baked texture.");
        if (model != null) ModernEmbedded(d, model, cat, names);
        if (model != null)
        {
            var sets = CreatureSkinSets(model);
            int displays = sets.Sum(s => s.Displays.Count);
            if (displays > 1) d.Facts.Add($"{displays} displays use {System.IO.Path.GetFileName(model)}.");
        }
    }

    private void ModernItemDisplay(EntryDetails d, long disp, string? head, IModernNames names)
    {
        var files = Assets("item", disp);
        var chain = new List<string>();
        if (head != null) chain.Add(head);
        chain.Add($"item display {disp}");
        var models = files.Where(f => f.Role.StartsWith("model", StringComparison.Ordinal) && !f.Role.StartsWith("model texture", StringComparison.Ordinal)).ToList();
        if (models.Count == 1) chain.Add(models[0].Path);
        else if (models.Count > 1) chain.Add($"{models.Count} models (one per race and sex, or per hand)");
        var tex = files.Where(f => f.Role.StartsWith("model texture", StringComparison.Ordinal)).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)).Distinct().ToList();
        if (tex.Count > 0) chain.Add("texture: " + string.Join(", ", tex));
        d.Chain.AddRange(chain);
        if (d.Title.Length == 0) d.Title = models.Count > 0 ? System.IO.Path.GetFileNameWithoutExtension(models[0].Path) : $"item display {disp}";
        foreach (var m in models)
        {
            if (!d.Models.Contains(m.Path)) d.Models.Add(m.Path);
            AddFile(d, m.Path, m.Role, names);
        }
        foreach (var f in files.Where(f => !models.Contains(f))) AddTexture(d, f.Path, f.Role, names);
        if (models.Count == 0) d.Facts.Add("This item has no 3D model (only an icon and/or body textures).");
        var named = Rows("SELECT COUNT(*) FROM item WHERE display_id=$0", r => r.GetInt64(0), disp).FirstOrDefault();
        if (named > 1) d.Facts.Add($"{named} items of the client use item display {disp}.");
    }

    private void ModernGameObjectDisplay(EntryDetails d, long disp, string? head, Catalog160 cat, IModernNames names)
    {
        var files = Assets("gameobject", disp);
        var model = files.FirstOrDefault(f => f.Role == "model")?.Path;
        if (head != null) d.Chain.Add(head);
        d.Chain.Add($"game object display {disp}");
        if (model == null)
        {
            d.Facts.Add($"Display {disp} names no model file that this client has.");
            return;
        }
        d.Chain.Add(model);
        d.Models.Add(model);
        AddFile(d, model, "model", names);
        if (model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            d.Facts.Add("A building (WMO): its textures and parts are listed in a later milestone (Maps).");
        else ModernEmbedded(d, model, cat, names);
    }
}
