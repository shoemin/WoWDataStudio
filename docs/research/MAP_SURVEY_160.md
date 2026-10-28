# 1.60 map survey: the modern Classic maps measured against 1.12.1

Research note, 2026-10-20. Milestone 2 of `MAP_CONVERSION_DESIGN.md`. Read-only: nothing was converted, no repo file
other than this one was written, nothing under `G:\WoW Clients and Patches` was changed, nothing was launched, no
network, no listfile.

**MEASURED** = a probe read the real files and counted (every number in a table is measured unless marked).
**FORMAT KNOWLEDGE** = public format facts (wowdev-wiki style) that were not checked here. **INFERRED** = derived from
measured data by a stated rule, not read from a definition.

Sources: 1.60 = `BattleNet snapshot 2026-10-20\_classic_beta_` (1.60.1.70450, our `CascStorage`); 1.15.9 Era =
`_classic_era_` (used for names and as a reference); 1.12 = `1.12.1.5875 - WoW Client\Data` (our `ClientData`).
Every tile of every map below was read: **no sampling** except two small diagnostics that say so.

---

## Summary

1. **Names without a listfile work for old assets.** Hashing the 1.12 client's own `(listfile)` names into Era's
   root gives an FDID for 56,563 of 79,733 1.12 paths (70.9%), and for **99-100% of everything a map uses**: 2,399 of
   2,429 ADTs, 42/43 WDTs and WDLs, 1,048/1,048 ground textures, 9,685/9,691 models, 815/815 WMO roots and 5,220/5,220
   groups. FDIDs are the same in Era and 1.60 (0 disagreements). 1.60's own root hashes almost none of them (5,632).
2. **Maps.** All 41 1.12 maps that have a WDT exist in 1.60 (only `test`, `Test` and `development` do not). 1.60
   Map.db2 lists **29 more WDTs**: 16 maps that Era also has (Season-of-Discovery-era IDs 2720-2921, 266 tiles) and **12
   maps that exist only in 1.60** (627 tiles; the largest has 240 tiles), plus the WMO-only `TEST_01`.
3. **The continents are not the 1.12 terrain any more.** Of the tiles both have, **142 of 687 Azeroth and 259 of 988
   Kalimdor tiles are identical in substance; 545 and 729 are edited**. Azeroth has **49 new tiles** (three coastal
   blocks), Kalimdor lost 30 empty filler tiles. Era 1.15.9, by contrast, is still 1.12: 586/687 and 846/979 identical,
   heights 100% exact, alpha 100% exact.
4. **What changed, chunk by chunk** (1.60 vs 1.12, both continents, 428,800 chunks): heights differ by more than
   0.01 yd in 15.9% of chunks (by more than 10 yd in 12.0%: real re-sculpting, concentrated in Wetlands, Hillsbrad,
   Silverpine, Burning Steppes, Redridge, Swamp of Sorrows, Silithus, Feralas, Hyjal, Stonetalon); texture lists differ
   in 12.2%; **alpha maps are 8-bit and re-painted** in about half the layers; area IDs differ in 5.3% (new 16xxx/17xxx
   IDs); 92.9% / 95.8% of 1.12 doodads are still placed exactly as before, and 63,599 / 13,625 doodads were added.
5. **Format of 1.60 tiles.** Split root/obj0/obj1/tex0/lod, all by FDID (no MTEX/MMDX/MWMO at all). Continents:
   MPHD `0x3CA`, 8-bit alpha (95.7% compressed, 4.3% raw 4096, 0 four-bit), MCCV in 69% of chunks, high-res holes flag
   on every chunk, **MCSH dropped from 99% of chunks**, MCSE always empty, no MFBO, up to **8 layers** per chunk.
   Instances keep 4-bit alpha (MPHD `0x248`). Minimaps are **512x512 DXT1** (1.12: 256x256) - confirmed on every tile.
   Placed models are **M2 MD21 v272** (Era: v274), WMOs v17 with GFID/MODI.
6. **Asset overlap.** Of the assets the surveyed tiles use: ground textures 96.1% of layer uses (91.8% byte-identical
   to 1.12), doodads 93.6% of placements, WMOs 83.8% of placements map to a **path that exists in 1.12**. New and
   unnamed: 103 textures, 1,060 M2s, 205 WMOs.
7. **Converter surprises** (details in section 6): alpha must be reduced with `v >> 4`, not rounding (Blizzard's own
   1.12-to-8-bit rule is measured as `16n + 15`); tex0 `MDID` points at the `_s.blp` file; MH2O's second u16 is a
   LiquidObject ID on every 1.60 continent instance, so the vertex format is not in the file; liquid type IDs are new
   (12xx-13xx); shadows must come from 1.12 or be dropped.

---

## 0. Method and probe

