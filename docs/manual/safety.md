# Safety and undoing everything

WoW Data Studio is built so that you cannot break your game by accident, and so that every change can be taken back.

## What the app never touches

* *The game's own archives* (`model.MPQ`, `texture.MPQ`, `patch.MPQ`, `patch-2.MPQ` and the others in `Data`). They are
  only ever opened for reading, even while the game is running. The app refuses to write any file with a game archive's
  name.
* *`WoW.exe` and everything else in the game folder*, except the three things below that you start yourself.
* *Your server's database.* The app reads names from it, and the **Server data** tab reads it to show and check it.
  Only **Apply to server...** (and its **Undo last apply**) writes into it, after you looked at the exact statements and
  confirmed: only the world database (never the characters or realm database), after a verified backup of every row
  and table it changes, in one transaction, logged. Your edits wait in your workspace until then. See
  [Server data](server-data.md#apply-to-server).
* *Your world server.* The app never stops or starts it. Its map data (`Server\data`) is only changed by
  **Install into the server** in **Update server maps...**, after you stopped the server, with a verified backup
  (**Undo server update** puts it back). See [Maps and terrain](maps-and-terrain.md#update-server-maps). Its data tables
  (`Server\data\5875\dbc`) are only changed by **Copy to server** in **Copy tables to the server**, after you stopped
  the server, with a verified backup (**Undo last copy to server** puts them back); tables the server takes from its
  database are never copied. See [Data tables](dbc-editor.md#copy-to-server).
* *Noggit and your game folder.* Noggit reads the app's own client view (links to the game's archives) and saves only
  into its project folder in the app's data folder; the app refuses to point Noggit at the game folder itself.
* *Your exported PNG files.* Reverting a change never deletes them.

## The only three ways the app writes into the game folder

Both happen only when you click the button and confirm:

1. **3. Test in game (install as loose files)** (and **Test all in game (install as loose files)**) copies your edited
   files to `Data\<the file's game path>`. It never overwrites a file it did not put there without asking you first,
   and then it keeps a backup. See [Testing changes in the game](testing-in-game.md).
2. **4. Build release patch (patch-3.MPQ)...** with **Also install it into the game:** ticked copies `patch-3.MPQ` into
   `Data`. An existing `patch-3.MPQ` is backed up first. See [Releasing](releasing.md).
3. **Uninstall release patch (patch-3.MPQ)** removes the release patch the app built (it checks the file's checksum) and
   puts back the one that was there before. A `patch-3.MPQ` it did not create is only moved to its `backups` folder,
   after you confirm.

The installer does not touch the game folder at all.

## Undo everything: back to the original game

Close the game first. Then:

1. *Remove the loose files.* Open the app, go to **My changes**, click **Remove all from game**. Every loose file the
   app installed is deleted, and files that were there before are put back.
2. *Remove the release patch.* In **My changes**, click **Uninstall release patch (patch-3.MPQ)**: it removes the
   release patch the app built and puts back the `patch-3.MPQ` you had before, if any. A `patch-3.MPQ` the app did not
   create is only moved to the app's `backups` folder after a clear question, never deleted (see
   [Releasing](releasing.md#removing-your-patch-from-the-game)).
3. *(Optional) Throw your edits away.* In **My changes**, click **Revert** or **Revert this import** for each change.
   Your exported PNG files stay where they are.
4. *If you updated your server's maps:* stop the world server and use **Undo server update** in
   **Update server maps...** (once per update you did), then start the server again.
5. *If you copied tables to your server:* stop the world server and use **Undo last copy to server** in
   **Copy tables to the server** (once per copy you did), then start the server again.
6. *If you applied server edits:* use **Undo last apply** in the **Server data** tab (once per apply you did, newest
   first), then restart the world server. The backups of each apply are kept in the app's `backups\server_db` folder.
   Clear the game's cache (**Clear the game's cache (WDB)...**).

Start the game: it shows the original again.

If you want to remove the app completely: do steps 1 and 2, uninstall the app (Windows *Settings > Apps*), then delete
`%APPDATA%\WoWDataStudio` and, if you like, `Documents\WoW Data Studio`.

## Good habits

* Keep a copy of your game folder before your first test, especially before you share a patch with others.
* Use **Compare original vs. edited** before you release: the **Difference** view shows exactly what you changed.
* Remember shared textures: **Used by** shows everything an edit changes.
* Build the release patch on a closed game.

## Command line

The command line follows the same rules: nothing changes the game folder or the server, and nothing deletes your edits, without `--yes`; see [The command line](command-line.md#safety).
