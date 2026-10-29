using WoWData.Core.Maps;

namespace WoWData.Core.Workspace;

/// <summary>One setting: its key (as the command line names it), its label in the Settings window, its meaning.</summary>
public sealed record SettingInfo(string Key, string Label, string Help, bool Secret = false);

/// <summary>
/// 0.8.0: the settings of the app by name, shared by the Settings window and <c>wds settings ...</c>: reading, setting and
/// clearing one value (with the same checks as the window), and the checks of the maps-and-terrain folders. Secrets (the
/// server password) are never returned: <see cref="Get"/> says only whether one is set.
/// </summary>
public static class SettingsCatalog
{
    public static readonly IReadOnlyList<SettingInfo> All = new List<SettingInfo>
    {
        new("client", "World of Warcraft folder", "the game folder (with WoW.exe and Data), version 1.12.1 (build 5875)"),
        new("export-dir", "Exports folder", "where exports go by default (Documents\\WoW Data Studio\\Exports when empty)"),
        new("ground-textures", "Specular ground (_s textures)", "which ground files the terrain previews and the Blender export show: as-the-game, base or specular"),
        new("dbc-locale", "Language", "the language of localized texts in the data tables: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (empty: the game's)"),
        new("noggit", "Noggit program (noggit.exe)", "our fixed Noggit fork's noggit.exe (empty: the fork's build next to the app)"),
        new("noggit-client", "Client copy for Noggit (optional)", "a copy of the game folder for Noggit (never the game folder itself; empty: the app's client view)"),
        new("other-clients", "Folder of other game versions", "experimental: a folder with copies of other World of Warcraft clients (any version) whose data the Other versions tab and \"wds versions\" read, never change (empty: none)"),
        new("other-clients.blizzard-only", "Blizzard archives only", "true or false: other versions are read without patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ)"),
        new("other-clients.listfile", "Listfile for modern clients", "experimental, optional: a listfile you chose (FileDataID;path lines) that names the files of modern (CASC) clients; never bundled or downloaded by the app (empty: files without a known name are shown by number)"),
        new("other-clients.dbd", "WoWDBDefs folder (optional)", "experimental, optional: a folder with WoWDBDefs definition files (.dbd, or a copy of the WoWDBDefs repository with its definitions folder) whose column names the tables of modern (CASC) clients show; read at run time, never bundled or downloaded by the app (empty: our own measured names, else col_N)"),
        new("server-tools", "Server tools folder", "the folder with reextract.ps1 and swap_in.ps1 (empty: found next to the app or the game folder)"),
        new("server-data", "Server data folder", "the world server's data folder with maps, vmaps, mmaps and 5875\\dbc (empty: Server\\data next to the game folder)"),
        new("server.enabled", "Use the server database for names", "true or false"),
        new("server.host", "Server", "the computer the database runs on (127.0.0.1 is this computer)"),
        new("server.port", "Port", "the MySQL port (3306)"),
        new("server.user", "User", "the database user name"),
        new("server.password", "Password", "that user's password: stored encrypted for your Windows user, never printed", Secret: true),
        new("server.database", "World database", "the world database (the one with creature_template), usually mangos"),
        new("server.patch", "Content patch of the server", "the content patch the world server runs: 0 (1.2) to 10 (1.12); empty: WowPatch of mangosd.conf, else 10"),
        new("server.custom-ids", "Custom ID range", "the IDs new server rows get (Add, Duplicate as new): from-to, for example 900000-999999 (spells have their own range: spell-ids)"),
        new("spell-ids", "Spell ID range", "the IDs new spells get (the spell, talent, class and quest designers, Add in spell_template): from-to within 1-40000, for example 35000-39999 (VMaNGOS casts no spell above 40000, the 1.12 game knows none above 65535)"),
        new("hot-reload", "Hot reload in the running game", "on or off, for the game folder (stored there as wowhotreload.txt): the running game reloads changed files without a restart; needs the project's client (wowcode.dll from the HR1 update on) and is read when the game starts"),
    };

