using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Session;

namespace WoWData.Core.Versions;

/// <summary>
/// M1: textures of another version opened read only, for the texture viewer and "wds textures info / export
/// --version": read by name or #FileDataID, decoded, saved as PNG. Never written anywhere near the client; no
/// .blp.json is written next to the PNG (that note is what the 1.12 import reads to know which texture a PNG
/// replaces, and a texture of another version must not replace one of your game by accident).
/// </summary>
public static class OtherTextures
{
    /// <summary>The texture's BLP (throws <see cref="ClientFileException"/> when encrypted or not on this computer,
    /// <see cref="BlpException"/> when it is not a BLP); null when the client has no such file.</summary>
    public static BlpFile? Read(AppSession s, string path)
    {
        var data = s.ReadOther(path);
        return data == null ? null : BlpFile.Parse(data);
    }

    /// <summary>Saves the full-size texture as a PNG (alpha kept). Returns the BLP.</summary>
    public static BlpFile ExportPng(AppSession s, string path, string png)
    {
        var blp = Read(s, path) ?? throw new FileNotFoundException($"{path} is not a file of {s.Profile?.VersionBuild ?? "this client"}.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(png));
        if (dir != null) Directory.CreateDirectory(dir);
        Png.Save(blp.Decode(0), png, true);
        return blp;
    }

    /// <summary>A file name for the PNG of a texture: its name without the folder, or "&lt;FileDataID&gt;" for a file known
    /// only by its number.</summary>
    public static string PngName(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
        return (n.StartsWith('#') ? n[1..] : n) + ".png";
    }
}
