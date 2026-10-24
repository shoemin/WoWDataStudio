using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WoWData.Core.Blp;
using WoWData.Core.Mpq;

namespace WoWData.Core.Workspace;

public sealed class WorkspaceException : Exception
{
    public WorkspaceException(string message) : base(message) { }
}

/// <summary>Where the original of an edited file came from (recorded when the edit is saved).</summary>
public sealed class OriginInfo
{
    [JsonPropertyName("archive")] public string? Archive { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("levels")] public int Levels { get; set; }
    [JsonPropertyName("is_new")] public bool IsNew { get; set; }
    /// <summary>Models that use the file (for "this texture is shared with ..." and the Phase 3c import review).</summary>
    [JsonPropertyName("models")] public List<string> Models { get; set; } = new();
    /// <summary>Displays / variants / sections that use the file, in words.</summary>
    [JsonPropertyName("used_by")] public List<string> UsedBy { get; set; } = new();
}

public sealed class InstallRecord
{
    [JsonPropertyName("dest")] public string Dest { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("installed")] public DateTime Installed { get; set; }
    [JsonPropertyName("backup")] public string? Backup { get; set; }
}

public sealed class PackRecord
{
    [JsonPropertyName("archive")] public string Archive { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("built")] public DateTime Built { get; set; }
    [JsonPropertyName("installed_to")] public string? InstalledTo { get; set; }
}

/// <summary>One edited file of the workspace.</summary>
public sealed class ChangeRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "texture";
    [JsonPropertyName("original")] public OriginInfo Original { get; set; } = new();
    [JsonPropertyName("workspace_sha256")] public string WorkspaceSha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("source_file")] public string? SourceFile { get; set; }
    [JsonPropertyName("source_sha256")] public string? SourceSha256 { get; set; }
    [JsonPropertyName("edited")] public DateTime Edited { get; set; }
    [JsonPropertyName("change_set")] public string ChangeSet { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("installed")] public InstallRecord? Installed { get; set; }
    [JsonPropertyName("packed")] public PackRecord? Packed { get; set; }
    /// <summary>0.9.0: for server edits (kind "server"): when they were applied to the server's database, and which version.</summary>
    [JsonPropertyName("server_applied")] public ServerAppliedRecord? ServerApplied { get; set; }

    /// <summary>0.14.0: the version of the file just before this record's change set replaced it, when that was an earlier
    /// edit of yours (null: the game's original, or <see cref="PriorDropped"/>). "Revert this import" puts it back.</summary>
    [JsonPropertyName("prior")] public PriorVersion? Prior { get; set; }
    /// <summary>0.14.0: there was an earlier edit before this change set, but its copy is no longer kept (the history of a
    /// file is limited to <see cref="Workspace.MaxPriorVersions"/> versions), so "Revert this import" refuses this file.</summary>
    [JsonPropertyName("prior_dropped")] public bool PriorDropped { get; set; }

    /// <summary>0.9.0: a server edit (ServerData\&lt;database&gt;\&lt;table&gt;.json), not a game file: never installed or packed.</summary>
    [JsonIgnore] public bool IsServerEdit => Kind == "server";
}

/// <summary>0.14.0: an earlier edit of a file that a later change set replaced: its record (with its own
/// <see cref="ChangeRecord.Prior"/>, so the versions form a chain), the change set it came from, and the copy of its bytes
/// under &lt;client-id&gt;\history (relative to the client's folder).</summary>
public sealed class PriorVersion
{
    [JsonPropertyName("record")] public ChangeRecord Record { get; set; } = new();
    [JsonPropertyName("set_label")] public string SetLabel { get; set; } = "";
    [JsonPropertyName("set_created")] public DateTime SetCreated { get; set; }
    [JsonPropertyName("stash")] public string Stash { get; set; } = "";
}

/// <summary>0.14.0: what "Revert this import" will do with one file: go back to the earlier edit from
/// <see cref="EarlierLabel"/>, or (null) to the game's original.</summary>
public sealed record RevertStep(string Path, string? EarlierLabel);

/// <summary>0.14.0: what "Revert this import" will do (the app's question, <c>wds changes revert-set --dry-run</c>), and
/// why it cannot (<see cref="Problems"/>: a newer change set changed one of its files again, or an earlier version is no
/// longer kept). With problems, nothing is reverted.</summary>
public sealed record RevertPlan(ChangeSet Set, IReadOnlyList<RevertStep> Steps, IReadOnlyList<string> Problems)
{
    public bool CanRevert => Problems.Count == 0;
    public int ToEarlier => Steps.Count(s => s.EarlierLabel != null);
    public int ToOriginal => Steps.Count(s => s.EarlierLabel == null);

    /// <summary>What happens, in words (one sentence).</summary>
    public string Text => ToEarlier == 0
        ? "Each file goes back to the game's original."
        : $"Each file goes back to the version it had before this import: {ToEarlier} to your earlier edit, {ToOriginal} to the game's original.";

    /// <summary>The refusal when <see cref="CanRevert"/> is false.</summary>
    public string RefusalText =>
        $"\"{Set.Label}\" was not reverted; nothing was changed. {Problems.Count} file{(Problems.Count == 1 ? "" : "s")} cannot go back to the version {(Problems.Count == 1 ? "it" : "they")} had before this import:\n" +
        string.Join("\n", Problems.Take(10)) + (Problems.Count > 10 ? $"\n... and {Problems.Count - 10} more" : "") +
        "\n\nRevert the newer import first (then this one), or use Revert on single files (that puts the game's original back).";
}

/// <summary>0.9.0: "Apply to server" of a server edit: when, where (host:port and database, never a password) and the
/// workspace version that was applied.</summary>
public sealed class ServerAppliedRecord
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>A group of files changed together (one import). "Revert this import" reverts the whole set.</summary>
public sealed class ChangeSet
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("created")] public DateTime Created { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
}

