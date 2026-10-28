# Server map re-extraction (maps / vmaps / mmaps / dbc)

When the client's terrain, WMOs, collision models or server-relevant DBCs change (our `Data\patch-3.MPQ`), the
world server must get matching data in `Server\data`. Otherwise creatures walk on the old ground, line of sight
goes through new walls, and pathing breaks. This folder contains:

* our own build of the vmangos extractors (`bin\`), with a one-line patch so mapextractor reads `patch-3.MPQ`;
* `reextract.ps1`, which extracts into a **staging** folder and writes a change report against the live data;
* `swap_in.ps1`, which **you** run after stopping mangosd. It installs only the changed files, keeps a
  verified backup and can restore it.

Nothing here writes into `Server\data` or the client, except `swap_in.ps1` run without `-DryRun`.

Licence: our scripts (`*.ps1`, `tools\*.py`) are MIT (SPDX header in each file) and contain no extractor code.
The extractors are GPL (vmangos / cmangos). They are built from `WoWDecomp\work\server_src` and only run as
separate programs. The patch-3 change (one line: `"patch-3.MPQ",` added to the extractor's MPQ list in
`contrib/extractor/System.cpp`) is applied by `tools\apply_patch3.py`; the patched GPL file and its backup stay on this
machine (`patches\`, not in the repository).

## Layout

| Path | What |
|---|---|
| `bin\` | `mapextractor.exe`, `vmapextractor.exe`, `vmap_assembler.exe`, `MoveMapGen.exe` + `ACE.dll`, `tbb*.dll` |
| `build\` | CMake/MSBuild tree (separate from the server's `work\server_build`) |
| `patches\` | `System.cpp.orig` (untouched original, written by `apply_patch3.py`); local only, not in the repository (GPL) |
| `baseline\` | `maps`, `vmaps`, `dbc` extracted from the **stock** client (no patch-3) with the same options; used to tell edits from extractor drift |
| `staging\` | output of `reextract.ps1` (created on demand; empty after this delivery) |
| `backups\` | `swap_<timestamp>\` folders written by `swap_in.ps1` |
| `tools\build_extractors.ps1` | builds the four extractors |
| `tools\apply_patch3.py` | the patch-3 change (binary-safe, keeps CRLF, idempotent) |
| `tools\compare_data.py` | the change report (size + SHA-1; edit/drift; touched navmesh tiles) |
| `tools\mapdiff.py`, `vmapdiff.py`, `mmapdiff.py`, `mmtileinfo.py` | section-level diff tools for `.map`, `.vmtile`, `.mmtile` |
| `tools\snapshot.py` | read-only folder snapshot (count, size, SHA-1 per file) used to prove nothing was touched |
| `tools\patch3_selftest.py`, `tools\swap_selftest.py` | end-to-end self-tests (fake client / fake server data only) |
| `verify\` | logs and results of the verification below |

## Build

Versions used (2026-10-10): Visual Studio 2022 Community, MSVC 19.44.35223 (toolset 14.44), its bundled
CMake 3.31.6-msvc6, Windows SDK 10.0.22621, ACE 6.5.25 and TBB 2020.3 from `WoWDecomp\work\libs` (the same
libraries `WoWDecomp\tools\build_server.ps1` uses). Source: `WoWDecomp\work\server_src` (vmangos, archived tree,
revision "unknown").

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool\server"
powershell -ExecutionPolicy Bypass -File tools\build_extractors.ps1      # -SkipConfigure for a rebuild only
```

What it does (equivalent manual commands):

```powershell
$cmake = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$w = (Resolve-Path '..\..').Path   # the WoW Custom Client folder (this runs in WoWDataTool\server)
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
python "$w\WoWDataTool\server\tools\apply_patch3.py" "$w\WoWDecomp\work\server_src" "$w\WoWDataTool\server\patches"
& $cmake -S "$w\WoWDecomp\work\server_src" -B "$w\WoWDataTool\server\build" -G 'Visual Studio 17 2022' -A x64 `
    "-DACE_ROOT=$w\WoWDecomp\work\libs\ace\ACE_wrappers" "-DTBB_ROOT_DIR=$w\WoWDecomp\work\libs\tbb\tbb" -DUSE_EXTRACTORS=1 `
    "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE=$w\WoWDataTool\server\bin" "-DCMAKE_LIBRARY_OUTPUT_DIRECTORY_RELEASE=$w\WoWDataTool\server\build\lib"
& $cmake --build "$w\WoWDataTool\server\build" --config Release --target mapextractor vmapextractor vmap_assembler MoveMapGen -- /m /nologo /v:m
# runtime DLLs next to the exes: ACE.dll (vmap_assembler, MoveMapGen) and tbb*.dll (MoveMapGen)
```

Notes:
* `server_src`'s top-level CMakeLists sets `CMAKE_RUNTIME_OUTPUT_DIRECTORY` to `server_src\bin`. The per-config
  `CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE` overrides it, so the exes land in `bin\` here and nothing goes into
  `server_src\bin` (checked).
* Only the four extractor targets and their libraries are built (no mangosd, no INSTALL target, nothing goes into
  `Server\`). The first build took a few minutes, and a re-run with nothing changed takes about 5 s. `build\` is 26 MB.
* The configure step rewrites `server_src\src\shared\revision.h` and `migrations_list.h` with identical content.
  The script restores the timestamp of `migrations_list.h` (the only one that changed), so the normal server build
  does not recompile because of us.
* ACE/TBB must be given as ABSOLUTE paths (relative ones make CMake report "requires TBB").

## The patch-3 change

`contrib\extractor\System.cpp`, `CONF_mpq_list`:

```diff
     "terrain.MPQ",
     "patch.MPQ",
     "patch-2.MPQ",
+    "patch-3.MPQ",
 };
