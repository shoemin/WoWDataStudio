using System.Text;

namespace WoWData.Core.Maps.Neutral;

/// <summary>What a writer reduced or dropped because its target version cannot hold it.</summary>
public enum LossKind
{
    /// <summary>A chunk had more texture layers than the target allows; the weakest were dropped and the rest renormalised.</summary>
    TextureLayersDropped,
    /// <summary>8-bit alpha values that the target's 4 bits cannot hold exactly (values other than 0 and 16n + 15).</summary>
    AlphaReduced,
    /// <summary>Fine (8 x 8) holes that are not whole 2 x 2 blocks: dropped (the ground stays closed there).</summary>
    HolesReduced,
    /// <summary>MCCV vertex colours dropped.</summary>
    VertexColorsDropped,
    /// <summary>MCLV vertex lighting dropped.</summary>
    VertexLightingDropped,
    /// <summary>Liquid instances that did not fit (the target keeps one liquid layer per kind, or one instance per chunk).</summary>
    LiquidInstancesDropped,
    /// <summary>A liquid type ID mapped to one of the target's basic kinds.</summary>
    LiquidTypeMapped,
    /// <summary>A liquid type ID with no mapping: written as river water.</summary>
    LiquidTypeUnmapped,
    /// <summary>An asset without a usable path got a made-up one (World\Converted\&lt;FileDataID&gt;.&lt;ext&gt;).</summary>
    AssetPathMadeUp,
    /// <summary>An asset with neither a path nor a FileDataID.</summary>
    AssetMissing,
    /// <summary>The chunk flags asked for a shadow map but the model has none: flag cleared.</summary>
    ShadowMissing,
    /// <summary>A chunk the model does not have (the target needs all 256): written flat.</summary>
    ChunkMissing,
    /// <summary>Chunk flag bits the target does not know, cleared.</summary>
    ChunkFlagsCleared,
    /// <summary>Texture layer flag bits the target does not know (for example 0x400 cube-map reflection), cleared.</summary>
    LayerFlagsCleared,
    /// <summary>Placement flag bits the target does not know, cleared.</summary>
    PlacementFlagsCleared,
    /// <summary>A WMO scale other than 1 (modern MODF flag 0x4) that the target cannot hold.</summary>
    WmoScaleDropped,
    /// <summary>Sound emitters without the target's record details: written with zeros in those fields.</summary>
    SoundEmittersDefaulted,
    /// <summary>MFBO flight bounds dropped.</summary>
    FlightBoundsDropped,
    /// <summary>MTXF / MTXP texture parameters dropped.</summary>
    TextureParamsDropped,
    /// <summary>The tile's LOD data dropped.</summary>
    LodDropped,
    /// <summary>Chunks the reader kept opaque and the target cannot place, dropped.</summary>
    UnknownChunksDropped,
    /// <summary>Map flags (MPHD) the target does not know, cleared.</summary>
    MapFlagsCleared,
    /// <summary>MPHD / MAID file references dropped (the target finds its files by name).</summary>
    MapFileRefsDropped,
    /// <summary>WDL far hole masks (MAHO) dropped.</summary>
    FarHolesDropped,
    /// <summary>An assumption, not a loss: assets that had only a path got their FileDataID from the name resolver (ground
    /// textures: the <c>_s.blp</c> file, which is what modern tex0 MDID entries name).</summary>
    AssetIdResolved,
    /// <summary>An asset with no known FileDataID got a new local one (listed in the writer's name map).</summary>
    AssetIdAllocated,
    /// <summary>The 1.12 model name lists (MMDX / MWMO order) cannot be stored: modern placements name their model by
    /// FileDataID, so a reader rebuilds the lists in first-use order.</summary>
    ModelListRebuilt,
    /// <summary>MCLQ details with no MH2O counterpart dropped (flow vectors, water flow bytes, irregular tile bytes).</summary>
    LiquidFlowDropped,
    /// <summary>Liquid instances of one chunk had different fishable / deep tiles; MH2O has one mask per chunk (merged).</summary>
    LiquidMasksMerged,
    /// <summary>A liquid without a LiquidType ID got one from its 1.12 kind (default or supplied table).</summary>
    LiquidTypeDefaulted,
    /// <summary>A liquid without a LiquidObject ID: the vertex format number was written in its place (the 3.3.5a meaning
    /// of MH2O's second field; a modern client reads a LiquidObject ID there).</summary>
    LiquidObjectDefaulted,
    /// <summary>The model has no modern obj1 data: written with empty level-of-detail placement lists.</summary>
    Obj1Generated,
    /// <summary>MAID slots left empty (no such file: LOD, map textures).</summary>
    MapFileRefsMissing,
    /// <summary>Alpha maps converted between sequential alphas and blend weights (<see cref="AlphaBlendMode"/>); to weights,
    /// paint hidden under an opaque layer above is lost.</summary>
    AlphaBlendConverted,
    /// <summary>Map conversion: placements whose model or building the target does not have (a newer format, no
    /// downgrade yet), left out.</summary>
    PlacementsLeftOut,
    /// <summary>Map conversion: baked shadows the target cannot use (the ground changed, so the old shadow does not fit;
    /// or the source's own shadow, which the conversion does not take).</summary>
    ShadowsDropped,
    /// <summary>Map conversion: a texture larger than the target loads, made smaller.</summary>
    TextureResized,
    /// <summary>Map conversion: minimap tiles made smaller (1.60's 512 x 512 to 1.12's 256 x 256).</summary>
    MinimapReduced,
    /// <summary>Map conversion: chunks naming an area the source's AreaTable does not list (encrypted rows), written
    /// without an area.</summary>
    AreaUnknown,
}

/// <summary>One reduced or dropped detail. <see cref="Chunk"/> is the chunk index (indexY * 16 + indexX) or null for a
/// tile- or map-wide entry; <see cref="Count"/> how many items (layers, texels, chunks, placements...) it covers.</summary>
public sealed record ConversionLoss(LossKind Kind, string Tile, int? Chunk, int Count, string Message)
{
    public override string ToString() => $"{Kind} {Tile}{(Chunk is { } c ? $" chunk {c}" : "")} x{Count}: {Message}";
}

/// <summary>The losses of a conversion: every detail a writer reduced or dropped, per tile and kind.</summary>
public sealed class ConversionReport
{
    private readonly List<ConversionLoss> _losses = new();
    private readonly object _gate = new();

    public IReadOnlyList<ConversionLoss> Losses
    {
        get { lock (_gate) return _losses.ToList(); }
    }

    public bool IsEmpty
    {
        get { lock (_gate) return _losses.Count == 0; }
    }

    public void Add(LossKind kind, string tile, int? chunk, int count, string message)
    {
        lock (_gate) _losses.Add(new ConversionLoss(kind, tile, chunk, count, message));
    }

    public IEnumerable<ConversionLoss> Of(LossKind kind) => Losses.Where(l => l.Kind == kind);

    /// <summary>Sum of <see cref="ConversionLoss.Count"/> per kind.</summary>
    public SortedDictionary<LossKind, int> Totals()
    {
        var d = new SortedDictionary<LossKind, int>();
        foreach (var l in Losses) d[l.Kind] = d.GetValueOrDefault(l.Kind) + l.Count;
        return d;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var (k, n) in Totals()) sb.AppendLine($"{k}: {n}");
        foreach (var l in Losses) sb.AppendLine(l.ToString());
        return sb.ToString();
    }
}
