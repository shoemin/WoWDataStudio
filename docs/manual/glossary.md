# Glossary

The words used in the app and in this manual, in alphabetical order.

| Word | Meaning |
|---|---|
| `.blp.json` | The small note saved next to an exported PNG. It remembers which game texture the PNG came from. Keep it with the PNG. |
| .reload | A GM command of VMaNGOS that reads a table of the database again while the world server runs, for example `.reload item_template`. Spells, areas, factions and flight points cannot be reloaded: restart the server. |
| Action (Blender) | Blender's name for one animation: a set of keys over time. The app exports every animation of a model as an action named after it (*Stand*, *Walk*, *Stand [2]*), and the action's name decides which animation it becomes in the game. See [Skeleton and animations](skeleton-and-animation.md). |
| Additive (blend mode) | A way of drawing a texture: its colour is *added* to what is behind it, so black is invisible and bright colours glow. Spell glows, fire and light beams use it. Tinting the black background of such a texture shows as a visible square. See [Glowing (additive) textures](editing-textures.md#glowing-additive-textures). |
| ADT | A map tile file (`<map>_<x>_<y>.adt`): the ground of one tile (heights, textures, water, holes) and the doodads and buildings placed on it. |
| Alpha map (texture painting) | Per terrain chunk, how strongly each ground texture layer shows (4 bits per point in 1.12). |
| Alpha, alpha channel | The transparency of each pixel: from fully solid (opaque) to fully see-through. *1-bit alpha* means each pixel is either solid or invisible; *8-bit alpha* allows anything in between. |
| Animation (sequence) | One movement of a model: *Stand*, *Walk*, *Attack1H*... A model has many. The names come from the game's AnimationData table, which also says what the game plays instead when a model lacks one (its *fallback*). |
| Apply to server | Writes your saved server edits into the server's world database: backed up first, in one transaction, logged; **Undo last apply** takes it back. See [Server data](server-data.md#apply-to-server). |
| Archive | See *MPQ*. |
| Area trigger | An invisible sphere or box in the world (the game's `AreaTrigger.dbc`, the server's `areatrigger_template`). When a player enters it the game tells the server, which can complete an *explore* quest. See [Explore a place](quest-chain-designer.md#objectives-kill-collect-speak-explore). |
| Attachment point | A point on a model's skeleton where the game attaches something: a weapon (*HandRight*), a shield, a helmet, shoulder armour, a spell effect (*SpellLeftHand*), the name plate. In Blender, an empty named `ATT_<id>_<name>`. See [Skeleton and animations](skeleton-and-animation.md). |
| Blend mode | How a model's or building's material draws its texture: *opaque* (the alpha is ignored), *alpha key* (cut out where the alpha is low), *alpha* (see-through), *additive* (glows, black invisible), *mod* / *mod2x* (darkens or brightens what is behind). The app reads them from the model files. |
| Blender | A free 3D program (blender.org). The app exports models for Blender 4.5 and imports your edits back; the *WoW add-on* for Blender reads and writes the app's files. See [Models and Blender](model-export-import.md). |
| BLP | The game's texture file format (`.blp`). The app converts BLP to PNG for editing, and your PNG back to BLP in the original's exact format. |
| Bones, skeleton | The invisible "joints" that move a model's surface during animations. Each bone follows its parent bone; the app can export them to Blender as an armature and import added, removed or moved bones back ([Skeleton and animations](skeleton-and-animation.md)). |
| Bookmark (Noggit) | A saved place in Noggit's start window; "Edit in Noggit" adds one at the tile you selected. |
| Bounding box | The box that encloses a model, used by the game for its size and for clicking it. |
| Breadcrumb (quest) | A quest that only leads to another one (*Breadcrumb for*): it closes when the player takes the quest it leads to. |
| CASC | How World of Warcraft stores its data from 6.x (Warlords of Draenor) on, and in every client Battle.net installs today: one shared store of numbered data files instead of MPQ archives. The **Other versions** tab reads their files (by path or *FileDataID*), not yet their data tables. |
| Listfile | A text file that names the files of a modern client (lines `FileDataID;path`). The app does not come with one; **Choose listfile...** in the **Other versions** tab uses one you have. |
| FileDataID | The number a modern (CASC) client finds a file by, for example `#775971` for the Azeroth map file. Many files of the newer clients have no name inside the client, only this number. |
| Change set | All the files that came with one import. **Revert this import** undoes a whole change set. |
| Character sections (CharSections) | The game's table of character textures: skin colours, faces, hair, facial hair, underwear for each race and sex. |
| Choice (in a data table) | A number with a name for each value, for example a spell's school: `2 · Fire`. The **Data tables** tab shows the names and offers them in a list. |
| Chunk (MCNK) | One of the 256 squares of a map tile (33 x 33 yards), with its own heights (145 points: 9 x 9 plus 8 x 8 in the middle of the squares), texture layers, shadows and water. In the file each chunk is an *MCNK* block; the points on a chunk's border are stored in both chunks that share them. |
| Class mask | A number with one bit per class (bit n-1 for class n: Warrior 1, Paladin 2, Hunter 4, Rogue 8, Priest 16, Shaman 64, Mage 128, Warlock 256, Druid 1024) that says which classes a row is for: skill lines, talent trees, items, quests. 0 means every class. See [Class masks](class-and-talent-designer.md#class-masks). |
| Client view | The app's own folder of links to your game's archives that Noggit reads, so that Noggit can never write into the game folder. |
| Colour changed / alpha changed | The compare's two counts: how many pixels changed colour (red, green, blue) and how many changed alpha. Colour changes under fully transparent pixels count too, because in many textures the alpha is not transparency. |
| Column (of a data table) | One value of every row, for example a spell's **SpellIconID**. A grey, italic column name means its meaning is not verified. |
| Content patch | VMaNGOS can run as any patch of the original game, from 1.2 (patch 0) to 1.12 (patch 10; `WowPatch` in `mangosd.conf`). Many rows of its database exist once per patch; the server uses the newest one up to its own patch. The **Server data** tab shows which one that is. |
| Copy to server | Copies your edited data tables that the world server reads too into the server's `dbc` folder (`Server\data\5875\dbc`), with a backup. See [Data tables](dbc-editor.md#copy-to-server). |
| Coverage map | In the tile breakdown: a picture of the tile (north up) that is bright where a ground texture is painted and dark where it is not. |
| Creature | A monster or NPC, as defined by the server (with an entry number, for example 3098 for the Mottled Boar). |
| DBC, data table | The game's data tables (`.dbc` files in `DBFilesClient`): spells, icons, item and creature looks, races, classes, maps, zones, flight points, talents, skills, sounds and many more, 154 in all. Each is a list of rows with the same columns. Edit them in the **Data tables** tab ([Data tables](dbc-editor.md)). |
| Display id (display) | One "look" of a model: which model and which skins. Creature 3098 uses display 503. Several creatures can share one display, and many displays can share one model. |
| Doodad | A model placed on the map: a tree, a rock, a fence, a barrel (an M2 model). |
| Drop source | Where an item comes from: a creature's loot, a chest, a container, pickpocketing, skinning, fishing, a vendor, a quest or a spell. A *quest drop* drops only for players who need it (a negative chance in the loot table). |
| DXT (DXT1, DXT3, DXT5) | Compressed texture formats. They store 4 x 4 pixel blocks with a few colours each: small files, but fine details and soft gradients lose a little. DXT1 has no or 1-bit alpha; DXT3 and DXT5 keep smooth transparency. |
| Edge (shared edge) | The line of points where two neighbouring tiles meet. Both tiles store those points, and their heights must be identical, or the ground has a *seam* (a crack). |
| EDGE_N, EDGE_E, EDGE_S, EDGE_W | The vertex groups of a terrain tile in Blender: the points on its north, east, south and west edge. |
| Event (animation event) | A moment in an animation when the game does something: plays a footstep sound (`$FSD`), lets a spell or an arrow go (`$CSL`, `$BWR`), sheathes a weapon. In Blender, a marker of the action named by its code. See [Skeleton and animations](skeleton-and-animation.md). |
| Exclusive group (quests) | A number shared by quests. Positive: a choice, taking one closes the others. Negative: all of them must be done before the quest they open. See [The quest-chain designer](quest-chain-designer.md). |
| Export package | The folder **Export for Blender...** writes (`Boar_export`): the model as glTF, its textures as PNG, and `wowdata_manifest.json`, which tells the import which game file each part came from. |
| Faction template | The server's and the game's row that says whom a creature fights and helps (its masks for the Alliance, the Horde and monsters, and its enemy and friend factions). A creature hostile to a faction cannot be a quest giver for it. |
| Flags | A number whose bits each switch something on, for example a spell's attributes. Shown as a hexadecimal number (`0x10000`); the **Data tables** tab names each bit with a tick box. |
| Fly camera (Fly) | A way to move in a 3D view like a free camera in a game: <kbd>W</kbd> <kbd>A</kbd> <kbd>S</kbd> <kbd>D</kbd> to move, the mouse to look around. Best for terrain. See [Moving around](model-viewer.md#moving-around). |
| Focus on the selection | <kbd>F</kbd> in a 3D view: brings the shown model or the selected tile back into view, from the direction you look from. |
| Full screen | A window using the whole monitor, without its frame and over the taskbar (<kbd>F11</kbd> in the pop-out and compare windows; <kbd>Esc</kbd> leaves it). |
| Game object | A clickable object in the world: a chest, a door, a herb, a sign. |
| Geoset | A part of a model that the game switches on or off: hair styles, beards, ears, gloves, capes... |
| Global sequence | A loop of a model that runs on its own clock, whatever animation plays: a waving flag, glowing runes, swaying leaves. In Blender, an action named *Global sequence N*. |
| glTF (`.gltf`, `.glb`) | A standard 3D file format that Blender and most 3D programs read and write. The app uses it for the trip to Blender and back. |
| Ground texture | A texture the maps paint on the ground (the `Tileset` folder), up to 4 layers per chunk. Most have a `_s` variant; see *`_s` texture*. |
| Height heat map | A picture of a tile's heights from above: blue for low ground, through green and yellow, to red for high ground; holes dark. |
| Hot reload | The running game reads changed files again, without a restart: **Reload in running game** in **My changes**. Needs the project's client with the hot-reload update and the switch in [Settings](settings.md#hot-reload-in-the-running-game). See [Testing in the game](testing-in-game.md#reload-in-the-running-game-hot-reload). |
| ID (of a row) | The number that names a row of a data table; other rows link to it by that number. Each row has its own. |
| Idempotent (SQL patch) | A script that gives the same result when it is run twice. The app's SQL patches are: each row is deleted by its key and written as it should be. |
| Index | The app's own list of everything in the game, built once per game folder ("Organizing game data"). |
| Item display | One look of an item: its model and textures. |
| Key (of a server row) | The values that tell a row of a server table apart, for example `entry=25;patch=0` for an item, `entry=3158;item=159` for a vendor's item. |
| Key bone | A bone the game's code finds by its job: the head (camera, helmet, name plate), the hands and fingers (weapons), the jaw, the root... The 1.12 client knows 27. |
| Learn spell | A spell whose first effect is *Learn spell*: it teaches another spell. Trainers list learn spells, not the spells themselves (the 1.12 server refuses anything else). The Spell designer makes one when needed. See [Learning](spell-designer.md#learning-trainers-and-skill-lines). |
| Link (in a data table) | A column that holds the ID of a row of another table, for example a spell's **SpellIconID** names a row of SpellIcon. 0 means "none". The **Data tables** tab shows the linked row's name and opens it when you click. |
| Localized text | A text stored in the game's eight languages (enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX); an English game only fills enUS. |
| Loose files | Single files placed in the game's `Data` folder, outside the archives. The game reads them only with its *loose-file mode* on (the project's client from wave185 on). |
| Loose-file mode | A switch in the game client that makes it read loose files before its archives. |
| M2 | The game's 3D model format (`.m2`; the game also calls it `.mdx`). Creatures, characters, items, trees and spell effects are M2 models. |
| maps, vmaps, mmaps (server) | The world server's own copies of the map data: *maps* = ground heights, zones and water; *vmaps* = collision and line of sight; *mmaps* = pathing (the navigation mesh creatures walk on). They must be updated after a map change. |
| Material variants | A glTF feature that stores several skins of one model in one file; Blender switches them in its *glTF Variants* panel. |
| MCLQ | The water block of a chunk in a 1.12 map tile: river, ocean, lava or slime, with its surface heights and which of the chunk's 8 x 8 squares have liquid. |
| MCNR (normals) | The block of a chunk that stores the direction each point of the ground faces. The game lights the ground with it; the app recomputes it around the points whose height you changed. |
| Minimap | The small top-down pictures of every tile that the game shows in its minimap; the Maps tab builds its map overview from them. |
| Mip level, mipmap | The smaller copies of a texture (half size, quarter size...) that the game shows when something is far away. Level 0 is the full-size picture. |
| Model | The 3D shape of a thing (an M2 file). |
| MPQ | The game's archive files (`.MPQ`) in the `Data` folder, which hold all game files. Later archives win over earlier ones: `patch-3.MPQ` wins over `patch-2.MPQ`, which wins over `patch.MPQ`, and so on. |
| MyISAM, InnoDB | The two ways MySQL stores a table. Most VMaNGOS tables are MyISAM, which cannot undo a transaction; the app then writes its backup rows back itself. |
| Next in chain | A quest the NPC offers right after you hand in another one, in the same window (`NextQuestInChain`). |
| Noggit | A free map editor for World of Warcraft (a separate program under the GPL). WoW Data Studio works with the project's fixed version for 1.12. |
| Orbit camera (Orbit) | The usual way to move in a 3D view: turn around the model (or tile) with the mouse, zoom with the wheel. |
| Palette texture | A texture format that stores up to 256 colours plus transparency. Used by character textures. |
| patch-3.MPQ | The archive WoW Data Studio builds with all your changes. The game loads it on top of its own archives. **Uninstall release patch (patch-3.MPQ)** in **My changes** takes it out again. |
| Pinned | A pop-out window with **Follow selection** switched off: it keeps showing its model or tile. |
| Playable-animation table | Part of every model: for each of the game's animations, the one the model really plays (its own, or a fallback from AnimationData, for example *Run* when it has no *Sprint*). The app rebuilds it when the animations change. |
| PNG | A common picture format every image editor can open. The app exports textures as PNG and imports your edited PNG. |
| Pop-out window | A 3D view in a window of its own (**Open in new window**), to make it bigger, put it on another monitor or show it full screen. See [Open in a new window](model-viewer.md#open-in-a-new-window). |
| Portability class | In the update catalog: what it takes to bring a 1.60 update into your 1.12 game. *Ready* (1.12 loads it, or the app converts it), *Needs converter* (a converter that does not exist yet), *Needs server* (only meaningful with server data), *Needs client code* (a feature of the modern engine), *No visible change* (the files differ but look the same). |
| Power of two | 2, 4, 8, 16, 32, 64, 128, 256, 512... The game only loads textures whose width and height are such numbers (at most 512). |
| Quest chain | Quests linked by what each needs first (*Must be done first*, *Opens*, *Next in chain*, breadcrumbs, exclusive groups). The **Quest chains** tab draws one as a flowchart. See [The quest-chain designer](quest-chain-designer.md). |
| Quest giver, quest ender | The NPC (or object, or item) that offers a quest, and the one you hand it in to (`creature_questrelation`, `creature_involvedrelation` and the game object tables). A creature needs the *Quest giver* flag. |
| Quest log category | The header a quest is listed under in the quest log: a zone, or a category such as *Warrior* (the game's `QuestSort.dbc`; `ZoneOrSort` negative). |
| Quest token ($N, $C, $R, $G, $B) | A `$` word in a quest text the game replaces: the player's name, class, race, a word for his or her sex, a new line. See [The text preview](quest-chain-designer.md#the-text-preview). |
| Rank spell | The spell a talent gives at one of its ranks (a talent with 5 ranks has 5 spells). See [Talent trees](class-and-talent-designer.md#talent-trees). |
| `_s` texture (specular ground texture) | The second file of a ground texture, `<name>_s.blp` (for example `DurotarDirt_s.blp`): the same colours, plus the ground's *shine* (specular strength) in its alpha channel. With terrain specular on, the game draws it *instead of* the file the map names. See [Ground textures and specular files](editing-textures.md#ground-textures-and-specular-files). |
| Seam | A crack in the ground where two tiles (or chunks) meet and their shared points have different heights. *Check edge seams* in Blender and the terrain import find them; *Sync shared edges* closes them. |
| Server data | The **Server data** tab: your server's world database. See [Server data](server-data.md). |
| Server database (world database) | The database of your game server (CMaNGOS / VMaNGOS, MySQL). The app reads creature, item and object names from it. |
| Server tools | The project's scripts that extract the server's map data from your game (`reextract.ps1`) and install it into the server (`swap_in.ps1`). |
| Skin | A texture painted on a creature model. One model, many skins: the boar model has blue, brown, crimson, ivory... skins. |
| Source chain | The path from a thing to its files, shown under **WHERE IT COMES FROM**: creature → display → model → skin. |
| Specular, terrain specular | The game's shine on the ground. On when the game's `WTF\Config.wtf` has `SET specular "1"` and `SET pixelShaders "1"`; then the ground is drawn with the `_s` textures. |
| Spell effect | A 3D model that a spell shows (a frost shield, fire in a caster's hand). |
| Spell family | The class a spell belongs to (*Class (spell family)* in the Spell designer: Mage 3, Warrior 4...), with a *family mask*. Talents and set bonuses change only spells of their family whose mask matches. |
| Spell visual | The look of a spell: what plays while casting, at the cast, the missile, the impact, the aura and the channel (a `SpellVisual` row and its `SpellVisualKit` rows, which name effect models). See [The visual](spell-designer.md#the-visual). |
| SQL patch | A text file of database statements (`.sql`). **Export SQL patch...** writes your server edits as one, with a revert script that puts the rows back. |
| Staging folder | Where the server tools put freshly extracted server data, before you install it into the server. |
| Talent tree, tier | A class's tree of talents (a `TalentTab` row; up to 5 per class) on a grid of 8 rows (*tiers*) and 4 columns. A talent in tier n needs 5 x n points spent in the tree. See [The class and talent designer](class-and-talent-designer.md). |
| Terrain package | The folder **Open terrain in Blender...** writes (`Azeroth_32_48_terrain`): the heights of each tile, its ground textures and their painting as pictures, optionally the doodads and buildings as references, and `wowdata_terrain.json`, which tells the import which tile each Blender object is. |
| Texture | A picture painted onto a model (a `.blp` file). |
| Tile | One square of a map: 533 x 533 yards, one ADT file. A continent has hundreds. |
| Tile breakdown | The window **Break down this tile...** opens: every piece of one tile (ground, chunks, textures, doodads, buildings, water, sounds, zones) in a list, each viewable on its own. |
| Tooltip variable | A `$` word in a spell's description that the game replaces with a value: `$s1` the first effect's value, `$d` the duration, `$o1` the total over the duration... See [The tooltip preview](spell-designer.md#the-tooltip-preview). |
| Update catalog | The **Updates** tab of **Other versions**: every difference between the 1.60 client and your 1.12 game, grouped into updates you can pick and import one at a time (see [Other versions](other-versions.md)). |
| Used by (data tables) | In the **Data tables** tab: the rows of other tables that link to the selected row. |
| Variation (animation) | One of several versions of an animation, for example *Stand*, *Stand [2]*, *Stand [3]*: the game picks one at random each time, by its chance. |
| Vertex group | A named set of points of a Blender object. The terrain tiles have one per edge (*EDGE_N*...). |
| View (skin profile) | One of a model's four ways of splitting its triangles into parts for 256, 75, 53 or 21 bones. All four show the same shape. |
| VMaNGOS | The 1.12 server this project uses (`mangosd`, the world server; `realmd`, the login server), with its MySQL databases `mangos` (the world: items, creatures, quests...), `characters` (players) and `realmd` (accounts). |
| WDB (the game's cache) | The game's `WDB` folder, where it keeps what the server told it about items, creatures, quests, objects and texts. Not needed for texture changes; after a server edit, clear it so the game asks again: **Clear the game's cache (WDB)...**. |
| WDL | A map's far-distance terrain: low-detail heights of every tile, shown on the horizon. |
| WDT | A map's tile list: which tiles the map has (or, for a map that is one building, which building). |
| WMO | The game's format for buildings (caves, houses, cities). Shown simply in the 3D view of a map tile (see [Maps and terrain](maps-and-terrain.md)); a WMO entry in the Browse list has no 3D view yet. |
| Workspace | The app's folder with your kept edits. Nothing in it is in the game until you install it. |
| World database | The server's database of the game world (`mangos`): items, creatures and their spawns, quests, loot, vendors, trainers, spells... |
| World object | A model placed on the map: a tree, a rock, a fence. Listed by file name. |
| World server (mangosd) | The game server program that runs the world. It must be stopped to update its map data. |
