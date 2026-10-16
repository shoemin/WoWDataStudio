# Contributing

## The manual is part of every change

WoW Data Studio has a user manual for modders in `docs\manual\` (shipped in the installer, opened by F1 and
**Help > User manual**). **Every change that adds, removes, renames or changes a feature updates its manual chapter and
its screenshots in the same change.** How to write it, take screenshots and run the check:
[`docs\manual\MAINTAINING.md`](docs/manual/MAINTAINING.md).

`app\WoWData.Tests\ManualTests.cs` enforces the mechanical part: every bold UI label in the manual must exist in the
app's XAML or C# text, every referenced screenshot must exist, and every F1 topic must have a chapter.

## One product, two front ends: the app and the command line (0.8.0)

WoW Data Studio has two front ends over one core: the desktop app (`app\WoWDataStudio`, WPF) and the command line `wds`
(`app\WoWData.Cli`, no WPF). They are always built from the same solution, installed together and carry the same version
(`app\Directory.Build.props` is the only place of the version; `build_installer.ps1` fails when either program is missing
or their versions differ).

**A change to a feature is not done until the GUI, the CLI, the feature registry, the manual chapter, `command-line.md`,
`known-issues.md` and `CHANGELOG.md` are all updated in the same change.** In practice:

* **Every new feature of the app gets its `wds` command**, with `--help`, examples, `--json` output, the documented exit
  codes, `--dry-run` where the app shows a preview and `--yes` for anything that changes the game folder or the server or
  deletes something. Only looking at things on screen, moving between tabs and an editor's unsaved state may stay
  app-only, and then with the reason in the registry.
* **The logic lives in `WoWData.Core`**, never in a view model or a command: the app and `wds` call the same Core code
  (no copies), so they give the same results. A view model only asks questions and shows answers; a command only parses
  options and prints results.
* **The feature registry** (`app\WoWData.Core\Features\FeatureRegistry.cs`) lists every feature with its app label and
  its command. `FeatureParityTests` fails when a feature has no command (or no reason to be app-only), when a command
  belongs to no feature, when a label is not in the app, when the command line references WPF, or when the versions
  differ.
* **The manual chapter `docs\manual\command-line.md`** documents every command (a `#### wds <command>` section each; its
  reference part is printed by `wds commands --json`), and its coverage table is the registry (`wds features --markdown`
  prints it). `ManualTests.CommandLineChapterMatchesTheProgram` fails when they differ, when a command written anywhere in
  the manual does not exist or uses an option it does not have, or when a feature chapter has no `## Command line` note.
* **Tests:** a command gets a test in `CliTests` (isolated `--home`, fake game and server folders, `--json` and exit codes);
  a change to shared logic gets a check in `ParityTests` when the app and the command line could drift.
* **Tests never write the user's databases (0.9.0).** Anything that reads or writes the server's world database is
  tested against the throwaway MySQL server of `ServerTestDb` (`app\WoWData.Tests\ServerTestDb.cs`): its own empty
  data folder in `%TEMP%`, its own port on 127.0.0.1, loaded from `serverdb\world_fixture.sql`, a fresh copy of the
  database per test, `[IsolatedDbFact]` (skipped when no `mysqld.exe` is found; never pointed at another server). A test
  that writes asserts it is on that server first (`ServerTestDb.AssertIsolated`). The fixture is remade with
  `dotnet run --project app\tools\ServerFixtureBuilder -- --server-folder <Server> --out app\WoWData.Tests\serverdb\world_fixture.sql`
  while the server's database is stopped (it copies the database files and reads the copy) or with `--live-port`
  (SELECT / SHOW only). The manual's screenshots of the Server data tab use the same kind of throwaway server.

## Before you finish a change

1. `dotnet test app\WoWData.Tests\WoWData.Tests.csproj` passes (including the UI smoke test and `ManualTests`).
2. The manual chapter of the feature is updated, with new screenshots where the screen changed, and its command in
   `command-line.md` and the feature registry (see above).
3. `README.md`, `docs\ARCHITECTURE.md` and `ROADMAP.md` are updated where they describe the change.
4. `docs\manual\known-issues.md` and `CHANGELOG.md` are updated: a bug you found and did not fix is added to the known
   issues (what happens, who is affected, the workaround, status); a bug you fixed is *removed* from them and recorded in
   the changelog under the new version (`ManualTests.KnownIssuesAndChangelogAreKept` checks the format).
5. A release is built with `build_installer.ps1` (see README "Installer"); the installer is tested only as described in
   "Testing the installer" below.

## Testing the installer: never touch the user's real installation

A silent test install / uninstall of the normal installer replaces and then deletes the user's real "WoW Data Studio"
(its Start-menu folder, its Apps entry and its program folder), because it has the same AppId and Start-menu group. This
happened once. The rule:

1. **Never run the normal setup (`WoWDataStudio-Setup-<version>.exe`) silently for a test, and never run the real
   uninstaller** (`%LOCALAPPDATA%\Programs\WoW Data Studio\unins000.exe`). Installing or upgrading the real app is the
   user's decision.
2. Test the installer only with the **TEST build**: `build_installer.ps1 -Version <x.y.z> -TestBuild` writes
   `dist\WoWDataStudio-TestSetup-<x.y.z>.exe`, a separate product "WoW Data Studio TEST" with its own AppId, install
   folder, Start-menu group and Apps entry.
3. Install it only through `installer\test_install.ps1 -Setup dist\WoWDataStudio-TestSetup-<x.y.z>.exe -Version <x.y.z>`.
   It refuses anything that is not a TEST build, installs into a temporary folder with `/DIR`, `/NOICONS` and no tasks,
   uninstalls with the TEST installation's own uninstaller (0.8.0: it also checks that `WoWDataStudio.exe` and `wds.exe`
   are both installed with the same version, that `wds` runs, and that the PATH task adds the TEST bin folder to the
   user's PATH and the uninstall restores the PATH exactly), and records the real installation (Start-menu group with
   file hashes, the real Apps entry, the real program folder, the desktop shortcut) before and after: any difference
   fails the run. Report its last line (`TEST INSTALL OK`) with the release.
