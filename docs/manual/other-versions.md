# Other versions (experimental)

The **Other versions** tab reads the data of other World of Warcraft versions: The Burning Crusade (2.4.3), Wrath of
the Lich King (3.3.5a), other 1.12 copies and any other client that stores its data in MPQ archives, and the files of
modern clients (Warlords of Draenor 6.x, and the clients Battle.net installs today: Classic Era, the "1.60" Classic
branch, MoP Classic, Retail). You need no server for them: everything comes from the client's own files. Use it to look things up and compare them with 1.12 (how
a spell, a map or a creature's display changed), and to copy files out for reference.

It is **experimental**: it is new, it may change, and later versions will add more (see *Not yet* below). It only ever
*reads* those clients. Nothing in their folders is changed, and your 1.12 game and your changes are not involved.

## Before you start: a folder of client copies

Put the clients you want to read into one folder, each in its own subfolder (the folder with its `Wow.exe` and `Data`
folder). Copies are best: a game that Battle.net keeps up to date may change while you read it.

For example:

```
G:\WoW Clients\
    2.4.3.8606 - The Burning Crusade\      (Wow.exe, Data\...)
    3.3.5a.12340 - Wrath of the Lich King\ (Wow.exe, Data\...)
    1.12.1.5875 - WoW Client\              (WoW.exe, Data\...)
```

## Finding the clients

1. Open the tab: **Tools > Other versions (experimental)**, or click **Other versions** at the top.
2. Next to **Folder of other game versions**, click **Browse...** and pick the folder. The app remembers it.
3. Click **Find clients**. The list **Clients found** shows every client in the folder and its subfolders: its
   folder name, the **Version** and **Build** from its `Wow.exe`, how it stores its data (**Data**: MPQ or CASC) and
   whether the app can read it (**Readable**).

Clients from 6.x (Warlords of Draenor) on, and every client that Battle.net installs today (Retail, Classic Era, the
other Classic flavours), store their data in *CASC* (see the [Glossary](glossary.md)). Each Battle.net flavour (its
`_classic_beta_`, `_retail_`... folder) is listed as a client of its own. Their files and their data tables can be read
(see *Modern clients* below).

