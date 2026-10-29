# Maintaining the user manual

The user manual (`docs\manual\*.md`) is part of the product. The standing rule:

> **Every change that adds, removes, renames or changes a feature updates its manual chapter and its screenshots in
> the same change.** A feature is not done until the manual describes it.

This file is for developers and agents; it is not shipped with the app.

The same goes for the list of known bugs and the release notes:

> **Every change updates `known-issues.md` and `CHANGELOG.md`.** A bug you find and do not fix goes into
> `known-issues.md` (what happens, who is affected, the workaround, *Status:* open). A bug you fix is *removed* from
> `known-issues.md` and recorded in `CHANGELOG.md` under the version that fixes it (create the version's section if it
> is not there yet). `known-issues.md` never lists fixed entries.

And since 0.8.0, for the two front ends of the product (the app and the command line `wds`):

> **A change to a feature is not done until the GUI, the CLI, the feature registry
> (`app\WoWData.Core\Features\FeatureRegistry.cs`), the manual chapter, `command-line.md`, `known-issues.md` and
> `CHANGELOG.md` are all updated in the same change.**

`command-line.md` has a `#### wds <command>` section per command (its reference part is what `wds commands --json`
describes: summary, usage, options, examples) and the coverage table, which is exactly what `wds features --markdown`
prints. Every feature chapter ends with a `## Command line` note (one or two lines with the equivalent commands).
`ManualTests.CommandLineChapterMatchesTheProgram` checks all of it, and every `wds ...` command written anywhere in the
manual against the program (the command must exist and have the options used).

## Files

| File | What it is |
|---|---|
| `index.md` | the table of contents. Its links decide which chapters are shipped and in which order. |
| `<chapter>.md` | one chapter per task or screen (`getting-started`, `main-window`, `finding-things`, `texture-viewer`, `editing-textures`, `model-viewer`, `testing-in-game`, `releasing`, `my-changes`, `model-export-import`, `settings`, `troubleshooting`, `safety`, `glossary`, `used-in-game`, `maps-and-terrain`, `terrain-in-blender`, `dbc-editor`, `server-data`, `spell-designer`, `class-and-talent-designer`, `quest-chain-designer`, `command-line`, `known-issues`) |
| `..\screenshots\NN_name.png` | the pictures, made by the app's `--screenshot` mode |
| `MAINTAINING.md` | this file (not linked from `index.md`, not shipped) |

The chapter file names are also the F1 topics of the app: a screen names its chapter with
`s:Manual.Topic="model-viewer"` (`xmlns:s="clr-namespace:WoWDataStudio.Services"`), and F1 opens the chapter of the
focused element or its nearest parent that names one (`app\WoWDataStudio\Services\Manual.cs`). A new screen or window
gets a `Manual.Topic`; renaming a chapter means updating the topics that point to it (the test checks them).

## Writing rules

* Audience: a WoW modder who is not a programmer. Short sentences, plain words, numbered steps for tasks, the result of
  each step. Explain every technical word in `glossary.md`.
* **Bold is reserved for UI labels**, written exactly as the app shows them: `**Keep this change**`,
  `**1. Export texture as PNG...**`. Menu paths use ` > `: `**File > Rebuild the index**`. Use *italics* for emphasis,
  file names in `code`, keys as `<kbd>F1</kbd>`. Do not bold anything that is not on screen (the test fails).
* Labels with a number in them (`Displays using this model (27)`) may be written with the number; the check treats the
  `{...}` parts of the app's text as "anything".
* Screenshots: `![What it shows](../screenshots/NN_name.png)`. Every referenced picture must exist.
* Links between chapters: `[Releasing](releasing.md)` or `releasing.md#backups`. The HTML build turns them into `.html`.
* Do not describe planned features as if they existed. Say "not yet" where a user would look for something missing.

## Updating screenshots

