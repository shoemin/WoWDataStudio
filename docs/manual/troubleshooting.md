# Troubleshooting and questions

## The game doesn't show my change

Go through this list from the top:

1. *Did you keep the change?* The texture must have a **Modified** badge and be listed in **My changes**. If you clicked
   **Cancel** in the check window, nothing was saved: import again.
2. *Is it in the game?* A kept change is only in your workspace. Its status in **My changes** must say
   **installed as loose file** or **in patch-3.MPQ**. If it says *older version*, install or build again.
3. *Did you restart the game?* The game reads textures when it starts. Close it completely and start it again, or,
   with hot reload, click **Reload in running game** in **My changes** and read what the result says: some changes still
   need a relog or a restart ([Testing in the game](testing-in-game.md#reload-in-the-running-game-hot-reload)).
4. *Loose files: does your client have the loose-file mode?* A normal 1.12.1 client ignores loose files. The project's
   client has the mode from update wave185 on. Use **4. Build release patch (patch-3.MPQ)...** instead; it works with
   every client. See [Testing changes in the game](testing-in-game.md#what-loose-files-need-the-loose-file-mode).
5. *Patch: is it in the right game folder?* The app installs into the folder chosen in [Settings](settings.md). If you
   play from another copy of the game, copy `patch-3.MPQ` into that copy's `Data` folder.
6. *Patch: do you have loose files installed too?* With the loose-file mode on, a loose file wins over the patch. Use
   **Remove all from game** in **My changes** and restart.
7. *Are you looking at the right thing?* A creature can have several displays (skins). The details panel shows which
   skin a creature uses. Make sure you edited that one. **Used by** lists everything that uses a texture.
8. *Is the change visible at that distance?* Far away, the game shows smaller copies of the texture (mip levels). The app
   rebuilds them from your edit; check them with **Mip level** in the compare window.
9. *A ground texture?* With terrain specular on (`SET specular "1"` and `SET pixelShaders "1"` in the game's
   `WTF\Config.wtf`), the game draws the texture's `_s` file instead of the one the map names, and never reads the other.
   Edit both files (**Edit both (recommended)** when you import); the details panel says which file your game draws. See
   [Ground textures and specular files](editing-textures.md#ground-textures-and-specular-files).
10. *A glow shows as a coloured square?* The texture is drawn additively: its black background must stay black. Import
   again with **Keep black areas black (recommended)**. See [Glowing (additive) textures](editing-textures.md#glowing-additive-textures).
11. *The compare said only a small share of pixels changed, but in the game everything changed?* Since 0.6.1 the app
   counts colour and alpha separately and never hides colour changes under transparent pixels (in 0.6.0 it did, so a
   ground `_s` texture or a building texture whose alpha is not transparency looked mostly unchanged).

## Known issues

Bugs and limitations that are not fixed yet, who they affect and what to do instead: [Known issues](known-issues.md).

## Messages and what they mean

| Message | Meaning | What to do |
|---|---|---|
| *This folder does not contain WoW.exe.* (and the other welcome-screen messages) | The selected folder is not a 1.12.1 game folder. | See [Getting started](getting-started.md#first-start-choose-your-world-of-warcraft-folder). |
| *The game data could not be read: ...* | An archive could not be opened, or the index could not be written. | Close the game if it is updating, check the disk is not full, then try again. Then **File > Rebuild the index**. |
| **Server database not connected** | The names could not be loaded. | Start the database, check [Settings](settings.md), click **Test connection**. Without it everything still works, with ids instead of names. |
| *Could not export the texture* | The PNG could not be written. | Pick another folder (for example inside `Documents`), or close the PNG in your image editor. |
| *Could not import the PNG* | The PNG could not be read, or the result would not load in the game. | Save the PNG again from your editor as a normal 8-bit RGBA PNG; keep the size a power of two (at most 512). |
| *Could not install into the game folder* | The game folder is write-protected. | If the game is under `C:\Program Files`, Windows protects it: move the game to another folder (for example `C:\Games`), or use the release patch. |
| *... already exists and was not installed by WoW Data Studio* | Another file is in the way. | See [Testing changes in the game](testing-in-game.md#install-one-change). |
| *Could not build the patch* | See the detail line. | Most often the game is running: close it. See [Releasing](releasing.md#if-the-build-fails). |
| *Could not revert* | A file is in use. | Close the game and other programs that use the file, then try again. |
| *3D view not available: OpenGL 3.3 could not be started* | The graphics driver cannot do 3D (too old, or a remote-desktop session). | Update the graphics driver. Everything except the 3D view keeps working. |
| *Could not show this model: ...* | The model could not be read. | Nothing to do; the rest of the app works. |
| *Also shown in a pop-out window that follows the selection...* (over the docked 3D view) | A pop-out 3D window follows the selection, so the docked view stopped animating to spare the graphics card. | Nothing to do. **Bring it to front** shows the window; close it, or switch off its **Follow selection**, and the docked view animates again. |
| **Something went wrong** | An unexpected error. Your files are safe. | Keep working or restart the app. The details are written to `%APPDATA%\WoWDataStudio\error.log`; send that file when you report the problem. |
| *The user manual was not found* | The `manual` folder of the installation is missing. | Run the installer again. |
| *Noggit was not found.* | The app cannot find `noggit.exe`. | Set **Noggit program (noggit.exe)** in [Settings](settings.md#maps-and-terrain) to our fixed Noggit's `noggit.exe`. |
| *Copy the game's archives for Noggit?* | The app's data folder is on another drive than the game, so it cannot link the archives. | Let it copy them (about 5 GB), or choose a copy of the game folder as **Client copy for Noggit (optional)** in Settings. |
| *Noggit must not use the game folder itself* | **Client copy for Noggit (optional)** points at your game folder. | Clear the field (the app makes its own client view) or pick a *copy* of the game folder. |
| *Cannot be imported* (map import) | The 1.12 check found a problem the game would trip over; the row says what, in words. | Fix it in Noggit (for example convert the alpha to 4-bit, use only 4 texture layers per chunk), save, and import again. |
| *No changes found* (map import) | Every file Noggit saved has the same content as what the app has. | Did you save in Noggit (<kbd>Ctrl</kbd>+<kbd>S</kbd>)? Is it the same map? The window names Noggit's save folder. |
| *Refused: the world server is running* | The server update does not install while mangosd runs. | Stop the world server (close its console window), click **Check again**, then **Install into the server**. |
| *... not in the installed patch-3.MPQ* (server update) | The server tools read the game's archives, not loose files. | Build the release patch with *Install into the game* ticked, then **Check again**. |
| *... cannot be proven to be built by WoW Data Studio. It was left untouched.* | The `patch-3.MPQ` in the game was not made by the app (or by version 0.6.0 or older and changed since). | Use **Uninstall release patch (patch-3.MPQ)** and confirm **Move it out of the game**: it is moved to the app's `backups` folder, not deleted. Or leave it. |
| *A ground texture has two files* | The texture is painted on the ground and has a `_s` variant that the game draws when terrain specular is on. | **Edit both (recommended)**. |
| *There is no SpellIcon with ID ...* (and other red messages in the **Data tables** tab) | A value you typed is not allowed: a link to a row that does not exist, a number that does not fit the column, an ID that is used already. | Type another value. The message says what is allowed; 0 means "none" for a link. See [Data tables](dbc-editor.md#edit-values). |
| *Could not save the table* | The table would not load in the game, or the workspace could not be written. | Read the detail; undo the last edits (<kbd>Ctrl</kbd>+<kbd>Z</kbd>) and save again. |
| *The world server is running ... Stop it first* (**Copy tables to the server**) | The server reads its tables when it starts; they must not change under it. | Stop the world server, then copy again. |
| *... is not copied to the server* | The table is one the server takes from its database, or does not use. | Edit the server's database table instead (the window names it). |
| *The extraction stopped with an error* | One of the server tools failed. | Open **Log**; the server tools need Python and their built extractors (see **Server tools README**). The server's data was not touched. |

## A table change does not show in the game

1. *Did you save it?* The **Data tables** tab must not say **Unsaved changes**; the table must be listed in
   **My changes**. Click **Save to my workspace**.
2. *Is it in the game?* As for textures: **Test in game** (loose files) or **Build release patch...**, then restart
   the game completely (it reads its tables when it starts).
3. *Did the right row change?* Many things have several rows: a spell has a row per rank (Fireball has twelve), an item
   look can be shared by many items. Use **Search all text...** or a column filter to find all of them, and
   **Bulk edit...** to change them together.
4. *Does the server decide?* For a table that says *Server uses its database instead (edit there)* (Spell, Map,
   AreaTable, Faction, FactionTemplate, TaxiNodes, SkillLineAbility, SoundEntries...), the server does not read your
   file: names, icons and tooltips change in the game, but what the spell does, a faction's reaction or a flight node
   on the server come from the server's database. For a table that says *Server also needs this file*, use
   **Copy to server...** and restart the world server.
5. *Items and creatures?* Their names, stats, loot and which look they use are in the server's database
   (`item_template`, `creature_template`), not in the game's tables. The tables only hold their looks (ItemDisplayInfo,
   CreatureDisplayInfo).
6. *The Browse tab still shows the old value?* The **Browse** list and **Used in game** are built from the game's own
   files, not from your workspace (see [Known issues](known-issues.md)). The **Data tables** tab shows your version.

## A designed spell, talent or class does not work in the game

1. *Did you save it?* The designers keep your edits until you click **Save to my workspace**; then they are in
   **My changes**.
2. *Both sides?* The game shows a spell from `Spell.dbc` (test it or build the release patch); the server casts it from
   `spell_template` (**Apply to server...** and restart the world server). The Spell designer's line under the name says
   whether the server database was connected when you saved; if not, **Sync with the server** (or `wds spells sync`)
   and apply.
3. *Helper tables on the server?* A new cast time, duration, range, radius, visual, talent or talent tree is read by the
   server from its own copy of the table: **Tools > Copy tables to the server...**.
4. *The checks warn about a script?* *Dummy*, *Script effect* and the *Dummy* aura do nothing on the server without
   code for that spell in the server source.
5. *A trainer does not list it?* The trainer needs the Trainer flag, the spell a *learn* spell (the designer makes one),
   and the player's class a skill line with the spell (**Add to skill line**). Clear the game's cache (WDB).
6. *A new class?* It needs code changes in the game and the server first: see
   [What a new class still needs](class-and-talent-designer.md#what-a-new-class-still-needs).

## A designed quest does not show or cannot be done in the game

1. *Saved and applied?* The Quest chains tab keeps your edits until **Save to my workspace**; then **Apply to
   server...** writes them. The world server reads quests when it starts: restart it, or `.reload all_quest` (and
   `.reload creature_questrelation`, `.reload creature_involvedrelation`) as a GM.
2. *Old texts?* The game caches quests (`WDB\questcache.wdb`): close it and use **Clear the game's cache (WDB)...**.
3. *The NPC shows no "!"* Look at **Checks of this quest**: the player's race, class or level may not fit (the
   **Minimum level**, **Races**, **Classes**), a quest it needs may not be done yet (**Must be done first**), the NPC may
   lack the *Quest giver* flag or be hostile to the player's faction, or the quest may be closed by a choice already made
   (**Exclusive group**). `.quest add <ID>` as a GM adds it anyway, to try the rest.
4. *An objective never completes?* An item needs a drop source (**Drop sources**); a quest drop drops only while the
   quest is in the log. A "speak to" needs the NPC's talk option (**Speak to an NPC**) and the quest must not have the
   *Explore or event* flag. An explore objective needs its area trigger in the game's `AreaTrigger.dbc` too: test the
   game's tables (**Test all in game (install as loose files)**) or build the release patch.
5. *The reward spell is not learned?* The quest must cast the spell's learn spell (**Reward spell** with **The player
   learns it (else: it is cast on the player)** ticked), and the spell must be in the game's `Spell.dbc` (test it or
   build the release patch) and in `spell_template` (applied).
6. *The quest log shows a wrong header?* A new **Quest log category** is in the game's `QuestSort.dbc`: test it or build
   the release patch, and copy it to the server (**Tools > Copy tables to the server...**).

## A server edit does not show in the game

1. *Did you save and apply it?* The **Server data** tab must not say **Unsaved changes**, and **My changes** must say
   *applied to the server* for the table (else **Apply to server...**).
2. *Did the server read it?* The world server reads its tables when it starts: restart it, or use the `.reload`
   command the apply named (spells, areas, factions and flight points need a restart). Creatures and objects already in
   the world keep their old values until they respawn.
3. *The game remembers the old one:* names, stats, quest texts and pages are cached by the game. Close it and use
   **Clear the game's cache (WDB)...**.
4. *Did the right row change?* VMaNGOS has several versions of many rows (one per content patch). The tab opens the one
   your server uses (*the server uses this row*); if your server runs another patch, set **Content patch of the server**
   in Settings.
5. *The game shows the item without a model or icon:* its **display_id** is not in the game's ItemDisplayInfo (for a
   creature: CreatureDisplayInfo). **Check client and server...** lists these.

| Message | What it means | What to do |
|---|---|---|
| *Could not connect to the server's database ...* | The database is not running, or the connection settings are wrong. | Start the server's MySQL (in the repack: *Start MySQL.bat*), check **File > Settings...**, click **Connect**. |
| *... is not a world database* | **World database** names another database. | Set it to the world database (usually `mangos`). |
| *Refused: N row(s) changed on the server since you started editing them* | Somebody (a script, another tool, the server) changed those rows after your first edit. | Look at them first; tick **Overwrite them with my edits** to apply anyway. |
| *Not saved: ... have a problem the server would refuse* | A row rule (levels, loot chances, counts...) fails. | Fix the value the message names, then save again. |

## My map change does not show, or the server does not match

* *Not in the game:* as for textures (list above): the tile must be **Modified** in the **Maps** tab and installed (loose
  file or `patch-3.MPQ`); restart the game. A new tile only loads if the map's tile list (the WDT) has it: import the
  WDT Noggit saved too.
* *Creatures float over a new valley or walk inside a new hill, or walk through new walls:* the world server still has the
  old map data. Use **Update server maps...** and restart the world server
  ([Maps and terrain](maps-and-terrain.md#update-server-maps)).
* *Creatures take strange paths near your edit:* rebuild pathing (mmaps) for the changed tiles; where rebuilt tiles meet
  old ones the paths can join a little less well (see the note about pathing in that chapter).
* *Noggit still shows my change after I reverted it:* close and reopen the project in Noggit; if you edited the tile in
  Noggit again after importing, that newer file stays in the project until you import or delete it.

## Terrain from Blender does not come back as expected

* *"No changes found"* although you edited in Blender: export again from Blender (*Export terrain for WoW Data Studio* in
  the *WoW Terrain* tab) into the package's `blender_export` folder, then pick the package folder in **Import edited
  terrain...**. Moving points only sideways, or moving a whole tile object, changes no height: only heights come back.
* *"vertices moved sideways ... were put back on the grid"*, *"added in Blender were ignored"*: the game's ground is a
  fixed grid; use sculpting or moving along Z only ([Terrain in Blender](terrain-in-blender.md#what-comes-back-and-what-does-not)).
* *"Edges do not match their neighbours"*: run *Sync shared edges* in Blender and export again, or let the import sync
  them (**Sync the edges (copy from the edited side)**). A neighbour you did not edit is then saved too.
* *"changed in the app since this package was exported"*: the tile was imported or reverted after you opened it in
  Blender. Open the terrain in Blender again and redo the edit on the new version.
* *A tile is grey in Material Preview*, or *pink*: its ground texture was not found, or the graphics card has too few
  texture slots; switch to *Tile colours*. The import is not affected.
* *Blender does not have the WoW Terrain tab*: the installed add-on is older than 0.4.0. **Open terrain in Blender...**
  loads the app's own copy; to install it permanently use **Tools > Install the Blender add-on...**.

## Animations or the skeleton do not come back as expected

* *No row "Skeleton and animations"* in the review: the package was exported without **Skeleton and animations (for
  editing)**. Export again with it ticked, redo the animation edit there (only the mesh and the textures come back from
  a package made without it).
* *"... is not named after a game animation, so the game would not know when to play it"*: rename the Blender action
  to a name the WoW panel lists (*Stand [3]*, *EmoteDance*...). Names are those of the game's AnimationData table.
* *"... is key bone Head: the game finds it by that name"*: you deleted a key bone. Keep it, or set that key bone on
  another bone first (*Bone properties > WoW bone*).
* *"... was removed from a character model"*: the game plays that animation for players (walking, fighting, emotes...).
  Edit it instead of deleting it.
* *An animation I did not touch is listed as changed*: a constraint, a driver or a change to the rest pose moved it.
  Check it with **Compare**; untick the row if you did not mean it, or undo the change in Blender.
* *Event markers did not move*: markers are read from the add-on's `wowdata_rig.json`. Export from Blender with the
  add-on 0.5.0 or newer (*File > Export > WoW 1.12 model for WoW Data Studio*), not with Blender's own glTF exporter.
* *The new animation never plays in the game*: a variation is picked by its chance (*Chance (%)* in the WoW panel); an
  emote plays only through its chat command; check **Used in game** for what the game plays it for.
* *A weapon sits in the wrong place*: check the attachment point with **Attachment points** in the 3D viewer; a weapon
  follows `ATT_01_HandRight` (`ATT_02_HandLeft` for the off hand).

## The command line

* *"wds" is not recognized*: open *WoW Data Studio command prompt* from the Start menu, or install again with
  *Add the command line tool to PATH* ticked (new command prompts only see it), or run `bin\wds.exe` of the installation.
* *Exit code 5 and "Run the command again with --yes"*: the command would change the game folder or the server, or
  delete something; it printed what it would do. Add `--yes` when that is what you want.
* *Exit code 4*: refused for safety (the world server runs, or a file in the game folder is not the app's). The message
  says which; see [The command line](command-line.md#safety).
* Every command, its options and the exit codes: [The command line](command-line.md).

## Resetting the index

The index is the app's own list of everything in the game. It is rebuilt automatically when the game's archives
change. To force it:

* **File > Rebuild the index** (a few seconds; your changes are kept);
* or, if the app cannot even start: close it and delete `index.sqlite` from the game folder's data folder (see below).
  The next start rebuilds it.

If names are missing or old, **File > Reload names from the server database** is enough.

## Where the app keeps its files

| What | Where |
|---|---|
| The program | the installation folder, by default `%LOCALAPPDATA%\Programs\WoW Data Studio` |
| Settings (`settings.json`, with the server password encrypted) | `%APPDATA%\WoWDataStudio\` |
| The error log (`error.log`) | `%APPDATA%\WoWDataStudio\` |
| Everything of one game folder | `%APPDATA%\WoWDataStudio\<folder name>-<code>\`, for example `Client-1a2b3c4d\` |
| ... the index | `index.sqlite` in that folder |
| ... your edited files (the workspace) | `workspace\` in that folder, with the same paths as in the game. **File > Open my workspace folder** opens it. |
| ... the list of your changes | `changes.json` in that folder |
| ... backups of files the app replaced in the game folder | `backups\` in that folder |
| ... the built `patch-3.MPQ` | `build\` in that folder |
| Your exported PNG files | where you saved them; by default `Documents\WoW Data Studio\Exports\` |
| Saved 3D pictures | by default `Documents\WoW Data Studio\Exports\Images\` |
| Noggit's client view and projects, the minimap cache | `noggit\` and `cache\minimap\` in the game folder's data folder (see [Maps and terrain](maps-and-terrain.md#where-the-files-are)) |

To open `%APPDATA%`, type `%APPDATA%` into the address bar of Windows Explorer.

## Questions

### Does the app change my game?

Only when you click an install button ([Testing changes in the game](testing-in-game.md), [Releasing](releasing.md)),
and never the game's own archives. See [Safety and undoing everything](safety.md).

### Does it need the internet?

No. Everything works offline. (The server database is usually on your own computer.)

### I moved my game folder and my changes are gone.

Each game folder has its own data folder, named after the folder and its location. Your changes are still in the old
one under `%APPDATA%\WoWDataStudio\`. Move the game back, or select the old location again, to get them back. To carry
them over, export them as a release patch first.

### Can I use it with another version of World of Warcraft?

No, only 1.12.1 (build 5875). The file formats of other versions differ.

### Windows SmartScreen or my antivirus warns about the installer.

The installer is not code-signed (that needs a paid certificate). See
[Getting started](getting-started.md#install) for how to continue. Only run an installer you got from the project.

### What happens to my changes when I uninstall?

They stay in `%APPDATA%\WoWDataStudio`, so a reinstall finds them again. To remove them for good, delete that folder
after uninstalling (first remove anything you installed into the game; see [Safety](safety.md)).

### Can I edit models, not just textures?

Exporting a model to Blender and importing it back is described in [Models and Blender](model-export-import.md): the
mesh and the textures, and with [Skeleton and animations](skeleton-and-animation.md) the bones, the animations, the
attachment points and the events.

### A pop-out 3D window opens in an odd place, or I cannot find it.

The app reopens a 3D window where it was last time. If that monitor is no longer connected, the window opens on the main
monitor; it is always made to fit on the screen. A window in full screen has no frame: press <kbd>Esc</kbd> (or
<kbd>F11</kbd>). If the tool bar and side panel are gone, press <kbd>Ctrl</kbd>+<kbd>H</kbd> or click
**Show controls (Ctrl+H)**. The places are kept in `settings.json` (see [Where the app keeps its files](#where-the-app-keeps-its-files)).

### Can I edit the maps?

Yes, with Noggit: see [Maps and terrain](maps-and-terrain.md). The ground heights can also be edited in Blender, with
the neighbouring tiles: see [Terrain in Blender](terrain-in-blender.md).
