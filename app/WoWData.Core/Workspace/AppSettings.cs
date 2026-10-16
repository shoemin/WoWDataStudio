using System.Text.Json;
using WoWData.Core.Index;

namespace WoWData.Core.Workspace;

/// <summary>User settings (settings.json in the app folder). The server password is stored encrypted with Windows DPAPI
/// for the current user (<see cref="ServerDbSettings.PasswordProtected"/>); a plain-text password from an older version
/// is migrated on load.</summary>
public sealed class AppSettings
{
    public string? ClientDir { get; set; }
    public ServerDbSettings Server { get; set; } = new();
    public string? ExportDir { get; set; }
    public string? LastImportDir { get; set; }
    public bool ShowedLooseFilesNote { get; set; }

    // ---- maps and terrain (phase 4)
    /// <summary>noggit.exe of our fixed Noggit fork (a separate GPL program). Null = look for the fork's build.</summary>
    public string? NoggitPath { get; set; }
    /// <summary>A client folder for Noggit to read instead of the app's own client view (must not be the real game folder).</summary>
    public string? NoggitClientDir { get; set; }
    /// <summary>The server tools folder (reextract.ps1, swap_in.ps1). Null = WoWDataTool\server found next to the app.</summary>
    public string? ServerToolsDir { get; set; }
    /// <summary>The world server's data folder (maps, vmaps, mmaps). Null = &lt;game folder&gt;\..\Server\data.</summary>
    public string? ServerDataDir { get; set; }

    /// <summary>0.6.1: which ground texture files the terrain previews and the Blender export show: as the game draws
    /// them (the "_s" files when the game's Config.wtf has specular 1 and pixelShaders 1), the base files, or the "_s" files.</summary>
    public Maps.GroundTextureMode GroundTextures { get; set; } = Maps.GroundTextureMode.AsTheGame;

    // ---- data tables (DBC editor, 0.7.0)
    /// <summary>The language edited in localized texts (0 enUS ... 7 esMX); null = the game's (Config.wtf), else enUS.</summary>
    public int? DbcLocale { get; set; }
    /// <summary>The table shown last in the Data tables tab.</summary>
    public string? DbcLastTable { get; set; }

    // ---- server data (0.9.0)
    /// <summary>The content patch the world server runs (0 = 1.2 ... 10 = 1.12); null = WowPatch of mangosd.conf, else 10.</summary>
    public int? ServerPatch { get; set; }
    /// <summary>The range new server rows get their IDs from ("Add" and "Duplicate as new" suggest the first free one).</summary>
    public long ServerCustomIdStart { get; set; } = 900000;
    public long ServerCustomIdEnd { get; set; } = 999999;
    /// <summary>The view shown last in the Server data tab.</summary>
    public string? ServerLastView { get; set; }

    // ---- 3D pop-out windows (0.5.1)
    /// <summary>Per kind ("model", "terrain"): the last size, position, monitor and layout of the pop-out 3D window.</summary>
    public Dictionary<string, PopoutSettings> Popouts { get; set; } = new();

    public PopoutSettings Popout(string kind)
    {
        if (!Popouts.TryGetValue(kind, out var p)) Popouts[kind] = p = new PopoutSettings();
        return p;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.SettingsFile)) ?? new AppSettings();
                if (s.Server.MigratedFromPlainText)
                {
                    // 0.3 stored the password as typed: rewrite the file at once with the encrypted form
                    try { s.Save(paths); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return s;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new AppSettings();
    }

    public void Save(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, paths.SettingsFile, true);
    }

    public string DefaultExportDir => ExportDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WoW Data Studio", "Exports");
}