The app draws its own screenshots with the real game data and a temporary data folder (it never installs anything
into the game folder). Use a copy of the client (here `Client3`), never the one you play with:

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool\app"
dotnet build WoWDataStudio\WoWDataStudio.csproj
WoWDataStudio\bin\Debug\net8.0-windows\WoWDataStudio.exe --client "..\..\Client3" --screenshot "$env:TEMP\wds_shots"
```

It writes `01_welcome.png` ... `136_quest_test_in_game.png` (plus offscreen `render_*.png` checks) and `screenshot.log`
(`ALL STEPS OK` at the end). Copy the pictures you need into `docs\screenshots\` (after a change to a shared part of the
window, such as the tabs or the top bar, copy all of them so the manual stays consistent). The steps are in
`Services\ScreenshotRunner.cs`; pictures made only for the manual are in `Services\ManualScreenshots.cs` (26, 27),
`Services\ModelIOScreenshots.cs` (28-35), `Services\UsedInGameScreenshots.cs` (40-45), `Services\MapScreenshots.cs`
(46-55), `Services\PopoutScreenshots.cs` (56-62: the pop-out 3D windows, drawn on an invisible monitor off screen) and
`Services\BreakdownScreenshots.cs` (63-70: the tile breakdown, the terrain tile picker, a scripted terrain import) and
`Services\SpecularScreenshots.cs` (75-82: ground textures and their `_s` files, the colour / alpha figures, the
additive note, the release patch's state and uninstall in a fake game folder, the specular ground in the Maps tab) and
`Services\DbcScreenshots.cs` (83-94: the Data tables tab, links and Used by, a refused edit, saving, My changes, the
row comparison, an item display, bulk edit, a new flight point, a copied creature display, the import preview, Copy to
server into a fake server folder) and `Services\ServerDataScreenshots.cs` (95-106: the Server data tab, a new vendor
and quest, a spell next to the game's row, the client / server check, apply, My changes, the import preview, the Data
tables tab's link, the game's cache) and `Services\SkeletonScreenshots.cs` (107-112: the skeleton and attachment points
in the viewer, the export dialog with the skeleton, a scripted rig edit of the boar imported through the review, the
compare window playing the new animation, the new animation in the viewer, the orc male's animations in Used in game). and
`Services\DesignerScreenshots.cs` (119-126: the Spell designer with Shadow Bolt cloned into Shadow Lance, its second
effect and checks, its visual composed from Fireball's parts with the missile in 3D, a trainer at level 20 and a skill
line, the Warrior's Arms tree on the grid, the Stance Disciplines tree, the Warlock's spells by level, a new class and its
engine report; against a throwaway MySQL server; the Arms picture is made without a server database because the
fixture holds only a sample of `spell_template`) and
`Services\QuestScreenshots.cs` (127-136: the Whirlwind weapon and Dreadsteed chains, a new quest giver on the map, the
Riposte Drill template, the text preview, a branch, the Horde copy, the checks, apply, test in game; against a throwaway
MySQL server loaded with the fixture and its quest-chain supplement `serverdb\quest_chains_fixture.sql`).
`Services\VersionScreenshots.cs` (137-143: the Other versions tab, the Convert map window with a preview of Elwynn
Forest, and the Updates tab with the update catalog and a preview of Elwynn Forest's terrain; dry runs that write
nothing; the catalog's first scan takes a minute or two) needs a folder of client copies in the environment variable
`WDS_SCREENSHOT_OTHER_CLIENTS` (else these pictures are skipped); `WDS_SCREENSHOT_ONLY=versions` makes only them.
`Services\VersionOpenScreenshots.cs` (144-154 and 157-161: the 1.60 client opened read only in the main window: the
Other versions tab's **Open read only**, a boar's display with its files, an item, an interface icon in the texture viewer, (M2)
the boar and the Worn Shortsword in the 3D view, (M3) Taerar playing EmoteRoar from its `.anim` file and the
Arcane_Missile_Lvl4 spell effect with its particles, and (M4) the Maps tab with Eastern Kingdoms, Elwynn Forest and
the tile of Northshire Abbey (32, 48) in 3D, and the biggest building of that tile followed from "On this tile" to
its building preview; the first build of that tile's models takes about 10 seconds; then the Data tables tab with
the Map table, the Server data tab's "No server connected", My changes and the map's edit buttons greyed out with the
reason, and "Used in game" of the boar's display; at the end your 1.12 game is opened again) needs the
same folder (with the `_classic_beta_` copy); its index is built in the run's temporary app folder (about 10 seconds);
`WDS_SCREENSHOT_ONLY=versions-open` makes only them. Use `--client` with a copy of the 1.12 client (`Client3`), never
the folder you play with.
`Services\DbdScreenshots.cs` (162-165, M5: the Settings window's WoWDBDefs folder, the 1.60 client's tables
in the Other versions tab with their name source, and its Data tables tab opened read only with the Map table named by
WoWDBDefs and ExteriorComponentGroup, whose definition does not fit, with the reason; 157 is made again by
`WDS_SCREENSHOT_ONLY=versions-open`) needs the same folder of client copies and a WoWDBDefs folder (the definitions
folder or a copy of the repository, only read, never copied) in `WDS_SCREENSHOT_DBD`; without either it is skipped.
`WDS_SCREENSHOT_ONLY=dbd` makes only them.
`Services\ParticleScreenshots.cs` (155-156: your game's particles and ribbons in the 3D viewer: the Fire Elemental's
fire and smoke, and the Holy Precast High Hand spell effect with the model statistics' notes on what is drawn
approximately) runs after the manual's pictures; `WDS_SCREENSHOT_ONLY=particles` makes only them.
The Server data pictures never use the user's server: the steps start a throwaway
MySQL server (`ThrowawayMySql`, the local VMaNGOS `mysqld.exe` or `WDS_TEST_MYSQLD`, its own data folder and port) and
load the tests' fixture `app\WoWData.Tests\serverdb\world_fixture.sql` into it. For a quick look at the Data tables
pictures only, set the environment variable `WDS_SCREENSHOT_ONLY=tables` before the run (`server` for the Server data
pictures only, `rig` for the skeleton and animation pictures only, `designers` for the designers' pictures only, `quests` for the
Quest chains pictures only).

The pictures are published with the manual, so make them from folders whose paths do not show your Windows user name
(several pictures show folders: the welcome screen, Settings, the export and add-on dialogs, Edit in Noggit, the
server and version folders). One way: a clone of the repository at `G:\WoW Custom Client\WoWDataTool`, built there,
with junctions `G:\WoW Custom Client\Client` (to the client copy), `Server` and `third_party` next to it; `TEMP` and
`TMP` set to a folder without your user name for the run (picture 02 shows it); the `--screenshot` folder on the
drive of the game's archives (the Noggit client view hard-links them), for example `C:\wds_shots`; and an export
folder in the run's own settings, written before the run as `<screenshot folder>\_home\settings.json` with
`{"ExportDir": "G:\\WoW Data Studio\\Exports"}` (else the export dialogs show your Documents folder). Look at
every picture that shows a path before you copy it.

The names of the data tables and of their columns (shown by the **Data tables** tab) come from the layout file
`app\WoWData.Core\Dbc\Layouts\dbc_layouts_5875.txt`; the manual may write them in bold, and the check reads them from
there. The same goes for the server's tables and their described columns (the **Server data** tab) and
`app\WoWData.Core\ServerData\Layouts\server_layouts_vmangos.txt`.

The pictures *of Blender* (71-74, chapter `terrain-in-blender`) are made by Blender itself with the add-on, from a
terrain package (export one with **Open terrain in Blender...**). Blender needs its window for screenshots, so this
opens Blender for about half a minute; pass any saved `.blend` file (an empty scene) so the splash screen stays away:

```powershell
blender --factory-startup empty.blend --python blender_addon\tests\manual_shots.py -- "$env:TEMP\wds_blender_shots" <package>\wowdata_terrain.json
```

It writes `71_blender_tiles.png` ... `74_blender_synced.png` (the whole Blender window; scale them to half size before
copying, they are large) and `manual_shots.json`.

The Blender pictures of the chapter `skeleton-and-animation` (113, 114: the *WoW* tab with the animations and the
Action editor with the walk's event markers; the *WoW bone* panel) come from a boar package exported with the skeleton
(`wds models export Creature\Boar\Boar.m2 --with-skeleton --out <folder>`):

```powershell
blender --factory-startup empty.blend --python blender_addon\tests\manual_shots_rig.py -- "$env:TEMP\wds_rig_shots" <folder>\Boar_export\Boar.gltf
```

It writes `113_blender_wow_animations.png`, `114_blender_wow_bone.png` (half size before copying) and
`manual_shots_rig.json`.
Numbers are never reused: a new feature continues after the highest number, adds a step that shows it, and the
chapter references the new picture.

## The leak test (developers)

`--leaktest` opens and closes the app's views many times with the real game data and measures the memory after every
cycle, to find leaks: event handlers that keep closed windows alive, GL / Direct3D resources not freed, caches without
a bound. It is a developer tool, not a feature for users (`Services\LeakTestRunner.cs`):

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool\app"
dotnet build WoWDataStudio\WoWDataStudio.csproj
$env:WDS_SCREENSHOT_OTHER_CLIENTS = "G:\WoW Clients and Patches\Clients"   # for other_versions (else it is skipped)
WoWDataStudio\bin\Debug\net8.0-windows\WoWDataStudio.exe --client "..\..\Client3" --leaktest "$env:TEMP\wds_leak" --cycles 100
```

