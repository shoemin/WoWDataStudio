# Opening another client version the way 1.12 opens (read only): design

Status: **design, not implemented** (2026-10-27). First target: the modern Classic "1.60" beta
(`_classic_beta_`, `WowB.exe` 1.60.1.70450, CASC) from `G:\WoW Clients and Patches\Clients\BattleNet snapshot 2026-10-20`.
Later: 2.4.3 / 3.3.5a (MPQ), Classic Era 1.15.x and Retail (CASC).

What the user asked for (2026-10-25): open another version **the same way the 1.12 client opens today**: Browse
(creatures, items, game objects, spell effects, textures, world models), the 3D model viewer, the texture viewer and the
maps. Read only: no editing, no workspace, no patch or install, no server. The existing "Other versions" tab (files,
tables, compare, map conversion, update catalog) stays as it is; it is a tool *about* other versions, not a way to open one.

Labels as in the other research reports: **MEASURED** (read from the client copies for this report, numbers below),
**FORMAT KNOWLEDGE** (public format facts, not checked here), **INFERRED**. No code was built, no test or app was run
for this report; two small read-only Python probes ran at below-normal priority (Appendix A).

---

## 0. Summary

* **One session class, two ways to open it.** `AppSession` keeps `OpenAsync` (1.12, unchanged) and gains
  `OpenOtherAsync(ClientCopy)`. A small `GameProfile` (version, build, capabilities) says what the open session can do;
  for another version every editing capability is off.
* **Same index, different producer.** The Browse list, search and details panel read the SQLite index
  (`AssetIndex`, `EntrySearch`). A new `ModernIndexBuilder` writes the **same schema** from DB2 tables; the 1.12
  `IndexBuilder` is not touched.
* **Four narrow interfaces** extracted from existing classes, the 1.12 classes implementing them unchanged:
  files (`IClientFiles`, already exists), display chains (`IDisplayCatalog` from `Catalog`), model looks
  (`IModelLooks` from `ModelResolver`), maps (`IMapView` from `MapCatalog`). A model loader turns any M2 version into
  the existing typed `M2Model`, so the renderer, animation evaluation and texture code stay as they are.
* **Opened in its own read-only window** next to the 1.12 window (side by side is the main use), hosting the same
  Browse / 3D view / texture viewer / Maps views.
* **1.60 is readable enough.** MEASURED: every display/model/texture link table needed is readable; the encrypted
  sections hold only 0-5 % of their rows (unreleased content). Locked rows are counted and shown as locked, never guessed.
* **DB2 column names:** our own measured layout file for the ~30 tables the features need (shipped, verified), and
  WoWDBDefs read **at run time from a folder the user chooses** for every other table (optional). Licence check in section 4.
* **Milestones** M1 Browse + textures, M2 models, M3 external animations / skeletons / effects, M4 maps, M5 WoWDBDefs
  names, then M6 characters and M7 the MPQ versions. About 20-28 working days to "1.60 opens like 1.12" (M1-M4).

---

## 1. How 1.12 opens today

### 1.1 The flow

| Step | Where | What happens |
|---|---|---|
| Pick a folder | `WoWDataStudio\ViewModels\MainViewModel.cs:156` `OpenClientAsync` | `ClientValidator.Check` insists on 1.12.1 build 5875 (`Workspace\ClientValidator.cs:10`) |
| Open | `WoWData.Core\Session\AppSession.cs:298-328` `OpenAsync` | `new ClientData(cp.DataDir)` (MPQ archives in the 1.12 mount order), then the index, then `Catalog`, `Workspace`, `TextureService`, `ModelResolver`, `ModelPackageService` |
| Index current? | `Index\AssetIndex.cs:111` `IsCurrent(path, ClientData)` | schema version + `ClientData.Fingerprint()` |
| Build the index | `Index\IndexBuilder.cs:83` `BuildAsync(ClientData, ...)`, `:248` `BuildFiles` | 12 stages (`AppSession.StageNames`, `AppSession.cs:289`): files of the archives, CreatureDisplayInfo/CreatureModelData, GameObjectDisplayInfo, ItemDisplayInfo, ChrRaces/CharSections, Spell visuals, world objects, "Used in game" (`UsageIndex.WriteClientTables/WriteWorld(Catalog)`), server names (`ServerDb`, VMaNGOS/CMaNGOS), `WriteEntries` (`:459`) |
| Browse list | `AppSession.Entries` <- `AssetIndex.LoadEntries` (`AssetIndex.cs:137`); search `Index\EntrySearch.cs:27` | one flat `entry` table: kind, ref id, name, info, path, key (`creature:`, `cdisplay:`, `item:`, `idisplay:`, `go:`, `gameobject:`, `race:`, `spell:`, `file:`) |
| Details panel | `AssetIndex.Details(e, Catalog?)` (`AssetIndex.cs:356`; embedded textures `:574` read the model through `catalog.Client`) | source chain, files with their archive, textures, users |
| Display chains | `Index\Catalog.cs:25-287` | `Table(name)` reads `DBFilesClient\<name>.dbc` (`:44-53`); creature: CreatureDisplayInfo -> CreatureModelData.model_path + skins `<folder>\<skin>.blp` (`:116`); item: model/texture **names** found by basename (`:203`); game object: model path (`:187`) |
| 3D view | `ViewModels\ModelViewerViewModel.cs:349-357` `LoadAsset`: workspace bytes or `_session.Data!.Read(model)` -> `Rendering\ModelAsset.cs:143` `Build(path, bytes, loadTexture)` -> `M2Model.Load` (`ModelAsset.cs:146`); looks from `ModelResolver` (`M2\ModelResolver.cs:325` creature, `:373` default, `:105` `LoadBlp` with the 32 MB `ImageCache`) |
| Texture viewer | `TextureViewerViewModel` through `AppSession.Textures` (`TextureService`, edit-oriented) and `ModelResolver.LoadBlp` | BLP2 decode `Blp\BlpFile.cs:151-197` |
| Maps tab | `ViewModels\MapsViewModel.cs:23`; `AppSession.MapCatalog` (`AppSession.cs:34`) = `Maps\MapCatalog.Load(ClientData, index)` (`MapCatalog.cs:76`) | Map.dbc, WDT tiles, zones, md5translate.trs minimaps; tile -> `TerrainTile.Load(bytes)` (`MapsViewModel.cs:345`, `Maps\TerrainTile.cs:101`) -> `TerrainAsset.Build` (`MapsViewModel.cs:358`, `Rendering\TerrainAsset.cs:111`) |
| Tabs | `ViewModels\BrowserViewModel.cs:224-237` | the constructor makes every tab's view model: Maps, Data tables, Server data, the three designers, Other versions |
| Command line | `WoWData.Cli\CliContext.cs:62` `SessionAsync` | the same `AppSession.OpenAsync` per command, `--client` |