```

* Lookup order: each opened archive is `push_front`ed into `gOpenArchives`, and file lookups walk it from the
  front, so the **last** list entry wins: patch-3 overrides everything.
* Missing archive: `LoadCommonMPQFiles` calls `FileExists()` first and silently skips a missing file, so the stock
  client (no patch-3) extracts exactly as before. Verified: the patched build reproduces the live `.map` files
  byte for byte without a patch-3.
* **End-to-end self-test** (`tools\patch3_selftest.py`; fake client folder, real client only read):
  `patch.MPQ` holds the Warsong Gulch WDT and the original ADT 29_29, and `patch-3.MPQ` holds the same ADT with
  every MCVT height +10. Without patch-3 the result is byte-identical to live `4892929.map`. With patch-3 every
  decoded height is exactly +10.000. **PASS.**
* Applied in `WoWDecomp\work\server_src` (CRLF kept). It does not affect the server build: mangosd does not
  compile `contrib` (`USE_EXTRACTORS=0` there).

vmapextractor (`vmapexport.cpp`) needs no change. It opens terrain, model, texture, wmo and base, then scans
`patch.MPQ`, `patch-2.MPQ` ... `patch-99.MPQ`. The scan does not stop at gaps, and each archive is pushed to the
front, so the highest number wins. Verified on the fake client: it opened `patch-3.MPQ` last (= searched first).
Upstream quirks, harmless here: `misc.MPQ` is formatted but never opened, and `dbc.MPQ` is not opened (the DBCs
come from patch.MPQ).

MoveMapGen (`contrib\mmap\src\generator.cpp`, verified in code):
`MoveMapGen <mapId> [--tile x,y] [--silent] [--offMeshInput f] [--configInputPath f] ...`.
* `x,y` = ADT x,y. Naming: `.map` and `.mmtile` = `MMMYYXX`, `.vmtile` = `MMM_XX_YY`, ADT = `Name_XX_YY.adt`.
* With `--silent` it returns **1 on success** (negative on errors) and never waits for a key.
* It reads `maps\`, `vmaps\`, `config.json` and `offmesh.txt` from its working folder, and writes only `mmaps\`
  there. It creates no log file.
* Every run rewrites `mmaps\MMM.mmap` (navmesh parameters). `maxTiles` comes from the tiles it discovers, so the
  working folder must hold the map's **complete** maps/vmaps. reextract.ps1 ensures that.

## reextract.ps1

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool\server"
.\reextract.ps1 -Steps maps,vmaps                 # terrain + collision for everything (about 1.5 min)
.\reextract.ps1 -Steps mmaps -Maps 0              # navmesh for the map-0 tiles whose input changed, + 8 neighbours
.\reextract.ps1 -Steps mmaps -Tiles 1:40,33       # explicit tile(s); 36:all = whole map
.\reextract.ps1 -Steps dbc                        # DBCs (report only; see the swap rules)
.\reextract.ps1 -Swap -DryRun                     # = swap_in.ps1 -DryRun
```

