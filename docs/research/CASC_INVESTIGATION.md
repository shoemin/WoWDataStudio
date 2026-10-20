# CASC clients: feasibility of reading other WoW versions offline

Research note, 2026-10-20. Read-only investigation for a possible experimental WoW Data Studio feature. Nothing was built
and nothing in the client copies was changed. MPQ-era clients (1.x to 3.x) are out of scope here.

**MEASURED** means a probe script read the real files and counted. **FORMAT KNOWLEDGE** means public format facts
(wowdev-wiki style) that were not checked separately. Unmarked numbers in tables are measured.

---

## Summary: how feasible is it, and what would it take?

**Reading the CASC clients offline is clearly feasible.** All of it was measured end to end.

* **One small reader covers every client.** A Python probe of about 400 lines did the whole chain. It read
  `.build.info`/`versions`, the build config, the local `.idx` index, the `data.###` archives, BLTE, the encoding file and
  the root file. It worked on WoD 6.2.3 and on all 10 Battle.net flavors (11 builds). All encoding and root files are
  local, and their MD5 checksums match.
* **Maps are complete locally.** We followed the full dependency chain of four whole continents in four clients: Era
  Azeroth, Retail Azeroth, MoP Classic Pandaria and WoD Draenor. The chain was WDT, ADT split files, terrain textures,
  WMOs and their groups and textures, M2 doodads and their skins and textures. That is 116,055 files, and every one is
  local. None of them is encrypted.
* **Encryption does not block maps or the main tables.**
  * Classic Era has 7 encrypted files in total.
  * Retail encrypts 11% of its files, mostly content that is not downloaded.
  * Core DB2 tables are encrypted only in a small tail section. For example, Retail SpellName has 195,096 of 195,300
    records in the clear.
  * The keys for those tails are mostly not on disk.
* **Names are mostly solvable without a download.**
  * WoD resolves by name hash. 99.998% of its names are matched by a listfile already on disk in
    `third_party\noggit-deps\casclib-src\listfile\listfile.txt`.
  * The Classic flavors hash every file name. The same listfile names 94-97% of their files.
  * Retail hashes only about 8% of its names. But modern maps reference everything by FileDataID, so conversion does not
    need names.
  * The FileDataIDs of DB2 tables come straight out of the client exe: 945 to 1,170 tables per exe, with no listfile.
* **The real cost is format conversion, not reading.** Bringing a modern map into 1.12 means several jobs:
  * merging split ADTs (root, obj0, obj1, tex0);
  * turning MH2O liquids into MCLQ;
  * mapping FileDataIDs to names;
  * shrinking textures (256 to 2048 px, DXT1 and DXT5);
  * downgrading WMOs (modern v17 has GFID, MODI and FileDataID materials);
  * downgrading M2 doodads from version 272/274 (chunked MD21, external skins) to version 256.

  The M2 downgrade is the largest single piece.

**Rough effort** (one developer, working days, including tests):

* CASC reader: 5-8
* names and DB2 IDs: 3-5
* DB2 reader: 8-12
* terrain, textures and liquids import to 1.12: 15-25
* WMO downgrade: 10-20
* M2 downgrade: 20-40

The reading side (about 3-5 weeks) is low risk. The map import side (about 2-4 months for everything) is where the risk
is. Terrain, textures and liquids alone are a useful first milestone. See section 9 for the order we recommend.

---

## 0. Sources and method

