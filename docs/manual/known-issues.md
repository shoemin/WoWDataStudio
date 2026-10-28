# Known issues

Bugs and limitations of the current version that are not fixed yet: what happens, who is affected, what to do
instead, and the status. When one is fixed, it is removed from this list and the fix is recorded in the
release notes (`CHANGELOG.md` next to the app's source; the installer's version history).

Something else does not work? See [Troubleshooting and questions](troubleshooting.md), and report it with the app's
`%APPDATA%\WoWDataStudio\error.log` if there is one.

## Hot reload

### A changed creature model is reported as "needs a relog" although it already changed

*What happens:* after **Reload in running game** with a changed creature model (`.m2`), the result lists the model under
*Needs a relog*, with *... creature(s) or character(s) ... still show the old one*. In the game test of 0.13.1 the
creatures in view already showed the new model (the game re-created all of them), so the message is too careful. The
report comes from the game side (`wowcode.dll`), not from the app.

*Who is affected:* everyone who reloads a creature or character model.

*Workaround:* look at the creature: if it shows the change, nothing else is needed; otherwise relog.

*Status:* open (game side: `wowcode.dll`'s report probably counts the creatures before it re-creates them in the same reload; its log then says *re-created 11 of 11 unit model(s)*).

### Some changes still need a relog or a restart

*What happens:* hot reload replaces what it can in place and says per file what it could not: a texture whose size or
pixel format changed, the textures of character skins and armor (combined into a character's look), the map's tile list
and far view (`.wdt`, `.wdl`), a building that a ship or zeppelin uses, flight paths and the world map (relog); the few
data tables the game prepares once at start-up, and tables with rows added or removed (restart). The character screen,
portraits and spell effects that are already playing show a changed model only when they are shown again.

*Who is affected:* everyone who uses hot reload.

*Workaround:* do what the result says: relog, or restart the game. A relog reads everything fresh.

*Status:* open (by design for now: these are rebuilt by the game only at those moments).

### Each reload of a data table keeps a copy of it in memory

*What happens:* a reloaded table's new rows are copied over the old ones, but the new texts (and the old ones) stay in the
game's memory until the game ends, because something in the game may still point to them. A table like `Spell.dbc`
adds about 20 MB per reload.

*Who is affected:* anyone who reloads a large table many times in one game session.

*Workaround:* restart the game now and then during long editing sessions.

*Status:* open (by design: freeing them could crash the game).

## Maps and the server

### A map edit changes only what the game draws, until the server is updated

*What happens:* after you raise or lower the ground (in Noggit or Blender) and install the change, the game *draws* the
new ground, but the world server still uses its own copy of the map heights. Players and creatures walk at the old
height: through a new hill, or floating over a new valley; creatures also use the old paths and line of sight.

*Who is affected:* everyone who edits terrain heights, holes or water and plays on a server.

*Workaround:* update the server's maps with **Update server maps...** in the **Maps** tab, then restart the world
server (see [Maps and terrain](maps-and-terrain.md#update-server-maps)). Players of your server also need your
`patch-3.MPQ`.

*Status:* open: by design, the game and the server have separate map data; the app guides the server update.

### The server update needs the server tools and was only tested against test copies

*What happens:* **Update server maps...** runs the project's server tools (`reextract.ps1`, `swap_in.ps1`), which
need Python and the built extractors; they are not part of the installer. The app's own tests run the update against
fake server data folders, never against a live server's data.

*Who is affected:* anyone who updates a server's maps.

*Workaround:* install the server tools as described in their README (the window links to it); keep the backups the
update makes (**Undo server update** restores them).

*Status:* open.

### CMaNGOS server databases are not tested

*What happens:* the server database reader detects the VMaNGOS and the CMaNGOS table layouts, but only VMaNGOS has
been tested with a real database. With a CMaNGOS database, names, spawns, loot or quests in **Used in game** may be
missing or wrong. The **Server data** tab describes the VMaNGOS tables only: with another layout, its friendly views
find little and the tables are shown as stored (**All tables**), without the VMaNGOS checks.

*Who is affected:* users whose server is CMaNGOS.

*Workaround:* none needed for editing textures and tables (names are only shown); without names the app uses file and
model names. Edit a CMaNGOS database in the **All tables** view, with care.

*Status:* open (untested).

## Ground textures

### Some ground textures have no `_s` file

*What happens:* with terrain specular on, the game draws the `_s` file of each ground texture. 17 ground textures that
the maps use have none, for example `Tileset\AlteracMtns\AlteracGrassBase.blp`, `Tileset\UnGoro\UnGoroDirt.blp`,
`UnGoroRock.blp` and the Emerald Dream textures. What the game draws there with terrain specular on has not been
checked in the game. The app's 3D previews show the base file for them (the preview's status line counts them), and
the details panel says the `_s` file is missing.

*Who is affected:* edits of those 17 textures, seen with terrain specular on.

*Workaround:* edit the base file; to see it in the game, turn terrain specular off (`SET specular "0"` in the game's
`WTF\Config.wtf`). The app cannot add a new `_s` file (new textures are not supported yet).

*Status:* open.

### Some image editors erase the colour under transparent pixels of a `_s` file

*What happens:* in a `_s` ground texture the alpha channel is the shine, not transparency, and most of it is near 0.
Some image editors throw away the colour of pixels whose alpha is 0 when they save a PNG. The app keeps the shine map,
but the colours of those pixels come from your PNG: the ground can turn dark in patches.

*Who is affected:* anyone who edits a `_s` file directly.

*Workaround:* edit the base file (`DurotarDirt.blp`, no alpha) and import it with **Edit both (recommended)**. The
`_s` file then gets your colours and keeps its shine map. The compare window shows the colour change of every pixel.

*Status:* open (an image-editor behaviour; the manual recommends the workaround).

## Textures and models

### Particle blend modes are read by the community's convention

*What happens:* the additive-texture note (*This texture is drawn additively...*) uses each model's blend modes. For
model materials, ribbons and buildings they are read as the game reads them; for particle emitters the numbering
(0 opaque, 1 add, 2 alpha, 3 alpha key, 4 add with alpha) is the modding community's, not yet checked in the game's
code. A particle texture could get the note when it should not, or miss it.

*Who is affected:* edits of spell and particle textures.

*Workaround:* none needed; the note is advice. Keep black backgrounds black in glow textures in any case.

*Status:* open.

### New textures, new skins and new geosets cannot be added

*What happens:* an import always replaces an existing game texture; models keep their list of textures and geosets
(bones can be added since 0.10.0, see [Skeleton and animations](skeleton-and-animation.md)). A picture that is not one of the model's textures is refused in the model import.

*Who is affected:* anyone who wants a new skin or a new texture file.

*Workaround:* paint over an existing texture (every user of that texture changes).

*Status:* open (planned: new skins as new display ids, see `ROADMAP.md`).

### The 3D views are previews, not the game's renderer

*What happens:* the model viewer does not draw particles, ribbons, lights or worn equipment (capes are grey); the
terrain preview has no sky, zone lighting, animation or ground shine; buildings in the Browse list have no 3D view.

*Who is affected:* everyone; it does not change what is saved.

*Workaround:* check the result in the game ([Testing changes in the game](testing-in-game.md)).

*Status:* open.

### Only heights come back from Blender; buildings cannot be imported

*What happens:* the terrain import takes the ground heights only (texture painting, holes, water and moving doodads
or buildings are not imported); a building opened in Blender is a shape to look at.

*Who is affected:* terrain and building edits in Blender.

*Workaround:* paint textures, holes and water, and move objects, in Noggit ([Maps and terrain](maps-and-terrain.md#edit-in-noggit)).

*Status:* open.

## Skeleton and animations

### The skeleton and animation import is checked against the client's code, not in the running game

*What happens:* the app writes animations, lookups and the playable-animation table the way the 1.12 client reads them
(from the client's own code), and its tests check every model of the game: unchanged models come back byte for byte,
edited ones are read back and validated. The app's tests never start the game, so how an edited skeleton or a new
animation *looks* in the game is not tested automatically.

*Who is affected:* every skeleton and animation edit, most of all character models (every player of that race and sex).

*Workaround:* test the change in the game before you release it (**3. Test in game (install as loose files)**): play the
animation, a few emotes, weapons, a mount, spells.

*Status:* open.

### Blender's curves come back as linear keys, sampled per frame

*What happens:* the 1.12 client plays bone keys linearly. The add-on samples every edited animation at its keys and,
where the curves are not linear, at every frame of Blender's frame rate; the import keeps the keys that matter. An
ease-in that happens between two frames is approximated. Constraints, drivers and IK are baked the same way. A marker you move in Blender
snaps to a frame (unmoved markers keep their exact times).

*Who is affected:* very fast motions (a whip, a blink) and precise event timing in edited animations.

*Workaround:* raise the scene's frame rate in Blender (*Output properties > Frame rate*) before you export, or key the
fast moment on whole frames.

*Status:* open (by design of the 1.12 format).

### A removed animation leaves its keys in the file

*What happens:* when you delete an animation (or edit one, which gets a new place at the end of the model's timeline),
its old keys stay in the model's tracks, unused. The game never plays them; the file is a little bigger.

*Who is affected:* nobody in the game; the size of edited models.

*Workaround:* none needed.

*Status:* open.

### Particles and ribbons: numbers only, no new emitters

*What happens:* particle emitters and ribbons are not shown in Blender. Their numbers can be edited in
`wowdata_effects.json`, but emitters and ribbons cannot be added or removed, and a new animation copies their values
from the animation it was made from.

*Who is affected:* spell effects and creatures with particles (fire, smoke, glows).

*Workaround:* edit the numbers in the JSON file ([Particles and ribbons](skeleton-and-animation.md#particles-and-ribbons-advanced)).

*Status:* open.

### Portrait camera splines of a retimed animation are stretched, not refitted

*What happens:* some models carry a camera (the character portrait) with curved (spline) keys. When you retime or
re-create an animation, those keys are moved and stretched with it, but the curve itself is not refitted.

*Who is affected:* edited animations of models with a portrait camera (characters, some creatures); only the unit
frame portrait.

*Workaround:* none needed in most cases; check the portrait in the game.

*Status:* open.

### A few game models get the client's own fallback table after an animation edit

*What happens:* each model stores which animation to play for an animation it lacks (the playable-animation table).
For 9,647 of the game's 9,691 models it is exactly what the client computes from AnimationData; 44 have small
differences from the original tools (for example *Stand* stored for animation 147). When you add or remove an
animation the app recomputes the table the client's way, so those 44 models lose their oddity.

*Who is affected:* animation edits of those 44 models (the review does not list them separately).

*Workaround:* none needed: the recomputed table is what the client itself would use.

*Status:* open.

### Edits from other 3D programs: no event markers or animation properties

*What happens:* a glTF exported by Blender's own exporter or by another program (not the add-on) has no
`wowdata_rig.json`: the bones, animations, attachment points and event empties still come back, but marker edits and
the animation properties of the WoW panel (*Loops*, *Chance*, *Blend time*, *Move speed*) do not.

*Who is affected:* edits made without the WoW add-on 0.5.0 or newer.

*Workaround:* export with *File > Export > WoW 1.12 model for WoW Data Studio* (install the add-on with
**Tools > Install the Blender add-on...**).

*Status:* open.

## Delivering changes

### Loose files need the loose-file mode in the game

*What happens:* **3. Test in game (install as loose files)** copies files into the game folder, but a normal 1.12.1
game ignores loose files; only the project's own client (update wave185 on) reads them.

*Who is affected:* everyone with a normal 1.12.1 game.

*Workaround:* use **4. Build release patch (patch-3.MPQ)...**; it works with every 1.12.1 game.

*Status:* open (by design of the game).

### Only one `patch-3.MPQ`

*What happens:* the game loads one archive of that name. Another mod that also ships a `patch-3.MPQ` cannot be used
together with yours as they are.

*Who is affected:* players with another `patch-3.MPQ` mod.

*Workaround:* when you install your patch, the app backs up the other one, and **Uninstall release patch (patch-3.MPQ)**
puts it back.

*Status:* open.

### A release patch from version 0.6.0 cannot always be proven to be yours

*What happens:* 0.6.0 did not record the checksum of the patch it installed. If its build file changed since, the app
cannot prove the installed `patch-3.MPQ` is yours, and **Uninstall release patch (patch-3.MPQ)** asks before moving it
to the `backups` folder (it is not deleted).

*Who is affected:* users who installed a release patch with 0.6.0 and did not build again since.

*Workaround:* build the release patch once with 0.6.1 (the checksum is then recorded), or confirm the question.

*Status:* open (affects old records only).

## Data tables

### Some columns are not verified

*What happens:* the meaning of 270 of the 1,484 columns of the 154 tables is not proven by the game's code, the
server or the files. They are shown with a grey, italic name: a best guess, or `col_N` when unknown. Among the tables
modders use most, these are not verified: ChrClasses *PlayerClass*; Map *col_16*, *col_17*, *col_18*;
SkillLineAbility *CharacterPoints1* and *CharacterPoints2* (always 0); SpellVisualEffectName *col_3*; SpellVisualKit
*KitType*, *CharParamTwo1-4* and *CharParamThree1-4*; LightParams *col_3* (always 0). 17 tables are raw apart from
their ID (sound lookups, video hardware, blood levels...).

*Who is affected:* anyone who edits those columns.

*Workaround:* edit them as stored values, test in the game. Links in unverified columns only give warnings.

*Status:* open (each new proof from the decompile or the files makes a column named; the layout file is
`app\WoWData.Core\Dbc\Layouts\dbc_layouts_5875.txt`).

### Edits of tables the server takes from its database change only what the game shows

*What happens:* the server (VMaNGOS) does not read Spell, Map, AreaTable, Faction, FactionTemplate, TaxiNodes,
SkillLineAbility, SoundEntries, AreaTrigger, MailTemplate and CreatureSpellData from its dbc folder; it reads the same
data from its database. An edit of these tables changes names, icons, tooltips and visuals in the game, but not what
the server does (a spell's damage, a faction's reaction, a flight node's position on the server).

*Who is affected:* anyone who changes the rules of the game, not only its looks.

*Workaround:* make the same change in the server's database table: **Edit it in the Server data tab** opens it in the
**Server data** tab, and **Check client and server...** there compares both sides and makes them match in either
direction ([Server data](server-data.md#client-and-server)). "Copy to server" refuses these tables on purpose.

*Status:* open (by design of the server; the app now guides the change on both sides).

### Copy to server was only tested against test copies

*What happens:* **Copy to server** writes into the server's dbc folder after a backup. The app's tests copy into fake
server folders only, never into a live server's data. The server must be restarted to read the files.

*Who is affected:* anyone who copies tables to a server.

*Workaround:* stop the world server before copying, keep the backups (**Undo last copy to server** restores them).

*Status:* open.

### Links to the server's items and creatures are not checked

*What happens:* some columns hold the number of a server thing (an item entry, a creature entry, a game object), for
example a spell's reagents or a flight point's mount. The app checks links between tables, but not these numbers
against the server's database.

*Who is affected:* edits of those columns (their hint says *a ... entry of the server's database*).

*Workaround:* look the number up in the **Server data** tab (search the ID in **Items** or **Creatures (NPCs)**) before
you type it.

*Status:* open.

### The Browse list and Used in game show the game's tables, not your edited ones

*What happens:* the index behind the **Browse** tab and **Used in game** is built from the game's own files. A table
you edited (for example a new creature display, or a spell with another icon) is not in the Browse list, and Used in
game shows the game's values. The 3D viewer and the texture viewer show your edited files.

*Who is affected:* anyone who adds rows (new displays, new spells) or changes links.

*Workaround:* the **Data tables** tab shows your version; to see a new display in 3D, look at the display you copied
it from.

*Status:* open.

## Server data

### Most world tables cannot roll back an apply by themselves

*What happens:* VMaNGOS keeps most tables of its world database in MySQL's MyISAM storage, which has no transactions.
When a statement of **Apply to server...** fails in the middle, the app writes the backup rows back itself (and says
so). If the database or the computer stops at that very moment, some rows can be left applied.

*Who is affected:* everyone who applies server edits.

*Workaround:* every apply keeps `backup_rows.sql` (the rows before) and `backup_tables.sql` (the whole tables) in the
app's `backups\server_db\apply_<date>_<time>` folder: **Undo last apply** runs the first; any MySQL tool can run either.

*Status:* open (by design of the server's tables).

### The world server keeps the old data until it restarts or reloads

*What happens:* the world server (`mangosd`) reads its tables when it starts. After an apply, it keeps using the old
data. Spells, areas, factions, faction templates, flight points and skills cannot be reloaded while it runs; creatures
and objects already in the world keep their old values until they respawn.

*Who is affected:* everyone who applies server edits to a running server.

*Workaround:* restart the world server, or use the `.reload` command the apply names (as a GM in the game).

*Status:* open (by design of the server).

### The game shows old names and stats until its cache is cleared

*What happens:* the game caches what the server told it about items, creatures, quests, objects and texts (its `WDB`
folder) and does not ask again, so a renamed item or a changed quest text keeps its old look in the game.

*Who is affected:* everyone who edits names, stats or texts on the server.

*Workaround:* close the game and use **Clear the game's cache (WDB)...** (the files are moved to the app's backups).

*Status:* open (by design of the game).

### Many world tables are only shown as stored

*What happens:* of the 176 tables of the VMaNGOS world database, 53 are described by WoW Data Studio (41 fully, 12
partly; their columns have names, hints, choices and links). The others (scripts and AI events, pools, game events,
battlegrounds, player levels and start data...) are listed in **All tables** and edited as the database stores them,
checked only by their SQL types.

*Who is affected:* edits of scripts, AI, pools, events and the other undescribed tables.

*Workaround:* edit them as stored values (their hint says so), test on the server.

*Status:* open (each table described in `app\WoWData.Core\ServerData\Layouts\server_layouts_vmangos.txt` gets the
friendly view).

### Rows of a table without a key are told apart by all their values

*What happens:* a few world tables have neither a primary nor a unique key (`item_display_info`). The app tells their
rows apart by all their values: two identical rows are edited and deleted together.

*Who is affected:* edits of those tables.

*Workaround:* none needed in practice (the tables are copies for tools); check the result with **Compare with the
server**.

*Status:* open.

### Apply to server was only tested against throwaway databases

*What happens:* the app's tests apply, undo and compare against a throwaway MySQL server they start themselves (its own
data folder and port), loaded from a copy of this machine's world database schema and a sample of its rows; they never
write a live database.

*Who is affected:* everyone who applies to a real server.

*Workaround:* look at *The statements* before you apply; keep the backups; export an SQL patch first if you want a
second copy.

*Status:* open.

### "The world server is running" means any mangosd on this computer

*What happens:* the app looks for a program called `mangosd.exe` on this computer. With several servers (or a server on
another computer), the warning of **Apply to server...** can be about another server than the one whose database you
edit, or missing.

*Who is affected:* users with several servers or a remote server.

*Workaround:* none needed: applying to a running server is allowed (with that warning); restart the right server
afterwards.

*Status:* open.

## Spell and class designers

### A new class needs code changes in the game and the server

*What happens:* **New class (experimental)** makes the new class's data (ChrClasses, CharBaseInfo, skill lines, start
outfits, `playercreateinfo` and level stats), but the 1.12 game and the server also know the classes in their code.
Without code changes the server ignores every `playercreateinfo` and level-stats row of the new class
(`CLASSMASK_ALL_PLAYABLE` in `SharedDefines.h`, checked by `ObjectMgr::LoadPlayerInfo`), so the class cannot be created,
and the game's creation screen has buttons and icons for the nine classes only. The full list, with where each place is,
is under **What a new class still needs** and `wds classes engine-report`; it lists the places found so far and may not
be complete.

*Who is affected:* everyone who wants to add a playable class.

*Workaround:* make the code changes in the client project and the server source the report lists, then use the data the
designer made. Until then the new class's rows do nothing.

*Status:* open (the class designer is experimental: it makes the data only).

### Talent rows added in the Data tables tab can hide the rest of a tree

*What happens:* the game reads each talent tree as one group of rows that follow each other in `Talent.dbc`. A row added
in the Data tables tab goes to the end of the file, so its tree then shows only that talent in the game. The talent
designer keeps the rows together (0.13.2) and its check warns, but the Data tables tab does not know this rule.

*Who is affected:* anyone who adds talents in the Data tables tab instead of the talent designer.

*Workaround:* add talents with **Classes and talents**; or open the tree there after editing and click **Save to my
workspace** (it puts every tree's rows together again).

*Status:* open.

### A duplicated visual's tint and size are an approximation

*What happens:* **Duplicate visual (tint and size)** scales the copied effect models (their geometry, bones, particle
emitters, particle sizes and speeds, ribbons) and multiplies their colours and textures. Particle effects that the game
draws in world space, or that use their own spline paths, may look a little different from a truly bigger effect, and a
tint cannot make a dark texture bright. The copies show in the designer's 3D view only after saving.

*Who is affected:* everyone who duplicates a visual with another size or colour.

*Workaround:* check the copy in 3D after saving, and in the game; or pick a visual of another spell instead.

*Status:* open.

### The tooltip preview does not know talents and items

*What happens:* the preview fills in the `$` variables as the game does, but without the player's talents, items and
other spell modifiers, and `$z` shows *your home inn*. In the game a talented player may see bigger numbers.

*Who is affected:* everyone who reads the preview of a spell that talents change.

*Workaround:* none needed; the numbers without talents are the spell's own.

*Status:* open (by design).

### Scripted effects cannot be checked

*What happens:* *Dummy*, *Script effect*, *Send event* and the *Dummy* and *Override class scripts* auras do what the
server's code for that spell says. The checks warn that a script is needed, but cannot tell whether the server source
has one.

*Who is affected:* everyone who uses these effects in new spells.

*Workaround:* use an effect the server handles by itself, or write the script in the server source.

*Status:* open (by design).

### Without the server database the class view lists only skill-line spells

*What happens:* the trainers and the starting spells of a class are in the server's database. Without it,
**Spells by level** lists the spells of the class's skill lines at their spell level, marked *taught (trainer data needs
the server)*, and trainers cannot be added.

*Who is affected:* anyone who uses the class designer without the server database connected.

*Workaround:* turn on the server database in Settings (a local or test server).

*Status:* open (by design).

## Quest-chain designer

### Only a simple chain was tried in the running game

*What happens:* the first test in the running game (VMaNGOS, 2026-10-16) tried a two-quest chain with a new quest giver:
the giver's `!` and `?`, the texts with `$N` / `$c` / `$B` filled in, the quest log, the kill objective counted and
tracked, the turn-in, the next quest offered at once (next in chain), the money and item rewards: all as designed. A
reward spell was shown but not learned: the spell IDs, fixed in 0.13.3 and tried in the game (the reward spell is learned
at the turn-in). Branches,
exclusive groups, talk and explore objectives, object givers and breadcrumbs were tested only against throwaway server
databases, not in the game yet.

*Who is affected:* everyone who builds bigger chains.

*Workaround:* apply the chain, clear the game's cache and try it with a GM character (**Test in game** lists the
commands) before you build on it.

*Status:* open.

### A new quest giver copies the AI name of the NPC it looks like

*What happens:* **New quest giver** copies the creature it looks like (Gornek in the test), with its AI name
(`ai_name` `EventAI`), but not that creature's AI events. The world server then logs *CreatureEventAI: EventMap for Creature N is
empty but creature is using CreatureEventAI* at start. The NPC works as a quest giver; it only does nothing on its own.

*Who is affected:* everyone who makes a quest giver from a scripted NPC.

*Workaround:* none needed; or clear `ai_name` of the new NPC in the Server data tab.

*Status:* open.

### Hostile and friendly NPCs are worked out from the faction tables only

*What happens:* the faction checks read the server's faction templates and, for factions with a reputation, the
starting reputation of the faction's races. They do not know what scripts do: an NPC whose faction a script changes for
a quest (Lord Banehollow of the Dreadsteed chain) is reported as hostile to everyone, and a player who raised his
reputation from *Hostile* is not taken into account.

*Who is affected:* everyone who reads the faction warnings of scripted or reputation quests.

*Workaround:* the faction findings of givers and enders are warnings or notes, not errors, unless the quest is only for
the faction the NPC is hostile to; read them, then decide.

*Status:* open.

### A decimal value with more than 6 digits comes back rounded from the server

*What happens:* the server's MySQL keeps 6 significant digits of a decimal number in a `float` column (a position, a
radius, a chance): `55.743053` becomes `55.7431`. After **Apply to server...**, **Undo last apply** then says the row
*changed on the server since the apply*. The quest designer rounds the positions it writes to 6 digits; values typed in
the **Server data** tab are not rounded.

*Who is affected:* anyone who types more than 6 significant digits into a decimal column of a server table and then
undoes the apply.

*Workaround:* type at most 6 significant digits (`-10508.1`, `55.7431`); or click **Undo anyway** when you know the
difference is only this rounding.

*Status:* open.

### Long chains are drawn up to 150 quests

*What happens:* the flowchart follows the links of a quest both ways and stops at 150 quests (zone-wide breadcrumb webs
of the game can be bigger). The chain's line says *Only the first quests are shown*.

*Who is affected:* anyone who opens a quest of a very large web of linked quests.

*Workaround:* open a quest further along the web to see its part, or use `wds quests chain <quest> --json`.

*Status:* open.

### Translations are not changed with the English texts

*What happens:* a quest's other languages (`locales_quest`) keep the old texts when you change the English ones, and a
copy for the other faction has no translations.

*Who is affected:* servers with players using other languages of the game.

*Workaround:* edit `locales_quest` in the **Server data** tab.

*Status:* open.

### Escorts and scripted events need a script on the server

*What happens:* the designer sets the flags VMaNGOS uses (*Party accept*, *Explore or event*) but cannot make the
escort itself (the creature's walk and its script), and it cannot tell whether an event's script completes the quest
other than through the script command it looks for (*quest explored*).

*Who is affected:* anyone who designs escort or event quests.

*Workaround:* write the escort or event script on the server (`script_escort_data`, the creature's AI scripts); use the
designer's *speak to* or *explore* objectives where they fit.

*Status:* open (by design).

## Other versions

### Many tables of other versions show raw columns

*What happens:* the **Tables of this version** list of a TBC or WotLK client shows many tables as *raw*: every field is
a number named `col_N`, and texts show as numbers (their place in the file). Only the tables whose columns were worked
out and checked for that version have names: 73 tables of 2.4.3 and 54 of 3.3.5a (the map, area, light, sound, liquid,
world-map, flight-path, creature, item and object display, character, class, race, faction, skill, talent and spell
tables), out of 185 and 245. Raw columns are left out of **Compare across versions**. In the named tables, a grey,
italic column name is a best guess, as in the 1.12 tables; the value names of spell effects, auras and attributes are
not given for these versions (they changed since 1.12).

*Who is affected:* anyone looking up a table of another version that is not named yet.

*Workaround:* compare the raw numbers with the same row in 1.12, or extract the table (**Extract selected...** on
`DBFilesClient\<Table>.dbc`) and read it with another tool.

*Status:* open (each version's layout file, `app\WoWData.Core\Dbc\Layouts\dbc_layouts_<build>.txt`, gets more tables).

### Most columns of the modern clients' tables are raw

*What happens:* the DB2 tables of modern clients are read with the field types their program file gives, but most
columns have no names (`col_N`). Names exist only for the tables maps need, and only for 1.60.1 build 70450
(`app\WoWData.Core\Db2\Layouts\db2_layouts_70450.txt`, each column proven by measurement). Records that are encrypted
in the client are left out (for example 8 of 81 rows of 1.60's Map table).

*Who is affected:* anyone reading other tables, or other modern builds.

*Workaround:* compare the raw values with the same table of an older version (**Compare across versions** with raw
columns: `wds versions compare <table> <id> --raw`).

*Status:* open (more tables get names as the conversion work needs them).

### Most files of the newer modern clients have no names

*What happens:* the "1.60" Classic and Retail clients know the names of only about 12% of their files; the others
are found by number (`#775971`). Wildcard searches need a listfile.

*Who is affected:* anyone browsing a modern client without a listfile.

*Workaround:* choose a listfile you have (**Choose listfile...**), or look a file up by its number.

*Status:* by design (the app does not ship or download a listfile).

### Another version opened read only lists most creatures by their display

*What happens:* in the read-only window of another version, creatures are listed by their model and skins (for
example *Boar - BoarSkinBlue*), not by name, except the 178 creatures the 1.60 client names itself. Creature names are
kept by the server, and another version has no server here.

*Who is affected:* anyone looking for a creature of another version by its name.

*Workaround:* search for the model or skin name (`boar`), or the display ID; your 1.12 game's names (with the server
database) tell which display a creature uses.

*Status:* by design (another version is read without a server).

### Humanoid NPCs of 1.60 show numbers instead of model names

*What happens:* the 1.60 client draws humanoid NPCs with new high-resolution player-race models. These files have no
name your 1.12 game knows, so without a listfile their model and textures are shown as `#878772.m2`, `#570505.blp`.

*Who is affected:* anyone browsing 1.60's humanoid NPCs without a listfile.

*Workaround:* choose a listfile you have (**Choose listfile...**), then **Rebuild the index** in the read-only window.

*Status:* by design (the app does not ship or download a listfile).

### Another version opened read only needs a few hundred MB of memory

*What happens:* the read-only window keeps its whole Browse list in memory, as the main window does: about 257,000
entries for the 1.60 client (about 300 MB), about 275,000 with a listfile. With a listfile only 200,000 named textures
and models are listed; the others (about 481,000 for 1.60) are left out of the list.

*Who is affected:* computers with little memory, and anyone looking for a texture that is left out of the list.

*Workaround:* close the read-only window when you are done; find the other files with
`wds find files <pattern> --version <client>`.

*Status:* open.

### A few animations of other versions stand still

*What happens:* a 1.60 animation kept in a separate `.anim` file plays when you pick it, but a few cannot: measured on
the 1.60 client, 6 of the 6,529 creature animations kept that way (2 whose model names no `.anim` file, 4 whose file
holds no keys). A character-style model whose parent skeleton is not in the client (9 of the 13 parent skeletons the
1.60 creature models name) lists only its own skeleton's animations. The line under the animation list, and
`wds models anims <model> --version <client>`, say why.

*Who is affected:* anyone watching those animations.

*Workaround:* none; the client does not have their keys.

*Status:* by design (the data is not in the client).

### Particles, ribbons and shine layers of other versions are drawn approximately

*What happens:* the 3D view of another version draws particle effects and ribbons with the app's own simulation: they
look close to the game's, not the same (sizes, speeds and timing can differ). Left out: particle tails, twinkling,
wind, particles that are models, the second and third textures of multi-texture particles, and the models' lights. An
effect that the model animates in one animation only is shown in the others with that animation's first value. The
commonest reflection and glow layers are combined the way the app understands the newer clients' combiners; the rarer
ones show their first texture only. The model statistics say what was approximated for each model. Measured on the
1.60 client: 9,157 particle emitters and 867 ribbons of creature and spell-effect models, every one read; 1,845 of
2,241 two-layer texture units of creature and sampled item models had their first texture only before, 1,641 of them
are combined now (204 still show their first texture only).

*Who is affected:* anyone comparing effects or shiny weapons and armour of 1.60 with 1.12.

*Workaround:* none in the app.

*Status:* open (particles of your 1.12 game's models are not drawn either, see above).

### Player characters and NPC customization of other versions are not shown

*What happens:* the read-only window lists no player races, and a humanoid NPC of 1.60 shows its baked texture with
its model's default parts: its hair, face and clothes choices are not applied (they use the customization tables of
the newer clients).

*Who is affected:* anyone looking at 1.60 NPCs or player models.

*Workaround:* none in the app yet.

*Status:* open (planned with the characters).

### The maps of other versions are drawn more simply than in the game

*What happens:* the 3D view of another version's tile (the read-only window's **Maps** tab) draws the ground with up
to 8 texture layers, the fine alpha maps and holes, the liquids, the doodads and the buildings, but leaves out what
the app's 3D view also leaves out for your game, and some parts only the newer clients have:

* liquids are flat coloured surfaces by their kind (water, ocean, lava, slime): no animated textures, no new liquid
  looks (fel, tar), no waterfalls from the newer liquid objects;
* the ground's vertex colours and the newer ground lighting are not applied; a ground texture's height-blending
  parameters (used by the newer clients to blend layers by height) are ignored: the layers are blended by their alpha
  maps only;
* a building's own furniture (its doodads), lights, portals, vertex colours and liquids are not drawn (the building
  preview counts the furniture models); level-of-detail copies of the doodads and buildings are not used;
* the doodads are shown in their rest pose, without particles;
* a chunk with more than 8 texture layers would show its first 8 (none measured on the 1.60 client).

*Who is affected:* anyone comparing a tile of 1.60 with the game.

*Workaround:* none in the app.

*Status:* open (the same limits as the 3D view of your game, see *The 3D views are previews, not the game's renderer*).

### Two maps of the 1.60 copy have no tiles to show

*What happens:* in the read-only window of the 1.60 client, the maps *test* (13) and *Manor Mistmantle* (3109) are
listed with *its WDT (#...) is not in this client: no tiles to show*: their map files are not in the measured copy.
Another 8 rows of the Map table are encrypted and not listed at all (the status above the minimap counts them).

*Who is affected:* anyone looking for those maps.

*Workaround:* none: the files are not on this computer, or locked without a key.

*Status:* by design (the app never guesses locked or missing data).

### "Blizzard archives only" knows three releases

*What happens:* the app knows which patch archives Blizzard shipped with 1.12.1, 2.4.3 and 3.3.5a. For any other
version **Blizzard archives only** leaves nothing out, and no archive is marked *not Blizzard's*.

*Who is affected:* copies of other versions with a server's or a mod's patch archive.

*Workaround:* look at the **Archives** list: a patch archive with an unusual name (`patch-7.mpq`, `patch-Z.MPQ`) is
usually not Blizzard's; move a copy of the client without it into the folder.

*Status:* open.

### Converted maps leave out the models and buildings 1.12 does not have

*What happens:* **Convert map...** keeps every tree, rock and building whose model your 1.12 game has, and keeps your
1.12 model where 1.60 put a new one at the same place. The others (new 1.60 models and buildings, which are in a newer
file format) are left out: on all of Eastern Kingdoms about 26,000 of 218,000 doodads and 720 of 2,800 buildings, on
Zephras Isle most of them. The report lists them (*Left out most*), by FileDataID when they have no name.

*Who is affected:* anyone converting 1.60 maps, most of all new areas and maps.

*Workaround:* place 1.12 models there in Noggit after the conversion.

*Status:* open (the conversion of modern models and buildings to 1.12 is planned).

### A converted new map is not in the Maps tab until the release patch is installed

*What happens:* a map 1.12 does not have (for example Zephras Isle) is saved into your workspace with its new Map row,
but the **Maps** tab lists the maps of the game's archives, so the new map is not listed there yet. Its files are in
**My changes**.

*Who is affected:* anyone converting a whole new map.

*Workaround:* build and install the release patch (**Build release patch (patch-3.MPQ)...**), then open the game
folder again (**File > Rebuild the index**); or test it in the game with **Test all in game (install as loose files)**.

*Status:* open.

### The update catalog imports only terrain, textures, interface art, fonts and MP3 music

*What happens:* in the **Updates** tab, updates of the classes **Needs converter**, **Needs server** and
**Needs client code** cannot be imported: modern models and buildings, OGG sounds, the light zones and their colours,
skyboxes, weather, data-table rows and the interface texts. **Preview import** lists them under *Not imported* with why.
The colours of the light zones are not even compared yet (their 1.60 table's columns are not decoded), so a zone's
*Sky and lighting* update counts its light zones, not its new colours.

*Who is affected:* anyone who wants the 1.60 lighting, models, sounds or tables in their 1.12 game.

*Workaround:* none in the app yet; the **Data tables** tab and Noggit can recreate single rows and placements by hand.

*Status:* open (the model, building, light and table converters are planned).

### Larger 1.60 textures are made 512 x 512, and larger character textures are not imported

*What happens:* 1.12 loads textures of at most 512 x 512, so a high-resolution 1.60 texture is imported at that size
(still sharper than a smaller 1.12 one; textures that are already 512 x 512 bring nothing and are listed as
*beyond 1.12's limit*). Character skins, faces, hair and armour pieces that 1.60 made larger are listed as
**Needs client code**: 1.12 puts them together into one texture of a fixed size (inferred from the 1.12 format, not tested
in the game). High-resolution textures the 1.60 client has not downloaded are left out (the catalog's notes say how many).

*Who is affected:* anyone importing high-resolution textures.

*Workaround:* none; let Battle.net download the client completely to have every high-resolution texture.

*Status:* by design for the 512 limit (the recompiled client may lift it); open for character textures.

### New interface art shows only where your interface names it

*What happens:* importing *Interface art* adds the 1.60 interface textures to your game, but the 1.12 interface code
does not use them: they appear only where your own interface code, an add-on or a data table (an icon) names them.

*Who is affected:* anyone importing interface art.

*Workaround:* use them in your interface code or tables (for example new spell icons).

*Status:* by design.

## The command line

### `wds models render` needs the app and OpenGL

*What happens:* the command line has no 3D renderer of its own: `wds models render` (and `wds models compare --images`)
start `WoWDataStudio.exe`, installed next to `wds.exe`, without a window. On a computer without OpenGL 3.3 (some remote
desktop sessions, servers without a graphics driver) they fail with exit code 1.

*Who is affected:* scripts that render models on such computers.

*Workaround:* run them on a computer with a graphics driver; the other commands do not need one.

*Status:* open (by design: one renderer for the app and the command line).

### The model import applies one choice to every texture

*What happens:* in the app's import review each texture row has its own **Size:** and **Transparency:** choice;
`wds models import --keep-size` and `--format` apply the same choice to every texture that asks.

*Who is affected:* model imports with several textures that need different answers.

*Workaround:* leave a texture out with `--exclude <name>` and import it alone with `wds textures import`, or use the app.

*Status:* open.

### The first command after installing or removing patch-3.MPQ organizes the game data again

*What happens:* the index belongs to the game's archives; installing or uninstalling the release patch changes them, so
the next command (in the app: the next start) builds the index again, about 10 seconds. The progress is printed on the
error output.

*Who is affected:* scripts that build and install the patch and then run other commands; everyone after a patch change.

*Workaround:* none needed; `--quiet` hides the progress.

*Status:* open.

### `--zone` needs the server's spawns

*What happens:* `wds find search --zone <name>` finds creatures and game objects by their spawns, which come from the
server database. Without it (or with names turned off), only world objects placed in the zone are found, and the
creature list is empty.

*Who is affected:* scripts that select creatures by zone without a server database.

*Workaround:* connect the server database and run `wds serverdb reload` once.

*Status:* open.

## Windows

### Pop-out windows: pinned windows do not reload, the terrain window has no "Save image"

*What happens:* a pinned 3D window keeps showing the model or tile as it was when you pinned it, also after an import
changes it; the terrain pop-out cannot save a picture.

*Who is affected:* users of pinned pop-out windows.

*Workaround:* unpin the window (**Follow selection**) or open a new one; take a screenshot with Windows
(<kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd>).

*Status:* open.

### Each closed 3D window leaves two small handles behind

*What happens:* every 3D view that closes (a model or terrain pop-out window, the three views of a compare window, the
read-only window of another version with its 3D view, its Maps tab's 3D view and its building preview once shown)
leaves two Windows handles (a named shared-memory section and a mutex, both named after the same GUID) and about
110 to 220 KB of memory behind. The app's own resources come back (the GL contexts, textures and buffers it counts, the
windows and their view models), so the rest happens below the app, in the OpenGL to Direct3D bridge of the 3D control
(OpenTK GLWpfControl 4.3.6) or in the graphics driver. Measured with the developers' leak test: 200 pop-outs opened and
closed add about 390 handles and 20 to 30 MB.

*Who is affected:* only someone who opens and closes 3D windows hundreds of times in one session; a few dozen make no
difference.

*Workaround:* keep a pop-out window open (it follows the selection) instead of opening a new one each time; restarting
the app gives everything back.

*Status:* open (the next step: try a newer GLWpfControl, and the same test on another graphics card).

## For developers

### Timing tests depend on the machine

*What happens:* the timing limits of the test suite (`PerformanceTests`: texture decode, model load, the **Used in game**
panel) run alone and take the best of several runs, with a wide margin; on a much slower or very busy machine they can
still fail without anything being wrong.

*Who is affected:* developers running `dotnet test`.

*Workaround:* run the suite again when the machine is idle, or run `--filter FullyQualifiedName!~PerformanceTests`.

*Status:* open (no failure seen in the 0.6.1 release runs).
