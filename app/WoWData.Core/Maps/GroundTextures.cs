using System.Text.RegularExpressions;
using WoWData.Core.Imaging;
using WoWData.Core.Mpq;

namespace WoWData.Core.Maps;

/// <summary>Which ground texture file the app shows / exports for terrain: the game's rule (read from the game's
/// Config.wtf), always the base file, or always the specular "_s" file.</summary>
public enum GroundTextureMode { AsTheGame, Base, Specular }

/// <summary>
/// The game's terrain specular settings, read from &lt;game&gt;\WTF\Config.wtf (read only). The 1.12 client loads the
/// "_s" variants of the ground textures when both <c>specular</c> and <c>pixelShaders</c> are 1 (and the graphics card
/// has pixel shaders): CMap_Load @006941f0 sets g_TerrainSpecularShaderOk from the world flags 0x8000000 (specular)
/// and 0x10000000 (pixelShaders), both registered with the default "0" (WorldCVar_OnChange_specular /
/// _pixelShaders refuse "1" on hardware without pixel shaders).
/// </summary>
public sealed record TerrainSpecularSettings(bool Specular, bool PixelShaders, bool FileFound, string Source)
{
    private static readonly Regex SetLine = new(@"^\s*SET\s+(\w+)\s+""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the game loads the "_s" textures for the ground.</summary>
    public bool UsesSpecularTextures => Specular && PixelShaders;

    public static TerrainSpecularSettings Unknown => new(false, false, false, "");

    public static string ConfigPath(string clientDir) => Path.Combine(clientDir, "WTF", "Config.wtf");

    /// <summary>Read the two settings from the game's Config.wtf (missing file or lines = the game's defaults, 0).</summary>
    public static TerrainSpecularSettings Read(string? clientDir)
    {
        if (string.IsNullOrEmpty(clientDir)) return Unknown;
        var path = ConfigPath(clientDir);
        try
        {
            if (!File.Exists(path)) return new(false, false, false, path);
            return Parse(File.ReadAllLines(path), path);
        }
        catch (IOException) { return new(false, false, false, path); }
        catch (UnauthorizedAccessException) { return new(false, false, false, path); }
    }

    public static TerrainSpecularSettings Parse(IEnumerable<string> lines, string source = "Config.wtf")
    {
        bool spec = false, ps = false;
        foreach (var l in lines)
        {
            var m = SetLine.Match(l);
            if (!m.Success) continue;
            bool on = m.Groups[2].Value.Trim() != "0" && m.Groups[2].Value.Trim().Length > 0;
            if (m.Groups[1].Value.Equals("specular", StringComparison.OrdinalIgnoreCase)) spec = on;
            else if (m.Groups[1].Value.Equals("pixelShaders", StringComparison.OrdinalIgnoreCase)) ps = on;
        }
        return new(spec, ps, true, source);
    }

    /// <summary>"specular 1, pixelShaders 1" in words, for the UI.</summary>
    public string Describe() => !FileFound
        ? "the game's settings file (WTF\\Config.wtf) was not found, so the game's defaults apply (specular 0, pixelShaders 0)"
        : $"your game's settings (WTF\\Config.wtf): specular {(Specular ? 1 : 0)}, pixelShaders {(PixelShaders ? 1 : 0)}";
}

/// <summary>A ground texture as the game knows it: the file the map's MTEX names (the base) and its specular variant
/// "&lt;name&gt;_s.blp", when the game has one.</summary>
public sealed record GroundTexturePair(string Base, string? Specular, bool BaseExists)
{
    public bool HasSpecular => Specular != null;

    /// <summary>The file the game draws with these settings (the "_s" file when specular is on, else the base).</summary>
    public string Drawn(bool specularOn) => specularOn ? Specular ?? GroundTextures.SpecularName(Base) ?? Base : Base;

    /// <summary>The other file of the pair (null when there is none).</summary>
    public string? Partner(string path) =>
        string.Equals(ClientData.ResolveName(path), Base, StringComparison.OrdinalIgnoreCase) ? Specular :
        Specular != null && string.Equals(ClientData.ResolveName(path), Specular, StringComparison.OrdinalIgnoreCase) ? Base : null;