* Only `G:\WoW Clients and Patches\Clients\` was read (read-only, `open(..., 'rb')`). Executables were scanned as bytes.
  Nothing was launched. There was no network access and nothing was downloaded.
* Probe scripts (Python 3.11, run with `-I`) are in
  `<a scratch folder, not in the repo>`:

| Script | What it does |
|---|---|
| `casc.py` | library: `.build.info`/config parsing, `.idx` v7 reader, `data.###` reader, BLTE (N/Z/F, E detected or zero-filled), encoding, root (legacy and MFST v1/v2), Jenkins lookup3 name hash |
| `analyze.py`, `p3_run_all.py` | for each build: encoding, root, presence of every content key, encryption (from the encoding ESpec table plus a 3,000-file BLTE sample), name-hash coverage. Results in `results_*.json` |
| `content.py` | open by name or FileDataID, IFF chunk lists, DB2 header parser (WDBC/WDB2/WDB5/WDB6/WDC3-5) |
| `p1_wod.py`, `p2_wod_names.py`, `p4_wod_content.py`, `p13_wod_closure.py`, `p18_wod_versions.py` | WoD: chain, name lookups, DBC/DB2 headers, ADB caches, maps, full Draenor closure, asset versions |
| `p5_exe_strings.py`, `p6_exe_dbmeta.py` | strings and DB2 meta-table scan of the executables (`dbmeta_*.json`) |
| `p7_enc_orient.py`, `p9_keys.py`, `p10_dbcache.py`, `p15_fdid_records.py` | key-name byte order, which keys exist locally (names only, values never printed or stored), DBCache.bin layout, root records for one FileDataID |
| `p8_modern_content.py` | Battle.net flavors: all DB2 tables via exe FileDataIDs, WDT/MAID, split ADTs, references |
| `p11_map_closure.py` | full dependency closure of one modern map |
| `p12_residency_missing.py`, `p14_fdid_by_name.py`, `p16_asset_versions.py`, `p17_archive_headers.py` | per-product index folders and what is missing; name to FileDataID; asset versions; `data.###` header check |

* Load stayed modest. Index and encoding/root files were read in full (tens of MB). Archive contents were only sampled:
  3,000 BLTE headers per build, plus the map closures.

---

## 1. Layout per client

### WoD 6.2.3.20779 (MEASURED)

