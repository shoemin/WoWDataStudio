namespace WoWData.Core.Casc;

/// <summary>Whether a file of a CASC storage can be read from this computer.</summary>
public enum CascFileState
{
    /// <summary>Not in the root file, or its content key has no encoding entry.</summary>
    Unknown,
    /// <summary>In the local archives and not encrypted: <see cref="CascStorage.ReadFileDataId"/> returns it.</summary>
    Local,
    /// <summary>Known to the build but not downloaded (the launcher streams it from the CDN when the game needs it).</summary>
    NotLocal,
    /// <summary>In the local archives, but at least one BLTE chunk is encrypted (the encoding file's ESpec says so).
    /// Encrypted parts are never decrypted; see <see cref="CascStorage.ReadFileDataId"/>'s <c>zeroFillEncrypted</c>.</summary>
    Encrypted,
}

/// <summary>The content of a file is not in the local archives (not downloaded).</summary>
public sealed class CascMissingException : Exception
{
    public CascMissingException(string message) : base(message) { }
}

/// <summary>The file (or a chunk of it) is encrypted. The reader never decrypts: no keys are used or shipped.</summary>
public sealed class CascEncryptedException : Exception
{
    /// <summary>The key name of the first encrypted chunk (16 hex digits, the usual byte-reversed form), or null.</summary>
    public string? KeyName { get; }

    public CascEncryptedException(string message, string? keyName = null) : base(message) { KeyName = keyName; }
}

/// <summary>Choices made when a storage is opened.</summary>
public sealed record CascOpenOptions
{
    /// <summary>Take the high-resolution variant of a texture (root content flag 0x1) when a FileDataID has both.
    /// Default false: the standard variant (see <see cref="CascRoot"/>).</summary>
    public bool PreferHighResTextures { get; init; }
}