| Parameter | Default | Meaning |
|---|---|---|
| `-Client` | `..\..\Client` | client folder (needs `Data\*.MPQ`); path under 110 characters (mapextractor buffer) |
| `-Staging` | `.\staging` | all output; must not be inside `Server\data` or the client (checked) |
| `-Steps` | `maps` | any of `dbc, maps, vmaps, mmaps` (run in that order) |
| `-Maps` | all | map ids for the report, the mmaps tile list and the swap |
| `-Tiles` | auto | `map:x,y` or `map:all`; without it, mmaps = tiles whose maps/vmaps input differs from the baseline, + 8 neighbours |
| `-ServerData` | `..\..\Server\data` | only read (report; copied as mmaps input when staging has no maps/vmaps) |
| `-Baseline` | `.\baseline` | stock-client extraction, for edit/drift labels and touched tiles |
| `-Jobs` | 4 | parallel MoveMapGen processes (8 logical CPUs here; a continent tile takes about 1-3 min) |
| `-MapHeightAsInt` / `-VmapSmall` | off | **leave off**: the live data was made with `-f 0` and `-l` (see verification) |

Steps and what runs (working folder = staging; client passed by path):
* `dbc`: `mapextractor -i <client> -e 2` -> `staging\dbc`
* `maps`: `mapextractor -i <client> -e 1 -f 0` -> `staging\maps` (clears `staging\maps` and `staging\mmaps` first)
* `vmaps`: `vmapextractor -l -d <client>\Data`, then `vmap_assembler Buildings vmaps` -> `staging\Buildings`,
  `staging\vmaps`. Clears them first: the extractor refuses a "polluted" Buildings folder.
