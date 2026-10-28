using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace WoWData.Core.Session;

/// <summary>Everything that belongs to the opened game folder: archives, the index, the workspace.</summary>
public sealed class AppSession : IDisposable
{
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public ClientPaths? Client { get; private set; }
    public ClientData? Data { get; private set; }
    public Catalog? Catalog { get; private set; }
    public AssetIndex? Index { get; private set; }
    public WoWData.Core.Workspace.Workspace? Workspace { get; private set; }
    public TextureService? Textures { get; private set; }
    /// <summary>Model textures and looks for the 3D viewer (your edited textures win over the game's).</summary>
    public ModelResolver? Models { get; private set; }
    /// <summary>Model export / import through Blender (Phase 3c).</summary>
    public WoWData.Core.ModelIO.ModelPackageService? ModelPackages { get; private set; }
    public List<IndexEntry> Entries { get; private set; } = new();

    // ------------------------------------------------------------------ M1: another version opened read only
    // (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md). For your 1.12 game the concrete properties above are set as before
    // and these stay null (except Profile); for another version only these are set.

    /// <summary>What is open and what it may do: <see cref="WoWData.Core.Versions.GameProfile.Own112"/> after
    /// <see cref="OpenAsync"/>, a read-only profile after <see cref="OpenOtherAsync"/>; null when nothing is open.</summary>
    public WoWData.Core.Versions.GameProfile? Profile { get; private set; }
    /// <summary>True when another version is open (read only): no workspace, no edits, no installs.</summary>
    public bool IsReadOnly => Profile is { IsOwnClient: false };
    /// <summary>Why the writes are greyed out (another version opened read only), or null for your 1.12 game: the text
    /// the app shows next to and on hover over every write, the same reason "wds" prints when it refuses one.</summary>
    public string? WriteRefusal => Profile?.WriteRefusal;
    /// <summary>The world server of the open version (<see cref="WoWData.Core.Servers.ServerProviders"/>): your 1.12
    /// game's world database, or, for another version, its provider ("no server connected" until one exists).</summary>
    public WoWData.Core.Servers.IServerProvider? Server { get; private set; }
    /// <summary>The other version's client (its files and DB2 tables).</summary>
    public WoWData.Core.Versions.OtherClient? Other { get; private set; }
    /// <summary>The other version's files by name or #FileDataID (names from the index).</summary>
    public WoWData.Core.Versions.ModernClientFiles? Files { get; private set; }
    /// <summary>The other version's display chains (DB2 tables with our measured column names).</summary>
    public Catalog160? Displays { get; private set; }
    /// <summary>The other version's app folder (only its index lives there; never a workspace).</summary>
    public ClientPaths? OtherPaths { get; private set; }
    /// <summary>M2: the looks of the other version's models (skins, baked NPC textures, item textures).</summary>
    public WoWData.Core.M2.Modern.ModernModelResolver? ModernLooks { get; private set; }

    /// <summary>M2 (design 2.2): the looks of the open game's models, for the viewing code: <see cref="Models"/> for
    /// your 1.12 game (the same object), <see cref="ModernLooks"/> for another version.</summary>
    public IModelLooks? Looks => (IModelLooks?)Models ?? ModernLooks;

    /// <summary>M2 (design 2.2): reads the open game's models: your workspace version, else the game's file (1.12), or
    /// the modern reader with the .skin files by FileDataID (another version).</summary>
    public IModelSource? ModelSource { get; private set; }

    /// <summary>The details panel of an entry, for either kind of index.</summary>
    public EntryDetails Details(IndexEntry e)
    {
        var index = Index ?? throw new InvalidOperationException("Nothing is open.");
        return index.IsModern && Displays != null && Files?.Names != null ? index.DetailsModern(e, Displays, Files.Names) : index.Details(e, Catalog);
    }

    /// <summary>A file of the other version (by name or #FileDataID); throws <see cref="WoWData.Core.Versions.ClientFileException"/>
    /// when it is encrypted or not on this computer, returns null when the client has no such file.</summary>
    public byte[]? ReadOther(string path) => Files?.Read(path);

    private WoWData.Core.Maps.MapCatalog? _maps;
    private readonly object _mapsGate = new();

