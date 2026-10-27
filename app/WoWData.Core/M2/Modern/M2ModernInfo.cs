namespace WoWData.Core.M2.Modern;

/// <summary>One chunk of a chunked (MD21) model: its magic, size, and what the reader did with it.</summary>
public sealed record M2ChunkInfo(string Magic, int Size, bool Used, string Meaning);

/// <summary>An external animation of a model (AFID): animation id, sub id (variation) and the .anim file.</summary>
public sealed record M2AnimFile(int AnimId, int SubId, uint File);

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md (3.3): what <see cref="M2ModernReader"/> read from a chunked
/// model and what it left out: its chunks (used, or ignored with their meaning), the skin, texture, animation and
/// other files it names by FileDataID, its sequences (inline, external, alias), the parts not shown and what was drawn
/// approximately. M3: the skeleton file and its parent, the external animations (<see cref="Animations"/>, loaded when
/// a sequence is chosen) and the effects (<see cref="Effects"/>). Shown by the model statistics and "wds models info
/// --version".
/// </summary>
public sealed class M2ModernInfo
{
    public uint Version { get; init; }
    public List<M2ChunkInfo> Chunks { get; } = new();
    /// <summary>The header's skin profile count (the views; SFID lists them first, then the LOD skins).</summary>
    public int SkinProfiles { get; init; }
    /// <summary>SFID: the .skin files, views first.</summary>
    public uint[] SkinFiles { get; init; } = System.Array.Empty<uint>();
    /// <summary>TXID: one FileDataID per texture record (0 for the textures the game chooses at run time).</summary>
    public uint[] TextureFiles { get; init; } = System.Array.Empty<uint>();
    /// <summary>AFID: the external animations (.anim files) of the model, or of its skeleton when it has one.</summary>
    public List<M2AnimFile> AnimFiles { get; } = new();
    /// <summary>SKID: the skeleton file (.skel), 0 when none.</summary>
    public uint SkeletonFile { get; set; }
    /// <summary>SKPD of the skeleton: its parent skeleton file, 0 when none.</summary>
    public uint SkeletonParentFile { get; set; }
    /// <summary>True when the skeleton (and the parent, when it has one) was read and gave the bones and sequences.</summary>
    public bool SkeletonRead { get; set; }
    /// <summary>The sequences the parent skeleton adds (those the child skeleton does not have itself).</summary>
    public int ParentSequences { get; set; }
    /// <summary>The views read (from their .skin files).</summary>
    public int ViewsRead { get; set; }
    /// <summary>Triangles of each view read, as its .skin file counts them (indices / 3).</summary>
    public List<int> ViewTriangles { get; } = new();
    public int InlineSequences { get; set; }
    public int ExternalSequences { get; set; }
    public int AliasSequences { get; set; }
    /// <summary>The blend mode of each material as the file has it (the 1.12-shaped materials hold the nearest 1.12
    /// mode; M3: the 3D view draws mode 7 with the modern factors).</summary>
    public int[] MaterialBlends { get; set; } = System.Array.Empty<int>();
    /// <summary>M3: the texture units whose second layer the 3D view combines with the first (a reflection or glow; the
    /// operation, <see cref="M2ModernReader.LayerOp"/>); the others draw their layers as the 1.12 renderer does.</summary>
    public Dictionary<M2TexUnit, int> LayerOps { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>M3: the external animations, loaded when a sequence is chosen; null without external sequences.</summary>
    public ModernAnimations? Animations { get; internal set; }
    /// <summary>M3: the particle emitters and ribbons (drawn by the 3D view of another version); null when none.</summary>
    public M2EffectSet? Effects { get; internal set; }
    /// <summary>Chunks and parts left out, in words ("PFID: physics file (.phys): not used for viewing").</summary>
    public List<string> Ignored { get; } = new();
    /// <summary>Parts of the model that are read but not drawn ("2 lights: not drawn by the viewer").</summary>
    public List<string> NotShown { get; } = new();
    /// <summary>What is drawn approximately ("blend mode 7 (blend add) drawn with ... (2 materials)").</summary>
    public List<string> Approximated { get; } = new();
    /// <summary>Other remarks: a skin that could not be read, a bad array...</summary>
    public List<string> Notes { get; } = new();

    /// <summary>"M2 version 274 (MD21, 1 view of 4 read)".</summary>
    public string FormatText => $"M2 version {Version} (chunked MD21, {ViewsRead} of {SkinProfiles} view{(SkinProfiles == 1 ? "" : "s")} read)";

    /// <summary>
    /// What a chunk is (FORMAT KNOWLEDGE, our own words) and whether the reader uses it. Unknown chunks are listed as
    /// "not known".
    /// </summary>
    public static (bool Used, string Meaning) Describe(string magic) => magic switch
    {
        "MD21" => (true, "the model (MD20 header and arrays)"),
        "SFID" => (true, "skin files by FileDataID (views, then LOD skins)"),
        "TXID" => (true, "texture files by FileDataID"),
        "AFID" => (true, "external animations (.anim files), read when an animation is chosen"),
        "SKID" => (true, "skeleton file (.skel): bones, animations and attachment points"),
        "BFID" => (false, "bone files (.bone): not used for viewing"),
        "PFID" => (false, "physics file (.phys): not used for viewing"),
        "LDV1" => (false, "level-of-detail data: not used for viewing"),
        "TXAC" => (false, "extra texture data: not used for viewing"),
        "EXP2" => (false, "particle emitter extension: not used (the emitters are drawn from their records)"),
        "PABC" or "PADC" or "PSBC" or "PEDC" or "PGD1" or "PFDC" => (false, "particle data: not used (the emitters are drawn from their records)"),
        "EDGF" => (false, "edge fade data: not used for viewing"),
        "DBOC" or "NERF" or "DETL" or "RPID" or "GPID" or "AFRA" or "WFV1" or "WFV2" or "WFV3" or "DPIV" or "TEXL" or "PCOL" or "EXPT" => (false, "not used for viewing"),
        _ => (false, "not known: not used"),
    };
}
