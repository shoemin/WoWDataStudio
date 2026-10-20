# Third-party code and references

WoWDataTool is MIT-licensed (see `LICENSE`). Rules:
* code may be copied or adapted only from MIT, Apache-2.0, BSD or zlib projects, credited here;
* GPL projects (WoW Model Viewer / WMVx, Noggit and Noggit Cinder, WoW Blender Studio) are used only as separate programs, or read for behaviour, never copied or linked;
* unlicensed code (e.g. the classic WBS fork) is not used at all.

## Code copied or adapted

**None.** Everything in `wowdata/` was written for this project:

* **MPQ hashing and encryption** (`crypto.py`): an independent implementation of the public algorithm. It was checked against the client's own Storm code in our decompile (`SFile_HashString`, `SFile_LocateFile` @006549a0) and against the archives themselves.
* **DXT encoder** (`dxt.py`): our own numpy implementation of the well-known approach of principal-axis endpoints, inset, nearest index and least-squares refinement. The idea is described publicly (for example in stb_dxt, public domain / MIT, by Fabian Giesen and Sean Barrett, and in libsquish, MIT, by Simon Brown); no code was taken from either.
* **DBC layouts of other versions** (`app\WoWData.Core\Dbc\Layouts\dbc_layouts_8606.txt`, `dbc_layouts_12340.txt`, 0.14.0): measured on the
  2.4.3 and 3.3.5a clients' own files (value matching with the 1.12 layouts, string and link checks); public format
  descriptions (wowdev.wiki) used as a reference for the names of new columns only, marked "(wowdev)" in the evidence.
* **BLP, DBC, M2 layouts:** from our own decompile of `WoW.exe` build 5875 (`WoWDecomp\names`, `work\out60\src`, `work\out61\src`) and the public format descriptions on wowdev.wiki (used as a reference for field names only; `m2/schema.py` marks those names "(wowdev)").
* **glTF writer** (`gltf.py`): written from the Khronos glTF 2.0 specification; no exporter code was used.
* **Blender add-on** (`blender_addon/wowdata_blender`, and the `blender_addon/studio` scripts WoW Data Studio runs in Blender): our own code, MIT. It calls Blender's Python API (`bpy`) and Blender's bundled glTF importer at run time. Blender is GPL; the Blender Foundation considers add-ons that use `bpy` to be covered by the GPL when distributed together with Blender. MIT is GPL-compatible, so the add-on can be distributed under MIT on its own and combined with Blender without conflict. Nothing from Blender or its glTF add-on (Apache-2.0) is copied.
* **Viewer page** (`viewer.py`): our own HTML/JS; it loads three.js at run time (see below).

### WoW Data Studio (`app\`, C#)

**Server data (0.9.0):** the meaning of the VMaNGOS world database's tables (`app\WoWData.Core\ServerData\Layouts\server_layouts_vmangos.txt`)
was written from facts read in the VMaNGOS schema and its loaders (which rows a 1.12 server loads, the checks at start,
the `.reload` commands) and checked on the local database; no VMaNGOS (GPL) code was copied. Test data only, never
shipped: `app\WoWData.Tests\serverdb\world_fixture.sql` holds the schema of the local world database and a small
sample of its rows (VMaNGOS world-database content), for the tests' throwaway database; the tests run the local
server's MySQL program (`mysqld.exe`, GPL) as a separate program to host it. Neither is part of the installer.

