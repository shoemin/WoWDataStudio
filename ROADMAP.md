# Roadmap

Based on `WoWDecomp\docs\MODEL_TOOL_RESEARCH.md` (research and the user's decisions of 2026-10-10).
Estimates are focused working days.

## Done (phase 1 + texture round trip, 2026-10-10)

* **Core:**
  * MPQ reader: all 14 archive files, the client's lookup order, delete markers.
  * MPQ writer: v1 patch archives, verified by read-back.
  * DBC reader: generic reader plus typed layouts for 10 tables.
  * BLP2: decode all variants; encode back to the same format with block reuse; client-rule validation.
* **Search index** (SQLite): DBC display chains, archive file lists, server creature, game-object and item templates with spawn counts. Commands: `find`, `extract`, `ls`, `dbc`.
* **Texture round trip:** `texture export` / `import` with sidecars, the staging folder, `loose install/remove`, `pack [--install]`.
* **Loose-file client patch:** proposed in `docs/LOOSE_FILES_PATCH.md`, to be applied through the wave pipeline.

## Done: in-game confirmation (2026-10-10)

Loose files (the client patch, applied from wave185) and `patch-3.MPQ` were both confirmed in the game by the visual
test of 2026-10-10 (`tests\ingame\results\20261010_2254\REPORT.md`). The original plan, for the record:

1. Apply the loose-file line, then `loose install` the tinted Mottled Boar and check it in the Valley of Trials.
2. Remove the loose copy, then `pack --install` and check `patch-3.MPQ` (the research left open whether 1.12 mounts it).
3. Record the results in `docs/LOOSE_FILES_PATCH.md` and in the project's `docs/CLIENT_PATCHES.md`.

## Done: phase 2, viewer and exporter (2026-10-10)

* **M2 reader / writer / validator** (`wowdata/m2/`): every record of the 1.12 format, layouts from the decompile.
  All 9,691 client models parse, re-serialise byte-identically and pass the validator with no error.
* **glTF 2.0 export** (`wowdata model export`): submesh meshes, materials with resolved textures (creature skins,
  default character skin composited from CharSections, NPC bakes, item textures), skeleton + skin, every sequence
  as an animation named from AnimationData, attachments, collision mesh, render flags in extras, the
  `.wowm2.json` sidecar. The skinning maths is tested against the client's formula.
* **Blender add-on** (`blender_addon/`, Blender 4.5): import with material fix-ups, default geoset visibility,
  orientation/scale on the root, sidecar as custom properties; "import from game data" runs the CLI. Tested headless.
* **Quick-look viewer** (`wowdata model view`): one HTML page with the model embedded, three.js 0.160.0 from jsDelivr.

Known gaps (none blocks Phase 3):
* the glTF has view 0 only and one material per submesh: extra texture units (second passes), second texture
  layers, colour / transparency / texture-transform animation, particles, ribbons, lights and cameras are only in
  the sidecar (static opacity is applied to blended materials);
* character models: no worn items (no equipment geosets or armour textures); humanoid NPCs get their baked
  texture but default facial-hair geosets; capes are a grey placeholder;
* global-sequence tracks are unrolled into each animation instead of running on their own clock;
* rotations are slerped by glTF/Blender, the client lerps then normalises (differences are tiny for the dense
  keys of 1.12 files);
* the 22 floats of the particle record between +0x17c and +0x1d4 are kept as an unnamed array (wowdev's split
  of that block is 4 bytes short; the bytes round-trip either way);
* WMO game objects are not exported (models only).

## Done: phase 3a, the desktop app WoW Data Studio, textures end to end (2026-10-10)

C# / .NET 8 / WPF, MIT, in `app\` (`WoWDataStudio.sln`: `WoWData.Core`, `WoWDataStudio`, `WoWData.Tests`); built
self-contained into `app\publish\` (`Start WoW Data Studio.cmd`). See README "WoW Data Studio" and ARCHITECTURE.
* **Core (port of the Python, checked against it by 62 tests):** MPQ reader (mount order, listfiles, 5,708 delete
  markers), patch-3.MPQ writer (byte-identical to the Python writer), DBC tables, BLP decode / encode in the original
  format re-encoding only changed blocks (byte-identical to Python's encode_like), own PNG codec.
* **Index** (SQLite, cached per client folder, rebuilt when the archives change): creatures (server entry or display ->
  model + skins), characters (race x sex, CharSections), items (ItemDisplayInfo), game objects, world objects (by path),
  spell effects (SpellVisualEffectName + the spells that use them), all 52,793 textures; **per-variant
  model <-> owner (display / section) <-> texture-slot relations** (`model_variant`) for 3b/3c.
* **Server names** (optional, read-only MySQL, VMaNGOS or CMaNGOS schema detected; "connected / not connected" in the UI,
  file and model names otherwise).
* **UI:** welcome + folder validation, "Organizing game data" progress, search + type filters + "Show only my changes",
  virtualized sortable results, details (source chain, users, textures), texture viewer (zoom/pan, alpha, mips),
  Export / Import (size and alpha checks) / **Compare original vs. edited** (side by side, slider, toggle, difference
  heat map; also the import's confirmation step) / Test in game (loose files) / Remove from game / Build release patch /
  Revert; "My changes" with change sets ("Revert this import"); settings; help panel; plain-language errors; async work.
* **Change tracking** (`changes.json`): per file the original's archive, SHA-256, format, the models/displays using it, the
  change set, loose install and patch records.

Known gaps of 3a:
* (since applied in wave185) the loose-file client patch (`docs/LOOSE_FILES_PATCH.md`) was still to be applied through the wave pipeline; until then
  "Test in game" writes the files, but the game ignores them (the dialog says so); use "Build release patch" to test;
* models: info, textures and a placeholder only (3D in 3b; done);
* a brand-new texture (no game original) is supported by the core but not offered in the UI: an import always replaces
  the selected texture;
* world doodads are found by path, not by map placement (maps module); spell visual kit slots 3-12 were located on the
  data, not the decompile;
* the server password was stored as typed in `settings.json` (fixed in 3b: DPAPI, migrated on load).

## Done: phase 3b, the 3D model viewer (2026-10-10)

See README "The 3D view" and ARCHITECTURE "The 3D viewer (Phase 3b)".
* **Spike:** OpenTK **GLWpfControl** 4.3.6 (OpenTK 4.9.4, MIT) chosen over Silk.NET: a real WPF visual (D3DImage), so
  overlays, clipping, `RenderTargetBitmap` screenshots, resize/DPI and two side-by-side viewports work; Silk.NET has no
  WPF control (HwndHost = airspace problems, or our own interop = what GLWpfControl is).
* **M2 port** (`WoWData.Core\M2`): reader, writer, validator, track evaluation, model looks. All 9,691 client models
  parse and re-serialise byte-identically, the validator matches the Python on every model, 259 models match the
  Python's parse field by field (plus 700+ animation samples), an edited model is written like the Python writer.
* **3D view in the details panel:** orbit / pan / zoom (mouse and keyboard), reset view; grid, ground, wireframe,
  skeleton, bounding box, background colour; a simple default light. Materials with the decompile's mapping: blend
  modes 0-6 (opaque, alpha key 224/255, alpha, add, mod, mod2x), two-sided, unlit, no depth test / write, colour and
  transparency animation, texture transforms, environment mapping, two texture layers, billboards. Animation: every
  sequence named from AnimationData, play / pause / speed / timeline, GPU skinning (4 weights), global sequences, the
  client's interpolation. All four views. Save image (PNG).
* **Pickers:** creatures: every display of the model with server names ("Mottled Boar (Ivory)"); items: every item
  display; characters: skin colour, face, hair style, hair colour, facial hair (composite and geosets like the game);
  humanoid NPCs: their bake, hair and beard; a geoset list with names. Model textures link to the texture viewer and
  its Export / Import / Compare actions; your edited textures show on the model.
* **Compare foundation:** `ModelCompareView` (two viewports, one shared camera, side by side / toggle, animation in
  step), demo entry Tools > Compare two skins.
* **Performance** (this machine): cold load of the Mottled Boar 4 ms and of the orc male (2,728 vertices, 128 bones, 134
  sequences) 25 ms in Core, about 30 ms in the app including the UI; skin switch under 1 ms decode + one frame; character
  look change about 3 ms; bone matrices of the orc 0.02 ms per frame; 100+ frames/s while animating; loading always on a
  background thread.
* **Also:** the server password is encrypted with DPAPI (current user) and migrated from plain text; the Python
  `character_geosets` now follows the client's facial-hair rule; `--render` developer tool.
* **Tests:** 74 (62 + M2 parity 6 + viewer 6); the UI smoke test renders the boar (two skins), the orc male (three looks,
  an animation frame, four views), a palm tree, an item, a spell effect and the compare view offscreen.

Known gaps of 3b (none blocks 3c):
* particles and ribbons are not drawn (listed in the statistics); lights and cameras of models are ignored;
* worn equipment on characters (item geosets, armour textures, attached weapons) is not shown; capes are grey;
* lighting is the viewer's own (not the zone's light and fog); the texture-transform matrix order (rotation around the
  texture centre) follows wowdev, the decompile only shows the matrix being used;
* cylindrical billboards are handled for flag 0x40 (around the bone's Z); 0x10 / 0x20 face the camera fully;
* the times between two sequences when they blend (the client's 150 ms cross-fade) are not reproduced: switching
  animations cuts;
* WMO buildings (game objects and world objects that are .wmo) show a message, not a 3D view (maps module);
* the GL context needs `WGL_NV_DX_interop` (all current NVIDIA / AMD / Intel drivers); without it the view shows a
  message.

## Done: user manual and installer (2026-10-10)

* **User manual** `docs\manual\` (14 chapters + contents, for modders), shipped as HTML (Markdig at build time); Help >
  User manual, the help panel link and F1 context help per screen. Upkeep rule in `CONTRIBUTING.md` /
  `docs\manual\MAINTAINING.md`, enforced by `ManualTests` (labels, screenshots, links, F1 topics).
* **Installer** (Inno Setup 6, per user, no admin): `build_installer.ps1` -> `dist\WoWDataStudio-Setup-<version>.exe`
  with the app, the manual, the Blender add-on zip and the licences. Not code-signed (SmartScreen note in the manual).
* 3c wrote `docs\manual\model-export-import.md`.

## Done: phase 3c, model export / import through Blender (2026-10-10, app 0.4.0)

See the manual chapter `docs\manual\model-export-import.md`, README "Models and Blender" and ARCHITECTURE "Model export /
import through Blender".
* **glTF in C#: our own reader / writer** (`WoWData.Core\ModelIO\Gltf.cs`). SharpGLTF 1.0.7 was checked: no
  `KHR_materials_variants`, and it would bring System.Text.Json 10 into the .NET 8 app; the extension is written / read as
  JSON by us. Coordinates, skeleton, skinning and animations as `gltf.py`.
* **Export for Blender** (dialog: current look / **all skins in one file as `KHR_materials_variants`** / one file per skin;
  animations on/off; open in Blender afterwards): `<Model>_export\` with `.gltf` + `.bin`, `textures\*.png`,
  `wowdata_manifest.json` (PNG -> BLP path, variants, original format, pixel fingerprint, model checksums, ids of every
  submesh / bone / material; also embedded in the glTF root's extras). **Open in Blender** (exports the shown look and
  starts Blender with the add-on). **Tools > Install the Blender add-on...** (zip + Blender's own installer, headless,
  after asking; or the zip for a manual install).
* **Blender add-on 0.3**: tags meshes, materials, images and bones with their ids (custom properties), keeps the manifest,
  and **File > Export > WoW 1.12 model for WoW Data Studio** (extras, variants, PNG textures, `wowdata_blender.json`).
* **Import edited model...** (package folder, Blender's `.gltf` / `.glb`, or the package with edited PNGs): rest-pose
  positions through the skin, the root's rotation / scale undone, joints -> bones and primitives -> submeshes by id;
  vertex matching tolerant to what Blender changes (split normals, loose vertices, float noise, back faces, duplicate bone
  entries), so an unchanged round trip is "No changes found"; review screen with include checkboxes, plain-language
  rules and **Compare** per row (3a's texture compare; two synchronized 3D views with the edited textures); one change set.
* **Mesh rebuild** (`M2MeshRebuilder`): untouched vertices keep their bytes, weights top 4 summing to 255, the four views
  regenerated for 256 / 75 / 53 / 21 bones (split per palette), bone lookup, submesh centres and centre bones, bounds and
  per-sequence bounds; everything else copied block by block; the validator must pass (also enforced by the workspace).
* **Rules**: who is affected ("A mesh change affects 27 creature displays", "This changes every orc male player and
  NPC"), vertex limit 65,535, bones outside the skeleton rejected, new objects explained (join or give them a part's
  material), missing parts kept, collision not imported; textures: power of two (resize), alpha added to DXT1
  (keep / DXT3 / DXT5), shared textures listed, new textures explained, composite character skins not importable.
* **Tests** (100 in the app suite; Blender parts skipped without Blender): the view regenerator on 1,388 client models;
  package export / read-back; Blender 4.5 headless round trips: boar (9 skins as variants), palm tree, item (10 skins),
  orc male: "no change"; scripted Blender edit (vertices moved + a texture tinted) of the boar, the tree and the item:
  only the edited part and texture change, the validator passes, all sequences evaluate, the 3D asset builds, revert
  restores both; add-on install into a temporary Blender profile; "Open in Blender" in a real Blender window; the UI smoke
  test exports, edits (scripted, without Blender), reviews, compares (both 3D sides render and differ) and imports the
  boar.

Known gaps of 3c:
* (since tested in the game on 2026-10-10: a scorpid mesh, a tree and a bush PASS with loose files and patch-3.MPQ; was:
  not tested in the game yet; the loose-file mode ships with wave185; test an edited boar with **Test in game** or the
  release patch); the bounds are recomputed by sampling (within a few percent of Blizzard's values);
* the regenerated views split a part first-fit by triangle; Blizzard's own splits differ (same triangles, other pieces);
* vertices next to an edit can come back with slightly different normals (Blender's custom normals follow the moved
  neighbours) and are then saved with Blender's normals;
* new geosets, new textures, new bones, skeleton and animation edits, collision edits: not supported (explained in the
  review; bones, skeleton and animation edits since 0.10.0, phase 5 below); character composites (body skin) cannot be imported as a whole;
* "Open in Blender" exports a fresh package each time (it does not reuse an earlier one);
* the `wowdata` command line has no import counterpart (the app is the importer).

## Done: "Used in game" context in the details panel (2026-10-10)

User request: "when looking something up, depending what it is, there should be context for how it is used in game".
A separate service (`WoWData.Core\Usage\`) and panel (`Views\UsedInGameView`, an expander under the facts); manual
chapter `docs\manual\used-in-game.md`, screenshots 40-45, tests `UsageTests` (9).
* **Per type:** creature (the NPC: level, rank, type, faction reaction to Alliance / Horde, roles; spawns per
  zone/subzone with counts and example positions; quests given / taken / kill objectives / quest drops; loot with
  chances incl. reference tables, skinning, pickpocketing; vendor and trainer lists; creature spells; NPCs and
  displays sharing the look; mount / companion / disguise spells), item (type, slot, levels, binding, spells; dropped
  by with chance and zone, chests / herbs / ore, containers, fishing; sold by with zone and limited stock; quests;
  crafted by with profession, trainers and recipe; item set; same look), game object (type in words incl. herb / mining
  node / locked chest from Lock.dbc; spawns; contents; quests), world object (placements per zone, map tiles, inside
  which WMOs), spell effect (spells per name with ranks and roles; who casts them: class trainers, creatures, items),
  character (choice counts, NPCs with the body model), texture ("Changing this texture affects" summary, creatures,
  items incl. icons, game objects, character-creation choice, models naming it with their placements, spell effects,
  spell icons), model files (all users). Every name links to its entry, with Back.
* **Index stages** (schema 4, "Organizing game data", about +4 s, total about 9 s with the server): zones, factions,
  spells, professions, item sets, locks; the **map-tile placement index** (all 2,429 ADT tiles: 378,639 M2 placements
  and 3,748 WMO placements, deduplicated by unique id, plus 152,844 WMO furniture doodads of the placed doodad sets;
  3.2 s) with the chunk area ids for zone lookups; the textures named inside all models (27,011 pairs, 0.6 s); and the
  server part (130,008 spawns with their computed zone, 268,389 loot rows with effective chances, vendors, trainers,
  4,433 quests). Reports take 1-25 ms.
* **Gaps:** script/event spawns are not known; disenchanting is shown by table number; quests are not entries of
  their own; the WMO furniture is counted at its WMO's position (the building's zone, not each doodad's); the
  faction reaction uses the faction template masks and enemy lists (not reputation); world objects cannot be
  searched by zone yet (the data is in `u_place`).

## Done: phase 4, maps and terrain (2026-10-10, app 0.5.0)

See the manual chapter `docs\manual\maps-and-terrain.md`, README "Maps and terrain" and ARCHITECTURE "Maps and terrain".
* **C# map formats** (`WoWData.Core\Maps`): ADT / WDT / WDL readers, the 1.12 validator and the semantic diff ported from
  `wowdata\maps` with the Python's messages word for word (oracle `make_maps.py`: all 2,515 client map files, no findings
  on either side; 41 edited or broken variants, same findings and diff lines); an ADT document that re-serialises all
  2,429 client tiles byte-identically and edits heights / normals / holes / placements; the placement matrix checked
  against the stock MODF boxes; MCNR shown to be world-space normals.
* **Maps tab**: maps of Map.dbc by kind, zones (AreaTable via the index's chunk areas), a clickable minimap overview built
  from the minimap textures and cached, the tile's details and an "On this tile" report, a 3D terrain preview (layers +
  alpha + shadows, holes, liquids, doodads through the model viewer's assets, simple WMO groups, optional neighbours).
* **Edit in Noggit**: client view of hard links (Noggit never gets the game folder), one project per map with a bookmark
  at the tile (no start-location option in the fork), guidance window, settings entry.
* **Import map changes**: validation in words, plain summary, review with include / Compare (two synchronized 3D terrain
  views + height difference + every change), one change set; Test map in game / release patch with the server note.
* **Update server maps** (guided): patch check, reextract.ps1 with progress and report, stop-the-server instructions,
  swap_in.ps1 with backup, Undo server update.
* **Installer 0.5.0**: Noggit and the server tools are not bundled (GPL obligations; see ARCHITECTURE "Noggit and the
  installer").
* **Tests**: 116 (map parity 9, map workflow 3, server update 4; UI smoke with the Maps screens and terrain renders).
* All screenshots regenerated (01-55) so the manual matches the window with the Maps tab.

Known gaps of phase 4:
* Noggit cannot be started at a tile (bookmark instead) and is not tested in a live session by the test suite (it is a
  GUI program); its round trip on a stock tile is still the manual test of `docs\MAPS_WORKFLOW.md`;
* the preview is not the game's renderer: no animation, particles, sky / zone lighting, WMO portals / lights / vertex
  colours / WMO doodad sets; additive and modulate doodad parts are left out; billboarded doodad parts face +x;
* minimap pictures Noggit generates are not imported; new maps / AreaTable / Map.dbc edits need the DBC editor (not yet);
* the server update was tested against fake server data folders; it was not run against the live `Server\data` (that is
  the user's step, with the world server stopped); mmaps are rebuilt per tile (mixed generations at the seam, see the
  server README);
* `swap_in.ps1` and `reextract.ps1` need Python and the built extractors (`server\bin`), which are not in the installer.

## Done: pop-out 3D windows and look-around navigation (2026-10-10, app 0.5.1)

User request: "pop the 3D view into a new window to better be able to look around", for models and maps. See the manual
(`model-viewer.md#open-in-a-new-window`, `maps-and-terrain.md#open-in-a-new-window`) and ARCHITECTURE "Pop-out 3D
windows and navigation".
* **Open in new window** over the docked model viewer and the Maps tab's terrain preview, **Tools > Open the 3D view in a
  new window**, Ctrl+Shift+O. Windows are resizable, maximizable, movable to another monitor, **Full screen (F11)** (Esc
  leaves it), with the docked controls in a collapsible side panel, **Hide controls** (Ctrl+H), F1 = the chapter.
* **Follow selection** (default) or pinned; several pinned windows side by side; the title says what is shown.
* Each kind remembers size, position, monitor, maximized, side panel and camera mode; a missing monitor puts it on the
  primary, always fitted on screen (`WindowPlacementInfo.Fit`, unit-tested).
* Decision: the docked model view stops its per-frame redraw while a *following* window is open (says so, **Bring it to
  front**); the docked terrain preview only redraws on demand anyway.
* GL: one context per view (GLWpfControl), each view disposes its renderers and context when its window closes (before,
  closed compare windows leaked their context); `GlTracker` counts contexts and objects; 20 open / close cycles per kind
  leave every count unchanged.
* Navigation in every 3D view: **Orbit** / **Fly** (W A S D / arrows, Q / E, Shift / Ctrl, right-drag look, wheel = speed),
  **Front** / **Side** / **Top** / **Reset view** (1 / 2 / 3, Home), F = focus on the selection, H = help overlay. Models
  default to Orbit, the terrain pop-out to Fly. The compare windows got **Full screen (F11)** and the same keys.
* Tests: 122 (`PopoutTests` 6 new; the UI smoke run checks the pop-outs off screen, screenshots 56-62).

Known gaps of 0.5.1:
* fly mode has no collision with the ground and no "walk" mode; the speed is not remembered between windows;
* a pinned window does not reload when the model or tile is edited by an import (a following one does);
* the terrain window has no **Save image...** yet; the compare windows do not remember their placement;
* the docked view's pause is all-or-nothing (no "pause only when the pop-out is on another monitor");
* full screen covers the monitor the window is on; spanning several monitors is not supported.

## Done: tile breakdown and terrain in Blender with neighbours (2026-10-10, app 0.6.0)

User request: a view that breaks a map tile into all its pieces (buildings, trees, textures, terrain...), each viewable
and jumpable to Browse, with "Open in Blender"; terrain into Blender with as many neighbour tiles as needed, marked as
separate pieces, so edge details can be matched to the neighbouring tile. See the manual (`maps-and-terrain.md#break-down-a-tile`,
`terrain-in-blender.md`) and ARCHITECTURE "Tile breakdown and terrain in Blender (0.6.0)".
* **Break down this tile...** (Maps tab, terrain pop-out, Tools): grouped, searchable list (terrain + 256 chunks, ground
  textures with coverage, doodads by model with every placement, buildings with groups and doodad sets, water per chunk,
  sound emitters, shadows, zones); **In the tile** (highlight, rest dimmed, camera to it) / **On its own** (model renderer,
  building alone, texture + coverage map, height heat map); **Show in Browse**; **Open in Blender** per kind.
* **Open terrain in Blender...**: tile picker (1 x 1 / 3 x 3 / 5 x 5 or clicked), terrain package with heights, ground
  textures and weight images, optional doodad / building references; add-on 0.4.0: one object per tile with collection,
  colour, label, EDGE_N/E/S/W groups, game-like ground material; *Sync shared edges*, *Check edge seams*, *Export terrain*.
* **Import edited terrain...**: heights only (vid attribute, position fallback; sideways moves snapped, added / deleted
  vertices reported), seam check against imported tiles and existing neighbours with sync (an unedited neighbour is pulled
  in), normals recomputed around the change (formula fitted on the stock tiles: 0.37 degrees mean), byte-for-byte
  elsewhere, validator, the map import review / compare / change set.
* Installer: `-TestBuild` (separate AppId / name / group) and `installer\test_install.ps1`, which checks the user's real
  installation is untouched; rule in CONTRIBUTING.md.
* Tests: 134 (12 new: `TerrainBreakdownTests`, `TerrainBlenderTests`, `UsageTests.ShowInBrowseKeysResolveInTheIndex`; the UI smoke run walks the
  breakdown and a terrain import, screenshots 63-70; Blender screenshots 71-74 by `blender_addon\tests\manual_shots.py`).

Known gaps of 0.6.0:
* only heights come back from Blender: texture painting, holes, water, doodad / building placement stay in Noggit;
* buildings open in Blender as a plain shape to look at (no textures, not importable); WMO doodads (furniture) are not
  in the references;
* a tile's Blender material shows at most 12 ground textures (GPU texture slots; a note lists the others);
* normals of an existing neighbour tile that is not part of the import are left as they are when only the interior next
  to the edge changed (its edge heights are unchanged; its lighting at the edge may differ by a degree);
* the breakdown's 3D view shows the tile alone (no neighbours) and its highlight is per placement (no outline effect).

## Done: fixes from the in-game visual test (2026-10-11, app 0.6.1)

The visual test of 0.6.0 (`tests\ingame\results\20261010_2254\REPORT.md`: 11 change types, loose files and patch-3.MPQ)
found five app bugs; all fixed (details in `CHANGELOG.md`):
* ground textures: the game draws `<name>_s.blp` with terrain specular on (`CMapArea_LoadTextureNames` @006c20e0); both
  files everywhere, the game's `Config.wtf` read, **Edit both** keeping the shine map, previews and the Blender package by
  the same rule (**Specular ground (_s textures)**), `u_ground_texture` in the index (schema 5);
* colour and alpha counted separately in every pixel-change figure, colour under alpha 0 never hidden; the model import
  decides by the material's blend mode;
* the additive / multiply note and **Keep black areas black** (`MaterialBlends`: M2 units, ribbons, particles, WMO);
* **Uninstall release patch (patch-3.MPQ)** with the installed patch's state, the earlier patch put back, a foreign one
  only moved after a warning; unique backup names;
* `--render` shows the workspace (`--original` for the game's files).
* Test suite made deterministic (one shared index, one collection for index builds and the app, `[ServerFact]`, timing
  limits only in `PerformanceTests`); 147 tests, 5 consecutive full runs green.
* Documentation: `CHANGELOG.md`, the manual's `known-issues.md` (kept by `ManualTests`), screenshots 75-82.

Known gaps of 0.6.1: see `docs\manual\known-issues.md` (the user-facing list). Still to do: re-run item 05 of the
in-game test (an edit of `DurotarDirt.blp` with **Edit both**) to confirm in the game; check in the game what the 17
ground textures without an `_s` file show with terrain specular on; check the particle blend numbering against the
decompile.

## Done: the DBC editor (2026-10-11, app 0.7.0)

The **Data tables** tab (manual chapter `docs\manual\dbc-editor.md`, details in `CHANGELOG.md`):
* typed layouts of all 154 tables of the 1.12.1 client in a readable text file (`dbc_layouts_5875.txt`), from the
  client's record readers in our decompile, the VMaNGOS loader and checks on the files: 107 tables fully named, 30
  partly, 17 raw apart from the ID; each layout records its evidence;
* a fast grid with search, column filters, sorting, Go to ID; links with the linked row's name, followed with Back;
  icon / texture previews and **Show in 3D** through the Browse tab; **Used by** (reverse links);
* checked editing, add / duplicate / delete rows, bulk edit, undo / redo, localized texts per language;
* saving into the workspace as one change set (My changes, row-by-row Compare, Test in game, release patch); writing is
  byte for byte for unchanged tables (all 154 checked), the string block deduplicated in first-use order like the
  client's files;
* CSV / JSON export and import with a preview diff and validation;
* the server side per table (client only / server file / server database) and **Copy to server** with backup and undo.
* 166 tests (18 new in `DbcEditorTests`, the spell-effect roles in `IndexTests`), consecutive full runs green; screenshots 83-94.

Known gaps of 0.7.0 (`docs\manual\known-issues.md`): 270 of 1,484 columns not verified; edits of database-backed
tables change only what the game shows; the index (Browse, Used in game) does not see table edits; links to server
entries (items, creatures) are not checked. Possible next steps: let the index read the workspace's tables (new
displays in Browse and 3D); a server-database editor for `spell_template` & co. next to the DBC rows; more columns
proven from the decompile.

## Done: the command line `wds` (2026-10-11, app 0.8.0)

Every feature of the app as a command (manual chapter `docs\manual\command-line.md`, details in `CHANGELOG.md`):
* `wds.exe` (`app\WoWData.Cli`, no WPF): 81 commands in groups like the app's tabs (setup, settings, serverdb, index, find,
  textures, models, maps, server-maps, dbc, changes), `--help` with examples, `--json`, documented exit codes, `--yes`
  and `--dry-run`, `--home`, the same safety rules as the app;
