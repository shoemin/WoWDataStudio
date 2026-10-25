# Changelog

What changed in each release of WoW Data Studio (the desktop app, `app\`). Bugs that are known and not fixed yet are
listed in the manual's [Known issues](docs/manual/known-issues.md); when one is fixed it is removed from that list and
recorded here. The phases before 0.6.1 are described in `ROADMAP.md`.

## Unreleased

### Fixed

* **Browsing many models no longer grows the memory without end.** The decoded textures of the 3D viewer were all kept
  for the whole session (the model resolver's cache had no limit), and every 3D view kept its GPU copy of each one.
  Browsing 200 different creatures added about 110 MB to the managed heap, 220 MB of private memory and 256 GPU
  textures, and the curve kept rising. The cache now keeps the most recently used textures up to 32 MB
  (`WoWData.Core.Imaging.ImageCache`); the same 200 creatures level off after about 60 (the cache and the 8 built
  models full) with the GPU textures at about 115, and the second half of the run no longer rises. A terrain build
  keeps one image per texture for the whole build. Tests: `ImageCacheTests`.

### Added (developers)

* **`--leaktest <folder> [--cycles N] [--only views]`**: opens and closes the views many times (the 3D viewer, its
  pop-out and compare windows, browsing many creatures, the texture viewer, the Maps tab and a terrain tile in 3D, the
  terrain pop-out, Data tables, Server data and the three designers on a throwaway MySQL server, Other versions) and
  records the memory after each cycle: the managed heap after a full collection, private bytes, handles (by kernel
  object type), GDI / USER objects, threads, GL contexts and objects, closed windows still alive. One CSV per view and
  a summary with the slopes; its own temporary app folder, the server database off
  ([MAINTAINING.md](docs/manual/MAINTAINING.md#the-leak-test-developers)). Tests: `LeakTestAnalysisTests`.
  First run (100 cycles, 200 for the 3D viewer, its pop-out and the browsing): every view flat except the browsing
  (fixed above) and the windows with their own 3D views, which leave two kernel handles behind per closed view
  ([Known issues](docs/manual/known-issues.md#each-closed-3d-window-leaves-two-small-handles-behind)).

## 0.14.0 (2026-10-24)

### Added

* **Other versions tab (experimental) and `wds versions`.** Reads other World of Warcraft clients without a server and
  never changes them ([Other versions](docs/manual/other-versions.md)):
  * **Find clients** in a folder (the `other-clients` setting): every client in it and its subfolders, with the version
    and build from its executable (the text of the version resource: modern executables pack the numbers differently),
    the release, MPQ or CASC, readable or not. Measured on `G:\WoW Clients and Patches\Clients`: 15 clients, the 4 MPQ
    ones readable (1.12.1 twice, 2.4.3, 3.3.5a), the 11 CASC ones listed (WoD 6.2.3 and the Battle.net flavours).
  * Each client's archives in the order **that** version mounts them, read from its `Wow.exe` (TBC 2.4.3 and WotLK
    3.3.5a: patch chains sorted by full path, the locale archives, the base archives;
    `docs\research\MPQ_ERA_DELTA.md`). Tests: `ArchiveOrderTests`.
  * **Blizzard archives only** (`other-clients.blizzard-only`, `--blizzard-only`): leaves out patch archives Blizzard
    did not ship. The 3.3.5a copy has a private server's `patch-7.mpq` that replaces 19 tables (Spell.dbc: 49,916 rows
    with it, 49,839 without).
  * Files by wildcard with their archive, a texture **Preview**, **Extract selected...** (refused into the client's own
    folder); **Tables of this version** with that build's layout (`DbcLayouts.ForBuild`, one layout file per build,
    16 languages per text from TBC on; raw `col_N` columns where none is known yet); **Compare across versions**
    (one row side by side, columns matched by name).
  * Commands: `wds versions find`, `info`, `files`, `extract`, `tables`, `rows`, `compare`. Tests: `OtherVersionsTests`.
  * Named columns for 73 tables of 2.4.3 (`dbc_layouts_8606.txt`) and 54 of 3.3.5a (`dbc_layouts_12340.txt`): maps and
    areas, lights, sounds, liquids, world map, flight paths, creature / item / object displays, characters, classes,
    races, factions, skills, talents, spells. Evidence per table in the file: columns matched by value with 1.12 (and
    2.4.3) on rows with the same ID (most 95-100%), texts checked as valid strings in every row and language, links
    resolving for at least 95% of the values (the few below are named in the hint with their rate), names of new
    columns from public format descriptions marked `~` unless the file proves them. A column keeps its 1.12 name when it
    holds the same thing. The 3.3.5a layouts were measured on Blizzard's archives (without the server's patch-7.mpq).
* **Modern clients (CASC): their files.** A read-only CASC reader of our own (`WoWData.Core.Casc`, written from the
  public format descriptions and our probe; `docs\research\CASC_INVESTIGATION.md`): `.build.info` / `versions`, the
  local `.idx` indexes, the data archives, BLTE (plain, zlib, nested; every chunk's MD5 checked; encrypted chunks are
  never decrypted), the encoding file, legacy and MFST roots. Every Battle.net flavour opens as a client of its own.
  Files are named by path (when the build hashes the name) or by number (`#775971`); a listfile the user chooses
  (`other-clients.listfile`, **Choose listfile...**, `--listfile`) names the rest. **Files**, **Preview**, **Extract
  selected...**, `wds versions info / files / extract` work for them; a file known only by its number is extracted as
  `<number>.<type>` from its first bytes. Measured on 1.60.1.70450 (`_classic_beta_`): 1,245,465 files, 1,240,600 on
  the computer, 3,601 encrypted, 1,254 not downloaded; Azeroth (736 tiles) and Kalimdor (988 tiles) complete; opens in
  about 3 s. Root records: the English / all-language one, not low-violence, not Mac-only, the standard resolution of a
  texture (not the 4x high-resolution twin), fewer languages first. Tests: `CascTests` (synthetic BLTE, hashes,
  indexes and a whole synthetic store; the real copies when present).
* **Modern clients' data tables (DB2).** Our own DB2 reader (`WoWData.Core.Db2`: WDC5, WDC4, WDC3, WDB2, WDBC; every
  field storage kind, pallet and common data, per-section strings, ID lists, copy tables, parent IDs, sparse records;
  encrypted sections left out and counted). The table list and the field types come from the client's own program file
  (`Db2Catalog`: 1,161 tables in 1.60.1.70450, 1,128 in Retail; WoD by the table paths in its program and name hashes).
  `db2_layouts_70450.txt` names the tables maps need, by measurement against the older versions (Map, AreaTable,
  LiquidType / LiquidObject / LiquidMaterial, Light, WMOAreaTable, GroundEffect*, ZoneMusic). The Other versions tab
  and `wds versions tables / rows / compare` show old and modern tables the same way (`VersionTable`); a modern client's
  table is read when clicked. Found with it: 1.60's 12 maps of its own (Zephras Isle, Hyjal Crater, Darkspear Islands,
  Battle for Gilneas, The Hall of Thanes...), and how its liquids map onto 1.12's four kinds. Tests: `Db2Tests`.