**No code copied** either. `WoWData.Core` is a port of our own Python code (above): MPQ reader/writer, DBC, BLP/DXT
codecs, M2 reader/writer/validator/animation, model looks, index. The 3D renderer (`WoWDataStudio\Rendering`: shaders,
skinning, M2 material state from our decompile) is our own; WoW Model Viewer / WMVx were not used. The PNG codec (`Imaging\Png.cs`, written from the PNG specification, W3C/ISO 15948), the bzip2 decoder (`Mpq\Bzip2.cs`, written from the public description of the bzip2 1.0 file format; no bzip2 library code; tested against Python's bz2 output), the image resize,
the compare heat map and the median-cut palette (our own; Pillow's is not used) were written for this project.

NuGet packages, **bundled** in the self-contained publish (`app\publish\bin`):

| Package | Version | Licence | Used for |
|---|---|---|---|
| Microsoft.Data.Sqlite (+ .Core) | 8.0.10 | MIT | the search index (SQLite) |
| SQLitePCLRaw.bundle_e_sqlite3 (+ core, provider, lib) | 2.1.6 | Apache-2.0 | SQLite native binding used by Microsoft.Data.Sqlite |
| SQLite (e_sqlite3.dll, inside the lib package) | 3.x | public domain | the database engine |
| MySqlConnector | 2.3.7 | MIT | queries of the server's world database (read-only, except "Apply to server" / "Undo last apply", 0.9.0) |
| Microsoft.Extensions.Logging.Abstractions | 7.0.1 | MIT | dependency of MySqlConnector |
| System.Memory | 4.5.3 | MIT | dependency of SQLitePCLRaw |
| CommunityToolkit.Mvvm | 8.2.2 | MIT | MVVM helpers (observable properties, commands) of the WPF app |
| OpenTK.GLWpfControl | 4.3.6 | MIT | the OpenGL 3D viewport inside WPF (renders into a D3DImage) |
| OpenTK (Graphics, Mathematics, Windowing.Common / Desktop / GraphicsLibraryFramework, Core, Input, Compute, Audio.OpenAL) | 4.9.4 | MIT | OpenGL bindings and the context creation used by GLWpfControl (the Compute and OpenAL binding assemblies come with the meta-package and are not used) |
| OpenTK.redist.glfw (GLFW 3.4, `glfw3.dll`) | 3.4.0.44 | zlib | creates the (hidden) OpenGL context for GLWpfControl |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | Windows DPAPI: the server password in settings.json is encrypted for the current user |
| .NET 8 runtime and WPF (self-contained publish) | 8.0 | MIT | the runtime, bundled so that nothing has to be installed |

Test-only (not shipped): xunit 2.9.2 and xunit.runner.visualstudio 2.8.2 (Apache-2.0), Microsoft.NET.Test.Sdk 17.11.1
and its dependencies (MIT; Newtonsoft.Json 13.0.1, MIT). The tests run the Python package as the test oracle (separate process).

Build-time tools (not part of the app):

| Tool | Version | Licence | Used for |
|---|---|---|---|
| Markdig | 1.3.2 | BSD-2-Clause | `app\tools\ManualBuilder` renders the user manual (`docs\manual\*.md`) to HTML pages. The pages contain no Markdig code. |
| Inno Setup | 6.7.3 | Inno Setup License (permissive: any use, including commercial; jrsoftware.org asks commercial users, not non-commercial ones, to buy a licence, and says it is not strictly required) | compiles the installer (`installer\WoWDataStudio.iss`). The installer carries Inno Setup's setup and uninstall program under that licence (copyright Jordan Russell and Martijn Laan). |

Silk.NET (MIT) was evaluated for the 3D view and not used (no WPF control; see ARCHITECTURE "The 3D viewer").
SharpGLTF (MIT, 1.0.7) was evaluated for Phase 3c and not used: it has no `KHR_materials_variants` support and would
bring System.Text.Json 10 into the .NET 8 app; the glTF reader / writer (`WoWData.Core\ModelIO\Gltf.cs`) is our own,
written from the Khronos glTF 2.0 specification (see ARCHITECTURE "Model export / import through Blender"). Phase 3c adds
no package: the add-on zip uses System.IO.Compression (part of .NET). Phase 4 adds no package either: the map formats,
validator, diff, terrain renderer, minimap and WMO reader are our own (`WoWData.Core\Maps`, `WoWDataStudio\Rendering\Terrain*`),
written from our decompile, the real files and our Python reference.

## Runtime dependencies (installed separately, not bundled)

| Package | Licence | Used for |
|---|---|---|
| Python 3.10+ | PSF | everything |
| numpy | BSD-3-Clause | DXT codec, image operations |
| Pillow | MIT-CMU (HPND) | PNG read/write; median-cut palette generation for new palette BLPs; cross-check of our BLP decoder in the tests |
| SQLite (Python's `sqlite3`) | public domain | search index |
| Blender 4.5 LTS (Microsoft Store build) | GPL-2.0-or-later | main model viewer/editor; the add-on runs inside it; the tests run it headless as a **separate program** |
| three.js 0.160.0 (`cdn.jsdelivr.net/npm/three@0.160.0`) | MIT | the quick-look viewer page loads it from the CDN when opened; not bundled |
| MySQL command-line client (`Server\mysql5\bin\mysql.exe`) | GPL-2.0 | read-only queries of the server DB. It is run as a **separate program** (subprocess); nothing is linked |
| Noggit Cinder, our fixed fork (`third_party\noggit-build\bin\noggit.exe`, source `third_party\noggit-cinder-fork`) | GPL-3.0 (Qt 5.15: LGPL-3.0; StormLib, CascLib, Lua: MIT) | the map editor of the app's "Edit in Noggit"; run as a **separate program**, not bundled |
| The server tools (`server\reextract.ps1`, `swap_in.ps1`, `tools\*.py`: MIT; the vmangos extractors in `server\bin`: GPL-2.0) | MIT / GPL-2.0 | "Update server maps" runs the scripts with PowerShell; the scripts run the extractors as **separate programs**; not bundled |

## Considered, not used

| Project | Licence | Why not |
|---|---|---|
| StormLib (Ladislav Zezula) | MIT | Licence-compatible, but our own ~150-line writer avoids a native DLL build and a ctypes binding. Our archives are simple (zlib only, unencrypted). Can be adopted later if needed. |
| wow.export | MIT | JavaScript/Electron application, read-only. Not needed: the M2 reader and glTF export were written from our decompile and the glTF spec. |
| warcraft-rs | MIT / Apache-2.0 | Rust. A possible reference for M2 v256 writing (unverified for vanilla). |
| pywowlib | MIT | Its 1.12 M2 support is "planned". |
| WMVx, Noggit / Noggit Cinder, WoW Blender Studio | GPL-3 | GPL: may be used as separate tools only. Noggit Cinder is used as the map editor through our own GPL fork in `third_party\noggit-cinder-fork` (run as a separate program). `wowdata map validate/diff` and their C# ports (`WoWData.Core\Maps`) were written from our decompile and the real files, not from Noggit. WoW Data Studio starts `noggit.exe` and writes / reads the plain JSON project file (`.noggitproj`) and the map files Noggit saves; no Noggit code is copied, linked or shipped. **Noggit is not part of the installer** (see README "Installer"). |

## Game data

Models, textures, DBCs and maps are Blizzard's. They are not covered by our licence and are not part of this repository. The tool only reads and writes the user's own local client files.
