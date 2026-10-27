using CommunityToolkit.Mvvm.ComponentModel;
using WoWData.Core.Index;
using WoWData.Core.M2;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: the 3D view of another version opened read only. The same viewer
/// (scene, animation, geosets, skins, pop-out, compare); only what is shown is found differently: displays from the
/// other version's index (creature displays by model, the creatures and items the client names, item displays by
/// model), looks from <see cref="WoWData.Core.M2.Modern.ModernModelResolver"/>, the model from the modern reader.
/// </summary>
public sealed partial class ModelViewerViewModel
{
    /// <summary>M2: what the modern reader did with the shown model of another version (views read, animations in
    /// external files, parts not shown yet, left-out chunks, approximations); empty for your 1.12 game.</summary>
    [ObservableProperty] private string _formatNotes = "";

    private static string SkinStem(string model, string skin) => SkinShortName(model, System.IO.Path.GetFileNameWithoutExtension(skin));

    private Plan? MakePlanModern(IndexEntry e, EntryDetails d)
    {
        var index = _session.Index!;
        var looks = Looks;
        var parts = e.Key.Split(':');
        var plan = new Plan();
        _ = looks.AnimationNames;
        long displayId = 0;
        switch (parts[0])
        {
            case "cdisplay":
                displayId = long.Parse(parts[1]);
                break;
            case "creature":
                displayId = index.NamedCreatureDisplayIds(long.Parse(parts[1])).FirstOrDefault();
                break;
            case "item":
            case "idisplay":
            {
                long id = parts[0] == "item" ? index.ItemDisplayOf(long.Parse(parts[1])) ?? 0 : long.Parse(parts[1]);
                plan.Subject = ViewerSubject.Item;
                plan.Selected = new VariantItem { DisplayId = id, IsItem = true };
                break;
            }
            default:
                plan.Subject = ViewerSubject.Plain;
                break;
        }
        var model = d.Models.FirstOrDefault(m => m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase));
        long extra = 0;
        if (displayId > 0)
        {
            var row = index.CreatureDisplayModel(displayId);
            if (row.Model != null) model = row.Model;
            extra = row.ExtraId;
        }
        if (model == null)
        {
            if (d.Models.Any(m => m.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("this is a building (WMO): the 3D view shows buildings of other versions later, with the maps.");
            return null;
        }
        plan.Model = model;
        plan.Asset = LoadAsset(model);
        if (displayId > 0) plan.Subject = extra > 0 ? ViewerSubject.Npc : ViewerSubject.Creature;
        if (plan.Subject == ViewerSubject.Plain && index.CreatureDisplaysUsingModel(model).Count > 0) plan.Subject = ViewerSubject.Creature;
        if (plan.Subject == ViewerSubject.Plain && index.ItemDisplaysUsingModel(model).Count > 0) plan.Subject = ViewerSubject.Item;

        switch (plan.Subject)
        {
            case ViewerSubject.Creature:
            case ViewerSubject.Npc:
            {
                List<(long Id, string[] Skins, long ExtraId)> displays;
                if (plan.Subject == ViewerSubject.Npc)
                {
                    // humanoid NPCs share a character model with thousands of displays: only this one (or the creature's own)
                    var ids = parts[0] == "creature" ? index.NamedCreatureDisplayIds(long.Parse(parts[1])) : new List<long> { displayId };
                    displays = ids.Select(i => (i, Array.Empty<string>(), 1L)).ToList();
                }
                else displays = index.CreatureDisplaysUsingModel(model);
                var names = index.NamedCreaturesForDisplays(displays.Select(x => x.Id));
                if (parts[0] == "creature")
                    foreach (var own in index.NamedCreatureDisplayIds(long.Parse(parts[1]))) names[own] = e.Name;
                foreach (var (id, skins, ex) in displays)
                {
                    var skinText = string.Join(" / ", skins.Where(s => s.Length > 0).Select(s => SkinStem(model, s)));
                    string who = names.TryGetValue(id, out var n) ? n : $"Display {id}";
                    plan.Variants.Add(new VariantItem
                    {
                        DisplayId = id, Skins = skins,
                        Label = skinText.Length > 0 ? $"{who} ({skinText})" : ex > 0 ? $"{who} (NPC look)" : who,
                        Detail = $"display {id}" + (skins.Any(s => s.Length > 0) ? " · " + string.Join(", ", skins.Where(s => s.Length > 0).Select(System.IO.Path.GetFileName)) : ""),
                    });
                }
                plan.Variants = plan.Variants.OrderBy(v => v.Label.StartsWith("Display ", StringComparison.Ordinal)).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.DisplayId).ToList();
                plan.Selected = plan.Variants.FirstOrDefault(v => v.DisplayId == displayId) ?? plan.Variants.FirstOrDefault();
                plan.Look = plan.Selected != null ? looks.CreatureDisplay((int)plan.Selected.DisplayId) : looks.DefaultFor(model);
                int skinsCount = plan.Variants.SelectMany(v => v.Skins).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (plan.Subject == ViewerSubject.Creature && plan.Variants.Count > 1)
                    plan.Note = $"{plan.Variants.Count} displays use {System.IO.Path.GetFileName(model)} with {skinsCount} skin{(skinsCount == 1 ? "" : "s")}.";
                break;
            }
            case ViewerSubject.Item:
            {
                var displays = index.ItemDisplaysUsingModel(model);
                var names = index.ItemNamesForDisplays(displays.Select(x => x.Id));
                if (parts[0] == "item" && plan.Selected != null) names[plan.Selected.DisplayId] = e.Name;
                foreach (var (id, tex) in displays.GroupBy(x => x.Id).Select(g => g.First()))
                {
                    string who = names.TryGetValue(id, out var n) ? n : $"Item display {id}";
                    plan.Variants.Add(new VariantItem
                    {
                        DisplayId = id, IsItem = true, Label = $"{who} ({System.IO.Path.GetFileNameWithoutExtension(tex)})", Detail = $"item display {id} · {System.IO.Path.GetFileName(tex)}",
                    });
                }
                plan.Variants = plan.Variants.OrderBy(v => v.Label.StartsWith("Item display ", StringComparison.Ordinal)).ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToList();
                long want = plan.Selected?.DisplayId ?? 0;
                plan.Selected = plan.Variants.FirstOrDefault(v => v.DisplayId == want) ?? plan.Variants.FirstOrDefault();
                if (plan.Selected == null && want > 0) plan.Selected = new VariantItem { DisplayId = want, IsItem = true, Label = $"Item display {want}" };
                plan.Look = plan.Selected != null ? looks.ItemDisplay((int)plan.Selected.DisplayId, model) : looks.DefaultFor(model);
                if (plan.Variants.Count > 1) plan.Note = $"{plan.Variants.Count} item displays use {System.IO.Path.GetFileName(model)}.";
                break;
            }
            default:
                plan.Look = new ModelLook();
                break;
        }
        return plan;
    }

