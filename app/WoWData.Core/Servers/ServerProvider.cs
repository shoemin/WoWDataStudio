using WoWData.Core.Versions;

namespace WoWData.Core.Servers;

/// <summary>
/// The world server of the open game version: what the app's server-backed parts read (the names in Browse, the
/// server rows of "Used in game", the Server data tab, quests and loot). Each game version has its own provider
/// (<see cref="ServerProviders.For"/>): your 1.12 game has the VMaNGOS / CMaNGOS world database of the settings;
/// another version has none yet (<see cref="NoServerProvider"/>), so its server-backed parts say "no server connected"
/// and never show made-up data. A server for that version is plugged in later by registering a provider.
/// </summary>
public interface IServerProvider
{
    /// <summary>What kind of server this is ("VMaNGOS / CMaNGOS world database", "none").</summary>
    string Kind { get; }
    /// <summary>True when the server's data is there (the names were loaded from it).</summary>
    bool IsConnected { get; }
    /// <summary>True when this version can have a server at all (false: no provider exists for it yet).</summary>
    bool IsAvailable { get; }
    /// <summary>One line for the status button: "Names from server database: connected", "No server connected for 1.60.1.70450".</summary>
    string StatusText { get; }
    /// <summary>Why it is (not) connected, for the tooltip and the notes of "Used in game".</summary>
    string Detail { get; }
}

/// <summary>Your 1.12 game: the world database of the settings (read through the session as before; this only
/// describes it, so the 1.12 behaviour is the same).</summary>
public sealed class Mangos112ServerProvider : IServerProvider
{
    private readonly Func<bool> _connected;
    private readonly Func<string> _message;

    public Mangos112ServerProvider(Func<bool> connected, Func<string> message)
    {
        _connected = connected;
        _message = message;
    }

    public string Kind => "VMaNGOS / CMaNGOS world database";
    public bool IsConnected => _connected();
    public bool IsAvailable => true;
    public string StatusText => IsConnected ? "Names from server database: connected" : "Names from server database: not connected";
    public string Detail => _message();
}

/// <summary>A version without a server provider: nothing from a server is shown, and every server-backed part says so.</summary>
public sealed class NoServerProvider : IServerProvider
{
    public NoServerProvider(string version) => Version = version;

    /// <summary>"1.60.1.70450"</summary>
    public string Version { get; }
    public string Kind => "none";
    public bool IsConnected => false;
    public bool IsAvailable => false;
    public string StatusText => $"No server connected for {Version}";
    public string Detail => NoServerText(Version);

    /// <summary>The sentence every server-backed part shows for a version without a server.</summary>
    public static string NoServerText(string version) =>
        $"No server is connected for {version}: names from a world database, spawns, loot, vendors, quests and the Server data tab need a world server of that version, " +
        "and none can be connected yet (the app is ready for one: a server provider for that version). Nothing is shown in their place.";
}

/// <summary>
/// The server provider of each game version. Your 1.12 game always has its world database; other versions get the
/// provider registered for them (none yet), else <see cref="NoServerProvider"/>.
/// </summary>
public static class ServerProviders
{
    private static readonly List<Func<GameProfile, IServerProvider?>> Registered = new();
    private static readonly object Gate = new();

    /// <summary>Adds a provider for other versions: it returns a provider for the profiles it serves, else null.</summary>
    public static void Register(Func<GameProfile, IServerProvider?> factory)
    {
        lock (Gate) Registered.Add(factory);
    }

    /// <summary>Removes a provider added by <see cref="Register"/> (tests).</summary>
    public static void Unregister(Func<GameProfile, IServerProvider?> factory)
    {
        lock (Gate) Registered.Remove(factory);
    }

    /// <summary>The provider of another version opened read only: a registered one, else "no server connected".</summary>
    public static IServerProvider ForOther(GameProfile profile)
    {
        List<Func<GameProfile, IServerProvider?>> list;
        lock (Gate) list = Registered.ToList();
        foreach (var f in list)
            if (f(profile) is { } p) return p;
        return new NoServerProvider(profile.VersionBuild);
    }
}
