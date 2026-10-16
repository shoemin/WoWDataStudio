# Finding things

How to find the creature, character, item, tree, spell effect or texture you want to change, and how to read where
it comes from.

## How the search works

Type in the search box at the top. The list updates as you type.

* *Every word must match.* `mottled boar` finds *Mottled Boar*, *Dire Mottled Boar* and *Elder Mottled Boar*, but
  not *Dire Boar*.
* *Parts of words count.* `boar` also finds *BoarSkinIvory* and everything in the `Creature\Boar\` folder.
* *Upper and lower case do not matter.*
* *The words can be in the name, the file path or the id.* You can type a name (`mottled boar`), a file name
  (`boarskinivory`), a folder (`creature\boar`), a creature or item number (`3098`), or a race and sex (`orc male`).
* *The best matches come first*: an exact name, then names that start with your text, then names that contain your
  words as whole words, then the rest.
* Clear the box to see everything again.

To narrow the list, use the type filters below the search box (see [The main window](main-window.md#the-filters-show)):
right-click a type and choose **Show only this type**, then **All** to go back.

## Examples

### A creature: the Mottled Boar

1. Type `mottled boar`.
2. Click *Mottled Boar* (type *Creature*, id 3098).
3. The details panel shows `Mottled Boar (creature 3098) → display 503 → Creature\Boar\Boar.m2 → skin: BoarSkinIvory`,
   the 3D view of the boar, and its texture *BoarSkinIvory.blp*.

![The Mottled Boar](../screenshots/04_main_creature.png)

Without the server database connected, creatures have no names; search for the model instead (`boar`) or the display
number. See [Getting started](getting-started.md#names-from-the-server-database-optional).

### A character: orc male

1. Type `orc male`.
2. Click *Orc Male* (type *Character*). It is the player race model `Character\Orc\Male\OrcMale.m2`.
3. The 3D view shows the character-creation choices (skin colour, face, hair...); the texture list holds the
   character's textures.

The search also finds many textures with "orc" and "male" in their file names. To see only the races, right-click
**Characters** and choose **Show only this type**.

![Orc male](../screenshots/05_main_character.png)

### A tree: the Durotar palm

Trees, rocks and other world decorations are *World objects*. They are listed by their file name, because the game
places them on the map, not by a name.

1. Type `durotar palm` (or `palm`, or `tree`).
2. Click *DurotarPalm01* (type *World object*).

![A palm tree](../screenshots/23_viewer_tree.png)

### An item: the Worn Battleaxe

1. Type `worn battleaxe`.
2. Click *Worn Battleaxe* (type *Item*). The details show the item's model, its texture and its icon.

![An item](../screenshots/24_viewer_item.png)

### A spell effect: Ice Barrier

Spell effects are the 3D models that spells show (the frost shield of Ice Barrier, the fire in a caster's hand).

1. Type `ice barrier`.
2. Click the spell effect (type *Spell effect*). The facts list the spells that show it.

![A spell effect](../screenshots/25_viewer_spell_effect.png)

### A texture directly

1. Right-click **Textures** and choose **Show only this type**.
2. Type a part of the file name, for example `boarskinivory`.
3. Click it: the texture viewer opens with it. **Used by** lists everything that uses it.

![A texture](../screenshots/06_texture_viewer.png)

### Only what you changed

Click **Show only my changes** (on the right of the filters). The list keeps only the textures you changed
(**Modified**) and the things that use them (**Uses edit**). Click it again to see everything.

## What the source chain means

**WHERE IT COMES FROM** shows how the game gets from the thing you clicked to the files on disk. For the Mottled Boar:

```
Mottled Boar (creature 3098) → display 503 → Creature\Boar\Boar.m2 → skin: BoarSkinIvory
```

| Step | Meaning |
|---|---|
| Mottled Boar (creature 3098) | the creature as the server knows it (its entry number) |
| display 503 | the *display id*: one "look" of a model. Several creatures can share a display. |
| `Creature\Boar\Boar.m2` | the 3D model file. All boars share this one model. |
| skin: BoarSkinIvory | the texture painted on the model for this display. Other boar displays use other skins (blue, brown, crimson...). |

For an item, the chain goes item → item display → model → texture (and icon). For a character, race and sex → model →
the character textures (skin, face, hair...).

### Why this matters: shared files

A texture is often used by more than one thing. The facts say so, for example *The same skin is also used by displays
193, 389, 499, 1167, 10015: a texture edit changes them too.* If you repaint *BoarSkinIvory*, every boar with that
skin changes in the game. The **Used by** list under the texture viewer shows them all.

If you want only one creature to change, it needs its own new skin and display; that is not possible in the app yet.

## Command line

`wds find search mottled boar --type creature` searches like the search box (with `--only-changes`, `--zone`, `--sort`); `wds find info <entry>` prints the source chain and the facts, `wds find where <path>` the archives a file comes from. See [The command line](command-line.md#find-the-browse-tab).