    public static SettingInfo? Info(string key) => All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>The value of a setting as text ("" when empty). A secret gives "(set)" or "". <paramref name="clientDir"/>: the game
    /// folder of a per-folder setting (hot-reload) when it is not the one in the settings (wds --client).</summary>
    public static string Get(AppSettings s, string key, string? clientDir = null) => (Info(key)?.Key ?? throw new ArgumentException($"unknown setting \"{key}\"")) switch
    {
        "hot-reload" => (clientDir ?? s.ClientDir) is { Length: > 0 } hc && Directory.Exists(hc) && HotReload.HotReloadFlag.IsEnabled(hc) ? "on" : "off",
        "client" => s.ClientDir ?? "",
        "export-dir" => s.ExportDir ?? "",
        "ground-textures" => s.GroundTextures switch { GroundTextureMode.Base => "base", GroundTextureMode.Specular => "specular", _ => "as-the-game" },
        "dbc-locale" => s.DbcLocale is { } l && l >= 0 && l < Dbc.DbcTable.LocaleNames.Length ? Dbc.DbcTable.LocaleNames[l] : "",
        "noggit" => s.NoggitPath ?? "",
        "other-clients" => s.OtherClientsDir ?? "",
        "other-clients.blizzard-only" => s.OtherClientsBlizzardOnly ? "true" : "false",
        "other-clients.listfile" => s.OtherClientsListfile ?? "",
        "other-clients.dbd" => s.OtherClientsDbd ?? "",
        "noggit-client" => s.NoggitClientDir ?? "",
        "server-tools" => s.ServerToolsDir ?? "",
        "server-data" => s.ServerDataDir ?? "",
        "server.enabled" => s.Server.Enabled ? "true" : "false",
        "server.host" => s.Server.Host,
        "server.port" => s.Server.Port.ToString(),
        "server.user" => s.Server.User,
        "server.password" => string.IsNullOrEmpty(s.Server.Password) ? "" : "(set)",
        "server.database" => s.Server.Database,
        "server.patch" => s.ServerPatch?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        "server.custom-ids" => $"{s.ServerCustomIdStart}-{s.ServerCustomIdEnd}",
        "spell-ids" => $"{s.SpellIdStart}-{s.SpellIdEnd}",
        _ => "",
    };