* Local CASC is in `data\`:
  * `data\data`: 29 archives (`data.000` to `data.028`), 32 `.idx` files (two generations per bucket), `shmem`;
  * `data\indices`: 291 CDN `.index` files;
  * `data\config`: 3 files;
  * `data\patch`: empty.
* `versions` (7 regions, all the same) gives:
  * BuildConfig `00647afeb4fe9bfc6b961a0a532a4baa`;
  * CDNConfig `325095a916ea88d9fc0c522be7260551`;
  * BuildId 20779.
* `cdns` names a non-Blizzard private host. It was not contacted.
* The build config has `build-name = WOW-20779patch6.2.3_Retail`, root, encoding (content key plus encoded key), install,
  download, and a patch config (`c0f613e8...`). The CDN config lists 238 archives and 51 patch archives. All of them
  have a local `.index`.
* `Cache\ADB\enUS\*.adb` holds 30 files of 50 bytes each, magic `WCH2`. All are header-only (no hotfix rows).
  `Hotfix.tbl` is 16 bytes, magic `XFTH`, and empty. So WoD has **no hotfix data** to merge.

### Battle.net snapshot (MEASURED)

* One shared store `Data\`:
  * `data`: 215 archives `data.000`-`data.214`, 16 `.idx` (v7), one `.lru`;
  * `indices`: 1,510 CDN `.index` files;
  * `config`: 14 files;
  * `ecache`: 16 small `.idx` and one 144 KB `data.000`; it holds 91 entries, 26 of which are also in the main index;
  * one folder per product (`wow`, `wow_classic`, ...).
* The per-product folders hold 16 `.idx` files each, but in **index version 8**: 16-byte keys and 8-byte offset and
  size, with only 35-39 entries. They also hold an empty `.residency` file. These files are **not needed** to read data:
  every file was resolved through `Data\data\*.idx` alone. We did not work out what they are for.
* `.build.info` header:
  `Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|CDN Key!HEX:16|Install Key!HEX:16|IM Size!DEC:4|CDN Path|CDN Hosts|CDN Servers|Tags|Armadillo|Last Activated|Version|KeyRing!HEX:16|Product`.
  It has 10 rows, all active.

| Flavor folder | Product | Version | Build config | build-name |
|---|---|---|---|---|
| `_retail_` | wow | 12.1.5.70448 | `88d5e8ef05ec5a8446cdec93d142d7c6` | WOW-70448patch12.1.5_Retail |
| `_ptr_` | wowt | 12.1.7.70424 | `328e394e14935f6e1017674de430603e` | WOW-70424patch12.1.7_PTR |
| `_xptr_` | wowxptr | 12.1.5.70340 | `d29f3a7796f46ad29526f03d6ff4b82c` | WOW-70340patch12.1.5_XPTR |
| `_classic_` | wow_classic (region kr) | 5.5.4.70469 | `2956a44a9c29d8960466db3472d48c35` | WOW-70469patch5.5.4_ClassicRetail |
| `_classic_ptr_` | wow_classic_ptr | 5.5.4.67849 | `04020a1762c81a0bf3d9d3aa6c5342fe` | WOW-67849patch5.5.4_ClassicPTR |
| `_anniversary_` | wow_anniversary | 2.5.6.70006 | `b3f09c9e3d47cbb9606fd0ccc0d831df` | WOW-70006patch2.5.6_Anniversary |
| `_classic_era_ptr_` | wow_classic_era_ptr | 2.5.6.70006 | `aedfea3d99ab76b4ba2a07cc0951b5b6` | WOW-70006patch2.5.6_ClassicPTR |
| `_classic_era_` | wow_classic_era | 1.15.9.70003 | `2417084b8b041ee120d02d4adf31b0bc` | WOW-70003patch1.15.9_ClassicRetail |
| `_classic_beta_` | wow_classic_beta | 1.60.1.70450 | `84bea4233331e390fda8724c6f91ffa5` | WOW-70450patch1.60.1_ForeverBeta |
| `_forever_` | wowf | 1.60.1.70245 | `e8c4ef4e67bd1d94d820254984258fa8` | WOW-70245patch1.60.1_Retail |

* All 10 rows use **CDN config `86e142712006071d5eb4b60e825c1f6e`** (1,423 archives and 78 patch archives, all with a
  local `.index`). It also lists `file-index` and `patch-file-index`.
* The config folder also holds:
  * an older CDN config `ff64ecfb...`;
  * a non-active build config `be681a48...` (wow_classic 5.5.4.70449);
  * the Retail **KeyRing** config `3ca57fe7...`, which holds one `key-<name>` line. Its value was masked and never
    printed. Its key name is not used by any file of the Retail build.
* The modern build configs also carry `vfs-root` and hundreds of `vfs-N` entries (TVFS). WoW's own files are still
  reached through the classic `root` file, so TVFS is not needed (FORMAT KNOWLEDGE; the root path was measured to work).

---

## 2. End-to-end local resolution (MEASURED)

| Item | WoD 6.2.3 | Battle.net store |
|---|---|---|
| `.idx` version / key / offset / size bytes / offset bits | 7 / 9 / 5 / 4 / 30 | 7 / 9 / 5 / 4 / 30 |
| `.idx` files used (newest per bucket) | 16 | 16 |
| Unique local encoded keys | 475,095 | 2,293,429 |
| `data.###` files | 29 | 215 |
| Random 500 entries: 30-byte entry header holds the reversed EKey, size matches the index, `BLTE` magic follows | 500/500 | 500/500 |

For every build: build config, then encoding (looked up by its encoded key in the local index), then root (content key,
through encoding, to encoded key). The decoded MD5 equals the content key in every case.

| Build | Encoding entries | ESpec strings | Root size | Root format |
|---|---|---|---|---|
| WoD 6.2.3.20779 | 822,453 | 70 | 24.5 MB | legacy (no header), 572 blocks |
| Era 1.15.9.70003 | 220,401 | 57 | 6.6 MB | MFST v2, 421 blocks |
| Anniversary 2.5.6.70006 | 271,066 | n/a | n/a | MFST v2 |
| MoP Classic 5.5.4.70469 | 784,094 | n/a | n/a | MFST v2 |
| Retail 12.1.5.70448 | 2,882,697 | 317,564 | 67.3 MB | MFST v2, 1,295 blocks |
| "1.60" beta / wowf | 2,209,148 / 2,506,810 | n/a | n/a | MFST v2 |
| PTR / XPTR / MoP PTR / Era PTR | 2,908,382 / 2,882,680 / 783,200 / 271,059 | n/a | n/a | MFST v2 |

**BLTE chunk modes observed** in 3,000 random local files per build: `Z` (zlib) and `N` (raw) everywhere, and `E`
(encrypted) in Retail, PTR, XPTR, MoP Classic and "1.60". `F` (nested frame) and LZ4 were never seen. The encoding file
itself uses N and Z chunks; the WoD root is a single Z chunk.

