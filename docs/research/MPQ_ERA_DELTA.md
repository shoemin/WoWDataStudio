# MPQ-era delta: TBC 2.4.3 and WotLK 3.3.5a compared with 1.12.1 (for map import)

Research for the experimental multi-version reader: read 2.4.3.8606 and 3.3.5a.12340 client data and import maps into the
1.12.1.5875 client. Written 2026-10-20. This is read-only research. No repo code was changed.

**How to read this document.** Every number in a table was **MEASURED** from the client copies in
`G:\WoW Clients and Patches\Clients\` (MANIFEST.md) with the probes listed in the appendix, unless it is marked
*(format knowledge)*. Format knowledge means public wowdev-style knowledge of the file formats that we did not check here.
Where the 1.12 client's own behaviour is quoted, it comes from our decompile (`WoWDecomp\src\wow_0081.c`) and is marked
**MEASURED (decompile)**. No GPL code was read or copied.

Main continents measured: 1.12 = Azeroth + Kalimdor (1,705 ADTs). 2.4.3 = Azeroth + Kalimdor + Expansion01 (2,505
ADTs). 3.3.5a = Azeroth + Kalimdor + Expansion01 + Northrend (3,606 ADTs). Every tile flagged in each WDT was read, and
none was missing.

---

## Summary

1. **Mount order (MEASURED from the code of each Wow.exe, confirmed by the data).** TBC and WotLK use the same rule.
   * First, the patch archives. Every `Data\patch-?.MPQ` and `Data\<loc>\patch-<loc>-?.MPQ` (exactly one character)
     is sorted by full path, descending and case-insensitive. Then the client adds the two exact names. TBC adds
     `patch-<loc>.MPQ` and then `patch.MPQ`. WotLK adds `patch.MPQ` and then `patch-<loc>.MPQ`. The list is mounted
     from the end, with priority rising from 0x40, so the first entry wins.
   * After the patches come the base archives, in a fixed table order with priority falling from 0x3F. TBC:
     `expansion`, `common`, `locale-<loc>`, `speech-<loc>`, `expansion-locale-<loc>`, `expansion-speech-<loc>`.
     WotLK: `expansion`, `lichking`, `common`, `common-2`, `locale`, `speech`, `expansion-locale`, `lichking-locale`,
     `expansion-speech`, `lichking-speech`.
   * In practice only the patch chains matter. The base archives share no files with each other, and `patch.MPQ`
     shares no files with `patch-<loc>.MPQ` (both MEASURED).
2. **TBC terrain is almost the 1.12 format.** Every 2.4.3 continent ADT has exactly the 1.12 chunk set. The only
   additions are `MFBO` (all 800 Outland tiles, MHDR flag 0x1) and MCNKs without `MCSH` (149,566 Outland chunks, flag
   0x1 clear, which 1.12 handles). Liquids are still `MCLQ`, alpha maps are still 4-bit (2,048 bytes), and MVER is 18
   everywhere. TBC WDTs are 1.12 WDTs. TBC WDLs only add `MAHO` after each `MARE`. Exception: one TBC dev map
   (`Sunwell5ManFix`) already uses big alpha (MPHD 0x4) and compressed alpha.
3. **WotLK terrain has five real gaps.**
   * **MH2O replaces MCLQ** on Azeroth, Kalimdor and Northrend: 2,297 files and 448,564 liquid chunks. Outland in 3.3.5
     still uses MCLQ.
   * **Northrend uses 8-bit alpha** (MPHD 0x4). 369,401 layers are RLE-compressed (all decode to exactly 4,096
     values) and 16,985 layers are 4,096 bytes uncompressed. In 86% of Northrend chunks the MCAL chunk-size field is
     wrong; `MCNK.sizeAlpha` is always right.
   * **MCCV vertex colours** in every Northrend chunk (289,536 chunks, MPHD 0x2).
   * **MCSE records are 28 bytes** (1.12 reads 52-byte records).
   * **WotLK WDLs start with `MWMO MWID MODF`** before `MAOF`. The 1.12 client reads the WDL positionally, so it
     would break.
4. **WMO**: version 17 in all three. Root and group chunk sets and record sizes are identical. The new things are
   semantic: MOHD flags (919 of 1,985 WotLK roots), MOMT shaders 2/5/6, new MOGP flag bits, a second MOCV/MOTV in 31
   WotLK groups, and WotLK `groupLiquid` values 3–8 instead of the 1.12/TBC constant 15. The 1.12 MOBA/MLIQ/MOPY
   layouts match every group in every version.
5. **M2**: 2.4.3 doodads are versions 260–263 with the 1.12 header layout (0x144 bytes, 4 embedded views). Bones are
   0x70 bytes (1.12: 0x6C) and rotations are int16×4 instead of float×4. Particles (0x1F8) and sequences (0x44) are
   unchanged. 3.3.5 doodads are all v264: 0x130 header, external `.skin`, 0x40 sequences, 0x58 bones, 0x1DC
   particles and per-sequence timestamp tracks. Only 1 of 5,859 placed doodads needs a `.anim` file. 3,404 of the
   5,859 WotLK MMDX names (and 3,257 of 4,230 TBC names) also exist in 1.12 under the same path.
6. **Textures**: ground textures are BLP2 DXT 256×256 in all versions. WotLK adds three 512×512 textures and four
   768×128 cube maps (MCLY 0x400). Specular `_s` files use DXT5 more often. There are no `_h` height textures. The
   minimap `md5translate.trs` format and the tiles (DXT1 256×256, no mips) are identical in all three versions.
7. **DBC**: column meanings mostly carry over. The main mechanical change is that **localized strings grow from 9 to
   17 fields** (16 locales + flags), which shifts every later column. Bigger reworks: WotLK `LiquidType` (4 → 45
   fields), `GroundEffectTexture` (7 → 11), `GroundEffectDoodad` (field order), `Light` (5 → 8 param slots) and `Map`.
   The column mapping was measured by matching values on shared IDs (section 6).
8. **Recommended order**:
   1. A reader (mount rule, bzip2, versioned DBC layouts).
   2. A TBC terrain/WMO pass-through import, end to end, including DBC rows, patch and server extraction.
   3. An M2 260–263 → 256 converter.
   4. WotLK terrain (WDL rewrite, alpha decode/requantize, MH2O → MCLQ, MCSE, drop MCCV).
   5. A WotLK WMO clean-up.
   6. An M2 264 → 256 converter (the hardest part).

   See section 8.

---

## 1. Archive mount order

### 1.1 Evidence in the executables (MEASURED)

`exexref.py` finds every `.MPQ` name string and the code or data that references it. `exetable.py` and `exedis.py`
(capstone) dump the tables and the mount functions. Addresses are virtual addresses in each `Wow.exe`.

| | 2.4.3 (`Wow.exe`) | 3.3.5a (`Wow.exe`) |
|---|---|---|
| Patch-list builder | `0x405260` | `0x405AB0` |
| Mount function | `0x4054E0` | `0x405DD0` |
| Base-archive table | `0x90F160`: 20 entries × 16 bytes `{name, kind, layout mask, mounted byte}` | `0xAB6168`: 28 entries × 16 bytes |
| Patch-name entries | 12 × 16 bytes on the stack `{pass, set, dir, name}` | 12 × 20 bytes `{pass, set, prefixFlag, dir, name}` |
| Sort comparator | `0x4011B0` = `-strnicmp(a, b)`: **descending** | `0x401200` = `-strnicmp(a, b)` (CRT `_strnicmp`-like, case-insensitive) |
| Open call | `0x654110(name, priority, 0, &h)` | `0x421950(name, priority, 0xC00, &h)` |

**What the code does (identical logic in both, MEASURED):**

1. **The patch list.** The builder walks its 12 entries twice.
   * Pass 0 handles the glob entries: `Data\` + `patch-?.MPQ` and `Data\<loc>\` + `patch-<loc>-?.MPQ`. Each match is
     added as `"%s%s"` (dir + file name). After pass 0, the list is **qsorted descending** with the comparator above.
   * Pass 1 appends the exact names. In TBC: `Data\<loc>\patch-<loc>.MPQ`, then `Data\patch.MPQ`. In WotLK:
     `Data\patch.MPQ`, then `Data\<loc>\patch-<loc>.MPQ`.
   * A selector picks between two sets of entries. Set 1 contains explicit `patch-3/-4.MPQ`, `patch-<loc>-3/-4.MPQ` and
     `..\Data\` paths. It is dead code in retail: TBC's selector `0x65A3F0` is `xor al, al; ret`, and WotLK pushes a
     constant 0.
2. **Patch mount.** The mount function walks that list **from the last entry to the first**. It opens each archive
   with priority starting at **0x40** and increasing by 1 after each archive that opens. So list entry 0 (the
   alphabetically highest glob match) gets the highest priority.
3. **`alternate.MPQ`** (kind 3) would be mounted next, above all patches, and again inside each patch archive. It is not
   present in either install.
4. **Base archives.** They are mounted in table order with priority starting at **0x3F** and **decreasing**, so the
   earlier table entries win.
   * The layout mask selects the archives. Mask 1 = the 1.x-style split archives (`model.MPQ`, `texture.MPQ`, …), used
     when `common.MPQ` is absent. Mask 2 = the `common/locale` layout. Mask 3 = both.
   * TBC's check is `0x402540`, which tests for `common.MPQ`.
   * A missing kind-1 archive is a fatal "Failed to open archive". A missing expansion archive (kind 2) is tolerated.
   * WotLK also skips kind-4 entries (`streaming.MPQ`, `streamingloc.MPQ`).

Format knowledge: Storm searches archives in priority order, highest first. The data in section 1.3 agrees.

### 1.2 Derived mount order (highest priority first)

**1.12.1** (from our decompile, `ClientData.ClientArchiveOrder`):

| # | Archive |
|---|---|
| 1 | `patch-?.MPQ` (one character, sorted descending) |
| 2 | `patch.MPQ` |
| 3 | `speech2.MPQ` (if present) |
| 4–13 | `model`, `texture`, `terrain`, `wmo`, `sound`, `misc`, `interface`, `fonts`, `speech`, `dbc` |

`base.MPQ` and `backup.MPQ` are never mounted.

**2.4.3 (this install, locale enGB):**

| # | Archive | Priority | Confidence |
|---|---|---|---|
| 1 | `Data\patch-2.MPQ` | 0x43 | High (code + data) |
| 2 | `Data\enGB\patch-enGB-2.MPQ` | 0x42 | High |
| 3 | `Data\enGB\patch-enGB.MPQ` | 0x41 | High (code); never conflicts with #4 in the data |
| 4 | `Data\patch.MPQ` | 0x40 | High |
| 5 | `expansion.MPQ` | 0x33 | High (code); never conflicts with #6 |
| 6 | `common.MPQ` | 0x32 | High |
| 7 | `enGB\locale-enGB.MPQ` | 0x31 | High |
| 8 | `enGB\speech-enGB.MPQ` | 0x30 | High |
| 9 | `enGB\expansion-locale-enGB.MPQ` | 0x2F | High |
| 10 | `enGB\expansion-speech-enGB.MPQ` | 0x2E | High |

`enGB\base-enGB.MPQ` and `enGB\backup-enGB.MPQ` are not in the table, so they are never mounted.

**3.3.5a (this install, locale enUS):**

| # | Archive | Notes |
|---|---|---|
| 1 | `Data\patch-7.mpq` | **Not Blizzard.** A 2015/2016 server patch: 19 DBCs (AreaTable, Item, ItemDisplayInfo, ItemExtendedCost, Spell, SpellIcon, SpellItemEnchantment, …), 40 item models, 22 textures/icons. No map files. It wins over every patch because "patch-7" > "patch-3", and `Data\p…` > `Data\enUS\…` |
| 2 | `Data\patch-3.MPQ` | |
| 3 | `Data\patch-2.MPQ` | |
| 4 | `Data\enUS\patch-enUS-3.MPQ` | |
| 5 | `Data\enUS\patch-enUS-2.MPQ` | |
| 6 | `Data\patch.MPQ` | |
| 7 | `Data\enUS\patch-enUS.MPQ` | Below `patch.MPQ` (the opposite of TBC). The two share no files, so this has no effect |
| 8 | `expansion.MPQ` | |
| 9 | `lichking.MPQ` | |
| 10 | `common.MPQ` | |
| 11 | `common-2.MPQ` | |
| 12–17 | `enUS\locale-enUS`, `speech-enUS`, `expansion-locale-enUS`, `lichking-locale-enUS`, `expansion-speech-enUS`, `lichking-speech-enUS` | |

Confidence: high for every step. The order inside each chain is read from the code and confirmed by the data
(section 1.3). The base-archive order is read from the code. The data cannot confirm it, but it cannot matter either,
because those archives share no files.

**Custom patches.**
* The global glob only takes one character after `patch-`, so `patch-10.MPQ` is ignored. Letters sort above digits
  (case-insensitive), so `patch-A`…`patch-Z` beat `patch-9`.
* Every global `Data\patch-?.MPQ` beats every locale `patch-<loc>-?.MPQ` whose locale code sorts below "patch" (enUS,
  enGB, deDE, esES, esMX, frFR, koKR). For ruRU, zhCN and zhTW the locale glob patches sort above the global ones, a
  quirk of sorting full paths.
* Our 1.12 rule (`patch-?.MPQ` descending, then `patch.MPQ`) is the same idea without locale archives.

### 1.3 Cross-checks in the data (MEASURED, `overlap_*.txt`, `where_trs.txt`)

The (attributes) FILETIMEs are zero in every Blizzard archive, so newness was judged from content instead.

**Which archive pairs share files at all.** Only these pairs (higher priority first) share any files:
* inside the global chain: `patch-N > patch > base`;
* inside the locale chain: `patch-<loc>-N > patch-<loc> > locale/speech`;
* `patch-7.mpq > patch-enUS*/locale-enUS`, through its 18 DBCs.

No two base archives share a file. `patch.MPQ` and `patch-<loc>.MPQ` share none.

**DBC row counts follow the derived order: newer = more rows or the final layout.**

| File | Copies, winner first |
|---|---|
| 2.4.3 `AreaTable.dbc` | patch-enGB-2 1,643×35 = patch-enGB 1,643×35 > locale-enGB 1,534×27 |
| 2.4.3 `Spell.dbc` | patch-enGB-2 28,315×**216** (the 2.4.3 layout) > patch-enGB 28,162×213 > locale-enGB 29,428×179 |
| 3.3.5 `AreaTable.dbc` | patch-7 2,307 = patch-enUS-3 2,307 > patch-enUS-2 2,306 > patch-enUS 2,283 > locale-enUS 2,170 |
| 3.3.5 `Spell.dbc` | patch-enUS-3 49,839×**234** (the 3.3.5 layout) beats patch-enUS-2 49,208×240 |
| 3.3.5 `textures\Minimap\md5translate.trs` | patch-3 19,089 lines > patch-2 19,073 > patch 18,704 > common-2 17,265 |
| 2.4.3 `md5translate.trs` | patch 10,975 lines > common 10,699 |

**Delete markers** (they hide all lower copies):

| Version | Delete markers |
|---|---|
| 2.4.3 | 8,923 in `patch.MPQ`, 13 in `patch-2.MPQ`, 13 in `patch-enGB.MPQ` |
| 3.3.5 | 3,610 in `patch.MPQ`, 1,234 in `patch-2`, 39 in `patch-3`, 26 in `patch-enUS`, 2 in `patch-enUS-2` |
| 1.12 | 5,708 in `patch.MPQ` (as documented in ARCHITECTURE.md) |

### 1.4 Files our reader could not open (MEASURED with the `WoWData.Core.dll` built 07:49 today)

* **8 files in `lichking.MPQ` use bzip2 sectors (mask 0x10), and all are map-relevant winners:**
  * `TILESET\Expansion02\StormPeaks\SP_SnowA.blp` (a Northrend ground texture placed by the ADTs);
  * `World\Goober\G_PulsatingPlantNexus.blp`;
  * 6 doodad `.skin` files (`UL_Chandelier00`, `OgreMeatChain01_A00`, `Firearm_Rifle_Dalaran_D_0100`,
    `Sword_Dalaran_D_0100`, `OrangeScourgeBubbles00`, `Missle_rocket0200`).
* **9 files in `enUS\backup-enUS.MPQ`** have odd masks (0x89, 0xF3, 0xB1, 0xB4, 0xA8, 0x39, 0xE6, 0x3E, 0x18).
  They are installer/launcher files (PDF manuals, splash JPGs, `Battle.net.dll`, `Scan.dll`) in an archive that is never
  mounted. They are irrelevant.

`app\WoWData.Core\Mpq\Bzip2.cs` appeared in the source at 12:48 today (not yet in the DLL I used). It should cover the
8 map files. The TBC client opened and read everything.

---

## 2. Terrain: WDT, WDL, ADT

### 2.1 WDT (MEASURED, all maps)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| WDT files | 43 | 72 | 106 |
| MVER | 18 | 18 | 18 |
| Chunk sequence | `MVER MPHD MAIN MWMO [MODF]` | same | same |
| MAIN entry | 8 bytes, flag 0x1 only on continents | same | same |
| MPHD flags | 0 or 0x1 (WMO-only) | 0, 0x1; **0x4 on `Sunwell5ManFix`** | 0, 0x1, **0x2\|0x4** (12 maps), **0x2\|0x4\|0x8** (20 maps incl. **Northrend = 0xE**), 0x8 (1 dev map) |
| Continent MPHD | Azeroth 0, Kalimdor 0 | Azeroth, Kalimdor, Expansion01: 0 | Azeroth, Kalimdor, Expansion01: 0; Northrend 0xE |
| Continent tiles | Azeroth 687, Kalimdor 1,018 | 687, 1,018, Expansion01 800 | 687, **Kalimdor 988**, 800, Northrend 1,131 |

MPHD bits *(format knowledge)*: 0x2 = the ADTs have MCCV, 0x4 = big alpha (8-bit/compressed), 0x8 = doodad refs
sorted by size. The 1.12 reader `CMap_LoadWdt` reads positionally and only tests bit 0x1 (`WdtFile`). A converted
WDT should keep only bit 0x1.

### 2.2 WDL (MEASURED, all maps)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| Chunk sequences | `MVER MAOF` (21), `MVER MAOF MARE*` (22) | `MVER MAOF` (38), `MVER MAOF MARE* MAHO*` (34) | `MVER MWMO MWID MODF MAOF` (40), `MVER MWMO MWID MODF MAOF MARE* MAHO*` (66) |

* The 1.12 client (`CMap_LoadWdl`, mirrored by `WdlFile`) expects the `MAOF` table at file offset 12.
  * TBC WDLs keep that layout. Their `MAHO` (hole masks) sits behind each `MARE`, and the 1.12 client never reads it.
  * **WotLK WDLs put `MWMO MWID MODF` (low-detail WMOs) before `MAOF`, so the 1.12 client would read garbage.**
* **Conversion:** rebuild the WDL as `MVER MAOF MARE*`, with offsets recomputed. This is trivial.

### 2.3 ADT top level (MEASURED)

| | 1.12 (1,705) | 2.4.3 (2,505) | 3.3.5a (3,606) |
|---|---|---|---|
| MVER | 18 | 18 | 18 |
| Sequence | `MVER MHDR MCIN MTEX MMDX MMID MWMO MWID MDDF MODF MCNK×256` (all) | 1,705 × the 1.12 sequence; 800 × the same + **`MFBO`** after the MCNKs (Outland) | 1,275 × `… MODF MH2O MCNK…`; 1,012 × `… MH2O MCNK… MFBO`; 880 × `… MCNK… MFBO`; 400 × 1.12 sequence; 29 × `… MFBO MTXF`; 10 × `… MH2O … MFBO MTXF` |
| MH2O | – | – | 2,297 files (Azeroth 529, Kalimdor 746, Northrend 1,022; Outland 0) |
| MFBO (36 bytes) | – | 800 | 1,931 |
| MTXF | – | – | 39 files: 505 entries, 40 with flag 0x1 |
| MHDR flags | 0 | 0x1 in 800 (= MFBO present) | 0x1 in 1,919; **0x1\|0x2 in 12**; 0 in 1,675 |
| MHDR fields | 16 dwords; offsets for MCIN…MODF | + `ofsMFBO` (field 9) | + `ofsMFBO`, `ofsMH2O` (10), `ofsMTXF` (11) |
| MHDR offset → magic | always correct | always correct | always correct |
| MCIN | present, points at all 256 MCNKs | same | same (MCIN still present in 3.3.5) |
| MTEX names per tile | 0–20 | 0–20 | **0–27** |
| Size | < 3 MB | < 3 MB | < 3 MB (148 files 2–3 MB) |

* The 1.12 client parses only the 8 MHDR offsets MCIN…MODF (`CMapArea_ParseAdtHeader`, ported as
  `MapBytes.MhdrChunks`), so extra top-level chunks are ignored by the game.
* Our `MapValidator` does flag them, though: `MapBytes.WotlkTopChunks` contains MH2O, MFBO and MTXF. A converter
  should strip them so the file passes our validator. A TBC Outland ADT is otherwise a valid 1.12 ADT.
* MHDR flag 0x2 *(format knowledge: "is Northrend")* has no 1.12 meaning.

### 2.4 MCNK header (MEASURED: the share of chunks where each dword is non-zero)

The header is 0x80 bytes in all versions, with the same field positions. The differences:

| Offset | 1.12 field | 2.4.3 | 3.3.5a |
|---|---|---|---|
| 0x00 | flags | **0x8000 in 282,624 chunks** (all of Outland; 1.12 only 5,888) | **0x40 in 289,536** (all Northrend = MCCV present); 0x8000 in 634,112 |
| 0x30 | sizeShadow | 0 when no MCSH | 0 when no MCSH |
| 0x3C | holes | low 16 bits only (1,988 chunks) | low 16 bits only (2,730) |
| 0x5C | nSndEmitters | 0 on continents | 262 chunks |
| 0x60/0x64 | ofsLiquid / sizeLiquid | always set (8 = empty) | **0 in 588,032 chunks** (MH2O tiles); 8 in 273,230; >8 in 61,874 (Outland) |
| 0x74 | ofsMCCV (unused in 1.12) | 0 | 289,536 (Northrend) |
| 0x78/0x7C | – | 0 | 0 (no MCLV) |

**MEASURED (decompile).** The 1.12 client:
* takes every sub-chunk through the header offsets plus 8 (`CMapChunk_SetupMcnkPointers @006af970`). It never checks
  magics or chunk sizes.
* reads shadow and `sizeAlpha` only when flag 0x1 is set (`CMapChunk_FinishLoad @006af5f0`), so the TBC/WotLK chunks
  without MCSH are safe.
* **honours flag 0x8000.** `CMapChunk_PackAlphaAndShadow @006b0350` picks the `*_NoEdgeFix` packers when it is set and
  the `*_EdgeFix` packers otherwise. Keep the bit as it is.
* builds liquids for flag bits 0x4, 0x8, 0x10 and 0x20 only (`CMapChunk_CreateLiquids @006af760`, one 0xC9-dword layer
  each). Flag 0x40 has no 1.12 meaning and should be cleared.
* reads MCSE as 0x34-byte records (`CMapChunk_CreateSoundEmitters @006af810`).

### 2.5 MCNK sub-chunks (MEASURED)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| Walked order | `MCVT MCNR MCLY MCRF MCSH MCAL MCLQ MCSE` | same; Outland without `MCSH` | `MCVT [MCCV] MCNR MCLY MCRF [MCSH] MCAL [MCLQ] MCSE` |
| MCVT / MCNR | 580 / 435 + 13 pad | same | same |
| MCSH | all chunks (512 bytes) | 491,714 of 641,280 | 550,774 of 923,136 (none in 222,796 Northrend chunks) |
| MCCV | – | – | 289,536 (Northrend only) |
| MCSE record | 52 bytes (9 emitters) | none on the continents (size not measured) | **28 bytes** (262 chunks) |
| MCLY 16-byte records | flags 0 or 0x100 (+ animation bits 0x1–0x80) | same | + **0x200 compressed alpha** (369,401 layers), + **0x400 cube-map reflection** (968 layers) |
| MCLY effectId | GroundEffectTexture id | same | 0 in 305,732 layers |

Header offset → magic: in TBC/WotLK, `ofsShadow` points at `MCAL` when there is no shadow (149,566 / 372,362
chunks).

### 2.6 Alpha maps (MEASURED per layer)

| Map (version) | Layer formats |
|---|---|
| All 1.12 | 4-bit, 2,048 bytes (549,345 layers) |
| All 2.4.3 continents | 4-bit, 2,048 bytes (744,826) |
| 2.4.3 `Sunwell5ManFix` (MPHD 0x4) | 4,433 compressed + 402 uncompressed 4,096-byte |
| 3.3.5 Azeroth / Kalimdor / Outland | 4-bit, 2,048 bytes (748,657) |
| 3.3.5 **Northrend** (MPHD 0x4) | **369,401 compressed** (every one decodes to exactly 4,096 values and uses exactly its bytes) + **16,985 uncompressed 4,096-byte** |

* Compression *(format knowledge, verified by the exact fit above)*. Each control byte has bit 0x80 = fill
  (repeat the next byte n times) or copy (copy the next n bytes), with n = the low 7 bits.
* **The MCAL chunk-size field is wrong in 248,237 of 289,536 Northrend chunks** (and in 2,764 Sunwell chunks, 413 in
  1.12). `MCNK.sizeAlpha − 8` is correct in every chunk of every version. **Readers must use `sizeAlpha`.**
* Conversion to 1.12:
  1. Decode to 64×64 8-bit.
  2. Quantize to 4 bits (`v >> 4`, or dithered), packed low nibble first.
  3. Keep flag 0x8000 as it is.
  4. Clear MCLY 0x200 and 0x400.

  When a 4-bit map would lose layer ordering (the 1.12 shader blends layers in sequence), check the result visually.
  This is format knowledge, not measured.

### 2.7 Liquids (MEASURED)

**MCLQ (1.12, 2.4.3, 3.3.5 Outland).**
* 804 bytes per layer, one layer per flag bit (river 0x4, ocean 0x8, magma 0x10, slime 0x20).
* 1.12: 229,426 chunks have 1 layer and 28 have 2.
* 2.4.3: 291,290 have 1 and 38 have 2.
* 3.3.5 Outland: 61,864 have 1 and 10 have 2.

**MH2O (3.3.5 Azeroth, Kalimdor, Northrend).**

| | Value |
|---|---|
| Header | 256 × `{ofsInstances, layerCount, ofsAttributes}` |
| Chunks with liquid | 448,564 (1 layer: 448,473; 2 layers: 91) |
| Attributes (fishable/deep 64-bit masks) | 77,340 chunks |
| Instance (24 bytes) | `u16 LiquidType, u16 LVF, f32 min, f32 max, u8 x, y, w, h, u32 ofsExistsBitmap, u32 ofsVertexData` |
| Rectangles | 432,385 full 8×8, 16,270 partial |
| Exists bitmap | 20,900 instances have one; 427,755 cover all tiles |
| Vertex data | 81,979 have per-vertex data; 366,676 are flat (min/max only) |
| LiquidType ids | 2 (Ocean) 432,903; 5 (Slow Water) 14,870; 81 (Lake Wintergrasp) 638; 7 (Slow Magma) 127; 1 (Water) 68; 181 (Orange Slime) 49 |
| Bytes per vertex by (type, LVF) | Ocean LVF 2: **1.0** (depth only); water types LVF 0: **5.0** (float height + u8 depth); magma LVF 1: **8.0** (float height + 2×u16 UV) |

The second u16 is the vertex format. It agrees with WotLK `LiquidMaterial.dbc` (material 1 → LVF 0, material 2 → LVF
1; MEASURED rows). Ocean uses LVF 2 even though its material says 0.

**Conversion MH2O → MCLQ.**
1. Choose the MCNK flag from WotLK `LiquidType.Type` (field 3: 0 water, 1 ocean, 2 magma, 3 slime; MEASURED rows).
2. Build 9×9 heights: the per-vertex height where present, else `min`.
3. Build the 8×8 tile flags from the rectangle and exists bitmap (no liquid = low nibble 0xF; *format knowledge*).
4. Fill MCLQ depth/flow from LVF 0 depth bytes.

Special types (Wintergrasp water, Orange Slime with a lava texture, Coilfang/Hyjal waters) only exist as IDs. 1.12
`LiquidType.dbc` has 4 fields and no texture, so they fall back to the basic kind (see section 8).

### 2.8 Placements (MEASURED)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| MDDF entries (36 bytes) | 334,916, flags 0 | 546,097, 6 × flag 0x1 | 852,038; 6 × 0x1, **812 × 0x2** |
| MODF entries (64 bytes) | 3,724, flags 0 | 5,862, flags 0 | 8,241; **64 × flag 0x1** |
| MMDX name extension | `.mdx` | `.mdx` | **`.M2`** (all 5,859). WMO MODN names stay `.mdx`/`.mdl` |
| Distinct MTEX / MMDX / MWMO | 318 / 2,945 / 598 | 428 / 4,230 / 893 | 705 / 5,859 / 1,330 |

Clear the MDDF/MODF flags for 1.12. Format knowledge: the 1.12 client reads `.mdx` names and maps them to `.m2`
(`ClientData.ResolveName` mirrors `M2Cache_GetShared`). Whether it accepts a literal `.m2` name is not verified, so
write `.mdx` names.

---

## 3. WMO (MEASURED, every `.wmo` reachable in each client)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| Roots / groups | 815 / 5,220 | 1,204 / 6,679 | 1,985 / 9,346 |
| MVER (root and group) | 17 | 17 | 17 |
| Root sequence | `MVER MOHD MOTX MOMT MOGN MOGI MOSB MOPV MOPT MOPR MOVV MOVB MOLT MODS MODN MODD MFOG [MCVP]` | same | same |
| Record sizes MOMT/MOGI/MOPT/MOLT/MODS/MODD/MFOG | 64/32/20/48/32/40/48 | same | same |
| MOHD flags (u16 at 0x3C) | always 0 | 0x1 in 71 | 0x1 (317), 0x1\|0x4 (440), 0x1\|0x2\|0x4\|0x8 (124), 0x1\|0x2 (19), 0x1\|0x2\|0x4 (19) |
| MOMT shader | 0, 1 (38), 3 (1) | + **2** (17), **5** (377) | 0, 1, 2, 3, **5** (1,273), **6** (52) |
| MOMT blend | 0, 1, 2 | 0, 1, 2 | 0, 1, 2, **3, 5** |
| Group sequence | `MOPY MOVI MOVT MONR MOTV MOBA [MOLR] [MODR] MOBN MOBR [MOCV] [MLIQ]` | same | same; 31 groups add a **second `MOTV` and `MOCV`** at the end |
| MOPY bytes per triangle | 2 | 2 | 2 |
| MOBA with the 1.12 offsets (start @12, count @16) | all groups in range | all | all |
| MOGP header | 0x44 bytes; flags, …, `groupLiquid` @0x34, id @0x38 | same | same |
| New MOGP flag bits | – | – | 0x100000 (7), 0x200000 (383), 0x800000 (5), 0x1000000 (31), 0x2000000 (31), 0x80000000 (322) |
| MOCV vs flag 0x4 | always together | always together | always together; 31 groups with flag 0x4 have 2 MOCV |
| MLIQ groups | 302, all with the 1.12 size (30 + verts×8 + tiles) | 333, same | 406, same |
| `groupLiquid` of MLIQ groups | always 15 | always 15 | 15 and also 3, 4, 5, 7, 8 |
| WMOs placed by continent ADTs | 598 | 893 (548 byte-identical to 1.12) | 1,330 (458 byte-identical to 1.12) |

**What 1.12 cannot read.** Structurally, nothing. `WmoGeometry` reads all three versions; for the 31 two-set groups it
takes the first MOTV.

**What changes meaning.**
* MOHD flags *(format knowledge: 0x1 do not attenuate vertices, 0x2 no base colour / unified lighting, 0x4
  `groupLiquid` is a LiquidType id, 0x8 LOD)*.
* Shaders 2/5/6 *(format knowledge: 5 = environment metal with a second UV set, 6 = two-layer terrain-like)*.
* WotLK `groupLiquid` values.

Converter: map shaders 2/5/6 to 0 or 1, clear the new MOHD/MOGP bits, drop the second MOTV/MOCV, and set
`groupLiquid = 15` with the liquid kind written into the MLIQ tile flags *(format knowledge)*. Doodad lists inside WMOs
(MODN) reference M2s, which need the same conversion as in section 4.

---

## 4. M2 doodads (MEASURED on every model the continent ADTs place, and on every MODN doodad of the placed WMOs)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| MMDX doodads, by version | 2,945 × v256 | 3,863 × v260, 237 × v263, 69 × v261, 61 × v262 | 5,859 × **v264** |
| WMO (MODN) doodads | 1,838 × v256 | 2,335 / 187 / 58 / 39 (v260/263/261/262) | 3,901 × v264 |
| Also present in 1.12 under the same path | – | MMDX 3,257 of 4,230; MODN 2,296 of 2,889 (none byte-identical) | MMDX 3,404 of 5,859; MODN 2,468 of 4,080 |
| Header (name offset) | 0x144 → name at 0x150 | the **1.12 layout**: all 31 arrays in range for every model, name at 0x150 (0x160 with global flag 0x8) | **0x130** (0x140 with flag 0x8 = textureCombinerCombos at 0x130); all 28 arrays of the 3.x layout in range |
| Views | 4 embedded (every model) | 4 embedded (every model) | external `.skin`: 5,797 have 1, 46 have 2, 16 have 4; every file present |
| `.anim` | – | – | 5,858 with every sequence inside the `.m2`; 1 needs `.anim` (present) |
| M2Sequence | 0x44 | 0x44 | **0x40** |
| Bone | 0x6C | **0x70** (+4-byte CRC) | **0x58** |
| Rotation keys | float×4 | **int16×4** (compressed) | int16×4 |
| Track | 0x1C (interp, gseq, ranges, timestamps, values) *(1.12 schema)* | 0x1C (bone fits prove it) | 0x14, per-sequence timestamp/value arrays *(format knowledge; bone size proves 0x14)* |
| Particle emitter | 0x1F8 | 0x1F8 | **0x1DC** |
| Global flags seen | 0, 0x1\|0x2 | + 0x8, 0x10 | + 0x8, 0x10 |
| Blend modes | 0, 1, 2, 4, 5, 6 | same | same (no 7) |
| Render flags | 0x1–0x10 | 0x1–0x10 | + **0x40, 0x100** (a few dozen materials) |
| Features (MMDX) | particles 211, lights 13 | particles 407, ribbons 1, cameras 3, lights 17 | particles 622, ribbons 4, lights 20, cameras 6 |

1.12 accepts only versions 0x100 and 0x101 (`M2Header`, `M2Schema`).

**TBC → 1.12 (moderate).** Set the version, remove the 4-byte bone CRC (0x70 → 0x6C, then fix every offset),
decompress the quaternions to float×4, and keep views and tracks. Format knowledge: other v260+ differences may exist
(for example extra texture/flag semantics). Our round-trip M2 tests would find them.

**WotLK → 1.12 (hard).**
* Inline `.skin` 0 as the 4 embedded views (all 1.12 models have 4). Format knowledge: replicate skin 0 when fewer
  exist, and rebuild submesh/texture-unit records in the 1.12 form.
* Convert per-sequence timestamp arrays into one global timeline with ranges, and sequences 0x40 → 0x44 (start/end
  instead of duration).
* Expand every track 0x14 → 0x1C.
* Resize bones, particles, ribbons, lights, colors, transparency, texture transforms, attachments, events and cameras
  to their 1.12 sizes.
* Map render flags 0x40/0x100 away.
* Read the one external `.anim`.

**Priority shortcut.** Many placed doodads also exist in 1.12 under the same path. Using the 1.12 model there needs
no conversion at all; the art may be older. That leaves 973 TBC and 2,455 WotLK MMDX models to convert.

---

## 5. Textures and minimaps (MEASURED)

| | 1.12 | 2.4.3 | 3.3.5a |
|---|---|---|---|
| Ground textures (MTEX) | 318: BLP2 type 1, DXT1 (enc 2, alphaDepth 0), 256×256, 128×128 (4) | 428: same | 705: same + **512×512 (3)** + **768×128 (4: cube maps `TCB_*`, `tgatest`)** + 1 unreadable (bzip2) |
| Also in 1.12, byte-identical | – | 323 | 328 |
| `_s` companions | 315 of 318; DXT3 (alphaEnc 1) | 424 of 428; DXT3, **11 DXT5 (alphaEnc 7)** | 700 of 705; DXT3 422, **DXT5 274** |
| `_h` height textures | none | none | none |
| `md5translate.trs` | in `patch.MPQ`; `dir: <map>` + `<map>\mapXX_YY.blp<TAB><32-hex>.blp` | same format; 10,681 entries | same format (from `patch-3.MPQ`); 18,644 entries |
| Continent minimap tiles | 1,724, all BLP2 DXT1 256×256, no mips | 2,524, same | 3,640, same |
| Unhashed `mapXX_YY.blp` names | 0 | 0 | 0 |

* Our BLP rules from the decompile accept DXT alpha encoding 7 and sizes up to 512 (ARCHITECTURE.md), so DXT5 `_s`
  and 512×512 need no change.
* The 768×128 cube maps are neither power-of-two nor usable by 1.12 (MCLY 0x400 has no 1.12 meaning). Replace those
  layers with a plain ground texture.
* Minimaps: copy the hashed BLPs. Ship a merged `md5translate.trs`: there is one file per client, so our patch must
  carry the full merged copy. Use a new map directory name if it would collide (WotLK "Azeroth" ≠ 1.12 "Azeroth").

---

## 6. DBC tables a map needs (MEASURED: records × fields, bytes per record, archive of the winning copy)

| Table | 1.12.1 | 2.4.3 | 3.3.5a | Our 1.12 layout |
|---|---|---|---|---|
| Map | 44×42 (168) patch-2 | 83×125 (500) patch-enGB-2 | 135×66 (264) patch-enUS-3 | 42 fields |
| AreaTable | 1,081×25 (100) | 1,643×35 (140) | 2,307×36 (144) patch-7 (custom; same size as patch-enUS-3) | 25 |
| WMOAreaTable | 21,115×20 (80) | 16,234×28 (112) | 22,550×28 (112) | 20 |
| LoadingScreens | 40×3 | 60×3 | 91×**4** | 3 |
| LiquidType | 5×4 | 7×4 | 26×**45** (180) | 4 |
| Light | 374×12 | 580×12 | 715×**15** | 12 |
| LightParams | 426×9 | 582×9 | 850×9 | 9 |
| LightIntBand / LightFloatBand | 7,668 / 2,556 ×34 | 10,476 / 3,492 ×34 | 15,300 / 5,100 ×34 | 34 |
| LightSkybox | 6×2 | 15×**3** | 124×3 | 2 |
| ZoneMusic | 99×8 | 218×8 | 369×8 | 8 |
| ZoneIntroMusicTable | 43×5 | 114×5 | 168×5 | 5 |
| SoundAmbience | 68×3 | 138×3 | 206×3 | 3 |
| SoundEntries | 4,623×29 | 8,019×29 | 12,941×**30** | 29 |
| AreaTrigger | 432×10 | 755×10 | 1,220×10 | 10 |
| WorldMapArea | 51×8 | 68×**9** | 108×**11** | 8 |
| WorldMapOverlay | 526×17 | 818×17 | 988×17 | 17 |
| WorldMapContinent | 2×13 | 3×13 | 4×**14** | 13 |
| WorldMapTransforms | absent | 2×9 | 9×10 | – |
| GroundEffectTexture | 12,742×7 | 21,963×7 | 24,981×**11** | 7 |
| GroundEffectDoodad | 443×3 | 529×3 | 580×3 (different order) | 3 |
| TerrainType / TerrainTypeSounds | 11×6 / 9×1 | 11×6 / 9×1 | 12×6 / 10×1 | 6 / 1 |
| AreaPOI | 339×29 | 546×45 | 738×54 | 29 |
| WorldSafeLocs | 122×14 | 491×22 | 685×22 | 14 |
| TaxiNodes | 85×16 | 189×24 | 364×24 | 16 |
| DungeonMap, DungeonMapChunk, MapDifficulty, LiquidMaterial | absent | absent | 55×8, 622×5, 187×23, 3×3 | – |

**Column mapping, measured by value.** For each named column of our 1.12 layout, I looked for the 2.4.3 or 3.3.5 field
that holds equal values on the rows with the same ID (numbers equal as u32, strings equal as text).

| Table | 2.4.3 | 3.3.5a |
|---|---|---|
| Map | 0–4 same (ID, Directory, InstanceType, PVP, MapName). MinLevel 13→21, MaxLevel 14→22, MaxPlayers 15→23, 16–18→24–26, AreaTableID 19→27, LoadingScreenID 38→62, BracketLevelRange 39→63, GroupJoin 40→64, IconScale 41→65 | ID 0, Directory 1, InstanceType 2, **new field 3**, PVP 3→4, MapName 4→5, AreaTableID 19→22, LoadingScreenID 38→57, IconScale 41→58. The level/player-limit columns have no reliable match (*format knowledge: they moved to MapDifficulty/LFGDungeons*) |
| AreaTable | fields 0–11 at the same index (Flags 97% equal); AreaName loc at 11; FactionGroupMask 20→28; LiquidType[4] 21–24 → 29–32 | the same, except Flags changed meaning (<50% equal) |
| WMOAreaTable | 0–11 at the same index; loc grows 9→17 | same |
| LoadingScreens | identical | ID, Name, FileName identical; + field 3 (*format knowledge: HasWideScreen*) |
| LiquidType | identical | ID 0, Name 1, SpellID 3→5. `Type` became field 3 with the enum 0 water / 1 ocean / 2 magma / 3 slime. Textures and material are in the row |
| Light | identical | 0–8 and param 5 match; WotLK has 8 param slots |
| LightParams | identical | LightSkyboxID 2→3 |
| LightInt/FloatBand | same layout (values differ only where the data changed) | same |
| ZoneMusic, ZoneIntroMusicTable, SoundAmbience, AreaTrigger, TerrainType(Sounds), WorldMapOverlay | identical | identical |
| WorldMapArea | 0–7 identical, + 1 field | 0–7 identical, + 3 fields |
| GroundEffectTexture | identical | Density 5→9, Sound 6→10. DoodadID[4] values differ: "none" is −1 in 1.12 and 0 in 3.3.5, plus 4 new weight fields |
| GroundEffectDoodad | identical (ID, IdTag, Path) | (ID, **Path**, Flags): the path moved to field 1 and IdTag is gone |
| SoundEntries | identical | + 1 field at the end |
| WorldSafeLocs, TaxiNodes, AreaPOI | loc grows 9→17; the following fields shift by +8 | same, plus extra AreaPOI fields |

**The general rule (MEASURED).** A 1.12 `loc` column is 9 fields (8 locales + flags). In 2.4.3 and 3.3.5 it is 17
(16 + flags), with English in slot 0. Every column after a loc string shifts by +8. MCLY `effectId` values are the
same GroundEffectTexture IDs: the 12,742 shared IDs have equal density and sound.

---

## 7. Server side (VMaNGOS) and what we already automate

What a new 1.12 map needs on the server:

| Item | Where | Notes |
|---|---|---|
| Map row | world DB `map_template` | VMaNGOS keeps Map.dbc in the DB (our `server_layouts_vmangos.txt`: entry, parent, map_type, linked_zone, player_limit, reset_delay, ghost entrance, map_name, script_name) |
| Area rows | world DB `area_template` | zone/parent, explore bit, flags, level, team, liquid_type |
| Extracted terrain | `Server\data\maps` | **mapextractor** reads `Map.dbc`, `AreaTable.dbc` and `LiquidType.dbc` and the 1.12-format ADTs (MCLQ only) from the client's MPQs |
| Collision | `Server\data\vmaps` | **vmapextractor** + **vmap_assembler** read WMO v17 and M2 v256 from the MPQs |
| Pathing | `Server\data\mmaps` | **MoveMapGen**, per tile |
| Graveyards, instance templates, area triggers | DB tables | *To verify for VMaNGOS.* |

So **the server tools work unchanged if the imported map is converted to 1.12 formats and shipped in `patch-3.MPQ`**
(including Map.dbc and AreaTable.dbc rows). The extractors know nothing about MH2O, v264 M2s or big alpha.

What we already automate:
* The CLI has `wds server-maps status | extract | install | undo` (`app\WoWData.Cli\Commands\MapCommands.cs`).
* These go through `ServerMapsUpdater` and `ServerMapsService`, which run `WoWDataTool\server\reextract.ps1`:
  * `mapextractor -e 2` for dbc;
  * `-e 1 -f 0` for maps;
  * `vmapextractor -l` + `vmap_assembler`;
  * `MoveMapGen <map> --tile x,y` for the touched tiles + 8 neighbours.
  * Our patched mapextractor reads `patch-3.MPQ`. All maps are always extracted, and the output goes to a staging
    folder with a change report.
* `swap_in.ps1` installs only changed files after a verified backup. It refuses while `mangosd` runs, and `-Restore`
  undoes it.
* DB rows (`map_template`, `area_template`) can be edited through the app's server-data views.

**Missing for imports:**
* creating new `map_template` / `area_template` rows from imported DBC rows;
* making sure the mmaps step builds every tile of a brand-new map, not just "touched" ones. A new map's files are all
  "new" in the report; check that `compare_data.py` counts them as touched;
* a 1.12-compatibility check of the imported files before packing. Today `MapValidator` would reject WotLK leftovers,
  which is the right safety net.

---

## 8. Conversion difficulty, recommended order, what 1.12 cannot show

### 8.1 Difficulty per item

| Item | TBC → 1.12 | WotLK → 1.12 |
|---|---|---|
| Archive reading (mount rule, listfiles, delete markers) | trivial (rule in 1.2) | trivial + bzip2 (8 map files) |
| WDT | trivial (copy; clear MPHD bits other than 0x1) | trivial |
| WDL | trivial (drop MAHO or keep) | trivial (rebuild without MWMO/MWID/MODF; recompute MAOF) |
| ADT structure (MHDR, MCIN, MCNK header) | trivial (strip MFBO, clear MHDR flags) | trivial (strip MH2O/MFBO/MTXF, clear MCNK 0x40, drop MCCV) |
| Heights, normals, holes, area IDs, placements | identical | identical (holes are low 16 bits only) |
| Alpha maps | trivial (4-bit already; Sunwell dev map: as WotLK) | moderate (decode RLE/8-bit using `sizeAlpha`, quantize to 4-bit, keep 0x8000) |
| Shadows (MCSH) | trivial (absent = flag clear, fine for 1.12) | trivial |
| Liquids | trivial (MCLQ already) | **hard** (MH2O → MCLQ: heights, rectangles, exists bitmaps, depth-only ocean, type mapping, 2-layer chunks) |
| Vertex colours (MCCV) | – | not representable (drop) |
| Sound emitters (MCSE) | trivial (none on the continents) | moderate (28 → 52-byte records, or drop) |
| Ground textures | trivial (BLP2 same; 323/428 identical to 1.12) | easy (copy; replace cube maps; 512² fine) |
| Minimap | trivial (copy BLPs, merge trs) | trivial |
| WMO | easy (v17; shader 2/5 → 0/1, MOHD flag) | moderate (shaders, flags, second MOTV/MOCV, groupLiquid → MLIQ flags, MOCV alpha semantics *format knowledge*) |
| M2 doodads | moderate (v260–263 → 256: bone CRC, quaternions) or reuse 1.12 copies | **hard** (v264: skins → views, timelines, track/record sizes, `.anim`) or reuse 1.12 copies |
| DBC rows (Map, AreaTable, WMOAreaTable, Light*, LoadingScreens, Zone/Sound, WorldMap*) | moderate (column maps above; loc 17 → 9; ID clashes) | moderate (+ LiquidType, GroundEffect* reworks; drop WotLK-only tables) |
| Server | trivial once client files are 1.12 (existing tools) + DB rows | same |

### 8.2 Recommended order of work (aimed at accurate map imports)

1. **Reader foundation.**
   * Version detection.
   * Mount order as the rule from 1.1, not a hard-coded list: globs, the descending sort and the exact names, which
     differ between TBC and WotLK.
   * bzip2.
   * Versioned DBC layouts for the map tables of section 6, generated from the measured mappings and verified on shared
     IDs.
2. **TBC terrain pass-through import**, for example one Outland zone, end to end:
   * WDT/WDL/ADT copy with the trivial fixes;
   * ground textures and minimap;
   * WMOs (548 of 893 already identical in 1.12);
   * Map/AreaTable/WMOAreaTable/LoadingScreens/Light* rows;
   * `patch-3.MPQ`, then the server extract, `map_template`/`area_template` rows, and an in-game test.

   This proves the whole pipeline with the smallest format risk. Doodads that already exist in 1.12 can be used as they
   are at first.
3. **M2 v260–263 → v256** for the remaining TBC doodads (973 MMDX + ~593 MODN names not in 1.12). Validate with the
   existing M2 validator and round-trip tests.
4. **WotLK terrain:**
   * WDL rewrite;
   * alpha decode/quantize;
   * MH2O → MCLQ (start with Azeroth/Kalimdor tiles: mostly ocean and flat water; then Northrend's partial
     rectangles and Wintergrasp);
   * MCSE convert or drop;
   * MCCV drop;
   * cube-map layers replaced.

   Check every converted tile with `MapValidator` and against the in-game look.
5. **WotLK WMO clean-up** (shaders, flags, liquids, second sets).
6. **M2 v264 → v256** (skins, timelines, records). Start with doodads that have 1 skin and only in-file sequences
   (5,797 / 5,858 of the placed models), particle-free ones first.
7. **Polish:**
   * LiquidType overrides through AreaTable;
   * Light/skybox rows (8 → 5 params);
   * GroundEffect doodad remapping (−1 vs 0, path order);
   * WorldMapArea/Overlay rows;
   * flight-path rows if needed.

### 8.3 What 1.12 cannot represent, and the proposed fallback

| WotLK/TBC feature | Fallback |
|---|---|
| MCCV vertex shading (all of Northrend) | Drop it. The terrain is lit like 1.12 terrain. Optional later: bake a subtle tint into the most-used textures per zone (not exact) |
| 8-bit alpha precision | Quantize to 4-bit (Floyd–Steinberg if banding shows) |
| Cube-map reflective layers (MCLY 0x400, MTXF 0x1, 768×128 BLPs) | Use the base texture without reflection |
| Liquid types beyond water/ocean/magma/slime (Wintergrasp, Orange Slime, Coilfang, …) | Nearest basic kind. Optionally an AreaTable LiquidType override with a new 1.12 LiquidType row (name + spell only; no custom texture) |
| MH2O depth-only oceans and per-vertex depth | MCLQ with flat heights = max; depth bytes kept where LVF 0 has them |
| MH2O fishable/deep attribute masks | MCLQ tile flags *(format knowledge: "fishable"/"fatigue" bits)* or drop |
| Flight bounds (MFBO) | Drop (no flying in 1.12) |
| WMO shaders 5/6, second UV/colour set, MOHD 0x2 lighting | Shader 0/1, first set only; interiors may look slightly brighter or darker |
| M2 render flags 0x40/0x100, newer particle features, texture combiners | Clear or approximate; the closest 1.12 blend mode |
| Multiple `.skin` LODs | Skin 0 copied into the 4 embedded views |
| WotLK-only DBCs (MapDifficulty, DungeonMap/Chunk for indoor map overlays, LiquidMaterial) | Not imported; instance difficulty becomes the server's business (`map_template`) |
| Light.dbc param slots 6–8, LightSkybox flags | Drop the extra slots and flags |
| Newer zone sounds/music | Import the SoundEntries rows + files (wav/mp3 work in 1.12) |

---

## Appendix A: probe scripts and outputs (scratch only)

Folder: `<a scratch folder, not in the repo>`.
These are read-only tools. They reference `WoWData.Core.dll` from `app\WoWData.Cli\bin\Release\net8.0-windows\win-x64`
(the build from 07:49 today) and only read `G:\WoW Clients and Patches\Clients\`.

| File | What |
|---|---|
| `mapdelta.csproj`, `Program.cs` | modes `order`, `overlap <ver>`, `maps <ver> [map…]`, `dbc`, `dump <ver> <table> [rows]`, `fails <ver> <archives…>`, `in112 <ver>`, `where <ver> <names…>`, `archive <mpq>`, `list` |
| `Vfs.cs` | the derived mount order (1.2) as code + a counter helper |
| `MapsProbe.cs` | WDT/WDL (all maps), continent ADTs (chunks, MHDR, MCNK, alpha incl. RLE decode, MCLQ/MH2O, MCSE, placements), ground BLPs, minimap |
| `WmoProbe.cs` | every WMO root/group |
| `M2Probe.cs` | header layout checks, record-size fitting (sequence, bone, particle), rotation storage, skins/anims, materials |
| `DbcProbe.cs` | DBC headers and the column mapping by value |
| `exestrings.py`, `exexref.py`, `exetable.py`, `exedis.py` | `Wow.exe` string, xref, table and disassembly dumps (Python 3.11 + capstone 5.0.7, run with `-I`) |
| `order.txt`, `overlap_{1.12,2.4.3,3.3.5}.txt`, `where_trs.txt` | section 1 |
| `exe_xref.txt`, `exe_tbc_mount.txt`, `exe_wotlk_mount.txt` | section 1.1 evidence |
| `fails_3.3.5.txt` | section 1.4 |
| `maps_{1.12,2.4.3,3.3.5}.txt`, `maps_2.4.3_sunwell.txt` | sections 2–5 |
| `set_{mmdx,modn,mwmo,mtex}_<ver>.txt`, `in112.txt` | asset sets and 1.12 overlap |
| `dbc.txt` | section 6 |

Rebuild and rerun: `dotnet build -c Release -o out`, then for example `out\mapdelta.exe maps 3.3.5 > maps_3.3.5.txt`.
WotLK takes about 2.5 minutes and TBC about 1 minute.
