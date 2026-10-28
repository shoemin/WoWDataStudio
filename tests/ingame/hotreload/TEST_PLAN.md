# Hot reload: test plan (WoW Data Studio 0.11.0 + wowcode.dll HR1)

The game side is staged in `WoWDecomp\work\staged\HR1-hotreload` and merged by a wave of the client project; the game was
not started for this change (waves were running). This plan says what was tested before the merge, what the wave's own
gates test, and the in-game test that runs after the wave that merges HR1.

## 1. Done before the merge (no game)

| Test | What it proves | Result |
|---|---|---|
| `WoWDecomp\work\staged\HR1-hotreload\test\hr_harness.exe test orig` / `hooked` / `hotpatch` (51 checks each) | the real `hooks\hotreload.c` in a fake game: the C code runs against the client's memory layout at the real addresses (texture list, M2 cache and scene, WMO cache, archive array, WowClientDB, object manager) with the client's calling conventions; the frame detour in all three forms the entry can have (original prologue, E9 hook, hot-patch hook), the pipe protocol and its errors, every reload path, the release / remount of patch-3.MPQ (refused while a file is open; the sub-archive; a new archive; an uninstalled one), cancelling when the main thread does not answer | 51/51 x 3 (also built with /O1 /std:c11, as the solution builds it) |
| scratch relink of the trace DLL (`build.bat trace` objects + the staged hooks files) | it compiles (no warnings at /W4 except the CRT's deprecations) and links into wowcode.dll | OK |
| `app\WoWData.Tests\HotReloadTests` | the app against the native code's real replies, a stand-in game over a real pipe, every state, the release patch while the stand-in holds it open, the command line; with `WDS_HR_HARNESS` the app against the real C code | all pass |

## 2. The wave that merges HR1 (its normal gates)

Without `wowhotreload.txt` (every regression clone) HR1 only logs `hotreload: off ...`: the regression, the stress runs,
the picture check and the scenario suite of `tools\regress.ps1` must stay exactly as before. That is the proof that it
"does not break the existing hooks or the test harness".

## 3. In the game, after that wave: `run_hotreload.ps1`

Client: the dedicated test client `Client_visual` (next to `WoWDataTool`) only (the script refuses another one).
Run it when no wave runs (it refuses while a chain log is active, a chain script or a console-mode server runs, or any
game is running) and the server is up (3306, 3724, 8085).

```
.\run_hotreload.ps1 -Run <run folder> -Plan                 # read-only: checks and the steps it would take
.\run_hotreload.ps1 -Run <run folder> -UseDecompDll         # the run; -UseDecompDll copies WoWDecomp\wowcode.dll (with HR1) in and back
```

Steps:

1. Safety checks (above) and the DLL check: Client_visual's (or WoWDecomp's) `wowcode.dll` must carry
   `WOWCODE_HOTRELOAD_PROTOCOL_1`.
2. Snapshots: `Client`, `Client1..6` (`snapshot.ps1`) and `Client_visual\Data`.
3. The edits, made the app's own way into the run's own studio home (`IngameRunner prepare ... 01 hr1 04`):
   `01` BoarSkinIvory.blp tinted bright green (texture), `hr1` Boar.m2 scaled 1.5x (creature model), `04`
   DurotarBush01.m2 scaled 4x (a doodad placed by the ADT).
4. `wds settings set hot-reload on --yes --client Client_visual` writes `Client_visual\wowhotreload.txt`.
5. Four sessions (`session.ps1`, the visual test's own launcher; the login is its autotest), each: the game starts,
   the scenario takes the BEFORE pictures (in-game `Screenshot()` plus an external `wowshot` series) and logs
   `hrphase install`; the script then runs `wds changes reload --status`, `wds changes install --yes <files>` and
   `wds changes reload --wait --json <files>` while the game keeps running; 30 s later the scenario takes the AFTER
   pictures and stays 20 s more (`hrphase done`); then the loose files are removed.

   | Case | Where | Files | Expected |
   |---|---|---|---|
   | boars, minimal hooks | 16 yd west of boar spawn 4691 | BoarSkinIvory.blp, Boar.m2 | both `reloaded`; the boars green and 1.5x in the AFTER pictures |
   | boars, all hooks | the same | the same | the same (the frame detour chains to the recompiled `EvtSched_DispatchUpdateEvent`) |
   | bush, minimal hooks | the start point, turned to the bush | DurotarBush01.m2 | `reloaded` (the restream); the bush 4x in AFTER |
   | bush, all hooks | the same | the same | the same |

6. Checks per session: the session ends with `SUCCESS` (no crash, no error dialog, no timeout); `wowcode.log` has
   `hotreload: on`, the reload line and no `EXCEPTION`; the scenario reached `hrphase done` after the reload; the
   report's results are as expected; the BEFORE and AFTER pictures differ.
7. Finally (always): every loose file removed, the switch off (`wowhotreload.txt` gone), the DLL put back, the test
   character restored (`restore_char.ps1`), the snapshots compared (0 differences in `Client`, `Client1..6`; `Client_visual\Data`
   as before), `REPORT.md` with the pictures side by side and the verdict.

## 4. By hand afterwards (optional)

* A data table: change a CreatureDisplayInfo row's scale (Data tables tab), **Test in game**, **Reload in running game**:
  the boars in view are rebuilt at the new scale. Add a row: the result says *restart*.
* The release patch while the game runs: **Build release patch (patch-3.MPQ)...** with **Also install it into the game:**:
  the message says *The running game was updated (hot reload)*; then **Uninstall release patch (patch-3.MPQ)**.
* A building (`.wmo`) and a terrain tile (`.adt`, Maps tab): the area reloads like a teleport.
* The states: start the game without the switch (*Hot reload is off*), switch on while it runs (*restart it once*).