* the logic of the app's view models moved into Core (session, search, texture import flow, change operations, map
  operations, server-map updater, table edit session, settings catalog): one implementation for both front ends;
* one product, two front ends: one version (`app\Directory.Build.props`), one installer with both programs, a command
  prompt shortcut and an optional PATH entry; the feature registry and its tests keep the app, the command line and the
  manual in step;
* two bugs found on the way and fixed (installing the release patch over an installed one; two programs on one workspace).

Known gaps (`docs\manual\known-issues.md`, *The command line*): rendering needs the app's renderer and OpenGL; one size /
format choice for all textures of a model import; the zone filter needs the server's spawns. Possible next steps: a
`wds watch` / batch mode that keeps the session open for long scripts (each command now opens the game data, about a
second); a PowerShell module with completion.

## Done: the server data editor (2026-10-13, app 0.9.0)

The **Server data** tab and `wds server` (manual chapter `docs\manual\server-data.md`, details in `CHANGELOG.md`):
* the VMaNGOS world database in friendly views (items, creatures, quests, game objects, spells, every loot table,
  vendors, trainers, areas, maps, flight paths, factions, faction templates, languages, all 176 tables raw); column
  names and types from the live schema, their meaning from a readable layout file (53 tables described); VMaNGOS row
  versions (content patch, client build, ranges) with the one the server uses; links everywhere, Used by;
