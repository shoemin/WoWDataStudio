# Hot reload: the running game reads changed files again

Status (2026-10-14): **app side done** (WoW Data Studio 0.11.0: **Reload in running game**, `wds changes reload`, the
Settings switch). **Game side staged** as `WoWDecomp\work\staged\HR1-hotreload` (a patch to `hooks\` of the client
project: `hooks\hotreload.c`); it is merged by a wave of that project, which runs the regression and the scenarios on it.
Until a client with HR1 is deployed, the app says *The running game has no hot-reload support*. Tested so far: the C
code in an offline harness that fakes the client's memory at the real addresses (51 checks, in the three ways the
per-frame entry can look), the app against a stand-in game and against that harness. The in-game test
(`WoWDataTool\tests\ingame\hotreload`) runs after the wave that merges HR1.

This is research section 5, method C of `WoWDecomp\docs\MODEL_TOOL_RESEARCH.md`: loose files (method B) plus
`SFile2_ResetFileMap` plus evicting the cached copies of the changed assets.

## 1. Off by default

`wowcode.dll` does nothing for hot reload unless the file `wowhotreload.txt` next to it (the game folder, next to
`WoW.exe`) has the line `enabled 1`. The DLL reads it once, when the game starts. Without it there is no thread, no pipe
and no code patch; `wowcode.log` has one line `hotreload: off (...)`. WoW Data Studio writes and deletes the file
(**Switch on hot reload for this game folder** in Settings; `wds settings set hot-reload on|off --yes`). A normal
player's client never has `wowcode.dll`; the regression clones of the client project never have the file.

The file also has four kill switches, all `1` by default (set one to `0` if that kind of live update ever misbehaves):
`units` (creatures and characters rebuild their model), `gameobjects` (objects swap their model), `restream` (terrain,
buildings and placed doodads around the player are reloaded), `dbc` (data tables are updated in place).

## 2. Trigger and protocol

The DLL serves the named pipe `\\.\pipe\wowcode-hotreload-<pid>` (local clients only, one request at a time). The app
finds the game as the `WoW.exe` process whose program file is in the game folder, and checks that the pipe exists by
listing the pipe folder (opening it would take the game's one connection).

A request is UTF-8 text lines and ends with the line `end`:

```
HOTRELOAD 1
reload                                    (or: status   or: release patch-3.MPQ)
remount patch-3.MPQ                       (optional: re-open a released or newly installed Data\patch-?.MPQ first)
file Creature\Boar\BoarSkinIvory.blp      (repeatable: archive paths)
wait 15000                                (how long the pipe waits for the game's main thread, 1000-120000 ms)
end
```

The reply is two JSON lines: `{"accepted":true,"protocol":1,"id":N}` (or `"accepted":false` with `"error"`), then the
report. A client that does not want the report may close after the first line (`wds changes reload` without `--wait`).
The report of a reload:

```
{"protocol":1,"id":2,"command":"reload","ok":true,"pid":1234,"elapsedMs":40,"frames":1,"inWorld":true,"looseFiles":true,
 "archive":{"name":"patch-3.MPQ","result":"remounted","detail":"..."},          (only with remount)
 "fileMap":"rescanned","restreamed":true,"restreamReason":"Kalimdor_32_32.adt changed",
 "files":[{"path":"Creature\\Boar\\BoarSkinIvory.blp","kind":"texture","result":"reloaded","detail":"2 copy(ies) re-uploaded in place"}, ...],
 "summary":{"reloaded":3,"freshOnNextUse":1,"needsRelog":1,"needsRestart":1,"failed":0}}
```

`result` per file: `reloaded` (live now), `not-in-memory` (the game had not loaded it: it reads the new file when it
needs it), `next-use`, `needs-relog`, `needs-ui-reload` (`/reload`), `needs-restart`, `busy` (still loading after 5 s),
`failed`. `status` reports `mainThread`, `inWorld`, `looseFiles` (the loose-file mode), the released archives and the
options. `release` answers `released`, `not-mounted` or `refused` (with the reason).

## 3. The safe point

All game work runs on the game's **main thread at the top of the main loop's per-frame update**:
`EvtSched_DispatchUpdateEvent` (00420f70), called once per iteration of `EvtSched_RunContextLoop` (00420c00), after the
input and poll events and before the update / render handlers. World loading (`CMap_Load` + `Async_WaitAll`) and
rendering happen inside those handlers, so at this point nothing is half loaded or being drawn, and the async loader
completions (`Async_PumpCompletions`) have run. The entry is detoured after the hook table is installed: when the
function is hooked (E9, or the hot-patch form EB F9 + E9 in the padding) the detour chains to the recompiled function,
otherwise a trampoline runs the 6 original prologue bytes (`55 8B EC 83 EC 0C`, checked). The pipe thread only queues
the request and waits; if the main thread does not take it within the `wait` time, it is cancelled (nothing changed).

## 4. What is reloaded, and why it is safe

| Kind | What the DLL does (decompile) | Live? |
|---|---|---|
| `Data\patch-?.MPQ` (`release` / `remount`) | `SFile2_CloseArchive` of the archive and its `speech2` sub-archive, only while Storm's refcount says no file of it is open (`ARCHIVEREC+0x38` = 1 + sub-archives, under `g_SFileArchiveLock`): the OS handle closes, the app replaces the file; `SFile2_OpenArchive` again at the same slot and priority (`+0x148`), the sub-archive re-opened; a new one gets a free slot of `g_MpqArchiveHandles` above every priority. A released archive not re-mounted within 60 s is re-mounted by the DLL. Only `patch-3..9 / A..Z` can be released. | yes |
| loose-file map | only when a requested loose file was added or removed (`SFile2_ResolvePath` with the loose bit disagrees with the disk): `SFile2_ResetFileMap` + `SFile2_BuildFileMap` back to back (the game does the same reset after saving SavedVariables); a changed loose file needs nothing, the map holds only its path. New and removed loose files are then seen by every later open | yes |
| textures (`.blp`) | every `CTexture` of `g_TextureList` with that name whose `CGxTex` is a BLP file texture (callback `Texture_LoadBlpNow`): when the new file has the same size and pixel format (`Texture_ReadBlpInfo` against the texture's +0x144/+0x148/+0x118 and the no-alpha flag), the decoded chain is uploaded through `GxTexUpdateRect` into the SAME texture object: no pointer anywhere changes (the game's own device-lost path). Textures still loading are retried on the next frames. | yes, same size/format |
| models (`.m2`; in 1.12 the views, the *skins* of later clients, are inside the `.m2`: there are no `.skin` files, and skin *textures* are textures) | the `M2Shared` is unlinked from `g_M2Cache`'s hash (both links zeroed; loading entries are never touched): models keep it alive by refcount, `M2Shared_Release` frees it when the last one goes. Units and players re-create their model through the display-change path (`CGUnit_C_UpdateDisplayModel` with a copy of the display record that differs in `ExtendedDisplayInfoID`; `CGUnit_C_LoadDisplayRecords` puts the real pointer back), game objects get a new model (`M2Scene_LoadModel` + `CGObject_C_SetModel` + their callbacks), placed doodads by the restream. | yes (UI model views, sky, missiles: relog) |
| buildings (`.wmo`) | the root in `g_MapObjCacheTable` gets a tombstone hash (the cache compares only the 32-bit hash; links are untouched because both free paths unlink them), then the restream re-creates the placements | yes (held by a game object: relog) |
| terrain (`.adt`) | `CMap_FlushForRestream` (00694920), the engine's same-map teleport path: frees the loaded areas, purges unused WMO roots, reloads synchronously around the player; `g_CameraMapObjGroup` cleared as `World_ClearMapObjects` does. Every file is read first (a bad map file would be a fatal error in `CMap_SafeOpen`). | yes |
| `.wdt`, `.wdl` | read only by `CMap_Load` | relog |
| tables (`.dbc`) | the table's own loader (`ClientDB_<T>_Load`, 144 of them, `hooks\hotreload_dbc.h` generated from the decompile) reads the new file into a scratch `WowClientDB`; when the count, the highest ID and every ID are the same, each live row (same address) gets the new row's bytes. The new string block is kept for good (and the old one too), so a cached `char *` stays valid. The file is checked first exactly as the loader will (a bad file would be an error box). Display tables (CreatureDisplayInfo, -Extra, CreatureModelData, CreatureSoundData, ItemDisplayInfo, HelmetGeosetVisData, CharHairGeosets, ItemVisuals, ItemVisualEffects) then rebuild every unit in view; GameObjectDisplayInfo every game object. | most; TaxiNodes/TaxiPath/WorldMap*: relog; 21 tables with data derived at start-up, the 6 whose memory row differs from the file row and the 5 loaded inline (AreaTable ...): restart; rows added/removed: restart |
| `.lua`, `.xml`, `.toc` | (FrameXML is loaded when the interface loads) | `/reload` |
| sounds | read when they play (a sound in the game's sound cache may play the old version) | next use |

Nothing is ever freed while something may still point to it: the old model data, building roots and string blocks are
released by the game's own refcounting or kept. The restream and the unit rebuild are code paths the game runs itself
(same-map teleports, transports, cinematics; display changes, polymorph). The race the game itself accepts: FMOD's
stream thread may open a sound file while the loose-file map is rebuilt (the game resets the map the same way after
saving SavedVariables).

## 5. The app

`WoWData.Core\HotReload`: `HotReloadFlag` (the switch, the DLL's marker `WOWCODE_HOTRELOAD_PROTOCOL_1`, the log),
`WindowsGameLocator` (the process and the pipe), `HotReloadProtocol` (requests, replies, the pipe client),
`HotReloadService` (`ProbeAsync`: the state and why; `ReloadAsync`; `ReleaseAsync`; `ChangePatchLiveAsync` for
installing or uninstalling the release patch while the game runs), `FakeGame` (a stand-in game for the tests and the
screenshots). The app (**Reload in running game**, the offer after an install, the state line, the Settings switch) and
`wds changes reload` use the same code.

States the app explains: no game from the folder; hot reload off for the folder (the DLL has the marker, no
`wowhotreload.txt`); started before it was switched on (`wowcode.log` of that process says `hotreload: off`); no support
(no `wowcode.dll`, or one without the marker); not answering (the pipe is there but the main thread did not answer).

## 6. Tests

* Game side, offline: `WoWDecomp\work\staged\HR1-hotreload\test\build_harness.bat` then
  `hr_harness.exe test orig|hooked|hotpatch` (51 checks each); `hr_harness.exe serve 60` is a fake game for other clients.
* App: `app\WoWData.Tests\HotReloadTests.cs` (protocol against the native code's real replies, the switch, every state,
  reloads, the release patch while a stand-in game holds it open, the command line). With `WDS_HR_HARNESS` set to
  `hr_harness.exe`, the app also talks to the real C code.
* In the game (after the wave with HR1): `WoWDataTool\tests\ingame\hotreload\run_hotreload.ps1` (texture + model change in
  `Client_visual`, reload, screenshots before and after without a restart).