![The clients found, and the archives of the 3.3.5a copy with a server's patch marked](../screenshots/137_other_versions_archives.png)

## Looking at a client

Click a readable client in the list. The line above the tabs says what it is (version, release, language) and how many
files and data tables it has.

### Files

Type a path with `*` and `?` into **Files matching** and press <kbd>Enter</kbd> (or click **Search**), for example
`World\Maps\Northrend\*.adt` or `Interface\Icons\INV_Sword_*`. The list shows each file and the archive it is read
from.

* Click a texture (`.blp`) to see it under **Preview**, with its size.
* Select one or more files and click **Extract selected...** to copy them out as they are, into a folder you pick.
  Their folders are kept (`World\Maps\Northrend\Northrend_30_20.adt` lands in `<your folder>\World\Maps\Northrend\`).
  The app refuses a folder inside the client.

![Storm Peaks ground textures of 3.3.5a, one in the preview](../screenshots/138_other_versions_files.png)

### Tables of this version

The client's data tables (its DBC files: spells, maps, areas, creature displays...), with their rows. Click a table to
see its rows; type into **Find in rows** to show only the rows with that text in any column.

Each version has its own columns: a table of 2.4.3 has other columns than the same table of 1.12. The **Columns** list
says for each table whether the app knows this version's columns:

* *named*: the columns have names (the same kind of layout as the [Data tables](dbc-editor.md) tab uses for 1.12),
  checked against the version's own files. For 2.4.3, 73 tables are named, for 3.3.5a 54: the tables of maps and areas,
  lights, sounds, liquids, the world map, flight paths, creature, item and object displays, characters, classes, races,
  factions, skills, talents and spells. A column keeps the name it has in 1.12 when it holds the same thing, so
  **Compare across versions** lines them up;
* *raw*: the app does not know this version's columns yet. Every field is shown as a number named `col_0`, `col_1`...,
  and a text shows as a number too (its place in the file).

The tables of other versions are read only: to change data, use the **Data tables** tab of your 1.12 game.

![The Map table of 3.3.5a with its named columns](../screenshots/139_other_versions_tables.png)

### Archives

The client's archives in the order that version of the game reads them: a file is taken from the first archive that
has it. The order was read from each version's own `Wow.exe` (TBC and WotLK read their patch archives, their language
archives and their base archives in a different order than 1.12).

### Compare across versions

One row of a data table side by side in several versions:

1. Type the table's name into **Table** (for example `Spell`, `Map`, `AreaTable`, `CreatureDisplayInfo`) and the row's
   **ID**.
2. Tick the versions to compare (one of each readable version found).
3. Click **Compare**. Each line is a column (matched by its name), with its value in each version; `-` means the
   version has no such column or no such row. Tick **Only differences** to see only the columns that changed.

![Fireball (spell 133) in 1.12.1, 2.4.3 and 3.3.5a, only the columns that differ](../screenshots/140_other_versions_compare.png)

Columns that are not named for a version (*raw*) are left out of the comparison, because the same position means
different things in different versions. The note above the result says so.

## Modern clients (CASC)

A modern client finds its files by number (the *FileDataID*), and many of its files have no name inside the client.
In **Files matching**, type:

* a full path, for example `World\Maps\Azeroth\Azeroth.wdt`: found when the client knows that name (Warlords of
  Draenor knows nearly all of them, the newer clients only some);
* a file number with `#`, for example `#775971` (that is the Azeroth map file in every modern client);
* a wildcard such as `World\Maps\Azeroth\*`, only with a listfile (below).

The list shows where the file stands: *local* (on this computer), *not downloaded* (Battle.net fetches it when the game
needs it) or *encrypted* (cannot be read). **Preview** and **Extract selected...** work as for the other clients; a file
known only by its number is copied as `<number>.<type>`, with the type taken from its first bytes (`.blp`, `.m2`,
`.adt`, `.wdt`, `.db2`...).

![The 1.60 Classic beta: a minimap tile of Elwynn Forest found by its number](../screenshots/141_other_versions_modern.png)

*Their data tables* (DB2 files) are listed in **Tables of this version** by name, from the client's own program file:
more than a thousand of them. A table is read when you click it. Most of their columns are raw (`col_N`); for 1.60.1
(build 70450) the tables that maps need have names (Map, AreaTable, the liquid tables, Light, WMOAreaTable, the ground
effects, ZoneMusic). Records that are encrypted in the client are left out, and the note above the rows says how many.
In **Compare across versions** the modern clients are listed too, not ticked at first.

**Listfile for modern clients** (optional): a listfile is a text file that names the files (lines `FileDataID;path`).
The app does not come with one and never downloads one; if you have one, click **Choose listfile...** and pick it.
Then the files have their names, and wildcards work.

## Blizzard archives only

Some client copies contain archives that Blizzard did not ship: a private server's or a mod's patch, such as a
`patch-7.mpq` in a 3.3.5a copy. Such a patch is read first and replaces some of the game's files (that one replaces 19
data tables, `Spell.dbc` among them). The **Archives** list marks these archives *not Blizzard's*.

Tick **Blizzard archives only** to leave them out and read the game as Blizzard released it. The app knows the patch
archives Blizzard shipped with 1.12.1, 2.4.3 and 3.3.5a; for other versions every archive is read.

## Converting a map to 1.12

**Convert map...** brings a map of the "1.60" Classic client (the modern `_classic_beta_`, build 1.60.1.70450) into
your 1.12 game: a zone with its subzones, some tiles, or a whole map, also a map 1.12 does not have (for example
Zephras Isle). The converted files go into your *workspace* as one change, like an import: your game folder is not
changed, **Revert this import** in [My changes](my-changes.md) takes it all back, and **Test all in game (install as
loose files)** and the release patch work as for any other change.

1. In the list of clients, click the 1.60 client (`_classic_beta_`). The button **Convert map...** appears next to its
   name. Click it.
2. Pick the **Map** (name, ID and folder; for example *Eastern Kingdoms (0, Azeroth)*).
3. Choose what to convert:
   * **A zone and its subzones**: type or pick the zone, for example *Elwynn Forest*. Every tile with ground of that zone
     is converted. A tile is converted whole, so the zones next to it change too (the report names them);
   * **These tiles**: tiles as `x_y`, separated by commas, for example `32_48,33_48`;
   * **The whole map**.
4. Click **Preview**. The app converts everything in memory and shows the **Conversion report**; nothing is written.
   It takes a few seconds (the whole of Eastern Kingdoms: about 10 seconds; **Stop** stops it).
5. Read the report, then click **Convert**. The files are checked with the 1.12 checks first (nothing is written when
   one fails) and saved as one change set.

What the conversion does:

* *Ground*: heights, holes, texture layers and their blending, water, lava and slime, zones. 1.12 shows at most 4
  texture layers per chunk: the 4 most visible are kept and the rest blended in. The 1.60 vertex colours are dropped.
  Water types become 1.12's four kinds (river, ocean, magma, slime) from the 1.60 client's own liquid table. Grass and
  small plants (ground effects) stay as in your 1.12 tile where the chunk keeps its texture.
* *Shadows*: where the ground is the same as in your 1.12 tile, your 1.12 shadow is kept; re-sculpted ground gets no
  shadow (1.60 has almost none of its own).
* *Ground textures*: a texture your game has (by its name) is used as it is. A new one is converted from the 1.60 file
  (at most 512 x 512, the largest the 1.12 game loads) and saved with its `_s` file.
* *Trees, rocks and buildings*: kept when your game has the model. Where 1.60 put a new model at the same place
  (many Elwynn trees, some city buildings), your 1.12 model stays. Models and buildings 1.12 does not have are left out
  and listed (converting them comes later).
* *Zones*: a zone your AreaTable has keeps its ID; a 1.60 zone gets a new AreaTable row (its 1.60 ID, its name, a free
  exploration bit).
* *Map files*: the tile list (WDT), the far terrain on the horizon (WDL, rebuilt from the new heights) and the minimap
  (1.60's 512 x 512 pictures made 256 x 256, with their entries in `md5translate.trs`). A map 1.12 does not have gets a
  new Map row (its folder is the map's name, for example `ZephrasIsle`), a loading screen your game has, and a default
  light.
* *Names*: only from your own files. The names of your 1.12 game's files are found by their hashes in a Classic Era
  copy (`_classic_era_`) next to the 1.60 client, plus the listfile you chose (**Choose listfile...**). A new texture
  without a name is saved as `World\Converted\<number>.blp`. Without a Classic Era copy far fewer names are known, and
  more models are left out.

![Converting Elwynn Forest: the report of the preview](../screenshots/142_other_versions_convert_map.png)

*Your server.* The server keeps its own copy of the zones and maps, and its own terrain files:

1. New zones (and a new map): in the [Server data](server-data.md) tab, **Check client and server...**, check *areas*
   (and *maps*), select the new rows and click **Make the server match the game**; save and apply.
2. Build the release patch (**Build release patch (patch-3.MPQ)...**, installed into the game), then **Update server
   maps...** ([Maps and terrain](maps-and-terrain.md#update-server-maps)).

The report lists these steps with the IDs.

## The update catalog: picking 1.60 updates for your game

The **Updates** tab lists *every* difference between the "1.60" Classic client and your 1.12 game, grouped into updates
you can pick and bring into your game one at a time: a zone's terrain, a new map, the re-lit light zones of a zone, the
repainted textures of a folder, new interface art, a data table, an engine feature. Each update says what it would
change in your game, what it needs, and what it takes to bring it over (its class). The differences that only the
modern engine or a server can use are listed too, so you know them.

1. In **Clients found**, click the 1.60 client (`_classic_beta_`), then the **Updates** tab.
2. Click **Scan for updates**. The first scan compares both sides, which takes a minute or two: every file of your game
   against the client (the changed textures pixel by pixel), every tile of every map chunk by chunk, every data table.
   The result is kept in the app's folder and used again until the client or your game changes; **Scan again** scans
   anyway, **Stop** stops.
3. The list shows each update with its **Kind**, its **Class**, the number of files (tiles for terrain, rows for a
   table), its size in the 1.60 client and whether it can be imported now. Narrow it with **Kind**, **Class**,
   **Zone or folder** (for example `Elwynn` or `Creature`) and **Search**. **Hide differences without a visible change**
   (ticked at first) leaves out the files that were only re-encoded or re-saved; **Only what can be imported** shows
   what the app can bring over today.
4. Click an update: **About this update** shows what it changes in your game, what it needs, why it has its class, what
   was measured and its files.
5. Pick one or several updates (<kbd>Ctrl</kbd> or <kbd>Shift</kbd> and click) and click **Preview import**. The app
   converts everything in memory and shows the **Import report**; nothing is written. Then click
   **Import picked updates**: all picks go into your workspace as *one* change set (**Revert this import** in
   [My changes](my-changes.md) takes it all back), after the 1.12 checks. Picks that cannot be imported yet are listed
   in the report with why; the others are imported.

![The Updates tab: the 1.60 catalog narrowed to Elwynn Forest, its terrain picked and previewed](../screenshots/143_other_versions_updates.png)

The classes:

| Class | Meaning |
|---|---|
| **Ready** | 1.12 loads it as it is, or after a conversion the app already does. Most of these can be imported now. |
| **Needs converter** | It needs a converter that does not exist yet: modern models (M2) and buildings (WMO), OGG sounds, the light colours, data tables (DB2 to DBC). |
| **Needs server** | It only matters with server data: hotfixed rows, spells and items, encrypted content. |
| **Needs client code** | A feature of the modern engine or interface (volumetric fog, ambient occlusion, the modern interface API, character customization...): it needs work on the 1.12 client itself. Listed so you know; it cannot be imported. |
| **No visible change** | The files differ, but nothing changes on screen (a texture only re-encoded, a model re-saved in the modern format, a sound stored as OGG like Classic Era's). Nothing to bring. |

The kinds:

* **Zone terrain**: one update per zone (a zone and its subzones), per dungeon or battleground, and one for the open sea:
  how many ground chunks change (heights, texture layers, water, zone IDs), the trees, rocks and buildings that are new,
  gone, replaced or moved, the new tiles. The import is the map conversion of [Converting a map to 1.12](#converting-a-map-to-1.12):
  all picked zones of one map are converted together.
* **New map**: a map 1.12 does not have (Zephras Isle, Hyjal Crater...), converted as a whole new map.
* **Sky and lighting**: per zone, the light zones 1.60 added, removed or resized and its polygon-shaped zone lights; the
  modern lighting tables (light colours per time of day, volumetric fog, sun shafts, ambient occlusion, colour grading).
  The colours themselves were not compared yet (their table's columns are not decoded).
* **Skyboxes**: the sky models 1.60 has and your LightSkybox table does not, and the sky textures of your game that
  changed.
* **Weather**: the modern weather types and particle volumes.
* **Ground textures** and **Textures**: per folder, the textures that were repainted or lightly edited, the ones 1.60
  made larger (the same picture with more detail), their high-resolution versions (usually four times as wide), and the
  ones only re-encoded (*No visible change*). 1.12 loads at most 512 x 512, so larger pictures are made 512 x 512: still
  sharper where yours are smaller. Character skins, faces, hair and armour pieces are put together at a fixed size by
  1.12, so larger ones need client code.
* **Models** and **Buildings (WMO)**: the high-resolution player models, the new models and buildings placed in the 1.60
  world, the landmarks 1.60 replaced (Stormwind, Ironforge, Orgrimmar...), and your models re-saved in the modern format.
* **Interface art**: the 1.60 interface textures your game does not have, by folder. They show only where your
  interface code or tables name them.
* **Interface strings**: the interface texts 1.60 changed, added or removed (GlobalStrings).
* **Fonts**: the fonts that changed, and the new ones (Cyrillic, Chinese, Korean).
* **Sounds and music**: changed sounds (OGG, not playable by 1.12 without a converter), changed MP3 music, and your WAV
  sounds stored as OGG.
* **Data tables**: per table of your game, the rows 1.60 added and changed (counted row by row where both sides' columns
  are named, by number of rows otherwise), the tables only 1.60 has (by system), and the rows Blizzard's server hotfixed
  (from the 1.60 client's cache).
* **Engine and UI features**: the modern renderer and its effects, the modern interface API, the sound engine, found in
  the 1.60 program file (and not in yours).
* **Other files**: movies, the 1.60 files without a name, encrypted files.

What an import writes:

* *Textures and interface art*: a file 1.12 loads (palette or DXT1, DXT3, DXT5, a power of two up to 512 x 512, all its
  mip levels) is saved as it is. A larger one with mip levels keeps its own smaller pictures (nothing is encoded again);
  any other is made a power of two of at most 512 x 512 and encoded again in its format. The checks of every saved
  texture run before anything is written.
* *Fonts* and *MP3 music*: as they are.
* *Zone terrain* and *New map*: the map conversion (the same report as **Convert map...**).
* *Names*: as for the map conversion, only your own 1.12 game's file names (found by their hashes in a Classic Era copy
  next to the 1.60 client) and the listfile you chose; the 1.60 interface files are also named by the client's own list
  of them (ManifestInterfaceData), each name checked against the client's name hash. With a listfile (**Choose
  listfile...**) the catalog also lists the 1.60 files without another name by folder.

## Not yet

* Most columns of the modern clients' data tables have no names yet.
* Maps convert from 1.60 to 1.12 only: 1.12 to 1.60, and maps of 2.x and 3.x, are planned. The models and buildings
  1.12 does not have are left out (their conversion is planned), see [Known issues](known-issues.md#other-versions).
* 3D views of TBC and WotLK models, and bringing models of other versions into your 1.12 game, are planned, not there
  yet. Textures, interface art, fonts and MP3 music of the 1.60 client can be imported through the update catalog.
* The update catalog compares the 1.60 client only. Its models, buildings, OGG sounds, light colours, data tables and
  interface texts cannot be imported yet (each update says why), see [Known issues](known-issues.md#other-versions).
* Many tables of other versions show raw columns ([Known issues](known-issues.md#other-versions)).

## Command line

`wds versions find`, `info`, `files`, `extract`, `tables`, `rows`, `compare`, `convert-map`, `updates`, `update-info` and
`import-updates` do everything this tab does, and `wds settings set other-clients <folder>` sets the folder; see
[The command line](command-line.md#other-versions-experimental).
