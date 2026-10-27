using System.Text.RegularExpressions;
using WoWData.Core.Casc;

namespace WoWData.Core.Versions;

/// <summary>Where a name comes from and whether the file can be read, for one FileDataID of a modern client.</summary>
public sealed record ModernFileInfo(uint FileDataId, string Path, NameSource Source, CascFileState State)
{
    public bool Readable => State == CascFileState.Local;

    /// <summary>"local", "encrypted", "not downloaded", "unknown".</summary>
    public string StateText => StateWord(State);

    public static string StateWord(CascFileState s) => s switch
    {
        CascFileState.Local => "local", CascFileState.Encrypted => "encrypted", CascFileState.NotLocal => "not downloaded", _ => "unknown",
    };

    public static CascFileState ParseState(string? s) => s switch
    {
        "local" => CascFileState.Local, "encrypted" => CascFileState.Encrypted, "not downloaded" => CascFileState.NotLocal, _ => CascFileState.Unknown,
    };
}

/// <summary>Names of a modern client's files as the session knows them (the index's <c>fdid</c> table).</summary>
public interface IModernNames
{
    ModernFileInfo? ByFileDataId(uint fdid);
    ModernFileInfo? ByPath(string path);
    /// <summary>Named files matching a wildcard (* and ?), sorted by path; at most <paramref name="limit"/>.</summary>
    IReadOnlyList<ModernFileInfo> Glob(string pattern, int limit = int.MaxValue);
    int Count { get; }
}

/// <summary>
/// A modern client's files for a session opened read only (M1): by <c>#FileDataID</c>, by a name the session's name
/// table knows (<see cref="IModernNames"/>: the client's hashes, your 1.12 names through Classic Era, your listfile),
/// or by a name the root hashes. <c>#123456.blp</c> (the index's name of a file without a known name) reads FileDataID
/// 123456.
/// </summary>
public sealed class ModernClientFiles : IClientFiles
{
    private static readonly Regex IdRe = new(@"^#?(\d+)(\.[A-Za-z0-9]+)?$", RegexOptions.CultureInvariant);

    public CascStorage Storage { get; }
    public IModernNames? Names { get; set; }

    public ModernClientFiles(CascStorage storage, IModernNames? names = null)
    {
        Storage = storage;
        Names = names;
    }

    public bool CanList => Names != null && Names.Count > 0;
    public int FileCount => Storage.Root.Count;

    /// <summary>The FileDataID of "#id", "#id.ext", a known name or a name the root hashes; null when unknown.</summary>
    public uint? IdOf(string pathOrId)
    {
        var t = pathOrId.Trim();
        if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) t = t[5..];
        var m = IdRe.Match(t);
        if (m.Success && (t.StartsWith('#') || m.Groups[2].Length == 0) && uint.TryParse(m.Groups[1].Value, out var id)) return id;
        var p = t.Replace('/', '\\');
        return Names?.ByPath(p)?.FileDataId ?? Storage.FileDataIdOf(p);
    }

    public byte[]? Read(string pathOrId)
    {
        var id = IdOf(pathOrId);
        if (id == null) return null;
        try { return Storage.ReadFileDataId(id.Value); }
        catch (CascMissingException e) { throw new ClientFileException($"{pathOrId} (#{id}) is not on this computer: {e.Message}", e); }
        catch (CascEncryptedException e) { throw new ClientFileException($"{pathOrId} (#{id}) is encrypted and cannot be read{(e.KeyName != null ? $" (key {e.KeyName})" : "")}.", e); }
    }

    public bool Exists(string pathOrId) => IdOf(pathOrId) is { } id && Storage.Root.Contains(id);

    public IReadOnlyList<ClientFileHit> Glob(string pattern) =>
        Names == null ? Array.Empty<ClientFileHit>() : Names.Glob(pattern).Select(f => new ClientFileHit(f.Path, $"#{f.FileDataId} {f.StateText}")).ToList();

    public string? SourceOf(string pathOrId)
    {
        var id = IdOf(pathOrId);
        if (id == null) return null;
        var st = Storage.State(id.Value);
        return st == CascFileState.Unknown ? null : $"#{id} {ModernFileInfo.StateWord(st)}";
    }

    /// <summary>What is known about a file: its FileDataID, name, name source and state; null when unknown.</summary>
    public ModernFileInfo? Info(string pathOrId)
    {
        var id = IdOf(pathOrId);
        if (id == null || !Storage.Root.Contains(id.Value)) return null;
        var named = Names?.ByFileDataId(id.Value);
        return named ?? new ModernFileInfo(id.Value, $"#{id}", NameSource.Number, Storage.State(id.Value));
    }

    public void Dispose() => Storage.Dispose();
}
