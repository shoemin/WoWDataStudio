# Noggit and WoW Data Studio

WoW Data Studio's **Maps** tab can prepare a map for **Noggit**, a free map editor for World of Warcraft, and import
what you save in it (see the user manual, chapter *Maps and terrain*).

**Noggit is not included in this installer.** Noggit is a separate program under the GNU General Public License,
version 3, built with Qt 5 (GNU LGPL). WoW Data Studio (MIT) only starts it and reads the files it saves; it contains no
Noggit code. Shipping Noggit's program would oblige this installer to carry Noggit's complete source code (and Qt's) for
every release, so it is kept a separate program that you build or get yourself.

## Which Noggit

Use the project's *fixed* Noggit for 1.12 (the "noggit-cinder" fork with the 1.12 fixes: far terrain, sound emitters,
water bits, 4-bit texture blending, no auto-updater). Never use the upstream download for this game version.

* Source code and licence: the fork's folder `third_party\noggit-cinder-fork` of the project (GPL-3, `COPYING`; the
  changes are listed in `FORK_CHANGES.md`); upstream: https://github.com/pkuzic/noggit-cinder
* How to build it on Windows: `third_party\noggit-cinder-fork\BUILD_NOTES.md` (Visual Studio 2022, Qt 5.15.2, CMake);
  the result is `third_party\noggit-build\bin\noggit.exe`.

## Pointing WoW Data Studio at it

WoW Data Studio finds `third_party\noggit-build\bin\noggit.exe` by itself when it runs next to the project. Otherwise
open **Settings**, section **Maps and terrain**, and set **Noggit program (noggit.exe)** with **Browse...**.

Noggit never gets your game folder: WoW Data Studio gives it its own view of the game's archives and a project folder in
WoW Data Studio's data folder, where Noggit saves your edits.

## Updating the server's maps

**Update server maps** uses the project's server tools (the `server` folder of WoWDataTool: `reextract.ps1`,
`swap_in.ps1`, Python and the built extractors). They are not included in this installer either; set their folder in
**Settings**, **Maps and terrain**, **Server tools folder**.
