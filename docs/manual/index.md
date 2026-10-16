# WoW Data Studio user manual

WoW Data Studio lets you find any creature, character, item, game object, tree, spell effect or texture of
World of Warcraft 1.12.1, look at it in 3D, change its textures in your favourite image editor (and its models in
Blender, its maps in Noggit, its data tables and your server's database in the app), check the result, and put your
change into the game and your server. It never changes
the game's own files.

This manual is written for modders, not programmers. Each chapter is a set of short steps you can follow.
In the app, press <kbd>F1</kbd> on any screen to open the chapter about that screen, or use **Help > User manual**.

## Contents

1. [Getting started](getting-started.md): install the app, choose your game folder, connect the server database for names.
2. [The main window](main-window.md): a tour of the search box, filters, results list, badges and details panel.
3. [Finding things](finding-things.md): search tips and examples (a boar, an orc, a tree, an item, a spell effect).
4. [The texture viewer](texture-viewer.md): zoom, transparency, mip levels and the texture's facts.
5. [Editing textures](editing-textures.md): export a PNG, edit it, import it, compare, keep or cancel; ground textures and their `_s` files; glowing (additive) textures.
6. [The 3D model viewer](model-viewer.md): turn, zoom, skins and looks, animations, geosets, views, save an image, compare two skins.
7. [Testing changes in the game](testing-in-game.md): loose files, restarting the game, removing them again.
8. [Releasing your changes](releasing.md): build `patch-3.MPQ`, what is in it, sharing it, backups, uninstalling it.
9. [My changes](my-changes.md): the list of everything you changed, change sets, undoing one file or a whole import.
10. [Models and Blender](model-export-import.md): export a model to Blender and import it back.
11. [Skeleton and animations](skeleton-and-animation.md): bones, animations, attachment points and events in Blender and back: new animations, retimed walks, new bones, moved weapon points; previewing and comparing animations; what the game plays each animation for.
12. [Settings](settings.md): every setting explained.
13. [Troubleshooting and questions](troubleshooting.md): error messages, "the game doesn't show my change" (also for ground textures and glows), where files live.
14. [Safety and undoing everything](safety.md): what the app never touches and how to go back to the original game.
15. [Glossary](glossary.md): MPQ, BLP, DXT, M2, display id, geoset, loose files and other words.
16. [Used in game](used-in-game.md): who uses what in the game: NPCs and their spawn zones, loot, vendors, quests, map placements, spells, and what a texture edit affects.
17. [Maps and terrain](maps-and-terrain.md): the Maps tab (maps, zones, minimap, a tile in 3D), the breakdown of a tile into all its pieces, editing a map in Noggit, importing and comparing the changes, testing and releasing them, and updating your server's maps.
18. [Terrain in Blender](terrain-in-blender.md): a tile and its neighbours in Blender, each tile its own object; editing heights, syncing the shared edges, importing back with the edge check.
19. [Data tables (the DBC editor)](dbc-editor.md): every DBC table of the game (spells, icons, items, creatures, races,
    maps, flight points, talents, skills, sounds...): find rows, follow links, edit with checks and undo, add and copy
    rows, bulk edits, CSV / JSON export and import, and which tables your server reads too ("Copy to server").
20. [Server data (the world database)](server-data.md): your server's items, creatures, quests, game objects, spells,
    loot, vendors, trainers, areas, maps, flight paths, factions and languages: find them, follow every link, edit with
    checks and undo, compare the game's tables with the server's and make them match, export an SQL patch, apply it to
    the server (backed up, undoable), clear the game's cache.
21. [The spell designer](spell-designer.md): make a new spell or clone one ("a spell like Shadow Bolt") with a guided
    form, the tooltip as the game writes it, checks of what the 1.12 game and server can handle, visuals (with a tinted
    and scaled copy), trainers and skill lines; the game's `Spell.dbc` and the server's `spell_template` in step.
22. [The class and talent designer](class-and-talent-designer.md): a class's talent trees on the game's grid (trees,
    talents, ranks, prerequisites, checks), its spells by level and trainers, class masks, and a new class
    (experimental) with what the game and the server still need in code; worked example: the Warrior's Stance
    Disciplines.
23. [The quest-chain designer](quest-chain-designer.md): quest chains as flowcharts (lanes per faction or class, steps,
    links, choice groups), a quest editor in plain fields with the text preview, givers and enders on the map, drop
    sources, "speak to" and "explore" objectives, reward spells, branches, the same chain for the other faction, the
    discipline template, new quest-giver NPCs, checks in plain words; worked example: the Warrior's "Riposte Drill" for
    both factions.
24. [The command line (wds)](command-line.md): every feature of the app as a command, for scripts and automation: every
    command with examples, which command does what the app does, exit codes, JSON output, recipes.
25. [Known issues](known-issues.md): bugs and limitations that are not fixed yet, who they affect and what to do instead.

## The whole workflow in one picture

```
 find it  ->  look at it  ->  export PNG  ->  edit in any image editor  ->  import + compare  ->  Keep this change
                                                                                                  |
                         test in the game (loose files)  <---------------------------------------+
                         release (patch-3.MPQ)           <---------------------------------------+

 maps:  Maps tab -> Edit in Noggit -> save -> Import map changes (check + compare) -> test / release -> Update server maps
        Maps tab -> Open terrain in Blender -> edit heights, Sync shared edges -> Import edited terrain -> test / release

 models: Export for Blender (+ skeleton) -> edit mesh, textures, bones, animations -> Import edited model (checks +
         compare, animations side by side) -> Save into my workspace -> test / release

 tables: Data tables tab -> find a row -> edit (checked, undo) -> Save to my workspace -> test / release -> Copy to server

 server: Server data tab -> find it -> edit (checked, undo) -> Save to my workspace -> Check client and server
         -> Export SQL patch / Apply to server (backup, undo) -> restart or .reload -> clear the game's cache

 spells: Spell designer -> new or clone -> form (tooltip preview, checks) -> visual, trainer, skill line
         -> Save to my workspace (Spell.dbc + spell_template together) -> test / release -> Apply to server
 classes: Classes and talents -> trees and talents on the grid (rank spells from the Spell designer) -> spells by level
          -> Save to my workspace -> Copy tables to the server + Apply to server
 quests: Quest chains -> open a chain (flowchart) or the template -> quests, givers on the map, objectives, rewards,
         branches, the other faction -> checks -> Save to my workspace -> Apply to server -> clear the quest cache -> test
```

Your edits stay in your own workspace until you decide to put them into the game, and every step can be undone
(see [Safety and undoing everything](safety.md)).