---

## 3. Root format per flavor (MEASURED)

| Build | Root | Records | Unique FileDataIDs | Records with a name hash |
|---|---|---|---|---|
| WoD 6.2.3 | legacy blocks: count, content flags, locale flags, FileDataID deltas, then (content key 16 B + name hash 8 B) per record | 874,632 | 521,822 | 874,632 (100%) |
| Era 1.15.9 | `TSFM` header (header size, version 2, total, named), v2 blocks | 236,259 | 159,749 | 236,259 (100%) |
| Anniversary 2.5.6 | same | 289,547 | 190,763 | 100% |
| MoP Classic 5.5.4 | same | 825,417 | 420,398 | 100% |
| "1.60" beta / wowf | same | 2,433,102 / 2,747,535 | 1,250,956 / 1,441,788 | 231,036 / 241,552 (about 9%) |
| Retail 12.1.5 | same | 3,261,808 | 1,932,662 | 254,287 (7.8%) |

* The WoD root already carries FileDataIDs, for example `World\Maps\Azeroth\Azeroth.wdt` = 775971, the same ID that
  modern clients use.
* **Locales.** Every build ships all 12-13 locales in its root. enUS is flagged on about 99% of FileDataIDs. WoD content
  flags seen: 0 (494k records), 0x80000000 (374k), 0x80 (6.5k; low-violence, FORMAT KNOWLEDGE).
* **v2 block header** (FORMAT KNOWLEDGE): count, locale, content flags in 3 parts (u32, u32, u8). Our decode agrees with
  the data:
  * Retail has 317,377 records with bit 0x08000000 ("encrypted"), which matches the 316,900 encrypted files in the ESpec
    table.
  * All of Retail's unnamed records carry 0x10000000 ("no name hash").
  * Retail's named records (254,287) are exactly those without that bit.

---

## 4. Completeness: what is downloaded locally (MEASURED, every content key, not a sample)

"enUS" means content keys flagged enUS or all-locale. Missing bytes are decoded sizes.

| Build | enUS files local | enUS files not local | All locales: local / not local | Decoded GB local / not local |
|---|---|---|---|---|
| WoD 6.2.3 | 474,443 (98.8%) | 5,572 | 474,443 / 347,824 | 47.9 / 14.2 |
| Era 1.15.9 | 145,519 (99.0%) | 1,512 | 146,040 / 73,946 | 9.5 / 4.1 |
| Anniversary 2.5.6 | 173,761 (99.1%) | 1,618 | 174,277 / 96,288 | 12.0 / 5.1 |
| MoP Classic 5.5.4 | 503,554 (99.0%) | 5,214 | 503,757 / 279,718 | 130.6 / 13.4 |
| Retail 12.1.5 | 1,568,009 (99.3%) | 10,612 | 1,568,035 / 1,313,639 | 237.3 / 70.8 |
| "1.60" beta | 1,127,189 (98.5%) | 17,658 | 1,127,787 / 1,080,630 | 164.8 / 40.9 |
| wowf 1.60.1 | 1,302,373 (98.6%) | 18,095 | 1,303,371 / 1,202,455 | 201.7 / 44.9 |
| PTR 12.1.7 | 1,583,071 (99.3%) | 10,659 | n/a | 241.9 / 71.5 |

* Almost everything "not local" is another locale's version of a file.
* The few enUS gaps are mostly textures, models and shaders. Grouped by name, WoD is 3,563 `.blp`, 1,280 `.anim`,
  647 `.skin`, 553 `.m2`. Era is 670 `.blp`, 357 `.bls` shaders, 231 `.skin`, 200 `.m2`.
* None of these gaps showed up in the map closures in section 8. We did not check whether they are other-platform,
  optional or unreleased content.

---

## 5. Encryption (MEASURED; key **names** only, values never printed or stored)

The encoding file's ESpec strings say which files are encrypted (`e:{KEYNAME,IV,...}`) for the **whole** build, local
or not. The text there is in raw byte order; the usual key name is the byte-reversed form, which is what BLTE `E`
chunks give after reversing.

