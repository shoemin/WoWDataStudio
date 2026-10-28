# The main window

A tour of everything on the main screen, from top to bottom. The task chapters ([Finding things](finding-things.md),
[Editing textures](editing-textures.md)...) explain how to use these parts together.

![The main window with the Mottled Boar selected](../screenshots/04_main_creature.png)

## The menu bar

**File**

| Menu item | What it does |
|---|---|
| **Change World of Warcraft folder...** | Back to the welcome screen to pick another game folder. Each game folder has its own index and its own edits. |
| **Settings...** | Opens [Settings](settings.md). |
| **Open another version (read only)...** | Opens another World of Warcraft version (such as the 1.60 Classic client) in this window, in place of your game, with the same tabs; everything that writes is greyed out with the reason. See [Other versions](other-versions.md#opening-another-version-read-only). |
| **Back to your 1.12 game** | Shown while another version is open: opens your game folder again, with your changes. |
| **Import edited model...** | Brings back a model you edited in Blender (or its PNG textures). See [Models and Blender](model-export-import.md). |
| **Build release patch (patch-3.MPQ)...** | Packs all your changes into one archive. See [Releasing](releasing.md). |
| **Open my workspace folder** | Opens the folder where your edited files are kept (in Windows Explorer). |
| **Reload names from the server database** | Reads creature, item and object names again, for example after you started the database. |
| **Rebuild the index** | Reads the game data again (a few seconds). Your changes are kept. With another version open, it reads that version again. |
| **Exit** | Closes the app. |

**Tools**

| Menu item | What it does |
|---|---|
| **Open the 3D view in a new window** | The 3D view of the selected model (**Browse** tab) or map tile (**Maps** tab) in a window of its own, to make it bigger, put it on another monitor or show it full screen (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>O</kbd>). See [Open in a new window](model-viewer.md#open-in-a-new-window). |
| **Compare two skins of this model (3D)** | Two 3D views side by side. See [The 3D model viewer](model-viewer.md#compare-two-skins). |
| **Export for Blender...** | Exports the model shown in the 3D view for Blender. See [Models and Blender](model-export-import.md). |
| **Import edited model...** | The same as in the File menu. |
| **Install the Blender add-on...** | Installs the WoW add-on into Blender 4.5, or shows you the zip to install it yourself. See [Models and Blender](model-export-import.md#install-the-blender-add-on). |
| **Maps and terrain** | Opens the **Maps** tab. See [Maps and terrain](maps-and-terrain.md). |
| **Edit in Noggit...** | Prepares a Noggit project for the map selected in the **Maps** tab. See [Maps and terrain](maps-and-terrain.md#edit-in-noggit). |
| **Import map changes...** | Reviews the files Noggit saved for that map. See [Maps and terrain](maps-and-terrain.md#import-map-changes). |
| **Update server maps...** | The guided update of your server's map data. See [Maps and terrain](maps-and-terrain.md#update-server-maps). |
| **Data tables (DBC editor)** | Opens the **Data tables** tab. See [Data tables](dbc-editor.md). |
| **Copy tables to the server...** | Copies your edited tables that the server reads too into its dbc folder (guided, with a backup). See [Data tables](dbc-editor.md#copy-to-server). |
| **Server data (world database)** | Opens the **Server data** tab. See [Server data](server-data.md). |
| **Check client and server...** | The game's tables next to the server's, every difference, made to match in either direction. See [Server data](server-data.md#client-and-server). |
| **Apply to server...** | Writes your saved server edits into the server's world database (backed up, logged, undoable). See [Server data](server-data.md#apply-to-server). |
| **Clear the game's cache (WDB)...** | Makes the game ask the server again for items, creatures and quests. See [Server data](server-data.md#after-applying-restart-or-reload-and-the-games-cache). |
| **Spell designer** | Opens the **Spell designer** tab. See [The spell designer](spell-designer.md). |
| **Classes and talents** | Opens the **Classes and talents** tab. See [The class and talent designer](class-and-talent-designer.md). |
| **Quest chains** | Opens the **Quest chains** tab. See [The quest-chain designer](quest-chain-designer.md). |

**Help**

| Menu item | What it does |
|---|---|
| **User manual** | Opens this manual in your web browser. <kbd>F1</kbd> opens the chapter of the screen you are on. |
| **Troubleshooting and questions** | Opens [Troubleshooting and questions](troubleshooting.md). |
| **How it works** | Opens the short help panel on the right (see below). |
| **About the loose-file mode (LOOSE_FILES_PATCH.md)** | Technical notes about how the game reads loose files. |
| **The command line (wds)** | Opens [The command line](command-line.md): every feature of the app as a command, for scripts. |
| **About WoW Data Studio** | The version and build of the app; the command line `wds` is the same version. |

## The top bar

* *The search box* ("Search models, textures, creatures, items..."): type any words. The list below updates as you
  type. Tips in [Finding things](finding-things.md).
* **Names from server database: connected** / **Names from server database: not connected**: whether creature, item
  and object names come from your server. Hover over it to see why it is not connected; click it to open the settings.
* **Settings**: opens [Settings](settings.md).
* **How it works**: opens or closes the help panel.

## The filters ("Show:")

One button per type of thing, with how many there are: **Creatures**, **Characters**, **Items**, **Game objects**,
**World objects**, **Spell effects**, **Textures**.

* Click a type to hide it or show it again (a tick means it is shown).
* Right-click a type for **Show only this type** or **Show all types**.
* **All** shows every type again.
* **Show only my changes** (on the right) lists only the textures you changed and the things that use them.

What the types mean:

| Type | What it contains |
|---|---|
| Creatures | monsters and NPCs, by name when the server database is connected |
| Characters | the player races (male and female) with their character-creation textures |
| Items | weapons, armour and other items, with their models, textures and icons |
| Game objects | chests, doors, plants, signs and other clickable objects |
| World objects | trees, rocks, buildings and other models placed in the world (listed by file name) |
| Spell effects | the models of spell visuals, and the spells that use them |
| Textures | every texture (`.blp` file) in the game |

## The tabs

* **Browse**: the results list and the details panel (this chapter).
* **My changes**: everything you changed. The tab shows the number of changes, for example *My changes (3)*.
  See [My changes](my-changes.md).
* **Maps**: the game's maps, zones and tiles, a tile in 3D, and editing maps with Noggit. See
  [Maps and terrain](maps-and-terrain.md).
* **Data tables**: the game's data tables (DBC files: spells, items, creatures, maps, flight points, talents...) to
  look up, follow and edit. See [Data tables](dbc-editor.md).
* **Server data**: your server's world database (items, creatures, quests, spells, loot, vendors, trainers...) to
  look up, follow, edit and deliver to the server. See [Server data](server-data.md).
* **Spell designer**: make a new spell or clone one with a guided form, its tooltip, checks, visual, trainers and skill
  lines, the game's and the server's rows in step. See [The spell designer](spell-designer.md).
* **Classes and talents**: a class's talent trees on the grid, its spells by level, class masks, and a new class
  (experimental). See [The class and talent designer](class-and-talent-designer.md).
* **Quest chains**: quest chains as flowcharts, a quest editor in plain fields, new chains (branches, the other
  faction, the discipline template), quest givers on the map, checks. See
  [The quest-chain designer](quest-chain-designer.md).

The search box and the type filters at the top belong to the **Browse** tab; the **Data tables**, **Server data**,
**Spell designer** and **Quest chains** tabs have their own search.

## The results list (left)

The number of results is shown above the list. Columns:

| Column | What it shows |
|---|---|
| **Name** | the name (or the file name when there is no name) |
| **Type** | Creature, Character, Item, Game object, World object, Spell effect or Texture |
| **ID** | the creature, item, object or display number, when there is one |
| **File path** | the model or texture file inside the game archives |
| **Status** | a badge when it is touched by your changes (see below) |

* Click a column title to sort by it; click again to reverse the order. With a search text, the best matches come
  first (an exact name before names that only contain the words).
* Hover over a row to see more information.
* Click a row to show it in the details panel.
* The list holds all ~97,000 entries without slowing down; just scroll.

### Badges

| Badge | Meaning |
|---|---|
| **Modified** | You changed this texture. Your edit is in your workspace. |
| **Uses edit** | This creature, item or model uses a texture you changed. |

![A modified texture and the badges](../screenshots/12_main_modified.png)

## The details panel (right)

When nothing is selected, it says **Select something in the list** with a few search examples.
When you click a row, it shows from top to bottom:

1. *The name* and a short description (for example *Creature 3098 · level 1-2*).
2. **WHERE IT COMES FROM**: the source chain, from the thing you clicked down to the files, for example
   `Mottled Boar (creature 3098) → display 503 → Creature\Boar\Boar.m2 → skin: BoarSkinIvory`.
   [Finding things](finding-things.md#what-the-source-chain-means) explains it.
3. *Facts*: the skins of the model, who else uses the same texture ("a texture edit changes them too"), where the
   creature is spawned, which archive a file is in.
4. *The 3D view* of models, with its skin picker, animations and options. See [The 3D model viewer](model-viewer.md).
5. **TEXTURES IT USES**: the textures of the selected thing, with their role (for example *skin1*). A **Modified**
   badge marks the ones you changed. Click one to show it in the texture viewer.
6. *The texture viewer*: the selected texture. See [The texture viewer](texture-viewer.md).
7. **Used by**: everything else that uses the selected texture.

![A character in the details panel](../screenshots/05_main_character.png)

### The action bar

At the bottom of the details panel, always visible while a texture is selected, the action bar asks
**WHAT DO YOU WANT TO DO WITH** *the texture*? The buttons are numbered in the order you usually use them:

| Button | What it does | Chapter |
|---|---|---|
| **1. Export texture as PNG...** | saves the texture as a PNG image to edit | [Editing textures](editing-textures.md) |
| **2. Import edited PNG...** | brings your edited PNG back, with a before/after check | [Editing textures](editing-textures.md) |
| **Compare original vs. edited** | opens the comparison window | [Editing textures](editing-textures.md#the-compare-window) |
| **3. Test in game (install as loose files)** | copies your edit into the game folder | [Testing in the game](testing-in-game.md) |
| **Remove from game** | takes that copy out of the game folder again | [Testing in the game](testing-in-game.md) |
| **Revert my change** | throws your edit away; the game's original is used again | [My changes](my-changes.md) |
| **4. Build release patch (patch-3.MPQ)...** | packs all your changes into one archive | [Releasing](releasing.md) |

Buttons that do not apply are greyed out: for example **Compare original vs. edited** only works after you imported
an edit of that texture. Hover over any button to read what it does.

## Messages

* *Toasts*: short messages in the bottom-left corner (for example *Saved your edit of BoarSkinIvory.blp*). They
  disappear after a few seconds; the cross closes them.
* *Busy box*: a box with a moving bar while the app works (exporting, importing, building). The window cannot be
  used during those few seconds.
* *Dialogs*: questions and results always say in plain words what happened and what you can do; their buttons say
  what they do (for example **Install into the game folder** or **Cancel**).

## The "How it works" panel

**How it works** (top right, or **Help > How it works**) opens a short summary of the six steps on the right side of
the window, with a link to this manual. Click **Close** to hide it.

![The How it works panel](../screenshots/17_help.png)

## Command line

The Browse tab's search and details are `wds find search`, `wds find info` and `wds find used`; the action bar's buttons are `wds textures export`, `wds textures import`, `wds textures compare`, `wds changes install`, `wds changes remove`, `wds changes revert` and `wds changes build-patch`. See [The command line](command-line.md).