### 1.2 What is 1.12-specific

| Part | 1.12 assumption | Where |
|---|---|---|
| Archive reader | MPQ, 1.12 mount order, `.mdx`->`.m2`, names from `(listfile)` | `Mpq\ClientData.cs` |
| Folder check | build 5875 only | `ClientValidator.cs:10` |
| Tables | WDBC with the typed 1.12 layouts (`DbcTables.*`, `dbc_layouts_5875.txt`) | `Catalog`, `IndexBuilder`, `ModelResolver`, `UsageIndex`, `MapCatalog` |
| Display links | **paths and names** (model path in CreatureModelData, skin names, item model names found by basename, `Textures\BakedNpcTextures\<name>`) | `Catalog.cs` |
| Characters | ChrRaces + CharSections compositing (regions, sections) | `ModelResolver.cs:181-301` |
| M2 | MD20 v256/v257 only, views (skins) inside the file, texture names inside, tracks with ranges on one global timeline, full-float quaternions | `M2File.cs:48-52`, `M2Header.cs:38`, `M2Schema.cs:165` |
| BLP | BLP2 palette / DXT1/3/5 / raw: **not** 1.12-specific (MEASURED below: covers every 1.60 sample) | `BlpFile.cs` |
| Maps | one ADT per tile, Map.dbc, md5translate.trs minimaps, 4-bit alpha, MCLQ liquids | `MapCatalog`, `TerrainTile`, `MapFormats` |
| WMO | names in MOTX/MOGN, group files by name | `Maps\WmoGeometry.cs` |
| Names in Browse | creature / item / object names from the **server database** (else display rows named by model file) | `IndexBuilder.cs:427` `WriteServer`, `:461`/`:477` (the `creature` table switches the creature list from displays to server creatures) |
| Everything editing | workspace, changes, patch, loose install, hot reload, Noggit, server data, designers, DBC editor, model import | `Workspace`, `ChangeOperations`, `NoggitService`, `ServerData*`, `Designers` |

Already version-neutral and reusable: `IClientFiles` (`Versions\ClientFiles.cs:15`, MPQ and CASC, path or `#FDID`),
`CascStorage` (+ listfile), `Db2Table` / `Db2Catalog` (field types from the executable; partly encrypted tables read with
zero-fill, `Db2Catalog.cs:318-328`), `Db2Layouts` (`Db2Layouts.cs:41`, `:92` applies names when the layout hash matches),
`VersionTable`, `OtherClient` (`Versions\OtherClients.cs:263`), the neutral map model with `Adt160Reader` and
`IMapSource` / `Casc160MapSource` (`Maps\Conversion\MapSource.cs`), `ImageCache`, the renderer (`ModelRenderer`,
`TerrainRenderer`), `M2Anim` (track evaluation), `EntrySearch`, `AssetIndex`.

---

## 2. The version-neutral design

### 2.1 GameProfile: what is open and what it may do

```csharp
// WoWData.Core\Versions\GameProfile.cs
public sealed record GameProfile(string Id, string Label, string Version, int Build, ClientCopy? Copy, GameCaps Caps)
{
    public static GameProfile Own112(string clientDir);           // the user's game folder: every capability on
    public static GameProfile ReadOnly(ClientCopy copy);           // another version: every editing capability off
    public bool IsOwnClient => Copy == null;
}

[Flags] public enum GameCaps
{
    None = 0, Edit = 1, Install = 2, Server = 4, HotReload = 8, Designers = 16, Noggit = 32,
    UsedInGame = 64, Characters = 128, ModelExport = 256,
}
```

`AppSession.Profile` is `Own112` after `OpenAsync`. The views bind visibility and `CanExecute` to `Profile.Caps`
(menus, tabs, buttons: Import, Revert, Install, Build patch, Reload in game, Edit in Noggit, Data tables, Server data,
designers). The command line refuses a command that needs a missing capability with exit code 4 (refused) and one line:
"1.60.1.70450 is open read only: <command> changes files."

### 2.2 Interfaces (extracted, the 1.12 classes implement them unchanged)

