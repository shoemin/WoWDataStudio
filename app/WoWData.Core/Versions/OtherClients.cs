using System.Diagnostics;
using System.Text.RegularExpressions;
using WoWData.Core.Dbc;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions;

/// <summary>How a client stores its game data.</summary>
public enum ClientFormat
{
    /// <summary>MPQ archives (1.x to 4.x): readable.</summary>
    Mpq,
    /// <summary>CASC storage (6.x on, and every Battle.net client): found and listed, not readable yet (docs\research\CASC_INVESTIGATION.md).</summary>
    Casc,
    Unknown,
}

/// <summary>A World of Warcraft client folder found on disk: its executable, version, data format and archive layout.</summary>
public sealed record ClientCopy(string Folder, string Exe, string DataDir, string Version, int Build, ClientFormat Format, ArchiveLayout Layout, string? Locale)
{
    /// <summary>The folder's name (the name a client is picked by on the command line, with the version and build).</summary>
    public string Name => Path.GetFileName(Folder.TrimEnd('\\', '/'));
    public string Label => $"{Version} (build {Build})";
    public bool Readable => Format == ClientFormat.Mpq;

    /// <summary>The release in words, from the version number.</summary>
    public string Release => Version.Split('.') is [var maj, var min, ..] && int.TryParse(maj, out var a) && int.TryParse(min, out var b)
        ? (a, b) switch
        {
            (0, _) => "Alpha/Beta",
            (1, >= 13) => "Classic (modern client)",
            (1, _) => "Vanilla",
            (2, >= 5) => "TBC Classic (modern client)",
            (2, _) => "The Burning Crusade",
            (3, >= 4) => "Wrath Classic (modern client)",
            (3, _) => "Wrath of the Lich King",
            (4, >= 4) => "Cataclysm Classic (modern client)",
            (4, _) => "Cataclysm",
            (5, >= 5) => "Mists of Pandaria Classic (modern client)",
            (5, _) => "Mists of Pandaria",
            (6, _) => "Warlords of Draenor",
            (7, _) => "Legion",
            (8, _) => "Battle for Azeroth",
            (9, _) => "Shadowlands",
            (10, _) => "Dragonflight",
            (11, _) => "The War Within",
            _ => "Retail",
        }
        : "";
}