* checked edits (SQL types, VMaNGOS load rules, links), add / duplicate with free IDs of a custom range, delete with
  Used by, bulk edit, undo / redo; saved into the workspace (one file per table, before / after rows, My changes);
* client / server checks (Spell, ItemDisplayInfo, CreatureDisplayInfo, Map, AreaTable, Faction, FactionTemplate,
  TaxiNodes, SkillLineAbility) with "make them match" both ways;
* an idempotent SQL patch with its revert, Apply to server (backup of rows and tables, one transaction, read back, log,
  conflicts refused) and Undo last apply; the `.reload` commands per table; clearing the game's WDB cache;
* CSV / JSON with a preview; 21 commands; the tests on a throwaway MySQL server loaded from a schema + sample fixture
  (never the live database), the app and the command line making byte-identical server edits.

Known gaps (`docs\manual\known-issues.md`, *Server data*): MyISAM tables cannot roll back (the app restores its
backup); a restart or `.reload` and a cleared game cache are needed to see edits; 123 tables only raw; tested against
throwaway databases only. Possible next steps: describe the script / AI / pool / event tables; a map view of spawns
(place a spawn on the minimap); a `.reload` sender through the server's remote console; CMaNGOS layouts.

## Done: phase 5, the skeleton and animation round trip (2026-10-13, app 0.10.0)