* **MPQ reader: the archives of TBC 2.4.3 and WotLK 3.3.5a.** bzip2-compressed sectors are read (our own decoder,
  `Mpq\Bzip2.cs`, written from the bzip2 format description; every block's CRC is checked), and so are files packed with a
  smaller sector size than their archive's header says (WotLK `backup-enUS.MPQ`). Measured on the copies in
  `G:\WoW Clients and Patches\Clients`: every listed file of 1.12.1 (91,636), 2.4.3 (134,672) and 3.3.5a (234,987) reads,
  0 failures; before, 17 WotLK files did not (8 game files in `lichking.MPQ`, including two Storm Peaks textures).
  Tests: `Bzip2Tests` (streams made by Python's bz2 module, run edges, multi-block, a corrupted stream must raise).

* **Converting a map of the 1.60 client to 1.12 (experimental): Convert map... and `wds versions convert-map`.**
  Milestones 5 and 6 of `docs\research\MAP_CONVERSION_DESIGN.md` (`WoWData.Core.Maps.Conversion`): a zone with its
  subzones, a list of tiles or a whole map of the "1.60" Classic client goes into your 1.12 workspace as ONE change set
  (**Revert this import** takes it back; the game folder is never written), after a preview (`--dry-run`) with the
  conversion report. Tiles through the neutral model and the 1.12 writer: liquid kinds from 1.60's LiquidType table,
  more than 4 texture layers blended into 4, vertex colours dropped, your 1.12 shadow kept on chunks whose ground did
  not change (none elsewhere), your 1.12 ground effects kept where the texture stays. Ground textures your game has are
  used by path, new ones converted (at most 512 x 512, base and `_s` file). Placements whose model 1.12 has are kept,
  your 1.12 model is kept where 1.60 swapped it at the same unique ID, the rest is left out and listed. Areas 1.12 has
  keep their ID; 1.60-only areas get AreaTable rows (their 1.60 ID, a free exploration bit of 1.12's 0-2047). WDT,
  WDL (rebuilt from the heights: chunk corners truncated, measured exact on stock tiles), minimap tiles (512 to 256,
  named by their MD5 like Blizzard's) with `md5translate.trs`; a map 1.12 does not have gets a Map row (its folder
  named after the map, a loading screen 1.12 has) and a default Light row. Names only from the user's own 1.12 files
  (hashed into a Classic Era copy found next to the 1.60 client) and the user's listfile. Everything is checked by the
  1.12 checks before anything is written (`Workspace.SaveEdits`: one check pass, one change list write). Measured:
  Elwynn Forest 23 tiles in 2.5 s, all of Eastern Kingdoms (736 tiles) in 11 s, Zephras Isle (72 tiles, a new map)
  in 3 s; the report names the server steps (area_template / map_template rows, the release patch, Update server maps).
  Tests: `MapConversionTests`.

* **The update catalog (experimental): the Updates tab of Other versions and `wds versions updates`, `update-info`,
  `import-updates`.** Every difference between the "1.60" Classic client and your 1.12 game, measured and grouped into
  updates you can pick (`WoWData.Core.Versions.Catalog`; the basis is `docs\research\DIFF_112_160.md`), each with its
  portability class (*Ready*, *Needs converter*, *Needs server*, *Needs client code*, or *No visible change*), a plain
  reason, what it changes in your game and what it needs. The scan: every 1.12 file against the client by content (the
  MD5 of the 1.12 bytes against the 1.60 content key; Classic Era's key tells format updates Era already had), changed
  textures compared pixel by pixel (re-encoded only, lightly edited, repainted, made larger; DIFF_112_160's rule), the
  high-resolution texture variants, every tile of every map compared chunk by chunk per zone (heights, texture layers,
  water, zone IDs, placements by unique ID with re-numbered ones matched by model and place), new maps, the light zones
  per zone, skyboxes, weather, every data table (rows added and changed where both sides' columns are named), the
  rows in the client's hotfix cache, the interface strings, fonts (named from their own name table), movies, and the
  engine features found in the program file. Measured against the stock 1.12.1 copy: 889 updates (76 zones' terrain,
  28 new maps, 60 lighting, 140 texture folders, 174 interface art folders, 272 data-table updates...), 406 importable;
  the first scan takes one to two minutes, a later one (the 1.12 MD5s and pixel results are kept) about 20 seconds; the
  catalog is kept in the app's folder per pair of fingerprints. **Preview import** / **Import picked updates**
  (`--dry-run` / `--yes`): several picks go into the workspace as ONE change set; terrain and new maps through the map
  conversion (the zones of one map converted together: `MapConvertTarget.JoinChangeSet`, `MapConvertRequest.SelectionLabel`),
  textures and interface art made loadable by 1.12 (kept when they pass the 1.12 checks, larger ones cut to their own
  512 mip level without re-encoding, others encoded again), fonts and MP3 music as they are; the rest is listed with why
  not yet. Names only from the user's 1.12 files, the user's listfile, and the client's own interface file list
  (ManifestInterfaceData) checked against its name hashes. Tests: `UpdateCatalogTests`.

### Fixed

* **Reverting an import no longer wipes your earlier edits of the same files** (was in Known issues: "Reverting a map
  conversion also reverts your earlier edits of the same files"). When a change set (a map conversion, an update import,
  a texture or model import, a new save of a table) replaces a file that already held an edit of yours, the app now keeps
  that earlier version (`<client-id>\history\`, the ten latest versions per file), and **Revert this import** /
  `wds changes revert-set` puts each file back to the version it had just before the import: your earlier edit (back in
  its own import) or, without one, the game's original. Other imports, older or newer, stay as they are. When a NEWER
  import changed one of its files again, nothing is reverted: the app shows why (the files and the newer imports) and
  `wds changes revert-set` ends with exit code 4; revert the newer import first, or **Revert** single files (which, as
  before, puts the game's original back). `--dry-run` / `--json` list where each file goes (`plan`). The rollback of a
  failed update import also restores the earlier edits now. Tests: `ChangeSetRevertTests`, `ChangeSetRevertCliTests`,
  `MapConversionTests.RevertingAConversionPutsYourEarlierTableEditBack`.
* The hot-reload test `ThePatchIsReplacedWhileTheGameRuns` could fail when the computer was busy: the test's stand-in game
  (`FakeGame`) opened its next pipe connection on a thread-pool thread that could start seconds late, so a 5-second connect
  gave up. It now serves its pipe from threads of its own and always has a connection waiting.
* The 0.13.3 entry below showed a broken path to its in-game test results.

## 0.13.3 (2026-10-16)

The cause of the open issue of 0.13.2 (*a designed spell given as a quest reward is not learned*) was found in the
server's and the game's code, proven on a copy of the test server and fixed; the in-game test (`tests\ingame\results\20261016_1900_spellids`) then saw the designed spell learned from the quest reward ("You have learned a new spell: WDS
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
