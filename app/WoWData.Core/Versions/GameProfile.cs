namespace WoWData.Core.Versions;

/// <summary>What an open session may do. Your own 1.12 game has every capability; another version opened read only
/// has none (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, section 2.1).</summary>
[Flags]
public enum GameCaps
{
    None = 0,
    /// <summary>Change files: the workspace, imports, the Data tables tab.</summary>
    Edit = 1,
    /// <summary>Test in game, the release patch, uninstall.</summary>
    Install = 2,
    /// <summary>The server's world database (names, Server data tab).</summary>
    Server = 4,
    HotReload = 8,
    /// <summary>The spell, class and talent, and quest-chain designers.</summary>
    Designers = 16,
    Noggit = 32,
    UsedInGame = 64,
    /// <summary>Player characters with their character-creation textures.</summary>
    Characters = 128,
    ModelExport = 256,
    All = Edit | Install | Server | HotReload | Designers | Noggit | UsedInGame | Characters | ModelExport,
}

/// <summary>The game an <see cref="Session.AppSession"/> has open: your 1.12 game, or another version read only.</summary>
public sealed record GameProfile(string Id, string Label, string Version, int Build, ClientCopy? Copy, GameCaps Caps)
{
    /// <summary>
    /// Why nothing is ever written into another version's client (the user's rule of 2026-10-21, and CASC): the one
    /// reason the app shows next to (and on hover over) every greyed-out write and the command line prints when it
    /// refuses a command, so both say the same thing.
    /// </summary>
    public const string WriteReason = "Nothing is written into another version's client until the standalone C client exists, and writing its CASC storage is not supported.";

    /// <summary>What another version opened read only can do: look at it (no edit, install, server, hot reload, designer
    /// or Noggit), its "Used in game" without a server, and exports to a folder (Export for Blender).</summary>
    public const GameCaps ReadOnlyCaps = GameCaps.UsedInGame | GameCaps.ModelExport;

    /// <summary>Your own game folder (1.12.1, build 5875): every capability on.</summary>
    public static GameProfile Own112(string clientDir) => new("1.12.1", "World of Warcraft 1.12.1", "1.12.1", 5875, null, GameCaps.All);

    /// <summary>Another version, opened read only: every editing capability off.</summary>
    public static GameProfile ReadOnly(ClientCopy copy) =>
        new(copy.Name, $"{copy.Version}.{copy.Build} {copy.Release} - read only", copy.Version, copy.Build, copy, ReadOnlyCaps);

    /// <summary>True for your own 1.12 game folder.</summary>
    public bool IsOwnClient => Copy == null;

    /// <summary>True when every capability of <paramref name="c"/> is on.</summary>
    public bool Has(GameCaps c) => (Caps & c) == c;

    /// <summary>"1.60.1.70450"</summary>
    public string VersionBuild => $"{Version}.{Build}";

    /// <summary>The app's reason on a greyed-out write: "1.60.1.70450 is open read only. Nothing is written into ...".
    /// Null for your own game (nothing is greyed out).</summary>
    public string? WriteRefusal => IsOwnClient ? null : WriteRefusalFor(VersionBuild);

    /// <summary>The app's reason on a greyed-out write for a version text.</summary>
    public static string WriteRefusalFor(string version) => $"{version} is open read only. {WriteReason}";

    /// <summary>The one line a refused command prints: "1.60.1.70450 is open read only: wds dbc set changes files.
    /// Nothing is written into ...".</summary>
    public string RefusedText(string command) => RefusedTextFor(VersionBuild, command);

    /// <summary>The refusal of a command for a version text (the command line uses it before the session opens).</summary>
    public static string RefusedTextFor(string version, string command) => $"{version} is open read only: wds {command} changes files. {WriteReason}";
}
