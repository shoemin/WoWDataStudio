namespace WoWData.Core.Features;

/// <summary>Where a feature can be used.</summary>
public enum FeatureFront
{
    /// <summary>In the desktop app and on the command line (the normal case).</summary>
    Both,
    /// <summary>Only in the desktop app, with a reason (looking at things on screen, the editor's unsaved state...).</summary>
    GuiOnly,
    /// <summary>Only on the command line (scripting helpers).</summary>
    CliOnly,
}

/// <summary>One user-visible feature: its id, the tab or area, the app's label (menu paths with " > "), the command
/// (the command path after "wds") and, for a one-sided feature, why.</summary>
public sealed record Feature(string Id, string Area, string Gui, string Cli, FeatureFront Front = FeatureFront.Both, string Reason = "")
{
    public bool HasGui => Front != FeatureFront.CliOnly;
    public bool HasCli => Front != FeatureFront.GuiOnly;
}

/// <summary>
/// 0.8.0: the machine-readable list of every user-facing feature of WoW Data Studio and where it lives in the desktop app
/// (GUI) and on the command line (CLI, <c>wds</c>). The two front ends must never drift apart (CONTRIBUTING.md):
/// <c>FeatureParityTests</c> fails when an entry's command does not exist, when a command maps to no entry, or when a
/// label is not in the app; <c>ManualTests</c> fails when the coverage table of docs\manual\command-line.md differs from
/// this list. A new feature of the app gets its entry here, its command, and its line in command-line.md in the same
/// change.
/// </summary>
public static class FeatureRegistry
{
    public static readonly IReadOnlyList<Feature> All = new List<Feature>
    {
        // ---- setup, settings, server database, index
        new("setup.client", "Setup", "Change World of Warcraft folder...", "setup client"),
        new("setup.check", "Setup", "Load game data", "setup check"),
        new("setup.status", "Setup", "", "setup status", FeatureFront.CliOnly, "a scripting helper: the version, the folders, the index and the workspace in one report (the app shows them in its window title, top bar and tabs)"),
        new("settings.list", "Setup", "File > Settings...", "settings list"),
        new("settings.get", "Setup", "File > Settings...", "settings get"),
        new("settings.set", "Setup", "File > Settings...", "settings set"),
        new("settings.unset", "Setup", "File > Settings...", "settings unset"),
        new("serverdb.test", "Setup", "Test connection", "serverdb test"),
        new("serverdb.defaults", "Setup", "Use the defaults (127.0.0.1:3306, root/root, mangos)", "serverdb defaults"),
        new("serverdb.reload", "Setup", "File > Reload names from the server database", "serverdb reload"),
        new("index.build", "Setup", "File > Rebuild the index", "index build"),
        new("index.status", "Setup", "Names from server database: connected", "index status"),

        // ---- find (the Browse tab)
        new("find.search", "Find", "Search models, textures, creatures, items...", "find search"),
        new("find.only-changes", "Find", "Show only my changes", "find search"),
        new("find.list", "Find", "Show only this type", "find list"),
        new("find.info", "Find", "WHERE IT COMES FROM", "find info"),
        new("find.used", "Find", "Used in game", "find used"),
        new("find.where", "Find", "TEXTURES IT USES", "find where"),
        new("find.files", "Find", "", "find files", FeatureFront.CliOnly, "a scripting helper: list archive paths by wildcard, with every archive that has them"),
        new("find.extract", "Find", "", "find extract", FeatureFront.CliOnly, "a scripting helper: copy a game file out of the archives as it is (the app exports PNG and glTF instead)"),

        // ---- textures
        new("textures.info", "Textures", "Mip level", "textures info"),
        new("textures.export", "Textures", "1. Export texture as PNG...", "textures export"),
        new("textures.export-all", "Textures", "TEXTURES IT USES", "textures export-all"),
        new("textures.import", "Textures", "2. Import edited PNG...", "textures import"),
        new("textures.edit-both", "Textures", "Edit both (recommended)", "textures import"),
        new("textures.keep-shine", "Textures", "Keep the original shine map (recommended)", "textures import"),
        new("textures.keep-black", "Textures", "Keep black areas black (recommended)", "textures import"),
        new("textures.compare", "Textures", "Compare original vs. edited", "textures compare"),

        // ---- models and Blender
        new("models.info", "Models", "Model statistics", "models info"),
        new("models.export", "Models", "Tools > Export for Blender...", "models export"),
        new("models.open", "Models", "Open in Blender", "models open"),
        new("models.import", "Models", "File > Import edited model...", "models import"),
        new("models.validate", "Models", "Import edited model...", "models validate"),
        new("models.compare", "Models", "Compare original vs. edited model", "models compare"),
        new("models.revert", "Models", "Revert the model", "changes revert"),
        new("models.addon", "Models", "Tools > Install the Blender add-on...", "models addon"),
        new("models.render", "Models", "Save image...", "models render"),
        // 0.10.0: skeleton and animation round trip
        new("models.export-skeleton", "Models", "Skeleton and animations (for editing)", "models export"),
        new("models.import-skeleton", "Models", "Skeleton and animations", "models import"),
        new("models.effects", "Models", "Particles and ribbons", "models import"),
        new("models.anims", "Models", "Animations and what the game plays them for", "models anims"),
        new("models.skeleton", "Models", "Skeleton, attachment points and events", "models skeleton"),
        new("models.animation-preview", "Models", "Loop", "models render"),
        new("models.attachment-points", "Models", "Attachment points", "models skeleton"),
        new("models.compare-animations", "Models", "Same animation on both", "models compare"),
        new("models.compare-skins", "Models", "Tools > Compare two skins of this model (3D)", "", FeatureFront.GuiOnly, "viewing: two live 3D views side by side (render each skin with \"wds models render --display\")"),
        new("models.viewer", "Models", "Displays using this model", "", FeatureFront.GuiOnly, "viewing: the interactive 3D viewer (camera, animations, geosets, background); \"wds models info\" lists the displays and skins"),
        new("view.popout", "Models", "Tools > Open the 3D view in a new window", "", FeatureFront.GuiOnly, "viewing: pop-out 3D windows (follow / pin, full screen)"),

        // ---- maps and terrain
        new("maps.list", "Maps", "Tools > Maps and terrain", "maps list"),
        new("maps.zones", "Maps", "ZONES", "maps zones"),
        new("maps.tiles", "Maps", "MAPS", "maps tiles"),
        new("maps.specular", "Maps", "Specular ground (_s textures)", "settings set"),
        new("maps.minimap", "Maps", "Maps", "maps minimap"),
        new("maps.tile", "Maps", "On this tile", "maps tile"),
        new("maps.breakdown", "Maps", "Tools > Break down this tile...", "maps breakdown"),
        new("maps.render", "Maps", "On its own", "maps render"),
        new("maps.noggit", "Maps", "Tools > Edit in Noggit...", "maps noggit"),
        new("maps.import-noggit", "Maps", "Tools > Import map changes...", "maps import-noggit"),
        new("maps.compare", "Maps", "Height difference", "maps compare"),
        new("maps.test", "Maps", "Test map in game (loose files)", "maps test-in-game"),
        new("maps.revert", "Maps", "Revert the tile", "maps revert"),
        new("maps.export-terrain", "Maps", "Tools > Open terrain in Blender...", "maps export-terrain"),
        new("maps.import-terrain", "Maps", "Tools > Import edited terrain...", "maps import-terrain"),
        new("maps.export-building", "Maps", "Open building in Blender", "maps export-building"),
        new("maps.show-in-browse", "Maps", "Show in Browse", "", FeatureFront.GuiOnly, "navigation between tabs (on the command line: \"wds find info\" with the file)"),
        new("maps.view3d", "Maps", "Orbit", "", FeatureFront.GuiOnly, "viewing: the interactive 3D view of a tile (orbit, fly, neighbours, doodads, water); \"wds maps render\" draws 2D previews"),
        new("server.status", "Server maps", "Tools > Update server maps...", "server-maps status"),
        new("server.extract", "Server maps", "Extract into staging", "server-maps extract"),
        new("server.preview", "Server maps", "Show what would change", "server-maps install"),
        new("server.install", "Server maps", "Install into the server", "server-maps install"),
        new("server.undo", "Server maps", "Undo server update", "server-maps undo"),

        // ---- data tables (DBC)
        new("dbc.tables", "Data tables", "Tools > Data tables (DBC editor)", "dbc tables"),
        new("dbc.changed", "Data tables", "Only tables I changed", "dbc tables"),
        new("dbc.info", "Data tables", "Server also needs this file", "dbc info"),
        new("dbc.rows", "Data tables", "Search all text...", "dbc rows"),
        new("dbc.get", "Data tables", "Go to ID", "dbc get"),
        new("dbc.set", "Data tables", "Values", "dbc set"),
        new("dbc.add", "Data tables", "Add row", "dbc add"),
        new("dbc.duplicate", "Data tables", "Duplicate as new", "dbc duplicate"),
        new("dbc.delete", "Data tables", "Delete rows", "dbc delete"),
        new("dbc.bulk", "Data tables", "Bulk edit...", "dbc bulk"),
        new("dbc.export", "Data tables", "Export all rows...", "dbc export"),
        new("dbc.import", "Data tables", "Apply import", "dbc import"),
        new("dbc.used-by", "Data tables", "Used by", "dbc used-by"),
        new("dbc.diff", "Data tables", "Compare with game", "dbc diff"),
        new("dbc.save", "Data tables", "Save to my workspace", "dbc set"),
        new("dbc.copy-to-server", "Data tables", "Tools > Copy tables to the server...", "dbc copy-to-server"),
        new("dbc.undo-server-copy", "Data tables", "Undo last copy to server", "dbc undo-server-copy"),
        new("dbc.undo", "Data tables", "Discard changes", "", FeatureFront.GuiOnly, "the editor's unsaved state (Undo, Redo, Discard): every \"wds dbc\" edit is saved at once; use --dry-run to look first and \"wds changes revert\" to undo"),
        new("dbc.show-in-3d", "Data tables", "Show in 3D", "", FeatureFront.GuiOnly, "navigation to the 3D viewer (\"wds models render\" draws a display)"),

        // ---- server data (0.9.0): the server's world database
        new("sdata.status", "Server data", "Tools > Server data (world database)", "server status"),
        new("sdata.views", "Server data", "Server data", "server views"),
        new("sdata.find", "Server data", "Search by name or ID...", "server find"),
        new("sdata.show", "Server data", "Values", "server show"),
        new("sdata.get", "Server data", "Values", "server get"),
        new("sdata.set", "Server data", "Save to my workspace", "server set"),
        new("sdata.add", "Server data", "Add new", "server add"),
        new("sdata.add-here", "Server data", "Add row here", "server add"),
        new("sdata.duplicate", "Server data", "Duplicate as new", "server duplicate"),
        new("sdata.delete", "Server data", "Delete", "server delete"),
        new("sdata.bulk", "Server data", "Bulk edit...", "server bulk"),
        new("sdata.used-by", "Server data", "Used by", "server used-by"),
        new("sdata.changes", "Server data", "Compare with the server", "server changes"),
        new("sdata.check", "Server data", "Check client and server...", "server check"),
        new("sdata.match-server", "Server data", "Make the server match the game", "server match"),
        new("sdata.match-client", "Server data", "Make the game match the server", "server match"),
        new("sdata.export-sql", "Server data", "Export SQL patch...", "server export-sql"),
        new("sdata.apply", "Server data", "Apply to server...", "server apply"),
        new("sdata.undo-apply", "Server data", "Undo last apply", "server undo-apply"),
        new("sdata.applies", "Server data", "Applied changes log", "server applies"),
        new("sdata.export", "Server data", "Export the list...", "server export"),
        new("sdata.import", "Server data", "Import...", "server import"),
        new("sdata.wdb", "Server data", "Clear the game's cache (WDB)...", "server clear-wdb"),
        new("sdata.patch", "Server data", "Content patch of the server", "settings set"),
        new("sdata.custom-ids", "Server data", "Custom ID range", "settings set"),
        new("sdata.spell-ids", "Server data", "Spell ID range", "settings set"),
        new("sdata.dbc-link", "Server data", "Edit it in the Server data tab", "server show"),
        new("sdata.undo", "Server data", "Discard changes", "", FeatureFront.GuiOnly, "the editor's unsaved state (Undo, Redo, Discard changes): every \"wds server\" edit is saved at once; use --dry-run to look first and \"wds changes revert\" to undo"),
        new("sdata.show-in-browse", "Server data", "Show in Browse", "", FeatureFront.GuiOnly, "navigation to the Browse tab's 3D viewer and to the Data tables tab (\"wds find info item:25\" and \"wds dbc get\" on the command line)"),

        // ---- 0.12.0: the spell designer
        new("spells.show", "Spell designer", "Tools > Spell designer", "spells show"),
        new("spells.find", "Spell designer", "Find a spell", "spells find"),
        new("spells.fields", "Spell designer", "Basics", "spells fields"),
        new("spells.new", "Spell designer", "New spell", "spells new"),
        new("spells.clone", "Spell designer", "Clone this spell", "spells clone"),
        new("spells.set", "Spell designer", "Save to my workspace", "spells set"),
        new("spells.tooltip", "Spell designer", "Tooltip preview", "spells tooltip"),
        new("spells.check", "Spell designer", "Checks (1.12 client and server)", "spells check"),
        new("spells.explain", "Spell designer", "What this effect does", "spells explain"),
        new("spells.icons", "Spell designer", "Find icons", "spells icons"),
        new("spells.visual", "Spell designer", "Pick visual...", "spells visual"),
        new("spells.visual-duplicate", "Spell designer", "Duplicate visual (tint and size)", "spells visual"),
        new("spells.trainer", "Spell designer", "Add to trainer", "spells learn"),
        new("spells.skill-line", "Spell designer", "Add to skill line", "spells learn"),
        new("spells.sync", "Spell designer", "Sync with the server", "spells sync"),
        new("spells.renumber", "Spell designer", "Change ID", "spells renumber"),
        new("spells.renumber-all", "Spell designer", "Fix all spell IDs", "spells renumber"),
        new("spells.visual-3d", "Spell designer", "Show in 3D", "", FeatureFront.GuiOnly, "viewing: the effect models in the designer's 3D view (\"wds models render\" draws a model to PNG)"),
        new("spells.undo", "Spell designer", "Discard changes", "", FeatureFront.GuiOnly, "the editor's unsaved state: every \"wds spells\" command saves at once; use --dry-run to look first and \"wds changes revert\" to undo"),

        // ---- 0.12.0: the class and talent designer
        new("talents.trees", "Classes and talents", "Talent trees", "talents trees"),
        new("talents.show", "Classes and talents", "Trees of this class", "talents show"),
        new("talents.add-tree", "Classes and talents", "Add tree...", "talents add-tree"),
        new("talents.set-tree", "Classes and talents", "Apply tree changes", "talents set-tree"),
        new("talents.remove-tree", "Classes and talents", "Remove tree", "talents remove-tree"),
        new("talents.add", "Classes and talents", "Add talent here", "talents add"),
        new("talents.rank-spells", "Classes and talents", "Make rank spells", "talents add"),
        new("talents.set", "Classes and talents", "Apply talent changes", "talents set"),
        new("talents.remove", "Classes and talents", "Remove talent", "talents remove"),
        new("talents.check", "Classes and talents", "Check the tree", "talents check"),
        new("classes.list", "Classes and talents", "Tools > Classes and talents", "classes list"),
        new("classes.show", "Classes and talents", "Spells by level", "classes show"),
        new("classes.assign", "Classes and talents", "Assign level", "classes assign"),
        new("classes.mask", "Classes and talents", "Class masks", "classes mask"),
        new("classes.new", "Classes and talents", "New class (experimental)", "classes new"),
        new("classes.engine-report", "Classes and talents", "What a new class still needs", "classes engine-report"),
        new("classes.grid", "Classes and talents", "The selected place", "", FeatureFront.GuiOnly, "viewing: the talent grid with icons, arrows and the tree's background (\"wds talents show\" prints the grid as text)"),
        new("classes.undo", "Classes and talents", "Discard changes", "", FeatureFront.GuiOnly, "the editor's unsaved state: every \"wds talents\" and \"wds classes\" command saves at once; use --dry-run to look first and \"wds changes revert\" to undo"),

        // ---- 0.13.0: the quest-chain designer
        new("quests.show", "Quest chains", "Tools > Quest chains", "quests show"),
        new("quests.find", "Quest chains", "Find a quest", "quests find"),
        new("quests.fields", "Quest chains", "Who can take it", "quests fields"),
        new("quests.chain", "Quest chains", "Open chain", "quests chain"),
        new("quests.check", "Quest chains", "Check the chain", "quests check"),
        new("quests.new", "Quest chains", "New quest", "quests new"),
        new("quests.add-after", "Quest chains", "Add quest after", "quests new"),
        new("quests.add-before", "Quest chains", "Add quest before", "quests new"),
        new("quests.set", "Quest chains", "Save to my workspace", "quests set"),
        new("quests.races-classes", "Quest chains", "Classes", "quests set"),
        new("quests.branch", "Quest chains", "Add branch", "quests branch"),
        new("quests.join", "Quest chains", "Join", "quests join"),
        new("quests.giver", "Quest chains", "Add giver", "quests giver"),
        new("quests.ender", "Quest chains", "Add ender", "quests ender"),
        new("quests.objective", "Quest chains", "Objectives in words", "quests objective"),
        new("quests.talk", "Quest chains", "Add speak-to objective", "quests talk"),
        new("quests.explore", "Quest chains", "Add explore objective", "quests explore"),
        new("quests.reward", "Quest chains", "Set reward spell", "quests reward"),
        new("quests.drops", "Quest chains", "Add drop source", "quests drops"),
        new("quests.loot-editor", "Quest chains", "Open in the loot editor", "quests drops"),
        new("quests.npcs", "Quest chains", "Find NPC", "quests npcs"),
        new("quests.npc", "Quest chains", "Make the quest giver", "quests npc"),
        new("quests.place", "Quest chains", "Place here", "quests place"),
        new("quests.duplicate", "Quest chains", "Duplicate for the other faction", "quests duplicate"),
        new("quests.template", "Quest chains", "Make the chain", "quests template"),
        new("quests.preview", "Quest chains", "Text preview ($N, $C, $R, $G)", "quests preview"),
        new("quests.categories", "Quest chains", "New quest log category", "quests categories"),
        new("quests.export", "Quest chains", "Export chain...", "quests export"),
        new("quests.import", "Quest chains", "Import quests...", "quests import"),
        new("quests.delete", "Quest chains", "Delete quest", "quests delete"),
        new("quests.test", "Quest chains", "Test in game", "quests test-in-game"),
        new("quests.apply", "Quest chains", "Apply to server...", "server apply"),
        new("quests.spell-link", "Quest chains", "Open in the Spell designer", "spells show"),
        new("quests.flowchart", "Quest chains", "Flowchart", "", FeatureFront.GuiOnly, "viewing: the chain drawn as a flowchart with lanes and arrows (\"wds quests chain\" prints it as text, JSON or a Mermaid chart)"),
        new("quests.map", "Quest chains", "Show on map", "", FeatureFront.GuiOnly, "viewing: the NPCs' spawns as pins on the Maps tab (\"wds quests show\" lists the givers, \"wds quests test-in-game\" their positions)"),
        new("quests.pick", "Quest chains", "Pick on the map", "", FeatureFront.GuiOnly, "viewing: a position picked by clicking the map (on the command line: --map, --x, --y)"),
        new("quests.npc-preview", "Quest chains", "Preview the NPC", "", FeatureFront.GuiOnly, "viewing: the NPC's model in 3D (\"wds models render\" draws a model to PNG)"),
        new("quests.undo", "Quest chains", "Discard changes", "", FeatureFront.GuiOnly, "the editor's unsaved state: every \"wds quests\" command saves at once; use --dry-run to look first and \"wds changes revert\" to undo"),

        // ---- my changes, testing and releasing
        new("changes.list", "My changes", "My changes", "changes list"),
        new("changes.sets", "My changes", "Revert this import", "changes sets"),
        new("changes.diff", "My changes", "Compare", "changes diff"),
        new("changes.revert", "My changes", "Revert my change", "changes revert"),
        new("changes.revert-set", "My changes", "Revert this import", "changes revert-set"),
        new("changes.install", "My changes", "Test all in game (install as loose files)", "changes install"),
        new("changes.install-one", "My changes", "3. Test in game (install as loose files)", "changes install"),
        new("changes.remove", "My changes", "Remove all from game", "changes remove"),
        new("changes.build-patch", "My changes", "File > Build release patch (patch-3.MPQ)...", "changes build-patch"),
        new("changes.patch-state", "My changes", "Also install it into the game:", "changes patch-state"),
        new("changes.uninstall-patch", "My changes", "Uninstall release patch (patch-3.MPQ)", "changes uninstall-patch"),
        new("changes.folder", "My changes", "File > Open my workspace folder", "changes folder"),
        // 0.11.0: hot reload of changed files in the running game
        new("changes.reload", "My changes", "Reload in running game", "changes reload"),
        new("settings.hot-reload", "Setup", "Hot reload in the running game", "settings set"),
        new("changes.show", "My changes", "Show", "", FeatureFront.GuiOnly, "navigation: jumps to the file in its tab (\"wds find info\" describes it)"),

        // ---- other versions (0.14.0, experimental): read other World of Warcraft clients, never change them
        new("versions.folder", "Other versions", "Folder of other game versions", "settings set"),
        new("versions.blizzard-only", "Other versions", "Blizzard archives only", "settings set"),
        new("versions.listfile", "Other versions", "Choose listfile...", "settings set"),
        new("versions.find", "Other versions", "Find clients", "versions find"),
        new("versions.info", "Other versions", "Archives", "versions info"),
        new("versions.files", "Other versions", "Files matching", "versions files"),
        new("versions.extract", "Other versions", "Extract selected...", "versions extract"),
        new("versions.preview", "Other versions", "Preview", "", FeatureFront.GuiOnly, "viewing: a picture of the selected texture (\"wds versions extract\" copies the file out)"),
        new("versions.tables", "Other versions", "Tables of this version", "versions tables"),
        new("versions.rows", "Other versions", "Find in rows", "versions rows"),
        new("versions.compare", "Other versions", "Compare across versions", "versions compare"),
        new("versions.convert-map", "Other versions", "Convert map...", "versions convert-map"),

        // ---- help and the command line itself
        new("help.manual", "Help", "Help > User manual", "manual"),
        new("help.about", "Help", "Help > About WoW Data Studio", "version"),
        new("help.cli", "Help", "Help > The command line (wds)", "manual"),
        new("help.how", "Help", "Help > How it works", "", FeatureFront.GuiOnly, "viewing: a help panel of the window (\"wds --help\" and \"wds GROUP COMMAND --help\" on the command line)"),
        new("help.loose-files", "Help", "Help > About the loose-file mode (LOOSE_FILES_PATCH.md)", "", FeatureFront.GuiOnly, "viewing: opens a document (it is in the docs folder of the installation)"),
        new("cli.commands", "Command line", "", "commands", FeatureFront.CliOnly, "a scripting helper: the command tree (with --json, for tools)"),
        new("cli.features", "Command line", "", "features", FeatureFront.CliOnly, "a scripting helper: this list of features (GUI and CLI)"),
    };

    /// <summary>The features of one command (several app labels can lead to one command).</summary>
    public static IEnumerable<Feature> ForCommand(string cli) => All.Where(f => string.Equals(f.Cli, cli, StringComparison.OrdinalIgnoreCase));
}