Bones, animations, attachment points and events to Blender and back (manual chapter
`docs\manual\skeleton-and-animation.md`, details in `CHANGELOG.md`):
* the client's track semantics from the decompile (`M2Model_FindTrackKeys` @00713d50: inclusive ranges, linear or step
  interpolation, quaternions lerped component-wise; events in half-open ranges, `M2Model_Update` @00719380; variations by
  frequency, `PlayAnimation`; the playable-animation table, `M2Model_ResolvePlayableAnimation` @00711bf0 with
  AnimationData's flags and fallback, matching 9,647 of the 9,691 client models); the lookup rules checked on every model;
* export with **Skeleton and animations (for editing)** (armature with key bones, every sequence an action named by
  AnimationData, global sequences as their own actions, attachment and event empties, event markers, the particles /
  ribbons JSON); the Blender add-on 0.5.0 (the WoW panels, the rig sidecar `wowdata_rig.json`);
* the import plans a new rig (bones added, removed, re-parented, key bones; sequences edited, retimed, new or removed;
  global sequences; attachments; events) with plain-language rules, writes it in *append mode* (unchanged sequences
  keep their keys byte for byte, edited and new ones go to the end of the one timeline), rebuilds every lookup and
  remaps every bone reference; mesh and rig are imported together;
* preview: animation picker and time slider in the compare window, attachment points, loop toggle, animation info and
  events in the viewer, `--sequence` / `--time` for render and compare; Used in game for animations (the client's own
  situations, emotes, spells);