| Build | Encrypted files (ESpec) | of them local | Distinct key names | Random local sample: files with an `E` chunk |
|---|---|---|---|---|
| WoD 6.2.3 | 0 | 0 | 0 | 0 / 3,000 |
| Era 1.15.9 | 7 | 7 | 2 | 0 / 3,000 |
| Anniversary 2.5.6 | 24 | 24 | 3 | 0 / 3,000 |
| MoP Classic 5.5.4 | 1,443 | 1,290 | 81 | 8 / 3,000 (11 keys) |
| "1.60" beta / wowf | 6,855 / 6,654 | 3,467 / 3,252 | 353 / 348 | 12 and 8 / 3,000 |
| Retail 12.1.5 | 316,900 (11%) | 32,610 | 20,512 | 57 / 3,000 (45 keys) |

**Where keys could come from, offline.** All of these are on disk:

* the `TactKey` and `TactKeyLookup` DB2 tables in each build;
* `TactKey` hotfix rows in `Cache\ADB\enUS\DBCache.bin`. The layout was checked: version 9, table hash at entry +16,
  and 12,404 of 26,785 Retail entries match known table hashes.

How far that gets us:

* Retail: 54 key values are local, and 21 of them are used by this build's files. That is 2,485 of 321,340 encrypted
  ESpec entries (0.8%).
* Anniversary: all 24 encrypted files have a local key.
* Era: 0 of 7. MoP Classic: 271 of 4,546.
* CascLib's built-in key table knows 253 of Retail's names, but the values are not ours to use without checking.
* The Retail `DBCache.bin` is stale: it was written by build 49570. Era and MoP Classic have no DBCache.bin in the
  snapshot.
* Anything else would need a community key list, which is a download, so it was not fetched.

**Encrypted DB2 tables are only partly encrypted.** At first it looked as if Retail `SpellName` was unreadable. It is
not. Only the last BLTE chunk is `E`, and it is the DB2's encrypted section.

| Build | DB2 tables (exe meta) | Fully encrypted | Partly encrypted | Key value local |
|---|---|---|---|---|
| Era 1.15.9 | 945 | 0 | 3 | 0 |
| Anniversary 2.5.6 | 947 | 0 | 10 | 10 |
| MoP Classic 5.5.4 | 952 | 0 | 105 | 2 |
| wowf 1.60.1 | 1,170 | 0 | 82 | 0 |
| Retail 12.1.5 | 1,137 | 0 | 144 | 18 |

Examples, Retail (zero-filled bytes, and records in clear sections):

* SpellName: 6,827 of 7.8 MB zero-filled; 195,096 of 195,300 records in the clear.
* ItemSparse: 175,503 of 175,758 records in the clear.
* AreaTable: 10,008 of 10,030 records in the clear.

MoP Classic shows the same pattern for its Spell/Item/CreatureDisplayInfo tables. Era has none of its core tables
affected. A reader should zero-fill an `E` chunk whose key is missing and skip the DB2 sections whose `tact_key_hash`
cannot be decrypted. That is the normal CASC-reader behaviour (FORMAT KNOWLEDGE).

**Maps:** none of the 116,055 files in the four map closures (section 8) has an encrypted chunk.

---

## 6. File naming (MEASURED)

### WoD

Name-hash lookup works. Jenkins lookup3 `hashlittle2` is applied to the uppercased path with backslashes, and the
64-bit value is `(c << 32) | b`. Lookups we checked:

* `DBFilesClient\Spell.dbc` resolves; it is localized, one record per locale.
* `DBFilesClient\Map.dbc`, `DBFilesClient\AreaTable.dbc`, `DBFilesClient\CreatureDisplayInfo.db2` resolve.
* `World\Maps\Azeroth\Azeroth.wdt` (FileDataID 775971) and its split ADTs resolve.
* `Interface\Icons\INV_Misc_QuestionMark.blp` resolves (case-insensitive).
* `Stormwind.wmo` and `HumanMale.m2` resolve.

The `.db2` spellings of tables that are still `.dbc` in 6.x do not resolve, as expected.

### Coverage by the listfile already on disk