    /// <summary>M3: where the keys of another version's sequence are (" Its keys are in .anim file #123, read when you
    /// chose it."), empty for the sequences in the model and for your 1.12 game.</summary>
    private static string ExternalKeysText(M2Model? m, int s)
    {
        if (m?.Modern?.Animations is not { } ext || !ext.IsExternal(s)) return "";
        var st = ext.Status(s);
        return st.State switch
        {
            WoWData.Core.M2.Modern.ExternalState.Loaded => $" Its keys are in .anim file #{st.File}, read when you chose it.",
            WoWData.Core.M2.Modern.ExternalState.NoFile => " Its keys would be in an .anim file, but the model names none: it stands still.",
            WoWData.Core.M2.Modern.ExternalState.NotLoaded => $" Its keys are in .anim file #{st.File}.",
            _ => $" Its keys are in .anim file #{st.File}, which cannot be used ({st.Why}): it stands still.",
        };
    }

    /// <summary>The modern reader's report of a model (the "Model statistics" note under the numbers).</summary>
    private static string ModernNotes(M2Model m)
    {
        if (m.Modern is not { } mi) return "";
        var lines = new List<string>
        {
            $"Read from another version: {mi.FormatText}; skin file{(mi.ViewsRead == 1 ? "" : "s")} {string.Join(", ", mi.SkinFiles.Take(Math.Max(1, mi.ViewsRead)).Select(f => "#" + f))}.",
        };
        if (mi.SkeletonFile != 0)
            lines.Add($"Skeleton: #{mi.SkeletonFile}" + (mi.SkeletonParentFile != 0 ? $", its parent #{mi.SkeletonParentFile}" + (mi.ParentSequences > 0 ? $" ({mi.ParentSequences} animations from the parent)" : "") : "") +
                      (mi.SkeletonRead ? "" : " (not read)") + ".");
        lines.Add($"Animations: {mi.InlineSequences} in the model" + (mi.SkeletonFile != 0 ? " and its skeleton" : "") + (mi.AliasSequences > 0 ? $", {mi.AliasSequences} sharing another one's keys" : "") +
                  (mi.ExternalSequences > 0 ? $", {mi.ExternalSequences} kept in .anim files (read when you choose one)" : "") + ".");
        if (mi.NotShown.Count > 0) lines.Add("Not shown: " + string.Join("; ", mi.NotShown) + ".");
        if (mi.Approximated.Count > 0) lines.Add("Drawn approximately: " + string.Join("; ", mi.Approximated) + ".");
        if (mi.Ignored.Count > 0) lines.Add("Left out: " + string.Join("; ", mi.Ignored) + ".");
        lines.AddRange(mi.Notes);
        return string.Join("\n", lines.Where(l => !l.EndsWith(": .", StringComparison.Ordinal)));
    }
}