* `wds models anims`, `wds models skeleton`, `--with-skeleton`, 8 new registry features with app / command-line parity;
* proven: identity plans byte-identical and re-encoded / relocated tracks semantically identical on all 9,691 client
  models; export with the skeleton -> import without edits comes back unchanged for all 9,691 models (and in the suite
  on a 25-model sample, through headless Blender for five); a scripted Blender edit (new bone, new animation, retimed
  walk, moved attachment point, new event) imports, validates and reads back.

Known gaps (`docs\manual\known-issues.md`, *Skeleton and animations*): not tested in the running game by the tests;
Blender curves come back as linear keys sampled per frame; removed animations leave unused keys; particles and ribbons
only as numbers (no new emitters); portrait camera splines are stretched, not refitted; 44 models lose Blizzard's
oddities in their playable-animation table after an animation edit. Possible next steps: compacting the timeline
(dropping unused keys); particles and ribbons in the viewer and in Blender; new geosets and new textures (new skins as
display ids).

## Done: the spell designer and the class and talent designer (2026-10-14, app 0.12.0)

User request: guided designers for the custom content of `WoWDecomp\docs\CUSTOM_CLASSES.md` (8 hero classes) and
`VANILLA_HERO_PROGRESSION.md` (the vanilla classes' progressions). Manual chapters `docs\manual\spell-designer.md` and
`docs\manual\class-and-talent-designer.md`, details in `CHANGELOG.md`:
* **Spell designer:** new or cloned spells (free IDs of the custom range), the grouped form with friendly units, the
  tooltip preview (the client's own `$` rules), checks from the client's code and VMaNGOS's handler tables, visuals
  (whole, by part, duplicated with tint and size, 3D preview), trainers (with their learn spells) and skill lines; the
  game's tables and the server's rows saved together;
* **Classes and talents:** talent trees on the grid with the client's limits, the class's spells by level with level and
  trainer assignment, class masks, an experimental new class with the engine report (client addresses and interface
  files, server source lines);
* 28 commands (`wds spells`, `wds talents`, `wds classes`), 35 registry features, the GUI / CLI parity test.

Known gaps (`docs\manual\known-issues.md`, *Spell and class designers*): a new class needs code changes in the client
and the server before it can be played; not yet tried in the running game; tint / size of duplicated visuals are an
approximation; the preview ignores talents; scripted effects cannot be checked. Possible next steps: the code changes
the engine report lists (client project waves, server source); a "one discipline per position" rule for the Warrior's
Stance Disciplines (server script); importing a class or talent design from a file (JSON) for whole hero classes.

## Done: the quest-chain designer (2026-10-14, app 0.13.0)

User request (the last part of step 3): a guided designer for the quest chains of the hero-class progressions
(`WoWDecomp\docs\VANILLA_HERO_PROGRESSION.md`: the Warrior's Stance Disciplines, one chain per discipline with
Alliance and Horde variants at real outposts; `CUSTOM_CLASSES.md`: the hero classes' class quests). Manual chapter
`docs\manual\quest-chain-designer.md`, details in `CHANGELOG.md`:
* **Flowchart** of any quest chain (lanes per faction / race / class, steps, the five kinds of links, choice and
  "all of" groups, problem badges); the Whirlwind weapon, Brutal armour and Dreadsteed chains as examples;
* **quest editor** in plain fields with the client's token preview, givers and enders with the NPC's model and the map,
  drop sources linked to the loot editor, speak-to (talk credit) and explore (area triggers) objectives, reward spells
  through the spell designer;
* **chain building**: after / before, branches (choices), joins, delete with relinking, the other faction's copy with
  swaps, the discipline template, new quest-giver NPCs placed on the map (pins, "Pick on the map"), quest log
  categories;
* **checks** from VMaNGOS's quest rules and the 1.12 client's quest cache and window;
* 27 commands (`wds quests`), 38 registry features, the GUI / CLI parity test; the test database's quest-chain
  supplement (real chains, read-only from a server).

Known gaps (`docs\manual\known-issues.md`, *Quest-chain designer*): not yet tried in the running game; faction checks
from the faction tables only (no scripts, no raised reputation); MySQL's 6-digit floats make "Undo last apply" see a
change after more precise values; chains drawn up to 150 quests; translations not updated; escorts need server
scripts. Possible next steps: the concrete quest steps of every discipline (`VANILLA_HERO_PROGRESSION.md` W.5), built
with the template and checked in the game; a "Server data"-wide float rounding to 6 digits; reputation-aware faction
checks for a chosen player standing; escort waypoints on the map.

## Maps module: what was planned (done in phase 4 above, kept for the record)

* **Noggit (Cinder) as the external map editor** (GPL-3: run as a separate program only; our fork in
  `third_party\noggit-cinder-fork`), with **our validator** (`wowdata map validate / diff`, written from the decompile and
  calibrated on all 2,515 stock map files) checking its output before packing. WoWDataTool supplies the search, the
  patch-3 packing and the server re-extraction. If Noggit falls short for 1.12 read/write: our own ADT/WDT/WDL reader, a
  tile-to-Blender export, and a fixed-topology round trip (research section 9).
* **Search index addition: done** with "Used in game" (above): every ADT tile and the WMO doodad sets are scanned and
  per model the tiles, placement counts and zone are stored (`u_place`; DurotarPalm01-03: 1,573 placements on
  Kalimdor). Still open: searching world objects *by zone* in the search box ("tree durotar").
* **Server re-extraction script:** `mapextractor` needs `patch-3.MPQ` added to its fixed list
  (`work\server_src\contrib\extractor\System.cpp`, then rebuild); vmaps/mmaps rebuilt per tile.
* WMO geometry editing is deferred (BSP, portals).

## Smaller items

* **New skins as new display IDs:**
  * a writer for CreatureDisplayInfo rows into a patched `DBFilesClient\CreatureDisplayInfo.dbc` (staged), plus the identical server DBC copy;
  * SQL for `creature_template` / `creature_display_info_addon`: done in 0.9.0 (the Server data tab, its client /
    server check adds a missing `creature_display_info_addon` row);
  * a reminder to clear the WDB cache: done in 0.9.0 (**Clear the game's cache (WDB)...**).
* **DBC writer** with typed layouts (needed by the above): done in 0.7.0 (the DBC editor); a new display row can now be
  made with **Duplicate as new** in CreatureDisplayInfo and copied to the server.
* **Hot reload** (research section 5, method C): `SFile2_ResetFileMap` plus evicting the texture/M2 caches through a hook:
  app side done in 0.11.0 (**Reload in running game**, `wds changes reload`, the Settings switch, the release patch while
  the game runs); the game side is staged as `WoWDecomp\work\staged\HR1-hotreload` (textures in place, models, buildings
  and terrain through the game's own rebuild / restream paths, tables in place) and comes with a wave of the client
  project; then the in-game test `tests\ingame\hotreload` (see `docs\HOT_RELOAD.md`). Still open after that: character
  skin textures without a relog (re-composing the character's look), WDT / WDL without a relog.
* `extract` for `.wmo` targets including their group files and textures.
* `ls --deleted` to show the files hidden by delete markers.
* Texture override directory: `Texture_CreateFromBlp` @0044a310 checks an override folder (`DAT_00905bd4`). Investigate it as a texture-only alternative to loose files.

## Backlog: other data types (user decision 6; not started; each becomes an app module after phase 4)

| Data type | Tool idea |
|---|---|
| DBC tables | **Done in 0.7.0** (the DBC editor: every table, typed layouts, editing, CSV / JSON, Copy to server). The server's database tables (spell_template...) next to the DBC rows: **done in 0.9.0** (the Server data tab) |
| Sounds and music (.wav/.mp3, SoundEntries/ZoneMusic DBCs) | Browse, preview and replace; zone music and ambience assignment |
| Zone lighting and sky (Light*.dbc, LightParams) | Lighting/sky editor with a live preview |
| Intro video and loading screens | Replace the cinematic and loading-screen art |
| Minimap tiles (textures/minimap, md5translate.trs) | Regenerate minimap tiles after terrain edits (maps module) |
| Spell visuals (SpellVisual* DBCs, particle emitters in models) | Spell-visual browser and editor |
| Interface (XML/Lua/TOC, fonts, interface art) | UI art and font replacement (Blizzard FrameXML is signature-checked and quarantined; add-on style edits already work) |
| Leftover types (lights.lit, water-level files, dnc.db) | None: the 1.12 client never loads them |