`third_party\noggit-deps\casclib-src\listfile\listfile.txt` holds 1,291,033 names and no FileDataIDs.

| Build | Unique name hashes | Matched by local listfile |
|---|---|---|
| WoD 6.2.3 | 521,822 | 521,810 (99.998%) |
| Era 1.15.9 | 159,749 | 150,575 (94.3%) |
| Anniversary 2.5.6 | 190,763 | 185,798 (97.4%) |
| MoP Classic 5.5.4 | 420,398 | 400,651 (95.3%) |
| "1.60" beta | 144,999 | 111,737 (77%) |
| Retail 12.1.5 | 152,895 (only 7.9% of its FileDataIDs) | 112,642 (74%) |

* Retail and "1.60" hash names for only 8-12% of their FileDataIDs (Retail 7.9%, "1.60" beta 11.6%). Names for the
  rest exist only in the community FileDataID listfile, which is a download (licence to verify).
* For **map conversion we don't need names**: modern WDT, ADT, WMO and M2 files point at FileDataIDs, and the 1.12
  output can use paths we choose ourselves.

### DB2 FileDataIDs without a listfile

* A strings scan finds no `DBFilesClient\*.db2` paths in the modern exes. The WoD exe has 517 such paths.
* But the modern exes contain the **DB2 meta table**: a name pointer, then u32 FileDataID, field count, record size,
  hotfix field count, and so on.
* A static scan recovered 945 tables (Era), 947 (Anniversary), 952 (MoP Classic), 1,170 (wowf) and 1,137 (Retail). Each
  known FileDataID appears exactly once, for example Map 1349477, AreaTable 1353545, SpellName 1990283,
  CreatureDisplayInfo 1108759.
* In the Classic flavors, `dbfilesclient/map.db2` also resolves by name hash.
* Caveat: this is a heuristic scan of a protected executable. It worked on all 5 exes tried, but a future build could
  change the layout.

---

## 7. Table formats (MEASURED headers)

* **WoD 6.2.3.** Most tables are **WDBC**: Map (548 records), AreaTable (4,687), Spell (124,120), ItemDisplayInfo
  (73,007), FileData, LiquidType, Light, WMOAreaTable, GameObjectDisplayInfo, ChrRaces. Some are **WDB2**
  (CreatureDisplayInfo 52,657, Item, Item-sparse, SpellMisc, TextureFileData, SpellVisualKit).
  * Both formats are close to what Data Studio already reads for 1.12 and 3.3.5. There are no hotfixes (section 1).
* **All Battle.net flavors.** Almost every table is **WDC5**: 941 of 945 in Era, 934 of 947 in Anniversary, 984 of
  1,137 in Retail. The few others start with non-DB2 magics and were not looked at.
* WDC5 headers carry a schema string, for example `WOWSTATIC_1_15_9_68185` or `WOWSTATIC_12_1_5_70077`. In the samples
  we saw:
  * field storage info on every table;
  * pallet data (Map, AreaTable, ItemDisplayInfo, CreatureDisplayInfo, LiquidType ...);
  * common data (CreatureDisplayInfo, WMOAreaTable);
  * sparse/offset-map tables (Spell, ItemSparse; flag 0x1);
  * ID lists (flag 0x4);
  * multiple sections (up to 49 in Retail ItemSparse);
  * encrypted sections (section 5).
* **Machinery a WDC5 reader needs** (FORMAT KNOWLEDGE):
  * bit-packed fields;
  * common-data defaults;
  * pallet and pallet-array fields;
  * per-section string tables (offsets relative to the field position);
  * offset map plus sparse records;
  * copy table;
  * relationship (foreign-key) data;
  * ID lists;
  * skipping encrypted sections.
* **Column names and types** are not in the file. They need either hand-written layouts for the few tables we care about,
  or WoWDBDefs `.dbd` definitions (a download, licence to verify). The exe meta table also points to per-field
  size/type arrays, which could help, but this was not explored.
* **Hotfixes:** `DBCache.bin` (magic `XFTH`, v9) exists for Retail (26,785 entries, 217 tables, stale build 49570),
  Anniversary (20,925 entries, build 68101) and "1.60" beta (build 70124). Merging hotfixes is optional for map work.

