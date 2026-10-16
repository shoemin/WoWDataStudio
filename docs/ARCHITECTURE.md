# Architecture

WoWDataTool has two implementations of the same formats:
* **WoW Data Studio** (`app\`, C# / .NET 8 / WPF): the desktop app (phases 3a, 3b, 3c and 4, then 0.5.1 to 0.9.0). Its core library is a port of
  the Python code, checked against it by the tests.
* **`wowdata`** (`wowdata\`, Python): the command line, the M2 / glTF / Blender / maps work of phases 1-2, and the
  **reference implementation and test oracle** of the app.

## WoW Data Studio (C#)

```
app/WoWDataStudio.sln        .NET 8 SDK pinned by app/global.json
  WoWData.Core/              class library, net8.0 (no WPF; portable)
    Mpq/StormCrypto.cs       Storm hash + table/file encryption            (crypto.py)
    Mpq/MpqArchive.cs        read-only reader: v0/v1 headers, zlib sectors, encryption, sector CRC, delete markers (mpq.py)
    Mpq/ClientData.cs        the client's mount order, delete-marker semantics, .mdx -> .m2, listfile names (vfs.py)
    Mpq/MpqWriter.cs         patch-3.MPQ writer + read-back verify            (mpqwrite.py; byte-identical output)
    Dbc/DbcFile.cs, DbcTables.cs   WDBC reader + typed 1.12 layouts (+ Spell, SpellVisual, SpellVisualKit, SpellVisualEffectName)
    Dbc/Layouts/dbc_layouts_5875.txt   0.7.0: the layouts of all 154 client tables (columns, types, links, enums / flags,
                             the server's use, evidence), an embedded text resource; DbcLayouts.cs parses it, DbcLayout.cs
    Dbc/DbcTable.cs          an editable table (raw fields, edited strings), the canonical writer, parse / format of values
    Dbc/DbcEditor.cs         undo / redo of cell edits, row adds / deletes, imports (one step each)
    Dbc/DbcValidation.cs, DbcDiff.cs, DbcExchange.cs   value checks; row-by-row diff; CSV / JSON export and import plans
    Dbc/DbcService.cs        the tables of a game folder: sources, loading (workspace first), saving, links, Used by, files
    Dbc/DbcServerCopy.cs     "Copy to server": plan, copy with backup (refused while mangosd runs), history, undo
    Blp/Dxt.cs, BlpFile.cs, BlpEncoder.cs   BLP2 decode / encode_like (block reuse) / encode_new / validate (blp.py, dxt.py)
    Imaging/Png.cs           own PNG codec (all colour types, 16-bit, Adam7) - no System.Drawing
    Imaging/ImageOps.cs      resize (area / bilinear, premultiplied), power-of-two helpers
    Imaging/ImageCompare.cs  before/after statistics (colour and alpha counted separately, colour under alpha 0 counted
                             too, 0.6.1) and the difference heat map (alpha-only changes in blue)
    M2/M2Header.cs, WmoHeader.cs   texture names of models (index, details)
    M2/M2Schema.cs           every M2 record layout (v256/v257)                          (m2/schema.py)
    M2/M2Data.cs, M2File.cs  generic blocks: Parse (everything, depth first) / Serialize (byte-identical) (m2/model.py)
    M2/M2Model.cs            typed view: vertices, views, bones + tracks, sequences, materials, lookups, attachments
    M2/M2Anim.cs             track evaluation with the client's semantics, bone matrices, texture matrices (m2/anim.py)
    M2/M2Validator.cs        the validator                                                 (m2/validate.py)
    M2/MaterialBlends.cs     0.6.1: the blend modes that draw a texture (M2 texture units of view 0, ribbons, particles,
                             WMO MOMT), the additive / multiply advice, "keep black areas black"
    M2/ModelResolver.cs      model looks: creature skins, item textures, character composites and geosets (modelctx.py)
    M2/M2Tracks.cs           0.10.0: raw tracks (the client's key search / interpolation), the append-mode encoder, key
                             reduction, bone matrix decomposition
    M2/AnimationInfo.cs      0.10.0: AnimationData (names, flags, fallbacks), the playable-animation algorithm, the
                             client's animation roles (UnitAnim_Is*), event codes
    M2/RigInfo.cs, RigEdit.cs    0.10.0: a model's skeleton / animations / attachments / events read as one rig; RigWriter
                             writes a RigPlan into a model (bones, sequences, lookups, bone remap, attachments, events)
    ModelIO/Gltf.cs          own glTF 2.0 reader (.gltf/.glb, strides, normalized ints, sparse, node transforms) and writer
    ModelIO/ModelPackage.cs  wowdata_manifest.json (the export package's manifest)
    ModelIO/ModelExporter.cs "Export for Blender": M2 + looks -> package (gltf.py port + ids, KHR_materials_variants)
    ModelIO/EditedModelReader.cs  what comes back: Blender's glTF / the package -> meshes in M2 space, images -> textures
    ModelIO/ModelImport.cs   the import analysis (mesh matching, texture fingerprints, plain-language rules) + commit
    ModelIO/M2MeshRebuilder.cs    new geometry into a model: vertices, the 4 views per bone limit, bone_lookup, bounds
    ModelIO/ModelPackageService.cs  looks / users of a model, the import context (workspace first)
    ModelIO/BlenderTools.cs  find Blender, add-on zip, headless install, "Open in Blender", headless runs (tests)
    ModelIO/RigPackage.cs    0.10.0: the manifest's rig part, the add-on's wowdata_rig.json (RigSidecarFile)
    ModelIO/RigGltfReader.cs, RigImport.cs   0.10.0: the rig from Blender (sidecar or plain glTF) -> a RigPlan with rules
    ModelIO/EffectsJson.cs   0.10.0: particles / ribbons as JSON (export, validation, apply)
    Index/Catalog.cs         display ids -> files (resolve.py)
    Index/ServerDb.cs        read-only MySQL (MySqlConnector), VMaNGOS and CMaNGOS schemas detected from the columns
    Index/IndexBuilder.cs    SQLite index (index.py + model_variant, spell effects, the flat "entry" search list)
    Index/AssetIndex.cs      queries: entries, details panel (source chain, textures, facts, users), variants
    Usage/MapScanner.cs      every map's WDT + ADT tiles: MCNK area ids per chunk, M2/WMO placements, WMO doodad sets,
                             the MTEX ground textures of each tile (0.6.1)
    Usage/UsageIndex.cs      the "Used in game" u_ tables (client part + server part) written by IndexBuilder
    Usage/UsageServerLoader.cs   spawns, loot, vendors, trainers, quests, item/creature extras (SELECT only, both schemas)
    Usage/UsageService.cs    "Used in game" reports (sections of linked lines) from indexed queries; UsageReport.cs, UsageDbc.cs
    Usage/AnimationUsage.cs  0.10.0: what the game plays each animation for (client roles, emotes, spells)
    Maps/MapFormats.cs       ADT / WDT / WDL readers located like the 1.12 loaders            (maps/formats.py)
    Maps/MapValidator.cs     the 1.12 map validator, same codes and messages + plain words    (maps/validate.py)
    Maps/MapDiff.cs          semantic chunk-level diff + counts for the plain summary         (maps/diff.py)
    Maps/PyFormat.cs         Python-style repr / %-formatting, so messages match the Python word for word
    Maps/AdtDocument.cs      an ADT split into chunks / sub-chunks, edited in place, re-serialised (offsets recomputed);
                             TerrainMath: tile sizes, placement coordinates -> world, the placement matrix
    Maps/TerrainTile.cs      a tile as terrain: heights, normals, layers + 4-bit alpha, shadows, holes, liquids, placements;
                             relief and height-difference pictures
    Maps/WmoGeometry.cs      WMO root + group files: materials, positions, normals, UVs, batches (for the preview)
    Maps/MapCatalog.cs       Map.dbc + WDT tiles, zones (AreaTable + index chunk areas), minimap (md5translate.trs),
                             the cached overview picture, the tile report ("On this tile")
    Maps/NoggitService.cs    "Edit in Noggit": client view (hard links), per-map project + bookmark, launch, sync / forget
    Maps/MapImport.cs        "Import map changes": find, validate, diff, review rows, commit (one change set)
    Maps/ServerMapsService.cs   "Update server maps": runs reextract.ps1 / swap_in.ps1, progress, report, history
    Workspace/AppPaths.cs, AppSettings.cs, ClientValidator.cs
    Workspace/Workspace.cs   edited files + changes.json, change sets, loose install/remove, patch build (deploy.py);
                             0.6.1: the installed patch's state (PatchState: ours by checksum / probably ours / foreign)
                             and UninstallPatch (puts back the patch that was there before the first install)
    Workspace/TextureService.cs   export (PNG + .blp.json sidecar) / import plan / commit (texture.py); 0.6.1:
                             PlanPartner ("edit both" for a ground texture) and several plans as one change set
  WoWDataStudio/             WPF app, net8.0-windows, MVVM (CommunityToolkit.Mvvm)
    MainWindow.xaml          welcome, "organizing game data" progress, main screen host, busy overlay, toasts
    Views/BrowserView.xaml   search, type filters, virtualized results, details panel, texture viewer, actions, "My changes", help
    Views/CompareView.xaml   side by side / slider / toggle / difference, alpha and mip options (reusable control)
    Views/CompareWindow.xaml compare window; also the import confirmation step ("Keep this change" / "Cancel")
    Views/SettingsWindow, BuildPatchWindow; Services/Dialogs (plain dialogs with labelled buttons)
    Controls/ZoomPanViewer.cs   zoom/pan image viewer, checkerboard, wipe divider; ViewState = shared zoom/pan
    Controls/ModelViewport.cs   3D viewport: OpenTK GLWpfControl + camera input, redraw on change / while animating, capture
    Rendering/ModelAsset.cs     background-built GPU data: vertex buffer, per-view index buffers and batches, file textures
    Rendering/ModelScene.cs     what a viewport shows: asset, look, view, animation clock, display options
    Rendering/ModelRenderer.cs  OpenGL 3.3 core renderer (GPU skinning, M2 material state, helpers, offscreen snapshot)
    Rendering/Shaders.cs, OrbitCamera.cs
    Views/ModelViewerView.xaml  the 3D view of the details panel (skins, animation, options, geosets, textures, statistics)
    Views/ModelCompareView.xaml two synchronized viewports (one camera), reusable; ModelCompareWindow hosts it
    Views/UsedInGameView.xaml   the "Used in game" expander of the details panel (sections built in code, links, Back)
    ViewModels/UsedInGameViewModel.cs, BrowserViewModel.UsedInGame.cs   loading the report, following links
    Services/RenderCli.cs       --render: one model offscreen to a PNG (developer tool); the workspace version unless --original
    Views/ModelExportWindow     "Export for Blender" (modes, animations, skeleton, folder, open in Blender)
    Views/ModelImportWindow     the import review (rows with include / checks / Compare); ModelImportViewModel
    ViewModels/BrowserViewModel.Models.cs   the model actions, import flow, add-on install, model compare
    ViewModels/              Main, Browser, TextureViewer, Compare (ICompareSide), Settings, ModelViewer, ModelCompare
    Services/AppSession.cs   the open game folder: archives, index, workspace
    Services/ScreenshotRunner.cs   --screenshot: drives every screen with real data, renders PNGs (also the UI smoke test)
    Services/ManualScreenshots.cs  --screenshot: the extra pictures the user manual needs (26_, 27_)
    Services/UsedInGameScreenshots.cs  --screenshot: the "Used in game" panel per kind of entry and its links (40_ to 45_)
    Services/Manual.cs       the user manual: F1 context help (inherited s:Manual.Topic), Help menu, finding the HTML
    Views/MapsView.xaml, ViewModels/MapsViewModel.cs   the Maps tab (maps, zones, minimap, tile, 3D preview, workflow)
    Controls/MinimapView.cs  the clickable minimap overview (zoom, pan, tile overlays)
    Rendering/TerrainAsset.cs, TerrainRenderer.cs   the terrain preview: CPU build (background) and OpenGL drawing;
                             ModelViewport draws a TerrainScene when its Terrain property is set
    Views/NoggitWindow, MapImportWindow, MapCompareWindow, ServerUpdateWindow   (+ ViewModels/MapWorkflowViewModels.cs,
                             ServerUpdateViewModel.cs)
    Services/MapScreenshots.cs  --screenshot: the Maps tab, Noggit, import, compare and server screens (46_ to 55_)
    Views/ModelPopoutWindow, TerrainPopoutWindow   0.5.1: the 3D view in a window of its own (ViewModels/PopoutViewModels.cs,
                             BrowserViewModel.Popouts.cs); Views/CameraBar (Orbit / Fly, Front / Side / Top, Reset, help);
                             Views/ModelVariantPicker + ModelViewerControls (the viewer's controls, docked and in the pop-out)
    Services/PopoutChrome.cs full screen (F11), key forwarding, placement memory per kind (Win32 monitors, physical pixels)
    Rendering/GlTracker.cs   counts GL contexts and objects (the leak check of the pop-out windows)
    Services/PopoutScreenshots.cs  --screenshot: the pop-out windows off screen, their checks, 20 open/close cycles (56_ to 62_)
    Views/DbcEditorView.xaml, ViewModels/DbcEditorViewModel.cs   0.7.0: the Data tables tab (list, grid built per table in
                             code: template columns with filter boxes, details, Used by); DbcDiffView/Window,
                             DbcImportWindow, DbcBulkEditWindow, DbcServerCopyWindow; Services/DbcScreenshots.cs (83 to 94)
    Views/ServerDataView.xaml, ViewModels/ServerDataViewModel.cs   0.9.0: the Server data tab (views, list, record values,
                             sections, Used by, the game's side, editing); ServerBulkEditWindow, ServerImportWindow,
                             ServerCheckWindow, ServerApplyWindow; Services/ServerDataScreenshots.cs (95 to 106)
    Views/TileBreakdownWindow, ViewModels/TileBreakdownViewModel   0.6.0: "Break down this tile" (list, highlight, on its own)
    Views/TerrainExportWindow, ViewModels/TerrainExportViewModel   0.6.0: "Open terrain in Blender" (the tile picker)
    ViewModels/MapsViewModel.Terrain.cs   breakdown windows, terrain export / Blender, "Import edited terrain"
    Services/BreakdownScreenshots.cs  --screenshot: breakdown, tile picker, scripted terrain import (63_ to 70_)
    Services/SpecularScreenshots.cs   0.6.1 --screenshot: ground textures and _s files, colour / alpha figures, the additive
                             note, the release patch's state and uninstall (fake game folder), the specular ground (75_ to 82_)
    (Core) Maps/GroundTextures.cs  0.6.1: the client's "_s" rule, Config.wtf (specular, pixelShaders), the pair of files,
                             "edit both" (MergeIntoPartner keeps the partner's alpha)
    (Core) Maps/TileBreakdown.cs   the pieces of a tile, texture weights, coverage map, height heat map
    (Core) Maps/TerrainGrid.cs     the 129 x 129 + 128 x 128 grid of a tile, vertex ids and chunk copies; TerrainNormals
    (Core) Maps/TerrainPackage.cs  terrain package (manifest, heights, textures, weights, references glTF): TerrainExporter
    (Core) Maps/TerrainImport.cs   Blender's export -> heights, seam check / sync, normals, MapImportAnalysis
    (Core) Workspace/WindowPlacement.cs   PixelRect, MonitorArea, WindowPlacementInfo.Fit, PopoutSettings
    --- 0.8.0: the operations both front ends share (moved out of the app's view models; see "Two front ends" below)
    StudioInfo.cs            the version and build of the product (from app\Directory.Build.props)
    ManualLocator.cs         where a chapter of the manual is (installed HTML, build\manual, docs\manual)
    Features/FeatureRegistry.cs   every feature: id, area, the app's label, the wds command, app-only / CLI-only reasons
    Session/AppSession.cs    the open game folder: archives, index, workspace, textures, models, maps, tables, Used in
                             game; texture advice (ground pair, additive note), entries by key, the server copy service
    Index/EntrySearch.cs     the Browse list's search: words, types, only my changes, keys (zone), sort, best matches first
    Workspace/TextureImportFlow.cs   "Import edited PNG" end to end; the questions through ITextureImportDecider
                             (the app's dialogs / the command line's options = FixedImportAnswers)
    Workspace/ChangeOperations.cs    revert (with the Noggit project), loose install, the patch plan / build / uninstall
                             question, map files of a map
    Workspace/SettingsCatalog.cs     every setting by key: get / set (checked) / unset, the maps-and-terrain checks
    Maps/MapOperations.cs    a tile's facts and report, breakdown, pending / changed tiles, Noggit project, terrain
                             export / import, compare pictures
    Maps/ServerMapsUpdater.cs        the guided server-map update: patch check, extract, install / dry run (refused while
                             mangosd runs, history), undo
    Dbc/DbcEditSession.cs    one open table: cell texts, checked edits, bulk edit, add / duplicate / delete (+ links),
                             query (search, DbcFilter, sort), changed rows, save, CSV / JSON plans
    --- 0.9.0: the server's world database (see "Server data" below)
    ServerData/Layouts/server_layouts_vmangos.txt   what the VMaNGOS tables mean (kinds, named values, per table: view,
                             versioning, reload command, per column: group, meaning, link, hint); ServerLayouts.cs parses it
    ServerData/WorldSchema.cs    the live schema (information_schema: columns, types, keys, engines, migration);
                             WorldValue: exact canonical texts, type checks, SQL literals
    ServerData/WorldDb.cs    the connection (SELECT / SHOW only for reading), RowFilter (SQL WHERE + the same test in memory)
    ServerData/ServerChanges.cs  the workspace files ServerData\<db>\<table>.json (rows before / after)
    ServerData/ServerEditor.cs   one editing session: the server's rows with your edits on top, set / add / duplicate /
                             delete / bulk, undo / redo, free IDs of the custom range, links and labels, save
    ServerData/ServerValidation.cs   value checks (SQL type, names, links) and VMaNGOS's load rules per table
    ServerData/ServerBrowser.cs  the views, lists, a record's values, related sections and Used by
    ServerData/ServerConsistency.cs  client <-> server checks (field maps per table) and "make them match"
    ServerData/ServerApply.cs    ServerSql (idempotent patch + revert), ServerApplier (plan, backup, apply, log, undo)
    ServerData/ServerExchange.cs CSV / JSON export, import plans, the row diff
    ServerData/WdbCache.cs   the game's WDB cache (clear = move to the backups)
    ServerData/ThrowawayMySql.cs a throwaway mysqld (own data folder and port) for the tests, screenshots and fixture
    ServerData/ServerDataService.cs  the server data of an open game folder (AppSession.ServerData)
  WoWData.Cli/               0.8.0: wds.exe, the command line (net8.0-windows, no WPF): CliApp (parser, help, JSON,
                             exit codes), CliContext (--home, --client, the session), Commands/*.cs (one file per group)
  tools/ManualBuilder/       build-time console tool (Markdig): docs\manual\*.md -> build\manual\*.html (not in the .sln)
  WoWData.Tests/             xUnit; oracle/make_*.py generate the Python reference results into app/.testcache/oracle/
                             TestEnv.cs: the shared index (IndexFixture.Instance, built once per run), the "Index"
                             collection (every class that uses it, builds an index or runs the app: one at a time), the
                             "Performance" collection (alone, all timing limits), [ServerFact] (skipped without the server)
                             0.9.0: ServerTestDb + [IsolatedDbFact]: a throwaway MySQL server per run (own data folder and
                             port) loaded from serverdb/world_fixture.sql, a database copy per test; never the live server
  tools/ServerFixtureBuilder/  0.9.0 developer tool: makes serverdb/world_fixture.sql from a read-only copy (not in the .sln);
                               0.13.0: --supplement makes serverdb/quest_chains_fixture.sql (SELECT only)
```

### Two front ends: the app and the command line (0.8.0)

WoW Data Studio is one product with two front ends over `WoWData.Core`: the desktop app (`WoWDataStudio`, WPF / MVVM)
and the command line `wds` (`WoWData.Cli`). Rules:

* **The logic is in Core, once.** A view model asks the user and shows the result; a command parses options and prints
  the result; both call the same Core operation. Where an operation asks questions (the texture import: which texture,
  size, transparency, black background, shine map, edit both, keep it?), Core takes an interface
  (`ITextureImportDecider`): the app answers with its dialogs and its check window, the command line with its options
  (`FixedImportAnswers`, whose defaults are the app's recommended answers, `Save = false` for `--dry-run`). Long steps
  run through a delegate (the app's busy box, the command line's progress on stderr). The view models that had logic
  (Browse: search, texture advice, import, revert, patch build / uninstall texts; Data tables: cell texts, edits, bulk
  edit, rows, filters, save, export / import; Maps: tile facts, pending tiles, Noggit, terrain, compare; the server
  update window; the build window's notes; settings checks) now delegate to Core.
* **One session.** `AppSession` (Core) opens a game folder (archives, the index, the workspace) for either front end.
  The command line opens it per command (about a second with the index built), builds the index when it is missing or
  out of date, as the app does at start.
* **One workspace, two programs.** The app and `wds` may run at the same time on the same `--home`. `Workspace` reads
  `changes.json` again (time and size) before every change and listing when another program changed it, and raises
  `Changed` so the app refreshes; the app also checks when the tab changes. Two programs changing the *same* file at the
  same moment is not guarded (the last write wins for that file; the records stay consistent).
* **No WPF in the command line.** The one thing the command line cannot do itself is 3D rendering (OpenGL through
  GLWpfControl): `wds models render` starts `WoWDataStudio.exe --render` (installed next to it) and waits.
* **Safety in the command line** mirrors the app's confirmations: what a dialog asks before changing the game folder,
  the server, or deleting edits needs `--yes` (exit code 5 and the plan without it); a dialog that offers to replace a
  foreign file needs `--force`; previews are `--dry-run`. Refusals (world server running, foreign files and patches) are
  exit code 4. Imports into the workspace need no `--yes` (they are undone with `changes revert-set`).
* **Parity is tested, not promised.** `FeatureRegistry` lists each feature with its app label and command;
  `FeatureParityTests` checks both directions (every feature has its command or a reason to be app-only; every command
  belongs to a feature; every label exists in the app's XAML / C#; no WPF in `wds`; the same version);
  `ParityTests` makes the same texture import, table edit and release patch through the app's view models and through
  `wds` and compares the workspaces and the patches byte for byte; `ManualTests` checks `command-line.md` against the
  program (every command documented, every written command valid, the coverage table equal to the registry, the exit
  codes).
* **One version.** `app\Directory.Build.props` holds the version and the build stamp (`WdsBuild`, set by
  `build_installer.ps1`; "dev" otherwise) for all projects; `StudioInfo` reads it from Core, so **Help > About** and
  `wds version` cannot differ; the installer build checks both exes and `wds version`.

### Hot reload (0.11.0)

The running game reads changed files again (research section 5, method C). Two halves:

* **The game:** `wowcode.dll` of the client project (`hooks\hotreload.c`, staged as `WoWDecomp\work\staged\HR1-hotreload`).
  Off unless the game folder has `wowhotreload.txt` with `enabled 1`; then it serves `\\.\pipe\wowcode-hotreload-<pid>`
  (text request, two JSON lines back) and does all game work on the main thread at the top of the per-frame update
  (a detour of `EvtSched_DispatchUpdateEvent`). Textures are re-uploaded into the same objects, models / buildings are
  evicted from the game's caches and their users rebuilt through the game's own paths (display change, the same-map
  restream), tables are overwritten in place when their IDs are unchanged, `patch-3.MPQ` is released and re-mounted. The
  report says per file what is live and what needs a relog or a restart.
* **The app:** `WoWData.Core\HotReload` (`HotReloadService`, `HotReloadProtocol`, `HotReloadFlag`, `WindowsGameLocator`,
  `FakeGame`). The app's **Reload in running game**, the offer after an install / removal, the state line of My changes
  (a quick probe every 4 s: process + pipe, no question to the game), the Settings switch, and `wds changes reload`,
  `--reload` and the live release-patch install (`ChangeOperations.BuildPatchLiveAsync` / `UninstallPatchLiveAsync`)
  all go through it. The tests use `FakeGame` (and `HotReloadService.UseLocator`, an `AsyncLocal`, so a test's in-process
  `wds` run sees its stand-in game); `docs\HOT_RELOAD.md` has the protocol and the decompile evidence.

### The designers (0.12.0)

`WoWData.Core\Designers\`, used by the Spell designer and Classes and talents tabs (`SpellDesignerViewModel`,
`ClassTalentViewModel`; the second shares the first's session) and by `wds spells`, `wds talents`, `wds classes`:

* `DesignSession`: the game's tables open for editing (one `DbcEditSession` per table, opened on first use; each sees
  the others' unsaved rows through `DbcEditSession.Others`), the server's `ServerEditor` or none (its `GameTables` hook
  makes its `dbc:` link checks see the same unsaved rows), free IDs (`FreeCustomId`: the custom ID range, free in the
  game's table and the server's; `NextId` for small tables), pending files (copied models and textures), and `Save`:
  first the server rows follow the client rows (`beforeServer`), the server's blocking problems refuse the save, then
  the dirty tables, the files and the server tables are written in ONE change set.
* `SpellDesigner`: new / clone (server-only values from the source's server row), `Set` through `SpellForm`, `Check`
  (`SpellRules`), `Tooltip` (`SpellText`), learn spells, trainers, skill lines, `SyncServer`
  (`ServerConsistency.SyncFromClient`: the same field map as the client / server check, so a saved design always
  checks clean).
* `SpellForm`: the form's fields (group, label, column, kind, help) and the friendly units, including finding or adding
  rows of the helper tables.
* `SpellText`: a port of the client's `SpellText_ExpandDescription` @005075f0 / `SpellText_ExpandVariable` @00507710,
  `Spell_C_GetSpellEffectPointsRange` @006e3800 and `Spell_C_GetSpellDurationMs` @006ea000 (formats from the client's
  GlobalStrings and its `%.1f` / `%d` strings).
* `SpellRules`: plain-language explanations of the 131 effects and 193 auras, and the server facts from VMaNGOS's
  `SpellEffects[]` / `AuraHandler[]` tables (empty handlers, scripted ones).
* `SpellVisuals`: describe, find, compose (copy-on-write when a visual is shared) and duplicate visuals; `Variant`
  scales and tints an M2 through `M2File` (vertices, pivots and translation keys, attachments, events, lights,
  particles and ribbons, bounds, collision; colour tracks, particle colours, renamed textures) and re-serialises it.
* `TalentDesigner`: trees and talents, rank spells, `Check` against `Blizzard_TalentUI.lua`'s constants and VMaNGOS's
  `MAX_TALENT_RANK` and trainer rules.
* `ClassDesigner`: classes, skill lines, the spell list by level (trainers through `creature_template.trainer_class`),
  `Assign`, `EditMask`, `NewClass` (experimental) and `EngineReport` (the client's addresses and interface files, the
  server's source files and lines that know classes by number).

### The quest-chain designer (0.13.0)

`WoWData.Core\Designers\Quest*.cs`, used by the Quest chains tab (`QuestDesignerViewModel`, sharing the Spell
designer's `DesignSession`, so reward spells are spells there and one save writes everything) and by `wds quests`:

* `QuestDesigner` (partial: `QuestDesigner.cs`, `QuestBuild.cs`, `QuestNpcs.cs`): quests are server rows
  (`quest_template` through the session's `ServerEditor`; refuses without the server with `NoServer`): find, the form
  (`Show` / `EditValue` / `Set` with friendly values, `Bits` / `Choices` for checkboxes and lists), givers and enders
  (`creature_*relation`, `gameobject_*relation`, items that start a quest; a creature giver gets npc_flags 0x2),
  objectives in words, rewards (`SetRewardSpell`: the spell's learn spell through `SpellDesigner.EnsureLearnSpell`,
  then `SyncServer` so the link checks see the server rows), `TestInGame`, `Save` (refused while a text would overflow
  the client's quest cache). `QuestBuild`: `New`, `AddAfter` / `AddBefore` (relinking NextQuestInChain / PrevQuestId),
  `Branch` (an exclusive group = the first new ID), `Join` (NextQuestId, with a negative group for "all of"),
  `Delete`, `DuplicateForFaction` (`QuestSwaps`: new IDs first, then the links and groups remapped between the copies),
  `Discipline` (the template). `QuestNpcs`: `Reaction` (faction_template masks, enemy / friend factions, and for
  reputation factions the races' starting reputation from Faction.dbc), `DropSources` / `AddDropSource` (a loot
  table's entry is given as the row's ID, so a new table is not checked as a missing link), `NewNpc` / `PlaceNpc`
  (`ServerEditor.Duplicate` of a creature template, a `creature` spawn; z from `TerrainTile.HeightAt` of the map's ADT;
  positions rounded to 6 significant digits, as MySQL stores a FLOAT), `AddTalkObjective` (gossip_menu / npc_text /
  broadcast_text when the NPC has none, a `gossip_menu_option` with `action_menu_id` -1 and a QUESTTAKEN condition:
  VMaNGOS's `Player::OnGossipSelect` then calls `TalkedToCreature`), `AddExplore` / `NewAreaTrigger` (AreaTrigger.dbc and
  `areatrigger_template` with one ID, `areatrigger_involvedrelation`, SpecialFlags 2).
* `QuestForm`: the fields (id, label, group, `quest_template` column, kind, help) and the friendly values (class and
  race names, money, targets `creature:` / `object:`, categories `sort:`).
* `QuestChains`: `Members` (breadth-first over every link, both ways, up to 150 quests), `Build` (edges with
  `QuestLinkKind` flags, groups, lanes by race / class text, columns by longest path over a topological order), `Cycles`,
  `Describe`, `Mermaid`. The flowchart control is `WoWDataStudio\Controls\QuestChainView` (drawn in `OnRender`).
* `QuestRules`: the checks (see the manual's table); reachability is computed in topological order so unreachable
  prerequisites propagate.
* `QuestText`: a port of the client's `ChatText_ExpandDollarTokens` @00506f70 / `ChatText_ExpandOneDollarToken`
  @005070a0, and the text limits of `QuestCache_Read` @006dbf60 (0x200 / 0x800 / 0x800 / 0x200 / 4 x 0x100, read without
  a length check) and the quest window (`QuestFrame_SetItemLists` @00500ef0: 0x40; `QuestFrame_BeginInteraction`
  @00500bd0: 0x400 after the tokens).
* `QuestExchange`: JSON / CSV of quest records (stored values, givers, enders), `Import` (new quests with their IDs,
  relations synced to the file).
* The Maps tab: `MinimapView.Pins` / `PointClicked` / `ZoomOn` and `MapsViewModel.ShowPinsAsync` / `BeginPickAsync`.
* Tests: `ServerTestDb.NewDatabase(name, questChains: true)` copies a second fixture database loaded with
  `serverdb\quest_chains_fixture.sql` (`ServerFixtureBuilder --supplement`, SELECT only: whole chains from seeds, their
  NPCs and spawns, needed items and their droppers, vendors, creating spells, scripts, area triggers, translations, and
  the Riposte Drill's outposts).

### Data flow (app)
(Phase 3c, model round trip:)
```
 export: M2 (workspace first) + looks (ModelPackageService) ─► ModelExporter ─► <Model>_export\ (.gltf/.bin, textures\*.png, manifest)
 Blender: wowdata_blender import (ids as custom properties) ─► edit ─► "Export for WoW Data Studio" ─► blender_export\*.gltf
 import: EditedModelReader ─► ModelImportAnalyzer (match parts / vertices / textures, rules) ─► ModelImportWindow (review, Compare)
         ─► M2MeshRebuilder (+ validator) and TextureService.PlanImage (BLP in the original format) ─► Workspace (one change set)
```


```
 Client\Data\*.MPQ ─(MpqArchive, read-only)─► ClientData ─► Catalog (DBC chains, M2/WMO texture names) ─┐
 mangos DB ─(ServerDb, SELECT only, optional)──────────────────────────────────────────────────────────┼─► IndexBuilder
                                                                                                        ▼
                %APPDATA%\WoWDataStudio\<client-id>\index.sqlite ─► AssetIndex ─► entries in memory ─► search / filters
 export:  ClientData.Read ─► BlpFile.Decode ─► Png + .blp.json sidecar
 import:  PNG (+ sidecar) ─► TextureService.Plan (size / alpha checks, BlpEncoder.EncodeLike against the game's original)
          ─► CompareWindow (confirmation) ─► Workspace.SaveEdit ─► workspace\<path> + changes.json (change set)
 deliver: Workspace.InstallLoose ─► Client\Data\<path>      Workspace.BuildPatch ─► build\patch-3.MPQ (─► Client\Data, backup)
 3D view: entry ─(background thread)─► ClientData.Read ─► M2Model.Load ─► ModelAsset (buffers, batches, file textures)
          AssetIndex (displays using the model, server names) + ModelResolver (skins / composite / geosets; your edits win)
          ─► ModelScene (asset + look) ─► ModelViewport: ModelRenderer on the GL context ─► WPF D3DImage
 used in game: index build ─► UsageIndex (DBCs, MapScanner over all ADT/WMO files, model texture names; server: spawns
          with their zone from the chunk area ids, loot, vendors, trainers, quests) ─► u_ tables in index.sqlite
          selection ─(background thread)─► UsageService.Report(key) ─► UsageReport ─► UsedInGameView (links ─► select entry)
```

### Decisions specific to the app

* **Same results as the Python, checked by tests.** The Python code is the oracle: `make_blp.py` / `make_mpq.py` write its
  results (decoded mips, encode_like outputs, the 79,733-name file list, the 5,708 delete markers, file hashes, the writer's
  archive, index counts) and the C# tests compare. Results: BLP decode identical on every mip of 211 files; unmodified round
  trips byte-identical; edited round trips byte-identical to Python's encode_like for all 92 DXT/keep-palette cases (151,174
  of 151,174 blocks); EncodeNew identical for every DXT format. New palettes use our own median cut (Pillow's is not
  copied); quality is equal or better. The patch writer's archive is byte-identical to Python's (.NET 8's zlib and CPython's
  zlib 1.3 produce the same level-9 stream; the test checks that first).
* **Index layout.** The Python tables plus:
  * `model_variant(model, owner_kind, owner_id, slot, role, texture)`: every texture variant of a model per owner
    (creature display: slots 11-13 skins and 1 baked NPC skin; item display: slot 2; character section: slot 1 body
    or 6 hair). `Boar.m2` has 27 creature-display variants with 9 skins. Phase 3b's skin picker and 3c's variant export
    read it (`AssetIndex.ModelVariants`).
  * `spell_effect` / `spell_effect_use`: SpellVisualEffectName models and the spells that show them. Field positions were
    located on the files (Spell 133 "Fireball": SpellVisual[2] at 115-116 = visual 67 → precast kit 30 "Fire Precast
    Hand", cast kit 38 "Fire Cast Hand", missile model 365; kit effect slots 3-12).
  * `entry`: one searchable row per creature (server entry, or display without the server), character (race x sex), item,
    game object, world object (`World\` .m2 and root .wmo files), spell effect and texture (~97,000 rows), loaded into
    memory (0.2 s) so every keystroke filters instantly; the list is virtualized.
  * The index is cached per client folder and rebuilt when the mounted archives' names, sizes or times change (or the
    schema version). The server part can be reloaded alone (`IndexBuilder.RefreshServerAsync`).
* **Server names.** `MySqlConnector` (MIT) instead of running `mysql.exe`. The schema is detected from the columns:
  VMaNGOS (`patch` column: the highest patch row per entry is used; `display_id1..4`, `displayId`, `display_id`) or
  CMaNGOS / MaNGOS (`ModelId1..4` / `modelid_1..`, `displayid`). Statements are checked to be SELECT/SHOW before running.
* **Workspace and change tracking** (`changes.json`): per file the original's archive, SHA-256, size, format, size and mip
  count, the models and displays that use it, the workspace SHA-256, the source PNG and its SHA-256, the change set (one
  import = one set; "Revert this import" reverts the set), the loose install (destination, SHA-256, backup) and the patch
  build. This is what the Phase 3c model-package import needs to apply and revert several textures (and later a mesh) as one.
* **Compare.** `CompareViewModel` takes two `ICompareSide`s and a shared `ViewState` (zoom/pan) for textures. Models
  have their counterpart since 3b: `ModelCompareViewModel` with two sides and one shared `OrbitCamera` (two synchronized
  3D views, see "The 3D viewer" below), with the same labels ("Original (from game)" / "Your edit") available to 3c.
* **Safety** (as in the Python): archives are opened read-only with shared access; nothing writes into `Client\Data`
  except the user-triggered "Test in game" (only `Data\<game path>`, refusing foreign files unless confirmed, with backup)
  and "Build release patch" (only `Data\patch-<3..9|A..Z>.MPQ`, with backup). Stock archive names are refused by the
  writer. The tests use a fake client folder and verify the real `Client\Data` is unchanged.
* **No GPL code, no System.Drawing**: the PNG codec, the BLP/DXT codecs and the MPQ code are our own (ports of our
  Python). NuGet packages are MIT/Apache-2.0 only (`THIRD_PARTY.md`).


### Maps and terrain (Phase 4)

Manual chapter `docs\manual\maps-and-terrain.md`; the command-line counterpart is `docs\MAPS_WORKFLOW.md`.

```
 Maps tab: MapCatalog (Map.dbc, WDTs, u_area_chunk, minimap) ─► MinimapView ─► tile ─► TerrainTile + TileReport
          ─(background)─► TerrainAsset (mesh, textures, alpha atlases, liquids, ModelAssets, WMO meshes) ─► TerrainRenderer
 Edit in Noggit: NoggitService ─► noggit\client\Data\*.MPQ (hard links) + projects\<map>\WDS_<map>.noggitproj (bookmark)
          ─► noggit.exe (separate GPL program) ─► saves into projects\<map>\World\Maps\<map>\
 Import: MapImporter.Analyze (base = workspace or game file; MapValidator; MapDiff) ─► MapImportWindow (+ MapCompareWindow)
          ─► Workspace.SaveEdit (map files validated again) ─► one change set ─► Test in game / patch-3.MPQ
 Server: ServerMapsService ─► reextract.ps1 (staging + changes.json) ─► [user stops mangosd] ─► swap_in.ps1 (backup) / -Restore
```

* **The C# port of the map module.** `MapFormats` / `MapValidator` / `MapDiff` follow `wowdata\maps` line by line, including
  the Python's message texts (lists, tuples and floats are printed Python-style by `PyFormat`), so `MapTests` can compare
  findings and diff lines word for word: on all 2,515 client map files (no findings on either side) and on 41 edited or
  broken files that the oracle script `make_maps.py` creates with Python (heights, placements, alpha, layers, holes,
  liquids, emitters, WotLK leftovers, truncation, WDT flags, WDL layout). The validator adds `MapFinding.Plain`, the
  finding in words for modders. `MapDiffResult.Stats` counts what changed (chunks with height / layer / alpha / hole /
  water changes, doodads and WMOs added / removed / moved) for the one-line summary.
* **Re-serialising an ADT** (`AdtDocument`): the file is split into its top-level chunks (each with whatever follows it up
  to the next chunk) and every MCNK into its header and the sub-chunks its header points at (sorted by offset; Blizzard's
  13 pad bytes after MCNR and MCLQ's data behind a zero-size header travel with their chunk). Writing lays them out in the
  same order and recomputes the MHDR offsets, the MCIN offsets and sizes, the MCNK chunk size and the MCNK header's
  sub-chunk offsets; unedited, all 2,429 client tiles come back byte for byte. Editing helpers (heights, normals,
  holes, doodad positions) work in place; a layout that cannot be split (non-sequential) is still readable and is
  written back as it is with in-place edits.
* **Coordinates, checked on the data.** MCNK `posX`/`posY` are world x / y of the chunk's corner (largest values); rows go
  towards -x, columns towards -y (`posX` of chunk 0 of `Azeroth_32_48` = 17066.67 - 48 x 533.33). MCNR holds world-space
  normals (x, y, z): a fit of all 48 axis permutations and signs against the height field picks the identity
  (`MapTests.NormalsAreWorldSpace`). Placements (MDDF / MODF) are in placement coordinates (a, height, b) with world
  x = 17066.67 - b, y = 17066.67 - a; the model is turned by Ry(ry - 90) Rz(-rx) Rx(rz) in placement axes with its own axes
  taken as (x, z, -y) (the convention of the fork's own code, read for behaviour); the resulting world box of each placed
  WMO reproduces the bounding box stored in MODF on the stock tiles (`MapTests.PlacementMatrixReproducesModfBoxes`; one cave
  stores a box that differs from its root's).
* **The terrain preview** (`TerrainAsset` built on a background thread, `TerrainRenderer` in the 3D view's viewport; the
  same `OrbitCamera`, so the controls are the model viewer's): per chunk 145 vertices (9x9 + 8x8) and 4 triangles per quad
  around the inner vertex, quads in the 16-bit hole mask left out; up to 4 ground textures tiled 8 times per chunk
  (as Noggit and the client do), blended by the 4-bit alpha maps (low nibble first) stored with the shadow map in one
  1024 x 1024 RGBA atlas per tile (64 x 64 per chunk, sampled at texel centres); MCLQ layers (river, ocean, magma, slime by
  MCNK flag bit) as translucent quads on the tiles whose low nibble is not 0xF; doodads through the model viewer's
  `ModelAsset` (view 0, first texture layer, alpha key / blend, additive and modulate batches left out, rest pose);
  WMO groups (MOVT / MONR / MOTV / MOVI / MOBA, the first texture of each material). Positions are relative to the first
  tile's corner, which keeps 32-bit floats precise. Placements on tile borders are deduplicated by unique id.
* **Minimap.** `textures\Minimap\md5translate.trs` maps "<map>\mapXX_YY.blp" to a hashed BLP; the overview is the
  map's tiles at 32 / 64 / 128 pixels per tile (by map size), cached as PNG in `<client>\cache\minimap\` with the
  archives' fingerprint.
* **Noggit integration** (`NoggitService`): our fixed fork (GPL-3) is only ever run as a separate program; the app writes
  its plain JSON project file and reads the map files it saves. *Never into the game folder*: the fork's patch exporter
  writes into `<ClientPath>\Data` and its "Start WoW" runs `<ClientPath>\WoW.exe`, so Noggit never gets the game folder:
  by default it gets the app's *client view* `<client>\noggit\client\Data\*.MPQ`, hard links to the game's archives
  (`CreateHardLink`: no copy; Noggit opens archives read-only, `MPQ_OPEN_NO_LISTFILE | STREAM_FLAG_READ_ONLY`, and its
  exporter only creates new `patch-4..9/A..Z` files, i.e. new files in the view; the view has no `WoW.exe`). The view is
  refreshed on every launch (a rebuilt `patch-3.MPQ` is a new file); where hard links fail (another volume) the archives
  are copied after asking; a client copy can be set instead; the game folder itself (or a folder inside it) is refused.
  *Start location*: the fork has no command-line option for it (its parser knows only `--disable-update` and
  `--force-changelog`), so the project gets a bookmark (`Bookmarks[]`: name, map id, camera pitch / yaw, position in
  Noggit's coordinates x = tile x * 533.33 + 266.67, y = height, z = tile y * 533.33 + 266.67) that the user double-clicks
  in Noggit's start window. The app does not touch Noggit's registry settings (recent projects): the first time the user
  opens the project with "Open an existing project" (its path is put on the clipboard).
* **Import** (`MapImporter`): the base of each file is what the app has now (an earlier import, else the game's file), so
  importing the same Noggit save twice finds no change; content-equal files (layout only) count as unchanged; files the
  validator rejects cannot be included; an ADT whose tile is not in the map's WDT gets a note. The workspace validates map
  files again on save and when building the patch (`Workspace.MapProblems`). Reverting a map file removes the identical copy
  from the Noggit project.
* **Server update** (`ServerMapsService`): PowerShell runs `reextract.ps1` (`-Client -Staging -ServerData -Steps -Maps`;
  without `-Tiles` the navmesh tiles are the touched tiles plus 8 neighbours, from the report) and `swap_in.ps1`
  (`-Staging -ServerData -BackupRoot -Kinds -Maps [-DryRun]`, `-Restore <backup>`). Progress comes from the scripts' own
  stage lines (`[maps]`, `[vmaps]`, `[mmaps]`, `[n/m] map ...`, `[report]`), the report from `staging\changes.json`. The
  extractors read archives only, so the app first checks that the installed `patch-3.MPQ` carries every map change (a
  fresh `ClientData` compared with the workspace). The world server is detected by process name (`mangosd`); the app
  never stops or starts it, and `swap_in.ps1` itself refuses while it runs. Each install is recorded in
  `server_updates.json` for "Undo server update".

### The DBC editor (0.7.0)

Manual chapter `docs\manual\dbc-editor.md`. Core in `WoWData.Core\Dbc`, UI in `DbcEditorView` / `DbcEditorViewModel`.

```
 Client\Data\*.MPQ ─(ClientData, game order)─┐                      ┌─► DbcDiff (vs the game) ─► save confirmation / Compare
 workspace\DBFilesClient\*.dbc ─────────────┴─► DbcService.LoadCurrent ─► DbcTable ─► DbcEditor (undo) ─► DbcTable.Write
                                                       │                                                     │
                                 dbc_layouts_5875.txt ─┘ DbcLayouts (+ fallback col_N)        Workspace.SaveEdit (DBC checks)
                                                                                   ─► Test in game / patch-3.MPQ / DbcServerCopy
```

* **Layouts from the client, as data.** `dbc_layouts_5875.txt` is a readable file (one section per table: category,
  plain-language description, the server's use, evidence, one line per column: name, storage, meaning, link, hint). It
  was generated from the client's own evidence and then curated by hand: the `<Table>Rec__Read` functions and `<Table>Rec`
  structs of our decompile (`WoWDecomp\work\out66`) give the field count, the record size, which fields get the string
  block added (texts; 8 + flags = a localized text), the 1- and 2-byte fields (CharBaseInfo, CharStartOutfit) and many
  names with the client code that reads them; the VMaNGOS loader's format strings (`DBCfmt.h`, read for facts only, no
  code taken) give decimal-number fields and the server's names; every column was checked on the 1.12.1 files (value
  ranges, link resolution, string offsets). The rule for *verified*: the client code reads it, the server reads it, or a
  public name is confirmed by the file; otherwise `~Name` (best guess, grey in the UI) or `col_N`. 107 tables fully named,
  1,214 of 1,484 columns. A file whose shape does not match its layout gets a fallback layout (raw `col_N` numbers).
* **The canonical string block.** Every one of the 154 client files has its string block in one exact form: offset 0
  is the empty string, then each distinct text once, in the order the records (and the fields of a record) first use it
  (checked with the final string-field sets: 154 of 154). `DbcTable` keeps every field raw (floats as bits, so NaN
  payloads and -0 survive) and only the *edited* texts as bytes (the others are read from the original block), and
  `Write` rebuilds the block in that canonical form. So an unchanged table comes back byte for byte (tested on all 154,
  also with every text rewritten), and an edited one is deduplicated and ordered the way Blizzard's are. A fallback layout
  (strings unknown) keeps the original block instead.
* **Values.** `DbcTable.Format` / `TryParse` are inverses (decimal; floats with "R", NaN as `raw:0x...`), which makes CSV /
  JSON round trips exact. Links treat 0 / -1 as "none" unless the linked table has such a row (Map 0).
* **Editing** is a list of `DbcChange` steps (cells with before / after; rows added / removed with their positions; a
  composite for imports). The save point is remembered, so undoing back to it makes the table clean.
* **Validation** (`DbcValidation`): type ranges, unique IDs, links to existing rows (errors for verified columns,
  warnings for unverified ones), yes / no 0-1, choices (warning), text length, path length (259) and slashes.
  `FileProblems` (field count / record size, duplicate IDs, offsets outside the block) runs in `Workspace.SaveEdit` and
  `BuildPatch` for every `DBFilesClient\*.dbc`.
* **Import** (`DbcExchange`): CSV (RFC 4180, UTF-8 with BOM, one column per field: a localized text is `Name_enUS` ...
  `Name_esMX` + `Name_flags`) and JSON (`{"rows": [...]}`, numbers as raw JSON numbers, parsed from their text). Rows
  are matched by ID (or position without an ID column); the plan lists changed / added / removed rows (keep or remove the
  rows the file lacks), errors (types, duplicate IDs, links; links between new rows of the same table are allowed) and
  warnings; `DbcEditor.Apply` makes it one step.
* **The server's use** of each table is in the layout file, from VMaNGOS's `LoadDBCStores` (46 tables read from
  `data\5875\dbc`) and its SQL storages (11 tables read from the database instead: Spell `spell_template`, Map
  `map_template`, AreaTable `area_template`, Faction, FactionTemplate, TaxiNodes, SkillLineAbility, SoundEntries,
  AreaTrigger, MailTemplate `mail_text_template`, CreatureSpellData `pet_spell_data`); everything else is client only.
  Map and AreaTable are also read by the map extractor (the note says to run "Update server maps").
* **Copy to server** (`DbcServerCopy`) is done in C# (not through `swap_in.ps1`, whose dbc kind needs explicit file
  names): only server-file tables, refused while `mangosd` runs (checked again before writing), every replaced file
  copied to `backups\server\server_dbc_<time>\` and verified (SHA-256) first, files written through a temporary name and
  verified, each copy recorded in `server_dbc_copies.json`; Undo restores the backups and deletes added files, leaving a
  file that changed since. Tests use fake server folders and a stubbed "running" probe.
* **UI.** The grid is a virtualized `DataGrid` whose template columns are made in code for each table (bound to
  `Display[i]` / `Edit[i]` indexers of the row view: the shown text with names, the raw value for editing); sorting and
  filtering are done by the view model (stable sort; the search text of all rows is built once per table version in
  the background). The ID and label columns are shown first and frozen. Previews reuse `TextureViewerViewModel`; **Show in
  3D** navigates the Browse tab to the index entry (`cdisplay:` / `idisplay:` / `godisplay:` keys, or the item / creature /
  game object whose links name the display).
* **Spell effect roles (index schema 6).** `DbcTables.SpellVisual` reads its field positions from the layout file
  (`SpellVisualRec.KitColumns`): kits 1-5 and 13 = precast, cast, impact, aura, channel, area; the missile model (7) only
  with *has missile* (6); the area model (12) only with *has area effect* (11). Fields 14 (a sound) and 15 (flags) were
  read as kits before 0.7.0.
* **Oracle.** `oracle\make_dbc.py` reads every table with `wowdata.dbc`; `DbcEditorTests` compares the headers and every
  typed value of the Python layouts (1,023,337 values).

### Server data (0.9.0)

Manual chapter `docs\manual\server-data.md`. Core in `WoWData.Core\ServerData`, UI in `ServerDataView` /
`ServerDataViewModel`, the command line in `WoWData.Cli\Commands\ServerCommands.cs` (`wds server ...`).

```
 world DB (mangos) ─(WorldDb: SELECT / SHOW)─► WorldSchema (live columns, keys) ─┐
 server_layouts_vmangos.txt ─► ServerLayouts (meaning) ─────────────────────────┼─► ServerBrowser (views, records, Used by)
 workspace\ServerData\<db>\<table>.json ─► ServerEditor (server rows + edits, undo) ┘      ServerConsistency (vs DBC)
          │ save (one change set)                         ServerSql: patch + revert ─► .sql files
          └─► Workspace (kind "server", never installed or packed) ─► ServerApplier: plan ─► backup ─► run ─► read back ─► log
                                                                                   └─► Undo last apply (backup rows)
```

* **The live schema rules; the layout file only explains.** Column names, types, NULL, defaults, lengths, character
  sets, keys and engines come from `information_schema` (`WorldSchema`), so a column added by a VMaNGOS migration is
  shown and type-checked even before the layout file knows it. The layout file (`server_layouts_vmangos.txt`, a
  readable text resource like the DBC layouts) adds kinds (`@kind item = item_template.entry name`), named values (its
  own and those of the DBC layouts), and per table the view, the versioning, the `.reload` command and per column a
  group, a meaning (id, patch, text, bool, enum, flags, money, ms, seconds, percent, coords), a link (`-> item`,
  `-> dbc:ItemDisplayInfo`, modifiers `zero`, `abs`, `neg`, `sign(kind)`) and a hint. Facts were read from the VMaNGOS
  schema and loaders (`ObjectMgr`, `SpellMgr`, `Chat.cpp`'s reload table) and checked on this machine's database
  (migration 20231231082728); no code was taken. `ServerDataTests` checks every described column against the schema.
* **Versions.** VMaNGOS keeps rows per content patch (`patch`: the highest <= `WowPatch`), per client build (`build`:
  the highest <= 5875; `skill_line_ability`: exactly 5875) or with ranges (`patch_min`..`patch_max`,
  `build_min`..`build_max`). `ServerDataService.Effective` / `RowVersion` apply the same rules (the patch from the
  setting, else `mangosd.conf`, else 10).
* **Exact values.** Every value is kept as a canonical text (`WorldValue`): whole numbers in decimal, FLOAT columns as
  the shortest text that gives the same 32-bit number ("R"), written into SQL with 9 significant digits (MySQL reads that
  back as exactly the same float), texts as they are (CHAR without trailing spaces, as MySQL returns them), NULL as null
  (`\N` in edit boxes, CSV and on the command line). That makes the workspace files, CSV / JSON and the SQL scripts
  lossless; `ServerDataTests` compares row dumps and CHECKSUM TABLE before and after.
* **Rows and keys.** A row is identified by its primary key, else its unique key (`npc_trainer`), else all its non-float
  columns (`item_display_info`). The editor keeps, per table and key, the row as it was on the server before your first
  edit and the row as you want it (null: added / deleted); a key edit is a delete plus an add. Reading merges: the
  database's rows matching a `RowFilter`, minus the edited keys, plus the edited rows that match the same filter in
  memory. Undo / redo store the changed entries per step.
* **The workspace.** One JSON file per table (`ServerData\<database>\<table>.json`, deterministic bytes) saved through
  `Workspace.SaveEdit` with kind `server`: My changes, change sets, Compare (the row diff), Revert work unchanged; the
  loose install, the release patch and the build window skip these records (`Workspace.Selected`, `GameFiles`).
  `ChangeRecord.ServerApplied` records an apply (time, target, the workspace version).
* **Checks.** `ServerValidation.CheckValue`: the SQL type (range from the column type, decimals, length in characters or
  bytes, NULL, utf8 without 4-byte characters, latin1), names of choices and flags, money, links (an error for the edited
  column; the server's own dangling links elsewhere are warnings). `CheckRow`: VMaNGOS's load rules per table (levels,
  displays missing in the game's CreatureDisplayInfo or without `creature_display_info_addon`, display probabilities,
  loot chances / counts / references / groups, quest objectives and rewards, patch and build ranges, the vendor,
  trainer and quest-giver flags). Saving refuses rows with *new* errors (errors the server's row already had do not
  block an unrelated edit).
* **SQL and apply.** The patch deletes every touched key and inserts the wanted row: idempotent; the revert inserts the
  rows as they were. Apply: refuse a database that is not the world database (no `quest_template` / `item_template`,
  a characters or realm name or tables); compare every key with the before / after rows (conflicts refused without
  overwrite); write `backup_rows.sql` (the current rows of every key, as statements) and `backup_tables.sql` (every
  affected table), read them back; run the statements in one transaction with `SQL_MODE='STRICT_ALL_TABLES'`; on a
  failure roll back and run the backup rows (MyISAM tables cannot roll back); read every row back; log
  (`server_db_applies.json`, `applied_changes.log`). Undo checks that the rows are still as applied, backs them up again
  and runs `backup_rows.sql`. Write statements are allowed only through this path (a pattern check).
* **Client and server.** `ServerConsistency.Maps`: per server table its DBC table and the column pairs (Spell: 150 pairs,
  `spellFamilyFlags` split into the two 32-bit masks; localized texts as the English text; positions with a tolerance;
  the localized-text flags and the server-only columns not compared), plus the reference checks of item and creature
  displays. "Make the server match the game" edits the editor; "make the game match the server" edits the DBC through
  `DbcEditSession` (enUS) and saves it into the workspace.
* **Tests and their database.** `ThrowawayMySql` starts `mysqld.exe` (the local server's `Server\mysql5\bin`, only
  read, or `WDS_TEST_MYSQLD`) with `--no-defaults`, an empty data folder in `%TEMP%`, a free port on 127.0.0.1 and
  `--skip-grant-tables`, waits until it answers, and kills it and deletes the folder at the end. `ServerTestDb` loads
  `serverdb\world_fixture.sql` once per run and gives each test its own copy of the database (`CREATE TABLE ... LIKE` +
  `INSERT ... SELECT`); writing tests assert the port and database first. The fixture is made by
  `app\tools\ServerFixtureBuilder` from a copy of the world database's files (refused while a mysqld runs) or a running
  server read-only: the schema of every table (SHOW CREATE TABLE) and a connected sample (the Valley of Trials: its
  creatures, spawns, loot, vendors, trainer templates, quests and their givers, objects, the items and spells they use;
  every row of the small world tables).

### Noggit and the installer

Decision: **Noggit is not bundled; the app finds it or is pointed at it.** Shipping `noggit.exe` would make the installer
distribute a GPL-3 binary (plus Qt 5.15 under LGPL-3 and its other libraries). That obliges us to ship the complete
corresponding source of exactly that build (the fork, its pinned dependencies, the build scripts) or a written offer valid
for three years, and the Qt LGPL notices and relinking terms, and to keep doing so for every release; the build is a
0.6 GB developer build with Qt 5.15 (end of life). Keeping it a separate download keeps the installer MIT-only and small,
and the app works fully without Noggit (it is needed only for "Edit in Noggit"). The app looks for the fork's build next
to it (`third_party\noggit-build\bin\noggit.exe`); otherwise **Settings > Maps and terrain** takes its path; the manual and
README say where the source and build notes are. The same applies to the server tools (Python scripts, GPL extractors).

### "Used in game"

The details panel's **Used in game** expander says how the selected entry is used by the game (manual chapter
`docs\manual\used-in-game.md`). It is a separate service (`WoWData.Core\Usage\`) and control (`UsedInGameView`); the
browser hook is `BrowserViewModel.UsedInGame.cs` and one expander in `BrowserView.xaml`.
* **Everything heavy is precomputed** at "Organizing game data" (index schema 5 since 0.6.1, 6 since 0.7.0; three more progress stages) into
  tables prefixed `u_`, so a report is a handful of indexed queries (1-25 ms measured; the panel loads on a background
  thread and shows the time). The client part: `u_area` (AreaTable), `u_area_chunk` (per map tile, the 256 MCNK area
  ids as a blob), `u_place` (per model, map, tile, area and parent WMO: the placement count), `u_model_texture` (type-0
  M2 textures and WMO MOTX of all 9,691 + root WMO files), `u_faction` (FactionTemplate + Faction name, reaction to
  Alliance / Horde), `u_spell` / `u_spell_fx` (Spell.dbc names and effects: 24 create item, 36 learn, aura 78 mount,
  aura 56 transform, 97 summon critter), `u_spell_icon`, `u_spell_skill` (SkillLineAbility + SkillLine), `u_item_set`,
  `u_lock` (herb / mining / lockpicking / key from Lock.dbc), `u_ground_texture` (0.6.1: every tile's MTEX names, so a
  texture is known as a ground texture, with its tiles and zones). The server part (rebuilt by "Reload names" / the panel's
  **Refresh server data**): `u_creature`, `u_spawn` (every spawn with its area), `u_gameobject`, `u_loot`, `u_vendor`,
  `u_trainer`, `u_quest`, `u_quest_link`, `u_item`, `u_item_spell`, `u_creature_spell`.
* **Map scan** (`MapScanner`, 3.2 s for all 2,429 tiles, in parallel; the client's own reading: WDT MAIN flags & 1,
  MHDR offsets relative to its data, MCIN -> MCNK header indexX/indexY/areaId @+0x34). Placements on tile borders are
  listed by both tiles with the same unique id and counted once (378,639 M2 + 3,748 WMO placements). A WMO's furniture
  counts the doodads of set 0 plus the placement's doodad set (152,844); they are located at the WMO's position.
  Positions: tile = MDDF/MODF x / 533.33, z / 533.33 (file `<map>_<x>_<y>.adt`); server coordinates map to it with
  tile x = 32 - Y / 533.33, tile y = 32 - X / 533.33 (checked: a Mottled Boar spawn at -349.3, -4184.4 is chunk (13,
  10) of Kalimdor_39_32, area 363 Valley of Trials).
* **Server rules:** VMaNGOS templates: the newest row with `patch <= 10`; spawns, loot and quest relations: rows valid
  at patch 10 (`patch_min <= 10 <= patch_max`); trainer rows valid for build 5875; spawns count every entry of
  `id`..`id5`. CMaNGOS / MaNGOS: CamelCase columns, no patch filters. Loot chances are stored as effective chances:
  group rows without a chance share what the explicit chances leave; references are resolved at query time with a
  recursive CTE that multiplies the chances (an item in a world-drop table lists every creature using the table).
* **Links** are entry keys (`creature:3098`, `item:2880`, `go:1618`, `spell:287`, `race:2:0`, `file:<path>`); the
  browser selects that entry (type filter on, searched by name or path) and keeps a Back history. The service opens
  its own read-only SQLite connection per report and closes it, so a rebuild can replace the index file.

### Model export / import through Blender (Phase 3c)

**glTF library: our own, not SharpGLTF.** SharpGLTF (MIT) was checked first (1.0.7, the current release): it has no
`KHR_materials_variants` support (its list of extensions does not contain it), so the variants would have to be written
through its extension registry anyway, and for .NET 8 it pulls System.Text.Json 10 into the app. Our needs are narrow
and fully specified: the writer is a port of `gltf.py`'s Builder (System.Text.Json nodes + one binary buffer), and the
reader handles what Blender writes (external / data-URI / GLB buffers, byteStride, normalized integer accessors, sparse
accessors, node TRS or matrices, up to two JOINTS/WEIGHTS sets). About 400 lines (`ModelIO\Gltf.cs`), written from the
Khronos specification. `KHR_materials_variants` is JSON we write and read ourselves: root `extensions.variants` and per
primitive `mappings` (material -> variant indices). Blender 4.5's glTF add-on reads it (it switches on its variants UI
by itself) and writes it back.

**The package** (`<Model>_export\`): `<Model>.gltf` + `.bin`, `textures\*.png`, `wowdata_manifest.json`. Geometry,
skeleton, animations and coordinates are those of `gltf.py` (glTF = (x, z, -y) of the M2; bone rest = pivot; IBM =
T(-pivot)). Additions for the round trip:
* ids everywhere, in glTF extras (Blender keeps node / mesh / material extras as custom properties and exports them
  back): `wow_submesh` / `wow_mesh_id` on mesh nodes and meshes, `wow_bone` on joints, `wow_material_id`,
  `wow_submesh`, `wow_texture_id` on materials; the add-on adds `wow_texture_id` on images (Blender does not export
  image extras, so the add-on's exporter writes them into the glTF and into `wowdata_blender.json`);
* one material per submesh (the material also tells which part a triangle belongs to; giving a new object one of the
  model's materials attaches it to that part);
* the manifest is also embedded in the root node's extras (`wow_manifest`), so a lone `.glb` from Blender still carries it;
* a bone listed twice in a vertex (some client vertices: 85/85/85 on bones a, a, b) is exported as one joint with the
  summed weight, because Blender keeps one weight per vertex group.
Looks: creature displays and item displays with the same textures are merged into one variant (the Mottled Boar's 27
displays are 9 variants); a character exports its current look (its body is a composite: listed, not importable).

**Reading back.** Positions are the rest pose of the skinned mesh: sum of weight x (inverse bind matrix x joint world
matrix), then the inverse of the model root's world matrix (undoes the add-on's -90 degree import rotation, its scale and
any move of the root), then glTF -> M2 axes. This makes the bone orientations Blender adds on import irrelevant. Joints
map to bones by `wow_bone` or the `bone_NNN` name; primitives to submeshes by their material's `wow_submesh`, the
node's / mesh's `wow_submesh`, or the `_sNN` name. Images map to textures by `wow_texture_id`, the add-on's list, the
material that uses them, or their name.

**What Blender changes on a round trip without edits** (measured on the boar, the palm tree, an item and the orc male):
vertices are split where a vertex's loop normals differ slightly (custom normals are stored compressed), loose vertices
are dropped, positions differ by ~1e-7, weights come back as floats, a second face over the same three corners (a back
side) comes back once, and duplicate bone entries are merged. The matcher therefore maps every vertex to an original one
(position within 0.2 mm, UV within 2e-4, normal within ~8 degrees, the same bones and weights within 2/255, also
accepting Blender's one-entry-per-bone reading), compares the triangles as sets over those originals (and requires every
original set of corners to be present), and calls a part unchanged when everything matches. Unchanged in every part and
texture = "No changes found", nothing is written. In an edited part, matched vertices keep their original 48 bytes,
split copies are merged back, and back-side faces Blender could not hold are put back from the original.

**Rebuilding a model** (`M2MeshRebuilder`), from what every client model shows (checked on all 3,231 models with views:
view 0's indices are the identity, the texture units of views 1-3 are view 0's per source submesh, `geoset_index` =
the source submesh; checked on 1,388 rebuilt models: same triangles per geoset in every view, the validator passes):
vertices = the submeshes' records in order; for each view and bone limit (256 / 75 / 53 / 21) each submesh whose bones
exceed the limit is split first-fit by triangle; palettes go to `bone_lookup` (identical palettes shared); properties =
palette indices; submesh centre = mean of its vertices, centre bone = the bone most of its vertices use (98% of client
submeshes follow both); texture units copied onto every piece. Bounds: per sequence the box of the vertices skinned at
the key times (at most every 33 ms), radius = the largest distance from the box centre (within a few percent of the
stored values; exact for static items); the model's box covers the rest pose and every sequence; sequences stored with
zero bounds (doodads) stay zero. Everything else (bones, sequences' timing, lookups, materials, colours, transparency,
texture transforms, attachments, events, lights, cameras, ribbons, particles, collision) is copied block by block by the
writer; the tests compare the checksums of those parts before and after.

**Textures** go through 3a's `TextureService` (`PlanImage`: an in-memory image): `EncodeLike` against the game's original
(only changed blocks), size checks with a resize offer, transparency added to a texture without alpha (keep / DXT3 /
DXT5), shared-texture users from the index. "Unchanged" = the pixel fingerprint of the manifest, or (0.6.1,
`ModelImportAnalyzer.CompareWithExport`) a difference only in the colour under pixels that are fully transparent in both
images (Blender rewrites it) *and* no opaque material (blend 0) of the export draws the texture: an opaque material shows
that colour, so there it is an edit. The additive / multiply note comes from the export's materials plus every other
model that draws the texture (`MaterialBlends`).

**Change set.** One import = one change set (the model and its textures), so "Revert this import" undoes all of it.
The workspace refuses an M2 the validator rejects (`Workspace.ModelProblems`), also when building the patch. The 3D
viewer, the compare view and the browser's **Modified** badges read the workspace's model first.

**Blender** is only ever a separate program (GPL): found as `blender-launcher.exe` (Microsoft Store alias) or
`Program Files\Blender Foundation\Blender*\blender.exe` (`WOWDATA_BLENDER` overrides). The app ships the add-on and
two scripts in `bin\blender_addon\`: `studio_install.py` (Blender's own `addon_install` + `addon_enable` +
`save_userpref`, headless, after the user agreed) and `studio_open.py` ("Open in Blender": the installed add-on when it
is at least as new, otherwise the app's copy for that session only; then the import). Tests run Blender headless with
`blender_addon\tests\studio_roundtrip.py` (import, optional scripted edit, export) and install into a temporary
profile (`BLENDER_USER_SCRIPTS` / `BLENDER_USER_CONFIG`).

### Skeleton and animations through Blender (0.10.0)

**The client's rules, from the decompile.** Bone, attachment and texture tracks: `M2Model_FindTrackKeys` @00713d50 picks
the keys of the playing sequence's range (inclusive; the second key may lie past the range's end; the fraction is not
clamped); interpolation 0 = step, anything else linear (hermite only in camera splines); quaternions are lerped
component-wise and not normalised. Events fire in half-open time windows (`M2Model_Update` @00719380); sequence flag 0x1
= plays once. Variations are picked by `rand()` over their frequencies (which sum to 0x7fff). The playable-animation
table is `M2Model_ResolvePlayableAnimation` @00711bf0: per AnimationData id, the model's own animation or the fallback
chain (AnimationData column 6), with flag 0x10 (play backwards) and 0x20 (hold the last frame) from column 5; this
reproduces the stored table of 9,647 of the 9,691 client models (the rest are Blizzard tool oddities, e.g. Stand for
animation 147). Checked on every model: the animation lookup is the first sequence per id up to the largest id; the
key-bone lookup the first bone per key id (-1 for none); the variation chain links sequences of one id in order; bone
flag 0x200 = animated.

**Append-mode encoding (`TrackEncoder`).** An identity plan writes the exact bytes (proven on all models). Unchanged
sequences keep their keys and ranges; an edited or new sequence gets a new place after the last key of the model
(+1000 ms, rounded to 100 ms) so no other sequence moves and the client's cached key index (a 500 ms heuristic) never
crosses into another sequence's keys; a guard key closes the array. Rangeless constant tracks stay untouched;
rangeless multi-key tracks become ranged; step tracks become linear with 1 ms steps where needed. Timestamps at or above
0x80000000 (seen in 4 client models) are ignored for the timeline's end. Tracks on a global sequence are written on
their own clock. Bone keys: L = T(p) T(t) R S T(-p), decomposed back from sampled matrices (`BoneTransform.Decompose`),
reduced with a tolerance (`KeyReducer`).

**Blender.** The export adds `wow_global_channels` on joints, `wow_duration_ms` on animations, `ATT_` / `EVT_` empties,
global-sequence actions and the manifest's `rig`. The add-on (0.5.0, `blender_addon\wowdata_blender\rig.py`) tags
actions with a fingerprint of their curves; its export writes `wowdata_rig.json`: every bone's rest matrix and parent,
for each *changed* action the bone matrices sampled at its keys (and every frame where curves are not linear, or with
constraints), unchanged actions by name only, markers with their remembered exact times, empties' matrices. Local bone
matrices are M2-space `rest @ basis @ rest^-1`. Without the sidecar, `RigGltfReader` reads the joints, animations and
empties of the glTF itself (local = inv(parentRest) inv(localRest) local(t) parentRest, which avoids inverting a
zero-scaled bone). `RigImportAnalyzer` compares the samples with the original track evaluated at the same times
(normalised quaternions, relative tolerances, whole matrices for scale-0 bones): an action within tolerance is
unchanged and keeps its bytes. The mesh rebuild uses the plan's final bone numbers.

**One model file.** Mesh, rig and effects are composed into one M2 (`ModelImportAnalyzer.Compose`: effects, then rig,
then mesh), validated once (only new validator errors block), saved as one workspace file and one change set.

**Proof.** All 9,691 client models: identity byte-identical, every sequence relocated plays the same (no new validator
errors), export with the skeleton -> import without edits unchanged. The suite repeats this on samples, plus Blender
headless round trips and a scripted Blender edit.

### The 3D viewer (Phase 3b)

**Spike: OpenTK GLWpfControl, not Silk.NET.** Both are MIT. The criteria were stability, resize/DPI, airspace with WPF
overlays, and maintenance. Findings (a test app on this machine, an RTX 5070 Ti + AMD iGPU hybrid laptop):
* **GLWpfControl 4.3.6** (OpenTK 4.9.4) renders OpenGL 3.3 core into a WPF `D3DImage` through `WGL_NV_DX_interop`, so the
  GL image is an ordinary WPF visual: WPF overlays on top of it work (no airspace problem; checked with a WPF border over
  the GL area), it clips inside a `ScrollViewer`, `RenderTargetBitmap` captures it (the app's `--screenshot` works
  unchanged), two controls with a shared context render side by side, 4x MSAA is available, resizing reallocates the
  framebuffer, `UseDeviceDpi` renders at device pixels. It worked on the hybrid-GPU machine without settings.
  Maintained by the OpenTK team (release 2026-02).
* **Silk.NET** has excellent bindings but no WPF control: the choices are an `HwndHost` child window (the classic
  airspace problem: WPF cannot draw over it, no `RenderTargetBitmap` capture) or writing our own D3D/GL interop into a
  `D3DImage`, which is exactly what GLWpfControl already is.
* Costs of the choice: the OpenTK meta-package brings unused assemblies (OpenAL, Compute bindings) and GLFW (zlib licence,
  used by GLWpfControl to create the context). The first context takes about 0.7 s, so the viewport stays alive and is
  reused across selections. If `WGL_NV_DX_interop` is ever missing (very old drivers, some remote desktops) the viewport
  shows a plain message instead of crashing.

**M2 port (`WoWData.Core\M2`).** A data-driven port of `m2/schema.py` + `m2/model.py`: each record layout is a list of
plain fields, arrays and tracks; `M2File.Parse` reads every block depth first into `M2Data` (raw bytes per element plus
the child arrays of each element), `Serialize` lays the file out exactly like Blizzard's exporter (header, then arrays
in field order, depth first, 16-byte aligned). `M2Model` decodes the typed parts the viewer uses. Checked against the
Python on the real client (`M2Tests`): all 9,691 models parse and re-serialise byte-identically (about 5 s), the
validator gives the same errors / warnings / notes (count and location) on every model, 259 models (the named test
models plus a seeded random sample) have the same counts, sequences, bones, track headers, vertices (sums and first
vertex), views, submeshes, texture units, textures, render flags, lookups, colours, transparency, attachments,
serialisation hash and issues, and 700+ evaluated animation values agree; an edited model (renamed, a vertex moved) is
written byte-identically to the Python writer's output. That writer is what 3c's model import needs.

**Rendering (`Rendering\`)**, the decompile's rules (M2Scene_SetBatchTextureStages @0070b740, SetBatchLightingAndFog
@0070baf0, SetBatchBlendState @0070c190 and the tables they read):
* One draw per texture unit of the selected view (a submesh with two units is drawn twice, as in the client). Opaque and
  alpha-key batches first, then the blended ones by priority plane. Geosets hide submeshes.
* Blend: M2 blend mode -> `g_M2BlendToGxBlendTable` {0,1,2,10,3,4,5} -> the client's GL factors (`g_GxGlBlendSrcByMode`
  / `DstByMode`): 0 opaque, 1 alpha key (discard below alpha x 224/255), 2 alpha (SRC_ALPHA, 1-SRC_ALPHA), 3 no-alpha add
  (ONE, ONE), 4 add (SRC_ALPHA, ONE), 5 mod (DST_COLOR, ZERO), 6 mod2x (DST_COLOR, SRC_COLOR); blended modes discard
  alpha below 1/255. Render flags: 0x1 unlit, 0x4 two-sided (no culling), 0x8 no depth test, 0x10 no depth write.
* Colour: diffuse = colour track x tint, alpha = colour alpha x transparency weight (fixed16). Lighting off for unlit
  batches and for mod / mod2x (`g_M2BlendFogEnableTable`), whose emissive is 1.0 / 0.5. Texture combiner per blend mode
  (`g_M2BlendTextureStageTable` {0,1,1,1,1,5,5}): MODULATE, or BLENDCURRENTALPHA for mod / mod2x. Up to two layers.
* A layer whose tex-unit lookup is >= 3 (the files store -1) gets sphere-environment coordinates from the view-space
  normal; otherwise its texture transform (UV animation) applies. Texture wrap per M2 texture flags (1 U, 2 V).
* Skinning on the GPU: bone matrices in a float texture (4 texels per bone), up to 4 weights per vertex; vertex bone
  indices are direct bone indices in 1.12 (the validator checks it on every model).
* Billboards: bones with flags & 0x78 (the switch in M2Model_UpdateBones @00714260) get their rotation replaced to face
  the camera, keeping axis lengths and pivot; 0x40 keeps the bone's Z axis (cylindrical).
* Lighting is the viewer's own: a light from the upper left of the camera plus ambient (not the game's zone lighting).
* The viewport redraws only when the scene or the camera changes, and every frame while something animates (not while
  a following pop-out window plays it, see "Pop-out 3D windows").

**Animation (`M2Anim`).** The client's M2Model_FindTrackKeys @00713d50: per sequence the key range {first, last}
(inclusive; last <= first = a constant key), global-sequence tracks on their own clock (global time modulo the global
sequence length) but still within the sequence's range, the key pair = the last key at or before the time and the next
one (which may belong to the next range, as in the client), interpolation 0 = step, anything else = linear. Quaternions
are lerped component-wise without normalisation and turned into a matrix with the standard formula, as
M2Model_EvalQuatTrack @00713ea0 and the client's matrix code do (full 16-byte float quaternions in 1.12). Hermite and
bezier (types 2/3) occur only in camera spline tracks in 1.12 files (checked on all models), which the viewer does not
play. The bone matrix is T(pivot) T(t) R S T(-pivot) times the parent's.

**The four views.** In 1.12 the views (skin profiles) are not geometric LODs: all four keep the same triangles and split
the mesh into submeshes for bone palettes of 256 / 75 / 53 / 21 bones (M2Shared_SelectViewAndBuild picks the largest the
device supports). The viewer can show each, which 3c needs to check regenerated views.

**Looks (`ModelResolver`).** A port of `modelctx.py` (the composite is byte-identical to the Python's for the tested
looks), extended with what character creation needs: the options per race and sex (CharSections, CharHairGeosets,
CharacterFacialHairStyles) and the client's geoset rule: geoset 0, the CharCreate group defaults {1, 101, 201, 301, 401,
501, 601, 702, 801, 901, 1001, ..., 1501}, the hair geoset of the style (1 when none), the facial-hair geosets (fields 6,
7, 8 + 100 / 300 / 200, CCharacterComponent_SetFacialHair @00478660). The Python `character_geosets` was corrected to
the same rule (it showed 101/201/301 for every facial hair before). Textures are read through the workspace first, so
your edits show on the model.

**Compare.** `ModelCompareViewModel` = two `ModelCompareSide`s (label + its own `ModelScene`) and ONE `OrbitCamera`
shared by both viewports; side by side or toggle; the right side follows the left's animation (same animation id and
time). It is model-agnostic (any two assets/looks), so 3c's review screen can show original vs. edited with it.

**Server password.** settings.json keeps the server password as `PasswordProtected`: DPAPI (`ProtectedData`, current
user, with an app-specific entropy), base64. A plain `Password` written by 0.3 is read once and the file is rewritten
encrypted at load; a value encrypted by another Windows user cannot be read and leaves the password empty.

### Pop-out 3D windows and navigation (0.5.1)

**Windows.** `ModelPopoutWindow` and `TerrainPopoutWindow` (`Views\`) are ordinary top-level windows (no owner: they can go
behind the main window, to another monitor, be minimized on their own; the main window closes them when it closes).
`BrowserViewModel.Popouts.cs` opens them (**Open in new window** over each docked 3D view, **Tools > Open the 3D view in a
new window**, Ctrl+Shift+O as a `KeyBinding` on the main window) and keeps the list. Any number can be open.
* **Model window**: its own `ModelViewerViewModel` (so it has every control of the docked viewer: the docked view was
  split into `ModelVariantPicker` + `ModelViewerControls`, which both hosts use) and `CopyFromAsync` takes over the docked
  viewer's model, skin / character look, sequence, speed, detail level, options, geosets and camera. The docked viewer and
  every window share one `ModelAssetCache` (a model is read and decoded once; each GL context uploads its own copy).
* **Terrain window**: its own `TerrainScene` + camera + display switches. A following window takes the Maps tab's
  `TerrainAsset` when it shows the same (map, tile, neighbours) key (`MapsViewModel.PreviewKey` / `PreviewShown`; it
  waits for the tab's build of that key instead of building twice); with other switches it builds its own.
* **Follow / pin** (`PopoutViewModelBase.FollowSelection`): following windows listen to `BrowserViewModel.ModelEntryShown`
  / `MapsViewModel.TileSelected`; a pinned one ignores them. Switching follow back on catches up with the selection.
  The title is `PopoutViewModelBase.WindowTitle` ("Mottled Boar — Boar.m2", "Eastern Kingdoms, tile 32,48 —
  Azeroth_32_48.adt", "(pinned)").
* **Docked view while a window is open (decision):** while at least one *following* model window is open, the docked
  viewport stops its continuous redraw (`ModelViewport.SuspendAnimation` bound to `ModelViewerViewModel.PausedForPopout`):
  the same animation would otherwise be drawn twice every frame. It still redraws on demand (camera, skin, options) and
  shows a note with **Bring it to front**. Pinned windows do not pause it (they show something else). The docked terrain
  preview needs no pause: it only ever redraws on demand.

**`PopoutChrome`** (`Services\`), attached to both pop-outs and the two compare windows:
* **Full screen** (F11; Esc / F11 leave): no frame, no resize border, the window set (Win32 `SetWindowPos`) to the whole
  area of its monitor, taskbar included; leaving restores frame, normal rectangle and maximized state. Not WPF's
  `WindowState.Maximized` + `WindowStyle.None`, so the same code path works for the screenshot run's off-screen monitor.
* **Keys**: a key nobody handled (focus on a check box or button of the window) goes to the 3D view; text boxes,
  drop-downs and sliders keep theirs. Ctrl+H toggles the controls.
* **Placement memory** (pop-outs only, per kind "model" / "terrain" in `settings.json` → `AppSettings.Popouts`): the
  normal rectangle in physical pixels (tracked on every move / resize while normal, so a maximized or full-screen window
  saves the rectangle it returns to), maximized, the monitor's device name and its work-area origin at the time, the side
  panel and the camera mode. On open (`SourceInitialized`, before the window is visible)
  `WindowPlacementInfo.Fit` (Core, unit-tested) decides: same monitor → same place (shifted with the monitor if the layout
  was rearranged); monitor gone → centred on the primary; always clamped into the work area. `SetWindowPos` is called
  twice because the first move onto a monitor with another DPI makes WPF rescale the window (PerMonitorV2).
  Monitors come from `EnumDisplayMonitors` / `GetMonitorInfo`; the screenshot run adds an invisible monitor at
  x = -30000 (`MonitorsOverride`) so it can save, restore and go full screen without showing anything on screen.

**GL contexts and resources (decision: one context per view, nothing shared).** GLWpfControl 4.3.6 gives every control
its own GLFW window + GL 3.3 context + Direct3D 9Ex device (a `SharedContext` setting exists but shared object lifetimes
across windows that open and close at random would need reference counting for little gain: a model is a few MB of GPU
memory). What changed:
* `ModelViewport` disposes itself when its window closes (it subscribes to `Window.Closed` on `Loaded`): it makes its
  context current, disposes its `ModelRenderer` and `TerrainRenderer` (every texture, buffer, vertex array and program),
  then disposes the GLWpfControl, which releases the interop framebuffer and destroys the context and the D3D device
  (checked in GLWpfControl's IL: `GLWpfControlRenderer.Dispose` → `DxGlContext.Dispose` → `NativeWindow.Dispose` +
  device release). Before 0.5.1 nothing disposed the viewports of closed compare windows: each one leaked a context.
* `GlTracker` counts the contexts opened / closed and every GL object the renderers create or delete (all `GL.Gen*` /
  `GL.Delete*` of the renderers go through it). The screenshot run opens and closes each pop-out kind 20 times after it
  has rendered, and requires the counts to come back exactly (`model pop-out: 20 open/close cycles leave no GL context
  behind (2 = 2)`, `... no GL texture or buffer behind (588 = 588)`).

**Navigation (`ModelViewport`, `OrbitCamera`, `Views\CameraBar`).** One camera class for both modes: orbit keeps the
target and moves the eye around it; fly (`Look`, `Move`, `SetDistanceKeepEye`) keeps the target a short *focus distance*
in front of the eye (`FlyFocusDistance`, 8 % of the scene radius), re-applied whenever anything reframes the camera, so
the near plane stays close to the eye; the far plane also covers the scene radius x 5 (`SceneRadius` from `Frame`). Fly
moves while a key is held, polled per frame on `CompositionTarget.Rendering` (`FlyInput` / `FlyStep`, unit-tested), at
`FlySpeed` (default 15 % of the scene radius per second; the wheel changes it; Shift x4, Ctrl x0.25). Presets: Front =
`FrontYaw` (0 for models, which face +X; 180 for terrain, from the south), Side = +90, Top = pitch 89 (north up for
terrain), and Reset. F = `Focus` (the framed selection, current direction). H toggles an overlay drawn by the viewport
itself (a WPF border over the D3DImage), listing the controls of the current mode (and the window keys in a pop-out).
Models default to Orbit, the terrain pop-out to Fly; the docked views remember the mode in their view model, the
pop-outs in `settings.json`.

### Tile breakdown and terrain in Blender (0.6.0)

**Breakdown** (`TileBreakdown.Build`): from `TerrainTile` (the 3D view's reader) plus the file itself for MCSE (52-byte
records: SoundEntries id @4, world position @8; names from SoundEntries.dbc field 2) and the WMO roots (`WmoGeometry.ReadRoot`:
MOGI flags + MOGN names, MODS sets; no group files read). A placement is *on* the tile when its placement coordinates
fall in it (MapScanner's rule); the ADT also lists border placements of neighbours, shown as such. Texture coverage =
the game's blend weights per alpha texel (w3 = a3, w2 = a2 (1 - a3), w1 = a1 (1 - a2)(1 - a3), w0 = the rest), summed per
texture. Checked against the ADT and against `MapScanner` (per model placements on the tile, per chunk area ids) on four
tiles. The highlight is a `TerrainHighlight` on the `TerrainScene` (chunks of tile 0, doodad / WMO unique ids, or a
texture: the shader lights the ground by the weight of the chunk's layers that use it); `TerrainAsset` keeps the unique
id of every placed doodad / WMO. "On its own": M2 through `ModelScene` / `ModelRenderer` (the docked viewer's renderer and
the shared `ModelAssetCache`), WMO through `TerrainAsset.BuildBuilding`, textures and heat maps as images.

**Terrain grid.** The editor's view of a tile is one grid: 129 x 129 outer + 128 x 128 inner vertices (`TerrainGrid`;
vertex id = row * 129 + col, inner = 16641 + row * 128 + col). The file stores 145 values per chunk, so a vertex on a
chunk border has 2 copies and on a chunk corner 4 (`TerrainGrid.Copies`); on every stock tile the copies are equal
(checked on Azeroth and Kalimdor: 0 internal mismatches), so Blender gets one vertex per grid vertex (a watertight tile)
and the import writes a changed height into every copy. Between tiles, edges are *not* merged (separate objects); the
stock data has 16 tile pairs (of 3,253) whose shared edges differ, so the seam checks only report differences that the
edit made.

**Normals** (`TerrainNormals`): fitted on the stock MCNR before choosing: the normalized sum of the unit normals of the
triangles around a vertex (8 for an outer vertex, 4 for an inner; each quad = 4 triangles around its inner vertex)
matches at 0.25-0.37 degrees mean (the signed-byte rounding), central differences at 1.1 degrees. Tile-edge vertices use
the neighbour tile's heights (1.0 degrees mean with, 3.5 without). The import recomputes exactly the vertices whose
triangles touch a changed height (`TerrainNormals.Affected`), in every imported tile, and leaves every other normal byte.

**Package** (`TerrainExporter`, `wowdata_terrain.json`): per tile the heights file (float32 outer + inner, uint8 holes),
the location (Blender X east = (x - cx) * 533.33, Y north = (cy - y) * 533.33, the centre tile's north-west corner at
the origin, so floats stay precise), a colour that differs from all 8 neighbours ((x mod 3) + 3 (y mod 3)), the
SHA-256 of the exported bytes (workspace first), and its ground textures: PNGs once per package, weights 3 per RGB image
(1024 x 1024, 64 per chunk), at most 12 per tile material (16 samplers with the weight images). References: one glTF,
one mesh per unique model (M2 view 0 rest pose, WMO all groups, untextured), one node per placement (unique id once) with
the placement matrix converted to package space and glTF axes; Blender's importer makes them linked duplicates.

**Add-on** (`wowdata_blender\terrain.py`, 0.4.0): builds each tile with `from_pydata` (vertex order = grid id order,
hole quads without faces), a point attribute `wow_vid` (id + 1; Blender keeps point attributes through edits, new
vertices get 0) and `wow_orig_z` (the height at import, which tells *Sync shared edges* which side was edited: the edited
side wins, both edited: the active tile or the larger change), vertex groups EDGE_N/E/S/W, a node material (UV map
`TileUV`, mapping x128 for the textures, Separate Color on the weight images, Vector Math scale + add), a FONT label.
Export = the evaluated mesh (modifiers applied): int32 vids + float32 local coordinates + matrix_world per tile.

**Import** (`TerrainImporter.Analyze`): the base of each tile is what the app has now, and must have the package's
checksum (else "changed since this package was exported"). Each Blender vertex maps to its grid id by `wow_vid` (or, if
the attribute was lost, to the nearest grid vertex); its world position is computed from the object's matrix, sideways
offsets > 0.01 yd are snapped (counted), duplicates keep the one closest to the grid (the rest count as added), missing ids
keep their height. Heights within 0.5 mm of the exported value are unchanged, so an untouched round trip is "no change".
Seams: the 4 edges and 4 corners of every imported tile against imported tiles and existing neighbours; without sync they
are reported (the app asks), with sync the edited side is copied (passes repeat until stable; an existing neighbour joins
the import). Then heights into every copy, normals, `Serialize` (offsets unchanged: same-size edits), validator, semantic
diff, and the rows of the normal map import (`MapImportAnalysis` with its own heading, `ChangeSetPrefix` "Terrain from
Blender", `NewLabel`, `BlockReason`), so review, compare, commit, revert, Test in game, patch and server update are the
existing code.

### User manual and installer

**Manual.** `docs\manual\*.md`, one chapter per task, written for modders; `index.md` is the table of contents and decides
which chapters ship. `app\tools\ManualBuilder` (Markdig, BSD-2, build time only) renders them to static HTML pages with a
chapter list, previous / next links and light / dark styles; chapter links become `.html`, the referenced screenshots are
copied, and missing pictures, chapters or anchors fail the build. F1: WPF's `ApplicationCommands.Help` (F1) is bound once
for every `Window` (`Manual.RegisterF1`); the chapter is the inherited attached property `s:Manual.Topic` of the focused
element (each window, the Browse areas, the texture viewer, the 3D view, My changes, the help panel name theirs).
`Manual.Locate` looks upwards from the exe for `manual\<topic>.html` (installed layout), then `build\manual\` (a developer
build), then the Markdown source, and opens the file with the shell (the default browser). No in-app browser control:
WebView2 would add a runtime dependency, the old WebBrowser control is IE.

**Upkeep check.** `WoWData.Tests\ManualTests` (no game data, under a second): every `**bold**` text of a shipped chapter is
a UI label (bold is reserved for labels; menu paths split at " > ") found in the app's XAML attribute / element text or
C# string literals of `WoWDataStudio` and `WoWData.Core` (access-key underscores removed; `{...}` placeholders of
interpolated strings and StringFormat match any value, if the text has at least 4 letters of its own); every referenced
screenshot and chapter link exists; every `.md` except `MAINTAINING.md` is linked from `index.md`; every `Manual.Topic`
and Help-menu chapter in the XAML has its file; `Manual.Locate` finds the installed layout; `known-issues.md` exists, is
linked from the contents, troubleshooting and README, has the four fields per entry and no entry marked fixed, and
`CHANGELOG.md` has a section for the app's version (0.6.1). Rules for writers:
`docs\manual\MAINTAINING.md`, `CONTRIBUTING.md`.

**Installer: Inno Setup 6** (`installer\WoWDataStudio.iss`, built by `build_installer.ps1`). Compared on the criteria
simplicity, per-user install without admin, shortcuts, uninstaller, clean upgrades, no account, licence:
* **Inno Setup** (chosen): one short script; `PrivilegesRequired=lowest` installs per user into
  `%LOCALAPPDATA%\Programs\WoW Data Studio` with no UAC prompt; Start-menu and optional desktop shortcuts, an uninstaller
  registered under HKCU (Settings > Apps); upgrades by a fixed `AppId` in place (closing a running app; `[InstallDelete]`
  clears `bin\`, `manual\` and `blender\` first so no stale library survives); a normal wizard (licence, folder, tasks,
  launch), silent switches for tests; no account; installed per user from winget (official release, hash verified).
  Licence: permissive (any use; commercial users are asked, not required, to buy a licence; this project is
  non-commercial); the produced installer carries only Inno's own setup runtime under that licence. No app code changes.
* **Velopack** (MIT): built around its update framework: the app must call `VelopackApp.Build().Run()` first thing in a
  custom `Main` (WPF's generated one replaced), installs into `%LOCALAPPDATA%\<id>\current` without a wizard (no
  licence or task pages), and needs the `vpk` tool; its strength, delta auto-updates from a feed, is not needed for a
  local modding tool today. A good later choice if auto-update is wanted.
* **WiX v4/v5** (MS-RL; MSI): the most work: ~260 published files to harvest into components, per-user MSI quirks
  (ICE validation, HKCU key paths for every component), a separate bundle for a custom UI; v4/v5 are MS-RL, while v6 and
  later binaries come with an EULA and the "Open Source Maintenance Fee" for revenue-generating users. Its strengths (MSI
  for enterprise deployment, group policy) do not apply here.

Installed layout (mirrors `app\publish`): `bin\` (the self-contained publish, `WoWDataStudio.exe`), `manual\` (HTML +
`screenshots\`), `blender\wowdata_blender.zip` (`blender_addon\build_zip.py`), `docs\LOOSE_FILES_PATCH.md` (the app's
loose-file link finds it by walking up from `bin\`), `docs\NOGGIT.md` (Noggit is not bundled: where to get it, see
"Noggit and the installer"), `LICENSE`, `THIRD_PARTY.md`. The installer never touches a World of
Warcraft folder; uninstalling keeps `%APPDATA%\WoWDataStudio` (settings, index, edits) and the exports. The version comes
from `app\Directory.Build.props` (or `-Version`, 0.8.0) and is stamped with one build stamp into both exes by the publish,
so the app, the command line, the installer file name and "Apps & features" agree. 0.8.0: `wds.exe` is published into the
same `bin\` (both self-contained from the same runtime pack; the shared libraries are the same files) and is not an
optional component; the build fails without it or with differing versions. The Start menu gets *WoW Data Studio command
prompt* (`cmd /k bin\wds_prompt.cmd`: `bin` on that window's PATH); the optional task *Add the command line tool to
PATH* appends `{app}\bin` to the user's `HKCU\Environment\Path` in `[Code]` and removes exactly that entry on uninstall
(`ChangesEnvironment=yes` broadcasts the change). The example scripts are installed in `scripts\`. Not code-signed (no certificate): SmartScreen may warn; the manual tells users what to
do ("More info" > "Run anyway").

**Test installs (0.6.0, after a test uninstall removed a user's real shortcuts).** Inno identifies an installation by its
AppId: a silent install of the real setup into another folder still registers under the real AppId and Start-menu group
(`/GROUP` is ignored with the group page disabled), and its uninstall removes them. `build_installer.ps1 -TestBuild`
therefore compiles a separate product (`/DTestBuild=1`: AppId `{5D7C2F3E-...}`, "WoW Data Studio TEST", its own folder,
group and Apps entry, no launch at the end), and `installer\test_install.ps1` is the only way tests install it (`/DIR` to
a temporary folder, `/NOICONS`, its own uninstaller), comparing a record of the real installation before, during and
after. CONTRIBUTING.md has the rule.

## wowdata (Python)

```
wowdata/                 Python 3.10+, numpy, Pillow; no native code
  config.py              paths (defaults + wowdata.json + WOWDATA_* env)
  crypto.py              Storm hash + table/file encryption
  mpq.py                 MPQArchive: read-only reader (v0/v1 headers, zlib/bzip2 sectors, encryption, delete markers)
  mpqwrite.py            build(): new v1 patch archives; verify(): read-back + client-style hash probe
  vfs.py                 ClientData: the client's lookup order, delete-marker semantics, loose overlays, .mdx -> .m2
  dbc.py                 WDBC reader, typed 1.12 layouts, raw access for unknown tables
  blp.py                 BLP2 parse/decode, encode_like (same format, block reuse), encode_new, validate
  dxt.py                 vectorized DXT1/3/5 decoder and encoder
  m2/schema.py           every M2 record layout (v256/v257), each checked against the decompile
  m2/model.py            M2.parse (everything, raw bytes + block offsets kept) / M2.serialize (byte-identical)
  m2/validate.py         the client's bounds checks + cross-reference and sanity checks
  m2/anim.py             track evaluation and per-sequence keys with the client's semantics
  m2/header.py           fast minimal header reader (texture names) for the index
  modelctx.py            model targets -> .m2 + replaceable texture slots + visible geosets; character skin compositing
  gltf.py                M2 -> glTF 2.0 (.gltf/.bin/PNG or GLB) + the .wowm2.json sidecar
  viewer.py              self-contained HTML quick-look page (three.js from a pinned CDN)
  resolve.py             Catalog: display ids -> model / skin / item / icon / character-section files
  serverdb.py            read-only mysql.exe queries (VMaNGOS schema)
  index.py               build + query the SQLite "what uses what" index
  targets.py             command-line targets (path, creature:, display:, item:, go:, race:) -> files
  texture.py             export (PNG + sidecar) / import (PNG -> staged BLP) / tint
  deploy.py              staging checks, loose install/remove (manifest), pack (+ install with backup)
  cli.py                 argparse front end
  maps/formats.py        ADT / WDT / WDL readers that locate data the way the 1.12 loaders do
  maps/validate.py       loader-derived rules (evidence in the module docstring), calibrated on all stock map files
  maps/diff.py           semantic chunk-level diff of map files
  maps/cli.py            `wowdata map validate|diff`
tests/                   unittest suites on the real client files
blender_addon/
  wowdata_blender/       the Blender 4.5 add-on (imports our glTF, fixes materials/geosets, tags ids, exports back for the app)
  studio/                scripts the app runs in Blender: studio_install.py (install the add-on), studio_open.py (Open in Blender)
  build_zip.py           build\wowdata_blender.zip for "Install from Disk"
  tests/                 scripts run inside headless Blender by tests/test_blender.py and the app's ModelRoundTripTests
```

## Data flow

```
 Client\Data\*.MPQ ──(mpq.MPQArchive, read-only)──► vfs.ClientData ──► dbc / blp / m2 readers
                                                        │
 mangos DB ──(serverdb, SELECT via mysql.exe)──┐        ▼
                                               ├──► index.build ──► cache\asset_index.sqlite ──► find
 DBCs + listfiles ─────────────────────────────┘                                                    │
                                                                                                   ▼
 texture export:  ClientData.read ─► blp.decode ─► PNG + .blp.json (work\export)          extract / export targets
 texture import:  PNG + sidecar + original BLP ─► blp.encode_like ─► staging\<archive path>
 delivery:        staging ─► loose install ─► Client\Data\<archive path>         (loose-file client patch)
                  staging ─► pack ─► build\patch-3.MPQ ─(--install, backup)─► Client\Data\patch-3.MPQ
 model export:    target ─► modelctx (DBC chain: skins, CharSections composite, geosets) ─┐
                  ClientData.read ─► m2.M2.parse ────────────────────────────────────────┴─► gltf.Exporter
                      ─► <Name>.gltf + .bin + textures\*.png + <Name>.wowm2.json  (+ viewer.html with the GLB embedded)
                      ─► Blender: wowdata_blender ─► bpy glTF importer + material/geoset fix-ups + sidecar properties
```

## Key decisions

### M2: parse everything, re-serialise byte-identically

`m2/schema.py` describes every record of the 1.12 format (header, sequences, bones, vertices, views with their
submeshes and texture units, colours, textures, transparency, texture transforms, render flags, lookups,
collision, attachments, events, lights, cameras, ribbons, particle emitters) as numpy structured dtypes. The
sizes and the "which field is an array" information come from `M2Shared_FixupHeader` @0071cdf0 and its
`M2Fixup_*` helpers; field meanings from `names/structs.csv` and the evaluators (`M2Model_FindTrackKeys`,
`M2Model_EvalQuatTrack`, `M2Scene_SetBatchBlendState`...). A few names the decompile does not give (particle
parameters, some texture-unit fields) are wowdev's and marked as such; their bytes are covered either way.

Facts the decompile and the files settled:
* rotations are full 16-byte float quaternions (x, y, z, w), not the compressed 8-byte form of later versions;
* key ranges are `{first, last}` inclusive for value tracks, but `[start, end)` for event tracks (`M2Model_Update`);
  Blizzard's files often have more ranges than sequences (never read) and keys outside a sequence's window;
* any interpolation type other than 0 is a linear blend in the bone/colour evaluators; type 2 (hermite) only
  occurs in camera spline tracks;
* render flags: 0x1 unlit, 0x2 unfogged, 0x4 two-sided, 0x8 no depth test, 0x10 no depth write; blend mode 1 is
  an alpha test with reference alpha*224;
* version 0x101 (5 files) adds two uint16 arrays at header +0x144 / +0x14c.

`M2.serialize()` rebuilds the file from the structure: header, then every non-empty array in header-field
order, depth first (an array of records is followed by the arrays of its first element, then of the second...),
each block 16-byte aligned, zero padding, empty arrays as {0, 0}. This is the layout of Blizzard's exporter:
**all 9,691 client models re-serialise byte-identically**, and no byte outside the parsed blocks is non-zero. So
an edited model is written in the same layout, with every count and offset recomputed (Phase 3).

The validator mirrors the client (`offset + count * size <= file size` for every array; the parser enforces it)
and adds what the client does not check: every index into another array (bones, views, triangles, submeshes,
texture units, lookups, colours, transparency, tracks, attachments, emitters), key ranges against key counts,
weights, finite floats. All client models pass with no error.

### glTF export

* glTF 2.0 with external `.bin` and PNG files, or GLB (for the viewer page). Coordinates: `glTF = (x, z, -y)` of
  the M2; Blender's importer converts back, so Blender shows M2 coordinates.
* A bone's rest pose is its pivot (M2 bones have no rest rotation): node translation = pivot - parent pivot,
  inverse bind matrix = T(-pivot). The client's bone matrix `T(pivot) T(t) R S T(-pivot)` then equals the glTF
  skin matrix with the node animated by `pivot - parent pivot + t`, `R`, `S`. The test rebuilds both and compares.
* Each sequence becomes one animation. Per bone channel, the keys of the sequence's range; keys outside the
  window are folded into interpolated keys at its edges; global-sequence tracks are unrolled over the sequence;
  a bone animated somewhere but not in this sequence gets its rest value, so switching actions never leaves a
  stale pose.
* View 0 (full detail) only; one mesh per submesh with the material of its first texture unit. Further texture
  units, the second texture layer, colour/transparency/UV animation, particles, ribbons, lights and cameras are
  not in the glTF but in the sidecar.
* The sidecar holds everything else needed to write the model back, and the SHA-256 of the source file: Phase 3
  takes the untouched bytes (tracks, emitters...) from that original rather than from JSON.

### Reading MPQs in pure Python

All 14 archives of this client use only zlib (verified: 2,394,635 compressed sectors, all mask 0x02).
* Their block flags are: compressed (0x80000200), compressed + sector CRC (0x84000200, the two patch archives), stored (0x80000000) and delete markers (0x82000000).
* There is no encryption and no PKWARE implode.

So the reader is about 250 lines with no dependency. Decrypted hash/block tables are cached in `cache\` (pickle, keyed by path, size and mtime). Opening all archives takes about 1 s the first time and less afterwards.

### Delete markers

`patch.MPQ` holds **5,708 delete markers**. Examples:
* `Creature\OgreMage\*`, moved to `Creature\Ogre\`;
* 5,435 old `textures\` files;
* 4 DBCs.

The client's `SFile_LocateFile` stops at a marker and reports "not found". It does not fall through to `model.MPQ`. `ClientData` does the same:
* `find`/`read` return nothing for such paths;
* `where` lists the marker and the hidden copies;
* the index lists 79,733 loadable files out of 85,441 listed names.

This also corrects the research note "156 of 158 server DBCs identical". The exact split is:
* 152 identical;
* 2 project edits (Spell, SkillLineAbility);
* 4 server-only tables that the client cannot load (deleted by patch.MPQ).

### Archive order

`vfs.client_archive_order` reproduces `PatchFiles_MountArchives` @00403740:
1. `Data\patch-?.MPQ` (one character), sorted descending;
2. `patch.MPQ`;
3. `speech2.MPQ` if present;
4. model, texture, terrain, wmo, sound, misc, interface, fonts, speech, dbc.

`base.MPQ` and `backup.MPQ` are never mounted by the game (installer and launcher files). They are only read with `--all-archives`.

### MPQ writer: our own, not StormLib

The writer has to produce small, simple archives: zlib, unencrypted, with a listfile. `mpqwrite.py` writes:
* a format-1 header (44 bytes, like Blizzard's own `patch-2.MPQ`) and 4 KB sectors;
* `(listfile)` and `(attributes)`: CRC32, zero FILETIME and MD5, version 100, flags 7, as in patch-2.

It refuses stock archive names and existing files. `verify()` reads the archive back with our reader. It also re-finds every name with `MPQArchive.storm_lookup`, a probe written the way the client does it: start bucket `hash0 & (size-1)`, linear probing until an empty entry. That probe is itself tested against every name of four Blizzard archives.

Why not StormLib (MIT):
* it needs a native DLL build plus a ctypes layer;
* it adds nothing for this archive shape.

It stays the fallback if we ever need encryption, PKWARE or huge archives.

### BLP: same format, change only what was edited

`blp.encode_like(original, rgba)` starts from the original file's bytes:
* **DXT:** a 4x4 block whose decoded pixels did not change keeps its bytes. Changed blocks are re-encoded. The lower mips are rebuilt (2x2 box filter of the edited image) only where the edit reaches.
* **Palette:** unchanged pixels keep their index and alpha bits. Changed pixels take the nearest palette colour, or a new median-cut palette when the error would be visible (RMS above 6).
* The header, the mip offsets and sizes, and Blizzard's quirks are preserved. For example, 628 DXT3 files store only `max(1, (w/4)*(h/4))` blocks for thin mip levels.

Results:
* **All 52,792 DXT/palette BLP2 files** in the client round-trip byte-identically when the PNG is unchanged. They also decode at every mip level and pass `validate()`.
* A partial edit changes only the blocks it touches. The tests check this byte by byte.

`blp.validate()` encodes the client's loader rules, taken from the decompile:
* `BLP2` / type 1;
* encoding 1 or 2;
* DXT alpha encoding 0/1/7;
* palette alpha depth 0/1/4/8;
* power-of-two sizes, at most 512;
* a full mip chain when `hasMips` is set (the client derives the level count from the size);
* every level present and large enough.

The decoder was cross-checked as follows:
* **DXT:** against Pillow, exact once Pillow's plain-shift 565 expansion is mirrored. Our default is bit replication, as on the GPU.
* **Palette colours:** against Pillow.
* **Palette alpha planes:** against a plain re-implementation of `BlpImage_PalToArgb8888` @005a87d0. Pillow ignores those planes.

DXT encoder quality: a full re-encode of decoded textures gives 47.8 to 51.8 dB PSNR (axe icon 48.0, Mottled Boar skin 51.8). Edited content typically shows a mean error of about 1.4 per channel.

### Ground textures and their `_s` files (0.6.1)

The rule is the client's: `CMapArea_LoadTextureNames` @006c20e0 loads each MTEX name with `WorldTexture_LoadWithStatus`,
or, when `g_TerrainSpecularShaderOk` (0xc9a31c) is set, the name with everything from the last '.' replaced by "_s" +
the original extension. There is no fallback to the base file. `CMap_Load` @006941f0 sets the flag when the world flags
0x8000000 (CVar `specular`, `WorldCVar_OnChange_specular`) and 0x10000000 (`pixelShaders`) are both set and the terrain
shader loaded; both CVars default to "0" and refuse "1" without pixel-shader hardware. The app reads both from
`<game>\WTF\Config.wtf` (`TerrainSpecularSettings`, read only) and applies the rule in the previews
(`TerrainBuildOptions.SpecularGround`; the layers keep the MTEX names, `DrawnTexturePaths` says which file was drawn),
the terrain package (`base_png`, `specular_png`, `drawn`; `png` is the drawn one, so the add-on needs no change), the
breakdown (two child entries; `LayerPath` keeps the highlight and coverage on the MTEX name), the index (`u_ground_texture`,
the entries' Info, the details' two texture rows) and **Used in game** (a *ground* section). Of the 346 ground textures
the maps use, 17 have no `_s` file (listed by `GroundTextureIndexTests`). The `_s` file is DXT3 (8-bit alpha) with the same colours
(mean difference 0.1 per channel against DurotarDirt.blp, DXT1) and the shine in its alpha.

### Search index

`index.build` writes SQLite tables:
* `files` (every loadable archive path and its winning archive);
* `creature_display`, `gameobject_display`, `item_display`, `race`, `char_section`, `map`;
* `asset`: every file a display uses, with its role (model, skin1-3, baked skin, model texture, item model per race/sex, region textures with the `_M`/`_F`/`_U` variant that exists, icon) and the archive it comes from;
* from the server: `creature`, `gameobject`, `item` (VMaNGOS keeps one row per content patch; the highest `patch` row per entry is used) and spawn counts per map.

`find` searches names (all words must match), race + sex words, model paths (with the texture variations of each creature model) and file paths. A full build takes about 5 s.

Map-tile placements (ADT MDDF/MODF, WMO doodad sets) are indexed by the app (`WoWData.Core\Usage\MapScanner.cs`, see "Used in game" above), not by the Python index.

### Safety

* MPQs are opened `rb` only.
* All outputs go to `staging\`, `work\`, `build\` and `cache\` inside the tool folder.
* `deploy.loose_install` refuses to overwrite files it did not install (unless `--force`, which backs them up). It records every installed file with its SHA-256 and removes only unchanged files of its own.
* `deploy.pack --install` accepts only `patch-<3..9|A..Z>.MPQ`, backs up an existing one and writes through a temporary file plus rename.
* The server database is queried with SELECT/SHOW statements only (checked before running).
