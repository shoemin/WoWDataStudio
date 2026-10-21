namespace WoWData.Core.Maps.Neutral;

/// <summary>What <see cref="NeutralCompare"/> leaves out.</summary>
public sealed class NeutralCompareOptions
{
    /// <summary>Compare the model lists only as the set the placements use (a modern file has no lists, so their 1.12
    /// order and unused entries cannot survive it).</summary>
    public bool IgnoreModelListOrder { get; set; }
    /// <summary>Leave out the 1.12-only MCLQ passthrough (flow vectors, flow bytes, tile residue) and the liquid origin.</summary>
    public bool IgnoreMclqPassthrough { get; set; }
    /// <summary>Leave out the source version, the alpha blend mode, MPHD / MAID / LOD / obj1 data and the other
    /// version-specific storage details (chunk flag bits 0x40 / 0x10000 / liquid bits, placement FDID flags, MH2O
    /// attribute presence, liquid IDs).</summary>
    public bool IgnoreStorageDetails { get; set; }
}

/// <summary>Field-by-field comparison of neutral tiles: the list of differences (empty = equal). Assets are equal when
/// <see cref="AssetRef.SameAs"/> says so and both have the same path presence.</summary>
public static class NeutralCompare
{
    public static List<string> Tiles(NeutralTile a, NeutralTile b, NeutralCompareOptions? o = null, int max = 50)
    {
        o ??= new NeutralCompareOptions();
        var d = new List<string>();
        void Diff(string what, object? x, object? y)
        {
            if (d.Count < max) d.Add($"{what}: {x} != {y}");
        }
        void Eq<T>(string what, T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y)) Diff(what, x, y);
        }
        void Seq<T>(string what, IEnumerable<T>? x, IEnumerable<T>? y)
        {
            if (x == null && y == null) return;
            if (x == null || y == null) { Diff(what, x == null ? "null" : "set", y == null ? "null" : "set"); return; }
            if (!x.SequenceEqual(y)) Diff(what, "differs", "");
        }
        void Asset(string what, AssetRef? x, AssetRef? y)
        {
            if (x == null && y == null) return;
            if (x == null || y == null || !x.SameAs(y) || x.HasPath != y.HasPath ||
                (x.HasPath && !string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase))) Diff(what, x, y);
        }
        Eq("map", a.Map, b.Map);
        Eq("x", a.X, b.X);
        Eq("y", a.Y, b.Y);
        Eq("version", a.Version, b.Version);
        if (!o.IgnoreStorageDetails)
        {
            Eq("source", a.Source, b.Source);
            Eq("alpha blend", a.AlphaBlend, b.AlphaBlend);
            Eq("header flags", a.HeaderFlags, b.HeaderFlags);
            Seq("lod", a.LodBlob, b.LodBlob);
            Seq("obj1", a.Obj1Blob, b.Obj1Blob);
        }
        Eq("texture count", a.Textures.Count, b.Textures.Count);
        for (int i = 0; i < Math.Min(a.Textures.Count, b.Textures.Count); i++) Asset($"texture {i}", a.Textures[i], b.Textures[i]);
        if (!o.IgnoreModelListOrder)
        {
            Eq("doodad model count", a.DoodadModels.Count, b.DoodadModels.Count);
            for (int i = 0; i < Math.Min(a.DoodadModels.Count, b.DoodadModels.Count); i++) Asset($"doodad model {i}", a.DoodadModels[i], b.DoodadModels[i]);
            Eq("WMO model count", a.WmoModels.Count, b.WmoModels.Count);
            for (int i = 0; i < Math.Min(a.WmoModels.Count, b.WmoModels.Count); i++) Asset($"WMO model {i}", a.WmoModels[i], b.WmoModels[i]);
        }
        Eq("doodads", a.Doodads.Count, b.Doodads.Count);
        for (int i = 0; i < Math.Min(a.Doodads.Count, b.Doodads.Count); i++)
        {
            var x = a.Doodads[i];
            var y = b.Doodads[i];
            Asset($"doodad {i} model", x.Model, y.Model);
            Eq($"doodad {i}", (x.UniqueId, x.Position, x.Rotation, x.ScaleRaw), (y.UniqueId, y.Position, y.Rotation, y.ScaleRaw));
            if (!o.IgnoreStorageDetails) Eq($"doodad {i} flags", x.Flags, y.Flags);
        }
        Eq("WMOs", a.Wmos.Count, b.Wmos.Count);
        for (int i = 0; i < Math.Min(a.Wmos.Count, b.Wmos.Count); i++)
        {
            var x = a.Wmos[i];
            var y = b.Wmos[i];
            Asset($"WMO {i} model", x.Model, y.Model);
            Eq($"WMO {i}", (x.UniqueId, x.Position, x.Rotation, x.ExtentMin, x.ExtentMax, x.DoodadSet, x.NameSet), (y.UniqueId, y.Position, y.Rotation, y.ExtentMin, y.ExtentMax, y.DoodadSet, y.NameSet));
            if (!o.IgnoreStorageDetails) Eq($"WMO {i} flags/scale", (x.Flags, x.ScaleRaw), (y.Flags, y.ScaleRaw));
        }
        Seq("flight bounds", a.FlightBounds, b.FlightBounds);
        Eq("texture params", a.TextureParams.Count, b.TextureParams.Count);
        for (int i = 0; i < Math.Min(a.TextureParams.Count, b.TextureParams.Count); i++)
        {
            var x = a.TextureParams[i];
            var y = b.TextureParams[i];
            Eq($"texture params {i}", (x.Flags, x.HeightScale, x.HeightOffset), (y.Flags, y.HeightScale, y.HeightOffset));
            Asset($"height texture {i}", x.HeightTexture, y.HeightTexture);
        }
        Eq("extra chunks", string.Join(" ", a.ExtraChunks.Select(c => c.Magic + "@" + c.Location)), string.Join(" ", b.ExtraChunks.Select(c => c.Magic + "@" + c.Location)));
        for (int i = 0; i < Math.Min(a.ExtraChunks.Count, b.ExtraChunks.Count); i++) Seq($"extra chunk {i}", a.ExtraChunks[i].Data, b.ExtraChunks[i].Data);
        for (int c = 0; c < 256; c++) Chunk(c, a.Chunks[c], b.Chunks[c], o, Diff, Asset);
        return d;
    }

    private static void Chunk(int c, NeutralChunk? x, NeutralChunk? y, NeutralCompareOptions o, Action<string, object?, object?> diff,
        Action<string, AssetRef?, AssetRef?> asset)
    {
        void Eq<T>(string what, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b)) diff($"chunk {c} {what}", a, b);
        }
        void Seq<T>(string what, IEnumerable<T>? a, IEnumerable<T>? b)
        {
            if (a == null && b == null) return;
            if (a == null || b == null) { diff($"chunk {c} {what}", a == null ? "null" : "set", b == null ? "null" : "set"); return; }
            if (!a.SequenceEqual(b)) diff($"chunk {c} {what}", "differs", "");
        }
        if (x == null || y == null)
        {
            if (x != y) diff($"chunk {c}", x == null ? "null" : "set", y == null ? "null" : "set");
            return;
        }
        uint storage = o.IgnoreStorageDetails ? 0x40u | 0x10000 | 0x4 | 0x8 | 0x10 | 0x20 : 0;
        Eq("flags", x.Flags & ~storage, y.Flags & ~storage);
        Eq("index", (x.IndexX, x.IndexY), (y.IndexX, y.IndexY));
        Eq("area", x.AreaId, y.AreaId);
        Eq("position", x.Position, y.Position);
        Seq("heights", x.Heights, y.Heights);
        Seq("normals", x.Normals, y.Normals);
        Seq("normal pad", x.NormalPad112, y.NormalPad112);
        Eq("holes", x.Holes, y.Holes);
        Seq("low-quality map", x.LowQualityTextureMap, y.LowQualityTextureMap);
        Eq("no-effect bits", x.NoEffectDoodad, y.NoEffectDoodad);
        Eq("layers", x.Layers.Count, y.Layers.Count);
        for (int L = 0; L < Math.Min(x.Layers.Count, y.Layers.Count); L++)
        {
            var a = x.Layers[L];
            var b = y.Layers[L];
            asset($"chunk {c} layer {L} texture", a.Texture, b.Texture);
            uint lf = o.IgnoreStorageDetails ? 0x200u : 0;
            Eq($"layer {L}", (a.Flags & ~lf, a.EffectId), (b.Flags & ~lf, b.EffectId));
            Seq($"layer {L} alpha", a.Alpha, b.Alpha);
        }
        Seq("shadow", x.Shadow, y.Shadow);
        Seq("vertex colours", x.VertexColors, y.VertexColors);
        Seq("vertex lighting", x.VertexLighting, y.VertexLighting);
        Seq("doodad refs", x.DoodadRefs, y.DoodadRefs);
        Seq("WMO refs", x.WmoRefs, y.WmoRefs);
        if (!o.IgnoreStorageDetails) Eq("MH2O attributes", x.Mh2oAttributes160, y.Mh2oAttributes160);
        Eq("sound emitters", x.SoundEmitters.Count, y.SoundEmitters.Count);
        for (int k = 0; k < Math.Min(x.SoundEmitters.Count, y.SoundEmitters.Count); k++)
        {
            var a = x.SoundEmitters[k];
            var b = y.SoundEmitters[k];
            Eq($"emitter {k}", (a.Id, a.Position, a.Size), (b.Id, b.Position, b.Size));
            Seq($"emitter {k} raw", a.Raw112, b.Raw112);
        }
        Eq("extra sub-chunks", string.Join(" ", x.ExtraSubChunks.Select(s => s.Magic + "@" + s.Location)), string.Join(" ", y.ExtraSubChunks.Select(s => s.Magic + "@" + s.Location)));
        for (int k = 0; k < Math.Min(x.ExtraSubChunks.Count, y.ExtraSubChunks.Count); k++) Seq($"extra sub-chunk {k}", x.ExtraSubChunks[k].Data, y.ExtraSubChunks[k].Data);
        Eq("liquids", x.Liquids.Count, y.Liquids.Count);
        for (int k = 0; k < Math.Min(x.Liquids.Count, y.Liquids.Count); k++)
        {
            var a = x.Liquids[k];
            var b = y.Liquids[k];
            if (!o.IgnoreStorageDetails)
                Eq($"liquid {k} ids", (a.Origin, a.Kind, a.LiquidType, a.LiquidObjectOrVertexFormat), (b.Origin, b.Kind, b.LiquidType, b.LiquidObjectOrVertexFormat));
            Eq($"liquid {k}", (a.Min, a.Max, a.RectX, a.RectY, a.RectW, a.RectH, a.Exists, a.Fishable, a.Deep), (b.Min, b.Max, b.RectX, b.RectY, b.RectW, b.RectH, b.Exists, b.Fishable, b.Deep));
            Seq($"liquid {k} heights", a.Heights, b.Heights);
            Seq($"liquid {k} depth", a.Depth, b.Depth);
            Seq($"liquid {k} u", a.TexU, b.TexU);
            Seq($"liquid {k} v", a.TexV, b.TexV);
            if (!o.IgnoreMclqPassthrough)
            {
                Seq($"liquid {k} flow", a.MclqFlow112, b.MclqFlow112);
                Seq($"liquid {k} tile residue", a.MclqTileResidue112, b.MclqTileResidue112);
                Seq($"liquid {k} flow vectors", a.MclqFlowVectors112, b.MclqFlowVectors112);
            }
        }
    }
}