* `mmaps`: builds `staging\mmapwork\maps` (all tiles of each needed map, from `staging\maps` or a copy of the
  live maps) and `staging\mmapwork\vmaps` (from `staging\vmaps` or a robocopy of the live vmaps). Each parallel
  job gets `mmapwork\jobN` with junctions to those copies, its own `mmaps\`, and `config.json`/`offmesh.txt` from
  `server_src\contrib\mmap`. It then runs `MoveMapGen <map> --tile x,y --silent` per tile and collects the
  results into `staging\mmaps`. A run with only `mmaps` adds to `staging\mmaps`, so you can build in batches.
* mapextractor and vmapextractor have **no map filter**: they always extract everything (10-60 s each).
  `-Maps` narrows the report, the tiles and the swap.
* Tool output goes to `staging\logs\*.out.txt` / `*.err.txt`, and the run summary to
  `staging\reextract_run.json`.

**Change report** `staging\changes.txt` / `changes.json`, per kind:
* `changed` / `new` / `only_in_server` / `identical` vs `Server\data`, by size, then SHA-1;
* each changed/new file labelled `edit` (differs from the stock-client baseline, so it comes from our client data)
  or `drift` (same as the baseline, so the live file came from another extractor version);
* `touched_tiles`: tiles whose navmesh input changed vs the baseline. That covers `.map` differences, `.vmtile`
  spawn **geometry** (spawn IDs and BIH node indices are ignored, see below), and tiles that place a changed model.
  `mmap_rebuild_tiles` adds the 8 neighbours.

## swap_in.ps1 (you run it, with mangosd stopped)

```powershell
.\swap_in.ps1 -DryRun                              # plan only; allowed while mangosd runs (warns)
# stop the world server (close the mangosd console window), then:
.\swap_in.ps1 -Kinds maps,mmaps -Maps 0            # or no arguments = every kind present in staging (except dbc)
.\swap_in.ps1 -Kinds dbc -DbcFiles Map.dbc,AreaTable.dbc
Server\Start mangosd 16bit.bat                     # restart
.\swap_in.ps1 -Restore backups\swap_YYYYMMDD_HHMMSS # undo (also only with mangosd stopped)
```

1. It refuses (exit 2) if `mangosd.exe` is running and re-checks right before copying. `-DryRun` only warns.
2. It recomputes the change list from scratch (`staging\swap_plan_<time>.json`).
3. It backs up every file it will replace or remove to `backups\swap_<time>\<subfolder>\` and verifies the copies
   (SHA-1) before touching anything. `manifest.json` records old/new hashes.
4. It copies changed + new files (through a `.swaptmp` name, hash-verified). It removes map-specific
   maps/vmaps files that the fresh extraction no longer produces (`-NoRemove` keeps them). mmaps are never removed.
5. **vmaps** are installed only from a complete assembly (as many `.vmtree` as live). A `.vmtile` refers to the
   BIH node order of its map's `.vmtree`, so all differing files of a map must go in together. Copying every
   differing file does exactly that.
6. **dbc** files are copied only when named with `-DbcFiles`. The live `5875\dbc\Spell.dbc` and
   `SkillLineAbility.dbc` are server-side edits (see verification); never overwrite them with the client's.
7. `-Restore <folder>` puts replaced/removed files back and deletes files the swap added, if they are unchanged
   since.

Tested: the real install + restore path (`tools\swap_selftest.py`: a copy of the script with only the mangosd check
stubbed, against a fake server-data folder; replace, add, remove and restore all PASS). The real script refused
while mangosd was running. It was **not** run against `Server\data`.

## Verification (2026-10-10, unchanged stock client, no patch-3)

`Server\data` (12,502 files, 3,300,956,329 bytes) and `Client\Data` (24 files, 5,377,229,429 bytes) were
snapshotted before and after all work (SHA-1 of every file + size + mtime): **0 differences**
(`verify\serverdata_*.json`, `verify\clientdata_*.json`). In `server_src` only `contrib\extractor\System.cpp`
changed (the diff above).

| Data | Result vs live `Server\data` | Time |
|---|---|---|
| maps, all 2,429 `.map` (`-e 1 -f 0`) | **byte-identical, 2,429 / 2,429** | 10-32 s |
| maps with default `-f 1` | 499 identical; 1,930 differ only in the height section (int16/int8/flat storage vs float), area/liquid/holes identical; max decoded difference 0.015 | 11 s |
| dbc (`-e 2`, 158 files) | 156 byte-identical; `Spell.dbc` (10 bytes) and `SkillLineAbility.dbc` (client 5,067 rows, live 5,072) are server-side edits (live dated 2017/2018) | <1 s |
| vmaps (`-l`, 6,082 files, 575 MB) | 5,100 identical, 980 changed, 2 new, 0 missing; **every difference explained** (below) | 50 s + 14 s |
| vmaps with default `-s` | model files 192 MB vs 486 MB live, so the live data was made with `-l` | 49 s + 14 s |
| mmaps `.mmap` headers (maps 0, 33, 36, 489) | byte-identical | |
| mmaps, dungeon tile 36:31,32 | **equivalent** (verts, polys, detail meshes/tris bit-identical; differs only in the link section, which Detour rebuilds in `addTile` (DetourNavMesh.cpp 995-1010), in BV-tree bounds by 1 quantum and in header bmax.y by 0.0017) | 1 s |
| mmaps, full maps 36 + 33 (50 tiles) | vs live: 3 equivalent, 3 geometry-diff, 37 topology-diff, 7 tiles that live does not have | 115 s + 50 s |
| mmaps, continent tile 0:32,48 | topology differs (11,835 vs 11,283 polys) | 164 s |
| our per-tile vs our full-map build (same inputs) | equivalent (only the runtime link section differs), so per-tile builds are safe | |
| MoveMapGen determinism | same tile built twice: byte-identical | |
| `reextract.ps1 -Steps dbc,maps,vmaps` (all maps) | same as the baseline; report: 0 edits, 982 vmaps + 2 dbc drift | 86 s total |
| `reextract.ps1 -Steps mmaps -Tiles 36:all,0:32,48 -Jobs 4` | 37 tiles (8 empty) | 187 s |
| simulated edit (WSG 4892929.map +10 m), `-Steps mmaps -Maps 489` | touched 489:29,29, rebuilt 9 tiles, 489.mmap identical | 29 s |

**Why vmaps are not byte-identical.** Our (newer) vmapextractor also extracts `RazorFen Leanto03.m2`. The model
path contains a space, and the extractor that made the live data missed it. That adds 44 spawns (Barrens,
Razorfen Kraul map 47, Razorfen Downs map 129) and one game-object model entry. Spawn IDs are handed out globally
in encounter order (`GenerateUniqueObjectId`), so every later spawn's ID and the BIH node indices shift. Result:
41 `.vmtree`, 938 `.vmtile` and `temp_gameobject_models` differ. After removing the lean-to spawns and ignoring
IDs/node indices, **all 938 vmtiles have identical spawn geometry** (`tools\vmapdiff.py`). The first vmaps swap
therefore installs these 982 files: a consistent set that adds the lean-to collision. Later reports show only real
edits. The report labels them `drift`.

**Why mmaps differ.** The live navmesh is older than the live maps/vmaps (files dated 2021-05-31 vs 2022-01-17), and
no `config.json`/offmesh file exists anywhere in `Server`. It was built by an earlier MoveMapGen and/or from earlier
inputs, so byte identity is not reachable. Same format (MMAP v6, Detour v7, 64-bit poly refs) and identical
`.mmap` parameters. A small part of the difference also comes from the vmaps drift above: spawn order changes the
rasterization order (one dungeon tile: 2 polys out of 2,555).

## Caveats

* **Mixed navmesh generations.** A per-tile rebuild puts new-generator tiles next to old live tiles. Detour links
  tiles at their shared border edges, which may connect a little less perfectly where the two generations meet.
  The rebuild ring (+8 neighbours) keeps the seam away from the edit. Rebuilding a whole map once (`-Tiles 0:all`)
  makes it uniform. Expect hours for a continent (516 tiles on map 0, about 1-3 min each, 4 jobs).
* Swapping only `maps` for edited terrain without `mmaps` leaves the old navmesh: creatures path on the old ground.
  Swap the neighbours' mmtiles too.
* Re-make `baseline\` only from a client **without** patch-3:
  `.\reextract.ps1 -Steps dbc,maps,vmaps -Staging baseline`.
* DBCs the server also reads (Map, AreaTable, CreatureDisplayInfo, ...) must be installed with `-DbcFiles`. Never
  install Spell.dbc / SkillLineAbility.dbc from the client.
* Disk: staging needs about 1.2 GB for maps + vmaps (+ ~0.6 GB mmapwork), and the backups grow with each swap.
  `baseline\` is 0.9 GB.
* mangosd loads grid data when a grid is first used: restart it after a swap (no hot reload).