| Interface | Extracted from | 1.12 implementation | 1.60 implementation | Used by |
|---|---|---|---|---|
| `IClientFiles` (exists) + `Exists`, `NamedFiles()` | `ClientFiles.cs:15` | `MpqClientFiles(ClientData)` | `CascClientFiles` + `GameNames` (2.6) | session reads, index builder, viewers |
| `IDisplayCatalog` | `Catalog`'s read API: `CreatureDisplay`, `GameObjectDisplay`, `ItemDisplay`, `EmbeddedTextures`, `ModelTextureSlots`, `Canonical`, `ArchiveOf` | `Catalog` | `Catalog160` (DB2 chains, section 3.2) | `AssetIndex.Details`, details panel, `ModelResolver` |
| `IModelLooks` | `ModelResolver`'s viewer API: `LoadBlp`, `CreatureDisplay`, `ItemDisplay`, `DefaultFor`, `Character`, `Options`, `AnimationName(s)`, `Animations`, `TextureCache`, `ClearTextureCache` | `ModelResolver` | `ModernModelResolver` | `ModelViewerViewModel`, pop-outs, compare |
| `IModelSource` (new) | the byte read in `ModelViewerViewModel.LoadAsset` | `M2Model.Load(ReadCurrent(path))` | `M2ModernReader.Load(path, files)` (fetches `.skin` / `.anim` / `.skel` by FileDataID) | `ModelAsset.Build(path, M2Model, loadTexture)` (new overload; the old one calls it) |
| `IMapView` | `MapCatalog`'s read API: `Maps`, `ById`, `ByDirectory`, `AreaName`, `ZoneOf`, `TileAreas`, `Zones`, `TileZones`, `MinimapTile`, `Overview`, `TileReport` + new `LoadTerrain(map, x, y)` | `MapCatalog` (`LoadTerrain` = `TerrainTile.Load(ReadCurrent(TilePath))`, today's code moved) | `MapCatalog160` over `Casc160MapSource` | `MapsViewModel` |

To keep the 1.12 path byte for byte, `AppSession` keeps its concrete properties (`Data`, `Catalog`, `Models`,
`MapCatalog`, `Workspace`, `Textures`, `ModelPackages`, `Dbc`, `ServerData`) with the same values as today, and adds
the neutral ones: `Profile`, `Files`, `Displays`, `Looks`, `ModelSource`, `Maps`. For 1.12 these are the same objects
(`Displays == Catalog`, `Looks == Models`, `Maps == MapCatalog`). For another version the concrete 1.12 properties are
null and only the neutral ones are set. Only the **viewing** code moves to the neutral properties (Browse list and
details, texture viewer, model viewer, pop-outs, Maps viewing). Editing code keeps the concrete types and is hidden by
the capabilities, so it never runs on another version. `AppSession.ReadCurrent` (`AppSession.cs:82`) becomes
"workspace, else `Data`, else `Files`" (1.12: identical result).

What stays 1.12-only: `Workspace`, `ChangeOperations`, `TextureService` (edit/import), `ModelPackageService`
(import; export may come later through `GameCaps.ModelExport`), `DbcService`, `ServerDataService`, designers,
`NoggitService`, `ServerMapsUpdater`, `HotReloadService`, `UsageIndex` / "Used in game" (needs placements and the
server; a later milestone can fill its client part from the neutral map reader).

### 2.3 Opening another version

```
AppSession.OpenOtherAsync(ClientCopy copy, OtherOpenOptions opts, IProgress<BuildProgress>, ct)
  1. OtherClient.Open(copy, listfile: opts.Listfile)              existing (OtherClients.cs:295)
  2. GameNames.Build(storage, listfile, eraHashes)                 section 2.6
  3. layouts = Db2Layouts.ForBuild(build) (+ DbdLayouts from opts.DbdFolder, section 4)
  4. Catalog160 / ModernModelResolver / MapCatalog160 over the client
  5. index: Paths.ForClient(copy.Folder).IndexFile; current when meta "fingerprint" equals
       build config key + listfile size/time + layout file version + dbd folder signature + builder version
     else ModernIndexBuilder.BuildAsync(...) writes the same SQLite schema (IndexBuilder.Schema)
  6. Entries = AssetIndex.LoadEntries(); Workspace = Textures = ModelPackages = null
```