    public bool IsSpecular(string path) => Specular != null && string.Equals(ClientData.ResolveName(path), Specular, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Ground textures and their specular "_s" variants. The rule is the client's (CMapArea_LoadTextureNames @006c20e0): for
/// every name of the tile's MTEX chunk it loads the file itself, or, when terrain specular shading is on, the same name
/// with "_s" put in front of the extension (everything from the last '.' is replaced by "_s" + the original extension:
/// Tileset\Durotar\DurotarDirt.blp -> Tileset\Durotar\DurotarDirt_s.blp). There is no fallback: with specular on, the
/// base file is not read at all. The "_s" file has the same colours (RGB) and keeps the specular (shine) strength in its
/// alpha channel; the base file is usually opaque DXT1.
/// </summary>
public static class GroundTextures
{
    public const string SpecularSuffix = "_s";

    /// <summary>The client's name of the specular variant, or null for a name without an extension.</summary>
    public static string? SpecularName(string path)
    {
        var p = ClientData.ResolveName(path);
        int dot = p.LastIndexOf('.');
        if (dot < 0 || dot < p.LastIndexOf('\\')) return null;
        return p[..dot] + SpecularSuffix + p[dot..];
    }

    /// <summary>The base name of a "..._s.blp" file (no check that it exists), or null.</summary>
    public static string? BaseNameOfSpecular(string path)
    {
        var p = ClientData.ResolveName(path);
        int dot = p.LastIndexOf('.');
        if (dot < 2 || dot < p.LastIndexOf('\\')) return null;
        var stem = p[..dot];
        return stem.EndsWith(SpecularSuffix, StringComparison.OrdinalIgnoreCase) ? stem[..^SpecularSuffix.Length] + p[dot..] : null;
    }

    /// <summary>True for the folder the 1.12 maps take their ground textures from.</summary>
    public static bool InTilesetFolder(string path) => ClientData.ResolveName(path).StartsWith("Tileset\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pair of a ground texture, from either of its two files. <paramref name="exists"/> says whether the game
    /// (or your workspace) has a file; <paramref name="isGround"/> whether a map names it as a ground texture (null: the
    /// Tileset folder is taken as the sign). Returns null for a texture that is not a ground texture.</summary>
    public static GroundTexturePair? PairOf(string path, Func<string, bool> exists, Func<string, bool>? isGround = null)
    {
        var p = ClientData.ResolveName(path);
        if (!p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) return null;
        bool Ground(string x) => isGround?.Invoke(x) ?? InTilesetFolder(x);
        var baseOfS = BaseNameOfSpecular(p);
        if (baseOfS != null && Ground(baseOfS) && (exists(baseOfS) || isGround != null))
            return new GroundTexturePair(baseOfS, p, exists(baseOfS));
        if (!Ground(p)) return null;
        var s = SpecularName(p);
        return new GroundTexturePair(p, s != null && exists(s) ? s : null, exists(p));
    }

    /// <summary>The file the app should show for a ground texture under a mode.</summary>
    public static bool SpecularOn(GroundTextureMode mode, TerrainSpecularSettings game) => mode switch
    {
        GroundTextureMode.Base => false,
        GroundTextureMode.Specular => true,
        _ => game.UsesSpecularTextures,
    };

    /// <summary>One sentence: which file the game draws, and when.</summary>
    public static string WhichIsDrawn(GroundTexturePair pair, TerrainSpecularSettings game)
    {
        var b = Path.GetFileName(pair.Base);
        if (pair.Specular == null)
            return $"The game draws {b} when terrain specular is off. With terrain specular on (specular 1 and pixelShaders 1) it looks for {Path.GetFileName(SpecularName(pair.Base) ?? pair.Base)}, which the game does not have.";
        var s = Path.GetFileName(pair.Specular);
        var now = game.UsesSpecularTextures ? s : b;
        return $"The game draws {s} when terrain specular is on (Config.wtf: specular 1 and pixelShaders 1), and {b} when it is off. " +
               $"With {game.Describe()}, it draws {now}.";
    }

    /// <summary>What the alpha channel of a "_s" file means, in plain words.</summary>
    public const string SpecularAlphaNote =
        "In a _s file the alpha channel is not transparency: it is the shine (specular) strength of the ground, from 0 (matt) to 255 (shiny). " +
        "Image editors show it as see-through; that is expected. Keep it as it is unless you mean to change how the ground shines.";

    /// <summary>"Edit both": the partner file made from your edited colours. RGB comes from <paramref name="editedRgb"/>
    /// (resized to the partner's size when they differ); alpha is the partner's own (the specular map of a "_s" file, or
    /// the base file's alpha), or your image's alpha when <paramref name="keepPartnerAlpha"/> is false.</summary>
    public static RgbaImage MergeIntoPartner(RgbaImage editedRgb, RgbaImage partnerOriginal, bool keepPartnerAlpha = true)
    {
        var src = editedRgb.Width == partnerOriginal.Width && editedRgb.Height == partnerOriginal.Height
            ? editedRgb : ImageOps.Resize(editedRgb, partnerOriginal.Width, partnerOriginal.Height);
        var res = partnerOriginal.Clone();
        var o = res.Pixels;
        var s = src.Pixels;
        for (int i = 0; i < o.Length; i += 4)
        {
            o[i] = s[i];
            o[i + 1] = s[i + 1];
            o[i + 2] = s[i + 2];
            if (!keepPartnerAlpha) o[i + 3] = s[i + 3];
        }
        return res;
    }

    /// <summary>True when the alpha channels of two images of the same size differ anywhere.</summary>
    public static bool AlphaDiffers(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return true;
        for (int i = 3; i < a.Pixels.Length; i += 4) if (a.Pixels[i] != b.Pixels[i]) return true;
        return false;
    }
}