Use a copy of the client (here `Client3`), never the one you play with. Like `--screenshot` the run has its own
temporary app folder (`<folder>\_home`: settings, index, workspace, error log), turns the server database off, installs
nothing, and draws its windows off screen. The Server data tab and the designers get a throwaway MySQL server
(`ThrowawayMySql`) loaded from the tests' fixture and its quest-chain supplement, as in the screenshot run; the user's
server is never used. The output (CSV files and logs about game data) stays in that folder: never copy it into the
repository.

The views, in order (`--only model_popout,maps_tab` runs some of them): `model_viewer` (the docked 3D viewer: a boar,
the orc male, a tree, an axe, a spell effect, the Fire Elemental and the Fireball missile with their particles running
for ten frames, shown and cleared), `model_popout` (a model pop-out window opened and
closed), `model_compare` (the compare window of two skins), `model_browse` (another creature every cycle, as when a
user browses the list), `texture_viewer`, `maps_tab` (two maps in turn, a tile with its 3D preview), `terrain_popout`,
`data_tables`, `server_data`, `spell_designer`, `class_designer`, `quest_designer`, `other_versions` (the 2.4.3 and
3.3.5a copies in turn), `versions_open` (the 1.60 client opened read only in the main window: a search, an
entry with its texture and, for a model, its 3D view with up to 8 of its animations kept in `.anim` files played in
turn and its particles running, for a building its building preview, then your 1.12 game opened again; the first
cycle builds its index), `versions_maps` (the 1.60 client's Maps tab in the main window: Eastern Kingdoms, Kalimdor,
map 2997 with 8-layer ground and the Deadmines in turn, one of their tiles in 3D turned around, every other cycle a
terrain pop-out window, then your 1.12 game again). Each runs `--cycles` times (default 100); `model_viewer`, `model_popout` and `model_browse`
run twice as many.