    /// <summary>The maps of the game for the Maps tab (Map.dbc, WDT tiles, zones, minimap), loaded on first use.</summary>
    public WoWData.Core.Maps.MapCatalog? MapCatalog
    {
        get
        {
            lock (_mapsGate)
            {
                if (_maps == null && Data != null && Client != null)
                {
                    _maps = WoWData.Core.Maps.MapCatalog.Load(Data, Client.IndexFile);
                    _maps.ReadTile = ReadCurrent;     // M4: LoadTerrain reads your workspace's version first, as before
                }
                return _maps;
            }
        }
    }

    private WoWData.Core.Maps.MapCatalog160? _maps160;

    /// <summary>M4: the maps of another version opened read only (Map / AreaTable, WDTs, minimaps, tiles through the
    /// neutral map reader), loaded on first use; null for your 1.12 game.</summary>
    public WoWData.Core.Maps.MapCatalog160? OtherMaps
    {
        get
        {
            lock (_mapsGate)
            {
                if (_maps160 == null && Other != null && Files != null && OtherPaths != null)
                {
                    var names = Files.Names != null ? new WoWData.Core.Maps.ModernAssetNames(Files.Names) : null;
                    var src = new WoWData.Core.Maps.Conversion.Casc160MapSource(Other, names ?? (WoWData.Core.Maps.Neutral.IAssetNames)new WoWData.Core.Maps.Neutral.CascAssetNames(Files.Storage), "the names of this client's index");
                    var files = Files;
                    _maps160 = WoWData.Core.Maps.MapCatalog160.Load(src, (id, ext) => WoWData.Core.M2.Modern.ModernModelSource.NameOf(files, id, ext), OtherPaths.MinimapCacheDir);
                }
                return _maps160;
            }
        }
    }

    /// <summary>M4 (design 2.2): the maps of the open game for the viewing code: <see cref="MapCatalog"/> for your 1.12
    /// game (the same object), <see cref="OtherMaps"/> for another version.</summary>
    public WoWData.Core.Maps.IMapView? Maps => Data != null ? MapCatalog : OtherMaps;

    /// <summary>M4: where the minimap overviews of the open game are cached (its app folder).</summary>
    public string? MinimapCacheDir => (Client ?? OtherPaths)?.MinimapCacheDir;

    private WoWData.Core.Dbc.DbcService? _dbc;

    /// <summary>0.7.0: the data tables (DBC) of the open game folder for the Data tables tab, made on first use.</summary>
    public WoWData.Core.Dbc.DbcService? Dbc
    {
        get
        {
            lock (_mapsGate)
            {
                if (_dbc == null && Data != null && Workspace != null) _dbc = new WoWData.Core.Dbc.DbcService(Data, Workspace);
                return _dbc;
            }
        }
    }

    /// <summary>The language of the game (WTF\Config.wtf "SET locale"), as an index of DbcTable.LocaleNames (0 = enUS).</summary>
    public int GameLocale => LocaleOf(Client?.ClientDir ?? Settings.ClientDir ?? "");

