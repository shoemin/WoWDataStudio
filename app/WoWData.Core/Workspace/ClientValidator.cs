using System.Diagnostics;

namespace WoWData.Core.Workspace;

public sealed record ClientCheck(bool Ok, string ClientDir, string Message, string? Version = null);

/// <summary>Checks that a folder is a World of Warcraft 1.12.1 (build 5875) client with the expected archives.</summary>
public static class ClientValidator
{
    public const int ExpectedBuild = 5875;
    public static readonly string[] RequiredArchives = { "model.MPQ", "texture.MPQ", "dbc.MPQ", "patch.MPQ", "interface.MPQ", "misc.MPQ" };

    /// <summary>Accepts the client folder, its Data folder or the WoW.exe file itself.</summary>
    public static ClientCheck Check(string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected))
            return new ClientCheck(false, "", "No folder was selected.");
        var dir = selected.Trim().Trim('"');
        if (File.Exists(dir) && Path.GetFileName(dir).Equals("WoW.exe", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
        if (!Directory.Exists(dir))
            return new ClientCheck(false, dir, $"The folder \"{dir}\" does not exist.");
        if (Path.GetFileName(dir.TrimEnd('\\', '/')).Equals("Data", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\', '/'))!, "WoW.exe")))
            dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'))!;
        dir = Path.GetFullPath(dir);
        var exe = Path.Combine(dir, "WoW.exe");
        if (!File.Exists(exe))
            return new ClientCheck(false, dir,
                "This folder does not contain WoW.exe. Please select the main World of Warcraft folder (the one with WoW.exe and the Data folder).");
        string? version = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            version = $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart} (build {vi.FilePrivatePart})";
            if (vi.FilePrivatePart != ExpectedBuild || vi.FileMajorPart != 1 || vi.FileMinorPart != 12)
                return new ClientCheck(false, dir,
                    $"This is World of Warcraft {version}. WoW Data Studio works with version 1.12.1 (build {ExpectedBuild}) only, because the file formats differ between versions.",
                    version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ClientCheck(false, dir, $"WoW.exe could not be read: {e.Message}");
        }
        var data = Path.Combine(dir, "Data");
        if (!Directory.Exists(data))
            return new ClientCheck(false, dir, "WoW.exe was found, but the Data folder next to it is missing.", version);
        var missing = RequiredArchives.Where(a => !File.Exists(Path.Combine(data, a))).ToList();
        if (missing.Count > 0)
            return new ClientCheck(false, dir, $"The Data folder is missing game archives: {string.Join(", ", missing)}. Is the installation complete?", version);
        return new ClientCheck(true, dir, $"World of Warcraft {version} found.", version);
    }
}
