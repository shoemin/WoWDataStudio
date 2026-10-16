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
into the game folder):

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool\app"
dotnet build WoWDataStudio\WoWDataStudio.csproj
WoWDataStudio\bin\Debug\net8.0-windows\WoWDataStudio.exe --client "..\..\Client" --screenshot "$env:TEMP\wds_shots"
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
The Server data pictures never use the user's server: the steps start a throwaway
MySQL server (`ThrowawayMySql`, the local VMaNGOS `mysqld.exe` or `WDS_TEST_MYSQLD`, its own data folder and port) and
load the tests' fixture `app\WoWData.Tests\serverdb\world_fixture.sql` into it. For a quick look at the Data tables
pictures only, set the environment variable `WDS_SCREENSHOT_ONLY=tables` before the run (`server` for the Server data
pictures only, `rig` for the skeleton and animation pictures only, `designers` for the designers' pictures only, `quests` for the
Quest chains pictures only).

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
