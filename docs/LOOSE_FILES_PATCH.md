# Loose-file mode: client patch

Status: **applied** from update wave185 (the line below is in `WoWDecomp\hooks\wowpatches.txt`, added by the project's
wave pipeline, not by WoWDataTool). Confirmed in the game by the visual test of 2026-10-10
(`WoWDataTool\tests\ingame\results\20261010_2254\REPORT.md`): edits delivered as loose files and as `patch-3.MPQ` both
showed in the game (10 of 11 change types passed; the one failure was the `_s` ground-texture bug of the app, fixed in
WoW Data Studio 0.6.1). The rest of this document is the proposal as it was written, kept for the reasoning.

## TL;DR

One line for `WoWDecomp\hooks\wowpatches.txt`:

```
# Loose files for WoWDataTool (docs: WoWDataTool\docs\LOOSE_FILES_PATCH.md). App_InitDataDirectories (00402210) calls
# SFile2_SetLooseFileFlags(0) at start-up, which turns off the loose-file preference that Storm has on by default.
# PUSH 0 -> PUSH 1: Data\<archive path> files on disk win over every MPQ.
0040221f  6a01  6a00
```

It fits the existing byte-patch mechanism as is; no hook and no C change are needed.

## 1. What the client does today (from our decompile)

### Start-up switches the mode off

`App_InitDataDirectories` @00402210 is called by `WowMain`. Its original code (`python tools\py\disasm.py 0x402210`; bytes checked in `Client\WoW.exe`):

```
00402210  55                 push ebp
00402211  8b ec              mov  ebp, esp
00402213  81 ec 04 01 00 00  sub  esp, 0x104
00402219  56                 push esi
0040221a  e8 01 6a 24 00     call 0x648c20      ; SFile2_DisableMpqLooseFiles   (g 0x865b44 = 0)
0040221f  6a 00              push 0             ; <-- the patch: 6a 01
00402221  e8 ba 69 24 00     call 0x648be0      ; SFile2_SetLooseFileFlags(0)
00402226  b9 01 00 00 00     mov  ecx, 1
0040222b  e8 f0 79 01 00     call 0x419c20      ; App_GetCommandLineSwitchValue(1) = -datadir
...                                             ; SetRootDirectory(exe folder), SetDataDirectory("Data\")
```

* `SFile2_SetLooseFileFlags(flags)` @00648be0: `g_SFile2LooseFlags` (0x865b40) = `flags & 3`, then `SFile_SetLooseFileFlags(flags)` @00654850 stores the same value in the Storm layer (`g_SFileLooseFlags`, 0x866534).
* The **static initial values** in `WoW.exe` are `0x865b40 = 1` and `0x865b44 = 1`: Storm ships with loose files ON. WoW turns them off at start-up. The patch only keeps the first one on.

### How a file is resolved

`SFile2_OpenFile` @006477c0 (152 callers: every DBC loader, `M2Cache_GetShared`, the texture loaders, ...) calls `SFile2_ResolvePath` @00647e60:

1. `resolveFlags = callerFlags | g_SFile2LooseFlags`.
2. The first call of the session builds the loose-file map (`SFile2_BuildFileMap` @00646ea0). **This already happens in the unpatched client.** It scans:
   * the client folder (`<Client>\`) recursively, **skipping its `Data` folder**; keys are paths relative to `<Client>\`;
   * then `<Client>\Data\` recursively; keys are paths relative to `Data\`;
   * an optional second root (`SFile2_SetSecondRootDirectory`, never set).

   For a duplicate key, the first one wins (`SFile2_ScanDirectoryIntoFileMap` @00646910 frees the newer path), so `<Client>\X` beats `<Client>\Data\X`. A name ending in `.bz` becomes kind 1 (a zlib stream with a `BZ00` header) with the extension stripped. A name ending in `.MPQ` becomes kind 2 (a single-file archive), also with the extension stripped.
3. If `resolveFlags & 1`: the requested path (e.g. `Creature\Boar\BoarSkinIvory.blp`) is looked up in the map, using `SStrHash` and `SStrCmpI`. The lookup is case-insensitive and treats `/` as `\`. On a hit, the client opens that disk file with `fopen` (kind 0) and **no archive is consulted**.
   (`& 2` would first try the bare file name, which is collision-prone. Not proposed.)
4. Otherwise it searches the MPQs (`SFile_HasFile` → `SFile_LocateFile` @006549a0, in priority order). `flags & 3` is cleared first, because `g_SFile2MpqLooseEnabled` is 0, so the Storm layer itself never probes the disk.

Today only callers that pass flag 1 themselves get step 3. These are the UI loaders (FrameXML/AddOns), `ConfigFile_Load` (Config.wtf, realmlist.wtf) and a few others. Models, textures, DBCs, maps and sounds (flag 0) never use loose files.

## 2. The patch

| | |
|---|---|
| Address | `0040221f` (inside `App_InitDataDirectories` @00402210) |
| Original bytes | `6a 00` (`push 0`) |
| New bytes | `6a 01` (`push 1`) |
| wowpatches.txt line | `0040221f  6a01  6a00` |

(A 1-byte form `00402220 01 00` is equivalent. The 2-byte form shows the instruction.)

### Why it fits `wowpatches.txt` without a hook

* `hooks.c` (`apply_patch_file`) applies `wowpatches.txt` at every start, before any hook is installed. It verifies the original bytes, so on any other build the patch is refused and logged, never misapplied.
* **`App_InitDataDirectories` is not hooked.** It is not in `hooktable.c` and is listed in `hooks\excluded.txt` (difftest note). Even if that exclusion is removed later, `tools\py\gen_hooktable.py` (wave CL1 rule) never hooks a function whose body contains a `wowpatches.txt` byte. So the patch can never be silently bypassed by a recompiled copy.
* The hooked, recompiled `WowMain__impl` calls `App_InitDataDirectories` through its fixed address. In `work\out60\include\wow_protos.h`: `#define App_InitDataDirectories ((void (__stdcall *)(void))0x00402210)`. So the patched original code runs.
* The callee `SFile2_SetLooseFileFlags` (00648be0) **is** hooked. Its recompiled body (`work\out60\src\wow_0069.c`) does exactly what the original does with the value 1: it stores 1 in `g_SFile2LooseFlags` and calls `SFile_SetLooseFileFlags(1)` (also hooked; it stores the value).

## 3. Expected behaviour after the patch

* **Where loose files go:** `Client\Data\<archive path>`, mirroring the path inside the MPQ. For example, `Client\Data\Creature\Boar\BoarSkinIvory.blp` replaces the Mottled Boar skin from `texture.MPQ`. `wowdata loose install` writes exactly there.
  * `Client\<archive path>` also works, and beats the Data copy. Do not use both.
* **Precedence:** a loose file wins over **every** archive: `patch-3.MPQ`, `patch-2.MPQ`, `patch.MPQ` and the base archives. It also wins over a delete marker: `patch.MPQ` hides 5,708 old files, and a loose copy brings such a path back.
* **Names:** the lookup is case-insensitive.
  * Models must be named `.m2`: `M2Cache_GetShared` renames `.mdx`/`.mdl` to `.m2` before the open, and a loose `.mdx` is never asked for.
* **All file types:** everything opened through `SFile2_OpenFile` can be overridden. That includes textures, models, DBCs (`DBFilesClient\*.dbc`), ADT/WDT/WMO and sounds.
  * For DBCs, keep the client and server copies identical. The server never sees loose files.
* **When changes are seen:** the map is built once, at the first file open of the session. `SFile2_ResetFileMap` rebuilds it only after SavedVariables / add-on saves (logout, `/reload`).
  * Even after a rebuild, already loaded models and textures stay in the client's caches.
  * **Rule: restart the game after adding, changing or removing loose files.** Hot reload is a later phase (ROADMAP).
* **Async reads:** loose files work with them. `SFile2_AsyncReadExecute` handles kind 0 with `fseek`/`fread`.
* **Scope:** only the client started with `wowcode.dll` (WoWLaunch / the test-bed tools) gets the patch. A plain `WoW.exe` keeps ignoring loose files.

### Effect when it was enabled: none until files are added

I listed what the map would index now:
* 393 files under `Client\` (outside Data): Interface\AddOns, WTF, WDB, Logs, Screenshots, crashes...;
* 24 under `Client\Data\`: the cinematics and the HTML/URL files.

**None of these 417 paths matches a file in the archives** (checked against all 79,733 loadable archive paths). So enabling the patch changes nothing until WoWDataTool (or someone) puts a file there.

## 4. Side effects and risks

1. **Storm-layer copy of the flag.** `push 1` also sets `g_SFileLooseFlags` to 1 (Storm's `SFile_*` API). It is read in only three places:
   * `SFile_GetEffectiveLooseFlags`, used by `LoadingScreen_LoadBackgroundTexture` @00406e20 as an existence check. It would also accept `<Client>\<path>` for a loading-screen texture. The texture itself still loads through SFile2.
   * `SFile_OpenArchiveFromFile` (no callers).
   * `SFileDda_BeginEx` (WAV streaming, no callers).

   All three are harmless. To avoid even that, use the 7-byte alternative instead. It writes only the SFile2 global and skips the call:
   ```
   0040221f  c605405b860001  6a00e8ba692400     ; MOV BYTE PTR [00865b40],1  (replaces PUSH 0 / CALL SFile2_SetLooseFileFlags)
   ```
   The Storm copy then keeps its static value 0, as today. I recommend the 1-byte `push 1`: it goes through the setter the code was designed with. The alternative is the fallback if anything shows up.
2. **The recompiled resolver path.** `SFile2_ResolvePath` is hooked, so the loose branch runs the recompiled code. That branch (flag bit 0) already runs every session for UI files and Config.wtf, so it is exercised code.
   * To rule it out when debugging, add `skip name SFile2_ResolvePath` to `wowhooks.txt` and compare with the original code.
3. **Stray files take effect silently.** Any file at `Client\Data\<archive path>` (or `Client\<archive path>`) overrides the game data. Unpacking an MPQ into `Data\` would make the client run from those files.
   * WoWDataTool records what it installs (`state\loose_manifest.json`), refuses to replace files it did not write, and `wowdata loose remove` cleans up.
4. **Performance:** one more hash lookup per file open. The directory scan already happens in the unpatched client, at the first open. Its cost grows with the number of files under the client folder, so keep big unrelated trees out of it.
5. **UI files are not affected.** FrameXML/GlueXML already load with the loose flag. `FrameXML_QuarantineLooseUiFiles` @0051fb50 still renames loose `Interface\FrameXML` / `Interface\GlueXML` folders and quarantines Blizzard add-on TOCs, exactly as before.
6. **Server consistency** is the user's responsibility, as with `patch-3.MPQ`:
   * DBC edits must be copied to `Server\data\5875\dbc`.
   * Model collision and terrain changes need server re-extraction.
   * Display-ID changes need the `WDB` cache cleared.

## 5. In-game test (done 2026-10-10, see the status at the top)

1. Add the line to `WoWDecomp\hooks\wowpatches.txt`, deploy (`tools\deploy_client.ps1`), and check `wowcode.log`. It should say `patched 2 byte(s) at 0040221f (original bytes verified)`.
2. In `WoWDataTool`, stage the tinted boar from the test run (`staging\Creature\Boar\BoarSkinIvory.blp`, green-tinted) and run `wowdata loose install`.
3. Start the game and go to the Valley of Trials (Mottled Boars, creature 3098, display 503). They should look green. The Elder Mottled Boar (display 193) shares the skin, so it changes too.
4. `wowdata loose remove`, restart: the boars are back to normal.
5. Optional, without the patch: `wowdata pack --install` builds `Data\patch-3.MPQ`. The same green boar must appear even with the loose-file line removed. This tests the MPQ path; remove the loose copy first, since loose files win.

What the in-game test answered:
* 1.12 does honour a `patch-3.MPQ` (the code said yes; one forum post said no): every change type showed from it;
* edited textures, models and terrain rendered as expected in both delivery forms (see the report's gallery).