/// <summary>
/// Experimental (0.14.0): finding other World of Warcraft client versions on disk, to read their game data without a
/// server. Nothing here writes to a client folder: archives are opened read-only with shared access (see
/// <see cref="MpqArchive"/>).
/// </summary>
public static class ClientCopies
{
    private static readonly Regex ExeRe = new(@"^wow(-64)?(classic)?[tb]?(-64)?(-patched(-offline)?)?\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The client in a folder (the folder with the game's executable, its Data folder, or the executable), or null.</summary>
    public static ClientCopy? Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var dir = path.Trim().Trim('"');
        if (File.Exists(dir)) dir = Path.GetDirectoryName(Path.GetFullPath(dir))!;
        if (!Directory.Exists(dir)) return null;
        dir = Path.GetFullPath(dir).TrimEnd('\\', '/');
        if (Path.GetFileName(dir).Equals("Data", StringComparison.OrdinalIgnoreCase) && FindExe(Path.GetDirectoryName(dir)!) != null)
            dir = Path.GetDirectoryName(dir)!;
        var exe = FindExe(dir);
        if (exe == null) return null;
        var (version, build) = ExeVersion(exe);
        var data = Path.Combine(dir, "Data");
        if (Directory.Exists(data) && Directory.EnumerateFiles(data, "*.mpq").Any())
        {
            var layout = ClientData.DetectLayout(data);
            return new ClientCopy(dir, exe, data, version, build, ClientFormat.Mpq, layout, layout == ArchiveLayout.Split ? null : ClientData.DetectLocale(data));
        }
        // CASC: a local store (data\config + data\indices), next to the executable or one level up (Battle.net flavors)
        foreach (var cand in new[] { data, Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Data") })
            if (Directory.Exists(Path.Combine(cand, "config")) && Directory.Exists(Path.Combine(cand, "indices")))
                return new ClientCopy(dir, exe, cand, version, build, ClientFormat.Casc, ArchiveLayout.Split, null);
        return new ClientCopy(dir, exe, data, version, build, ClientFormat.Unknown, ArchiveLayout.Split, null);
    }

    /// <summary>Every client under a folder (the folder itself and its subfolders, <paramref name="maxDepth"/> levels deep).</summary>
    public static List<ClientCopy> Find(string root, int maxDepth = 4)
    {
        var found = new List<ClientCopy>();
        void Walk(string d, int depth)
        {
            ClientCopy? c = null;
            try { c = Inspect(d); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (c != null)
            {
                found.Add(c);
                return;
            }
            if (depth >= maxDepth) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var s in subs)
            {
                var n = Path.GetFileName(s);
                if (n.Equals("Data", StringComparison.OrdinalIgnoreCase) || n.Equals("Cache", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("WTF", StringComparison.OrdinalIgnoreCase) || n.Equals("Interface", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(s, depth + 1);
            }
        }
        if (Directory.Exists(root)) Walk(Path.GetFullPath(root), 0);
        return found;
    }

    private static string? FindExe(string dir)
    {
        try
        {
            var exes = Directory.EnumerateFiles(dir, "*.exe").Where(f => ExeRe.IsMatch(Path.GetFileName(f))).ToList();
            // the plain game executable first (Wow.exe), then the 64-bit and the variants
            return exes.OrderBy(f => Path.GetFileName(f).Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The version ("3.3.5") and build (12340) in the executable's version resource; ("?", 0) when it has none.</summary>
    public static (string Version, int Build) ExeVersion(string exe)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            // the text is reliable ("3, 3, 5, 12340", "12.1.5.70448"); the numeric parts of the modern executables are
            // packed differently (12.1.5.70448 reads as 1201.5.7044.8)
            var parts = (vi.FileVersion ?? "").Split(new[] { '.', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts.All(p => p.All(char.IsDigit)))
                return ($"{int.Parse(parts[0])}.{int.Parse(parts[1])}.{int.Parse(parts[2])}", int.Parse(parts[3]));
            if (vi.FileMajorPart == 0 && vi.FileMinorPart == 0 && vi.FilePrivatePart == 0) return ("?", 0);
            return ($"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}", vi.FilePrivatePart);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException) { return ("?", 0); }
    }

    /// <summary>
    /// The patch archives Blizzard shipped with a release (names relative to the Data folder, &lt;loc&gt; = the locale).
    /// Any other patch-?.MPQ in a client copy was added later (a private server's or a mod's patch); "Blizzard archives
    /// only" leaves those out. Measured on the installs (docs\research\MPQ_ERA_DELTA.md): WotLK's patch-7.mpq is a
    /// server patch that outranks every Blizzard patch and replaces 19 tables, Spell.dbc among them.
    /// </summary>
    public static IReadOnlyList<string> BlizzardPatches(int build) => build switch
    {
        5875 => new[] { "patch.MPQ", "patch-2.MPQ" },
        8606 => new[] { "patch.MPQ", "patch-2.MPQ", @"<loc>\patch-<loc>.MPQ", @"<loc>\patch-<loc>-2.MPQ" },
        12340 => new[] { "patch.MPQ", "patch-2.MPQ", "patch-3.MPQ", @"<loc>\patch-<loc>.MPQ", @"<loc>\patch-<loc>-2.MPQ", @"<loc>\patch-<loc>-3.MPQ" },
        _ => Array.Empty<string>(),
    };
}

/// <summary>Picking a found client by what a person types: its folder, its name, a version ("3.3.5", "2.4") or a build.</summary>
public static class ClientPick
{
    public static ClientCopy Resolve(string text, IReadOnlyList<ClientCopy> found)
    {
        var t = text.Trim().Trim('"');
        if (Directory.Exists(t) || File.Exists(t))
            return ClientCopies.Inspect(t) ?? throw new ArgumentException($"no World of Warcraft client (Wow.exe with game data) in \"{t}\".");
        var hits = found.Where(c => c.Name.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) hits = found.Where(c => c.Build.ToString() == t || c.Version == t).ToList();
        if (hits.Count == 0) hits = found.Where(c => (c.Version + ".").StartsWith(t.TrimEnd('.') + ".", StringComparison.Ordinal)).ToList();
        if (hits.Count == 0) hits = found.Where(c => c.Name.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        // two copies of the same release: prefer the readable one, then the first by name
        if (hits.Count > 1 && hits.Select(h => (h.Version, h.Build)).Distinct().Count() == 1 && hits.Count(h => h.Readable) >= 1)
            return hits.Where(h => h.Readable).OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).First();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new ArgumentException($"no client \"{t}\" among {found.Count}: {string.Join(", ", found.Select(c => $"{c.Name} ({c.Version})"))}."),
            _ => throw new ArgumentException($"\"{t}\" matches {hits.Count} clients: {string.Join(", ", hits.Select(c => $"{c.Name} ({c.Version})"))}. Give the folder name."),
        };
    }
}

/// <summary>One data-table row side by side in several versions, column by column (matched by name).</summary>
public sealed record VersionRowCompare(string Table, long Id, IReadOnlyList<string> Clients, IReadOnlyList<(string Column, string?[] Values)> Columns, IReadOnlyList<string> Notes);

public static class VersionCompare
{
    /// <summary>The row with this ID (the table's ID column, or its first field when the layout is unknown).</summary>
    public static DbcRow? FindRow(DbcTable t, long id)
    {
        if (t.Layout.HasKey) return t.Find(id);
        if (t.Layout.Columns.Count == 0) return null;
        var c = t.Layout.Columns[0];
        return t.Rows.FirstOrDefault(r => t.Number(r, c) == id);
    }

    /// <summary>
    /// The row of <paramref name="table"/> with <paramref name="id"/> in each client. Columns are matched by name, in the
    /// order they first appear; unnamed columns (col_N, from a layout not known yet) are left out unless
    /// <paramref name="includeRaw"/>, because the same position means different things in different versions.
    /// </summary>
    public static VersionRowCompare Row(string table, long id, IReadOnlyList<(string Client, DbcTable? Table)> tables, int locale = 0, bool includeRaw = false)
    {
        var notes = new List<string>();
        var order = new List<string>();
        var values = new Dictionary<string, string?[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tables.Count; i++)
        {
            var (client, t) = tables[i];
            if (t == null) { notes.Add($"{client}: no {table} table."); continue; }
            if (t.Layout.IsFallback) notes.Add($"{client}: {table} has no known layout for this version yet (raw columns{(includeRaw ? "" : ", left out")}).");
            var row = FindRow(t, id);
            if (row == null) { notes.Add($"{client}: no row {id}."); continue; }
            foreach (var c in t.Layout.Columns)
            {
                if (!includeRaw && c.Name.StartsWith("col_", StringComparison.Ordinal)) continue;
                if (!values.TryGetValue(c.Name, out var v))
                {
                    values[c.Name] = v = new string?[tables.Count];
                    order.Add(c.Name);
                }
                v[i] = t.Format(row, c, locale);
            }
        }
        return new VersionRowCompare(table, id, tables.Select(x => x.Client).ToList(), order.Select(n => (n, values[n])).ToList(), notes);
    }
}

/// <summary>
/// An open client of another version (experimental, read-only): its archives in the order that client mounts them, and
/// its data tables read with that build's layouts (<see cref="DbcLayouts.ForBuild"/>; raw columns where none is known).
/// </summary>
public sealed class OtherClient : IDisposable
{
    public ClientCopy Copy { get; }
    public ClientData Data { get; }
    public DbcLayouts Layouts { get; }
    /// <summary>True when patch archives that Blizzard did not ship are left out.</summary>
    public bool BlizzardOnly { get; }
    /// <summary>The mounted (or, with <see cref="BlizzardOnly"/>, left-out) patch archives that are not Blizzard's.</summary>
    public IReadOnlyList<string> CustomArchives { get; }

    private OtherClient(ClientCopy copy, ClientData data, bool blizzardOnly, IReadOnlyList<string> custom)
    {
        Copy = copy;
        Data = data;
        BlizzardOnly = blizzardOnly;
        CustomArchives = custom;
        Layouts = DbcLayouts.ForBuild(copy.Build, copy.Version);
    }

    public static OtherClient Open(ClientCopy copy, bool blizzardOnly = false)
    {
        if (copy.Format == ClientFormat.Casc)
            throw new NotSupportedException($"{copy.Name} ({copy.Label}) stores its data in CASC, which WoW Data Studio cannot read yet (see the manual: Other versions).");
        if (copy.Format != ClientFormat.Mpq)
            throw new NotSupportedException($"{copy.Name}: no game archives were found in {copy.DataDir}.");
        var order = ClientData.ClientArchiveOrder(copy.DataDir);
        var known = ClientCopies.BlizzardPatches(copy.Build).Select(n => copy.Locale == null ? n : n.Replace("<loc>", copy.Locale)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var custom = known.Count == 0 ? new List<string>()
            : order.Where(n => Path.GetFileName(n).StartsWith("patch", StringComparison.OrdinalIgnoreCase) && !known.Contains(n)).ToList();
        var data = blizzardOnly ? new ClientData(copy.DataDir, order.Except(custom, StringComparer.OrdinalIgnoreCase).ToList()) : new ClientData(copy.DataDir, order);
        return new OtherClient(copy, data, blizzardOnly, custom);
    }

    /// <summary>The data tables of the client (names without DBFilesClient\ and .dbc), sorted.</summary>
    public IReadOnlyList<string> TableNames() =>
        Data.Glob(@"DBFilesClient\*.dbc").Select(p => Path.GetFileNameWithoutExtension(p)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A data table read with this build's layout, or null when the client has no such table.</summary>
    public DbcTable? Table(string name)
    {
        var n = name.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var bytes = Data.Read($@"DBFilesClient\{n}.dbc");
        return bytes == null ? null : DbcTable.Load(bytes, n, Layouts);
    }

    public void Dispose() => Data.Dispose();
}