public sealed class PatchBuild
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("built")] public DateTime Built { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("installed_to")] public string? InstalledTo { get; set; }
    [JsonPropertyName("backup")] public string? Backup { get; set; }
    /// <summary>0.6.1: SHA-256 and size of the archive as built (proves an installed patch is this build).</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>0.6.1: the backup of the patch that was in the game before WoW Data Studio first installed one (null: there
    /// was none); "Uninstall release patch" puts it back.</summary>
    [JsonPropertyName("original_backup")] public string? OriginalBackup { get; set; }
    [JsonPropertyName("original_recorded")] public bool OriginalRecorded { get; set; }
    /// <summary>0.6.1: when "Uninstall release patch" took this build out of the game.</summary>
    [JsonPropertyName("uninstalled")] public DateTime? Uninstalled { get; set; }
}

/// <summary>Who made the patch archive in the game's Data folder.</summary>
public enum PatchOwner { None, Ours, OursUnverified, Foreign }

/// <summary>The state of Data\patch-3.MPQ for "My changes" and the build / uninstall dialogs.</summary>
public sealed record PatchState(string Path, PatchOwner Owner, PatchBuild? Build, long Size, DateTime? Modified, string? RestoreBackup)
{
    public bool Exists => Owner != PatchOwner.None;
    public bool CanUninstall => Owner is PatchOwner.Ours or PatchOwner.OursUnverified;
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Text => Owner switch
    {
        PatchOwner.None => $"No {FileName} is installed in the game.",
        PatchOwner.Ours => $"A release patch built by WoW Data Studio is installed: {Build!.Files.Count} file{(Build.Files.Count == 1 ? "" : "s")}, built {Build.Built:yyyy-MM-dd HH:mm}. " + RestoreText,
        PatchOwner.OursUnverified => $"A {FileName} is installed that WoW Data Studio probably built ({Build!.Files.Count} file{(Build.Files.Count == 1 ? "" : "s")}, built {Build.Built:yyyy-MM-dd HH:mm}), " +
                                     "but an older version of the app did not record its checksum, so this cannot be proven. " + RestoreText,
        _ => $"A {FileName} is installed that WoW Data Studio did not create ({Size / 1024.0 / 1024.0:0.0} MB, {Modified:yyyy-MM-dd HH:mm}). The app never removes it without asking you.",
    };

    private string RestoreText => RestoreBackup != null
        ? $"Uninstalling it puts back the {FileName} that was there before (backed up in {RestoreBackup})."
        : $"There was no {FileName} before, so uninstalling simply removes it.";
}

public sealed class ChangeManifest
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("client_dir")] public string ClientDir { get; set; } = "";
    /// <summary>Keyed by the upper-case archive path.</summary>
    [JsonPropertyName("files")] public Dictionary<string, ChangeRecord> Files { get; set; } = new();
    [JsonPropertyName("change_sets")] public Dictionary<string, ChangeSet> ChangeSets { get; set; } = new();
    [JsonPropertyName("patch_builds")] public List<PatchBuild> PatchBuilds { get; set; } = new();
    /// <summary>Loose files installed whose workspace copy was reverted but that could not be removed yet.</summary>
    [JsonPropertyName("orphan_installs")] public Dictionary<string, InstallRecord> OrphanInstalls { get; set; } = new();
}

public enum ChangeStatus { InWorkspace, InstalledLoose, InstalledOutdated, Packed, PackedOutdated }

