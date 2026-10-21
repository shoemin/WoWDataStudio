# Map conversion between versions: design (draft)

Design note, 2026-10-20, for the experimental multi-version work of WoW Data Studio. Status: milestones 1-7 are built
(5 and 6, the 1.60 to 1.12 conversion into the workspace: `app\WoWData.Core\Maps\Conversion\`, **Convert map...** in the
Other versions tab, `wds versions convert-map`); 8 and 9 are not.

## Goal and the user's decisions

* Long term: the project's recompiled 1.12 client reads the map and terrain data of the modern **1.60** Classic
  client natively. Until then, Data Studio **converts and exports** map data between versions.
* **Both directions**: 1.60 to 1.12 and 1.12 to 1.60. Later also 2.x (TBC) and 3.x (WotLK) to 1.60. So **1.60 is
  the hub**: every version gets a reader into one version-neutral model, and a writer out of it.
* Reference 1.60 build: the newest one found (today `_classic_beta_`, 1.60.1.70450).
* TBC/WotLK map import comes after 1.60.
* File names: a listfile only from a path the user chooses. Files without a known name get made-up paths
  (`World\Converted\<FileDataID>.<ext>`). Encryption keys found on disk are not used (maps do not need them).

## Architecture

```
 1.12 ADT/WDT/WDL ─┐                                   ┌─> 1.12 ADT/WDT/WDL (+ textures, DBC rows, server data)
 2.4.3 ADT ────────┤                                   │
 3.3.5a ADT ───────┼──> reader ──> NEUTRAL MAP ──> writer ┼─> 1.60 split ADT (root / obj0 / obj1 / tex0) + WDT
 1.60 split ADT ───┘        (one per version)  model    (one per version)  └─> exports: Blender, Noggit, a folder
```

* **Readers** fill the neutral model with everything the source has. **Writers** take what the target can show, and
  write a **conversion report**: every detail that was reduced or dropped, per tile and per kind (for example "alpha
  8-bit reduced to 4-bit on 212 chunks", "3 liquid types mapped to the nearest 1.12 kind", "41 doodads had no 1.12
  model and were skipped").
* Round trips are the tests: a stock 1.12 tile read and written back as 1.12 is byte-identical, as the existing
  `AdtDocument` already is. A 1.60 tile read and written as 1.60 has the same chunk contents.
* The readers are written so the recompiled client project can reuse their knowledge for native support later. The
  formats are documented in the code with the evidence for each field, as the 1.12 code is.

## The neutral model (a superset of every version)

Per **map**: name, directory, kind (continent, instance...), the tile grid (64 x 64, which tiles exist), map-wide
flags (the WDT's MPHD: big alpha, height texturing, vertex shading...), the global WMO for WMO-only maps, the
minimap tiles, and the table rows the map needs (Map, AreaTable, LoadingScreens, Light, LiquidType, WMOAreaTable...).

Per **tile** (an ADT): 16 x 16 chunks, the asset lists, the placements, flight bounds (MFBO), texture parameters
(MTXP: height-texture scale), and the tile's LOD data (kept, not used by 1.12).

Per **chunk** (an MCNK):

| Field | Superset of | 1.12 can show |
|---|---|---|
| 145 heights, 145 normals | all versions | yes |
| holes: 8 x 8 bits | modern high-res holes; 1.12 has 4 x 4 | coarser (a 1.12 hole covers 2 x 2 modern holes) |
| texture layers (up to 4): texture, flags, ground-effect ID | all | yes |
| alpha per layer: 64 x 64, 8-bit | WotLK/modern 8-bit; 1.12 4-bit | reduced to 4 bits |
| shadow 64 x 64 | all | yes |
| vertex colours (MCCV), vertex lighting (MCLV) | WotLK on / modern | no (dropped; later native) |
| liquids: instances with type, heights, depth, area of 8 x 8 tiles, minimum/maximum (MH2O) | MH2O; 1.12 MCLQ | one layer per chunk, 4 kinds |
| area ID | all | yes, renumbered into 1.12's range |
| sound emitters | all (WotLK 28 bytes, 1.12 52) | yes |
| chunk flags | all | the 1.12 bits |

**Placements**: doodads (M2) and WMOs with position, rotation, scale, flags, unique ID, doodad set, name set. An asset
reference is `{ path?, FileDataID? }`.

## Asset references

A modern tile names its textures and models by FileDataID. Conversion resolves each one, in this order:

1. **The same file exists in 1.12** under the same path (most of Azeroth reuses the original world's assets): the
   1.12 tile uses 1.12's own file, no conversion.
2. **A known name** (from the root's name hashes, or the user's listfile): convert the file, keep the path.
3. **No name**: convert the file to `World\Converted\<FileDataID>.<ext>`.

Textures convert directly (same BLP format family; sizes and pixel formats checked against what 1.12 loads).
Models and buildings of a newer format need the M2 / WMO downgrades; until those exist, a placement whose model
cannot be shown is left out and listed in the report.

## Writing 1.12 (from 1.60, later from 2.x/3.x)

* Split tiles merged into one ADT; MCIN, MHDR and every offset recomputed (the existing `AdtDocument` writer).
* Alpha: 8 bits to 4 bits (rounded; compressed modern alpha decoded first).
* Holes: a 1.12 hole where all four modern holes of its 2 x 2 are holes (conservative: no new gaps in the ground).
* Liquids: MH2O to MCLQ. One layer per chunk: the instance that covers the most tiles; its type mapped to river,
  ocean, magma or slime. Others are reported.
* Areas: area IDs renumbered into free 1.12 AreaTable rows (with the server's area data in step), like the custom spell
  ID range.
* WDT, WDL (rebuilt from the heights: 1.12 reads WDL by position) and minimap tiles (1.12's `md5translate.trs`).
* The result goes into the user's workspace as a change set. **Test in game**, the release patch and **Update server
  maps** then work as for any map edit (the server extractor reads 1.12 data).

## Writing 1.60 (from 1.12, later the hub for 2.x/3.x)

* One 1.12 ADT split into root / obj0 / tex0 (obj1 and LOD optional); MCLQ to MH2O; 4-bit alpha to 8-bit; the WDT's
  MAID entries.
* **Open question:** a modern client finds files by FileDataID. Files that are not in its root (new or converted
  assets) need IDs of their own. The output is meant for our own native-1.60 client later and for tools, not for
  Blizzard's 1.60 client, so a local ID range plus a name map is enough. To be decided when native support starts.

## Milestones

1. **CASC reader** (in progress): read-only, 1.60 first; the Other versions tab shows CASC clients as readable.
2. **1.60 map survey** (measured): which maps and tiles exist, how they differ from 1.12 (identical, edited, new),
   chunk and liquid features used, and how many placed models and textures also exist in 1.12 by path.
3. **Neutral model + 1.12 reader/writer** with the byte-identical round trip on every stock tile.
4. **1.60 reader** (split ADT, WDT MAID, MH2O, 8-bit and compressed alpha, high-res holes) with its own round trip.
5. **1.60 to 1.12 terrain**: heights, holes, textures and alpha, liquids, areas, WDT/WDL/minimap; the report.
6. **Placements** with the asset matching above (models and buildings that exist in 1.12).
7. **1.12 to 1.60** writer.
8. **In-game and server test** (a converted tile shown in the test client, collision after **Update server maps**).
9. Later: 2.x/3.x readers into the hub, the M2 / WMO downgrades, the modern data-table reader for the map tables.

Measured facts this design rests on: `MPQ_ERA_DELTA.md` (1.12 / 2.4.3 / 3.3.5a formats) and
`CASC_INVESTIGATION.md` (modern storage; map data complete and unencrypted in four sampled clients). 1.60's own maps
are measured in milestone 2.
