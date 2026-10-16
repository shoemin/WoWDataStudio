# Getting started

This chapter takes you from the installer to the main window: install, first start, choosing your World of Warcraft
folder, the one-time "Organizing game data" step, and (optionally) names from your server's database.

## What you need

* Windows 10 or 11, 64-bit.
* A graphics driver from the last ten years (the 3D view uses OpenGL 3.3).
* A World of Warcraft *1.12.1 (build 5875)* game folder: the folder with `WoW.exe` and the `Data` folder.
  Other versions are refused, because their file formats differ.
* About 400 MB of disk space for the app, plus about 100 MB per game folder for its index.
* Optional: your local server's world database (CMaNGOS or VMaNGOS, MySQL), so creatures and items are listed by
  their names. See [Names from the server database](#names-from-the-server-database-optional) below.

Nothing else has to be installed: the app brings everything it needs (no .NET, no Python).

## Install

1. Run `WoWDataStudio-Setup-<version>.exe` (for example `WoWDataStudio-Setup-0.9.0.exe`).
2. *Windows may warn you.* The installer is not code-signed (signing needs a paid certificate), so Windows
   SmartScreen can show a blue window saying *Windows protected your PC*. Click *More info*, check that the
   program name is `WoWDataStudio-Setup-...exe`, then click *Run anyway*. Only do this for an installer you got from
   the project itself. Your antivirus may also scan it; that is normal.
3. The installer asks no administrator password: it installs for your Windows user only.
4. *License Agreement*: the app is free software under the MIT licence. Choose *I accept the agreement*, then *Next*.
5. *Select Destination Location*: by default `%LOCALAPPDATA%\Programs\WoW Data Studio`. Keep it, or pick another
   folder. *Next*.
6. *Select Additional Tasks*: tick *Create a desktop shortcut* if you want one (a Start-menu entry is always created),
   and *Add the command line tool to PATH* if you want to use [the command line](command-line.md) `wds` in every command
   prompt (for your Windows user only; uninstalling removes it again). *Next*, then *Install*.
7. On the last page, keep *Launch WoW Data Studio* ticked and click *Finish*.

What gets installed (all in the install folder):

| Folder or file | What it is |
|---|---|
| `bin\` | the app itself (`WoWDataStudio.exe`) and its command line (`wds.exe`, the same version: see [The command line](command-line.md)) |
| `scripts\` | example scripts for the command line (PowerShell and batch) |
| `manual\` | this manual, as web pages (it opens in your browser) |
| `blender\wowdata_blender.zip` | the Blender add-on, for [Models and Blender](model-export-import.md) |
| `docs\LOOSE_FILES_PATCH.md` | technical notes about the game's loose-file mode |
| `docs\NOGGIT.md` | where to get the map editor Noggit and how to point the app at it |
| `LICENSE`, `THIRD_PARTY.md` | the licence of the app and of the libraries it uses |

The installer *never touches your World of Warcraft folder*. It does not install the map editor Noggit (a separate
program under the GPL) or the server tools; the [Maps and terrain](maps-and-terrain.md) chapter and `docs\NOGGIT.md`
explain how to use them.

*Updating:* run the installer of the new version. It replaces the program files and keeps your settings, your index
and your edits. *Uninstalling:* Windows *Settings > Apps > Installed apps > WoW Data Studio > Uninstall* (or the
*Uninstall WoW Data Studio* entry in the Start menu). Uninstalling removes the program files only; your edits and
settings stay in `%APPDATA%\WoWDataStudio` (see [Troubleshooting](troubleshooting.md#where-the-app-keeps-its-files)
if you want to delete them too).

## First start: choose your World of Warcraft folder

The first time, the app shows **Welcome to WoW Data Studio**.

![The welcome screen](../screenshots/01_welcome.png)

1. Under **Select your World of Warcraft folder**, click **Browse...** and pick the folder that contains `WoW.exe`
   and the `Data` folder. You can also paste the path into the box. Picking the `Data` folder or `WoW.exe` itself
   works too: the app goes to the right folder.
2. Click **Load game data**.

The app checks the folder first. If something is wrong, a red box says what, in plain words:

![A folder that is not a game folder](../screenshots/02_welcome_error.png)

| Message | What to do |
|---|---|
| *This folder does not contain WoW.exe.* | You picked the wrong folder. Pick the main game folder. |
| *This is World of Warcraft x.y.z ... works with version 1.12.1 (build 5875) only* | This is another game version. Use a 1.12.1 client. |
| *WoW.exe was found, but the Data folder next to it is missing.* | The installation is incomplete or moved. |
| *The Data folder is missing game archives: ...* | Some `.MPQ` files are missing. Reinstall or copy the game again. |

The game folder is only read. Nothing in it changes unless you later click one of the install buttons
(see [Testing changes in the game](testing-in-game.md) and [Releasing](releasing.md)).

The app remembers the folder. To change it later, use **File > Change World of Warcraft folder...** or
[Settings](settings.md).

## Organizing game data

The first time you open a game folder, the app reads the game's archives and builds its own index, so every search
afterwards is instant. A list of stages and a progress bar show what it is doing; it takes about 5 to 10 seconds.

![Organizing game data](../screenshots/03_organizing.png)

* The next start opens instantly: the index is kept on your computer.
* The app rebuilds the index by itself when the game's archives change (for example after you install a new
  `patch-3.MPQ`).
* To force a rebuild, use **File > Rebuild the index**. Your changes are kept.

When it is done, the main window opens. Continue with [The main window](main-window.md).

## Names from the server database (optional)

Creatures, items and game objects have their names in the *server's* database, not in the game client. Without it,
the app lists them by model and id, for example *Boar - BoarSkinIvory* or *creature display 503*. With it, you see
*Mottled Boar*.

The top bar shows the state: **Names from server database: connected** (green dot) or
**Names from server database: not connected** (grey dot). Click it to open the settings.

To connect:

1. Start your local server's database (MySQL), the one the game server uses.
2. Open **File > Settings...** (or the **Settings** button at the top right).
3. Under **Names from the server database (optional)**, tick **Use the server database for names**.
4. Fill in **Server**, **Port**, **User**, **Password** and **World database**, or click
   **Use the defaults (127.0.0.1:3306, root/root, mangos)**.
5. Click **Test connection**. It should say *Connected: ...*.
6. Tick **Reload the names from the database when I click Save** and click **Save**.

The app only reads from the database; it never changes it. Your password is saved encrypted for your Windows user.
If the database is started later, use **File > Reload names from the server database**.
All fields are explained in [Settings](settings.md).

The same connection opens the **Server data** tab, where you can look at and edit the server's items, creatures,
quests and more ([Server data](server-data.md)).

## Next steps

* Take [the tour of the main window](main-window.md).
* Prefer scripts? Everything also works on [the command line](command-line.md): open *WoW Data Studio command prompt* in the
  Start menu and type `wds --help`.
* Jump straight to a task: [find a creature](finding-things.md), [repaint its skin](editing-textures.md),
  [see it in the game](testing-in-game.md).
