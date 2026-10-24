# The command line (wds)

Everything you can do in WoW Data Studio you can also do with a command: find things, export and import textures and
models, edit the data tables, edit your server's world database (0.9.0), design spells, talent trees and classes (0.12.0) and quest chains (0.13.0), work
with maps and terrain, test in the game,
build and uninstall the release patch, update your server. That is useful for scripts (a batch export of a whole zone, a table round trip through a
spreadsheet, a nightly release build) and for automation.

The command-line program is called `wds` (`wds.exe`). It is installed together with the app, always in the same
version, and it uses the same code for every operation, so a change made with `wds` is exactly the change the app would
make: the same files in your workspace, the same checks, the same backups, the same safety rules. Both work on the same
settings, index and workspace, so you can start a change in the app and finish it on the command line, and the other
way round.

## Start it

* In the Start menu: *WoW Data Studio command prompt*. It opens a command prompt where `wds` works, and prints the
  version.
* Or tick *Add the command line tool to PATH* when you install (a choice on the installer's *Additional Tasks* page):
  then `wds` works in every new command prompt and PowerShell window. Uninstalling removes it from the PATH again.
* Or run it with its full path: `"%LOCALAPPDATA%\Programs\WoW Data Studio\bin\wds.exe"`.
* In the app: **Help > The command line (wds)** opens this chapter; **Help > About WoW Data Studio** shows the version,
  which is the version of `wds` too.

```
wds --help
wds setup client "C:\Games\World of Warcraft"
wds find search mottled boar
```

The first command that needs the game data organizes it, as the app does at its first start (a few seconds; the
progress is printed).

## How the commands are built

```
wds [global options] <group> <command> [arguments] [options]
```

The groups follow the app: `setup`, `settings`, `serverdb`, `index`, `find` (the Browse tab), `textures`, `models`,
`maps`, `server-maps`, `dbc` (the Data tables tab), `server` (the Server data tab), `spells` (the Spell designer tab),
`talents` and `classes` (the Classes and talents tab), `quests` (the Quest chains tab), `changes` (My changes), and
`manual`, `version`, `commands`, `features`.

* `wds --help` lists the groups, `wds <group> --help` the commands of a group, `wds <group> <command> --help` one
  command with its options and examples.
* Arguments with spaces go in quotes: `wds find info "Mottled Boar"`. Game paths use backslashes:
  `Creature\Boar\BoarSkinIvory.blp`.
* Options can come anywhere after the command; `--name value` and `--name=value` both work.

### Global options

| Option | What it does |
|---|---|
| `--home <folder>` | keeps the settings, the index and your edits in that folder instead of `%APPDATA%\WoWDataStudio` (the app's). Scripts and tests use it to work apart from your normal edits; the app has the same option. |
| `--client <folder>` | uses that World of Warcraft folder for this one command, instead of the one in the settings |
| `--json` | prints one JSON object instead of text (see [JSON output](#json-output)) |
| `--quiet` | no progress messages |
| `--yes` | confirms a command that changes the game folder or the server, or deletes something (see [Safety](#safety)) |
| `--dry-run` | shows what a command would do and changes nothing (on the commands where the app shows a preview) |
| `--help` | the help of the command |

Results are printed to the standard output; progress, notes and warnings to the error output. So
`wds find search boar --json > boars.json` writes only the result into the file.

## Safety

The command line follows the same rules as the app ([Safety and undoing everything](safety.md)):

* *Nothing that changes the game folder or the server, and nothing that deletes your edits, happens without `--yes`.*
  Without it, the command prints what it would do and exits with code 5. For example `wds changes install` lists the
  files it would copy into the game; `wds changes install --yes` copies them.
* *`--dry-run`* shows the check or the preview the app shows (the import check, the rows a table import changes, the
  files of a release patch, what a server update would replace) and changes nothing.
* *Imports go into your workspace*, like in the app: `wds textures import`, `wds models import`, `wds dbc set`,
  `wds maps import-terrain` need no `--yes` (nothing in the game changes, and `wds changes revert-set` undoes them).
* *The world server:* copying tables to the server, installing server maps and their undo are refused (exit code 4)
  while `mangosd` runs, as in the app.
* *The server's database:* `wds server set / add / duplicate / bulk / import` only change your workspace;
  `wds server apply --yes` is the only command that writes the world database (after backing up every affected row and
  table; `wds server undo-apply --yes` takes it back). It never touches the characters database. A row that changed on
  the server since your first edit is not overwritten without `--overwrite` (exit code 4).
* *A file in the game folder that the app did not put there* is never replaced without `--force` (it is then backed up
  and put back when you remove your change). *A `patch-3.MPQ` the app did not create* is never removed without `--force`,
  and then it is moved into the `backups` folder, never deleted.
* *The game's own archives* are only ever read; `wds find extract` refuses to write into the game's `Data` folder.
* *The server database password* is never printed (`wds settings list` says only *(set)*); give it with
  `wds settings set server.password --stdin` so that it does not stay in the history of your command prompt.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | ok: done, nothing to do, or a dry run |
| 1 | error: a file could not be read or written, a program could not be started, the server database did not answer |
| 2 | usage: unknown command or option, a missing or extra argument |
| 3 | validation failed: a value, file, model, map or import the game would not load; nothing was changed |
| 4 | refused for safety: the world server runs, a file in the game folder or a `patch-3.MPQ` is not the app's (without `--force`), a newer import changed a file of the import to revert; nothing was changed |
| 5 | confirmation needed: the command would change the game folder or the server, or delete something; it printed what, changed nothing; run it again with `--yes` |
| 6 | not found: no game folder is set, or the table, row, file, entry, map, tile or change does not exist |

In a script: `if errorlevel 1` (batch) or `$LASTEXITCODE` (PowerShell).

## JSON output

With `--json` every command prints exactly one JSON object:

```
{
  "command": "dbc get",
  "ok": true,
  "exitCode": 0,
  "status": "ok",
  "version": "0.9.0",
  "data": { ... },
  "messages": [ "..." ],
  "warnings": [],
  "error": null
}
```

* `ok`, `exitCode` and `status` (the name of the exit code) say how it went; `error` says why when it did not.
* `data` is the result of the command: the found entries, the row values, the files written, the saved change set...
  Its fields are named in the same words as the text output (camelCase). `wds commands --json` lists every command
  with its arguments and options.
* `messages` are the lines of the text output, `warnings` the warnings.

```
wds find search --type creature --zone Durotar --limit 0 --json
wds dbc rows Spell --where Name==Fireball --columns ID,SpellIconID --json
```

## Every command

### Setup: the game folder and an overview

#### wds setup client

Set the World of Warcraft folder (checked: 1.12.1, build 5875) and organize its data (the index), or show it.

```
wds setup client [folder] [options]
```

* `--no-index`: only save the folder; build the index later (wds index build)

```
wds setup client "C:\Games\World of Warcraft"
wds setup client
wds --home D:\wds-test setup client D:\WoW --no-index
```

#### wds setup check

Check whether a folder is a World of Warcraft 1.12.1 (build 5875) game folder (nothing is saved).

```
wds setup check <folder>
```

```
wds setup check "C:\Games\World of Warcraft"
```

#### wds setup status

Everything at a glance: version, folders, index, server database, workspace, release patch.

```
wds setup status
```

```
wds setup status
wds setup status --json
```

### Settings

#### wds settings list

Every setting with its value (the password only says whether it is set).

```
wds settings list
```

```
wds settings list
wds settings list --json
```

#### wds settings get

The value of one setting.

```
wds settings get <key>
```

```
wds settings get client
wds settings get server.host
```

#### wds settings set

Change one setting (checked like the Settings window); --stdin reads the value (use it for the password).

```
wds settings set <key> [value] [options]
```

* `--stdin`: read the value from the first line of stdin (not visible in the shell history)

```
wds settings set server.port 3310
wds settings set ground-textures specular
wds settings set server.password --stdin < password.txt
wds settings set dbc-locale deDE
wds settings set hot-reload on --yes
```

#### wds settings unset

Put one setting back to its default (folders: found automatically).

```
wds settings unset <key>
```

```
wds settings unset noggit
wds settings unset export-dir
```

### The server database

#### wds serverdb test

Try to connect to the server database with the saved settings ("Test connection"); it is only read.

```
wds serverdb test
```

```
wds serverdb test
```

#### wds serverdb defaults

Use the defaults of a local test server (127.0.0.1:3306, root/root, mangos).

```
wds serverdb defaults
```

```
wds serverdb defaults
```

#### wds serverdb reload

Read the names, spawns, loot and quests from the server database again (File > Reload names from the server database).

```
wds serverdb reload
```

```
wds serverdb reload
```

### The index

#### wds index build

Organize the game data (build the index) when it is missing or out of date; --rebuild always (File > Rebuild the index).

```
wds index build [options]
```

* `--rebuild`: build it again even when it is up to date (your changes are kept)

```
wds index build
wds index build --rebuild
```

#### wds index status

Whether the index is built and up to date, when, and whether it has the server's names.

```
wds index status
```

```
wds index status --json
```

### Find (the Browse tab)

#### wds find search

Search like the Browse tab: every word must be in the name, path or id; type filters, only my changes, a zone.

```
wds find search [words...] [options]
```

* `--type <types> (repeatable)`: only these types (comma-separated): creature, character, item, gameobject, worldobject, spelleffect, texture
* `--only-changes`: only what you changed and what uses it ("Show only my changes")
* `--zone <name>`: only what is in a zone or subzone: creatures and game objects spawned there (needs the server database) and world objects placed there
* `--sort <column>`: name (default; best matches first), type, id or path
* `--desc`: the other direction
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds find search mottled boar
wds find search orc male --type character
wds find search --type creature --zone Durotar --limit 0 --json
wds find search --only-changes
```

#### wds find list

List everything of one or more types ("Show only this type").

```
wds find list [options]
```

* `--type <types> (repeatable)`: the types (comma-separated): creature, character, item, gameobject, worldobject, spelleffect, texture
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds find list --type character
wds find list --type spelleffect --limit 0 --json
```

#### wds find info

The details panel of an entry: where it comes from (source chain), facts, models, the textures it uses, used by.

```
wds find info <entry>
```

```
wds find info "Mottled Boar"
wds find info creature:3098 --json
wds find info Creature\Boar\BoarSkinIvory.blp
```

#### wds find used

"Used in game": NPCs and their spawns, loot, vendors, quests, placements, spells, what a texture edit affects.

```
wds find used <entry>
```

```
wds find used "Mottled Boar"
wds find used Creature\Boar\BoarSkinIvory.blp --json
wds find used item:2880
```

#### wds find where

Which archive serves a file and which copies it overrides (and your workspace edit, a loose copy in the game folder).

```
wds find where <path>
```

```
wds find where Creature\Boar\BoarSkinIvory.blp
wds find where DBFilesClient\Spell.dbc --json
```

#### wds find files

List the game's files by wildcard (* and ?), with the archive that serves each.

```
wds find files <pattern> [options]
```

* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds find files Creature\Boar\*
wds find files *.dbc --limit 0 --json
wds find files Tileset\Durotar\*_s.blp
```

#### wds find extract

Copy a game file out as it is (your workspace version when you edited it; --original for the game's).

```
wds find extract <path> [options]
```

* `--out <file>`: where to write it (default: the file name in the current folder; a folder: the file name in it)
* `--original`: the game's file, not your edit

```
wds find extract DBFilesClient\Spell.dbc --out Spell.dbc
wds find extract Creature\Boar\Boar.m2 --out models\ --original
```

### Textures

#### wds textures info

A texture's facts: format, size, mip levels, your edit, who uses it, its ground pair (_s) and the additive note.

```
wds textures info <texture>
```

```
wds textures info Creature\Boar\BoarSkinIvory.blp
wds textures info Tileset\Durotar\DurotarDirt.blp --json
```

#### wds textures export

"1. Export texture as PNG...": the PNG plus its .blp.json note (keep it next to the PNG); --with-partner also the other file of a ground texture.

```
wds textures export <texture> [options]
```

* `--out <file.png>`: the PNG to write (default: <exports>\<game folder>\<name>.png, as the app suggests)
* `--original`: the game's texture even when you edited it (default: your edit when there is one, as the viewer shows)
* `--with-partner`: a ground texture: also export its other file (the _s specular variant or the base), next to the PNG

```
wds textures export Creature\Boar\BoarSkinIvory.blp --out boar.png
wds textures export Tileset\Durotar\DurotarDirt.blp --with-partner
wds textures export file:Creature\Boar\BoarSkinIvory.blp --original --json
```

#### wds textures export-all

Export every texture an entry uses (TEXTURES IT USES); --variants also every skin of its model.

```
wds textures export-all <entry> [options]
```

* `--out <folder>`: the folder (each PNG under its game path; default: the exports folder)
* `--original`: the game's textures even where you edited them
* `--variants`: also the textures of every display / skin of the entry's models (all looks)

```
wds textures export-all "Mottled Boar" --out exports
wds textures export-all creature:3098 --variants --json
```

#### wds textures import

"2. Import edited PNG...": check and convert a PNG exactly like the app, show the check, save into your workspace.

```
wds textures import <png> [options]
```

* `--target <texture>`: the texture to replace (default: the one the PNG was exported from, its .blp.json)
* `--keep-size`: keep your PNG's size when it is another power of two (default: resize to the original size)
* `--format <same|dxt3|dxt5>`: a PNG with transparency for a texture without: keep the original format (default) or save as DXT3 / DXT5
* `--only-this`: a ground texture: edit only this file (default: "Edit both (recommended)")
* `--use-png-alpha`: a _s ground file: your PNG's alpha becomes the shine map (default: keep the original shine map)
* `--as-is`: an additive (glow) texture: import as it is (default: "Keep black areas black (recommended)")
* `--dry-run`: show what would happen; change nothing

```
wds textures import boar.png --dry-run
wds textures import boar.png
wds textures import dirt.png --target Tileset\Durotar\DurotarDirt.blp --only-this
wds textures import glow.png --as-is --json
```

#### wds textures compare

"Compare original vs. edited": colour and alpha changes of your edit; --heatmap writes the Difference view as a PNG.

```
wds textures compare <texture> [options]
```

* `--mip <level>`: compare the smaller copy of this mip level (default 0, full size)
* `--colour-only`: ignore the alpha channel ("Ignore it (colour only)")
* `--heatmap <file.png>`: write the difference heat map (grey unchanged, yellow small, red big, blue alpha only)
* `--original-png <file.png>`: also write the original as a PNG
* `--edited-png <file.png>`: also write your edit (as the game shows it) as a PNG

```
wds textures compare Creature\Boar\BoarSkinIvory.blp
wds textures compare Tileset\Durotar\DurotarDirt_s.blp --heatmap diff.png --json
```

### Models and Blender

Export a model to Blender and import it back (mesh, textures and, with `--with-skeleton`, the skeleton, animations,
attachment points, events and the particles / ribbons file), look at its animations and skeleton, validate, compare and
render it. See [Models and Blender](model-export-import.md) and [Skeleton and animations](skeleton-and-animation.md).

#### wds models info

A model's statistics, its displays and skins, who a mesh change affects, your edit and its checks.

```
wds models info <model>
```

```
wds models info Creature\Boar\Boar.m2
wds models info "Mottled Boar" --json
```

#### wds models export

"Export for Blender...": a package folder with the glTF, the textures as PNG and wowdata_manifest.json.

```
wds models export <model> [options]
```

* `--out <folder>`: where the package folder goes (default: <exports>\Models; a new <Model>_export folder, never overwritten)
* `--mode <current|all|per-skin>`: current: the current look (default); all: every skin in one file (glTF variants); per-skin: one file per skin
* `--display <id>`: the creature display that is the current look (default: the entry's, or the first)
* `--no-animations`: leave the animations out (faster to open when you only paint textures)
* `--with-skeleton`: "Skeleton and animations (for editing)": bones, every animation as a named action, events as markers, attachment points and events as empties, the particles and ribbons as JSON
* `--open`: start Blender with it afterwards ("Open in Blender afterwards")

```
wds models export Creature\Boar\Boar.m2 --out exports
wds models export "Mottled Boar" --mode all --no-animations --json
wds models export Creature\Boar\Boar.m2 --with-skeleton
```

#### wds models open

"Open in Blender": export the current look (with the animations) and start Blender with it.

```
wds models open <model> [options]
```

* `--display <id>`: the creature display to show
* `--out <folder>`: where the package folder goes (default: <exports>\Models)

```
wds models open "Mottled Boar"
wds models open World\Kalimdor\Durotar\PassiveDoodads\Trees\DurotarPalm01.m2
```

#### wds models import

"Import edited model...": review what came back from Blender (mesh and textures, every check) and save it as one import.

```
wds models import <package> [options]
```

* `--exclude <row>`: leave out the rows whose title contains this text ("mesh", a texture's file name) (repeatable)
* `--keep-size`: textures of another power-of-two size keep their size (default: resize to the original)
* `--format <same|dxt3|dxt5>`: textures that gained transparency: keep the original format (default) or save as DXT3 / DXT5
* `--dry-run`: show what would happen; change nothing

```
wds models import exports\Boar_export --dry-run
wds models import exports\Boar_export
wds models import exports\Boar_export --exclude mesh --json
wds models import exports\Boar_export --exclude skeleton
```

#### wds models validate

Run the model validator (the check every model import must pass) on a model of the game, your edit or an .m2 file.

```
wds models validate <model> [options]
```

* `--original`: the game's model even when you edited it

```
wds models validate Creature\Boar\Boar.m2
wds models validate D:\work\Boar.m2 --json
```

#### wds models compare

"Compare original vs. edited model": the game's model and your edit side by side (numbers; --images renders both).

```
wds models compare <model> [options]
```

* `--images <prefix>`: also render both to <prefix>_original.png and <prefix>_edited.png (the app's renderer)
* `--sequence <name|n>`: with --images: the animation to draw on both (its name, e.g. Walk)
* `--time <ms>`: with --images: the moment of the animation

```
wds models compare Creature\Boar\Boar.m2
wds models compare Creature\Boar\Boar.m2 --images boar --json
wds models compare Creature\Boar\Boar.m2 --images boar --sequence Walk --time 400
```

#### wds models addon

"Install the Blender add-on...": write the add-on zip; --install installs it into Blender 4.5 (needs --yes).

```
wds models addon [options]
```

* `--zip <file>`: where to write the zip (default: Documents\WoW Data Studio\Blender\wowdata_blender.zip)
* `--install`: install and enable it in your Blender (runs Blender in the background; changes Blender's preferences: needs --yes)

```
wds models addon
wds models addon --zip addon.zip --json
wds models addon --install --yes
```

#### wds models anims

A model's animations: names, variations, loop, chance, timing, blend, move speed, events and what the game plays them for (emotes, spells, combat, mounts).

```
wds models anims <model> [options]
```

* `--sequence <name|n>`: only this animation, with its events and every use
* `--original`: the game's model even when you edited it

```
wds models anims Creature\Boar\Boar.m2
wds models anims "Mottled Boar" --json
wds models anims Character\Orc\Male\OrcMale.m2 --sequence EmoteDance
```

#### wds models skeleton

A model's skeleton: bones (key bones, parents, pivots, weighted vertices), attachment points, events, particles and ribbons, and the client's limits.

```
wds models skeleton <model> [options]
```

* `--original`: the game's model even when you edited it

```
wds models skeleton Creature\Boar\Boar.m2
wds models skeleton Character\Orc\Male\OrcMale.m2 --json
```

#### wds models render

Draw a model to a PNG with the app's 3D renderer (your workspace version, or --original), like "Save image...".

```
wds models render <model> <png> [options]
```

* `--display <id>`: a creature display's skins
* `--sequence <name|n>`: the animation: its name (Walk, "Stand [2]", see wds models anims) or its number
* `--time <ms>`: the moment of the animation (ms from its start)
* `--yaw <degrees>`: turn the camera
* `--size <WxH>`: the picture size (default 900x700)
* `--original`: the game's files only, not your edits

```
wds models render Creature\Boar\Boar.m2 boar.png --display 503
wds models render "Mottled Boar" boar.png --size 1920x1080 --original
wds models render Creature\Boar\Boar.m2 walk.png --sequence Walk --time 500
```

### Maps and terrain

#### wds maps list

Every map of the Maps tab: continents, world maps, dungeons, raids, battlegrounds.

```
wds maps list
```

```
wds maps list
wds maps list --json
```

#### wds maps zones

The zones of a map (ZONES) with the tiles each covers.

```
wds maps zones <map>
```

```
wds maps zones Azeroth
wds maps zones 1 --json
```

#### wds maps tiles

The tiles of a map (the minimap), with the ones you changed and the ones Noggit saved that are not imported yet.

```
wds maps tiles <map> [options]
```

* `--changed`: only the tiles in your workspace (orange on the minimap)
* `--pending`: only the tiles Noggit saved that you did not import yet (dashed yellow)

```
wds maps tiles Azeroth --changed
wds maps tiles Kalimdor --json
```

#### wds maps minimap

The map's minimap overview as a PNG (the picture the Maps tab shows, cut into its tiles).

```
wds maps minimap <map> [options]
```

* `--out <file.png>`: the PNG to write (required)
* `--cell <pixels>`: pixels per tile (default: 32, 64 or 128 by the map's size)

```
wds maps minimap Azeroth --out azeroth.png
wds maps minimap DeadminesInstance --out deadmines.png --cell 128
```

#### wds maps tile

A tile's details: zones, size, heights, doodads, buildings, water, and "On this tile" (what is placed and spawned there).

```
wds maps tile <map> <x> <y>
```

```
wds maps tile Azeroth 32 48
wds maps tile Kalimdor 40 41 --json
```

#### wds maps breakdown

"Break down this tile...": every piece of the tile by kind (terrain and chunks, ground textures, doodads, buildings, water, other).

```
wds maps breakdown <map> <x> <y> [options]
```

* `--filter <text>`: only the pieces whose text contains this (palm, river, hole, a file name...)
* `--all`: also every child (chunks, placements); default: the groups and their first level

```
wds maps breakdown Azeroth 32 48
wds maps breakdown Azeroth 32 48 --filter elwynnbush --all --json
```

#### wds maps render

A 2D preview of a tile as a PNG: its relief, the height heat map (with chunks outlined) or where a ground texture is painted.

```
wds maps render <map> <x> <y> [options]
```

* `--out <file.png>`: the PNG to write (required)
* `--kind <relief|heights|texture>`: relief (shaded, default), heights (blue low, red high, holes dark) or texture (the coverage map of --texture)
* `--texture <path>`: the ground texture for --kind texture
* `--chunks <list>`: heights: outline these chunks (0-255, comma-separated)
* `--size <pixels>`: the picture's size (default 512)

```
wds maps render Azeroth 32 48 --out relief.png
wds maps render Azeroth 32 48 --kind heights --chunks 0,1,2 --out heights.png
wds maps render Azeroth 32 48 --kind texture --texture Tileset\Elwynn\ElwynnGrassBase.blp --out grass.png
```

#### wds maps noggit

"Edit in Noggit...": prepare the client view and the project with a bookmark at the tile; --launch starts Noggit.

```
wds maps noggit <map> [x] [y] [options]
```

* `--copy-archives`: when the app's data folder is on another drive than the game: allow copying the archives (about 5 GB)
* `--launch`: start Noggit afterwards (our fixed fork; set its path with "wds settings set noggit")

```
wds maps noggit Azeroth 32 48
wds maps noggit Azeroth 32 48 --launch --json
```

#### wds maps import-noggit

"Import map changes...": review the files Noggit saved (what changed, the 1.12 check) and save them as one import.

```
wds maps import-noggit <map> [options]
```

* `--exclude <file> (repeatable)`: leave out the files whose name contains this
* `--dry-run`: show what would happen; change nothing

```
wds maps import-noggit Azeroth --dry-run
wds maps import-noggit Azeroth --exclude .wdl
wds maps import-noggit Azeroth --json
```

#### wds maps export-terrain

"Open terrain in Blender...": a terrain package of a tile and its neighbours (each tile its own object); --open starts Blender.

```
wds maps export-terrain <map> <x> <y> [options]
```

* `--size <1|3|5>`: the selected tile only (1, default), with its 8 neighbours (3) or two rings (5)
* `--tiles <x,y;x,y>`: exactly these tiles (the selected tile always goes)
* `--references`: doodads and buildings as grey reference shapes (ignored on import)
* `--out <folder>`: where the package goes (default: <exports>\Terrain)
* `--open`: start Blender with it ("Export and open in Blender")

```
wds maps export-terrain Azeroth 32 48 --size 3
wds maps export-terrain Azeroth 32 48 --tiles "33,48;32,49" --references --out terrain --json
```

#### wds maps export-building

"Open building in Blender": a building's (WMO's) shape as a glTF to look at (buildings cannot be imported back yet).

```
wds maps export-building <wmo> [options]
```

* `--out <folder>`: where the folder goes (default: <exports>\Models)
* `--open`: start Blender with it

```
wds maps export-building World\wmo\Azeroth\Buildings\Stormwind\Stormwind.wmo --out buildings
```

#### wds maps import-terrain

"Import edited terrain...": the heights from Blender back, with the edge (seam) check, the review and one import.

```
wds maps import-terrain <package> [options]
```

* `--sync-edges`: when shared edges disagree: copy the heights of the edited side onto the neighbour ("Sync the edges"); without it the import is refused
* `--exclude <file> (repeatable)`: leave out the tiles whose file name contains this
* `--dry-run`: show what would happen; change nothing

```
wds maps import-terrain terrain\Azeroth_32_48_terrain --dry-run
wds maps import-terrain terrain\Azeroth_32_48_terrain --sync-edges --json
```

#### wds maps compare

A map file before (the game's) and after (your edit): every change in words; --out writes the height difference (red raised, blue lowered).

```
wds maps compare <map-or-path> [x] [y] [options]
```

* `--out <file.png>`: the height difference picture (tiles only)

```
wds maps compare Azeroth 32 48 --out diff.png
wds maps compare World\Maps\Azeroth\Azeroth.wdt --json
```

#### wds maps test-in-game

"Test map in game (loose files)": copy the map's changed files into the game folder (needs --yes).

```
wds maps test-in-game <map> [options]
```

* `--force`: replace a file there that the app did not put there (it is backed up first and put back on remove)
* `--reload`: then reload the files in the running game (hot reload; the game must run with it switched on)
* `--dry-run`: show what would happen; change nothing

```
wds maps test-in-game Azeroth --dry-run
wds maps test-in-game Azeroth --yes
wds maps test-in-game Kalimdor --yes --reload
```

#### wds maps revert

"Revert the tile": throw away your edit of a tile (or every changed file of the map); also removes its loose copy (needs --yes).

```
wds maps revert <map> [x] [y] [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds maps revert Azeroth 32 48 --yes
wds maps revert Azeroth --dry-run
```

### Server maps

#### wds server-maps status

"Update server maps...": the changed maps, whether they are in patch-3.MPQ, the world server, the last extraction and update.

```
wds server-maps status
```

```
wds server-maps status
wds server-maps status --json
```

#### wds server-maps extract

"Extract into staging": run the extractors into the staging folder only (the server's data is only read; allowed while it runs).

```
wds server-maps extract [options]
```

* `--steps <list>`: maps,vmaps,mmaps (default: all three): terrain (about 20 s), collision (about 1 min), pathing (1-3 min per tile)

```
wds server-maps extract
wds server-maps extract --steps maps,vmaps --json
```

#### wds server-maps install

"Install into the server" (--yes) or "Show what would change" (--dry-run): only the changed files, after a verified backup; refused while mangosd runs.

```
wds server-maps install [options]
```

* `--steps <list>`: maps,vmaps,mmaps (default: all three)
* `--dry-run`: show what would happen; change nothing

```
wds server-maps install --dry-run
wds server-maps install --yes
```

#### wds server-maps undo

"Undo server update": put the server's data back as before the last update done through the app (needs --yes; refused while mangosd runs).

```
wds server-maps undo
```

```
wds server-maps undo --yes
```

### Data tables (DBC)

#### wds dbc tables

Every data table of the game: group, what the server does with it, how much is named, your edit.

```
wds dbc tables [options]
```

* `--filter <words>`: find a table by its name or what it is for (icon, mount, flight...)
* `--changed`: only the tables in your workspace ("Only tables I changed")

```
wds dbc tables
wds dbc tables --filter flight
wds dbc tables --changed --json
```

#### wds dbc info

A table: what it controls, where the file comes from, what the server does with it, and every column with its type and hint.

```
wds dbc info <table>
```

```
wds dbc info Spell
wds dbc info TaxiNodes --json
```

#### wds dbc rows

Rows of a table, like the grid: search, column filters, sorting, chosen columns (choices and links shown with their names).

```
wds dbc rows <table> [options]
```

* `--search <words>`: "Search all text...": every word somewhere in the row
* `--where <column=filter> (repeatable)`: a column filter as in the grid: text (part), =exact, !not; numbers 5, >5, <=10, 5-10, !0; a choice or link by its name
* `--ids <list>`: only these IDs (comma-separated)
* `--columns <list>`: only these columns (comma-separated names)
* `--sort <column>`: sort by a column
* `--desc`: the other direction
* `--raw`: the stored values (no names of choices and links)
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds dbc rows Spell --where Name==Fireball --columns ID,Name,NameSubtext,SpellIconID
wds dbc rows ItemDisplayInfo --where InventoryIcon=INV_Sword_04 --limit 0 --json
wds dbc rows Spell --search "frost bolt" --sort ID --desc
```

#### wds dbc get

One row ("Go to ID"): every value with its type, links with the linked row's name; or one value.

```
wds dbc get <table> <id> [column] [options]
```

* `--raw`: the stored values
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)

```
wds dbc get Spell 133
wds dbc get Spell 133 SpellIconID
wds dbc get CreatureDisplayInfo 503 --json
```

#### wds dbc set

Change one value (checked like the app: type, range, links, unique IDs) and save the table into your workspace.

```
wds dbc set <table> <id> <column> <value> [options]
```

* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--dry-run`: show what would happen; change nothing

```
wds dbc set Spell 133 SpellIconID 184
wds dbc set TaxiNodes 88 Name "Stormwind, Old Town" --locale enUS
wds dbc set CreatureDisplayInfo 503 CreatureModelScale 1.5 --dry-run --json
```

#### wds dbc add

"Add row": a new row with the next free ID (or --id), values from --set, saved into your workspace.

```
wds dbc add <table> [options]
```

* `--id <n>`: the new row's ID (default: the next free one)
* `--set <column=value> (repeatable)`: a value of the new row (checked)
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--dry-run`: show what would happen; change nothing

```
wds dbc add SpellIcon --set TextureFilename=Interface\Icons\INV_Misc_QuestionMark
wds dbc add TaxiPathNode --set PathID=500 --set NodeIndex=0 --dry-run
```

#### wds dbc duplicate

"Duplicate as new": a copy of a row with the next free ID (or --id), changed by --set, saved into your workspace.

```
wds dbc duplicate <table> <id> [options]
```

* `--id <n>`: the copy's ID (default: the next free one)
* `--set <column=value> (repeatable)`: a value of the copy (checked)
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--dry-run`: show what would happen; change nothing

```
wds dbc duplicate CreatureDisplayInfo 503 --set CreatureModelScale=1.5
wds dbc duplicate TaxiNodes 2 --set "Name=Stormwind, Old Town" --set X=-8800 --json
```

#### wds dbc delete

"Delete rows": delete rows by ID (says which other rows link to them first; needs --yes).

```
wds dbc delete <table> <ids...> [options]
```

* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--dry-run`: show what would happen; change nothing

```
wds dbc delete SpellIcon 4000 --dry-run
wds dbc delete TaxiPathNode 9000 9001 --yes
```

#### wds dbc bulk

"Bulk edit...": set, add to or multiply one column of many rows (all checked first; nothing changes if one is refused).

```
wds dbc bulk <table> [options]
```

* `--column <name>`: the column to change (required)
* `--op <set|add|multiply>`: what to do (required)
* `--value <v>`: the value or the number (required)
* `--ids <list>`: only these IDs (comma-separated)
* `--search <words>`: "Search all text...": every word somewhere in the row
* `--where <column=filter> (repeatable)`: a column filter as in the grid: text (part), =exact, !not; numbers 5, >5, <=10, 5-10, !0; a choice or link by its name
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--dry-run`: show what would happen; change nothing

```
wds dbc bulk Spell --where Name==Fireball --where NameSubtext=Rank --column SpellIconID --op set --value 184
wds dbc bulk SpellCastTimes --ids 4,5,6,7 --column Base --op multiply --value 0.5 --dry-run
```

#### wds dbc export

Export rows as CSV (for spreadsheets) or JSON (by the file's extension): all rows, or the ones a filter shows.

```
wds dbc export <table> [options]
```

* `--out <file>`: the .csv or .json file to write (required)
* `--ids <list>`: only these IDs (comma-separated)
* `--search <words>`: "Search all text...": every word somewhere in the row
* `--where <column=filter> (repeatable)`: a column filter as in the grid: text (part), =exact, !not; numbers 5, >5, <=10, 5-10, !0; a choice or link by its name
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)

```
wds dbc export Spell --out Spell.csv
wds dbc export ItemDisplayInfo --where InventoryIcon=INV_Sword --out swords.json
```

#### wds dbc import

Import a CSV or JSON file: a preview of every changed, added and removed row; --apply imports it and saves the table.

```
wds dbc import <table> <file> [options]
```

* `--remove-missing`: the file is the whole table: rows it does not have are removed ("Remove them"; default: keep them)
* `--apply`: apply the import and save the table into your workspace (without it: the preview only)
* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)

```
wds dbc import Spell Spell.csv
wds dbc import Spell Spell.csv --apply
wds dbc import TaxiNodes nodes.json --remove-missing --apply --json
```

#### wds dbc used-by

"Used by": the rows of other tables that link to a row.

```
wds dbc used-by <table> <id> [options]
```

* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds dbc used-by SpellIcon 185
wds dbc used-by CreatureModelData 145 --json
```

#### wds dbc diff

"Compare with game": your version of a table row by row against the game's file.

```
wds dbc diff <table> [options]
```

* `--locale <language>`: the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)

```
wds dbc diff Spell
wds dbc diff TaxiNodes --json
```

#### wds dbc copy-to-server

"Copy to server...": copy your edited tables that the server reads from its dbc folder (backed up first; refused while mangosd runs; needs --yes).

```
wds dbc copy-to-server [options]
```

* `--tables <list>`: only these tables (comma-separated; default: every edited table the server reads)
* `--dry-run`: show what would happen; change nothing

```
wds dbc copy-to-server --dry-run
wds dbc copy-to-server --yes
wds dbc copy-to-server --tables TaxiPath,TaxiPathNode --yes --json
```

#### wds dbc undo-server-copy

"Undo last copy to server": put the server's previous files back (refused while mangosd runs; needs --yes).

```
wds dbc undo-server-copy [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds dbc undo-server-copy --yes
```

### Server data (the world database)

The Server data tab: the server's world database (VMaNGOS) in friendly views, edits with checks saved into your
workspace, the client / server checks, the SQL patch, applying to the server and its undo, CSV / JSON and the game's
cache. Every edit is saved into your workspace at once (there is no unsaved state on the command line; `--dry-run`
looks first). Nothing reaches the server without `wds server apply --yes`. See [Server data](server-data.md).

A *key* is an ID (for a table with versions: the row the server uses) or `column=value` pairs: `entry=25,patch=0`.
`\N` means NULL.

#### wds server status

The server's world database: connection, schema, the content patch, the custom ID range, whether mangosd runs, your server edits and the last apply.

```
wds server status
```

```
wds server status
wds server status --json
```

#### wds server views

The views of the Server data tab (items, creatures, quests...) and their tables, with how much WoW Data Studio describes.

```
wds server views
```

```
wds server views
wds server views --json
```

#### wds server find

A view's list: everything, an ID, or names with these words (the search box of the Server data tab).

```
wds server find <view> [words...] [options]
```

* `--table <name>`: the table of a view with several (loot: creature_loot_template..., locales: locales_item..., tables: any table)
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds server find items shortsword
wds server find creatures 3098
wds server find loot --table skinning_loot_template
wds server find vendors duokna --json
wds server find tables --table map_template
```

#### wds server show

One record with everything the details panel shows: its values (named, typed, links), related rows (spawns, loot, vendor lists, quest givers...), Used by and the game's values.

```
wds server show <table> <key> [options]
```

* `--raw`: the stored values only
* `--no-sections`: only the values
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds server show creature_template 3098
wds server show item_template entry=25,patch=0 --json
wds server show spell_template 133
```

#### wds server get

One row's values (or one value), with your edits.

```
wds server get <table> <key> [column] [options]
```

* `--raw`: the stored value only (no names of choices and links)

```
wds server get item_template 25 name
wds server get creature_template 3098 --json
```

#### wds server set

Change values of one row (checked like the app) and save it into your workspace (the server is not changed: "server apply" does that).

```
wds server set <table> <key> [column] [value] [options]
```

* `--set <column=value>`: a value (checked like the app: type, range, choices by name, links that exist; \N = NULL) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds server set item_template 25 dmg_max1 12
wds server set creature_template 3098 --set level_min=3 --set level_max=4
wds server set item_template 25 quality Rare --dry-run --json
```

#### wds server add

"Add new": a new row with the next free ID of your custom ID range (or --id), values from --set, saved into your workspace.

```
wds server add <table> [options]
```

* `--id <n>`: the new row's ID (default: the first free one of server.custom-ids)
* `--set <column=value>`: a value (checked like the app: type, range, choices by name, links that exist; \N = NULL) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds server add npc_vendor --set entry=900000 --set item=25
wds server add creature_template --set "name=Grokk the Merchant" --set display_id1=1879 --set npc_flags=Vendor --dry-run
```

#### wds server duplicate

"Duplicate as new": a copy of a row with the next free ID of your custom ID range (or --id), changed by --set, saved into your workspace.

```
wds server duplicate <table> <key> [options]
```

* `--id <n>`: the copy's ID (default: the first free one of server.custom-ids)
* `--set <column=value>`: a value (checked like the app: type, range, choices by name, links that exist; \N = NULL) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds server duplicate creature_template 3158 --set "name=Grokk" --set subname="Trade Goods"
wds server duplicate item_template 25 --id 900100 --set "name=Sharp Shortsword" --json
```

#### wds server delete

"Delete": delete rows (says first what refers to them; needs --yes).

```
wds server delete <table> <keys...> [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds server delete npc_vendor entry=3158,item=159 --dry-run
wds server delete creature_template 900000 --yes
```

#### wds server bulk

"Bulk edit...": set, add to or multiply one column of many rows (all checked first; nothing changes if one is refused).

```
wds server bulk <table> [options]
```

* `--column <name>`: the column to change (required)
* `--op <set|add|multiply>`: what to do (required)
* `--value <v>`: the value or the number (required)
* `--where <column=value>`: a filter: column=value (also !=, >, >=, <, <=, ~ for "contains", a,b,c for one of) (repeatable)
* `--keys <list>`: only these keys (separated by spaces or |; e.g. "25|117")
* `--dry-run`: show what would happen; change nothing

```
wds server bulk item_template --where quality=0 --where sell_price>0 --column sell_price --op multiply --value 2 --dry-run
wds server bulk creature_loot_template --where entry=3098 --column ChanceOrQuestChance --op multiply --value 1.5
```

#### wds server used-by

"Used by": the rows of every table that refer to an item, creature, quest, spell, game object... (from the meaning of every described column).

```
wds server used-by <table> <key> [options]
```

* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds server used-by item_template 25
wds server used-by creature_template 3098 --json
```

#### wds server changes

"Compare with the server": your saved server edits, row by row (before on the server, after in your workspace).

```
wds server changes [table]
```

```
wds server changes
wds server changes item_template --json
```

#### wds server check

"Check client and server...": the game's tables next to the server's (spells, item and creature looks, maps, areas, factions, faction templates, flight paths, skills), every difference.

```
wds server check [area] [options]
```

* `--ids <list>`: only these IDs (comma-separated)
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds server check spells --ids 133,143
wds server check creatures
wds server check --limit 0 --json
```

#### wds server match

"Make the server match the game" / "Make the game match the server": fix one difference found by "server check" (an edit in your workspace).

```
wds server match <area> <id> [options]
```

* `--to <server|client>`: which side changes: server (it takes the game's values) or client (the game's table takes the server's values) (required)
* `--dry-run`: show what would happen; change nothing

```
wds server match spells 133 --to client
wds server match creatures 1879 --to server --dry-run
```

#### wds server export-sql

"Export SQL patch...": your server edits as an idempotent .sql file (with a header) and its revert script (.revert.sql).

```
wds server export-sql [options]
```

* `--out <file>`: the .sql file to write (required); the revert script is written next to it

```
wds server export-sql --out my_changes.sql
```

#### wds server apply

"Apply to server...": write your server edits into the world database (shows the statements; backs up every affected row and table first; one transaction; logged; needs --yes).

```
wds server apply [options]
```

* `--overwrite`: apply although rows changed on the server since you started editing them
* `--show-sql`: print every statement
* `--dry-run`: show what would happen; change nothing

```
wds server apply --dry-run --show-sql
wds server apply --yes
wds server apply --yes --json
```

#### wds server undo-apply

"Undo last apply": put the rows of the last apply back as they were before it (from its backup; needs --yes).

```
wds server undo-apply [options]
```

* `--overwrite`: undo although rows changed on the server since the apply
* `--dry-run`: show what would happen; change nothing

```
wds server undo-apply --dry-run
wds server undo-apply --yes
```

#### wds server applies

The applied-changes log: every apply to the server and its undo, with its backup folder.

```
wds server applies
```

```
wds server applies
wds server applies --json
```

#### wds server export

Export rows of a table as CSV (for spreadsheets) or JSON (by the file's extension), with your edits.

```
wds server export <table> [options]
```

* `--out <file>`: the .csv or .json file to write (required)
* `--where <column=value>`: a filter: column=value (also !=, >, >=, <, <=, ~ for "contains", a,b,c for one of) (repeatable)
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds server export npc_vendor --where entry=3158 --out duokna.csv
wds server export item_template --where name~Shortsword --out swords.json
```

#### wds server import

Import a CSV or JSON file into a table: a preview of every changed, added and removed row; --apply imports it and saves it into your workspace.

```
wds server import <table> <file> [options]
```

* `--remove-missing`: the file is the whole set: rows of --where (or of the table) that it does not have are removed
* `--where <column=value>`: a filter: column=value (also !=, >, >=, <, <=, ~ for "contains", a,b,c for one of) (repeatable)
* `--apply`: apply the import and save it (without it: the preview only)

```
wds server import npc_vendor duokna.csv
wds server import npc_vendor duokna.csv --where entry=3158 --remove-missing --apply
```

#### wds server clear-wdb

"Clear the game's cache (WDB)...": move the game's cached items, creatures, quests and objects out of its WDB folder (backed up; refused while the game runs; needs --yes).

```
wds server clear-wdb [options]
```

* `--game <folder>`: the game folder whose cache to clear (default: the game folder of WoW Data Studio)
* `--dry-run`: show what would happen; change nothing

```
wds server clear-wdb --dry-run
wds server clear-wdb --yes
wds server clear-wdb --game "C:\Games\WoW 1.12 test" --yes
```
### The spell designer

The Spell designer tab: new and cloned spells, the form's fields in friendly units, the tooltip preview, the checks, visuals, trainers and skill lines. Every change is saved into your workspace at once, the game's `Spell.dbc` (and the helper tables it needed) and, when the server database is connected, the server's `spell_template` (and `npc_trainer`, `skill_line_ability`) in step, in one change set; `--dry-run` looks first. See [The spell designer](spell-designer.md).

#### wds spells find

Find spells by name or ID (to open or clone one): ID, name, rank.

```
wds spells find <words...> [options]
```

* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds spells find shadow bolt
wds spells find 686 --json
```

#### wds spells show

A spell in the designer's form: every group of fields, the tooltip as the game fills it in, the effects explained, the checks, trainers and skill lines.

```
wds spells show <spell> [options]
```

* `--group <name>`: only one group (Basics, Casting, Targeting, Effect 1...), with every field
* `--level <n>`: the caster level of the tooltip preview (default: the spell's level)

```
wds spells show 686
wds spells show 686 --group Effect --json
```

#### wds spells fields

The fields of the spell designer's form: their group, label, the column they edit, the kind of value and help.

```
wds spells fields [options]
```

* `--group <name>`: only one group

```
wds spells fields
wds spells fields --group Casting --json
```

#### wds spells new

A new spell (instant, self, mana, on the global cooldown) with a free ID of your spell ID range (35000-39999 by default), then --set fields; saved into the workspace (and the server's spell_template).

```
wds spells new <name> [options]
```

* `--rank <text>`: the rank text ("Rank 1")
* `--id <n>`: this ID (at most 40000) instead of the first free one of your spell ID range
* `--set <field=value>`: a field of the form ("wds spells fields" lists them; friendly units: seconds, yards, "instant", an icon file name) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds spells new "Shadow Lance" --rank "Rank 1" --set school=Shadow --set cast-time=2.5 --set range=30
wds spells new Test --dry-run --json
```

#### wds spells clone

A copy of an existing spell ("a spell like Shadow Bolt") with a free ID of your spell ID range, a new name and rank, then --set fields; saved like spells new.

```
wds spells clone <source> [options]
```

* `--name <text>`: the new spell's name
* `--rank <text>`: its rank text
* `--id <n>`: this ID (at most 40000) instead of the first free one
* `--set <field=value>`: a field of the form ("wds spells fields" lists them; friendly units: seconds, yards, "instant", an icon file name) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds spells clone 686 --name "Shadow Lance" --set cast-time=2 --set effect1-value=40-48
wds spells clone 133 --name "Blue Fireball" --dry-run
```

#### wds spells set

Change fields of a spell (friendly units, checked) and save it with the server's spell_template in sync.

```
wds spells set <spell> [field] [value] [options]
```

* `--set <field=value>`: a field of the form ("wds spells fields" lists them; friendly units: seconds, yards, "instant", an icon file name) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds spells set 35000 cast-time 2
wds spells set 35000 --set effect1-value=40-48 --set icon=Spell_Shadow_ShadowBolt
```

#### wds spells check

What the 1.12 client or server cannot handle in a spell (errors), and what is probably a mistake (warnings), in plain words.

```
wds spells check <spells...>
```

```
wds spells check 35000
wds spells check 686 133 --json
```

#### wds spells tooltip

The spell book tooltip and the buff text as the game fills in $s1, $d, $o1... (or any text with --text).

```
wds spells tooltip <spell> [options]
```

* `--level <n>`: the caster level (default: the spell's level)
* `--text <text>`: expand this text with the spell's values instead of its description

```
wds spells tooltip 686
wds spells tooltip 172 --level 20
wds spells tooltip 686 --text "Deals $s1 damage over $d."
```

#### wds spells explain

The spell effect and aura types in plain words: what each does, what its misc value holds, and whether the 1.12 server handles it.

```
wds spells explain [kind] [id]
```

```
wds spells explain effect 6
wds spells explain aura
wds spells explain --json
```

#### wds spells icons

The icon picker: SpellIcon rows and icon files (Interface\Icons) whose name has these words.

```
wds spells icons <words...> [options]
```

* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds spells icons shadow bolt
wds spells icons frost --json
```

#### wds spells visual

A spell's visual: describe its parts, use another spell's visual, compose it from parts of others (precast, cast, missile, impact, aura, channel, area), or duplicate it tinted and scaled.

```
wds spells visual <spell> [options]
```

* `--use <visual>`: use this SpellVisual (or spell:N for another spell's visual)
* `--precast <part>`: visual:N, spell:N, kit:N or none
* `--cast <part>`: visual:N, spell:N, kit:N or none
* `--missile <part>`: visual:N, spell:N, model:N or none
* `--impact <part>`: visual:N, spell:N, kit:N or none
* `--aura <part>`: visual:N, spell:N, kit:N or none
* `--channel <part>`: visual:N, spell:N, kit:N or none
* `--area <part>`: visual:N, spell:N, kit:N, model:N or none
* `--duplicate`: copy the visual with its own kits and effect models (tint and size below)
* `--tint <color>`: #RRGGBB multiplied into the copied models' colours and textures
* `--scale <x>`: size of the copied models (1 = unchanged)
* `--find <words>`: list the visuals of spells with these words in their name (the picker)
* `--dry-run`: show what would happen; change nothing

```
wds spells visual 686
wds spells visual 35000 --use spell:686
wds spells visual 35000 --missile spell:133 --impact spell:686
wds spells visual 35000 --duplicate --tint #8040FF --scale 1.5
wds spells visual 35000 --find frostbolt
```

#### wds spells learn

How a spell is learned: list its trainers and skill lines, add it to a trainer at a level for a price (with its "learn" spell), put it in a skill line (SkillLineAbility) for classes, or remove either.

```
wds spells learn <spell> [options]
```

* `--trainer <entry>`: add to this trainer (a creature entry, or list:N for a shared trainer list)
* `--level <n>`: the level the trainer teaches it at (default: the spell's level)
* `--cost <price>`: the price: copper (2500) or 25s, 1g 50s
* `--remove-trainer <entry>`: remove it from this trainer (entry or list:N)
* `--skill-line <id>`: put it into this SkillLine (a class's talent-tree tab, a profession...)
* `--class <classes>`: the classes of the skill line row: names or IDs, comma-separated (default: every class)
* `--race <races>`: the races of the skill line row (default: every race)
* `--automatic`: learned automatically with the skill (instead of from a trainer)
* `--remove-skill-line <id>`: take it out of this skill line
* `--dry-run`: show what would happen; change nothing

```
wds spells learn 35000
wds spells learn 35000 --trainer 3156 --level 20 --cost 25s
wds spells learn 35000 --skill-line 355 --class Warlock
```

#### wds spells sync

Make the server's spell_template rows of spells match the game's Spell.dbc rows (after editing the game's side alone), saved into the workspace.

```
wds spells sync <spells...> [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds spells sync 35000 35001
wds spells sync 35000 --dry-run
```

#### wds spells renumber

"Change ID": give a spell you made a usable ID (VMaNGOS casts no spell above 40000, the 1.12 game knows none above 65535): the first free one of the spell ID range or --to; its learn spells, skill lines, talents, quests and trainer rows follow; --all moves every spell of yours above 40000.

```
wds spells renumber [spell] [options]
```

* `--to <n>`: this ID (at most 40000) instead of the first free one of the spell ID range
* `--all`: every spell you made whose ID is above 40000 (taught spells first, then their learn spells)
* `--keep-learn-spells`: leave its learn spells where they are
* `--dry-run`: show what would happen; change nothing

```
wds spells renumber 900000
wds spells renumber 900000 --to 35010
wds spells renumber --all --dry-run
```

### The talent designer

The talent trees of the Classes and talents tab: trees (TalentTab) and talents (Talent) of a class, checked against the 1.12 talent frame and the server. The rank spells are made with the spell designer. See [The class and talent designer](class-and-talent-designer.md).

#### wds talents trees

A class's talent trees (TalentTab rows), in their order, with their talent counts.

```
wds talents trees <class>
```

```
wds talents trees warrior
wds talents trees 9 --json
```

#### wds talents show

A talent tree as a grid (tiers x 4 columns, ranks, prerequisites), its talents and the checks.

```
wds talents show <tree>
```

```
wds talents show 161
wds talents show 161 --json
```

#### wds talents add-tree

A new talent tree for a class (at most 5 per class in the 1.12 talent frame).

```
wds talents add-tree <class> <name> [options]
```

* `--icon <icon>`: the tab's icon: a SpellIcon ID or an icon file name
* `--background <name>`: the background picture (Interface\TalentFrame\<name>-TopLeft.blp ...)
* `--order <n>`: the tab's place 0-4 (default: the next free)
* `--races <races>`: only these races (names or IDs; default every race)
* `--dry-run`: show what would happen; change nothing

```
wds talents add-tree warrior "Stance Disciplines" --icon Ability_Warrior_DefensiveStance --background WarriorProtection
```

#### wds talents set-tree

Change a tree: name, icon, background, order, class-mask, race-mask.

```
wds talents set-tree <tree> [options]
```

* `--set <field=value>`: name=..., icon=..., background=..., order=0-4, class-mask=..., race-mask=... (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds talents set-tree 410 --set name="Disciplines" --set order=3
```

#### wds talents remove-tree

Remove a tree and its talents (their rank spells stay); needs --yes.

```
wds talents remove-tree <tree> [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds talents remove-tree 410 --yes
```

#### wds talents add

A talent in a tree at a tier and column, with its rank spells (existing, or made from a template spell with --make-ranks).

```
wds talents add <tree> [options]
```

* `--tier <n>`: the row 0-7 (row n needs 5 x n points in the tree)
* `--column <n>`: the column 0-3
* `--ranks <spells>`: the rank spells, comma-separated (1-5)
* `--make-ranks <spell>`: make the rank spells: clones of this template spell
* `--name <text>`: the name of the made rank spells
* `--count <n>`: how many ranks to make (default 5)
* `--values <list>`: effect values per rank: 1,2,3,4,5 or 2-4,4-6,...
* `--effect <n>`: the effect whose value --values sets (default 1)
* `--description <text>`: the description of the made rank spells ($s1 = the rank's value)
* `--prereq <talent[:rank]>`: a talent that must be learned first (and the rank it needs; default its last)
* `--dry-run`: show what would happen; change nothing

```
wds talents add 410 --tier 0 --column 1 --make-ranks 12295 --name "Iron Line" --count 5 --values 1,2,3,4,5
wds talents add 410 --tier 1 --column 1 --ranks 900010 --prereq 2000:5
```

#### wds talents set

Change a talent: move it (--tier, --column), its rank spells, its prerequisites, its required spell.

```
wds talents set <talent> [options]
```

* `--tier <n>`: the row 0-7
* `--column <n>`: the column 0-3
* `--ranks <spells>`: the rank spells, comma-separated
* `--prereq <talent[:rank]>`: prerequisite 1 (0 removes it)
* `--prereq2 <talent[:rank]>`: prerequisite 2
* `--prereq3 <talent[:rank]>`: prerequisite 3
* `--required-spell <spell>`: a spell the player must know
* `--dry-run`: show what would happen; change nothing

```
wds talents set 2000 --tier 1 --column 2
wds talents set 2001 --prereq 2000:5
```

#### wds talents remove

Remove a talent from its tree (its rank spells stay); needs --yes.

```
wds talents remove <talent> [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds talents remove 2001 --yes
```

#### wds talents check

Check talent trees (a tree ID, or every tree of a class) against the 1.12 talent frame and server: grid, ranks, prerequisites, arrows, tiers.

```
wds talents check <tree-or-class>
```

```
wds talents check 161
wds talents check warrior --json
```

### The class designer

The rest of the Classes and talents tab: a class's spells by level, its skill lines, assigning spells to levels and trainers, class masks, and (experimental) a new class's data with the report of what the game and the server still need in code. See [The class and talent designer](class-and-talent-designer.md).

#### wds classes list

The classes (ChrClasses): ID, name, token, power, spell family and races.

```
wds classes list
```

```
wds classes list
wds classes list --json
```

#### wds classes show

A class's full spell list by level (trainers, skill lines, automatic, starting spells, talents), its skill lines and its talent trees.

```
wds classes show <class> [options]
```

* `--level <range>`: only these levels: 20 or 10-30
* `--source <kind>`: only trainer, taught, automatic, starting or talent

```
wds classes show warlock
wds classes show 1 --level 20-30 --json
```

#### wds classes assign

Give a class's spell a level: its trainer rows of the class and its own level follow; --trainer all adds it to every class trainer that lacks it.

```
wds classes assign <class> <spell> [options]
```

* `--level <n>`: the level (1-255)
* `--trainer <who>`: all (every trainer of the class), or creature entries / list:N, comma-separated
* `--cost <price>`: the price: copper or 25s / 1g 50s
* `--dry-run`: show what would happen; change nothing

```
wds classes assign warlock 35000 --level 20 --trainer all --cost 25s
wds classes assign 9 686 --level 2
```

#### wds classes mask

Add or remove a class in the class mask of rows: SkillLineAbility, SkillRaceClassInfo, TalentTab (game) or item_template, quest_template, skill_line_ability (server).

```
wds classes mask <table> <ids...> [options]
```

* `--class <class>`: the class (name or ID)
* `--add`: add the class's bit
* `--remove`: remove the class's bit
* `--column <name>`: the mask column when a table has two (SkillLineAbility: ClassMask or ExcludeClass)
* `--dry-run`: show what would happen; change nothing

```
wds classes mask SkillRaceClassInfo 75 76 --class 10 --add
wds classes mask item_template 25 --class Warlock --remove
```

#### wds classes new

EXPERIMENTAL: the data of a new class copied from a template class (ChrClasses, CharBaseInfo, shared skill lines, outfits; playercreateinfo*, level stats on the server) and the report of the code it still needs.

```
wds classes new <name> [options]
```

* `--token <TOKEN>`: the internal name in capitals (KNIGHT)
* `--copy-from <class>`: the template class (name or ID)
* `--races <races>`: the races that can be it (names or IDs, comma-separated)
* `--power <type>`: Mana, Rage, Focus, Energy or a number (default: the template's)
* `--id <n>`: the class ID (default: 6 or 10, then 12 and up)
* `--dry-run`: show what would happen; change nothing

```
wds classes new Knight --token KNIGHT --copy-from Paladin --races Human --dry-run
wds classes new Knight --token KNIGHT --copy-from Paladin --races Human
```

#### wds classes engine-report

What a new class still needs outside the tables: the 1.12 client (decompile and interface files) and the server (VMaNGOS source), with where.

```
wds classes engine-report [class]
```

```
wds classes engine-report
wds classes engine-report 10 --json
```

### The quest-chain designer

The Quest chains tab: quest chains as flowcharts (lanes, steps, links, exclusive groups), the quest editor's fields, new quests after, before, as branches or joins, givers and enders, objectives (kill, use, cast, collect with their drop sources, explore, speak to), rewards (items, choices, money, experience, reputation, a learned spell), new quest-giver NPCs placed on the map, the same chain for the other faction, the "discipline chain" template, the checks, the text preview, CSV / JSON and the test-in-game helper. Quests live on the server: every command except `wds quests fields` needs the server database (Settings). Each command saves into your workspace at once (`--dry-run` shows and saves nothing); `wds server apply --yes` writes the rows into the database. See [The quest-chain designer](quest-chain-designer.md).

#### wds quests find

Quests by words of their title or by ID (the server's quests with your edits).

```
wds quests find <words...> [options]
```

* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds quests find whirlwind
wds quests find 1718 --json
```

#### wds quests show

A quest in plain fields: texts, levels, who may take it, chain links, objectives, rewards, givers and enders, and its checks.

```
wds quests show <quest>
```

```
wds quests show 1718
wds quests show 7562 --json
```

#### wds quests fields

The quest editor's fields: id, label, group, the quest_template column and what it holds.

```
wds quests fields
```

```
wds quests fields
wds quests fields --json
```

#### wds quests chain

The chain of a quest as a flowchart: lanes (faction, race / class), columns in order, links (must be done first, opens, next in chain, breadcrumb), exclusive groups, problems per quest.

```
wds quests chain <quest> [options]
```

* `--mermaid`: print the chain as a Mermaid flowchart (for wikis and design documents)

```
wds quests chain 1718
wds quests chain 7562 --json
wds quests chain 1718 --mermaid
```

#### wds quests check

Check a quest's chain (or only the quest): unreachable quests and cycles, level gaps, missing givers and enders, objectives that can't be completed, faction mismatches, the game's text limits.

```
wds quests check <quest> [options]
```

* `--only`: check only this quest, not its whole chain

```
wds quests check 1718
wds quests check 900000 --only --json
```

#### wds quests new

A new quest (the first free ID of your custom range): alone, after or before another quest (linked in the chain), with its givers, enders and fields.

```
wds quests new <title> [options]
```

* `--after <quest>`: after this quest: it needs that one done, and the NPC that takes that one back offers it next
* `--before <quest>`: before this quest: that quest then needs this one first
* `--like <quest>`: copy levels, races, classes and log category from this quest
* `--id <n>`: this quest ID instead of the first free one
* `--giver <who>`: a quest giver: creature:<ID>, object:<ID> or a creature ID (repeatable)
* `--ender <who>`: who takes it back (repeatable)
* `--set <field=value>`: a field of the quest ("wds quests fields" lists them; class and race names, 1g 50s, creature:<ID>) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds quests new "Riposte Drill" --giver 234 --ender 234 --set level=20 --set races=Alliance --set classes=Warrior
wds quests new "Report to the Sergeant" --before 900001
```

#### wds quests set

Change fields of a quest: "<field> <value>" or --set field=value (texts, levels, classes, races, chain links, objectives, rewards...).

```
wds quests set <quest> [field] [value] [options]
```

* `--set <field=value>`: a field of the quest ("wds quests fields" lists them; class and race names, 1g 50s, creature:<ID>) (repeatable)
* `--dry-run`: show what would happen; change nothing

```
wds quests set 900000 level 22
wds quests set 900000 --set races=Horde --set "details=Well met, $N."
wds quests set 900000 flags +Sharable
```

#### wds quests branch

Follow-ups of a quest: a choice (taking one closes the others: "pick one of three = one of three follow-ups") or parallel quests.

```
wds quests branch <quest> [options]
```

* `--titles <a|b|c>`: the follow-ups' titles, separated by |
* `--parallel`: no choice: all of them can be done
* `--rewards <items>`: each follow-up's reward item, comma-separated, in order
* `--dry-run`: show what would happen; change nothing

```
wds quests branch 900001 --titles "The Sword Path|The Axe Path|The Mace Path" --rewards 2488,2490,2491
```

#### wds quests join

A quest after several: --all (every one must be done: an "all of" group) or --any (any one of them, e.g. the branches of a choice).

```
wds quests join <quests> [options]
```

* `--title <text>`: the new quest's title
* `--all`: all of them must be done
* `--any`: any one of them is enough
* `--dry-run`: show what would happen; change nothing

```
wds quests join 900002,900003,900004 --title "Back to the Sergeant" --any
```

#### wds quests giver

Add or remove a quest giver (a creature gets the Quest giver flag).

```
wds quests giver <quest> <who> [options]
```

* `--remove`: remove this giver
* `--dry-run`: show what would happen; change nothing

```
wds quests giver 900000 234
wds quests giver 900000 object:1618 --remove
```

#### wds quests ender

Add or remove who takes the quest back (a creature or a game object).

```
wds quests ender <quest> <who> [options]
```

* `--remove`: remove this ender
* `--dry-run`: show what would happen; change nothing

```
wds quests ender 900000 creature:234
```

#### wds quests objective

Set an objective: kill a creature, use an object, cast a spell on it, collect an item (shows its drop sources); --clear empties a slot.

```
wds quests objective <quest> [options]
```

* `--kill <creature[:count]>`: a creature to kill (count default 1)
* `--use <object[:count]>`: a game object to use
* `--cast <spell>`: with --kill / --use: cast this spell on it instead of killing it
* `--item <item[:count]>`: an item to collect
* `--slot <n>`: the objective slot 1-4 (default 1)
* `--text <text>`: the objective's line in the quest log (instead of "<creature> slain")
* `--clear`: empty the slot (with --kill or --item: which kind)
* `--dry-run`: show what would happen; change nothing

```
wds quests objective 900001 --kill 122:8
wds quests objective 900001 --item 2589:10 --slot 2
wds quests objective 900001 --use 1618:1 --cast 8593
```

#### wds quests talk

A "speak to" objective: the NPC gets a gossip option, shown while the quest is in the log, that gives the talk credit (VMaNGOS TalkedToCreature).

```
wds quests talk <quest> <npc> [options]
```

* `--option <text>`: the option's text (default "I am ready.")
* `--greeting <text>`: the NPC's greeting when it has no gossip menu yet
* `--slot <n>`: the objective slot 1-4 (default 1)
* `--dry-run`: show what would happen; change nothing

```
wds quests talk 900001 821 --option "Captain, the sergeant sent me."
```

#### wds quests explore

An "explore" objective: entering an area trigger completes the quest (an existing trigger, or a new one at a position: AreaTrigger.dbc and the server's areatrigger_template).

```
wds quests explore <quest> [options]
```

* `--trigger <id>`: an existing area trigger (AreaTrigger.dbc)
* `--map <id>`: a new trigger's map
* `--x <x>`: its position
* `--y <y>`: its position
* `--z <z>`: its height (default: the ground)
* `--radius <yards>`: its radius (default 10)
* `--dry-run`: show what would happen; change nothing

```
wds quests explore 900001 --map 0 --x -10650 --y 1120 --radius 15
wds quests explore 900001 --trigger 87
```

#### wds quests reward

Set rewards: items, choices (pick one), money, experience, reputation, a spell the player learns (made through the spell designer when it has no learn spell).

```
wds quests reward <quest> [options]
```

* `--item <item[:count]>`: an item always given (repeatable)
* `--choice <item[:count]>`: an item to choose from (repeatable)
* `--money <amount>`: money (1g 50s; negative: money required)
* `--xp <n>`: experience
* `--rep <faction:value>`: reputation with a faction (repeatable)
* `--spell <spell>`: a spell the player learns (its learn spell is cast; 0 removes it)
* `--cast-only`: with --spell: cast the spell on the player instead of teaching it
* `--clear`: remove the item, choice and reputation rewards first
* `--dry-run`: show what would happen; change nothing

```
wds quests reward 900001 --spell 35010 --xp 1200 --money 1g
wds quests reward 900001 --choice 2488 --choice 2490 --choice 2491
```

#### wds quests drops

Where an item comes from (creatures' loot, chests, containers, pickpocketing, skinning, fishing, vendors, quests, spells), and --creature adds a creature that drops it.

```
wds quests drops <item> [options]
```

* `--creature <id>`: add this creature as a drop source (its loot table; made when it has none)
* `--chance <percent>`: the drop chance (default 35)
* `--always`: drop it for everyone, not only for players on the quest
* `--dry-run`: show what would happen; change nothing

```
wds quests drops 2589
wds quests drops 900100 --creature 122 --chance 40
```

#### wds quests npcs

Find quest givers by name or ID: creatures (quest givers first) and game objects, with how often each is placed in the world.

```
wds quests npcs <words...> [options]
```

* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds quests npcs gryan
wds quests npcs thork --json
```

#### wds quests npc

A new quest-giver NPC: a copy of an existing creature's look, faction and level with its own name (custom ID), the Quest giver flag, placed at a map position.

```
wds quests npc <name> [options]
```

* `--copy-from <creature>`: the creature whose look, faction and level it gets
* `--subname <text>`: the line under its name (<Drill Sergeant>)
* `--level <n>`: its level
* `--faction <template>`: its faction template
* `--map <id>`: the map it stands on (0 Eastern Kingdoms, 1 Kalimdor)
* `--x <x>`: its position
* `--y <y>`: its position
* `--z <z>`: its height (default: the ground there)
* `--o <radians>`: the way it faces
* `--dry-run`: show what would happen; change nothing

```
wds quests npc "Drill Sergeant Halvor" --copy-from 821 --subname "Drill Sergeant" --map 0 --x -10508 --y 1053
```

#### wds quests place

Place a creature at a map position (a new spawn; the height from the terrain when --z is not given).

```
wds quests place <creature> [options]
```

* `--map <id>`: the map
* `--x <x>`: the position
* `--y <y>`: the position
* `--z <z>`: the height (default: the ground)
* `--o <radians>`: the way it faces
* `--dry-run`: show what would happen; change nothing

```
wds quests place 900020 --map 1 --x -470 --y -2620
```

#### wds quests duplicate

The same chain for the other faction: a copy of every quest of the chain (new IDs) for Alliance or Horde, its links between the copies, NPCs, objects, items, zones, factions and words of the texts swapped.

```
wds quests duplicate <quest> [options]
```

* `--faction <faction>`: Alliance, Horde or races of the copy
* `--swap <old=new>`: 234=3429 (creature), object:a=b, item:a=b, zone:40=17, faction:72=76, spell:a=b, "text:Sentinel Hill=the Crossroads" (repeatable)
* `--only <quests>`: copy only these quests of the chain (comma-separated)
* `--suffix <text>`: add this to the copies' titles
* `--dry-run`: show what would happen; change nothing

```
wds quests duplicate 900000 --faction Horde --swap 900020=900021 --swap 122=3258 --swap zone:40=17 --swap "text:Sentinel Hill=the Crossroads"
```

#### wds quests template

The "discipline chain" template (the Warrior's Stance Disciplines): talk -> kill N -> return -> learn the discipline, for one faction.

```
wds quests template <discipline> [options]
```

* `--faction <faction>`: Alliance, Horde or any (default any)
* `--classes <classes>`: the classes (default Warrior)
* `--level <n>`: the level (default 20)
* `--giver <creature>`: the NPC who gives and takes back the drill (the drill sergeant, the champion)
* `--intro-from <creature>`: the NPC of the talk step who sends the player to the giver (none: no talk step)
* `--outpost <name>`: the outpost's name for the texts
* `--kill <creature[:count]>`: what to fight (default count 8)
* `--spell <spell>`: the discipline's spell (an existing one)
* `--clone-spell <spell>`: make the discipline's spell as a clone of this one
* `--spell-text <text>`: the made spell's description
* `--category <category>`: the quest log category (default sort:Warrior)
* `--xp <n>`: experience of the drill
* `--money <amount>`: money of the drill
* `--dry-run`: show what would happen; change nothing

```
wds quests template "Riposte Drill" --faction Alliance --giver 900020 --intro-from 234 --outpost "Sentinel Hill" --kill 122:8 --clone-spell 12295 --xp 1450
```

#### wds quests preview

The quest's texts as the game shows them: $N, $C, $R, $G filled in for a player (the client's own rules).

```
wds quests preview <quest> [options]
```

* `--name <text>`: the player's name (default Thrall)
* `--race <race>`: the race (default Orc)
* `--class <class>`: the class (default Warrior)
* `--female`: a female character ($G)

```
wds quests preview 1718 --name Jaina --race Human --class Mage --female
```

#### wds quests categories

The quest log categories of the game (QuestSort.dbc: Warrior, Cooking, Seasonal...); --new adds one (a game table the server also reads).

```
wds quests categories [options]
```

* `--new <name>`: add a category
* `--dry-run`: show what would happen; change nothing

```
wds quests categories
wds quests categories --new "Stance Disciplines"
```

#### wds quests export

A quest's chain (or quests) to a JSON or CSV file: every field, givers and enders (a file that "wds quests import" reads back exactly).

```
wds quests export <quest> [options]
```

* `--out <file>`: the .json or .csv file
* `--only <quests>`: these quests instead of the chain (comma-separated)

```
wds quests export 1718 --out whirlwind.json
wds quests export 900000 --only 900000,900001 --out drill.csv
```

#### wds quests import

Quests from a JSON or CSV file ("wds quests export"): new quests are made with their IDs, existing ones get the file's values, givers and enders.

```
wds quests import <file> [options]
```

* `--dry-run`: show what would happen; change nothing

```
wds quests import drill.json --dry-run
wds quests import drill.csv
```

#### wds quests delete

Delete a quest (its givers, enders, triggers, translations); the quests around it are linked to each other (--keep-links: only cleared); needs --yes.

```
wds quests delete <quest> [options]
```

* `--keep-links`: clear the other quests' links to it instead of linking them to each other
* `--dry-run`: show what would happen; change nothing

```
wds quests delete 900003 --yes
```

#### wds quests test-in-game

How to try a quest in the game: apply, reload, clear the quest cache, GM commands (.quest add, .go creature id <giver>, .quest complete).

```
wds quests test-in-game <quest>
```

```
wds quests test-in-game 900001
```

### My changes, testing and releasing

#### wds changes list

"My changes": every edited file with its status (in workspace, installed as loose file, in patch-3.MPQ), format and import.

```
wds changes list
```

```
wds changes list
wds changes list --json
```

#### wds changes sets

The change sets (one per import: "Revert this import" undoes a whole set) with their files.

```
wds changes sets
```

```
wds changes sets
wds changes sets --json
```

#### wds changes diff

"Compare": your edit of a file against the game's (a texture, a model, a map file or a data table).

```
wds changes diff <path> [options]
```

* `--out <file.png>`: a texture: the difference heat map; a tile: the height difference

```
wds changes diff Creature\Boar\BoarSkinIvory.blp --out diff.png
wds changes diff DBFilesClient\Spell.dbc --json
wds changes diff Spell
```

#### wds changes revert

"Revert" / "Revert my change": throw away your edit of one file (its loose copy is removed from the game too; needs --yes).

```
wds changes revert <path> [options]
```

* `--reload`: then reload the files in the running game (hot reload; the game must run with it switched on)
* `--dry-run`: show what would happen; change nothing

```
wds changes revert Creature\Boar\BoarSkinIvory.blp --yes
wds changes revert Spell --dry-run
```

#### wds changes revert-set

"Revert this import": put every file of one change set back to the version it had before that import (needs --yes).

A file the import replaced while it held an earlier edit of yours (a map conversion builds on your tables, an update
import replaces your texture) goes back to that edit; the others go back to the game's original. Other imports are not
changed. `--dry-run` lists where each file goes (`--json`: `plan.steps`, `plan.toEarlier`, `plan.toOriginal`). When a
newer import changed one of its files again, nothing is reverted and the command ends with exit code 4, naming the files
(`plan.problems`): revert the newer import first, or use `wds changes revert` on single files. See
[My changes](my-changes.md#undo-a-whole-import).

```
wds changes revert-set <change-set> [options]
```

* `--reload`: then reload the files in the running game (hot reload; the game must run with it switched on)
* `--dry-run`: show what would happen; change nothing

```
wds changes revert-set cs-20261011-101500-123-ab12 --yes
wds changes revert-set cs-20261011-101500-123-ab12 --dry-run
```

#### wds changes install

"Test in game (install as loose files)": copy edited files (default: all) into the game's Data folder (needs --yes).

```
wds changes install [paths...] [options]
```

* `--force`: replace a file in the game folder that the app did not put there (it is backed up first and put back on remove)
* `--reload`: then reload the files in the running game (hot reload; the game must run with it switched on)
* `--dry-run`: show what would happen; change nothing

```
wds changes install --dry-run
wds changes install --yes
wds changes install Creature\Boar\BoarSkinIvory.blp --yes --json
wds changes install --yes --reload
```

#### wds changes remove

"Remove from game" / "Remove all from game": take the loose files the app installed out of the game folder (needs --yes).

```
wds changes remove [paths...] [options]
```

* `--reload`: then reload the files in the running game (hot reload; the game must run with it switched on)
* `--dry-run`: show what would happen; change nothing

```
wds changes remove --yes
wds changes remove Creature\Boar\BoarSkinIvory.blp --yes
wds changes remove --yes --reload
```

#### wds changes build-patch

"Build release patch (patch-3.MPQ)...": every change in one archive, checked by reading it back; --install puts it into the game (needs --yes).

```
wds changes build-patch [options]
```

* `--install`: also install it into the game's Data folder ("Also install it into the game:"); an existing patch-3.MPQ is backed up first
* `--dry-run`: show what would happen; change nothing

```
wds changes build-patch --dry-run
wds changes build-patch
wds changes build-patch --install --yes --json
```

#### wds changes patch-state

What patch-3.MPQ is in the game: none, the release patch the app built (proved by its checksum), or someone else's.

```
wds changes patch-state
```

```
wds changes patch-state
wds changes patch-state --json
```

#### wds changes uninstall-patch

"Uninstall release patch (patch-3.MPQ)": remove the app's patch and put back the one that was there before (needs --yes).

```
wds changes uninstall-patch [options]
```

* `--force`: a patch-3.MPQ the app did not create (or cannot prove it built): move it out into the backups folder (never deleted)
* `--dry-run`: show what would happen; change nothing

```
wds changes uninstall-patch --dry-run
wds changes uninstall-patch --yes
wds changes uninstall-patch --force --yes
```

#### wds changes reload

"Reload in running game": the running game reads changed files again without a restart (hot reload); default: every change of your workspace.

```
wds changes reload [paths...] [options]
```

* `--wait`: wait for the game's report and print what is live now and what needs a relog or a restart (without it: return once the game took the request)
* `--timeout <seconds>`: how long to wait for the game (default 30)
* `--status`: only say whether the running game can reload (and why not); nothing is reloaded

```
wds changes reload --wait
wds changes reload Creature\Boar\BoarSkinIvory.blp --wait --json
wds changes reload --status
wds changes reload
```

#### wds changes folder

"Open my workspace folder": where your edited files are kept (--open opens it in Explorer).

```
wds changes folder [options]
```

* `--open`: open it in Windows Explorer

```
wds changes folder
wds changes folder --open
```

### Other versions (experimental)

Read the data of other World of Warcraft clients without changing them (the **Other versions** tab, see
[Other versions](other-versions.md)). `<client>` is a client's folder, its name in `wds versions find`, a version
(`3.3.5`, `2.4`) or a build (`12340`). The folder that is searched is the `other-clients` setting, or `--folder`.
A modern (CASC) client's files are named by their full path or their number (`#775971`); a listfile you choose
(`--listfile`, or the `other-clients.listfile` setting) names them for wildcard searches.

#### wds versions find

The World of Warcraft clients in the folder of other versions: version, release, data format, readable or not.

```
wds versions find [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting

```
wds versions find
wds versions find --folder "G:\WoW Clients" --json
```

#### wds versions info

One client: version, release, locale, its archives in the order the game reads them (patches that are not Blizzard's marked), files and tables.

```
wds versions info <client> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--blizzard-only`: leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting
* `--all-patches`: read every patch archive the client mounts, even when other-clients.blizzard-only is set
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting

```
wds versions info 3.3.5
wds versions info "2.4.3.8606 - The Burning Crusade" --json
wds versions info _classic_beta_
```

#### wds versions files

A client's files by wildcard (* and ?) with where each is read from; a modern (CASC) client: a full path or #FileDataID, wildcards with a listfile.

```
wds versions files <client> <pattern> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--blizzard-only`: leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting
* `--all-patches`: read every patch archive the client mounts, even when other-clients.blizzard-only is set
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds versions files 3.3.5 World\Maps\Northrend\*.adt
wds versions files 2.4.3 Interface\Icons\INV_Sword_* --limit 0 --json
wds versions files _classic_beta_ #775971
```

#### wds versions extract

Copy files out of a client as they are (one path or #FileDataID, or a wildcard into a folder, keeping the folders).

```
wds versions extract <client> <path-or-pattern> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--blizzard-only`: leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting
* `--all-patches`: read every patch archive the client mounts, even when other-clients.blizzard-only is set
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--out <file-or-folder>`: where to write (default: the current folder)

```
wds versions extract 3.3.5 DBFilesClient\Map.dbc --out wotlk\
wds versions extract 2.4.3 "World\Maps\Expansion01\*.adt" --out tbc_maps\
wds versions extract _classic_beta_ #775971 --out beta\
```

#### wds versions tables

A client's data tables: rows, fields, and whether this version's columns are named or shown raw (a modern client: its DB2 tables from its executable).

```
wds versions tables <client> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--blizzard-only`: leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting
* `--all-patches`: read every patch archive the client mounts, even when other-clients.blizzard-only is set
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--read`: a modern (CASC) client: read every table to count its rows (slower)

```
wds versions tables 3.3.5
wds versions tables 2.4.3 --json
wds versions tables _classic_beta_
```

#### wds versions rows

Rows of a client's data table, old (DBC) or modern (DB2): columns named where this version's layout is known, else col_N.

```
wds versions rows <client> <table> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--blizzard-only`: leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting
* `--all-patches`: read every patch archive the client mounts, even when other-clients.blizzard-only is set
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--columns <list>`: only these columns (comma-separated)
* `--where <column=value> (repeatable)`: only rows whose column has this value (text: contains, case-insensitive)
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds versions rows 3.3.5 Map --columns ID,Directory,MapName
wds versions rows 2.4.3 AreaTable --where MapID=530 --limit 0 --json
wds versions rows _classic_beta_ Map --limit 0
```

#### wds versions compare

One data-table row side by side in several versions, old (DBC) or modern (DB2) tables (columns matched by name).

```
wds versions compare <table> <id> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--blizzard-only`: leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting
* `--all-patches`: read every patch archive the client mounts, even when other-clients.blizzard-only is set
* `--clients <list>`: these clients (comma-separated names, versions or builds; default: one of each MPQ version)
* `--raw`: also the unnamed columns (col_N)
* `--diff`: only the columns whose values differ

```
wds versions compare Spell 133
wds versions compare AreaTable 1519 --clients 1.12,2.4,3.3.5 --diff
wds versions compare Map 0 --clients 1.12,_classic_beta_
```

#### wds versions convert-map

"Convert map...": a map of the 1.60 client (a zone with its subzones, tiles or all) converted into your 1.12 workspace as one change set, with the conversion report; --dry-run only reports.

```
wds versions convert-map <client> <map> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--zone <name-or-id>`: only the tiles with ground of this zone (or its subzones): its name or AreaTable ID in that client
* `--tiles <x_y,...>`: exactly these tiles (for example 32_48,33_48)
* `--dry-run`: show what would happen; change nothing

```
wds versions convert-map _classic_beta_ Azeroth --zone "Elwynn Forest" --dry-run
wds versions convert-map _classic_beta_ 0 --tiles 32_48,33_48 --yes
wds versions convert-map _classic_beta_ "Zephras Isle" --yes --json
```

#### wds versions updates

The update catalog: every difference between another client (the 1.60 Classic client) and your 1.12 game, grouped into updates you can pick (zone terrain, new maps, lighting, textures, models, interface, sounds, tables, engine features), each with its portability class.

```
wds versions updates <client> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--kind <kind>`: only one kind: zone-terrain, new-map, sky-lighting, skybox, weather, ground-textures, textures, models, buildings, interface-art, interface-strings, fonts, sounds, data-tables, engine-features, other
* `--class <class>`: only one portability class: ready, needs-converter, needs-server, needs-client-code, no-visible-change
* `--zone <text>`: only updates whose zone, map, folder or table contains this text
* `--search <words>`: only updates with these words in the title, place, ID or facts
* `--importable`: only what the app can import now
* `--hide-unchanged`: leave out the differences without a visible change
* `--rescan`: scan both sides again even when the cached catalog is current
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds versions updates _classic_beta_
wds versions updates _classic_beta_ --kind zone-terrain --zone "Elwynn"
wds versions updates _classic_beta_ --class ready --importable --limit 0 --json
```

#### wds versions update-info

One update of the catalog: what it changes in your game, what it needs, why it has its class, the measured facts and its files.

```
wds versions update-info <client> <id> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--rescan`: scan both sides again even when the cached catalog is current
* `--limit <n>`: at most n results (default 50; 0 = all)

```
wds versions update-info _classic_beta_ terrain:0:12
wds versions update-info _classic_beta_ tex:changed:creature --limit 0 --json
```

#### wds versions import-updates

Import picked updates of the catalog into your 1.12 workspace as one change set (terrain through the map conversion, textures, interface art, fonts and MP3 sounds made loadable by 1.12); --dry-run only reports.

```
wds versions import-updates <client> <ids...> [options]
```

* `--folder <folder>`: search this folder for clients instead of the other-clients setting
* `--listfile <file>`: names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting
* `--dry-run`: show what would happen; change nothing

```
wds versions import-updates _classic_beta_ terrain:0:12 --dry-run
wds versions import-updates _classic_beta_ terrain:0:12 terrain:0:40 tex:changed:creature --yes
wds versions import-updates _classic_beta_ fonts:changed --yes --json
```

### Help and the command line itself

#### wds version

The version and build (the same as the app's Help > About WoW Data Studio).

```
wds version
```

```
wds version
wds --version
wds version --json
```

#### wds commands

Every command of wds (with --json: the whole tree with arguments, options and examples).

```
wds commands
```

```
wds commands
wds commands --json
```

#### wds features

Every feature of the app and its command (GUI <-> CLI), from the feature registry.

```
wds features [options]
```

* `--area <name>`: only one area (Setup, Find, Textures, Models, Maps, Server maps, Data tables, My changes, Help, Command line)
* `--markdown`: print the coverage table of docs\manual\command-line.md (for maintainers)

```
wds features
wds features --area "Data tables" --json
wds features --markdown
```

#### wds manual

Where the user manual (or one chapter) is; --open opens it in the browser.

```
wds manual [chapter] [options]
```

* `--open`: open it (in the web browser, or the program for .md files)

```
wds manual
wds manual command-line --open
wds manual dbc-editor
```

## Coverage: the app and the command line

Every feature of the app and its command. *(app only)* marks what is only in the app, with the reason; *(command
line only)* the helpers for scripts. The list is kept by the app's feature registry: `wds features` prints it, and the
app's tests fail when the app, the command line and this table differ.

| Feature | In the app | Command |
|---|---|---|
| setup.client | **Change World of Warcraft folder...** | `wds setup client` |
| setup.check | **Load game data** | `wds setup check` |
| setup.status | (command line only) | `wds setup status` |
| settings.list | **File > Settings...** | `wds settings list` |
| settings.get | **File > Settings...** | `wds settings get` |
| settings.set | **File > Settings...** | `wds settings set` |
| settings.unset | **File > Settings...** | `wds settings unset` |
| serverdb.test | **Test connection** | `wds serverdb test` |
| serverdb.defaults | **Use the defaults (127.0.0.1:3306, root/root, mangos)** | `wds serverdb defaults` |
| serverdb.reload | **File > Reload names from the server database** | `wds serverdb reload` |
| index.build | **File > Rebuild the index** | `wds index build` |
| index.status | **Names from server database: connected** | `wds index status` |
| find.search | **Search models, textures, creatures, items...** | `wds find search` |
| find.only-changes | **Show only my changes** | `wds find search` |
| find.list | **Show only this type** | `wds find list` |
| find.info | **WHERE IT COMES FROM** | `wds find info` |
| find.used | **Used in game** | `wds find used` |
| find.where | **TEXTURES IT USES** | `wds find where` |
| find.files | (command line only) | `wds find files` |
| find.extract | (command line only) | `wds find extract` |
| textures.info | **Mip level** | `wds textures info` |
| textures.export | **1. Export texture as PNG...** | `wds textures export` |
| textures.export-all | **TEXTURES IT USES** | `wds textures export-all` |
| textures.import | **2. Import edited PNG...** | `wds textures import` |
| textures.edit-both | **Edit both (recommended)** | `wds textures import` |
| textures.keep-shine | **Keep the original shine map (recommended)** | `wds textures import` |
| textures.keep-black | **Keep black areas black (recommended)** | `wds textures import` |
| textures.compare | **Compare original vs. edited** | `wds textures compare` |
| models.info | **Model statistics** | `wds models info` |
| models.export | **Tools > Export for Blender...** | `wds models export` |
| models.open | **Open in Blender** | `wds models open` |
| models.import | **File > Import edited model...** | `wds models import` |
| models.validate | **Import edited model...** | `wds models validate` |
| models.compare | **Compare original vs. edited model** | `wds models compare` |
| models.revert | **Revert the model** | `wds changes revert` |
| models.addon | **Tools > Install the Blender add-on...** | `wds models addon` |
| models.render | **Save image...** | `wds models render` |
| models.export-skeleton | **Skeleton and animations (for editing)** | `wds models export` |
| models.import-skeleton | **Skeleton and animations** | `wds models import` |
| models.effects | **Particles and ribbons** | `wds models import` |
| models.anims | **Animations and what the game plays them for** | `wds models anims` |
| models.skeleton | **Skeleton, attachment points and events** | `wds models skeleton` |
| models.animation-preview | **Loop** | `wds models render` |
| models.attachment-points | **Attachment points** | `wds models skeleton` |
| models.compare-animations | **Same animation on both** | `wds models compare` |
| models.compare-skins | **Tools > Compare two skins of this model (3D)** | (app only: viewing: two live 3D views side by side (render each skin with "wds models render --display")) |
| models.viewer | **Displays using this model** | (app only: viewing: the interactive 3D viewer (camera, animations, geosets, background); "wds models info" lists the displays and skins) |
| view.popout | **Tools > Open the 3D view in a new window** | (app only: viewing: pop-out 3D windows (follow / pin, full screen)) |
| maps.list | **Tools > Maps and terrain** | `wds maps list` |
| maps.zones | **ZONES** | `wds maps zones` |
| maps.tiles | **MAPS** | `wds maps tiles` |
| maps.specular | **Specular ground (_s textures)** | `wds settings set` |
| maps.minimap | **Maps** | `wds maps minimap` |
| maps.tile | **On this tile** | `wds maps tile` |
| maps.breakdown | **Tools > Break down this tile...** | `wds maps breakdown` |
| maps.render | **On its own** | `wds maps render` |
| maps.noggit | **Tools > Edit in Noggit...** | `wds maps noggit` |
| maps.import-noggit | **Tools > Import map changes...** | `wds maps import-noggit` |
| maps.compare | **Height difference** | `wds maps compare` |
| maps.test | **Test map in game (loose files)** | `wds maps test-in-game` |
| maps.revert | **Revert the tile** | `wds maps revert` |
| maps.export-terrain | **Tools > Open terrain in Blender...** | `wds maps export-terrain` |
| maps.import-terrain | **Tools > Import edited terrain...** | `wds maps import-terrain` |
| maps.export-building | **Open building in Blender** | `wds maps export-building` |
| maps.show-in-browse | **Show in Browse** | (app only: navigation between tabs (on the command line: "wds find info" with the file)) |
| maps.view3d | **Orbit** | (app only: viewing: the interactive 3D view of a tile (orbit, fly, neighbours, doodads, water); "wds maps render" draws 2D previews) |
| server.status | **Tools > Update server maps...** | `wds server-maps status` |
| server.extract | **Extract into staging** | `wds server-maps extract` |
| server.preview | **Show what would change** | `wds server-maps install` |
| server.install | **Install into the server** | `wds server-maps install` |
| server.undo | **Undo server update** | `wds server-maps undo` |
| dbc.tables | **Tools > Data tables (DBC editor)** | `wds dbc tables` |
| dbc.changed | **Only tables I changed** | `wds dbc tables` |
| dbc.info | **Server also needs this file** | `wds dbc info` |
| dbc.rows | **Search all text...** | `wds dbc rows` |
| dbc.get | **Go to ID** | `wds dbc get` |
| dbc.set | **Values** | `wds dbc set` |
| dbc.add | **Add row** | `wds dbc add` |
| dbc.duplicate | **Duplicate as new** | `wds dbc duplicate` |
| dbc.delete | **Delete rows** | `wds dbc delete` |
| dbc.bulk | **Bulk edit...** | `wds dbc bulk` |
| dbc.export | **Export all rows...** | `wds dbc export` |
| dbc.import | **Apply import** | `wds dbc import` |
| dbc.used-by | **Used by** | `wds dbc used-by` |
| dbc.diff | **Compare with game** | `wds dbc diff` |
| dbc.save | **Save to my workspace** | `wds dbc set` |
| dbc.copy-to-server | **Tools > Copy tables to the server...** | `wds dbc copy-to-server` |
| dbc.undo-server-copy | **Undo last copy to server** | `wds dbc undo-server-copy` |
| dbc.undo | **Discard changes** | (app only: the editor's unsaved state (Undo, Redo, Discard): every "wds dbc" edit is saved at once; use --dry-run to look first and "wds changes revert" to undo) |
| dbc.show-in-3d | **Show in 3D** | (app only: navigation to the 3D viewer ("wds models render" draws a display)) |
| sdata.status | **Tools > Server data (world database)** | `wds server status` |
| sdata.views | **Server data** | `wds server views` |
| sdata.find | **Search by name or ID...** | `wds server find` |
| sdata.show | **Values** | `wds server show` |
| sdata.get | **Values** | `wds server get` |
| sdata.set | **Save to my workspace** | `wds server set` |
| sdata.add | **Add new** | `wds server add` |
| sdata.add-here | **Add row here** | `wds server add` |
| sdata.duplicate | **Duplicate as new** | `wds server duplicate` |
| sdata.delete | **Delete** | `wds server delete` |
| sdata.bulk | **Bulk edit...** | `wds server bulk` |
| sdata.used-by | **Used by** | `wds server used-by` |
| sdata.changes | **Compare with the server** | `wds server changes` |
| sdata.check | **Check client and server...** | `wds server check` |
| sdata.match-server | **Make the server match the game** | `wds server match` |
| sdata.match-client | **Make the game match the server** | `wds server match` |
| sdata.export-sql | **Export SQL patch...** | `wds server export-sql` |
| sdata.apply | **Apply to server...** | `wds server apply` |
| sdata.undo-apply | **Undo last apply** | `wds server undo-apply` |
| sdata.applies | **Applied changes log** | `wds server applies` |
| sdata.export | **Export the list...** | `wds server export` |
| sdata.import | **Import...** | `wds server import` |
| sdata.wdb | **Clear the game's cache (WDB)...** | `wds server clear-wdb` |
| sdata.patch | **Content patch of the server** | `wds settings set` |
| sdata.custom-ids | **Custom ID range** | `wds settings set` |
| sdata.spell-ids | **Spell ID range** | `wds settings set` |
| sdata.dbc-link | **Edit it in the Server data tab** | `wds server show` |
| sdata.undo | **Discard changes** | (app only: the editor's unsaved state (Undo, Redo, Discard changes): every "wds server" edit is saved at once; use --dry-run to look first and "wds changes revert" to undo) |
| sdata.show-in-browse | **Show in Browse** | (app only: navigation to the Browse tab's 3D viewer and to the Data tables tab ("wds find info item:25" and "wds dbc get" on the command line)) |
| spells.show | **Tools > Spell designer** | `wds spells show` |
| spells.find | **Find a spell** | `wds spells find` |
| spells.fields | **Basics** | `wds spells fields` |
| spells.new | **New spell** | `wds spells new` |
| spells.clone | **Clone this spell** | `wds spells clone` |
| spells.set | **Save to my workspace** | `wds spells set` |
| spells.tooltip | **Tooltip preview** | `wds spells tooltip` |
| spells.check | **Checks (1.12 client and server)** | `wds spells check` |
| spells.explain | **What this effect does** | `wds spells explain` |
| spells.icons | **Find icons** | `wds spells icons` |
| spells.visual | **Pick visual...** | `wds spells visual` |
| spells.visual-duplicate | **Duplicate visual (tint and size)** | `wds spells visual` |
| spells.trainer | **Add to trainer** | `wds spells learn` |
| spells.skill-line | **Add to skill line** | `wds spells learn` |
| spells.sync | **Sync with the server** | `wds spells sync` |
| spells.renumber | **Change ID** | `wds spells renumber` |
| spells.renumber-all | **Fix all spell IDs** | `wds spells renumber` |
| spells.visual-3d | **Show in 3D** | (app only: viewing: the effect models in the designer's 3D view ("wds models render" draws a model to PNG)) |
| spells.undo | **Discard changes** | (app only: the editor's unsaved state: every "wds spells" command saves at once; use --dry-run to look first and "wds changes revert" to undo) |
| talents.trees | **Talent trees** | `wds talents trees` |
| talents.show | **Trees of this class** | `wds talents show` |
| talents.add-tree | **Add tree...** | `wds talents add-tree` |
| talents.set-tree | **Apply tree changes** | `wds talents set-tree` |
| talents.remove-tree | **Remove tree** | `wds talents remove-tree` |
| talents.add | **Add talent here** | `wds talents add` |
| talents.rank-spells | **Make rank spells** | `wds talents add` |
| talents.set | **Apply talent changes** | `wds talents set` |
| talents.remove | **Remove talent** | `wds talents remove` |
| talents.check | **Check the tree** | `wds talents check` |
| classes.list | **Tools > Classes and talents** | `wds classes list` |
| classes.show | **Spells by level** | `wds classes show` |
| classes.assign | **Assign level** | `wds classes assign` |
| classes.mask | **Class masks** | `wds classes mask` |
| classes.new | **New class (experimental)** | `wds classes new` |
| classes.engine-report | **What a new class still needs** | `wds classes engine-report` |
| classes.grid | **The selected place** | (app only: viewing: the talent grid with icons, arrows and the tree's background ("wds talents show" prints the grid as text)) |
| classes.undo | **Discard changes** | (app only: the editor's unsaved state: every "wds talents" and "wds classes" command saves at once; use --dry-run to look first and "wds changes revert" to undo) |
| quests.show | **Tools > Quest chains** | `wds quests show` |
| quests.find | **Find a quest** | `wds quests find` |
| quests.fields | **Who can take it** | `wds quests fields` |
| quests.chain | **Open chain** | `wds quests chain` |
| quests.check | **Check the chain** | `wds quests check` |
| quests.new | **New quest** | `wds quests new` |
| quests.add-after | **Add quest after** | `wds quests new` |
| quests.add-before | **Add quest before** | `wds quests new` |
| quests.set | **Save to my workspace** | `wds quests set` |
| quests.races-classes | **Classes** | `wds quests set` |
| quests.branch | **Add branch** | `wds quests branch` |
| quests.join | **Join** | `wds quests join` |
| quests.giver | **Add giver** | `wds quests giver` |
| quests.ender | **Add ender** | `wds quests ender` |
| quests.objective | **Objectives in words** | `wds quests objective` |
| quests.talk | **Add speak-to objective** | `wds quests talk` |
| quests.explore | **Add explore objective** | `wds quests explore` |
| quests.reward | **Set reward spell** | `wds quests reward` |
| quests.drops | **Add drop source** | `wds quests drops` |
| quests.loot-editor | **Open in the loot editor** | `wds quests drops` |
| quests.npcs | **Find NPC** | `wds quests npcs` |
| quests.npc | **Make the quest giver** | `wds quests npc` |
| quests.place | **Place here** | `wds quests place` |
| quests.duplicate | **Duplicate for the other faction** | `wds quests duplicate` |
| quests.template | **Make the chain** | `wds quests template` |
| quests.preview | **Text preview ($N, $C, $R, $G)** | `wds quests preview` |
| quests.categories | **New quest log category** | `wds quests categories` |
| quests.export | **Export chain...** | `wds quests export` |
| quests.import | **Import quests...** | `wds quests import` |
| quests.delete | **Delete quest** | `wds quests delete` |
| quests.test | **Test in game** | `wds quests test-in-game` |
| quests.apply | **Apply to server...** | `wds server apply` |
| quests.spell-link | **Open in the Spell designer** | `wds spells show` |
| quests.flowchart | **Flowchart** | (app only: viewing: the chain drawn as a flowchart with lanes and arrows ("wds quests chain" prints it as text, JSON or a Mermaid chart)) |
| quests.map | **Show on map** | (app only: viewing: the NPCs' spawns as pins on the Maps tab ("wds quests show" lists the givers, "wds quests test-in-game" their positions)) |
| quests.pick | **Pick on the map** | (app only: viewing: a position picked by clicking the map (on the command line: --map, --x, --y)) |
| quests.npc-preview | **Preview the NPC** | (app only: viewing: the NPC's model in 3D ("wds models render" draws a model to PNG)) |
| quests.undo | **Discard changes** | (app only: the editor's unsaved state: every "wds quests" command saves at once; use --dry-run to look first and "wds changes revert" to undo) |
| changes.list | **My changes** | `wds changes list` |
| changes.sets | **Revert this import** | `wds changes sets` |
| changes.diff | **Compare** | `wds changes diff` |
| changes.revert | **Revert my change** | `wds changes revert` |
| changes.revert-set | **Revert this import** | `wds changes revert-set` |
| changes.install | **Test all in game (install as loose files)** | `wds changes install` |
| changes.install-one | **3. Test in game (install as loose files)** | `wds changes install` |
| changes.remove | **Remove all from game** | `wds changes remove` |
| changes.build-patch | **File > Build release patch (patch-3.MPQ)...** | `wds changes build-patch` |
| changes.patch-state | **Also install it into the game:** | `wds changes patch-state` |
| changes.uninstall-patch | **Uninstall release patch (patch-3.MPQ)** | `wds changes uninstall-patch` |
| changes.folder | **File > Open my workspace folder** | `wds changes folder` |
| changes.reload | **Reload in running game** | `wds changes reload` |
| settings.hot-reload | **Hot reload in the running game** | `wds settings set` |
| changes.show | **Show** | (app only: navigation: jumps to the file in its tab ("wds find info" describes it)) |
| versions.folder | **Folder of other game versions** | `wds settings set` |
| versions.blizzard-only | **Blizzard archives only** | `wds settings set` |
| versions.listfile | **Choose listfile...** | `wds settings set` |
| versions.find | **Find clients** | `wds versions find` |
| versions.info | **Archives** | `wds versions info` |
| versions.files | **Files matching** | `wds versions files` |
| versions.extract | **Extract selected...** | `wds versions extract` |
| versions.preview | **Preview** | (app only: viewing: a picture of the selected texture ("wds versions extract" copies the file out)) |
| versions.tables | **Tables of this version** | `wds versions tables` |
| versions.rows | **Find in rows** | `wds versions rows` |
| versions.compare | **Compare across versions** | `wds versions compare` |
| versions.convert-map | **Convert map...** | `wds versions convert-map` |
| versions.updates | **Scan for updates** | `wds versions updates` |
| versions.updates-filter | **Hide differences without a visible change** | `wds versions updates` |
| versions.update-info | **About this update** | `wds versions update-info` |
| versions.import-preview | **Preview import** | `wds versions import-updates` |
| versions.import-updates | **Import picked updates** | `wds versions import-updates` |
| help.manual | **Help > User manual** | `wds manual` |
| help.about | **Help > About WoW Data Studio** | `wds version` |
| help.cli | **Help > The command line (wds)** | `wds manual` |
| help.how | **Help > How it works** | (app only: viewing: a help panel of the window ("wds --help" and "wds GROUP COMMAND --help" on the command line)) |
| help.loose-files | **Help > About the loose-file mode (LOOSE_FILES_PATCH.md)** | (app only: viewing: opens a document (it is in the docs folder of the installation)) |
| cli.commands | (command line only) | `wds commands` |
| cli.features | (command line only) | `wds features` |

## Recipes

Example scripts (PowerShell and batch) are installed in the `scripts` folder of the installation
(`%LOCALAPPDATA%\Programs\WoW Data Studio\scripts`).

### Export every creature texture of a zone

```
wds find search --type creature --zone "Durotar" --limit 0 --json > creatures.json
wds textures export-all creature:3098 --out exports\Durotar
```

`scripts\export_zone_creature_textures.ps1 -Zone Durotar -Out exports\Durotar` does both for every creature of the
zone. The zone filter uses the spawns from your server's database (`wds serverdb reload` reads them).

### A table through a spreadsheet and back

```
wds dbc export Spell --out Spell.csv
rem ... edit Spell.csv in LibreOffice Calc or Excel, save as CSV (UTF-8) ...
wds dbc import Spell Spell.csv
wds dbc import Spell Spell.csv --apply
```

The first import shows what would change (and the errors that would block it); `--apply` imports it and saves the
table into your workspace. `scripts\dbc_csv_round_trip.cmd Spell` does the export and the checked import.

### Build and install a release patch

```
wds changes build-patch --dry-run
wds changes build-patch --install --yes
wds changes patch-state
```

`scripts\build_and_install_patch.ps1` stops when a check fails and prints what was installed.

### Change a spell's icon

```
wds dbc get Spell 133 SpellIconID
wds dbc rows SpellIcon --search fireball --columns ID,TextureFilename
wds dbc bulk Spell --where Name==Fireball --where NameSubtext=Rank --column SpellIconID --op set --value 184
wds changes install --yes
```

### Repaint a texture with another program

```
wds textures export Creature\Boar\BoarSkinIvory.blp --out boar.png
rem ... edit boar.png with any program ...
wds textures import boar.png --dry-run
wds textures import boar.png
```

### Change an item on the server and deliver it

```
wds server show item_template 25
wds server set item_template 25 --set dmg_max1=7 --set quality=Uncommon
wds server export-sql --out sharper_sword.sql
wds server apply --dry-run --show-sql
wds server apply --yes
wds server clear-wdb --yes
```

The SQL file can also be given to another server owner (it comes with `sharper_sword.revert.sql`). Restart the world
server, or use the `.reload` command the apply prints.

### Clone a spell, teach it at level 20 and check both sides

```
wds spells clone 686 --name "Shadow Lance" --rank "Rank 1" --set cast-time=2 --set effect1-value=40-48
wds spells learn 35000 --trainer 3156 --level 20 --cost 25s
wds spells learn 35000 --skill-line 593 --class Warlock
wds server check spells --ids 35000
wds changes build-patch
wds server apply --yes
```

The first free ID of your spell ID range is used (here 35000; spells stay at or below 40000, the highest ID the VMaNGOS server casts). `server check` finds no difference between the game's
`Spell.dbc` and the server's `spell_template`; the release patch carries the game's side and `server apply` writes the
server's rows (the world server must be stopped; it reads spells at start).

### A discipline chain for both factions, checked and applied

```
wds quests npc "Drill Sergeant Halvor" --copy-from 821 --subname "Drill Sergeant" --map 0 --x -10508 --y 1053
wds quests npc "Champion Gorrak" --copy-from 3429 --subname "Crossroads Champion" --map 1 --x -470 --y -2620
wds quests template "Riposte Drill" --faction Alliance --giver 900000 --intro-from 234 --outpost "Sentinel Hill" --kill 122:8 --clone-spell 12295 --xp 1450
wds quests duplicate 900000 --faction Horde --swap 900000=900001 --swap 234=3429 --swap 122=3258 --swap zone:40=17 --swap "text:Sentinel Hill=the Crossroads"
wds quests check 900000
wds quests check 900002
wds server apply --yes
wds quests test-in-game 900001
```

The two NPCs get the first free creature IDs of your custom range (900000, 900001), the template the first free quest
IDs (900000 the talk step, 900001 the drill) and the discipline's spell (a clone of Tactical Mastery, 12295, with its
learn spell). `duplicate` copies the whole chain for the Horde (900002, 900003) with the champion, Thork and Bristleback
Hunters instead. `check` exits with code 3 while a chain has an error. Clear the game's quest cache before trying it
(`wds server clear-wdb --yes`, with the game closed).

### Isolated runs

`--home` keeps a script's settings, index and edits apart from yours:

```
wds --home D:\wds-ci setup client "D:\Games\WoW 1.12" --no-index
wds --home D:\wds-ci settings set server.enabled false
wds --home D:\wds-ci index build
```

## Good to know

* `wds` and the app can run at the same time; they share the workspace, and neither overwrites the other's changes.
  After a change on the command line, the app shows it when you switch tabs (or make a change yourself); a table you
  have open with unsaved changes in the app says that it changed in your workspace. Only one of them should change the
  *same* file at the same moment.
* `wds models render` uses the app's 3D renderer: it starts `WoWDataStudio.exe` (installed next to `wds.exe`) without a
  window and needs a graphics driver with OpenGL 3.3.
* `wds models open`, `wds maps export-terrain --open`, `wds models addon --install` and `wds maps noggit --launch` start
  Blender or Noggit, which are separate programs.
* See also [Known issues](known-issues.md) for what the command line cannot do yet.