The per-client folder (`AppPaths.ForClient`, `<home>\_classic_beta_-<hash>\`) holds only `index.sqlite` and
`cache\minimap`; no workspace or changes file is ever created for another version. `IndexBuilder.SchemaVersion` and the
1.12 index stay as they are. Extra modern facts (FileDataIDs, locked counts) go into extra tables the 1.12 builder does
not create (`fdid(path, fdid, state)`, `locked(table, clear, locked)`) and that `AssetIndex` ignores unless present. The
"Organizing game data" screen takes its stage names from the builder (today a static list, `AppSession.StageNames`).

### 2.4 Where the user opens it

* **Recommended: a read-only window per version** ("1.60.1.70450 beta - read only"), opened from the Other versions tab
  (a button "Open read only" on the selected client), from **File > Open another version (read only)...**, and from the
  welcome screen. It hosts the same `BrowserView`, model viewer, texture viewer and Maps view with its own `AppSession`
  and a `BrowserViewModel` whose tabs and editing commands follow `Profile.Caps` (the constructor,
  `BrowserViewModel.cs:224-237`, creates the Data tables / Server data / designer / Other versions view models only when
  the profile has those capabilities). The 1.12 window is untouched, and the two can sit side by side (the main reason
  to open 1.60). Window placement memory per kind already exists (`PopoutChrome`).
* Alternative: replace the main window's session (simpler, but loses the side-by-side view and makes every 1.12-only
  screen deal with a read-only session). Open question 1.

### 2.5 Command line (house rule: every GUI feature has its command)

* A global option `--version <client>` (folder, folder name, version or build; resolved like `wds versions` does with
  `ClientPick.Resolve` over the `other-clients` setting) makes `CliContext.SessionAsync` call `OpenOtherAsync` instead
  of `OpenAsync`.
* Read-only commands work with it unchanged: `find search/list/info/files/extract`, `textures info/export/export-all`,
  `models info/anims/skeleton/render` (render starts `WoWDataStudio.exe --render --version ...`), `maps list/zones/tiles/
  minimap/tile/render`.
* Commands that change anything refuse with exit code 4 (see 2.1).
* New `wds versions open <client> [--rebuild]`: builds or checks the index and prints what the GUI's organizing screen
  and status line show (counts, locked rows per table, names source). It is the command of the GUI's "Open read only".
* `FeatureRegistry` gets the new features (open, read-only window), and a `VersionAware` mark on the features whose
  command takes `--version`; `FeatureParityTests` checks both directions as today, plus that every `VersionAware`
  command runs with `--version` on the synthetic CASC client (section 5.1).

### 2.6 Names (version-neutral `GameNames`)

A CASC client names files only through what we bring. In order:

1. **The client's own root name hashes** (Classic flavors keep them: MEASURED 144,999 named FileDataIDs in the 1.60
   beta root). A candidate name is accepted when its Jenkins hash is in the root (`CascStorage.FileDataIdOf`).
2. **The Era-hash trick** (MEASURED in the map survey; FileDataIDs are shared across flavors): hash candidate names
   (the user's own 1.12 file list, `Directory` / file names in DB2 strings, `ManifestInterfaceData` path+name rows,
   MEASURED 120,858 interface files named that way) and look them up in the 1.60 root and, when present in the same
   Battle.net `Data` folder, the Era root.
3. **A listfile the user chose** (setting `other-clients.listfile`, e.g.
   `G:\WoW Clients and Patches\listfile\community-listfile.csv`, 2,275,770 lines, 154 MB). Changes needed in
   `CascStorage.LoadListfile` (`CascStorage.cs:345`): keep only FileDataIDs that are in this root (the community file
   covers every product), normalise `/` to `\`, and mark a name **verified** when the root's name hash agrees (1),
   else "listfile name".
4. Otherwise `#<FileDataID>` plus the extension guessed from the first bytes (`ClientFileKinds.GuessExtension`),
   computed only for FileDataIDs a display or model actually references (never a scan of 1.25 M files).

The index `files.path` column holds the name, or `#<FileDataID>.<ext>`. `IClientFiles.Read` already accepts both.
The details panel shows the FileDataID next to every file and where its name came from.

---

## 3. 1.60 per feature

### 3.1 What is readable (MEASURED, probe `p1_sections.py`, 1.60.1.70450)

"Locked" = records in WDC5 sections with a TACT key (the client reads them only with a key we do not have; the reader
zero-fills those BLTE chunks and skips the sections, `Db2Table.SkippedIds`). No table is fully encrypted.

| Table | Records | Readable | Locked | Role |
|---|---|---|---|---|
| CreatureDisplayInfo | 14,229 | 14,204 | 25 | creature displays |
| CreatureModelData | 1,171 | 1,171 | 0 | display -> model FileDataID |
| CreatureDisplayInfoExtra | 8,995 | 8,994 | 1 | humanoid NPCs: race, sex, baked texture |
| CreatureDisplayInfoOption | 87,928 | 87,919 | 9 | NPC customization choices |
| NPCModelItemSlotDisplayInfo | 53,126 | 53,120 | 6 | NPC equipment |
| Creature | 188 | 178 | 10 | the few creatures the client itself names |
| GameObjects | 1,526 | 1,526 | 0 | client-placed objects with names |
| GameObjectDisplayInfo | 3,234 | 3,223 | 11 | object displays |
| Item | 9,118 | 9,038 | 80 | items (class, icon FileDataID) |
| ItemSparse | 19,268 | 19,179 | 89 | item names |
| ItemSearchName | 6,608 | 6,589 | 19 | |
| ItemModifiedAppearance | 16,809 | 16,790 | 19 | item -> appearance |
| ItemAppearance | 9,059 | 9,042 | 17 | appearance -> ItemDisplayInfo, icon |
| ItemDisplayInfo | 7,071 | 7,056 | 15 | models / materials of an item |
| ItemDisplayInfoMaterialRes | 55,660 | 55,644 | 16 | body textures per component section |
| ModelFileData | 121,131 | 121,131 | 0 | ModelResourcesID -> model FileDataIDs |
| TextureFileData | 58,693 | 58,693 | 0 | MaterialResourcesID -> texture FileDataIDs |
| ComponentModelFileData | 58 | 58 | 0 | race / sex / class / side of item models |
| ComponentTextureFileData | 27 | 27 | 0 | race / sex / class of item textures |
| Spell / SpellName / SpellMisc | 14,304 / 17,984 / 32,887 | 13,816 / 17,361 / 31,919 | 488 / 623 / 968 | spells |
| SpellXSpellVisual | 21,062 | 20,392 | 670 | spell -> visual |
| SpellVisual / SpellVisualKit | 2,325 / 64 | 2,301 / 50 | 24 / 14 | |
| SpellVisualKitModelAttach | 9,014 | 8,963 | 51 | kit -> effect model |
| SpellVisualEffectName | 2,732 | 2,719 | 13 | effect model / texture FileDataIDs |
| ChrModel / ChrRaces / ChrCustomizationMaterial | 127 / 58 / 25,696 | all | 0 | characters (M6) |
| Map / AreaTable | 81 / 1,421 | 73 / 1,375 | 8 / 46 | maps (M4; already used by the converter) |
| ManifestInterfaceData | 134,284 | all | 0 | interface file names |

Also present (from the earlier DB2 survey, `diff160\results\db_tables.tsv`): SpellVisualEvent 15,724 rows (9
encrypted sections), SpellVisualMissile 733, SpellVisualKitEffect 18,336, ChrModelTextureLayer 940,
CharComponentTextureSections 796, ChrCustomizationGeoset 515, AnimationData 1,806 (no name column in 1.60), TactKey /
TactKeyLookup. The user-supplied TACT keys (`keys\`) would unlock only ItemDisplayInfoMaterialRes's tail among the
tables above (MEASURED with the earlier `p20_keys_user.py` output); this design does not use keys.

**How to show locks:** the status line of the read-only window says "1.60 beta: 14,204 of 14,229 creature displays
readable, 25 locked (encrypted, no key)"; `wds versions open` prints the same per table. A Browse entry whose model or
texture file is encrypted or not downloaded shows the file with "encrypted (key <name>)" / "not on this computer"
(key **names** only, as the CASC report does), and the 3D view says which file is missing instead of failing.

### 3.2 Browse: entries and link chains (FORMAT KNOWLEDGE for the chains; table presence MEASURED)

`Catalog160 : IDisplayCatalog` gives every display its files by FileDataID; `ModernIndexBuilder` writes the same
tables as 1.12 (`creature_display`, `gameobject_display`, `item_display`, `asset`, `model_variant`, `spell_effect`,
`spell_effect_use`, `map`, `files`, `entry`) plus the extra `fdid` / `locked` tables.

| Kind | Chain in 1.60 | Entry name | Key |
|---|---|---|---|
| Creature display | CreatureDisplayInfo.ModelID -> CreatureModelData.FileDataID (M2); TextureVariationFileDataID[4] -> skins (slots 11-13); ExtendedDisplayInfoID -> CreatureDisplayInfoExtra (race, sex, BakeMaterialResourcesID -> TextureFileData -> baked texture, slot 1) | model stem + skin names (as 1.12 without a server) | `cdisplay:<id>` |
| Named creature | Creature.Name_lang, DisplayID[4] (178 rows); optionally the client's own `Cache\WDB\enUS\creaturecache.wdb` (19.5 KB in the snapshot: a few dozen) | creature name | `creature:<id>` |
| Item | ItemSparse.Display_lang; Item -> ItemModifiedAppearance (ItemID) -> ItemAppearance.ItemDisplayInfoID, DefaultIconFileDataID | item name | `item:<id>` |
| Item display | ItemDisplayInfo.ModelResourcesID[2] -> ModelFileData (one FileDataID per race/sex/side, ComponentModelFileData says which); ModelMaterialResourcesID[2] -> TextureFileData (slot 2); ItemDisplayInfoMaterialRes (section, MaterialResourcesID) -> TextureFileData (body textures, ComponentTextureFileData race/sex) | as 1.12 | `idisplay:<id>` |
| Game object | GameObjects.Name_lang + DisplayID; GameObjectDisplayInfo.FileDataID (M2 or WMO) | object name / model stem | `go:<id>`, `gameobject:<id>` |
| Spell effect | SpellVisualEffectName.ModelFileDataID / TextureFileDataID; users: SpellXSpellVisual -> SpellVisual -> SpellVisualEvent -> SpellVisualKit -> SpellVisualKitModelAttach / SpellVisualKitEffect; missiles SpellVisualMissile; names SpellName | model stem | `spell:<id>` |
| Textures, world models | named files: `.blp`, `.m2` / root `.wmo` under `World\` | file stem | `file:<path>` |

Differences from 1.12 that the builder must respect:

* **Do not fill the `creature` table with the 178 Creature rows**: `WriteEntries` switches the whole creature list to
  server creatures when that table has rows (`IndexBuilder.cs:461`, `:477`), which would hide 14,204 displays. The
  modern builder has its own `WriteEntries160` (displays always listed; named creatures, items and objects added).
* **Do not read every model at build time.** 1.12 reads each creature model for its embedded textures during the
  build (`Catalog.cs:134`); in 1.60 that is ~14,000 CASC reads. Embedded textures (TXID) are read on demand by the
  details panel and cached.
* **Entry count.** 1.12 has tens of thousands of entries; the 1.60 root has 1.25 M FileDataIDs and the community
  listfile names ~870 k `.blp` and ~135 k `.m2` across all products. `AssetIndex.LoadEntries` keeps every entry in
  memory. The modern builder lists textures and models only when named, and M1 measures memory with a hard limit
  (fallback: textures only under the folders displays use, the rest searchable by `find files`).
* Characters (`race:` entries) are left out until M6 (1.60 uses ChrModel + ChrCustomization*, not CharSections).

### 3.3 Models (M2 v272 / v274)

MEASURED (`p2_chunks.py`, 60 sampled 1.60 M2s): all `MD21`, version 274 (50) and 272 (10); chunks `SFID` 60/60,
`TXID` 58/60, `TXAC` 41, `LDV1` 38, `EXP2` 17, `PGD1` 5, `AFID` 3, `EDGF` 1, `DBOC` 1; the first `SFID` file is a
`SKIN` file in 60/60; texture records never carry inline names (TXID instead); 396 of 407 sequences are inline (flag
0x20), 11 external. The earlier 200-file sample saw `.anim` files as `AFM2` (113) and `AFSB` (1).

`M2ModernReader` (new, our own code, `WoWData.Core\M2\Modern\`) reads into the existing typed `M2Model`, so
`ModelAsset`, `ModelRenderer`, `M2Anim`, geoset / skin / texture handling stay unchanged:

| Piece | Modern form (FORMAT KNOWLEDGE unless noted) | Into `M2Model` |
|---|---|---|
| Wrapper | `MD21` chunk holds an `MD20` header, offsets relative to the chunk; other chunks after it | parse the MD20 part with a v272+ header schema (`M2Schema.Header272`, new records only where layouts differ) |
| Views | external `.skin` files, FileDataIDs in `SFID` (view 0 first; then LOD views) | `Views` from `SKIN` files (submesh record 0x30, `level` = vertex start high part) |
| Textures | `TXID`: one FileDataID per texture record of type 0 | `M2TextureDef.FileName` = name or `#FDID`; `LoadBlp` reads either |
| Tracks | per-sequence arrays (`M2Array<M2Array<T>>`), no ranges; rotations as compressed 16-bit quaternions | concatenated onto one synthetic timeline: sequence *i* gets `Start/End`, each track `Ranges` -> exactly the 1.12 shape `M2Anim` evaluates |
| External animations | `AFID` (anim id, sub id -> FileDataID); `.anim` = raw (old) or chunked `AFM2` / `AFSA` / `AFSB` | M2: inline sequences only; M3: external data loaded when the sequence is chosen |
| Skeleton | `SKID` -> `.skel` (`SKB1` bones, `SKS1` sequences, `SKA1` attachments, `SKPD` parent) for character-style models (none in the doodad sample) | M3 |
| Particles, ribbons | changed records (v264+), `EXP2`, `PABC`/`PADC` | M3; until then "not shown" in the statistics |
| Ignorable for viewing | `PFID` (.phys), `BFID` (.bone), `LDV1` (LOD), `TXAC`, `PGD1`, `EDGF`, `DBOC`, `WFV*`, `NERF`, `DETL`, `RPID`, `GPID` | listed in the model info, not used |
| Blend modes, shader ids | more blend modes than 1.12 (FORMAT KNOWLEDGE: up to 7+), texture-unit `shader_id` combiners | map to the nearest 1.12 blend; the statistics name what was approximated |

The same reader serves the update catalog's open need "the M2 converter (version 272 to 256)"
(`Versions\Catalog\UpdateCatalogScanner.Terrain.cs:391`) and the 3.3.5a models (v264, `.skin` by name) of M7.

`ModernModelResolver : IModelLooks`: creature looks from TextureVariationFileDataID (slots 11-13) and the baked NPC
texture (slot 1); item looks from ModelMaterialResourcesID (slot 2) and the race/sex model choice from
ComponentModelFileData; `DefaultFor(model)` = the first display using the model; animation names: the 1.12
AnimationData names for the IDs 1.12 has (stable IDs; read from the user's own 1.12 client at run time), `Anim<id>`
otherwise (1.60's AnimationData has no name column).

### 3.4 Textures

MEASURED (200-file sample): BLP2 `dxt1` 2,187, `dxt5` 1,032, `dxt1a` 764, `pal` 603, `dxt3` 156, `argb` 20; all are
encodings `BlpFile.Decode` handles (`BlpFile.cs:151-197`), and the map converter already decodes 1.60 ground and
minimap textures. Sizes go up to 4096 x 4096 in modern clients (FORMAT KNOWLEDGE): the texture viewer decodes the
largest mip only when shown. The 32 MB `ImageCache` needs one change: today an image larger than the limit evicts every
other entry and stays as the only one (`ImageCache.cs:47`, `_lru.Count > 1`), so a 4096 x 4096 RGBA (64 MB) would
empty the cache. M1: the model resolver loads models' textures at most 1024 x 1024 (a lower mip, as the game's texture
quality setting does), and images above the limit are returned without being cached. The texture viewer's export
(PNG + sidecar) works read-only; the import / revert / install buttons follow `Caps`.

### 3.5 Maps

Reuse the converter's reader: `MapCatalog160 : IMapView` over `Casc160MapSource` (`MapSource.cs`): maps from Map (73
readable rows), tiles from the WDT `MAID`, zones from AreaTable (1,375 readable) and the root files' MCNK area IDs
(`IMapSource.ChunkAreas`), minimaps from the MAID minimap FileDataIDs (512 x 512, MEASURED in the survey; the overview
scales them). `LoadTerrain` reads a `NeutralTile` (`Adt160Reader.ReadTile`) and adapts it to a `TerrainTile` (new
`TerrainTileFromNeutral`: heights, normals, up to 8 layers with 8-bit alpha, holes, MH2O liquids as layers, placements
by FileDataID), so `TerrainAsset` / `TerrainRenderer` draw it unchanged. Going through a 1.12 ADT in memory
(`Adt112Writer`) would also work but would show 1.12's limits (4 layers, 4-bit alpha), not 1.60.

Buildings: MEASURED 22/22 sampled 1.60 WMO roots are MVER 17 with `GFID` (group FileDataIDs) and `MODI` (doodad
FileDataIDs) and no `MOTX`; `MOMT` then holds texture FileDataIDs (FORMAT KNOWLEDGE); new chunks `MOMX`, `MGI2`,
`MAVG`, `MNLD`, `MFED`, `MDDI`. `WmoGeometry` needs a FileDataID mode (groups from `GFID`, textures from `MOMT`) for
the building preview. The Maps tab's tile report ("On this tile") uses placements only (no server, no "Used in game").

### 3.6 Not in the first round

"Used in game", Export for Blender (possible later, `GameCaps.ModelExport`), player characters with customization
(M6), sounds, the interface (Lua/XML), quests and loot (server data), and any write path.

---

## 4. DB2 column names: WoWDBDefs at run time or our own layout file

### 4.1 WoWDBDefs licence (read from `G:\WoW Clients and Patches\WoWDBDefs\WoWDBDefs-master\LICENSE.md`)

The repository has two licences: **`definitions\` (the `.dbd` files) are CC BY-SA 4.0**; `code\` and `exedumper\` are
BSD-3-Clause. MEASURED: 1,346 `.dbd` files; 1,161 of them have a layout for 1.60.1.70450 (every WDC5 table of the
build), each with a `LAYOUT` hash that equals the file header's layout hash (Map: `D43AFAC3` in both theirs and our
`db2_layouts_70450.txt`).

Reading the `.dbd` files at run time from a folder the user chose is using them on the user's machine, not sharing or
adapting them, so CC BY-SA's conditions (attribution and share-alike apply when the material or an adaptation is
shared) are not triggered by the reading itself. Conditions this design keeps (not legal advice):

* never copy `.dbd` files, or anything converted from them, into the repository, the installer or a test fixture
  (that would be sharing an adaptation, which must then carry CC BY-SA 4.0);
* never generate or "fix" our built-in layout file from them (it stays evidence-based, as its header says: "No
  community definition file was used");
* our `.dbd` parser is our own code, written from the format (COLUMNS section: type, `<Table::Column>` foreign key,
  name, `?` unverified, `//` comment; `LAYOUT` / `BUILD` blocks; `$id$`, `$relation$`, `$noninline$`, `<size>` /
  `<usize>`, `[N]`, `_lang`), not a port of their C# or Python code;
* where names come from WoWDBDefs, the UI says so ("column names: WoWDBDefs, CC BY-SA 4.0, from <folder>"), and
  `THIRD_PARTY.md` and the manual mention the optional use without bundling.

### 4.2 Options

| | Our own `db2_layouts_<build>.txt` (embedded) | WoWDBDefs from a user folder |
|---|---|---|
| Coverage for 1.60 today | 11 tables (map conversion) | 1,161 tables |
| Work to cover the ~30 tables Browse needs | 2-3 days of evidence work (value matching with 1.12 / Era, FileDataID columns checked by the files' magic, links by ID resolution) | none |
| Works without anything from the user | yes | no: Browse would depend on an optional folder |
| Correctness | measured, `~Name` marks guesses | community-maintained, generally good, `?` marks unverified |
| New builds (1.60 patches, Era, Retail) | a new file per build (or a layout-hash match to an older one) | usually already there |
| Licence | ours | CC BY-SA, run time only (4.1) |

### 4.3 Recommendation

**Both, with clear roles.** The features (Browse chains, models, maps) bind to our own measured layout file, extended
in M1 to the ~30 tables of section 3.1 (types still from the executable, names applied only on a matching layout hash,
as today). WoWDBDefs is optional (new setting `other-clients.dbd`, a folder with `.dbd` files): it names the other
~1,130 tables in the Other versions tab and `wds versions tables/rows/compare`, and it is the fallback for a build that
has no built-in file (then the read-only window says "column names from WoWDBDefs"). Code finds a column by a logical
name with candidates (our name, then the `.dbd` name with `_lang` stripped), so both sources work. When both name the
same table, a check (test-only, with `WOWDATA_DBD_DIR` set) reports columns where they disagree for a human to look at;
it never writes names.

---

## 5. Milestones

Each milestone ships on its own (GUI + CLI + manual + screenshots + known issues + CHANGELOG, house rules), keeps the
full 1.12 test suite green, and leaves the 1.12 index byte for byte the same (test: build the 1.12 index from the test
copy before and after, compare the `entry`, `asset`, `model_variant` tables).

### 5.1 Test strategy (all milestones)

* **Synthetic fixtures, our own bytes, no client data:** promote `CascTests.MakeStore` (a synthetic CASC install) and
  `Db2Tests.BuildWdc` (synthetic WDC tables) into a shared `SyntheticClients` builder: a tiny "modern client" with a
  few WDC5 tables (one with a TACT-key section and a zero-filled `E` chunk), a cube M2 v274 (`MD21`, `SFID`, `TXID`,
  one bone, one inline and one external sequence, compressed quaternions), its `.skin` and `AFM2` `.anim`, a BLP made
  by `BlpEncoder`, one WDT + split ADT made by `Adt160Writer`, a three-line listfile and a small `.dbd` text written for
  the test. Expected values come from how the fixture was built.
* **Real copies, read only, opt-in:** a `[ClientCopyFact("BattleNet snapshot 2026-10-20\\_classic_beta_")]` attribute
  reads the root from **`WOWDATA_CLIENT_COPIES`** (e.g. `G:\WoW Clients and Patches\Clients`) and skips when it is
  unset or the folder is missing; optional `WOWDATA_LISTFILE` and `WOWDATA_DBD_DIR`. The existing hard-coded
  `CascRealStoreTests.Bnet` / `ClassicBeta` constants move to it. Assertions are measured numbers (row counts, locked
  counts, file magics, vertex counts), never names or file contents checked into the repo. These tests never use
  `TestEnv.ClientDir` (which can fall back to the user's `Client` folder); where a 1.12 reference is needed (animation
  names) they use the 1.12 copy under `WOWDATA_CLIENT_COPIES`.
* **Parity:** `FeatureParityTests` + the `VersionAware` check (2.5); `ManualTests` for `command-line.md`.
* **UI smoke:** the screenshot runner gets a `versions-open` set (read-only window: Browse, details, 3D view, texture,
  Maps) driven on the real copy when `WOWDATA_CLIENT_COPIES` is set.
* **Memory / time:** opening 1.60 and building its index measured (`--leaktest` gains a read-only-window cycle).

### 5.2 The milestones

| # | Content | GUI | CLI | Tests | Effort |
|---|---|---|---|---|---|
| **M1** Open read only, Browse, textures | `GameProfile` + caps; `OpenOtherAsync`; `GameNames` (root hashes, Era trick, filtered listfile, `#FDID`); `Catalog160`; `ModernIndexBuilder` (same schema, `WriteEntries160`, `fdid` / `locked`); layout file extended to the ~30 tables of 3.1; details panel with FileDataIDs, name source, lock state; texture viewer + PNG export | read-only window (from Other versions, File menu, welcome); status line with locked counts; editing hidden | `--version`; `versions open`; `find search/list/info/files/extract`, `textures info/export/export-all` with `--version`; write commands exit 4 | synthetic client end to end; real copy: row/locked counts of 3.1, entry counts, names verified share; 1.12 index unchanged | 6-8 d |
| **M2** Models | `M2ModernReader` (MD21, v272/274 header, `SFID` skins, `TXID`, compressed quaternions, per-sequence tracks onto one timeline, inline sequences); `IModelSource`; `ModelAsset.Build(M2Model)`; `ModernModelResolver` (creature skins, baked NPC texture, item model by race/sex, item texture); model info lists the ignored chunks | 3D view, pop-out, compare skins for 1.60 entries | `models info/anims/skeleton/render --version` | cube fixture (vertices, bounds, sequence times, quaternion decode); real copy: N sampled creature/item/object models load, view 0 triangle counts equal the `.skin` counts | 6-8 d |
| **M3** External animations, skeletons, effects | `AFID` + `.anim` (raw, `AFM2`/`AFSA`/`AFSB`) loaded per chosen sequence; `SKID` `.skel` (`SKB1`, `SKS1`, `SKA1`, `SKPD`); modern particle / ribbon records into the existing effect drawing; blend-mode mapping | every sequence plays; effects drawn or listed as approximated | `models anims --version` lists external sequences and their files | fixture `.anim`/`.skel`; real copy: every sequence of sampled models evaluates | 4-6 d |
| **M4** Maps | `IMapView` extracted (`MapCatalog` unchanged behind it, `LoadTerrain` moved); `MapCatalog160` over `Casc160MapSource`; `TerrainTileFromNeutral`; minimap overview from MAID; WMO FileDataID mode (`GFID`, `MOMT`, `MODI`) | Maps tab in the read-only window: maps, zones, minimap, tile 3D with placements | `maps list/zones/tiles/minimap/tile/render --version` | synthetic WDT/ADT via `Adt160Writer`; real copy: Azeroth tile count, a tile's chunk heights equal `Adt160Reader`'s | 4-6 d |
| **M5** WoWDBDefs names | own `.dbd` parser; setting `other-clients.dbd`; names for every table in Other versions and `versions tables/rows/compare`; fallback for builds without a built-in file; disagreement report (test-only) | Settings: "WoWDBDefs folder (optional)"; source shown per table | `settings set other-clients.dbd`; `versions tables` shows the name source | parser on test-written `.dbd` text; real (opt-in): 1,161 tables of 70450 matched by layout hash | 2-3 d (independent; can run any time) |
| **M6** Characters (optional) | ChrModel / ChrRacesXChrModel, ChrCustomization* choices, ChrModelTextureLayer + CharComponentTextureSections compositing, geosets; NPC equipment (NPCModelItemSlotDisplayInfo) | `race:` entries back; look pickers | `models render --version --race ...` | fixture compositing; real: default looks of the 1.60 races | 6-10 d |
| **M7** Next backends | 2.4.3 / 3.3.5a (MPQ): `Catalog` with the 8606 / 12340 DBC layouts (73 / 54 tables exist), M2 v260-264 (the modern reader covers v264's per-sequence tracks; v260-263 views inline), their ADTs into the neutral model; then Era 1.15.x and Retail (CASC, layouts per build or WoWDBDefs) | the same window | the same `--version` | per-version fixtures + opt-in real copies | 6-9 d, then 2-4 d per CASC flavor |

M1-M4 make 1.60 open like 1.12: about **20-28 working days**.

---

## 6. Risks and open questions

### 6.1 Risks

* **Memory with 1.25 M FileDataIDs.** The listfile (154 MB) and the entry list can cost hundreds of MB. Mitigation:
  root-filtered listfile, named files only in Browse, measured limit in M1.
* **Two sessions at once** (1.12 window + 1.60 window): CASC encoding/root tables plus two GL contexts. Measured in the
  leak test.
* **Format guesses.** The modern M2 track and particle layouts, `.skel` and WMO v17 details are FORMAT KNOWLEDGE; each
  is checked on the real copy in its milestone before the GUI uses it.
* **Locked or missing files** (1.60 beta: 3,601 enUS files encrypted, 1,254 not downloaded, MEASURED in the diff
  survey) leave gaps; every gap is shown with its reason.
* **The 1.12 path.** The interface extraction touches viewer code (`ModelViewerViewModel`, `MapsViewModel`, Browse
  details). Guarded by the unchanged-index test, the parity tests and the screenshot run.
* **Screenshots of 1.60 content** in `docs\screenshots` follow the existing practice (137-143 already show other
  versions); the no-client-data audit still runs before every commit.

### 6.2 Open questions for the user

1. **Window:** open another version in its own read-only window next to the 1.12 window (recommended), or replace the
   main window's session?
2. **Column names:** extend our own measured layout file for the ~30 tables the features need and keep WoWDBDefs
   optional (recommended), or let Browse use WoWDBDefs directly (faster to build, but 1.60 Browse then needs the
   WoWDBDefs folder set)?
3. **Characters:** are 1.60 player models with customization (M6) part of this round, or later?

---

## Appendix A. Measurements and probes

Scripts in `<a scratch folder, not in the repo>`
(Python 3.11, `-I`, below-normal priority, using the earlier `casc_probe\casc.py` / `content.py`; read-only; key
names and values not printed):

* `p1_sections.py`: per-section record counts of 33 DB2 tables (section 3.1), output `p1_out.tsv`.
* `p2_chunks.py`: chunk inventory of 60 sampled M2 files and 22 WMO roots (sections 3.3, 3.5), output `p2_out.txt`.

Also used: `scratchpad\diff160\results\db_tables.tsv` (DB2 survey), `heads_200_50.tsv` (BLP formats, M2 versions,
`.anim` magics), `inventory_report.txt` (name sources), `casc_probe\keys160_user_decryptable.txt` (FileDataIDs only);
`G:\WoW Clients and Patches\WoWDBDefs\WoWDBDefs-master\LICENSE.md` and `definitions\*.dbd` (read, not copied);
`G:\WoW Clients and Patches\listfile\community-listfile.csv` (line and extension counts only).