---

## 8. Map data (MEASURED)

### WDT

* **WoD WDT** = `MVER MPHD MAIN`, with no MAID. Tiles are found by name, and ADT/WMO/M2 references inside are
  **strings** (MTEX/MMDX/MWMO).
* Each WoD tile has 5 files: root (`MHDR MH2O MCNK MFBO`), `_obj0` and `_obj1` (MMDX/MWMO/MDDF/MODF), `_tex0` and
  `_tex1` (MAMP MTEX MCNK, plus MTXP on Draenor). There is no `_lod.adt`.
* Each WoD map also has `.wdl`, `_occ.wdt`, `_lgt.wdt` and `.tex` locally.
* **Modern WDT** = `MVER MPHD MAIN MAID`; some, such as Retail Kalimdor and Anniversary Azeroth, also have `MAI2`, which
  we did not decode. MAID gives 8 FileDataIDs per tile: root, obj0, obj1, tex0, lod, map texture, map normal texture,
  minimap.
  * In the classic flavors lod and map textures are 0 (not used).
  * Retail and MoP Classic fill all 8.
* Modern ADTs reference things by FileDataID:
  * `_tex0` has `MDID`/`MHID` (diffuse and height texture FileDataIDs) instead of MTEX;
  * `_obj0` MDDF/MODF entries carry the FileDataID flag, with no MMDX/MWMO at all;
  * `_obj1` holds the LOD placement chunks (MLDD MLDX MLMD MLMX ...);
  * root ADT MVER is 18.

### Tile files: all local

| Map (build) | Tiles | root / obj0 / obj1 / tex0 / minimap local |
|---|---|---|
| Azeroth, Kalimdor, Expansion01, Northrend, Draenor, PVPZone01 (WoD, sampled 26-35 tiles each) | 839 / 1,011 / 826 / 1,131 / 1,221 / 35 | 100% |
| Azeroth 687, Kalimdor 979 (Era) | all tiles | 100% |
| Azeroth 687, Kalimdor 979, Expansion01 800 (Anniversary) | all tiles | 100% |
| Azeroth 1,176, Kalimdor 1,002, Expansion01 826, Northrend 1,131 (Retail; plus lod, map textures) | all tiles | 100% |

### Full dependency closure: every file is local, none encrypted

| Map (build) | ADT files | Terrain textures | WMO roots / groups / textures | M2 / skins / textures | Total |
|---|---|---|---|---|---|
| Azeroth (Era 1.15.9) | 3,435 | 191 | 352 / 2,084 / 1,505 | 2,481 / 2,494 / 1,858 | **14,400 local** |
| Azeroth (Retail 12.1.5) | 9,408 | 531 | 1,157 / 7,139 / 8,179 | 9,904 / 18,496 / 9,480 | **64,294 local** |
| Pandaria (MoP Classic 5.5.4) | 5,472 | 190 | 344 / 1,155 / 1,251 | 3,799 / 3,799 / 2,771 | **18,781 local** |
| Draenor (WoD 6.2.3; skins: `00.skin` only; M2 textures not followed) | 6,105 | 223 | 616 / 1,226 / 1,644 | 4,383 / 4,383 / n/a | **18,580 local** |

### Asset versions seen

| Asset | What we saw |
|---|---|
| M2 | WoD = `MD20` version 272. Era = `MD21` chunked, version 274, chunks MD21 SFID TXID LDV1 TXAC. Retail = 272 and 274. 1.12 needs version 256 with embedded views (FORMAT KNOWLEDGE). |
| WMO | MVER 17 everywhere. Modern roots have no MOTX; they add GFID (group FileDataIDs) and MODI (doodad FileDataIDs), and MOMT texture fields are FileDataIDs. |
| Terrain BLP2 | Era: DXT1 256x256. WoD Draenor: DXT1 256-1024. Retail: DXT1 and DXT5 (alpha encoding 7), 256 to 2048 px. |
| Liquids | MH2O in most root ADTs; 1.12 uses MCLQ. Data Studio's validator already knows the MCLQ rules. |
| Vertex colours | MCCV appears in some Retail tiles. |

---

## 9. Effort and feasibility per piece