    /// <summary>The language of the game in a folder (its WTF\Config.wtf "SET locale"), as an index of DbcTable.LocaleNames (0 = enUS).</summary>
    public static int LocaleOf(string clientDir)
    {
        {
            try
            {
                var cfg = Path.Combine(clientDir, "WTF", "Config.wtf");
                if (File.Exists(cfg))
                    foreach (var line in File.ReadLines(cfg))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*SET\s+locale\s+""(\w+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) return Math.Max(0, Array.FindIndex(WoWData.Core.Dbc.DbcTable.LocaleNames, n => string.Equals(n, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)));
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 0;
        }
    }

    /// <summary>"Edit in Noggit": the client view and the per-map projects (WoWData.Core.Maps.NoggitService).</summary>
    public WoWData.Core.Maps.NoggitService? Noggit => Client == null ? null : new WoWData.Core.Maps.NoggitService(Client, Settings);

    /// <summary>A map file as the app has it: your workspace's version, else the game's.</summary>
    public byte[]? ReadCurrent(string path) => Workspace != null && Workspace.IsModified(path) ? Workspace.ReadEdited(path) ?? Data?.Read(path) : Data != null ? Data.Read(path) : ReadOtherOrNull(path);

    private byte[]? ReadOtherOrNull(string path)
    {
        try { return ReadOther(path); }
        catch (WoWData.Core.Versions.ClientFileException) { return null; }
    }
    /// <summary>0.6.1: the game's terrain specular settings (its WTF\Config.wtf, read only).</summary>
    public WoWData.Core.Maps.TerrainSpecularSettings GameSpecular => WoWData.Core.Maps.TerrainSpecularSettings.Read(Client?.ClientDir ?? Settings.ClientDir);

    /// <summary>True when the terrain previews and exports show the "_s" ground textures (the setting, or the game's rule).</summary>
    public bool SpecularGround => WoWData.Core.Maps.GroundTextures.SpecularOn(Settings.GroundTextures, GameSpecular);

    /// <summary>Whether the game (or your workspace) has a file.</summary>
    public bool FileExists(string path) => Data?.Exists(path) == true || Workspace?.IsModified(path) == true || (Data == null && Files?.Exists(path) == true);

    /// <summary>The ground texture pair (base + "_s") of a texture, or null when it is not a ground texture.</summary>
    public WoWData.Core.Maps.GroundTexturePair? GroundPair(string path) =>
        Index != null ? Index.GroundPair(path) : WoWData.Core.Maps.GroundTextures.PairOf(path, FileExists);

    /// <summary>One sentence for the UI: which ground files the game draws with the user's settings.</summary>
    public string GroundRuleText
    {
        get
        {
            var g = GameSpecular;
            return g.UsesSpecularTextures
                ? $"Your game draws the _s ground textures ({g.Describe()})."
                : $"Your game draws the base ground textures, not the _s ones ({g.Describe()}).";
        }
    }

    // ------------------------------------------------------------------ 0.8.0: shared by the app and the command line

    /// <summary>0.6.1 (moved to Core in 0.8.0): the advice for a texture: which ground file the game draws with the
    /// user's settings, and the additive / multiply note when models draw it that way.</summary>
    public List<string> TextureAdvice(string path)
    {
        var res = new List<string>();
        try
        {
            if (GroundPair(path) is { } pair)
            {
                var g = GameSpecular;
                res.Add($"With {g.Describe()}, the game draws {System.IO.Path.GetFileName(pair.Drawn(g.UsesSpecularTextures))}.");
                if (pair.IsSpecular(path)) res.Add(WoWData.Core.Maps.GroundTextures.SpecularAlphaNote);
            }
            if (GlowNote(path) is { } glow) res.Add(glow);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { }
        return res;
    }

    /// <summary>The additive / multiply note of a texture, from the blend modes of every model that draws it.</summary>
    public string? GlowNote(string path)
    {
        var index = Index;
        if (index == null) return null;
        var models = index.ModelsDrawingTexture(path);
        if (models.Count == 0) return null;
        var uses = MaterialBlends.ForTexture(path, models, p => ReadCurrent(p), 40);
        return MaterialBlends.AdditiveNote(uses);
    }

    private Dictionary<string, IndexEntry>? _byKey;

    /// <summary>The entry of the Browse list with this key ("creature:3098", "file:Creature\Boar\Boar.m2"...), or null.</summary>
    public IndexEntry? FindEntry(string key)
    {
        var map = _byKey;
        if (map == null || map.Count == 0 && Entries.Count > 0)
        {
            map = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries) map.TryAdd(e.Key, e);
            _byKey = map;
        }
        return map.GetValueOrDefault(key);
    }

    /// <summary>The world server's data folder (the setting, else Server\data next to the game folder).</summary>
    public string ServerDataDir => WoWData.Core.Maps.ServerMapsService.DefaultServerData(Settings, Client?.ClientDir ?? Settings.ClientDir ?? throw new InvalidOperationException("No game folder is open."));

    /// <summary>"Copy to server" for the open game folder: the server's dbc folder from the settings, backups in the app's folder.</summary>
    public WoWData.Core.Dbc.DbcServerCopy? DbcServerCopy(Func<List<string>>? running = null)
    {
        var client = Client;
        if (client == null) return null;
        return new WoWData.Core.Dbc.DbcServerCopy(WoWData.Core.Dbc.DbcServerCopy.DefaultServerDbcDir(ServerDataDir), Path.Combine(client.BackupDir, "server"), client.ServerDbcHistoryFile, running);
    }

    private WoWData.Core.ServerData.ServerDataService? _serverData;

    /// <summary>The world server's folder (with mangosd.exe and mangosd.conf): the parent of the server data folder.</summary>
    public string? ServerFolder
    {
        get
        {
            try
            {
                var data = ServerDataDir;
                var parent = Path.GetDirectoryName(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar));
                return parent != null && Directory.Exists(parent) ? parent : null;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { return null; }
        }
    }

    /// <summary>0.9.0: the server's world database for the Server data tab and "wds server" (made on first use; again
    /// after the settings change: <see cref="ResetServerData"/>).</summary>
    public WoWData.Core.ServerData.ServerDataService? ServerData
    {
        get
        {
            lock (_mapsGate)
            {
                if (_serverData == null && Client != null)
                    _serverData = new WoWData.Core.ServerData.ServerDataService(Settings.Server, Workspace, Dbc, Settings, ServerFolder, Client.Dir, ServerRunningProbe);
                return _serverData;
            }
        }
    }

    /// <summary>Tests and screenshots: who says whether mangosd runs (default: the real process list).</summary>
    public Func<List<string>>? ServerRunningProbe { get; set; }

    /// <summary>Forget the server database connection (after its settings changed).</summary>
    public void ResetServerData()
    {
        lock (_mapsGate) _serverData = null;
    }

    private WoWData.Core.Usage.UsageService? _usage;
    private AssetIndex? _usageFor;

    /// <summary>"Used in game" reports for the open index (recreated when the index is reopened).</summary>
    public WoWData.Core.Usage.UsageService? Usage
    {
        get
        {
            lock (_mapsGate)
            {
                if (Index == null) return null;
                if (_usage == null || !ReferenceEquals(_usageFor, Index))
                {
                    _usage?.Dispose();
                    _usage = new WoWData.Core.Usage.UsageService(Index.Path);
                    _usageFor = Index;
                }
                return _usage;
            }
        }
    }

    private WoWData.Core.Usage.AnimationUsage? _animUsage;

    /// <summary>0.10.0: what the game plays each animation for (emotes, spells, the client's own situations).</summary>
    public WoWData.Core.Usage.AnimationUsage? AnimationUsage
    {
        get
        {
            lock (_mapsGate)
            {
                if (Catalog == null || Models == null) return null;
                return _animUsage ??= new WoWData.Core.Usage.AnimationUsage(Catalog, Models.Animations);
            }
        }
    }

    /// <summary>
    /// 0.10.0: the "Used in game" report of an entry (the app's panel and "wds find used"), with the animations of its
    /// model and what the game plays them for.
    /// </summary>
    public WoWData.Core.Usage.UsageReport? UsageReport(string key)
    {
        // another version: what its client says, and its server provider's note (no u_ tables, no server)
        if (IsReadOnly) return Index == null ? null : WoWData.Core.Usage.ModernUsage.Report(this, key);
        var rep = Usage?.Report(key);
        if (rep == null) return null;
        try
        {
            var model = ModelOfEntry(key);
            var data = model == null ? null : ReadCurrent(model);
            if (data != null && AnimationUsage is { } au)
            {
                var m = M2.M2Model.Load(data);
                if (m.Sequences.Length > 1 || m.Bones.Length > 1) au.AddSection(rep, m);
            }
        }
        catch (Exception e) when (e is M2.M2Exception or IOException or InvalidOperationException or KeyNotFoundException) { }
        return rep;
    }

    /// <summary>The .m2 of an entry (creature, display, character, item, game object, model file), or null.</summary>
    public string? ModelOfEntry(string key)
    {
        if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return key.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ? key[5..] : null;
        var e = FindEntry(key);
        if (e == null || Index == null) return null;
        if (e.Path != null && e.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) return e.Path;
        return (IsReadOnly ? Details(e) : Index.Details(e, Catalog)).Models.FirstOrDefault(x => x.EndsWith(".m2", StringComparison.OrdinalIgnoreCase));
    }

    public bool ServerConnected { get; private set; }
    public string ServerMessage { get; private set; } = "";
    public string IndexBuilt { get; private set; } = "";

    public AppSession(AppPaths paths, AppSettings settings)
    {
        Paths = paths;
        Settings = settings;
    }

    public bool IsOpen => Index != null;

    public static readonly string[] StageNames =
    {
        "Reading the archive file lists", "Reading creature displays", "Reading game object displays", "Reading item displays",
        "Reading character races and textures", "Reading spell effects", "Listing world objects and textures",
        "Reading zones, factions and spells", "Mapping world placements and zones",
        "Loading names from the server database", "Linking spawns, loot, vendors and quests", "Saving the index",
    };

    /// <summary>Open a client folder: archives, then the cached index (built first when missing or out of date).</summary>
    public async Task OpenAsync(string clientDir, bool forceRebuild, IProgress<BuildProgress>? progress, CancellationToken ct = default)
    {
        CloseClient();
        var cp = Paths.ForClient(clientDir);
        Directory.CreateDirectory(cp.Dir);
        var data = await Task.Run(() => new ClientData(cp.DataDir), ct);
        try
        {
            bool current = !forceRebuild && await Task.Run(() => AssetIndex.IsCurrent(cp.IndexFile, data), ct);
            if (!current) await IndexBuilder.BuildAsync(data, cp.IndexFile, Settings.Server, progress, ct);
            var index = new AssetIndex(cp.IndexFile);
            var entries = await Task.Run(index.LoadEntries, ct);
            Client = cp;
            Data = data;
            Catalog = new Catalog(data);
            Index = index;
            Entries = entries;
            _byKey = null;
            Workspace = new WoWData.Core.Workspace.Workspace(cp);
            Textures = new TextureService(data, Workspace);
            var ws = Workspace;
            Models = new ModelResolver(Catalog, p => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? data.Read(p));
            ModelPackages = new WoWData.Core.ModelIO.ModelPackageService(data, Catalog, index, ws, Models, Textures);
            ModelSource = new ModelSource112(p => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? data.Read(p));
            Profile = WoWData.Core.Versions.GameProfile.Own112(clientDir);
            Server = new WoWData.Core.Servers.Mangos112ServerProvider(() => ServerConnected, () => ServerMessage);
            ReadMeta();
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    /// <summary>The stages of the organizing screen for another version (<see cref="ModernIndexBuilder.StageNames"/>).</summary>
    public static IReadOnlyList<string> OtherStageNames => ModernIndexBuilder.StageNames;

    /// <summary>
    /// M1: opens another version read only (a modern CASC client; design section 2.3): the client, its index (built by
    /// <see cref="ModernIndexBuilder"/> when missing or out of date, in the app's folder of that client: only the index,
    /// never a workspace), its file names and display chains. The 1.12 open path (<see cref="OpenAsync"/>) is not used.
    /// </summary>
    public async Task OpenOtherAsync(WoWData.Core.Versions.ClientCopy copy, OtherOpenOptions opts, IProgress<BuildProgress>? progress, CancellationToken ct = default)
    {
        CloseClient();
        if (copy.Format != WoWData.Core.Versions.ClientFormat.Casc)
            throw new NotSupportedException($"{copy.Name} ({copy.Label}) stores its data in MPQ archives: opening it like your game comes later. The Other versions tab reads its files and tables.");
        var oc = await Task.Run(() => WoWData.Core.Versions.OtherClient.Open(copy), ct);
        WoWData.Core.Casc.CascStorage? era = null;
        try
        {
            var storage = oc.Casc!;
            // an executable without a version resource: the version and build of the CASC build (.build.info)
            if (copy.Build == 0 && storage.Build > 0 && storage.Version.LastIndexOf('.') is > 0 and var dot)
                copy = copy with { Version = storage.Version[..dot], Build = storage.Build };
            var cat = await Task.Run(() => new Catalog160(storage, oc.Catalog, copy.Build), ct);
            var eraCopy = WoWData.Core.Maps.Conversion.ConvertNames.FindEra(copy, opts.Found);
            if (eraCopy != null)
            {
                try { era = WoWData.Core.Casc.CascStorage.Open(eraCopy.Folder); }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or WoWData.Core.Casc.CascMissingException) { era = null; }
            }
            var names112 = opts.Client112?.Names().Values.Select(v => v.Path).ToList();
            var mopts = new ModernIndexOptions
            {
                Listfile = string.IsNullOrWhiteSpace(opts.Listfile) ? null : opts.Listfile,
                Names112 = names112,
                Names112Fingerprint = opts.Client112 == null ? "" : $"{names112!.Count} {opts.Client112.Fingerprint()}",
                Era = era,
                MaxFileEntries = opts.MaxFileEntries,
            };
            var cp = Paths.ForClient(copy.Folder);
            Directory.CreateDirectory(cp.Dir);
            var fingerprint = await Task.Run(() => ModernIndexBuilder.Fingerprint(storage, mopts), ct);
            bool current = !opts.ForceRebuild && await Task.Run(() => ModernIndexBuilder.IsCurrent(cp.IndexFile, fingerprint), ct);
            if (!current)
            {
                await ModernIndexBuilder.BuildAsync(storage, cat, cp.IndexFile, mopts, progress, ct);
                // the build read whole tables; the session needs none of them (details read the index and the models)
                cat = new Catalog160(storage, oc.Catalog, copy.Build);
            }
            var index = new AssetIndex(cp.IndexFile);
            var entries = await Task.Run(index.LoadEntries, ct);
            var anims = WoWData.Core.M2.Modern.ModernModelResolver.Animations112(opts.Client112);
            Other = oc;
            Displays = cat;
            Files = new WoWData.Core.Versions.ModernClientFiles(storage, index.ModernNames(storage));
            ModernLooks = new WoWData.Core.M2.Modern.ModernModelResolver(cat, Files, anims);
            ModelSource = new WoWData.Core.M2.Modern.ModernModelSource(Files);
            OtherPaths = cp;
            Index = index;
            Entries = entries;
            _byKey = null;
            Profile = WoWData.Core.Versions.GameProfile.ReadOnly(copy);
            Server = WoWData.Core.Servers.ServerProviders.ForOther(Profile);
            ReadMeta();
            // the index of another version never has server names: what the server provider says counts
            ServerConnected = Server.IsConnected;
            ServerMessage = Server.Detail;
        }
        catch
        {
            oc.Dispose();
            throw;
        }
        finally
        {
            era?.Dispose();
        }
    }

    private void ReadMeta()
    {
        var m = Index!.Meta();
        ServerConnected = m.GetValueOrDefault("server") == "True";
        ServerMessage = m.GetValueOrDefault("server_message") ?? "";
        IndexBuilt = m.GetValueOrDefault("built") ?? "";
    }

    /// <summary>Reload the server names into the existing index (after settings changed or the server started).</summary>
    public async Task<(bool Ok, string Message)> RefreshServerAsync(CancellationToken ct = default)
    {
        if (IsReadOnly && Server != null) return (false, Server.Detail);
        if (Client == null || Index == null) return (false, "No game folder is open.");
        Index.Dispose();
        Index = null;
        (bool Ok, string Message) r;
        try
        {
            r = await IndexBuilder.RefreshServerAsync(Client.IndexFile, Settings.Server, ct);
        }
        finally
        {
            Index = new AssetIndex(Client.IndexFile);
            if (Data != null && Catalog != null && Workspace != null && Models != null && Textures != null)
                ModelPackages = new WoWData.Core.ModelIO.ModelPackageService(Data, Catalog, Index, Workspace, Models, Textures);
            Entries = await Task.Run(Index.LoadEntries, ct);
            _byKey = null;
            ReadMeta();
        }
        return r;
    }

    private void CloseClient()
    {
        Index?.Dispose();
        Data?.Dispose();
        Index = null;
        Data = null;
        Catalog = null;
        Workspace = null;
        Textures = null;
        Models = null;
        ModelPackages = null;
        Other?.Dispose();
        Other = null;
        Files = null;
        Displays = null;
        OtherPaths = null;
        ModernLooks = null;
        ModelSource = null;
        Profile = null;
        Server = null;
        lock (_mapsGate)
        {
            _maps = null;
            _maps160 = null;
            _dbc = null;
            _serverData = null;
            _usage?.Dispose();
            _usage = null;
            _usageFor = null;
        }
        Entries = new();
        _byKey = null;
    }

    public void Dispose() => CloseClient();
}

/// <summary>What <see cref="AppSession.OpenOtherAsync"/> takes besides the client (all optional).</summary>
public sealed class OtherOpenOptions
{
    /// <summary>A listfile the user chose (setting other-clients.listfile).</summary>
    public string? Listfile { get; init; }
    /// <summary>The user's 1.12 game (its file names name the other version's files, design 2.6).</summary>
    public ClientData? Client112 { get; init; }
    /// <summary>The clients found in the folder of other versions (to find a Classic Era copy).</summary>
    public IReadOnlyList<WoWData.Core.Versions.ClientCopy>? Found { get; init; }
    public bool ForceRebuild { get; init; }
    public int MaxFileEntries { get; init; } = ModernIndexBuilder.DefaultMaxFileEntries;
}