    /// <summary>Set a value (with the window's checks). Throws <see cref="ArgumentException"/> with the reason when the
    /// value is not allowed. hot-reload writes (on) or deletes (off) wowhotreload.txt in the game folder.</summary>
    public static void Set(AppSettings s, string key, string value, string? clientDir = null)
    {
        var k = Info(key)?.Key ?? throw new ArgumentException($"unknown setting \"{key}\". The settings: {string.Join(", ", All.Select(a => a.Key))}.");
        switch (k)
        {
            case "hot-reload":
            {
                bool on = value.Trim().ToLowerInvariant() switch
                {
                    "on" or "true" or "yes" or "1" => true,
                    "off" or "false" or "no" or "0" => false,
                    _ => throw new ArgumentException("hot-reload is on or off."),
                };
                var dir = clientDir ?? s.ClientDir;
                var check = ClientValidator.Check(dir);
                if (!check.Ok) throw new ArgumentException("Set the World of Warcraft folder first: hot reload is switched on per game folder. " + check.Message);
                HotReload.HotReloadFlag.SetEnabled(check.ClientDir, on);
                break;
            }
            case "client":
                var c = ClientValidator.Check(value);
                if (!c.Ok) throw new ArgumentException(c.Message);
                s.ClientDir = c.ClientDir;
                break;
            case "export-dir": s.ExportDir = N(value) == null ? null : Path.GetFullPath(value.Trim()); break;
            case "ground-textures":
                s.GroundTextures = value.Trim().ToLowerInvariant() switch
                {
                    "as-the-game" or "game" or "auto" => GroundTextureMode.AsTheGame,
                    "base" => GroundTextureMode.Base,
                    "specular" or "_s" => GroundTextureMode.Specular,
                    _ => throw new ArgumentException("ground-textures is as-the-game, base or specular."),
                };
                break;
            case "dbc-locale":
                if (N(value) == null || value.Trim().Equals("game", StringComparison.OrdinalIgnoreCase)) s.DbcLocale = null;
                else
                {
                    int i = Array.FindIndex(Dbc.DbcTable.LocaleNames, n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (i < 0) throw new ArgumentException($"dbc-locale is one of {string.Join(", ", Dbc.DbcTable.LocaleNames)} (or \"game\").");
                    s.DbcLocale = i;
                }
                break;
            case "noggit": s.NoggitPath = N(value); break;
            case "other-clients":
                if (N(value) is { } oc && !Directory.Exists(oc)) throw new ArgumentException($"the folder \"{oc}\" does not exist.");
                s.OtherClientsDir = N(value);
                break;
            case "other-clients.blizzard-only":
                s.OtherClientsBlizzardOnly = value.Trim().ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" or "1" => true,
                    "false" or "off" or "no" or "0" => false,
                    _ => throw new ArgumentException("other-clients.blizzard-only is true or false."),
                };
                break;
            case "other-clients.listfile":
                if (N(value) is { } lf && !File.Exists(lf)) throw new ArgumentException($"the file \"{lf}\" does not exist.");
                s.OtherClientsListfile = N(value);
                break;
            case "other-clients.dbd":
                if (Db2.DbdFolder.Check(N(value), out _) is { } bad) throw new ArgumentException(bad);
                s.OtherClientsDbd = N(value);
                break;
            case "noggit-client":
                if (N(value) != null && s.ClientDir != null && NoggitService.IsSameOrInside(Path.GetFullPath(value.Trim()), Path.GetFullPath(s.ClientDir)))
                    throw new ArgumentException("Noggit must never read or write the game folder itself: choose a copy of it (or leave this empty for the app's own client view).");
                s.NoggitClientDir = N(value);
                break;
            case "server-tools": s.ServerToolsDir = N(value); break;
            case "server-data": s.ServerDataDir = N(value); break;
            case "server.enabled":
                s.Server.Enabled = value.Trim().ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" or "on" => true,
                    "false" or "no" or "0" or "off" => false,
                    _ => throw new ArgumentException("server.enabled is true or false."),
                };
                break;
            case "server.host": s.Server.Host = N(value) ?? throw new ArgumentException("server.host cannot be empty."); break;
            case "server.port":
                s.Server.Port = int.TryParse(value, out var p) && p > 0 && p < 65536 ? p : throw new ArgumentException("server.port is a number from 1 to 65535.");
                break;
            case "server.user": s.Server.User = value.Trim(); break;
            case "server.password": s.Server.Password = value; break;
            case "server.database": s.Server.Database = N(value) ?? throw new ArgumentException("server.database cannot be empty."); break;
            case "server.patch":
                if (N(value) == null || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) s.ServerPatch = null;
                else s.ServerPatch = int.TryParse(value.Trim(), out var sp) && sp >= 0 && sp <= 10 ? sp : throw new ArgumentException("server.patch is a content patch from 0 (1.2) to 10 (1.12), or empty for mangosd.conf's WowPatch.");
                break;
            case "server.custom-ids":
            {
                var m = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(\d+)\s*-\s*(\d+)$");
                if (!m.Success || !long.TryParse(m.Groups[1].Value, out var a) || !long.TryParse(m.Groups[2].Value, out var b) || a < 1 || b < a || b > 4294967295)
                    throw new ArgumentException("server.custom-ids is a range from-to of whole numbers, for example 900000-999999.");
                s.ServerCustomIdStart = a;
                s.ServerCustomIdEnd = b;
                break;
            }
            case "spell-ids":
            {
                var m = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(\d+)\s*-\s*(\d+)$");
                if (!m.Success || !long.TryParse(m.Groups[1].Value, out var a) || !long.TryParse(m.Groups[2].Value, out var b) || a < 1 || b < a)
                    throw new ArgumentException("spell-ids is a range from-to of whole numbers, for example 35000-39999.");
                if (b > ServerData.SpellIdLimits.MaxCastable)
                    throw new ArgumentException($"spell-ids ends at {ServerData.SpellIdLimits.MaxCastable} at most: the VMaNGOS server never casts a spell with a higher ID (MAX_SPELL_ID), so such a spell could not be learned or used.");
                s.SpellIdStart = a;
                s.SpellIdEnd = b;
                break;
            }
        }
    }

    /// <summary>Back to the default (empty = automatic for folders; hot-reload: off).</summary>
    public static void Unset(AppSettings s, string key, string? clientDir = null)
    {
        var d = new AppSettings();
        var k = Info(key)?.Key ?? throw new ArgumentException($"unknown setting \"{key}\"");
        switch (k)
        {
            case "hot-reload":
                if ((clientDir ?? s.ClientDir) is { Length: > 0 } hc && Directory.Exists(hc)) HotReload.HotReloadFlag.SetEnabled(hc, false);
                break;
            case "client": s.ClientDir = null; break;
            case "export-dir": s.ExportDir = null; break;
            case "ground-textures": s.GroundTextures = GroundTextureMode.AsTheGame; break;
            case "dbc-locale": s.DbcLocale = null; break;
            case "noggit": s.NoggitPath = null; break;
            case "other-clients": s.OtherClientsDir = null; break;
            case "other-clients.blizzard-only": s.OtherClientsBlizzardOnly = false; break;
            case "other-clients.listfile": s.OtherClientsListfile = null; break;
            case "other-clients.dbd": s.OtherClientsDbd = null; break;
            case "noggit-client": s.NoggitClientDir = null; break;
            case "server-tools": s.ServerToolsDir = null; break;
            case "server-data": s.ServerDataDir = null; break;
            case "server.enabled": s.Server.Enabled = d.Server.Enabled; break;
            case "server.host": s.Server.Host = d.Server.Host; break;
            case "server.port": s.Server.Port = d.Server.Port; break;
            case "server.user": s.Server.User = d.Server.User; break;
            case "server.password": s.Server.Password = d.Server.Password; break;
            case "server.database": s.Server.Database = d.Server.Database; break;
            case "server.patch": s.ServerPatch = null; break;
            case "server.custom-ids":
                s.ServerCustomIdStart = d.ServerCustomIdStart;
                s.ServerCustomIdEnd = d.ServerCustomIdEnd;
                break;
            case "spell-ids":
                s.SpellIdStart = d.SpellIdStart;
                s.SpellIdEnd = d.SpellIdEnd;
                break;
        }
    }

    /// <summary>"Use the defaults (127.0.0.1:3306, root/root, mangos)" of the server database.</summary>
    public static void UseServerDefaults(AppSettings s)
    {
        var d = new Index.ServerDbSettings();
        s.Server.Host = d.Host;
        s.Server.Port = d.Port;
        s.Server.User = d.User;
        s.Server.Password = d.Password;
        s.Server.Database = d.Database;
    }

    /// <summary>The lines under the maps-and-terrain fields of the Settings window: what the app found.</summary>
    public static (string Noggit, string ServerTools, string ServerData) MapsChecks(AppSettings s, string? clientDir)
    {
        var exe = NoggitService.FindNoggit(s, clientDir);
        var noggit = exe != null ? (string.IsNullOrWhiteSpace(s.NoggitPath) ? $"Found the fork's build: {exe}" : "Found.") : string.IsNullOrWhiteSpace(s.NoggitPath) ? "Not found next to the app: browse to noggit.exe." : "This file does not exist.";
        var tools = ServerMapsService.FindTools(s, clientDir);
        var toolsText = tools != null ? (string.IsNullOrWhiteSpace(s.ServerToolsDir) ? $"Found: {tools}" : "Found.") : "Not found: the folder must contain reextract.ps1 and swap_in.ps1.";
        if (string.IsNullOrWhiteSpace(clientDir) && string.IsNullOrWhiteSpace(s.ServerDataDir)) return (noggit, toolsText, "Set the World of Warcraft folder first (the default is Server\\data next to it).");
        var data = ServerMapsService.DefaultServerData(s, clientDir!);
        var dataText = Directory.Exists(Path.Combine(data, "maps")) ? (string.IsNullOrWhiteSpace(s.ServerDataDir) ? $"Found: {data}" : "Found.") : $"No maps folder in {data}.";
        return (noggit, toolsText, dataText);
    }
}