Working days for one developer, including tests and docs. These are estimates; the "measured" column says how sure we
are.

| Piece | Status | Effort | Notes |
|---|---|---|---|
| CASC read core (configs, `.idx` v7, `data.###`, BLTE N/Z/F, encoding, root legacy + MFST v1/v2, Jenkins hash) | **doable, proven** (probe does it for all 11 builds) | 5-8 | Pure C#. No package needed. Test fixtures must be small synthetic stores (the real one is 215 GB). CascLib is MIT (LICENSE checked locally) but is C++; reimplementing is simpler and fits the "no code copied" rule. |
| Encryption | **partial** | 2-3 | Salsa20 (public algorithm), keys from local TactKey DB2 and DBCache.bin, zero-fill otherwise. Most Retail keys are not local. Maps are not affected. |
| Names | **doable** | 3-5 | Name hash plus optional user-supplied listfile (don't ship one until its licence is checked). DB2 FileDataIDs from the exe meta table (heuristic) or by name hash in Classic flavors. Synthetic paths for unnamed FileDataIDs. |
| DB2 reader (WDBC/WDB2 for WoD, WDC5 for the rest) | **doable** | 8-12 | Plus column layouts for the tables maps need (Map, AreaTable, LiquidType, Light*, WMOAreaTable, ZoneMusic ...). DBCache hotfix merge later. |
| Terrain + textures + liquids into 1.12 ADT/WDT/WDL | **doable, largest risk is quality** | 15-25 | Merge split ADTs, MH2O to MCLQ, alpha maps to 4-bit with at most 4 layers, high-res holes, texture FileDataIDs to names, resize/re-encode BLPs (DXT5 support in 1.12 to verify in the decompile), remap AreaIDs/liquid types to the 1.12 DBCs. The existing map validator and MPQ writer help a lot. |
| WMO modern v17 to 1.12 v17 | **doable** | 10-20 | GFID/MODI/FileDataID materials to names, new MOGP chunks and flags, shader IDs, liquids. |
| M2 272/274 to 256 | **doable but big** | 20-40 | Chunked MD21, external skins/anims/bones, per-animation track arrays to vanilla tracks, particles/ribbons, shaders. Data Studio's existing 1.12 M2 writer is a base. For doodads that also exist in 3.3.5a, the MPQ route may be cheaper. |
| Streaming of non-local files | **blocked offline** | n/a | About 1% of enUS files per build are not local. None were needed by the sampled maps. Fetching them would need the CDN. |

### Recommended order (maps first)

1. **CASC core in `WoWData.Core`, read-only, behind an "experimental" flag**, plus a `wds casc` CLI (`info`, `ls`,
   `extract` by name or FileDataID, `stats`). Use the probe's numbers as acceptance checks.
   * Start with **WoD 6.2.3** (name-based, no encryption, no hotfixes) and **Classic Era 1.15.9** (closest content to
     1.12, all names hashed, 7 encrypted files).
   * The same code then opens every other flavor.
2. **Names and IDs.** Name hash; optional listfile path in settings; exe DB2 meta scanner; synthetic names.
3. **Map browser and terrain/texture/liquid import.**
   * WoD first: its ADTs already use names and its liquids are MH2O.
   * Then the FileDataID-based flavors (Era, MoP, Retail) through the same converter, with a FileDataID-to-path step.
   * Doodads/WMOs are left out or replaced by 1.12/3.3.5 equivalents at first.
4. **DB2 reader** for the map-support tables (AreaTable, LiquidType, Light, WMOAreaTable, Map), to remap IDs into the
   1.12 DBCs.
5. **WMO downgrade**, then **M2 downgrade** (largest), each with the existing validators and in-game testing.

### Questions for us to decide before building

* Should the tool ship or download a listfile? Downloads are currently out of policy. Option: a user-supplied path only.
* Should the source of truth for "other client" maps be the read-only `G:\WoW Clients and Patches` copies only? The
  probes assumed yes.
* Is it acceptable to use key values found locally (TactKey DB2 and DBCache) to decrypt the few encrypted files? Maps
  don't need it.
* Which maps and expansions matter most? This picks whether the WoD (names, M2 272) or modern-FileDataID path comes
  first.