After every cycle the run collects the garbage (full, blocking, compacting) and records the managed heap
(`GC.GetTotalMemory`), private bytes, working set, handles, GDI and USER objects, threads, the GL contexts and objects
(`GlTracker`) and how many of the windows and view models closed so far are still alive (weak references). It writes:

* `leak_<view>.csv`: one row per cycle;
* `leak_summary.txt` / `.csv`: per view the slope per cycle of each measure, the first 10% of the cycles left out as
  warm-up, and the verdict. A measure "grows" when it keeps rising: after the warm-up its slope passes the limit, is
  clearly larger than the noise (t > 4) and adds up to more than the total limit, and the second half of the run still
  rises. The limits: the managed heap 2 KB per cycle and 1 MB, private bytes 32 KB per cycle and 16 MB, handles 0.1 per
  cycle, GDI / USER objects and threads 0.05, GL objects 0.2. A view also grows when its GL contexts do not come back
  or more than 4 closed windows / view models stay alive (the last closed window may stay referenced by WPF's input
  state until the next one replaces it). A cache that fills and then stays rises in the first half only, and views that
  rotate through a few states swing by tens of MB of private bytes: neither counts;
* `leaktest.log`: progress, the handles by kernel object type after the warm-up against the end (which handles a view
  leaks; Mutant and Section handles by name), and the closed objects still alive at the end.

Exit code 0: every view flat; 1: a view grows; 2: the run failed. A full run takes about 40 minutes. A view that grows
is a lead, not a verdict: look at its CSV for a curve that keeps rising (caches fill during the first cycles: the
decoded-texture cache holds up to 32 MB, the model cache 8 models). The run of 2026-10-25: every view flat except the
windows with their own 3D views (two handles per closed view, see `known-issues.md`). To see what holds the memory, a heap snapshot of
the running process (for example with Microsoft.Diagnostics.Runtime) at two points of a long run shows which types
grow and their roots.

## The automated check

`app\WoWData.Tests\ManualTests.cs` (part of `dotnet test`, a second or so) fails when:

* a bold UI label in a chapter is not found in the app's XAML or C# text (a renamed or removed control);
* a referenced screenshot is missing;
* a link to another chapter points to a missing file;
* `index.md` does not link a chapter file, or a `Manual.Topic` in the XAML names a chapter that does not exist;
* `known-issues.md` is missing, not linked from `index.md` and `troubleshooting.md` (and README's *Known issues*), an
  entry lacks one of *What happens:*, *Who is affected:*, *Workaround:*, *Status:*, or an entry's status says it is
  fixed; `CHANGELOG.md` has no section for the app's version.

Run it alone with `dotnet test WoWData.Tests\WoWData.Tests.csproj --filter FullyQualifiedName~ManualTests`.
When it fails, fix the manual (or the label), not the test.

## Building the HTML

`build_installer.ps1` renders the manual with `app\tools\ManualBuilder` (Markdig) into `build\manual\` and ships it in
the installer as `<install>\manual\`. To look at it without building the installer:

```powershell
dotnet run --project app\tools\ManualBuilder -- --version dev
start build\manual\index.html
```

A developer build of the app finds `build\manual\` by itself (F1 and **Help > User manual**); without it, the app
opens the Markdown files.
