# Changelog

What changed in each release of WoW Data Studio (the desktop app, `app\`). Bugs that are known and not fixed yet are
listed in the manual's [Known issues](docs/manual/known-issues.md); when one is fixed it is removed from that list and
recorded here. The phases before 0.6.1 are described in `ROADMAP.md`.

## 0.13.3 (2026-10-16)

The cause of the open issue of 0.13.2 (*a designed spell given as a quest reward is not learned*) was found in the
server's and the game's code, proven on a copy of the test server and fixed; the in-game test (`tests\ingame
esults20261016_1900_spellids`) then saw the designed spell learned from the quest reward ("You have learned a new spell: WDS
Shadow Lance (Rank 1)", in the spell book's Destruction tab), listed and bought at the class trainer, and cast (its
tinted missile, *Your WDS Shadow Lance hits Mottled Boar for 791 Shadow damage*).

### Fixed

* **Spell designer: designed spells could never be learned or used.** New spells got IDs from the custom ID range
  (900000 and up). The VMaNGOS world server casts no spell above 40000: its spell code returns at once, without a log line,
  when the ID is above `MAX_SPELL_ID` (40000, `Spell.h` / `Spell::cast`). Every way of learning a spell casts a learn
  spell (the quest giver at the turn-in, the trainer, the re-teach at login), so the learn spell 900001 taught nothing,
  and spell 900000 itself would have done nothing. Besides, the 1.12 game cannot know a spell above 65535: the packets
  that fill the spell book carry 16-bit IDs (spell 900000 arrives as 48032), which is why even `.learn all_trainer`
  showed nothing. A test server copy (the game's own learn spell copied byte for byte to a new ID above 40000 teaches
  nothing; the designed rows renumbered to 39000/39001 are taught by the quest and kept after a new login) confirmed it.
  * New spells (the spell, talent, class and quest designers; **Add new** in the server's `spell_template`) now get IDs
    from a **Spell ID range** of their own: **Settings > Spell ID range** (`wds settings set spell-ids`), 35000-39999 by
    default, at most 40000 (the game's own spells end at 33392). The other rows keep the custom ID range 900000-999999.
  * **Change ID** and **Fix all spell IDs** in the Spell designer (`wds spells renumber <spell>` / `--all`) move spells
    you made with an unusable ID into the range, with everything that names them: learn spells, skill lines, talent
    ranks, item sets and creature spell lists in the game's tables; quests, trainer rows, starting spells, items and
    creatures on the server. Spells made with 0.12.0-0.13.2 need this once (then save, deliver, apply and restart the
    world server).
  * The checks show an error for a spell ID or trigger spell above 40000 (the spell designer, the quest checks and the
    Server data checks of `spell_template`, `quest_template`, `npc_trainer`, `skill_line_ability`,
    `playercreateinfo_spell`, `spell_learn_spell`, items and creatures); **Set reward spell** refuses such a spell; a
    learn spell above 40000 is no longer reused for a trainer or quest.
* **Spell designer tab: a new spell's visual or a talent's rank spells could seem missing** (*SpellVisual ... does not
  exist in the game's table*, *There is no Spell with ID ...*) when the tab was loaded twice at once (a tab switch and a
  command): two design sessions were opened and the designer of one could be paired with the session of the other. The
  tab now loads once (seen in the screenshot run).
* Manual: the spell designer said *Spell IDs up to 999999 work in 1.12*; it now explains what a spell needs to be
  learnable on VMaNGOS (IDs, the learn spell, trainer rows and quest columns, the skill line for class trainers).

### Changed

* Manual pictures 119-136 (the designers and the quest chains) made again: the Spell designer shows **New ID**,
  **Change ID** and **Fix all spell IDs**, and the spells have IDs from 35000.
* Known issues: *A designed spell given as a quest reward is not learned* is fixed (above); the quest-chain entry says
  the reward spell is learned now.
* The in-game test runner (`tests\ingame`) knows the spell ID range (its server safety rule, the leftover check) and its
  test spell now has a skill line, so the class trainer's window lists it.

## 0.13.2 (2026-10-16)

Fixes found by the first test of the designers in the real game (`tests\ingame\run_designers.ps1`: a spell, a talent and
a quest chain made with the designers, applied to a running VMaNGOS server and tried with a game master character).

### Fixed

* **Talent designer: a new talent made the rest of its tree disappear in the game.** *Add talent here* (`wds talents add`)
  put the new talent's row at the end of `Talent.dbc` (the next free ID). The 1.12 game reads each talent tree as one
  group of rows that follow each other and starts the tree again at every row where the tree changes, so it showed only
  the new talent: in the test the Warlock's Destruction tree had 1 talent instead of 17. A new talent's row now goes
  right after the other rows of its tree, and **Save to my workspace** (and every `wds talents` command that changes
  a tree, as they save) puts the rows of every tree together again, which also repairs a `Talent.dbc` saved by
  0.12.0-0.13.1 (the save says how many rows it moved).
  **Check the tree** warns about a tree whose rows are in separate groups (for example after adding rows in the Data
  tables tab).
* **Spell designer: the learn spell made for a trainer copied the spell's description.** The 1.12 trainer window shows
  a learn spell's own description when it has one, filled in with the learn spell's numbers (*causing 1 to 0 Shadow
  damage*), and the taught spell's description only when it is empty. New learn spells are now copies of the game's
  own (Shadow Bolt (Rank 2)'s learn spell 1381: no description, effect 1 without a target, 100 yards, the learn glow)
  instead of a spell of the app's own making (effect 1 on *Unit*, range *self*); the spell checks warn about a learn
  spell whose description has `$` numbers (empty it). Still open: in the game test the server did not teach a designed
  spell given as a quest reward (see Known issues).
* **Spell designer: no warning that a class trainer hides a spell without a skill line.** The game's trainer window of
  a class (or pet) trainer leaves out every spell that has no skill line (`SkillLineAbility`), although the server sends
  it: in the test the trainer window was empty. *Add to trainer* said so once; now the spell's checks warn too, as long
  as a class trainer teaches it and it has no skill line.
* **Quest chains, Test in game:** the GM commands `.quest add` and `.quest complete` need an administrator account on
  VMaNGOS (GM level 4 or higher); a game master (level 3) only gets *There is no such subcommand*. The text now says so
  and gives the game master's way: `.damage 100000` on each creature to kill, which counts as the character's kill.

### Changed

* Known issues: the designers and the quest-chain designer were tried in the running game; the entries *not tried in
  the running game* are replaced by what the test found (a reward spell is not learned; talent rows added in the Data
  tables tab; a new quest giver keeps the copied NPC's AI name).

### Tests

* `DesignerTests`: a talent added to the Destruction tree stays with its tree (checked with the client's own way of
  reading `Talent.dbc`: the last group of a tree is what the game shows), the move is one undo step, and the saved file
  shows all 17 talents; a tree split as 0.13.1 saved it is reported by the check and put together by the save, every
  other tree as in the game's own file; a new learn spell is a copy of the game's own (no implicit target, its range,
  its visual, no description, the taught spell's icon).
* `DesignerServerTests`: the class-trainer warning (and its end once the spell has a skill line), the learn spell's
  empty description and the warning for a learn spell's description with `$` numbers.

## 0.13.1 (2026-10-14)

A fix found by the first hot-reload test in the real game.

### Fixed

* **Reload in running game** and `wds changes reload` always said *The game is not running from ...* while the game ran,
  when the game was started by the project's launcher `WoWDebug.exe` (which also loads `wowcode.dll`, so: always, for
  hot reload). That game refuses to be opened by other programs, even only to read which program file it runs, so the
  app could not tell which folder it runs from and skipped it. The app now reads the program file from Windows' process
  list, which needs no access to the game, and finds the game (32-bit, seen from the 64-bit app) in its folder; the
  hot-reload connection is the one of `WoW.exe` itself (`\\.\pipe\wowcode-hotreload-<WoW.exe's process id>`), never
  `WoWDebug.exe`'s. Folder names are compared in any spelling (letter case, a trailing `\`, short `WOWCUS~1` names).
* **Clear the game's cache** (Server data) refused while *any* game started by `WoWDebug.exe` ran, also one from another
  game folder (it could not tell the folder either); it now refuses only for a game running from the chosen folder.
* When another WoW Data Studio window or a `wds` command is talking to the same game (the game takes one hot-reload
  connection at a time), the message said only *the game's hot-reload connection is busy or gone*; it now says that
  another WoW Data Studio window or a `wds` command is talking to this game. (This never caused *The game is not
  running*: that came only from the bug above.)

### Changed

* The first test in the real game (`tests\ingame\hotreload`, `Client_visual`, both hook sets): a texture, a creature model and a placed doodad were reloaded live without a restart. The known issue *Hot reload needs the next client update, and has not been tried in the game yet* is gone (the client has it since 2026-10-14); a new one is listed: a changed creature model is reported as *needs a relog* although it already changed (the game side's report).
* Developers: `WoWData.Core\HotReload\ProcessImage.cs` (the program file of a process, with the handle-free fallback
  through `NtQuerySystemInformation(SystemProcessIdInformation)` and NT-to-drive path conversion), shared by
  `WindowsGameLocator` (now over an `IProcessQuery` layer the tests can replace) and `WdbCache.RunningGames`.

### Tests

* `GameLocatorTests`: a real 32-bit process named `WoW.exe` (a copy of the 32-bit `cmd.exe`) whose security descriptor
  refuses everyone, as the game's does: the 0.13.0 way reads nothing, the new one finds it in its folder (several
  spellings), not in another one, gives `ProbeAsync` a game instead of *not running*, and `WdbCache` sees it in its
  folder only; NT path conversion (volumes 1 vs 10, network shares, `\??\`); a faked query layer (folder matching,
  newest game first, unreadable processes, short names, the pipe name with `WoW.exe`'s process id); a one-instance
  pipe held by another client: the status and the reload name the other app instead of *not running*.

## 0.13.0 (2026-10-14)

The quest-chain designer: quest chains as flowcharts, a quest editor in plain fields, chains built for both factions.

### Added

* **Quest chains** (tab and **Tools > Quest chains**; `wds quests find | show | fields | chain | check | new | set |
  branch | join | giver | ender | objective | talk | explore | reward | drops | npcs | npc | place | duplicate | template |
  preview | categories | export | import | delete | test-in-game`; manual chapter
  [The quest-chain designer](docs/manual/quest-chain-designer.md)):
  * any quest's chain as a flowchart: every quest linked by PrevQuestId, NextQuestId, NextQuestInChain,
    BreadcrumbForQuestId or an exclusive group (both ways), in lanes per faction / race / class, in steps, with arrows
    per kind of link, choice ("one of") and "all of" frames, problem badges; zoom and move; `--mermaid` on the command
    line;
  * the quest editor in plain fields: texts (with the client's own `$N` / `$C` / `$R` / `$G` / `$B` preview), levels,
    classes and races as checkboxes, reputation, the quest log category (zones and QuestSort.dbc categories), flags,
    chain links, objectives (kill, use, cast on, collect), rewards (items, choices, money, experience, reputation, a
    learned spell made through the spell designer), givers and enders (an NPC picker by name with the spawns, the NPC's
    model and "Show on map");
  * drop sources of the items to collect (loot, chests, containers, pickpocketing, skinning, fishing, vendors, quests,
    spells, reference loot), "Add drop source" (a quest drop) and "Open in the loot editor";
  * "speak to" objectives (a talk option shown while the quest is in the log, VMaNGOS's talk credit) and "explore"
    objectives (a new area trigger in AreaTrigger.dbc and `areatrigger_template`);
  * building chains: add after / before, a branch (a choice of follow-ups with their rewards, or parallel ones), a join
    (any one or all of), delete with relinking; the same chain for the other faction with swaps of NPCs, creatures,
    items, zones, factions, spells and words of the texts; the "discipline chain" template of the Warrior's Stance
    Disciplines (talk -> kill N -> return -> learn the discipline); new quest-giver NPCs (a copy of a creature's look,
    custom IDs) placed at a map position, picked on the Maps tab's map (pins, "Pick on the map"); new quest log
    categories;
  * the checks in plain words: unreachable quests and circles, level gaps, missing givers / enders and quest-giver
    flags, NPCs nowhere in the world, objectives that cannot be completed (no drop source, nothing spawned, friendly
    targets, event quests nothing completes, unique items), faction mismatches (hostile givers, impossible race and
    class), and the 1.12 client's text limits (`QuestCache_Read`, the quest window); a text that would overflow the
    game's quest cache is refused;
  * delivery as today: one change set in My changes (server rows and game tables), Apply to server / SQL patch / undo,
    the quest-cache reminder, and **Test in game** (the GM commands `.quest add`, `.go creature id`, `.quest complete`);
  * CSV / JSON of whole chains (`wds quests export` / `import`, round trip exact).
* Worked examples in the manual: the Whirlwind weapon chain opened; the Warrior's "Riposte Drill" for both factions
  (Sentinel Hill and the Crossroads); a reward choice that branches; apply and undo.
* The Maps tab shows pins (the quest givers' spawns) and lets another tab pick a position on the map.
* Feature registry: 38 new features, each in the app and on the command line (or app-only with the reason).

### Fixed

* A data table's row lookup could miss a row that had just been added when another part of the app read the same table
  at that moment (rarely: a designed spell "does not exist on the server" right after it was made).
* The server editor could keep a stale "does not exist" answer for a row added at the same moment by another part of
  the app.
* `quest_template.RewXP`'s hint said 0 meant "computed from the level"; VMaNGOS gives exactly RewXP, so 0 is no
  experience.

### Changed

* Developers: `WoWData.Core\Designers\Quest*.cs` (`QuestDesigner`, `QuestForm`, `QuestChains`, `QuestRules`,
  `QuestText`, `QuestExchange`, `QuestBuild`, `QuestNpcs`); `ThrowawayMySql.RunSqlFile`; `ServerFixtureBuilder
  --supplement` writes the quest-chain supplement of the test database (`serverdb\quest_chains_fixture.sql`, read-only
  from a server: whole chains, their NPCs, items, loot, spells and the Riposte Drill's outposts).

### Tests

* `QuestDesignerTests` (on the throwaway MySQL server with the quest-chain supplement: the chain graphs of the Whirlwind
  weapon, Brutal armour, Dreadsteed and Valley of Trials chains; the Riposte Drill for both factions with new NPCs, the
  template, a branch, a join, add before / after, the Horde copy, save, apply and undo; drop sources, speak-to and
  explore objectives; every check broken on purpose; the token preview against the client's rules; CSV / JSON round
  trips; delete and relink; categories), `QuestCliTests` (every `wds quests` command with its exit codes and JSON, and
  the Quest chains tab and `wds` making the same rows and files).

## 0.12.0 (2026-10-14)

The designers: guided, checked editors for spells, talent trees and classes, the game's and the server's rows in step.

### Added

* **Spell designer** (tab and **Tools > Spell designer**; `wds spells find | show | fields | new | clone | set | check |
  tooltip | explain | icons | visual | learn | sync`; manual chapter [The spell designer](docs/manual/spell-designer.md)):
  * a new spell or a clone of an existing one, with the first free ID of the custom ID range in both `Spell.dbc` and
    `spell_template`;
  * the form, grouped like a player thinks: basics (name, rank, description, buff text, icon, school, levels, spell
    family), casting (cast time, cooldowns, global cooldown, range, power and cost, reagents, tools, missile speed,
    interrupts), targeting, effects 1-3 (effect and aura type with plain-language explanations and what their misc value
    holds, value as "10-20", targets, radius, ticks, chains, trigger spell, created item), mechanics and flags (named
    checkboxes as names: `+Passive`), procs, duration and stacks, visuals, server-only values (spell power coefficients,
    VMaNGOS custom flags); friendly units, with the helper rows (SpellCastTimes, SpellDuration, SpellRange, SpellRadius,
    SpellIcon) found or added;
  * the tooltip preview, with the `$` variables filled in as the client's own `SpellText_ExpandVariable` does;
  * checks of what the 1.12 client and the VMaNGOS server cannot handle, from the server's effect and aura handler
    tables and the client's code;
  * visuals: a whole visual or single parts (precast, cast, missile, impact, aura, channel, area) of other spells; a
    duplicate with its own kits and copied effect models, scaled and tinted; a 3D view of the effect models;
  * learning: trainer entries (`npc_trainer` / `npc_trainer_template`) with the spell's *learn* spell made when needed,
    SkillLineAbility rows mirrored to `skill_line_ability`;
  * one save: the client tables, the copied models and textures and the server rows in one change set.
* **Classes and talents** (tab and **Tools > Classes and talents**; `wds talents trees | show | add-tree | set-tree |
  remove-tree | add | set | remove | check`, `wds classes list | show | assign | mask | new | engine-report`; manual
  chapter [The class and talent designer](docs/manual/class-and-talent-designer.md)):
  * talent trees on the 1.12 grid with icons, background and arrows; add / remove trees; place, move and remove
    talents; rank spells made from a template spell; prerequisites; checks against the client's talent frame and the
    server (the 27 stock trees pass);
  * a class's spells by level (trainers, skill lines, automatic, starting spells, talents), assigning a level and
    trainers, its skill lines;
  * class masks of SkillLineAbility, SkillRaceClassInfo, TalentTab, `item_template`, `quest_template` and
    `skill_line_ability`;
  * EXPERIMENTAL: a new class's data (ChrClasses with its own spell family, CharBaseInfo, shared skill lines,
    CharStartOutfit; `playercreateinfo`, `_spell`, `_action`, `_item`, `player_classlevelstats`, `player_levelstats`)
    and the report of what the client and the server still need in code (`wds classes engine-report`).
* Worked examples in the manual: Shadow Bolt cloned into "Shadow Lance", a new spell at a trainer at level 20, a talent
  tree for a class, the Warrior's Stance Disciplines mapped onto the tables.
* Feature registry: 35 new features, each in the app and on the command line (or app-only with the reason).

### Fixed

* `spell_template.effectItemType1`-3 were always checked as items. For aura effects they hold the spell family mask the
  aura changes (Tactical Mastery: 8388608), so the Server data tab refused such rows and **Make the server match the
  game** failed for those spells. They are now items only for a *Create item* effect.

### Changed

* The Data tables editor sessions can see other open tables' unsaved rows (the designers edit several tables at once),
  and the server editor's checks of links to the game's tables see the designers' unsaved rows.
* Developers: `WoWData.Core\Designers\` (`DesignSession`, `SpellDesigner`, `SpellForm`, `SpellText`, `SpellRules`,
  `SpellVisuals`, `TalentDesigner`, `ClassDesigner`); `ServerConsistency.SyncFromClient`; shared change sets for
  `DbcService.Save` and `ServerEditor.Save`.

### Tests

* `DesignerTests` (tooltips against the game's own texts, the form's units, new / clone / save, the checks, visuals
  composed and duplicated with the scaled vertices checked, learn spells and skill lines, the 27 stock talent trees,
  every talent rule broken on purpose, the class view, class masks, a new class, CSV / JSON round trips),
  `DesignerServerTests` (on the throwaway MySQL server: both sides in step after a save, an apply to the throwaway server
  and its undo, trainers and skill lines, the class view from the trainers, rank spells on the server, a new class's
  server rows and masks), `DesignerCliTests` (every command with its exit codes and JSON, the server side through the
  command line, and the app's two tabs and `wds` making byte-identical files).

## 0.11.0 (2026-10-14)

Hot reload: the running game reads changed files again, without a restart.

### Added

* **Reload in running game** in **My changes** (`wds changes reload`, `--wait` for the report, `--status`, `--json`): the
  game that runs from the game folder reads your installed changes again and says per file what is live now and what
  needs a relog or a restart. Textures are re-uploaded in place (same size and format), models are taken out of the
  game's model cache and creatures, characters, objects and placed doodads get the new one, buildings and terrain are
  reloaded around the character (the game's own teleport path), data tables are updated in place when their rows stay
  the same. The line under the buttons says whether the running game takes reloads (checked every few seconds).
* After **Test in game** (textures, maps, tables), **Remove from game** and a **Revert** that took a loose file out of the
  game folder, the app offers **Reload in running game** when the game runs with hot reload (`wds changes install --reload`,
  `wds changes remove --reload`, `wds changes revert --reload`, `wds changes revert-set --reload`,
  `wds maps test-in-game --reload`).
* **Build release patch (patch-3.MPQ)...** with **Also install it into the game:** and **Uninstall release patch
  (patch-3.MPQ)** work while the game runs with hot reload: the game lets go of its `patch-3.MPQ`, the file is replaced,
  then the game opens it again and reloads the patch's files. When the game cannot let go, nothing is changed.
* Settings: **Hot reload in the running game** with **Switch on hot reload for this game folder** (`wds settings set
  hot-reload on --yes`): writes `wowhotreload.txt` next to `WoW.exe`; off by default, per game folder.
* The app explains when the running game cannot reload: not running from the folder, switched off, started before it
  was switched on, no hot-reload support in its `wowcode.dll` (older than the HR1 update, or a normal client), not
  answering.
* `docs\HOT_RELOAD.md` (shipped with the app): how hot reload works, what reloads, the protocol.

### Notes

* The game side of hot reload is part of the project's client (`wowcode.dll`, staged as HR1 in the WoWDecomp project); it
  arrives with a coming wave. Until then the app says the running game has no hot-reload support (see
  [Known issues](docs/manual/known-issues.md)).

## 0.10.0 (2026-10-13)

The skeleton and animation round trip: bones, animations, attachment points and events to Blender and back.

### Added

* **Skeleton and animations (for editing)** in **Export for Blender...** (`wds models export --with-skeleton`): the
  bones as a Blender armature (hierarchy, pivots, key bones), every animation as an action named by AnimationData
  (*Stand*, *Stand [2]*, *Walk*, *AttackUnarmed*...) with its loop flag, chance, blend time, move speed and repeats,
  every global sequence as an action of its own, the attachment points (`ATT_<id>_<name>`) and events
  (`EVT_<n>_<code>`) as empties, the events of each animation as markers, and the particles / ribbons as
  `wowdata_effects.json` (manual chapter [Skeleton and animations](docs/manual/skeleton-and-animation.md)).
* **Import edited model...** imports them back as a row *Skeleton and animations of ...* that lists every change in
  words: bones added, removed, moved or re-parented (a removed bone's vertices, attachments, events, lights, particles
  and ribbons go to its nearest remaining parent; every bone number in the model follows), key bones, retimed / edited /
  new / removed animations, chances (scaled to 100 %), move speeds (recomputed for a retimed movement animation so the
  feet do not slide), global sequences, attachment points and events. Unchanged animations keep their keys byte for
  byte; edited and new ones are encoded the way the 1.12 client plays them (linear keys, one timeline, a place of their
  own at its end). The animation lookup, variation chains, playable-animation table (the client's own fallback
  algorithm), key-bone lookup and attachment lookup are rebuilt; the mesh is weighted to the final bones (at most 4
  per vertex, bones 0-255, the views' 256 / 75 / 53 / 21 bone limits). Refused, with the reason in plain words: action
  names that are not game animations, removed key bones, unknown key bone or attachment ids, removing Stand, removing
  animations the game plays from a character model; extra warnings for character models.
* **Particles and ribbons (advanced)**: `wowdata_effects.json` is validated (field sizes, types, textures, materials,
  bones, key times) and imported as its own row; only edited emitters and ribbons are written.
* **Compare** of a skeleton / animation import plays any animation on both sides (**Animation** list and time slider;
  a new animation plays on the right only); `wds models compare --sequence <name> --time <ms>` with the animation
  differences.
* **3D viewer:** **Attachment points** (where the game attaches weapons, shields, helmets, spell effects; cyan) and
  **Loop** toggles; the playing animation's properties, what the game plays it for, its fallback and its events under
  the timeline; **Skeleton, attachment points and events** (key bones, attachment points, events); new animations
  appear in the list right after an import. `wds models render --sequence <name|n> --time <ms>`.
* **Used in game:** **Animations and what the game plays them for** for every animated model: variations, lengths,
  loops, the game's own situations (moving, combat, casting, emotes, death, mounts, from the client's code), the chat
  emotes (Emotes / EmotesText) and spells (Spell -> SpellVisual -> SpellVisualKit) that play it, and the fallbacks of
  missing animations.
* **The command line:** `wds models anims` (the animations and what the game plays them for; `--sequence` for one with
  its events), `wds models skeleton` (bones, key bones, attachment points, events, limits), `--with-skeleton` for
  `models export`, the skeleton / particles rows in `models import` (`--exclude skeleton`, `--exclude particles`,
  `changes` in JSON), `--sequence` / `--time` for `models render` and `models compare`.
* **Blender add-on 0.5.0:** the *WoW* tab in the 3D view (the animations, play one, its WoW properties, *New animation
  from this one*, *Check for WoW*), *WoW bone* in Bone properties (original bone, key bone), *WoW point* for the
  empties; one animation at a time (NLA tracks removed); the export writes `wowdata_rig.json` (bones, changed actions
  sampled, unchanged ones recognised by a fingerprint, markers with their exact times, empties).
* Feature registry: 8 new features (*models.export-skeleton*, *models.import-skeleton*, *models.effects*,
  *models.anims*, *models.skeleton*, *models.animation-preview*, *models.attachment-points*,
  *models.compare-animations*), each in the app and on the command line.
* Manual: the chapter [Skeleton and animations](docs/manual/skeleton-and-animation.md) with five worked examples (a new
  idle animation for a creature, a retimed walk, a new bone with a new part, a moved weapon attachment point, preview
  and compare) and screenshots 107-114; updates in *Contents*, *Models and Blender*, *The 3D model viewer*, *Used in
  game*, *Glossary*, *Troubleshooting*, *The command line*, *Known issues*.

### Fixed

* AnimationData's columns 4-6 were shifted by one: they are *Priority* (not read by the model code), *Flags* (0x10
  backwards, 0x20 hold) and *Fallback*, as the client's `M2Model_ResolvePlayableAnimation` reads them. The Data tables
  tab and its links now show the right fallback.
* Model import: a vertex whose normal differs from another vertex's by a rounding error (or a UV of -0 vs. +0) could be
  matched to the wrong original vertex, so a few untouched models (`Kobold.m2`, `ManaTotem.m2`, `UI_RS_Scourge.m2`)
  were reported as changed. Vertices are now matched by position, UV, rounded normal and weights.
* Screenshots of dialogs were cut by the window's margin at the bottom and right (the last row of buttons) and had a
  transparent margin at the top and left.

### Changed

* The export package's manifest has a `rig` part (bones, animations, global sequences, attachment points, events, key
  bones) when the skeleton is exported; glTF bones carry `wow_global_channels` and animations `wow_duration_ms`.
* A model import that changes the mesh, the skeleton and the particles saves them as one model file in one import.
* Developers: `WoWData.Core\M2\M2Tracks.cs` (raw tracks, the client's key search and interpolation, the append-mode
  encoder, key reduction, bone matrix decomposition), `AnimationInfo.cs` (AnimationData, the playable-animation
  algorithm, roles, events), `RigInfo.cs`, `RigEdit.cs` (`RigWriter`); `WoWData.Core\ModelIO\RigPackage.cs`,
  `RigGltfReader.cs`, `RigImport.cs`, `EffectsJson.cs`; `WoWData.Core\Usage\AnimationUsage.cs`.

### Tests

* `RigFormatTests` (on every client model: an identity rig plan writes the same bytes; re-encoded and relocated
  tracks play the same; the lookup rules; key reduction; bone removal / addition), `RigRoundTripTests` (export -> import
  without edits on a 25-model sample of creatures, characters, items, doodads and spell effects; a scripted rig edit;
  the limits; the particles JSON; Used in game; Blender headless: no-edit round trips and an edit with a new bone, a
  new animation, a retimed walk, a moved attachment point and a new event), `CliRigTests` (the command line and its
  parity with the app). Outside the suite, all 9,691 client models were exported with the skeleton and imported back
  unchanged.
* 221 tests, three consecutive full runs green. `TerrainBlenderTests` now expects the add-on version of the app's own
  add-on instead of a fixed number.

## 0.9.0 (2026-10-13)

The server data editor: your server's world database (VMaNGOS) in the app and on the command line.

### Added

* **Server data tab** (manual chapter [Server data](docs/manual/server-data.md)): the world database (`mangos`) of the
  server in friendly views: **Items**, **Creatures (NPCs)**, **Quests**, **Game objects**, **Spells**, **Loot tables**
  (creature, game object, item, pickpocketing, skinning, fishing, reference, disenchanting, mail), **Vendors**,
  **Trainers**, **Areas**, **Maps**, **Flight paths**, **Factions**, **Faction templates**, **Languages (locales)** and
  **All tables** (every one of the 176 tables, raw). Search by name or ID; one line per object with the version the
  server uses.
  * A record shows every column (from the live schema) with its type, its meaning, choices and flags by name, money and
    times in words, and links (to items, creatures, quests, spells, factions, maps, areas... on the server, and to rows
    of the game's tables in the Data tables tab); columns not described yet are shown as stored, with a hint.
  * The rows that belong to it: spawns, loot (with reference loot), vendor and trainer lists (own and shared templates),
    quest givers and enders, quest chains, equipment, gossip, the server's facts about a creature's looks, reputation,
    languages...; **Used by** (every row of every described table that refers to it); icons of items and spells from the
    game's tables; **Show in Browse** for the 3D model.
  * VMaNGOS versions: rows per content patch (`patch`), client build (`build`) or with patch / build ranges are shown with
    the version the server uses (setting **Content patch of the server**, else `WowPatch` of `mangosd.conf`, else 10).
* **Editing with checks:** SQL types (ranges, decimals, lengths, NULL, character sets), choices and flags by name, money
  as `1g 2s 3c`, links that must exist, and the rules VMaNGOS applies at start (levels, looks the game does not have,
  display probabilities, loot chances and counts, reference loot, quest objectives and rewards, patch ranges, vendor /
  trainer / quest-giver flags...). **Add new** and **Duplicate as new** take the first free ID of a configurable custom
  ID range (**Custom ID range**, default 900000-999999); **Add row here** in a related list; **Delete** says what refers
  to the row; **Bulk edit...**; **Undo** / **Redo**.
* **Server edits in the workspace:** **Save to my workspace** (with the row diff first) keeps one file per table
  (`ServerData\<database>\<table>.json`: every changed row before and after) in **My changes** (*server table*, *not
  applied to the server yet* / *applied to the server*), with **Compare**, **Show** and **Revert**. Server edits are never
  installed into the game or packed into `patch-3.MPQ`.
* **Client and server:** **Check client and server...** compares Spell.dbc with `spell_template`, ItemDisplayInfo with
  the items' displays (and the server's `item_display_info` copy), CreatureDisplayInfo with the creatures' displays (and
  `creature_display_info_addon`), Map / AreaTable / Faction / FactionTemplate / TaxiNodes / SkillLineAbility with
  `map_template` / `area_template` / `faction` / `faction_template` / `taxi_nodes` / `skill_line_ability`; **Make the
  server match the game** and **Make the game match the server** (as workspace edits). The record of a spell, area, map,
  flight point or faction shows the game's values next to the server's.
* **Delivering to the server:** **Export SQL patch...** (an idempotent `.sql` with a header, and its `.revert.sql`);
  **Apply to server...** (the exact statements, rows changed on the server meanwhile, the world server's state, what it
  needs afterwards; a verified backup of every affected row and table; one transaction in strict SQL mode, MyISAM
  failures taken back from the backup; the rows read back; an applied-changes log); **Undo last apply**; **Applied
  changes log**. Only the world database is ever written (never the characters or realm database). The notes name the
  `.reload` commands VMaNGOS has for each table or say that a restart is needed.
* **The game's cache:** **Clear the game's cache (WDB)...** moves the game's `WDB\*.wdb` files of a chosen game folder
  into the app's backups (refused while that game runs).
* **CSV / JSON** export of the list or of a related list, and import with a preview (changed, added, removed rows,
  errors, warnings) and one undo step.
* **The command line:** the group `wds server` (21 commands: `status`, `views`, `find`, `show`, `get`, `set`, `add`,
  `duplicate`, `delete`, `bulk`, `used-by`, `changes`, `check`, `match`, `export-sql`, `apply`, `undo-apply`, `applies`,
  `export`, `import`, `clear-wdb`) and the settings `server.patch` and `server.custom-ids`.
* **Settings:** a *Server data (world database)* card (**Content patch of the server**, **Custom ID range**).
* **Data tables tab:** a table the server keeps in its database (Spell, Map, AreaTable, Faction, FactionTemplate,
  TaxiNodes, SkillLineAbility...) has **Edit it in the Server data tab**.
* **Tools** menu: **Server data (world database)**, **Check client and server...**, **Apply to server...**, **Clear the
  game's cache (WDB)...**; the "How it works" panel has a *Server data* part.
* Manual: the chapter [Server data](docs/manual/server-data.md) with five worked examples (an item's stats and its look
  on both sides, a new NPC vendor, a small quest with a starter and an ender, a spell's numbers on both sides, apply and
  undo) and screenshots 95-106; updates in *Contents*, *The main window*, *My changes*, *Releasing*, *Safety*,
  *Glossary*, *Troubleshooting*, *Settings*, *Used in game*, *Data tables*, *The command line*, *Known issues*.

### Changed

* The settings text says that the app writes the server's database only through **Apply to server...**.
* The build window and the release patch leave server edits out; "Build release patch" with only server edits says how
  to deliver them instead.
* `DbcDiffWindow` takes the names of its two sides (the server's rows and yours).
* Developers: `WoWData.Core\ServerData` (the server layout file `server_layouts_vmangos.txt`, the live schema, the
  editor, validation, views, SQL, apply / undo, consistency, CSV / JSON, the game's cache, the throwaway MySQL server for
  tests); `AppSession.ServerData`; the feature registry's *Server data* area (28 features).

### Tests

* The tests never use the live server: a throwaway MySQL server (the local VMaNGOS `mysqld.exe`, only read, or
  `WDS_TEST_MYSQLD`) with its own empty data folder in `%TEMP%` and its own port, loaded from
  `app\WoWData.Tests\serverdb\world_fixture.sql` (the schema of all 176 tables and a connected sample of 9,945 rows,
  made by `app\tools\ServerFixtureBuilder` from a read-only copy of the database files); every test gets its own copy of
  the database. `ServerDataTests` (13), `ServerCliTests` (2, with the app <-> command line parity of server edits).

## 0.8.0 (2026-10-11)

The command line: every feature of the app as a command, for scripts and automation. WoW Data Studio is now one product
with two front ends, the desktop app and `wds`, built together, installed together, always the same version.

### Added

* **The command line `wds`** (`wds.exe`, next to `WoWDataStudio.exe`; manual chapter
  [The command line](docs/manual/command-line.md)): 81 commands in the groups of the app's tabs: `setup` (the game folder,
  an overview), `settings` (get / set / unset every setting, the password only with `--stdin` and never printed),
  `serverdb` (test, defaults, reload the names), `index` (build, status), `find` (search with the Browse tab's filters,
  only my changes and a zone; list by type; the details; Used in game; which archive serves a file and what it
  overrides; list and extract game files), `textures` (info, export with the ground texture's other file or every texture
  of an entry and its skins, import with every question of the app as an option, compare with the heat map), `models`
  (info, export to glTF with all skins, open in Blender, import from Blender, validate, compare, the Blender add-on,
  render to PNG with the app's renderer), `maps` (maps, zones, tiles, a tile's details, the breakdown, previews and the
  minimap as PNG, Edit in Noggit, import map changes, terrain to and from Blender with the seam check, compare, test in
  game, revert), `server-maps` (status, extract, install, undo: refused while `mangosd` runs), `dbc` (tables, rows with the
  grid's filters, get / set, add, duplicate, delete, bulk edit, CSV / JSON export and import with a preview and `--apply`,
  used by, compare with the game, copy to server and its undo) and `changes` (list, change sets, compare, revert, test in
  game, remove from game, build the release patch, its state, uninstall it).
  * `--help` on every command with examples; `--json` (one JSON object: `ok`, `exitCode`, `status`, `data`, `messages`,
    `warnings`, `error`); results on stdout, progress and warnings on stderr; exit codes 0 ok, 1 error, 2 usage,
    3 validation failed, 4 refused for safety, 5 confirmation needed, 6 not found.
  * Non-interactive and safe by default: nothing changes the game folder or the server, and nothing deletes an edit,
    without `--yes` (otherwise it prints what it would do, exit code 5); `--dry-run` wherever the app shows a preview;
    the app's refusals (the world server running, a file in the game folder that is not the app's without `--force`, a
    `patch-3.MPQ` the app did not create: only moved to the backups, with `--force`).
  * `--home` for an isolated settings, index and workspace folder (scripts, tests), `--client` for another game folder.
  * `wds commands --json` (the command tree) and `wds features` (every feature and its command).
* **Example scripts** (installed in `scripts\`): `export_zone_creature_textures.ps1` (every creature texture of a zone),
  `dbc_csv_round_trip.cmd` (a table through a spreadsheet and back), `build_and_install_patch.ps1`.
* **Installer:** `wds.exe` is always installed with the app; a Start-menu *WoW Data Studio command prompt*; an optional
  *Add the command line tool to PATH* task (per user; removed on uninstall, the PATH restored exactly).
* **Help > The command line (wds)** and **Help > About WoW Data Studio** (the version and build, the same as
  `wds version`).
* Manual: the chapter [The command line](docs/manual/command-line.md) (every command with examples, the coverage table
  app <-> command line, exit codes, JSON, recipes, safety); a *Command line* note in every feature chapter; updates in
  *Contents*, *Getting started*, *The main window*, *Troubleshooting*, *Known issues*.

### Fixed

* **Building the release patch with "Also install it into the game" failed when a `patch-3.MPQ` was already installed when
  the game folder was opened** (*Could not write ...patch-3.MPQ. Is the game running?*, although the game was closed). The
  app itself reads the installed patch (it mounts the game's archives), and Windows refuses to replace an open file. The
  old file is now renamed out of the way (allowed while it is open), the new one put in place, the old one deleted.
  Found by the command line's tests (`CliChangesTests`: a second build over an installed patch, and over a foreign one).
* **Two programs on one workspace:** the app and `wds` (or two instances of the app) could overwrite each other's change
  list with an old copy. The change list is now read again before every change and listing when another program changed
  it, and the app shows such changes when you switch tabs.

### Changed

* Developers: the logic the app had in its view models moved into `WoWData.Core`, where the app and the command line
  share it: the session (`Session\AppSession`), the Browse search (`Index\EntrySearch`, with a zone filter
  `AssetIndex.KeysInZone`), the import flow of textures with its questions (`Workspace\TextureImportFlow`, the app's
  dialogs and the command line's options are two `ITextureImportDecider`s), revert / install / build / uninstall and their
  texts (`Workspace\ChangeOperations`), the settings by name (`Workspace\SettingsCatalog`), a tile's facts, the breakdown,
  pending tiles, Noggit, terrain export / import and compare (`Maps\MapOperations`), the server-map update
  (`Maps\ServerMapsUpdater`), the Data tables tab's edits, filters, bulk edit, rows, save, CSV / JSON
  (`Dbc\DbcEditSession`, `DbcFilter`), the manual's location (`ManualLocator`), the model package folder, the add-on zip's
  place. The app's behaviour is unchanged.
* The version of the app, the command line and the core is in one place, `app\Directory.Build.props` (with a build stamp
  set by `build_installer.ps1`); `build_installer.ps1` publishes both programs into one `bin\` and fails when either is
  missing, their versions differ or `wds version` does not run; `installer\test_install.ps1` checks both, runs `wds` and
  the PATH task.
* The feature registry (`WoWData.Core\Features\FeatureRegistry.cs`) lists every feature with its app label and command;
  tests keep the app, the command line and the manual in step (`FeatureParityTests`, `ParityTests`, `CliTests`,
  `ManualTests.CommandLineChapterMatchesTheProgram`).

## 0.7.0 (2026-10-11)

The DBC editor: the game's data tables in the app.

### Added

* **Data tables tab** (manual chapter [Data tables](docs/manual/dbc-editor.md)): all 154 DBC tables the 1.12.1 client
  loads (plus the 4 that `patch.MPQ` deletes, listed as server-only), grouped by subject, read in the game's archive
  order, with the archives each comes from, a loose copy in the game folder and your workspace edit shown. Each table
  says what it controls in the game and what the server does with it: *Client only*, *Server also needs this file* or
  *Server uses its database instead (edit there)* (with the server's table).
* **Layouts of every table** from the client itself (the record readers of our decompile of `WoW.exe`: field counts,
  record sizes, text fields, 1- and 2-byte fields, names proven by the client code), the server's DBC loader (formats and
  names, read for facts only) and checks on the files. 107 tables are fully named, 30 partly, 17 raw apart from the ID
  (1,214 of 1,484 columns named); unknown columns stay visible as `col_N` with a hint. Each layout records its evidence.
  Columns are typed: whole numbers (signed, unsigned, 1- and 2-byte), decimal numbers, texts, file paths, localized
  texts (8 languages + flags), flags with named bits, choices with named values, links to other tables.
* **A fast grid** (virtualized rows and columns) with search over all text, a filter per column (text, `=`, `!`,
  `>`, `<`, ranges), sorting, **Go to ID**; the ID and the name stay in view. Links show the linked row's name; rows
  that differ from the game's file are marked.
* **The selected row**: every value with its type and hint; links that open the linked row (with **Back**), flags as
  tick boxes, choices as lists, the other languages; the icon or texture the row names (a spell's icon through
  **SpellIconID**, an item display's icon, a creature display's skin) in the texture viewer; **Show in 3D** opens
  creature, item and game object displays, spell effects and races in the Browse tab's 3D viewer; **Used by**: the
  rows of other tables that link to this row.
* **Editing with checks**: in the grid or the details panel; type ranges, links to rows that exist, unique IDs, yes /
  no values, text and path lengths. **Add row** (the next free ID), **Duplicate as new**, **Delete rows** (warns when
  other rows link to them), **Bulk edit...** (set / add / multiply a column of the selected rows, all checked first,
  one step), **Undo** / **Redo**, localized texts edited in the chosen **Language** with the other languages kept.
* **Into the workspace**: **Save to my workspace** shows the changed / added / removed rows first; the table is then
  one change set in **My changes** (format *DBC, N rows*), where **Compare** shows the row-by-row difference with the
  game's file and **Show** opens it. **Test in game** and **Build release patch...** work as for textures; the patch
  window reminds which tables the server needs. The workspace and the patch build refuse a table the game would not
  load. Saving a table identical to the game's removes the edit.
* **Writing tables byte for byte**: every one of the 154 tables written back unchanged gives the same bytes; texts are
  stored once each, in the order the rows use them (that is how all client files are made).
* **Export and import**: CSV (for spreadsheets) and JSON, of all rows, the rows shown or the selected rows; import with
  a preview of every changed, added and removed row, errors that block it, warnings, and the choice to keep or remove
  the rows the file does not have; one undo step.
* **Copy to server**: copies your edited tables that the server reads from its dbc folder (`Server\data\5875\dbc`)
  there, refused while `mangosd` runs, each replaced file backed up and checked first, written through a temporary name;
  **Undo last copy to server**. Tables the server takes from its database are never copied.
* **Tools > Data tables (DBC editor)** and **Tools > Copy tables to the server...**.
* Manual: the chapter [Data tables](docs/manual/dbc-editor.md) with four step-by-step examples (change a spell's icon,
  rename an item display's files, add a flight point, copy a creature display) and screenshots 83-94; updates in *The
  main window*, *My changes*, *Releasing*, *Used in game*, *Glossary*, *Troubleshooting*, *Safety*, *Settings*, *Known
  issues*.

### Fixed

* **Spell effects: wrong roles and false "used by" spells.** The index read fields 13-15 of SpellVisual as three more
  visual kits (*caster impact*, *target impact*, *channel*). The client's code (decompile `SpellVisualRec`) shows that
  field 13 is the kit of a persistent area, 14 a sound and 15 flags: a flags value of 1 or 2 was taken for kit 1 or 2
  (Frostbolt listed `zzOLD__FireShield_Cast_Base` as a *channel* effect), and sound IDs for kits. Fields 4 and 5 were
  called *state* and *state done*; they are the aura kit and the channel kit. The reader now takes the field positions
  from the DBC layout file, the roles are *precast*, *cast*, *impact*, *aura*, *channel*, *area* and *missile* (the
  missile only when the visual has one), and the model of a persistent area (field 12, Blizzard, Consecration) is listed
  as *area*. Checked by `IndexTests.SpellEffectRolesFollowTheClientsSpellVisualFields` (Shadow Bolt, Frostbolt,
  Blizzard, Consecration, Arcane Missiles).

### Changed

* The index has a new version (spell effect roles): the first start of 0.7.0 organizes the game data again (about 10
  seconds).
* All screenshots of the main window were taken again (the new **Data tables** tab).
* Developers: `WoWData.Core\Dbc` has the editable table model (`DbcTable`, `DbcEditor`, `DbcDiff`, `DbcExchange`,
  `DbcValidation`, `DbcService`, `DbcServerCopy`) and the layout file `Dbc\Layouts\dbc_layouts_5875.txt`;
  `DbcEditorTests` check every layout against every file, the byte-for-byte writing of all tables, the Python reference
  (`oracle\make_dbc.py`: 154 headers and 1,023,337 typed values agree), edits through the workspace and a fake client's
  loose install and patch, CSV / JSON round trips of every table, links and Used by, validation, undo, and Copy to server
  into a fake server folder. `--screenshot` with `WDS_SCREENSHOT_ONLY=tables` runs only the Data tables pictures.

## 0.6.1 (2026-10-11)

Fixes for the findings of the in-game visual test of 0.6.0 (`tests\ingame\results\20261010_2254\REPORT.md`).

### Fixed

* **Ground textures: the game draws the `_s` file with terrain specular on (was: an edit of a ground texture could show
  no change in the game).** With `specular 1` and `pixelShaders 1` in the game's `WTF\Config.wtf`, the 1.12 client loads
  `<name>_s.blp` *instead of* the ground texture the map names, with no fallback (`CMapArea_LoadTextureNames` @006c20e0,
  the flag set in `CMap_Load` @006941f0). The app now:
  * shows both files wherever it shows a ground texture: the details panel (**TEXTURES IT USES**), the search list
    (*ground texture* / *specular variant* in the Info column), **Used in game** (a *Ground (map terrain)* section with
    the tiles and zones, linking the other file), the tile breakdown (the two files as children, the `_s` file on its
    own with its shine map), the terrain previews and the terrain package for Blender (both PNGs, the materials use the
    file the game draws);
  * reads the game's `Config.wtf` (read only) and says which file *your* game draws;
  * offers **Edit both (recommended)** when you import a ground texture: the other file gets your colours, the `_s`
    file keeps its alpha channel (the shine / specular map); **Keep the shine map?** when your PNG changed the alpha of
    a `_s` file;
  * draws the 3D previews with the same rule, with **Specular ground (_s textures)** to switch (remembered).

  Confirmed in game (`tests\ingame\results\20261011_0525`): a blue `DurotarDirt.blp` imported with **Edit both** saves
  both files in one change set and keeps the `_s` shine map unchanged. It shows blue in the game with terrain specular on,
  both as loose files and as `patch-3.MPQ`.
* **Pixel-change figures counted only visible colour.** The compare and import statistics ignored colour under fully
  transparent pixels, so a ground `_s` texture or an opaque building texture tinted everywhere reported 14-17 %. Now:
  *Colour changed in X % of pixels; alpha changed in Y %.*, colour changes are always counted (the summary says how many
  sit under alpha 0), and the difference heat map shows them (alpha-only changes in blue).
* **The model import could drop a real edit on an opaque material.** It ignored colour changes under fully transparent
  pixels for every texture; now only when every material that draws the texture uses its alpha as transparency
  (alpha key or blended). An opaque material (blend mode 0) shows that colour, so it counts as an edit.
* **No in-app way to remove an installed `patch-3.MPQ`.** **Uninstall release patch (patch-3.MPQ)** in **My changes**
  removes the release patch the app built (proved by the checksum it now records) and puts back the `patch-3.MPQ` that was
  there before the app's first install. A `patch-3.MPQ` the app did not create (or cannot prove it built) is only moved to
  the `backups` folder, after a warning that explains it; it is never deleted. My changes, the build window and
  **Remove from game** show what is installed (*A release patch built by WoW Data Studio is installed: N files, date*).
  Confirmed in game (same run): after the uninstall, the game starts and looks like the baseline. That run had no earlier
  `patch-3.MPQ` to put back.
* **A second install in the same second overwrote the previous `patch-3.MPQ` backup** (same time-stamped name). Backups
  now get a unique name and are never overwritten.
* **The `--render` developer command ignored workspace edits.** It now renders your workspace version (edited model and
  textures, from the app folder of `--home` or the default one); `--original` renders the game's files.

### Added

* **Additive textures:** when you import a texture that a model draws additively (or as a mod / mod2x layer), the check
  window says *This texture is drawn additively: black is invisible, bright colours glow. Tinting the black background
  will show as a visible square.*, and when your picture brightened the black background it offers
  **Keep black areas black (recommended)**. The blend modes come from every model that draws the texture (materials,
  ribbons, particles; buildings' materials). The note is also in the texture's facts and in the model import review.
* Manual: [Known issues](docs/manual/known-issues.md) and this changelog; new sections in *Editing textures* (ground
  textures and their `_s` files, glowing textures), *Maps and terrain*, *Releasing* (uninstalling the patch),
  *Troubleshooting*, *Glossary*; screenshots 75-82.

### Changed

* The index has a new table (ground textures of every tile): the first start of 0.6.1 organizes the game data again
  (about 10 seconds).
* Tests (developers): one shared index per test run, built once (no parallel index builds); every class that builds an
  index or runs the app is in one test collection; server-data tests are marked `[ServerFact]` (skipped when the server
  does not answer) and read one snapshot; all timing limits moved into `PerformanceTests` (run alone, best of several
  runs); the UI smoke test logs timings instead of judging them.
* `installer	est_install.ps1` (developers): a TEST Start-menu group left by an earlier, normal TEST install is recorded
  and must stay unchanged, instead of failing the check as if this run had created it.