public sealed record ChangeView(ChangeRecord Record, string WorkspaceFile, bool InWorkspace, bool InstalledLoose, bool InstalledCurrent, bool Packed, bool PackedCurrent)
{
    public string StatusText
    {
        get
        {
            var parts = new List<string> { "In workspace" };
            if (Record.IsServerEdit)
            {
                var a = Record.ServerApplied;
                parts.Add(a == null ? "not applied to the server yet" : a.Sha256 == Record.WorkspaceSha256 ? $"applied to the server {a.Time:yyyy-MM-dd HH:mm}" : "applied to the server (older version - apply again)");
                return string.Join(" · ", parts);
            }
            if (InstalledLoose) parts.Add(InstalledCurrent ? "installed as loose file" : "installed loose (older version - reinstall)");
            if (Packed) parts.Add(PackedCurrent ? "in patch-3.MPQ" : "in patch-3.MPQ (older version - rebuild)");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record DeliveryAction(string Path, string Source, string Dest, string Action);

/// <summary>
/// The user's edits for one client folder: files under &lt;client-id&gt;\workspace\&lt;archive path&gt; plus changes.json with,
/// per file, the original's archive, checksum, format and users (models/variants), the change set it belongs to, and
/// where it was delivered (loose install, patch archive). Delivery rules (from wowdata/deploy.py):
///  * the stock archives are never opened for writing, replaced or deleted;
///  * loose install writes only Client\Data\&lt;archive path&gt;, records each file's SHA-256, and refuses to replace a file
///    it did not install unless forced (the replaced file is then backed up); remove deletes only files it installed
///    and that are unchanged since;
///  * the patch archive is only ever Data\patch-&lt;3..9|A..Z&gt;.MPQ (default patch-3.MPQ); an existing one is backed up.
/// </summary>
public sealed class Workspace
{
    private static readonly Regex PatchName = new(@"^patch-[3-9a-z]\.mpq$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ClientPaths Paths { get; }
    public ChangeManifest Manifest { get; private set; }
    private readonly object _lock = new();

    public event EventHandler? Changed;

    public Workspace(ClientPaths paths)
    {
        Paths = paths;
        Manifest = Load();
        Stamp();
    }

    // 0.8.0: the app and the command line (wds) can work on the same workspace at the same time. Before every change and
    // every listing, changes.json is read again when another program changed it since (its time and size), so neither
    // overwrites the other's records with an old copy.
    private DateTime _stampTime;
    private long _stampLength = -2;

    private void Stamp()
    {
        var fi = new FileInfo(Paths.ChangesFile);
        _stampTime = fi.Exists ? fi.LastWriteTimeUtc : default;
        _stampLength = fi.Exists ? fi.Length : -1;
    }

    /// <summary>Reads changes.json again when another program (the app or wds) changed it. Call under the lock.</summary>
    private void Sync()
    {
        var fi = new FileInfo(Paths.ChangesFile);
        if ((fi.Exists ? fi.LastWriteTimeUtc : default) == _stampTime && (fi.Exists ? fi.Length : -1) == _stampLength) return;
        Manifest = Load();
        Stamp();
        ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Reads changes.json again if another program changed it (the app calls it when it shows My changes).</summary>
    public void Refresh()
    {
        lock (_lock) Sync();
    }

    private ChangeManifest Load()
    {
        if (File.Exists(Paths.ChangesFile))
        {
            try
            {
                var m = JsonSerializer.Deserialize<ChangeManifest>(File.ReadAllText(Paths.ChangesFile));
                if (m != null) return m;
            }
            catch (JsonException)
            {
                var bad = Paths.ChangesFile + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(Paths.ChangesFile, bad, true);
            }
        }
        return new ChangeManifest { ClientDir = Paths.ClientDir };
    }

    private void Save()
    {
        Directory.CreateDirectory(Paths.Dir);
        var tmp = Paths.ChangesFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Manifest, Json));
        File.Move(tmp, Paths.ChangesFile, true);
        Stamp();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The validator's errors for an M2 (empty when it is fine).</summary>
    public static List<string> ModelProblems(byte[] data)
    {
        try
        {
            var m = M2.M2Model.Load(data);
            return M2.M2Validator.Errors(M2.M2Validator.Validate(m)).Select(i => i.ToString()).ToList();
        }
        catch (M2.M2Exception e)
        {
            return new List<string> { e.Message };
        }
    }

    /// <summary>The 1.12 map validator's errors for an ADT / WDT / WDL (empty when it is fine or not a map file).</summary>
    public static List<string> MapProblems(byte[] data, string path)
    {
        if (Maps.MapValidator.KindOf(path) == null) return new List<string>();
        var r = Maps.MapValidator.ValidateBytes(data, System.IO.Path.GetFileName(path));
        return r.Errors.Select(f => f.Plain).ToList();
    }

    /// <summary>0.7.0: the problems of a DBC table that would make the game misread it (empty when fine or not a DBC).</summary>
    public static List<string> DbcProblems(byte[] data, string path)
    {
        var table = Dbc.DbcService.TableOf(path);
        return table == null ? new List<string>() : Dbc.DbcValidation.FileProblems(data, table);
    }

    public static string Key(string archivePath) => ClientData.ResolveName(archivePath).ToUpperInvariant();

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    public string WorkspaceFile(string archivePath) => System.IO.Path.Combine(Paths.WorkspaceDir, ClientData.ResolveName(archivePath).Replace('\\', System.IO.Path.DirectorySeparatorChar));

    private string DataFile(string archivePath) => System.IO.Path.Combine(Paths.DataDir, ClientData.ResolveName(archivePath).Replace('\\', System.IO.Path.DirectorySeparatorChar));

    public bool IsModified(string archivePath)
    {
        lock (_lock) return Manifest.Files.ContainsKey(Key(archivePath));
    }

    public ChangeRecord? Get(string archivePath)
    {
        lock (_lock) return Manifest.Files.GetValueOrDefault(Key(archivePath));
    }

    public IReadOnlyCollection<string> ModifiedPaths()
    {
        lock (_lock)
        {
            Sync();
            return Manifest.Files.Values.Select(r => r.Path).ToList();
        }
    }

    /// <summary>The edited bytes of a file, or null.</summary>
    public byte[]? ReadEdited(string archivePath)
    {
        var f = WorkspaceFile(archivePath);
        return File.Exists(f) ? File.ReadAllBytes(f) : null;
    }

    public ChangeSet BeginChangeSet(string label)
    {
        var cs = new ChangeSet { Id = "cs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..4], Label = label, Created = DateTime.Now };
        lock (_lock)
        {
            Sync();
            Manifest.ChangeSets[cs.Id] = cs;
        }
        return cs;
    }

    /// <summary>Save an edited file into the workspace (atomically) and record it under a change set.</summary>
    public ChangeRecord SaveEdit(string archivePath, byte[] data, OriginInfo origin, ChangeSet changeSet, string? sourceFile = null, string summary = "")
    {
        CheckLoads(archivePath, data);
        var path = ClientData.ResolveName(archivePath);
        lock (_lock)
        {
            Sync();
            var rec = Put(path, data, origin, changeSet, sourceFile, summary);
            Save();
            return rec;
        }
    }

    /// <summary>Keeps the version a file has now when <paramref name="changeSet"/> replaces an earlier edit of another
    /// change set (0.14.0), writes the new bytes and records them (call under the lock; the caller saves the manifest).</summary>
    private ChangeRecord Put(string path, byte[] data, OriginInfo origin, ChangeSet changeSet, string? sourceFile, string summary)
    {
        var (prior, dropped, stash) = KeepPrior(path, changeSet);
        try
        {
            WriteWorkspaceFile(path, data);
        }
        catch
        {
            if (stash != null) DeleteStash(stash);
            throw;
        }
        return Record(path, data, origin, changeSet, sourceFile, summary, prior, dropped);
    }

    /// <summary>One file of <see cref="SaveEdits"/>: its game path, its bytes (loaded when needed), its origin and summary.</summary>
    public sealed record PendingEdit(string Path, Func<byte[]> Load, OriginInfo Origin, string Summary);

    /// <summary>
    /// 0.14.0 (map conversion): many files saved as one change set. Every file is checked first (the same checks as
    /// <see cref="SaveEdit"/>; one that would not load refuses the whole set and nothing is written), then they are
    /// written and the change list is saved once.
    /// </summary>
    public List<ChangeRecord> SaveEdits(IReadOnlyList<PendingEdit> edits, ChangeSet changeSet, Action<int, int>? progress = null)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            CheckLoads(edits[i].Path, edits[i].Load());
            if (i % 50 == 0) progress?.Invoke(i, edits.Count * 2);
        }
        var recs = new List<ChangeRecord>(edits.Count);
        lock (_lock)
        {
            Sync();
            try
            {
                for (int i = 0; i < edits.Count; i++)
                {
                    var e = edits[i];
                    var data = e.Load();
                    var path = ClientData.ResolveName(e.Path);
                    recs.Add(Put(path, data, e.Origin, changeSet, null, e.Summary));
                    if (i % 50 == 0) progress?.Invoke(edits.Count + i, edits.Count * 2);
                }
            }
            finally
            {
                if (recs.Count > 0) Save();
            }
        }
        return recs;
    }

    /// <summary>The checks of a file before it is saved: a texture, model, map file or table the game would not load is refused.</summary>
    private static void CheckLoads(string archivePath, byte[] data)
    {
        if (archivePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
        {
            var problems = BlpEncoder.Validate(data);
            if (problems.Count > 0) throw new WorkspaceException("The texture would not load in the game: " + string.Join("; ", problems));
        }
        if (archivePath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || archivePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
        {
            var problems = ModelProblems(data);
            if (problems.Count > 0) throw new WorkspaceException("The model would not load correctly in the game: " + string.Join("; ", problems));
        }
        var mapProblems = MapProblems(data, archivePath);
        if (mapProblems.Count > 0) throw new WorkspaceException("The map file would not load correctly in the game: " + string.Join("; ", mapProblems));
        var dbcProblems = DbcProblems(data, archivePath);
        if (dbcProblems.Count > 0) throw new WorkspaceException("The table would not load correctly in the game: " + string.Join("; ", dbcProblems));
    }

    private void WriteWorkspaceFile(string path, byte[] data)
    {
        var dest = WorkspaceFile(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest + ".tmp", data);
        File.Move(dest + ".tmp", dest, true);
    }

    /// <summary>Records a written file under a change set (call under the lock; the caller saves the manifest).</summary>
    private ChangeRecord Record(string path, byte[] data, OriginInfo origin, ChangeSet changeSet, string? sourceFile, string summary, PriorVersion? prior, bool priorDropped)
    {
        var key = Key(path);
        Manifest.Files.TryGetValue(key, out var old);
        if (old != null && old.ChangeSet != changeSet.Id && Manifest.ChangeSets.TryGetValue(old.ChangeSet, out var oldSet))
        {
            oldSet.Files.Remove(key);
            if (oldSet.Files.Count == 0) Manifest.ChangeSets.Remove(oldSet.Id);
        }
        var rec = new ChangeRecord
        {
            Path = path,
            Kind = ServerData.ServerChangeFiles.IsServerPath(path) ? "server" : path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? "texture" : path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ? "model" : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            Original = origin,
            WorkspaceSha256 = Sha256(data),
            Size = data.Length,
            SourceFile = sourceFile,
            SourceSha256 = sourceFile != null && File.Exists(sourceFile) ? Sha256File(sourceFile) : null,
            Edited = DateTime.Now,
            ChangeSet = changeSet.Id,
            Summary = summary,
            Installed = old?.Installed,
            Packed = old?.Packed,
            ServerApplied = old?.ServerApplied,
            Prior = prior,
            PriorDropped = priorDropped,
        };
        Manifest.Files[key] = rec;
        Manifest.ChangeSets[changeSet.Id] = changeSet;
        if (!changeSet.Files.Contains(key)) changeSet.Files.Add(key);
        return rec;
    }

    // ------------------------------------------------------------------ earlier versions (0.14.0)

    /// <summary>How many earlier edits of one file are kept for "Revert this import" (older ones are dropped).</summary>
    public const int MaxPriorVersions = 10;

    /// <summary>Before <paramref name="changeSet"/> replaces a file: when the file holds an edit of ANOTHER change set, its
    /// bytes are copied to history\&lt;change set&gt;\&lt;path&gt; and its record becomes the new record's
    /// <see cref="ChangeRecord.Prior"/>; a file the same change set already wrote keeps its prior version. Call under the
    /// lock, before the file is written. Returns the prior version, whether an earlier version is lost, and the copy made.</summary>
    private (PriorVersion? Prior, bool Dropped, string? Stash) KeepPrior(string path, ChangeSet changeSet)
    {
        if (!Manifest.Files.TryGetValue(Key(path), out var old)) return (null, false, null);
        if (old.ChangeSet == changeSet.Id) return (old.Prior, old.PriorDropped, null);
        var wf = WorkspaceFile(old.Path);
        if (!File.Exists(wf)) return (null, true, null);   // the earlier edit's copy is already gone: nothing to put back
        var baseRel = System.IO.Path.Combine("history", changeSet.Id, ClientData.ResolveName(old.Path).Replace('\\', System.IO.Path.DirectorySeparatorChar));
        var rel = baseRel;
        for (int k = 2; File.Exists(System.IO.Path.Combine(Paths.Dir, rel)); k++) rel = baseRel + "." + k;
        var dest = System.IO.Path.Combine(Paths.Dir, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.Copy(wf, dest + ".tmp", true);
        File.Move(dest + ".tmp", dest, true);
        var set = Manifest.ChangeSets.GetValueOrDefault(old.ChangeSet);
        var kept = new ChangeRecord
        {
            Path = old.Path, Kind = old.Kind, Original = old.Original, WorkspaceSha256 = old.WorkspaceSha256, Size = old.Size,
            SourceFile = old.SourceFile, SourceSha256 = old.SourceSha256, Edited = old.Edited, ChangeSet = old.ChangeSet,
            Summary = old.Summary, Prior = old.Prior, PriorDropped = old.PriorDropped,
            // the deliveries (loose install, patch, server) belong to the file, not the version: they stay on the newest record
        };
        var prior = new PriorVersion { Record = kept, SetLabel = set?.Label ?? old.Summary, SetCreated = set?.Created ?? old.Edited, Stash = rel };
        // keep at most MaxPriorVersions versions: the copies of older ones are deleted
        var link = prior;
        for (int depth = 1; link.Record.Prior != null; depth++)
        {
            if (depth >= MaxPriorVersions)
            {
                DeleteHistory(link.Record.Prior);
                link.Record.Prior = null;
                link.Record.PriorDropped = true;
                break;
            }
            link = link.Record.Prior;
        }
        return (prior, false, dest);
    }

    /// <summary>Deletes the kept copies of a chain of earlier versions (a file reverted to the game's original).</summary>
    private void DeleteHistory(PriorVersion? p)
    {
        for (; p != null; p = p.Record.Prior)
            if (p.Stash.Length > 0) DeleteStash(System.IO.Path.Combine(Paths.Dir, p.Stash));
    }

    private void DeleteStash(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
            PruneEmptyDirs(System.IO.Path.GetDirectoryName(file)!, Paths.HistoryDir);
            if (Directory.Exists(Paths.HistoryDir) && !Directory.EnumerateFileSystemEntries(Paths.HistoryDir).Any()) Directory.Delete(Paths.HistoryDir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Status of every change, newest first.</summary>
    public List<ChangeView> List()
    {
        lock (_lock)
        {
            Sync();
            return Manifest.Files.Values.OrderByDescending(r => r.Edited).Select(View).ToList();
        }
    }

    public ChangeView View(ChangeRecord r)
    {
        var wf = WorkspaceFile(r.Path);
        bool inWs = File.Exists(wf);
        bool installed = r.Installed != null && File.Exists(r.Installed.Dest);
        bool installedCurrent = installed && r.Installed!.Sha256 == r.WorkspaceSha256 && SafeSha(r.Installed.Dest) == r.Installed.Sha256;
        bool packed = r.Packed != null && File.Exists(r.Packed.Archive);
        bool packedCurrent = packed && r.Packed!.Sha256 == r.WorkspaceSha256;
        return new ChangeView(r, wf, inWs, installed, installedCurrent, packed, packedCurrent);
    }

    private static string? SafeSha(string path)
    {
        try { return Sha256File(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ revert

    /// <summary>Undo the edit of one file: removes its loose install from the game (only if unchanged since we installed it),
    /// deletes the workspace copy and the record (and the kept earlier versions of the file): the game's original is used
    /// again. Returns what was done, in words.</summary>
    public List<string> Revert(string archivePath)
    {
        var done = new List<string>();
        lock (_lock)
        {
            Sync();
            var key = Key(archivePath);
            if (!Manifest.Files.TryGetValue(key, out var rec)) return done;
            RevertToOriginal(key, rec, done);
            Save();
        }
        return done;
    }

    /// <summary>One file back to the game's original (call under the lock; the caller saves the manifest).</summary>
    private void RevertToOriginal(string key, ChangeRecord rec, List<string> done)
    {
        if (rec.Installed != null) done.Add(RemoveInstalled(rec.Path, rec.Installed, key));
        var wf = WorkspaceFile(rec.Path);
        if (File.Exists(wf)) File.Delete(wf);
        PruneEmptyDirs(System.IO.Path.GetDirectoryName(wf)!, Paths.WorkspaceDir);
        DeleteHistory(rec.Prior);
        Manifest.Files.Remove(key);
        if (Manifest.ChangeSets.TryGetValue(rec.ChangeSet, out var cs))
        {
            cs.Files.Remove(key);
            if (cs.Files.Count == 0) Manifest.ChangeSets.Remove(cs.Id);
        }
        done.Add($"{rec.Path}: your edit was removed; the game's original is used again" + (rec.Packed != null ? " (rebuild patch-3.MPQ to drop it from the archive too)" : "") + ".");
    }

    /// <summary>0.14.0: what "Revert this import" would do with each file of a change set, and what stops it: a file this
    /// change set replaced that a NEWER change set changed again (reverting would throw the newer work away, or leave it
    /// built on this import), or an earlier version whose copy is no longer kept. Null when there is no such change set.</summary>
    public RevertPlan? PlanRevertChangeSet(string changeSetId)
    {
        lock (_lock)
        {
            Sync();
            return PlanCore(changeSetId);
        }
    }

    private RevertPlan? PlanCore(string changeSetId)
    {
        if (!Manifest.ChangeSets.TryGetValue(changeSetId, out var cs)) return null;
        var steps = new List<RevertStep>();
        var problems = new List<string>();
        foreach (var key in cs.Files)
        {
            if (!Manifest.Files.TryGetValue(key, out var r)) continue;
            if (r.Prior != null)
            {
                if (File.Exists(System.IO.Path.Combine(Paths.Dir, r.Prior.Stash))) steps.Add(new RevertStep(r.Path, r.Prior.SetLabel));
                else problems.Add($"{r.Path}: the kept copy of your earlier version is missing ({r.Prior.Stash})");
            }
            else if (r.PriorDropped) problems.Add($"{r.Path}: the version before this import is no longer kept (only the {MaxPriorVersions} latest versions of a file are kept)");
            else steps.Add(new RevertStep(r.Path, null));
        }
        // files this change set wrote that a newer change set took over since: their chain of earlier versions names it
        foreach (var r in Manifest.Files.Values.Where(r => r.ChangeSet != changeSetId).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var newer = new List<string> { r.ChangeSet };
            for (var p = r.Prior; p != null; p = p.Record.Prior)
            {
                if (p.Record.ChangeSet == changeSetId)
                {
                    var labels = newer.Distinct().Select(id => "\"" + (Manifest.ChangeSets.GetValueOrDefault(id)?.Label ?? id) + "\"");
                    problems.Add($"{r.Path}: changed again later by {string.Join(", then ", labels.Reverse())}");
                    break;
                }
                newer.Add(p.Record.ChangeSet);
            }
        }
        return new RevertPlan(cs, steps, problems);
    }

    /// <summary>"Revert this import": every file of a change set goes back to the version it had just before it (0.14.0):
    /// your earlier edit when the change set replaced one (a map conversion that builds on your tables, an update import over
    /// your texture), the game's original otherwise. Other change sets are not changed. When a newer change set changed one
    /// of its files again, nothing is reverted (<see cref="WorkspaceException"/> with <see cref="RevertPlan.RefusalText"/>).</summary>
    public List<string> RevertChangeSet(string changeSetId)
    {
        var done = new List<string>();
        lock (_lock)
        {
            Sync();
            var plan = PlanCore(changeSetId);
            if (plan == null) return done;
            if (!plan.CanRevert) throw new WorkspaceException(plan.RefusalText);
            var cs = plan.Set;
            try
            {
                foreach (var key in cs.Files.ToList())
                {
                    if (!Manifest.Files.TryGetValue(key, out var rec))
                    {
                        cs.Files.Remove(key);
                        continue;
                    }
                    if (rec.Prior != null) StepBack(key, rec, cs, done);
                    else RevertToOriginal(key, rec, done);
                }
                if (cs.Files.Count == 0) Manifest.ChangeSets.Remove(cs.Id);
            }
            finally
            {
                Save();
            }
        }
        return done;
    }

    /// <summary>One file back to its earlier edit (call under the lock): the kept copy is moved back into the workspace, the
    /// earlier record returns to its own change set (made again when it had no files left), the loose copy of the
    /// reverted version is removed from the game; the patch and server marks stay (they show "older version").</summary>
    private void StepBack(string key, ChangeRecord rec, ChangeSet cs, List<string> done)
    {
        var prior = rec.Prior!;
        var src = System.IO.Path.Combine(Paths.Dir, prior.Stash);
        if (rec.Installed != null) done.Add(RemoveInstalled(rec.Path, rec.Installed, key));
        var back = prior.Record;
        var wf = WorkspaceFile(back.Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(wf)!);
        File.Copy(src, wf + ".tmp", true);
        File.Move(wf + ".tmp", wf, true);
        DeleteStash(src);
        back.Installed = null;
        back.Packed = rec.Packed;
        back.ServerApplied = rec.ServerApplied;
        Manifest.Files[key] = back;
        cs.Files.Remove(key);
        if (!Manifest.ChangeSets.TryGetValue(back.ChangeSet, out var owner))
            Manifest.ChangeSets[back.ChangeSet] = owner = new ChangeSet { Id = back.ChangeSet, Label = prior.SetLabel, Created = prior.SetCreated };
        if (!owner.Files.Contains(key)) owner.Files.Add(key);
        done.Add($"{rec.Path}: put back to your earlier edit (\"{prior.SetLabel}\")" +
                 (rec.Installed != null ? "; install it again to test it in the game" : "") +
                 (rec.Packed != null ? "; rebuild patch-3.MPQ to put it into the archive" : "") + ".");
    }

    private string RemoveInstalled(string path, InstallRecord inst, string key)
    {
        if (!File.Exists(inst.Dest)) return $"{path}: the loose copy was already gone from the game folder.";
        if (SafeSha(inst.Dest) != inst.Sha256)
        {
            Manifest.OrphanInstalls[key] = inst;
            return $"{path}: the loose copy in the game folder was changed by something else since it was installed, so it was left in place: {inst.Dest}";
        }
        File.Delete(inst.Dest);
        if (inst.Backup != null && File.Exists(inst.Backup))
        {
            File.Copy(inst.Backup, inst.Dest, true);
            return $"{path}: removed from the game; the file that was there before was restored from the backup.";
        }
        PruneEmptyDirs(System.IO.Path.GetDirectoryName(inst.Dest)!, Paths.DataDir);
        return $"{path}: removed from the game folder.";
    }

    private static void PruneEmptyDirs(string dir, string stopAt)
    {
        var stop = System.IO.Path.GetFullPath(stopAt).TrimEnd('\\');
        var d = System.IO.Path.GetFullPath(dir).TrimEnd('\\');
        while (d.Length > stop.Length && d.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(d).Any()) break;
                Directory.Delete(d);
            }
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
            d = System.IO.Path.GetDirectoryName(d)!;
        }
    }

    // ------------------------------------------------------------------ loose files

    /// <summary>What a loose install would do (no files are written).</summary>
    public List<DeliveryAction> PlanLooseInstall(IEnumerable<string>? paths = null, bool force = false)
    {
        lock (_lock)
        {
            Sync();
            var recs = Selected(paths);
            if (recs.Count == 0) throw new WorkspaceException("There are no changes to install.");
            var actions = new List<DeliveryAction>();
            foreach (var r in recs)
            {
                var src = WorkspaceFile(r.Path);
                if (!File.Exists(src)) throw new WorkspaceException($"The workspace copy of {r.Path} is missing; revert it and import it again.");
                var dest = DataFile(r.Path);
                string action;
                if (File.Exists(dest))
                {
                    if (r.Installed != null && SafeSha(dest) == r.Installed.Sha256) action = "update";
                    else if (force) action = "replace (backed up)";
                    else throw new WorkspaceException($"{dest} already exists and was not installed by WoW Data Studio. It was left untouched.");
                }
                else action = "add";
                actions.Add(new DeliveryAction(r.Path, src, dest, action));
            }
            return actions;
        }
    }

    /// <summary>Copy workspace files to Client\Data\&lt;archive path&gt; (the client's loose-file mode reads them there).</summary>
    public List<DeliveryAction> InstallLoose(IEnumerable<string>? paths = null, bool force = false)
    {
        lock (_lock)
        {
            Sync();
            var actions = PlanLooseInstall(paths, force);
            var backupRoot = System.IO.Path.Combine(Paths.BackupDir, "loose_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            foreach (var a in actions)
            {
                var rec = Manifest.Files[Key(a.Path)];
                string? backup = rec.Installed?.Backup;
                if (a.Action.StartsWith("replace"))
                {
                    backup = System.IO.Path.Combine(backupRoot, a.Path.Replace('\\', System.IO.Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                    File.Copy(a.Dest, backup, true);
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(a.Dest)!);
                var tmp = a.Dest + ".wowdata_tmp";
                File.Copy(a.Source, tmp, true);
                File.Move(tmp, a.Dest, true);
                rec.Installed = new InstallRecord { Dest = a.Dest, Sha256 = Sha256File(a.Dest), Installed = DateTime.Now, Backup = backup };
                Manifest.OrphanInstalls.Remove(Key(a.Path));
            }
            Save();
            return actions;
        }
    }

    /// <summary>Remove installed loose files (only unchanged ones of our own) from the game folder.</summary>
    public List<string> RemoveLoose(IEnumerable<string>? paths = null)
    {
        lock (_lock)
        {
            Sync();
            var done = new List<string>();
            foreach (var r in Selected(paths).Where(r => r.Installed != null))
            {
                done.Add(RemoveInstalled(r.Path, r.Installed!, Key(r.Path)));
                if (!Manifest.OrphanInstalls.ContainsKey(Key(r.Path))) r.Installed = null;
            }
            foreach (var kv in Manifest.OrphanInstalls.ToList())
                if (paths == null && SafeSha(kv.Value.Dest) == kv.Value.Sha256)
                {
                    done.Add(RemoveInstalled(kv.Key, kv.Value, kv.Key));
                    Manifest.OrphanInstalls.Remove(kv.Key);
                }
            Save();
            return done;
        }
    }

    /// <summary>The game files to deliver (test in game, release patch): never the server edits (0.9.0), which go to the
    /// server's database with "Apply to server" instead.</summary>
    private List<ChangeRecord> Selected(IEnumerable<string>? paths)
    {
        if (paths == null) return Manifest.Files.Values.Where(r => !r.IsServerEdit).OrderBy(r => r.Path.ToUpperInvariant(), StringComparer.Ordinal).ToList();
        return paths.Select(p => Manifest.Files.GetValueOrDefault(Key(p))).Where(r => r != null && !r.IsServerEdit).Cast<ChangeRecord>().ToList();
    }

    /// <summary>0.9.0: the changes that are game files (the server edits left out).</summary>
    public List<ChangeView> GameFiles() => List().Where(v => !v.Record.IsServerEdit).ToList();

    /// <summary>0.9.0: record that server edits were applied to the server (or, with null, that the apply was undone).</summary>
    public void MarkServerApplied(IEnumerable<string> paths, DateTime? time, string? target)
    {
        lock (_lock)
        {
            Sync();
            foreach (var p in paths)
                if (Manifest.Files.TryGetValue(Key(p), out var r))
                    r.ServerApplied = time == null ? null : new ServerAppliedRecord { Time = time.Value, Target = target ?? "", Sha256 = r.WorkspaceSha256 };
            Save();
        }
    }

    // ------------------------------------------------------------------ patch archive

    public static bool IsAllowedPatchName(string name) =>
        PatchName.IsMatch(name) && !ClientData.StockArchives.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Build a patch archive from every workspace file (verified by read-back). With <paramref name="install"/>, copy
    /// it to Client\Data (an existing file of that name is backed up first).</summary>
    public PatchBuild BuildPatch(string name = "patch-3.MPQ", bool install = false, string? outPath = null)
    {
        if (!IsAllowedPatchName(name))
            throw new WorkspaceException($"{name}: the archive must be named patch-<3..9 or A..Z>.MPQ (the game loads Data\\patch-?.MPQ; patch.MPQ and patch-2.MPQ are the game's own).");
        lock (_lock)
        {
            Sync();
            var recs = Selected(null);
            if (recs.Count == 0) throw new WorkspaceException("There are no changes to put into a patch.");
            var contents = new Dictionary<string, byte[]>();
            foreach (var r in recs)
            {
                var f = WorkspaceFile(r.Path);
                if (!File.Exists(f)) throw new WorkspaceException($"The workspace copy of {r.Path} is missing; revert it and import it again.");
                var data = File.ReadAllBytes(f);
                if (r.Path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                {
                    var problems = BlpEncoder.Validate(data);
                    if (problems.Count > 0) throw new WorkspaceException($"{r.Path} would not load in the game: {string.Join("; ", problems)}");
                }
                if (r.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
                {
                    var problems = ModelProblems(data);
                    if (problems.Count > 0) throw new WorkspaceException($"{r.Path} would not load correctly in the game: {string.Join("; ", problems)}");
                }
                var mp = MapProblems(data, r.Path);
                if (mp.Count > 0) throw new WorkspaceException($"{r.Path} would not load correctly in the game: {string.Join("; ", mp)}");
                var dp = DbcProblems(data, r.Path);
                if (dp.Count > 0) throw new WorkspaceException($"{r.Path} would not load correctly in the game: {string.Join("; ", dp)}");
                contents[r.Path] = data;
            }
            Directory.CreateDirectory(Paths.BuildDir);
            var output = outPath ?? System.IO.Path.Combine(Paths.BuildDir, name);
            if (string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output))?.TrimEnd('\\'), System.IO.Path.GetFullPath(Paths.DataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceException("Build into the build folder; installing into the game's Data folder is a separate step.");
            MpqWriter.Build(contents, output, overwrite: true);
            var problemsMpq = MpqWriter.Verify(output, contents);
            if (problemsMpq.Count > 0) throw new WorkspaceException("The built archive failed its read-back check: " + string.Join("; ", problemsMpq));
            var build = new PatchBuild { Path = output, Built = DateTime.Now, Files = contents.Keys.ToList(), Sha256 = Sha256File(output), Size = new FileInfo(output).Length };
            string archiveForRecords = output;
            if (install)
            {
                var dest = System.IO.Path.Combine(Paths.DataDir, name);
                var before = PatchStateCore(name);
                build.OriginalRecorded = true;
                if (File.Exists(dest))
                {
                    Directory.CreateDirectory(Paths.BackupDir);
                    var b = System.IO.Path.Combine(Paths.BackupDir, $"{name}.{DateTime.Now:yyyyMMdd_HHmmss}.bak");
                    for (int k = 2; File.Exists(b); k++) b = System.IO.Path.Combine(Paths.BackupDir, $"{name}.{DateTime.Now:yyyyMMdd_HHmmss}_{k}.bak");   // never overwrite an older backup
                    File.Copy(dest, b, false);
                    build.Backup = b;
                    // the patch to put back on uninstall: the one that was there before the app's first install
                    build.OriginalBackup = before.CanUninstall ? before.RestoreBackup : b;
                }
                var tmp = dest + ".wowdata_tmp";
                var old = dest + ".wowdata_old";
                bool moved = false;
                try
                {
                    File.Copy(output, tmp, true);
                    // 0.8.0: the app itself reads the installed patch-3.MPQ (the game's archives are mounted when the folder is
                    // opened, with delete sharing): replacing an open file is refused by Windows, renaming it is not. So the
                    // old file is renamed out of the way first, and deleted once the new one is in place.
                    if (File.Exists(dest))
                    {
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(dest, old);
                        moved = true;
                    }
                    File.Move(tmp, dest, true);
                    if (moved)
                    {
                        try { File.Delete(old); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still open elsewhere: removed later
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    if (moved && !File.Exists(dest) && File.Exists(old)) File.Move(old, dest);
                    throw new WorkspaceException($"Could not write {dest}. Is the game running? Close it and try again. ({e.Message})");
                }
                build.InstalledTo = dest;
                archiveForRecords = dest;
            }
            foreach (var r in recs) r.Packed = new PackRecord { Archive = archiveForRecords, Sha256 = r.WorkspaceSha256, Built = build.Built, InstalledTo = build.InstalledTo };
            Manifest.PatchBuilds.Add(build);
            Save();
            return build;
        }
    }

    // ------------------------------------------------------------------ installed patch archive (0.6.1)

    /// <summary>Who made Data\&lt;name&gt; (default patch-3.MPQ), and what uninstalling it would do.</summary>
    public PatchState GetPatchState(string name = "patch-3.MPQ")
    {
        lock (_lock)
        {
            Sync();
            return PatchStateCore(name);
        }
    }

    private static bool SamePath(string? a, string b) =>
        a != null && string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private PatchState PatchStateCore(string name)
    {
        var dest = System.IO.Path.Combine(Paths.DataDir, name);
        if (!File.Exists(dest)) return new PatchState(dest, PatchOwner.None, null, 0, null, null);
        var fi = new FileInfo(dest);
        var sha = SafeSha(dest);
        var last = Manifest.PatchBuilds.LastOrDefault(b => b.Uninstalled == null && SamePath(b.InstalledTo, dest));
        if (last == null) return new PatchState(dest, PatchOwner.Foreign, null, fi.Length, fi.LastWriteTime, null);
        PatchOwner owner;
        if (last.Sha256 != null) owner = sha == last.Sha256 ? PatchOwner.Ours : PatchOwner.Foreign;
        else if (File.Exists(last.Path) && SafeSha(last.Path) == sha) owner = PatchOwner.Ours;
        else owner = PatchOwner.OursUnverified;
        if (owner == PatchOwner.Foreign) return new PatchState(dest, owner, null, fi.Length, fi.LastWriteTime, null);
        return new PatchState(dest, owner, last, fi.Length, fi.LastWriteTime, OriginalBackupOf(last, dest));
    }

    /// <summary>The backup to put back when <paramref name="build"/> is uninstalled: recorded since 0.6.1; for older
    /// records, the backup of the first build of the unbroken run of installs into the same file.</summary>
    private string? OriginalBackupOf(PatchBuild build, string dest)
    {
        string? found;
        if (build.OriginalRecorded) found = build.OriginalBackup;
        else
        {
            int i = Manifest.PatchBuilds.IndexOf(build);
            int first = i;
            found = null;
            bool done = false;
            for (int k = i - 1; k >= 0 && !done; k--)
            {
                var b = Manifest.PatchBuilds[k];
                if (!SamePath(b.InstalledTo, dest)) continue;
                if (b.Uninstalled != null) break;
                if (b.OriginalRecorded)
                {
                    found = b.OriginalBackup;
                    done = true;
                    first = -1;
                }
                else first = k;
            }
            if (first >= 0) found = Manifest.PatchBuilds[first].Backup;
        }
        return found != null && File.Exists(found) ? found : null;
    }

    /// <summary>"Uninstall release patch": takes Data\&lt;name&gt; out of the game and puts back the patch that was there before
    /// WoW Data Studio first installed one (if there was one). A patch the app cannot prove it built is only touched with
    /// <paramref name="confirmedNotOurs"/> (the user agreed), and is then moved to the backup folder, never deleted.
    /// Your workspace is not changed.</summary>
    public List<string> UninstallPatch(string name = "patch-3.MPQ", bool confirmedNotOurs = false)
    {
        if (!IsAllowedPatchName(name)) throw new WorkspaceException($"{name} is not a patch archive the app manages.");
        lock (_lock)
        {
            Sync();
            var st = PatchStateCore(name);
            var done = new List<string>();
            if (st.Owner == PatchOwner.None)
            {
                done.Add($"No {name} is installed in the game: nothing to uninstall.");
                return done;
            }
            if (st.Owner != PatchOwner.Ours && !confirmedNotOurs)
                throw new WorkspaceException($"{st.Path} cannot be proven to be built by WoW Data Studio. It was left untouched.");
            try
            {
                if (st.Owner == PatchOwner.Ours)
                {
                    File.Delete(st.Path);
                    done.Add($"{name}: the release patch built by WoW Data Studio on {st.Build!.Built:yyyy-MM-dd HH:mm} was removed from the game (your changes stay in your workspace; build it again at any time).");
                }
                else
                {
                    Directory.CreateDirectory(Paths.BackupDir);
                    var keep = System.IO.Path.Combine(Paths.BackupDir, $"{name}.removed_{DateTime.Now:yyyyMMdd_HHmmss}.bak");
                    for (int k = 2; File.Exists(keep); k++) keep = System.IO.Path.Combine(Paths.BackupDir, $"{name}.removed_{DateTime.Now:yyyyMMdd_HHmmss}_{k}.bak");
                    File.Move(st.Path, keep, false);
                    done.Add($"{name} was moved out of the game into the backup folder (not deleted): {keep}");
                }
                if (st.CanUninstall && st.RestoreBackup != null)
                {
                    File.Copy(st.RestoreBackup, st.Path, false);
                    done.Add($"The {name} that was there before was put back from {st.RestoreBackup}.");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new WorkspaceException($"Could not change {st.Path}. Is the game running? Close it and try again. ({e.Message})");
            }
            foreach (var b in Manifest.PatchBuilds.Where(b => b.Uninstalled == null && SamePath(b.InstalledTo, st.Path)))
                b.Uninstalled = DateTime.Now;
            foreach (var r in Manifest.Files.Values.Where(r => r.Packed != null && SamePath(r.Packed.Archive, st.Path)))
                r.Packed = null;
            Save();
            done.Add("Restart the game to see the change.");
            return done;
        }
    }

    /// <summary>True when any loose file is installed (with the loose-file client patch they win over every archive).</summary>
    public bool HasLooseInstalls()
    {
        lock (_lock)
        {
            Sync();
            return Manifest.Files.Values.Any(r => r.Installed != null) || Manifest.OrphanInstalls.Count > 0;
        }
    }
}
