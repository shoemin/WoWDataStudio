using System.Reflection;

namespace WoWData.Core;

/// <summary>
/// 0.8.0: the version and build of WoW Data Studio, read from this library (stamped from app\Directory.Build.props). The
/// desktop app (Help > About WoW Data Studio) and the command line (<c>wds version</c>, <c>wds --version</c>) both show
/// <see cref="Display"/>, so the two front ends can never report different numbers.
/// </summary>
public static class StudioInfo
{
    public const string ProductName = "WoW Data Studio";

    /// <summary>The file name of the command-line program (installed next to WoWDataStudio.exe).</summary>
    public const string CliName = "wds";

    /// <summary>The file name of the desktop program.</summary>
    public const string GuiExe = "WoWDataStudio.exe";

    private static readonly string Informational =
        typeof(StudioInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StudioInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The version, for example "0.8.0".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>The build stamp, for example "20261011.1530" for a release or "dev" for a developer build.</summary>
    public static string Build
    {
        get
        {
            int plus = Informational.IndexOf('+');
            return plus < 0 ? "dev" : Informational[(plus + 1)..];
        }
    }

    /// <summary>"0.8.0 (build dev)".</summary>
    public static string Display => $"{Version} (build {Build})";
}