Probe: `survey160\` in a scratch folder (not in the repo)
(C# console app on `app\WoWData.Core\bin\Release\net8.0\WoWData.Core.dll`; `dotnet build -c Release -o out`).

| Mode | Files | What it does |
|---|---|---|
| `names` | `Names.cs` → `results\names112.tsv`, `names_other.tsv`, `names_report.txt` | hash every 1.12 listfile path (`.mdx` read as `.m2`) into Era, 1.60 and `_forever_`; also the 2.4.3/3.3.5a clients' own listfiles (only to tell "named elsewhere" from "unnamed") |
| `maps` | `Maps.cs` → `maps_report.txt` | 1.12 Map.dbc directories + 2.4.3/3.3.5a Map.dbc + 1.60 Map.db2 strings → WDT name hash; MPHD, MAIN vs MAID, companions |
| `wdtscan`, `newmaps` | `Program.cs` → `wdtscan.txt`, `newmaps.tsv` | every u32 of 1.60 Map.db2 that is an FDID starting `MVER..MPHD` (finds WDTs without names); numeric directory names hashed 0-19999 |
| `terrain <maps> [--era]` | `Terrain.cs`, `Read160.cs`, `Model.cs` → `terrain_*.txt`, `tiles_*.tsv`, `set_*`, `use_*` | per tile and chunk comparison, feature counters |
| `assets` | `Assets.cs` → `assets_report.txt` | BLP/M2/WMO formats, 1.12 overlap, minimaps |
| `alphadiag <map> <n>`, `m2ver <n>`, `areas112` | `Program.cs` | sampled diagnostics (alpha alignment and rule, Era vs 1.60 M2 version, 1.12 areas of given tiles) |

**Throughput (MEASURED).** Reading is not the bottleneck. The first full 1.60 run of both continents (1,754 tiles:
1.60 root/obj0/obj1/tex0/lod + the 1.12 ADT + the comparison) took 80 s (Azeroth 32 s, Kalimdor 48 s, about 22
tiles/s); repeated runs 35 s. The Era run took 249 s cold, 48 s warm. The 19 instance maps (706 tiles) take 16 s.
Names 6 s, maps 2 s, assets 27 s.

**How tiles are compared (rules used for "identical in substance").**
* Chunks are matched by MCNK `indexX/indexY`. Heights are compared absolute (`posZ + MCVT`); "same" = within 0.01 yd.
* Holes: 1.12's 4x4 mask against the 1.60 8x8 mask reduced both ways ("all four fine holes" and "any"); same if
  either reduction matches. High-res layout used: byte = row, bit = column (FORMAT KNOWLEDGE, confirmed by 451 + 358
  chunks where 1.60 is exactly 1.12 expanded 2x2).
* Textures: the per-chunk list of texture paths (1.60 FDIDs via the name map; `_s.blp` mapped to the plain name, see
  5.1). Alpha only where both lists are equal; inner 63x63 texels; a layer counts as edited when a texel is more than
  two 4-bit steps (|v - 17n| > 34) off.
* Liquids: presence, 8x8 tile coverage, kind (INFERRED map from LiquidType ID, section 3.5), heights on vertices of
  tiles covered on both sides.
* Placements: by unique ID; same = same model, position within 0.01 yd, rotation matrices within 0.002 (about 0.1
  degree; Euler angles are compared as matrices because 1.60 normalises them), same scale.
* Not counted as edits: dropped shadows (format change, 3.3), ground-effect IDs, normals.

---

## 1. Names: 1.12 path ↔ FileDataID without a listfile (MEASURED)

1.12 has 79,733 listed, mounted, non-deleted names (none spelled `.mdx`; models are listed as `.m2`). Each path was
hashed (Jenkins, upper case, backslashes) and looked up in the root name hashes.

| Kind | 1.12 paths | FDID via Era hash | via 1.60 hash | any | % | FDID in 1.60 root | content equal Era = 1.60 |
|---|---|---|---|---|---|---|---|
| **all** | 79,733 | 56,561 | 5,632 | 56,563 | 70.9 | 55,032 | 24,684 |
| `.adt` | 2,429 | 2,399 | 0 | 2,399 | 98.8 | 2,381 | 353 |
| `.wdt` | 43 | 42 | 0 | 42 | 97.7 | 41 | 26 |
| `.wdl` | 43 | 42 | 0 | 42 | 97.7 | 41 | 29 |
| ground textures `TILESET\*.blp` | 1,048 | 1,048 | 0 | 1,048 | **100** | 1,034 | 952 |
| other `.blp` | 44,623 | 37,085 | 5,586 | 37,085 | 83.1 | 36,983 | 22,183 |
| minimap `.blp` (md5 names) | 7,122 | 0 | 0 | 0 | 0 | - | - |
| all `.blp` except minimaps | 45,671 | 38,133 | | 38,133 | 83.5 | | |
| models `.m2` (`.mdx` in MMDX) | 9,691 | 9,685 | 29 | 9,685 | **99.9** | 9,578 | 17 |
| WMO roots | 815 | 815 | 0 | 815 | **100** | 701 | 120 |
| WMO groups | 5,220 | 5,220 | 0 | 5,220 | **100** | 4,056 | 807 |
| `.mp3` / `.wav` / `.dbc` / `.lua` | 200 / 7,154 / 154 / 130 | 200 / 0 / 0 / 10 | | | | | |

* FDID disagreements between the Era and the 1.60 lookup: **0**. `_forever_` 1.60.1.70245 hashes the same 5,632.
* 1.12 minimaps have md5 file names, so they cannot be hashed. Not needed: 1.60's WDT MAID gives the minimap FDID per
  tile (section 4.6).
* The 2.4.3 and 3.3.5a clients' own listfiles (local clients, not a bundled listfile) add 61,594 and 79,166 hashed
  names. They were used only to classify assets in section 5 (class B).
* The map 1.12 path → FDID is in `results\names112.tsv` (56,563 lines).

---

## 2. Maps and tile sets

### 2.1 How 1.60 WDTs were found (MEASURED)

* 1.12 `Map.dbc` directories (44 rows), plus 2.4.3/3.3.5a `Map.dbc` directories, plus every string of the 1.60
  `Map.db2` (FDID 1349477, WDC5, 10,197 bytes; strings read from the raw bytes, no DB2 reader), hashed as
  `World\Maps\<Dir>\<Dir>.wdt`.
* 1.60's new maps use **numeric directory names** (`2720` = "The Searing Basin", ...). Hashing `World\Maps\N\N.wdt` for
  N = 0..19999 finds 16 of them in Era's root.
* **WDT FDIDs straight from Map.db2:** a byte scan for u32 values that are 1.60 FDIDs whose file starts `MVER ... MPHD`
  finds 62 WDTs at 4-byte steps from offset 1416 (one column, one value per record), including 12 WDTs whose names are
  in no root. That is the cheap way to list every 1.60 map until the DB2 reader exists.

### 2.2 1.12 maps in 1.60

| 1.12 maps | Count |
|---|---|
| 1.12 Map.dbc rows | 44 |
| with a 1.12 WDT | 43 |
| with a 1.60 WDT | **41** (all but `test`, `Test`; `development` (18 tiles) has an Era name but is not in 1.60's root) |
| tile maps (MAIN tiles) | 21 in both: Azeroth, Kalimdor + 19 instances/battlegrounds |
| WMO-only maps | 20 (1.60 MPHD `0x241`) |

**Tile sets (1.12 MAIN vs 1.60 MAID).** Identical for all 19 instance maps. Differences:

| Map | 1.12 tiles | 1.60 tiles | same | only 1.12 | only 1.60 |
|---|---|---|---|---|---|
| Azeroth | 687 | 736 | 687 | 0 | **49** |
| Kalimdor | 1,018 | 988 | 988 | **30** | 0 |
| (Era 1.15.9) Azeroth / Kalimdor | 687 / 1,018 | 687 / 979 | | 0 / 39 | |

* Azeroth's new tiles are three coastal blocks (x_y): **24..26 x 31..39** west of the 1.12 Great Sea tiles (areas 2365,
  2397, 2398), **43..45 x 43..50** east of the Forbidding Sea tiles (2402, 2403), **23..26 x 54..59** next to Gillijim's
  Isle (408). Their chunks carry area 0 (6,560), 16820 (4,377), 16728 (391), 16591 (32) and the old sea areas 2398
  (1,024) and 2403 (160).
* Kalimdor's 30 removed tiles (45..50 x 12..16) have area 0 in every chunk in 1.12 (empty filler). Era also lacks them
  and 9 more (0..2 x 0..2), which 1.60 has again.
* WDT layout: continents `MVER MPHD MAIN MAID MAI2` (MAI2 not decoded), tile instances `MVER MPHD MAIN MAID`, WMO-only
  `MVER MPHD MAIN MAID MODF`. Companions `.wdl _occ.wdt _lgt.wdt _fogs.wdt _mpv.wdt .tex` exist for every 1.60 map.
  Every tile file named by MAID is local (one exception in map 2772, 5 of 6).

### 2.3 Maps new in 1.60 (MEASURED; names from Map.db2 strings)

Map.db2 holds these directory/name string pairs after the 1.12 maps: 2720 The Searing Basin, 2784 Demon Fall Canyon,
2789 The Tainted Scar, 2791 Storm Cliffs, 2804 The Crystal Vale, 2806 Shadow Hold, 2807 Burning of Andorhal, 2817
Starfall Barrow Den, 2832 Nightmare Grove, 2853 Deadwind Pass, 2856 Scarlet Enclave, 2868 Eastern Plaguelands (Scarlet
Raid Phase), 2875 Karazhan Crypts, 2902 The Scarab Dais, 2921, 2959 City of Dalaran, 2980 Dalaran City, 2991 Zephras
Isle, 2995 Hyjal Crater, 2996 Warsong Gulch (Winter), 2997 Darkspear Islands, 2998 Excavation Site: Wetlands, 2999
Ruins of Lordaeron, 3005 Battle for Gilneas, 3021 Eastern Kingdoms Preserved, 3065 The Hall of Thanes, 3104 nothing to
see here, 3109 Manor Mistmantle, 3217 Dalaran Skycuter, 3218 Mulgore Skycuter.

| WDT FDID | Directory (by hash) | MPHD | Tiles | In Era? | MAID slots filled |
|---|---|---|---|---|---|
| 2323096 | TEST_01 | 0x241 | 0 (WMO-only) | yes | - |
| 5554791 | 2720 | 0x248 | 12 | yes (33 of 48 tile files identical) | 5 |
| 5836760 | 2772 | 0x3CA | 1 | yes | 6 |
| 5900942 | 2784 | 0x3CA | 9 | yes | 8 |
| 5922434 | 2789 | 0x3CA | 9 | yes | 8 |
| 5930564 | 2791 | 0x3CA | 20 | yes | 8 |
| 6081492 | 2804 | 0x3CA | 9 | yes | 8 |
| 6099506 | 2806 | 0x24C | 9 | yes | 5 |
| 6099634 | 2807 | 0x24C | 25 | yes | 5 |
| 6155791 | 2817 | 0x3CA | 9 | yes | 8 |
| 6227405 | 2832 | 0x3CA | 36 | yes | 8 |
| 6357503 | 2853 | 0x3CA | 12 | yes | 8 |
| 6372264 | 2856 | 0x3CA | 42 | yes | 8 |
| 6402491 | 2868 | 0x3CA | 12 | yes | 8 |
| 6420120 | 2875 | 0x3C8 | 12 | yes | 8 |
| 6635982 | 2902 | 0x2CA | 25 | yes | 5 |
| 6789271 | 2921 | 0x3CA | 24 | yes | 8 |
| 6982267, 7090844, 7198644, 7245741, 7251896, 7251908, 7252920, 7255103, 7338374, 7456538, 7713294, 7898491 | not hashed anywhere | 0x3CA | 25, 16, 72 (with MAI2), 35, 9, 25, 39, 9, 16, 60, 81, **240** | **no** | 8 |

The 12 beta-only WDTs belong to the directories 2959..3218 above (15 directories; 3 of them presumably reuse another
map's WDT), but pairing each FDID with its name needs the DB2 reader (record layout); the scan order alone is not proof.

---

## 3. Terrain per tile and chunk: 1.60 vs 1.12 (MEASURED, every tile)

### 3.1 Tile status

| Map | Tiles | identical in substance | edited | new in 1.60 | only in 1.12 |
|---|---|---|---|---|---|
| **Azeroth** | 736 | **142** | **545** | **49** | 0 |
| **Kalimdor** | 1,018 | **259** | **729** | 0 | **30** |
| 19 instances/battlegrounds | 706 | 331 | 375 | 0 | 0 |
| (reference) Era Azeroth | 687 | 586 | 101 | 0 | 0 |
| (reference) Era Kalimdor | 1,018 | 846 | 133 | 0 | 39 |

Instances: PVPZone01 29/6, Shadowfang 19/6, DeadminesInstance 25/11, PVPZone02 0/30 (liquid), RazorfenKraul 1/5,
RazorfenDowns 15/9, EmeraldDream 121/135, MonasteryInstances 25/11, TanarisInstance 3/18, CavernsOfTime 20/19,
SchoolofNecromancy 7/9, Zul'gurub 10/15, Stratholme 3/17, BlackwingLair 7/9, PVPZone03 3/13, AhnQiraj 18/22,
PVPZone04 5/11, AhnQirajTemple 16/9, Stratholme Raid 4/20 (identical/edited). Edit aspects there: liquid 206 tiles,
textures 125, doodads 84, WMOs 29, area 9, alpha 7, heights 6.

**Edited tiles by aspect** (a tile can have several):

| Aspect | Azeroth | Kalimdor |
|---|---|---|
| alpha (a layer > 2 steps off) | 450 | 699 |
| heights (> 0.01 yd) | 357 | 350 |
| doodads | 294 | 308 |
| textures | 280 | 300 |
| liquid | 179 | 123 |
| WMOs | 139 | 102 |
| area IDs | 110 | 54 |
| holes | 29 | 17 |
| shadow (both have one, differs) | 0 | 34 |
| tiles whose only edit is alpha | 97 | 233 |

Per-tile results: `results\tiles_Azeroth.tsv`, `tiles_Kalimdor.tsv`, `tiles_<instance>.tsv` (chunk counts per aspect,
placements added/removed/moved, and whether root/obj0/tex0 are byte-identical to Era).

**Era vs 1.60 tile files (content keys):** of the 1,705 continent tiles both have, **no root ADT is byte-identical**
(MCCV, MH2O and flags were added everywhere), obj0 is identical in 727 and tex0 in 266.

### 3.2 Where the edits are (1.12 top-level zone of the chunk; chunks / heights > 1 yd / texture list differs / alpha edited)

| Azeroth zone | chunks | heights | textures | alpha | | Kalimdor zone | chunks | heights | textures | alpha |
|---|---|---|---|---|---|---|---|---|---|---|
| Wetlands | 23,240 | 7,249 | 6,311 | 6,632 | | (area 0) | 19,693 | 6,919 | 5,877 | 3,029 |
| Stranglethorn Vale | 20,505 | 1,491 | 2,597 | 7,510 | | Silithus | 21,338 | 4,972 | 3,737 | 6,245 |
| Eastern Plaguelands | 13,967 | 1,562 | 1,206 | 5,640 | | Feralas | 18,983 | 2,665 | 1,935 | 9,265 |
| Hillsbrad Foothills | 6,655 | 2,995 | 2,796 | 2,416 | | The Barrens | 13,400 | 332 | 178 | 11,321 |
| Silverpine Forest | 8,516 | 2,651 | 2,657 | 2,544 | | Ashenvale | 13,068 | 849 | 526 | 9,179 |
| Swamp of Sorrows | 9,400 | 2,618 | 2,442 | 2,486 | | Azshara | 17,173 | 254 | 118 | 9,667 |
| Westfall | 12,674 | 2,010 | 1,827 | 3,493 | | Tanaris | 27,993 | 966 | 678 | 6,111 |
| Burning Steppes | 4,661 | 2,757 | 2,476 | 1,846 | | Winterspring | 7,014 | 1,041 | 1,000 | 4,812 |
| Tirisfal Glades | 19,202 | 897 | 873 | 4,372 | | Stonetalon Mountains | 6,383 | 1,818 | 1,199 | 3,314 |
| Redridge Mountains | 3,186 | 1,826 | 1,501 | 1,438 | | Hyjal | 4,228 | 1,932 | 1,467 | 2,129 |
| Searing Gorge | 2,698 | 1,256 | 979 | 1,351 | | Teldrassil | 27,967 | 6 | 0 | 5,985 |
| Gillijim's Isle | 1,536 | 1,098 | **1,536** | 0 | | Orgrimmar | 542 | 291 | 166 | 16 |
| Stormwind City | 849 | 450 | 338 | 89 | | GM Island | 2,304 | 0 | 10 | 40 |

(Sea areas count under the zone their AreaTable parent names; full lists in `terrain_Azeroth_Kalimdor.txt`.)

### 3.3 Chunk-level results

| Measure | Azeroth (175,872 chunks) | Kalimdor (252,928 chunks) |
|---|---|---|
| heights exact / ≤ 0.01 yd | 136,960 / 137,970 (78.4%) | 221,456 / 222,538 (88.0%) |
| heights 0.01-1 / 1-10 / > 10 yd | 4,477 / 4,789 / **28,636** | 2,180 / 5,386 / **22,824** |
| posZ field differs (heights compared absolute) | 34,735 | 25,603 |
| normals byte-equal | 119,782 (68%) | 194,438 (77%) |
| holes: chunks with holes on either side | 602 | 448 |
| - 1.60 = 1.12 expanded 2x2 exactly | 451 | 358 |
| - 1.12 = "all four" reduction / "any" reduction | 502 / 457 | 392 / 360 |
| - disagree under both / only 1.60 has holes / only 1.12 | 94 / 87 / 35 | 54 / 57 / 27 |
| texture list same, same order | 145,052 (82.5%) | 231,582 (91.6%) |
| same set, other order / different | 104 / 30,716 | 50 / 21,296 |
| chunks with a texture that has no 1.12 name | 7,222 | 1,992 |
| chunks with more than 4 layers (1.12 max is 4) | 2,438 | 1,635 |
| layer flags differ (ignoring 0x200) | 43 | 0 |
| ground-effect ID differs (layers) | 27,744 of 343,061 (8.1%) | 56,640 of 554,144 (10.2%) |
| area ID differs | 17,707 (10.1%) | 5,152 (2.0%) |
| 1.12 MCSH present, 1.60 has none ("shadow dropped") | 165,260 | 224,686 |
| both have MCSH: equal / differs | 0 / 0 | 407 / 3,963 |
| sound emitters (1.12 / 1.60) | 7 / 0 chunks | 2 / 0 chunks |

Area changes: mostly sea and border areas renumbered into new IDs (`0 → 16361` 1,445 chunks, `2397 The Great Sea →
17065` 1,437, `2365 → 2398` 955, `44 Redridge Mountains → 16591` 748, `46 Burning Steppes → 16591/17780/16737`,
`616 Hyjal → 16866/16849/17145/...`). The 1.60 names of 16xxx/17xxx need AreaTable.db2.

### 3.4 Alpha maps

1.60 continents store 8-bit alpha; 1.12 stores 4-bit (n = 0..15). Layers compared where both chunks have the same
texture list:

| | Azeroth | Kalimdor |
|---|---|---|
| layers compared | 198,265 | 322,562 |
| texels (inner 63x63) | 786.9 M | 1,280.2 M |
| `v >> 4 == n` | **90.67%** | **88.28%** |
| `round(v / 17) == n` | 78.50% | 74.65% |
| `v == 17n` | 61.93% | 57.26% |
| texels more than two steps off (> 34) | 7.30% | 8.89% |
| layers with no texel > 2 steps off ("clean") | 98,667 (49.8%) | 157,831 (48.9%) |
| layers with < 1% / 1-10% / ≥ 10% of texels > 2 steps off | 18,780 / 37,770 / 43,048 | 29,555 / 58,105 / 77,071 |

**The rule Blizzard used (MEASURED, `alphadiag`, 60 random tiles per continent, 15,389 + 17,358 layers).** In the clean
layers (about half) `v >> 4 == n` holds for **100%** of texels (rounding: 83-87%), and the 8-bit value is `0` for n = 0 and
**`16n + 15`** otherwise (n 6 → 111, 8 → 143, 15 → 255; a few `16n + 14` and smoothed values). The maps are aligned
(a one-texel shift, a 3x3 blur or a 2x upsample all fit worse: within one step 65-71% of texels vs 89.6-91.9% unshifted). So:
the other half of the layers were re-painted in 8 bits, and `v >> 4` gives back the 1.12 value wherever they were not.

Era (reference) stores 4-bit alpha that equals the 1.12 nibbles in 100% of 2.18 G texels.

### 3.5 Liquids

| | Azeroth | Kalimdor |
|---|---|---|
| chunks with liquid in both / only 1.60 / only 1.12 | 88,758 / 1,299 / 3,415 | 129,563 / 979 / 38 |
| 8x8 tile coverage equal / differs | 86,107 / 2,651 | 128,809 / 754 |
| kind equal / differs (single-liquid chunks, inferred map) | 87,725 / 223 | 129,343 / 156 |
| chunks with > 1 instance (1.60) / > 1 layer (1.12) | 810 / 13 | 64 / 15 |
| heights on shared covered vertices exact / ≤ 0.01 / > 0.01 | 84,392 / 1,717 / 2,632 | 127,014 / 1,503 / 1,040 |

**LiquidType IDs → 1.12 kind (INFERRED** by majority on the chunks with exactly one liquid on both sides; agreement
in brackets): `1250` → ocean (207,006 of 207,006 chunks); `7` and `1267` → magma; water: `5` (23/23), `1240`
(1,739/1,778), `1268`, `1269`, `1284`, `1288` (796/969), `1297`, `1306`, `1313` (1,151/1,162), `1319`, `1320`,
`1324`, `1325`, `1337`, `1338`, `1339`, `1340`, `1345`, `1347`, `1348`, `1349`; `1343` is split (166 of 322 ocean).
No slime was seen on the continents. The instance maps still use the old IDs `2` (ocean) and `5` (water).
A converter should read LiquidType.db2 (its `Type`/material column) instead of this table.

---

## 4. Features used by 1.60 tiles (MEASURED, all 1,724 continent tiles and 706 instance tiles)

### 4.1 WDT

| | Continents | 1.12 tile instances | WMO-only |
|---|---|---|---|
| MPHD | `0x3CA` | `0x248` (15), `0x24A` (TanarisInstance, PVPZone03, PVPZone04), `0x348` (Stratholme) | `0x241` |
| MAID slots filled | all 8 (root, obj0, obj1, tex0, lod, map texture, map normal texture, minimap) | root, obj0, obj1, tex0, minimap (Stratholme all 8) | none |

MPHD bits (FORMAT KNOWLEDGE): 0x1 WMO-only, 0x2 ADTs have MCCV, 0x4 big alpha, 0x8 doodad refs sorted by size, 0x40
unknown, 0x80 height texturing (also implies 8-bit alpha in modern clients: **measured**, the continents have 0x80
without 0x4 and store 8-bit alpha), 0x100 unknown (LOD-related), 0x200 MAID present.

### 4.2 Chunk lists (top level)

| File | Sequence (count of tiles) |
|---|---|
| root | `MVER MHDR MH2O MCNKx256` (1,346 + 295 instance), `MVER MHDR MCNKx256` (375 + 411), `... MCNKx256 MBMH MBBB MBMI MBNV` (3, blend meshes) |
| tex0 | `MVER MAMP MDID MHID MCNKx256` (all; MAMP = 0; **no MTEX, no MTXP**) |
| obj0 | `MVER MDDF MODF MCNKx256` (1,708 + 706), `+ MLMB` (16); **no MMDX/MMID/MWMO/MWID** |
| obj1 | `MVER [MLFD] MLDD MLDX [MLDL] MLMD MLMX [MLMB]` |
| lod (continents) | `MVER MLHD MLVH MLLL MLND MLVI MLSI` + optional `MLLD MASD MALP`, repeated `MLLN MLLI MLLV`, `MBMH...` |

No MFBO and no MTXF anywhere. MHDR offsets were not used (chunks read sequentially).

### 4.3 MCNK

| | Continents (441,344 chunks) | Instances (180,736) |
|---|---|---|
| root sub-chunks | `MCVT MCCV MCNR MCSE` 303,603; `MCVT MCNR MCSE` 135,119; `+ MPTX` 2,612; `+ MCBB` 10 | `MCVT MCNR MCSE` 170,105; `MCVT MCCV MCNR MCSE` 10,625; `+ MPTX` 6 |
| tex0 sub-chunks | `MCLY MCAL` 277,671; `MCLY` 159,303; `MCLY MCSH MCAL` 4,225; `MCLY MCSH` 145 | `MCLY` 116,037; `MCLY MCAL` 64,699 (**no MCSH**) |
| obj0 sub-chunks | empty 287,655; `MCRD` 115,405; `MCRD MCRW` 26,251; `MCRW` 12,033 | similar |
| flags | `0x18040` 300,731; `0x18000` 135,221; `0x18041` 4,370; `0x18042` 976; `0x18002` 46 | `0x18000` 169,545; `0x18040` 10,468; `0x18002` 566; `0x18042` 157 |
| MCCV | 306,077 (69.4%) | 10,625 (5.9%) |
| MCLV | 0 | 0 |
| high-res holes flag 0x10000 | every chunk; mask non-zero in 988 | every chunk |
| MCSE | always present, always 0 bytes | same |
| layers per chunk | 0: 7,916; 1: 151,532; 2: 65,422; 3: 87,963; 4: 124,377; **5: 3,372; 6: 672; 7: 76; 8: 14** | 0-4 |

`MPTX` is not in our format knowledge (2,618 chunks); `MCBB` is a blend-batch chunk (FORMAT KNOWLEDGE). Flag 0x40 =
MCCV present, 0x1 = MCSH present, 0x2 = impassable, 0x8000 = no alpha fix-up, 0x10000 = high-res holes (FORMAT
KNOWLEDGE, consistent with the sub-chunk counts).

### 4.4 Alpha and MCLY

| | Continents | Instances |
|---|---|---|
| MCLY flags | `0x300` 604,193; `0x0` 433,395; `0x100` 27,475; `0x380` 88; `0x400` 33; `0x3C0-0x3F2` (animation bits) ~110 | `0x100` 138,875; `0x0` 133,154; `0x80` 82; `0x180` 65 |
| alpha layers | **compressed (0x200) 604,406** - every one decodes to exactly 4,096 values and ends exactly at the next layer or the MCAL end; **uncompressed 8-bit 27,475** (span 4,096) | **4-bit 138,940** (span 2,048) |

MCLY 0x400 = cube-map reflection layer (FORMAT KNOWLEDGE; 33 layers, matching the 768x128 textures in 4.6).

### 4.5 Liquids (MH2O)

| | Continents | Instances |
|---|---|---|
| root files with MH2O | 1,348 of 1,724 | 295 of 706 |
| instances | 233,915 | 19,690 |
| chunks with 1 / 2 / 3 / 4 instances | 232,100 / 871 / 23 / 1 | 19,639 / 21 / 3 / 0 |
| rectangle | always the full 8x8; exists bitmap in 20,185 instances | always 8x8; bitmap in 5,765 |
| chunk attribute masks | 59,398 chunks | 18,337 |
| second u16 | **always ≥ 42** (a LiquidObject ID, FORMAT KNOWLEDGE): 42 for ocean, ~350 different values for the others | ≥ 42 |
| vertex data (bytes/vertex, INFERRED vertex format) | **5.00 (LVF 0: f32 height + u8 depth) 233,355**; 8.00 (LVF 1, height + UV) 114 (type 7 magma); 9.00 (LVF 3, height + UV + depth) 446 (types 1267, 1268, 1269, 1284, 1347-1349) | 1.00 (depth only, LVF 2) 16,770 (type 2 ocean); 5.00 for types 5, 1240, 1288; 1,273 ocean instances without vertex data |

So on 1.60 continents even the ocean has per-vertex heights (LVF 0), unlike 3.3.5a (ocean LVF 2) and the 1.60
instances.

### 4.6 Textures, minimaps, map textures (BLP headers)

| Set | Count | Formats |
|---|---|---|
| ground textures (tex0 MDID, continents + instances) | 434 FDIDs | 256² DXT3 309, 256² DXT5 109, 256² palette 5, 128² DXT5 2, **1024² DXT5 1, 512² DXT5 1, 768x128 ARGB8888 2** (cube maps), 128²/32²/8² DXT1 3, 1 unreadable |
| same texture in 1.12 → 1.60 (328 named) | | DXT3 → DXT3 305, DXT3 → DXT5 13, palette → palette 5, palette → DXT5 2, small DXT1 unchanged 3 |
| height textures (MHID) | 1 non-zero entry of 11,359 | 1024² DXT5 |
| **minimaps** (MAID slot 7) | Azeroth 736, Kalimdor 988, Shadowfang 25, EmeraldDream 256, Stratholme 20 | **all 512x512 DXT1, no mips**; the 1.12 tiles (md5translate) are all 256x256 DXT1, no mips |
| map textures (MAID slots 5/6) | 1,744 + 1,744 | 256² DXT1 and 512² DXT1 |

`MDID` entries: 10,889 name an **`_s.blp`** (diffuse + specular in alpha), 470 are unnamed, **0** name a plain `.blp`.
All 328 named ones also have the plain diffuse in 1.12 and in the 1.60 root.

### 4.7 Placements and placed assets

| | Continents | Instances |
|---|---|---|
| MDDF flags | `0x40` 299,819; `0x240` 99,779; `0x60` 93; `0x64` 30 | `0x40` 46,440; `0x240` 17,750; `0x60` 49; `0x64` 22 |
| MODF flags | `0xC` 4,804; `0x4C` 29; `0x16C` 12 | `0xC` 1,104 |

* **Every MDDF has 0x40 and every MODF has 0x8** (the FDID flags, FORMAT KNOWLEDGE; confirmed: every name ID resolved
  to an M2 or WMO file, and obj0 has no name chunks). MODF 0x4 = has scale (FORMAT KNOWLEDGE). MDDF 0x200, 0x20, 0x4
  and MODF 0x40, 0x100 are not in our format knowledge.
* Rotations: 1.60 writes angles normalised to [0, 360) and sometimes an equivalent Euler triple
  (`(-91, 0, 65.5)` → `(-89, 180, -114.5)`); positions carry ≤ 0.01 yd float noise. Compare as matrices.
* **M2 (4,403 placed FDIDs):** all `MD21`; version **272** in 4,338, 274 in 65. Chunks (on disk order reversed in our
  dump): always `SFID TXID`; often `LDV1 TXAC`; some `PGD1 EXP2 DBOC EDGF AFID PFDC`. Sample of 400 FDIDs that Era
  also has: Era v274 → 1.60 **v272** in 397 (383 bigger files), v272 → v272 in 3; **0 of 3,351** shared M2s have the
  same content as Era.
* **WMO roots (864 placed FDIDs):** all v17, all with `GFID`, `MODI` in 532, `MOSB` in 721, `MAVG MGI2` in 133, no
  `MOTX`. 116 of 662 shared with Era are unchanged.

---

## 5. Asset overlap with 1.12 (MEASURED; continents + 19 instance maps)

Class A = the FDID's name (via the 1.12 path hash) is a path that exists in 1.12; B = named only by a 2.4.3/3.3.5a
path; C = no name at all (new FDID).

| Assets | Distinct FDIDs: A / B / C | Uses: A / B / C | A byte-identical to 1.12 |
|---|---|---|---|
| ground textures (layer uses) | 328 / 3 / 103 | **1,284,884 (96.1%)** / 28 / 52,573 (3.9%) | 313 FDIDs, 1,227,798 uses (91.8%) |
| doodads M2 (placements) | 3,328 / 15 / 1,060 | **434,137 (93.6%)** / 263 / 29,582 (6.4%) | 0 (v272 vs 1.12 v256) |
| WMOs (placements) | 651 / 8 / 205 | **4,985 (83.8%)** / 10 / 954 (16.0%) | 0 |

* Changes of model at the same unique ID (continents): Elwynn tree canopies 01-04 → new FDIDs 7549688-7549692 (1,153
  placements), Hyjal tree 04 → FDID 7561408 (345), Azshara seaweed → generic seaweed (370), waterfalls → new FDIDs; 29
  city/landmark WMOs → new FDIDs (Stormwind, Ironforge, Goldshire inn, Booty Bay, Blackrock, Stratholme, Sunken Temple,
  Grim Batol, Dire Maul, Caverns of Time, Teldrassil dock, ...). Their 1.12 originals still exist under the old path.
* Placement totals (unique IDs): Azeroth doodads 154,187 → 208,873 (same 143,282; moved 689; other model 1,303; removed
  8,913 of which 6,503 reappear at the same spot with the same model under a new ID; added 63,599, 24,915 of them with a
  model that has no 1.12 name). WMOs 1,508 → 2,243 (same 1,373; added 850, 541 unnamed; removed 115). Kalimdor doodads
  162,221 → 170,118 (same 155,365; added 13,625, 2,137 unnamed; removed 5,728, 4,480 re-ID'd); WMOs 1,413 → 1,537.

---

## 6. Recommendations for the converter

### 6.1 1.60 → 1.12

| Item | What to do | Expected loss (measured) |
|---|---|---|
| Tile files | Merge root + obj0 + tex0. Ignore obj1, lod, map textures, MAI2. | none for 1.12 |
| New tiles / maps | 49 new Azeroth tiles and 28 new maps (893 tiles) need WDT/WDL/minimap entries; new maps also Map/AreaTable rows | - |
| Heights, normals | copy | none |
| Holes | use the "all four" reduction (the design's conservative rule) | 94 + 54 continent chunks differ from 1.12 under both reductions; fine holes not 2x2 aligned (1.60 had 87 + 57 chunks with holes 1.12 lacks) |
| Alpha | decode compressed (exact) or raw 8-bit, then **`n = v >> 4`** (not rounding: `v >> 4` restores the 1.12 value in 100% of unchanged layers, rounding in 83-87%) | about half the layers were re-painted in 8 bits; 7-9% of texels are more than two steps from 1.12 anyway - quantisation keeps the 1.60 painting, 4-bit coarse |
| Layers > 4 | 4,134 continent chunks (0.9%) use 5-8 layers: drop or merge the weakest | visible in those chunks |
| Textures | MDID names `_s.blp`: write the **plain** name (strip `_s`) - all 328 named ones exist in 1.12; convert the 103 unnamed ones (3.9% of layer uses) to `World\Converted\...` or their 2.4.3/3.3.5a name; downscale 1024²/512²; replace the 2 cube maps (768x128) | 3.9% of layer uses need new files |
| Shadows | 1.60 dropped MCSH in 99%. Keep 1.12's MCSH for chunks whose heights are unchanged (the shadow fits), else leave flag 0x1 clear | no baked shadows on re-sculpted chunks |
| MCCV | drop (69% of continent chunks have it) | vertex tint |
| Liquids | MH2O → MCLQ; kind from LiquidType.db2 (until then the inferred table in 3.5); vertex format from LiquidObject/LiquidMaterial DB2 (or byte count, as here); 1 instance per chunk (895 continent chunks have more) | type nuances (25 IDs → 4 kinds), multi-instance chunks |
| Area IDs | 1.60 uses new IDs in 10.1% / 2.0% of chunks: needs AreaTable.db2 to name and renumber them (DB2 reader first). Chunks whose area ID equals 1.12 keep it | - |
| Ground effects | 8-10% of layers have other effect IDs: remap via GroundEffectTexture DB2 or keep 1.12's on unchanged chunks | ground clutter |
| Placements | names via FDID → 1.12 path: 93.6% of doodads and 83.8% of WMOs reuse the 1.12 file directly; 1,060 new M2s (v272 → v256 downgrade) and 205 new WMOs (v17 modern → 1.12 v17) or skip; clear MDDF/MODF flags; rotations as they are | 6.4% of doodad and 16% of WMO placements until the downgrades exist |
| Swapped landmarks | offer "keep the 1.12 model" for the 29 WMOs and the tree/waterfall models (about 1,500 doodad placements) that 1.60 points at FDIDs without a 1.12 name under the same unique ID | - |
| Minimaps | 512x512 → 256x256 DXT1 + md5translate entries | half resolution |
| WDT / WDL | 1.12 WDT (MPHD 0 or 0x1); WDL rebuilt from heights | - |

### 6.2 1.12 → 1.60

| Item | What to do (measured basis) |
|---|---|
| Alpha | **`v = n == 0 ? 0 : 16n + 15`** (Blizzard's own 1.12 → 8-bit rule in 1.60); write compressed (0x200) like 95.7% of 1.60 layers, MPHD 0x80 or 0x4; or keep 4-bit with MPHD 0x248 as the 1.60 instance maps do |
| Textures | MDID = FDID of the `_s.blp` (1,034 of 1,048 1.12 ground textures are in the 1.60 root by FDID); MHID 0 |
| Holes | high-res mask = 1.12 mask expanded 2x2, flag 0x10000 (what 1.60 itself did in 809 chunks) |
| Shadows | MCSH may stay (flag 0x1; 1.60 still has it in 4,370 chunks) |
| Liquids | MH2O full 8x8 instances + exists bitmap; ocean as type 1250 / object 42 with LVF 0 (continents) or type 2 / LVF 2 (instances); other types need LiquidObject IDs from DB2 |
| Placements | MDDF flag 0x40 / MODF 0x8 + 0x4 with FDIDs (99.9% of 1.12 models and 100% of WMOs have an FDID by hash); obj0 without name chunks |
| WDT | MAID with at least root/obj0/obj1/tex0/minimap (the instance-map pattern; lod/map textures optional) |
| New files | anything not in the 1.60 root needs a local FDID range (open question in the design) |

### 6.3 Surprises

* **1.60 is a re-worked world, not a re-packaged 1.12.** 76% of the continent tiles both have are edited (Era: 14%), with
  re-sculpted terrain (> 10 yd) in 12% of chunks, 77,000 added doodads, 49 new Azeroth tiles and 12 maps that even Era
  does not have. A "1.60 → 1.12" conversion therefore brings real new content, and a converter cannot assume "same as
  1.12 except the format".
* **M2s went from v274 (Era) to v272 (1.60)** in every placed model checked; no placed M2 is byte-identical to Era.
* **Shadows were dropped** (MCSH in 1% of continent chunks, none in the instance maps), and MCSE is always empty.
* **MH2O's second u16 is a LiquidObject ID on every 1.60 instance** (also in the instances), so the vertex format must
  come from DB2 tables; byte counts identify it reliably (5, 8, 9 and 1 bytes per vertex).
* **The 8-bit alpha rule `16n + 15`**, and `v >> 4` as its exact inverse.
* **MPHD 0x80 without MTXP**: height texturing is flagged but only 1 of 11,359 MHID entries is set.
* **Up to 8 texture layers** per chunk (1.12 and 3.3.5a: 4).
* **New maps hide from name hashes**: directories are numeric and the newest 12 WDTs are hashed nowhere; Map.db2's
  WDT FDID column finds them without a DB2 reader.

---

## Appendix: result files

All in `...\scratchpad\survey160\results\`: `names_report.txt`, `names112.tsv`, `names_other.tsv`, `maps_report.txt`,
`wdtscan.txt`, `newmaps.tsv`, `terrain_Azeroth_Kalimdor.txt` (1.60 continents, features), `terrain_19maps.txt` (1.60
instances), `terrain_era_Azeroth_Kalimdor.txt` (Era reference), `tiles_*.tsv` (per tile), `set_*` / `use_*` (asset
FDIDs and use counts), `assets_report.txt`. Rerun: `out\survey160.exe names`, `maps`, `wdtscan`, `newmaps`,
`terrain Azeroth Kalimdor`, `terrain --era Azeroth Kalimdor`, `terrain <19 instance directories>`, `assets`,
`alphadiag Azeroth 60`, `m2ver 400`.
