# Map editing workflow (1.12.1)

Edit terrain in our fixed Noggit, check the result with `wowdata`, put it in the game, then update the server.

**In WoW Data Studio (0.5.0 and later) all of this is one guided workflow in the Maps tab**: Edit in Noggit (client view +
project + bookmark), Import map changes (the C# port of the validator and diff below, with a 3D before/after), Test map in
game / Build release patch, Update server maps (reextract.ps1 + swap_in.ps1 with progress, report, backup and undo). See
the user manual chapter `docs\manual\maps-and-terrain.md`. The steps below are the command-line way.

```
Noggit (fixed fork) ──save──► project folder\World\Maps\<map>\...      (never the real Client folder)
      │
      ▼
wowdata map validate / map diff  ──► fix anything reported as an error
      │
      ▼
install: loose files (testing) or patch-3.MPQ (release)  ──► restart the game
      │
      ▼
server\reextract.ps1 (maps, vmaps, mmaps into staging) ──► stop mangosd ──► swap step ──► start mangosd
```

## 1. Edit in Noggit (the fixed fork)

* Program: `<WoW Custom Client folder>\third_party\noggit-build\bin\noggit.exe` (the folder that holds `WoWDataTool`), built from our fork
  `third_party\noggit-cinder-fork` (see its `BUILD_NOTES.md` and `FORK_CHANGES.md`). **Never use the upstream
  prebuilt release** (it has the auto-updater and the vanilla bugs).
* Project: open **Vanilla112** in the start window. It points at a **copy** of the client
  (`third_party\noggit-test\Client`), so Noggit can never write into the real `Client` folder. Noggit saves edited
  files into the project folder `third_party\noggit-test\projects\Vanilla112\World\Maps\<map>\`.
* What the fork fixed for 1.12 (details in `FORK_CHANGES.md`): WDL layout, 52-byte sound emitters, clean MCLQ water,
  4-bit alpha only, a plain listfile in exported patches, no auto-updater.
* **Disabled for 1.12 on purpose:** the Area (AreaTable) editor, the SoundEntries editor and the Light/sky editor:
  they would corrupt the 1.12 DBCs. Painting area IDs on terrain still works. Edit DBCs with our own tool (later)
  and copy them to the server too.
* Don't use "Fix all UIDs" or the patch exporter's automatic slot naming on the real client; when the UID dialog
  appears on an untouched Blizzard map, "Get max UID" is the safe choice.
* Run Assist > **Check loaded tiles for 1.12 issues** before saving; it now also flags 8-bit alpha.

## 2. Validate and diff

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool"
$proj = (Resolve-Path "..\third_party\noggit-test\projects\Vanilla112").Path

.\wowdata.cmd map validate "$proj\World\Maps"             # every saved ADT/WDT/WDL, grouped per map
.\wowdata.cmd map diff "$proj\World\Maps\Azeroth"          # each saved file against the client's original
.\wowdata.cmd map diff OLD.adt NEW.adt                     # any two files (disk paths or archive paths)
```

* `validate` checks what the 1.12 client's loaders read (rules and evidence: `wowdata\maps\validate.py`): chunk
  positions and offsets, MCNK header and sub-chunks, 4-bit alpha (2048 bytes per layer, at most 4 layers), MCLQ
  layout (804 bytes per liquid layer, no stray tile bits), 52-byte MCSE records, MCRF/MDDF/MODF references in
  range, the WDL offset table at file offset 20, the positional WDT, and no WotLK-only chunks or flags.
  * **error** = the client would read garbage or crash: do not install. **warning** = differs from every stock
    file (usually a writer bug); look at it. **info** = harmless.
  * All 2,515 stock map files pass with no error and no warning: `.\wowdata.cmd map validate --all-client`.
  * Exit code 1 when there is an error, so it can gate scripts.
* `diff` compares content, not bytes: heights (count and max change), normals, texture layers by name, alpha
  texels, shadows, doodad/WMO placements by unique id, liquids, sound emitters; WDL heights per tile; WDT tiles and
  flags. Offsets that only moved are hidden unless `--layout` is given. Exit code 1 when content differs.

### Round-trip test (do this once after every fork update)

Noggit is a GUI and cannot be driven headlessly, so this test is manual:
1. In Noggit, open the Vanilla112 project, open **Eastern Kingdoms**, fly to an area with water, a town and texture
   blends (e.g. Elwynn / Goldshire), let the tiles load, change **nothing**, and save (Ctrl+S / "Save all"). If
   Noggit saves only changed tiles, make a tiny edit on one tile and undo it before saving, so it is marked changed.
2. Run:
   ```powershell
   .\wowdata.cmd map validate "$proj\World\Maps\Azeroth"
   .\wowdata.cmd map diff "$proj\World\Maps\Azeroth" --layout
   ```
3. Expected: validate = PASS with no warnings. The diff should show no height, layer, placement, liquid or emitter
   changes. Known, expected differences: name tables re-sorted (`--layout` only), `.MDX` written as `.M2` (normalised
   by the diff), MODF last field 1024 (the client ignores it). **Watch for** `MCAL alpha layer ... texels changed`:
   that is the alpha re-quantisation the review suspected (review 3.4 F). Small (1-step) changes only where layers
   almost cover each other are cosmetic; report anything larger.
4. The WDL is rewritten on every save: `map diff` of the `.wdl` should show only small height changes (Noggit
   regenerates it from the tiles) and validate must PASS (before fix 1 it failed with WDL-POS).

## 3. Install into the game

* **Testing (loose files):** copy the saved files into `Client\Data\World\Maps\<map>\` (needs the loose-file client
  patch, `docs\LOOSE_FILES_PATCH.md`). Easiest: copy them into `WoWDataTool\staging\World\Maps\<map>\` and run
  `.\wowdata.cmd loose install` (undo: `loose remove`).
* **Release (patch-3.MPQ):** put the files in `staging\World\Maps\<map>\`, then `.\wowdata.cmd pack --install`.
  (Noggit's own patch exporter also works, but point it only at the client copy; we use `patch-3.MPQ` by decision.)
* Restart the game after installing. Check the far horizon too (that is the WDL).

## 4. Re-extract the server data and restart the server

The server never reads the client's MPQs at run time: it uses its own extracted `Server\data\maps`, `vmaps`, `mmaps`.
After a terrain, water or object change they must be regenerated, or creatures walk on the old ground and line of
sight/pathing use the old world.

See `WoWDataTool\server\README.md` for details. In short:

```powershell
cd "<your WoW Custom Client folder>\WoWDataTool\server"
.\reextract.ps1 -Steps maps,vmaps            # all maps into server\staging (about 1.5 min) + staging\changes.txt
.\reextract.ps1 -Steps mmaps -Maps 0         # navmesh for the changed map-0 tiles + their 8 neighbours (1-3 min/tile)
.\swap_in.ps1 -DryRun                        # what would be replaced (allowed while the server runs)
# stop the world server (close the mangosd console window), then:
.\swap_in.ps1 -Kinds maps,vmaps,mmaps -Maps 0   # backs up, copies only the changed files into Server\data
# start the world server again: Server\"Start mangosd 16bit.bat"
# undo if needed (server stopped): .\swap_in.ps1 -Restore backups\swap_YYYYMMDD_HHMMSS
```

* The extractors read the client in the game's order **including `patch-3.MPQ`** (our one-line mapextractor patch;
  vmapextractor already scans patch-N), so install the patch (section 3) before re-extracting.
* `changes.txt` labels each differing file `edit` (comes from your change) or `drift` (only an extractor-version
  difference). The very first vmaps swap also brings 982 `drift` files (a lean-to model the old extractor missed);
  that set is consistent and safe to install. Live `.map` files are reproduced byte for byte.
* The swap refuses to run while `mangosd.exe` is running and keeps a verified backup of every replaced file.
* DBC changes the server also reads (Map, AreaTable, ...): `.\reextract.ps1 -Steps dbc`, then
  `.\swap_in.ps1 -Kinds dbc -DbcFiles Map.dbc,AreaTable.dbc`. **Never** install Spell.dbc / SkillLineAbility.dbc from
  the client (the server's copies are deliberate edits).
* Details, verification and caveats (mixed navmesh generations at the rebuild border): `server\README.md`.
