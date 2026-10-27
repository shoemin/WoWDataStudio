namespace WoWData.Core.M2.Modern;

/// <summary>One chunk of a chunked (MD21) model: its magic, size, and what the reader did with it.</summary>
public sealed record M2ChunkInfo(string Magic, int Size, bool Used, string Meaning);

/// <summary>An external animation of a model (AFID): animation id, sub id (variation) and the .anim file.</summary>
public sealed record M2AnimFile(int AnimId, int SubId, uint File);

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md (3.3): what <see cref="M2ModernReader"/> read from a chunked
/// model and what it left out: its chunks (used, or ignored with their meaning), the skin, texture, animation and
/// other files it names by FileDataID, its sequences (inline, external, alias), the parts not shown yet (M3) and what
/// was drawn approximately (blend modes 1.12 does not have). Shown by the model statistics and "wds models info
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
    /// <summary>AFID: the external animations (.anim files; M3).</summary>
    public List<M2AnimFile> AnimFiles { get; } = new();
    /// <summary>SKID: the skeleton file (.skel; M3), 0 when none.</summary>
    public uint SkeletonFile { get; set; }
    /// <summary>The views read (from their .skin files).</summary>
    public int ViewsRead { get; set; }
    /// <summary>Triangles of each view read, as its .skin file counts them (indices / 3).</summary>
    public List<int> ViewTriangles { get; } = new();
    public int InlineSequences { get; set; }
    public int ExternalSequences { get; set; }
    public int AliasSequences { get; set; }
    /// <summary>Chunks and parts left out, in words ("AFID: 3 external animations (.anim files): later (M3)").</summary>
    public List<string> Ignored { get; } = new();
    /// <summary>Parts of the model that are read but not converted or drawn yet ("2 particle emitters (M3)").</summary>
    public List<string> NotShown { get; } = new();
    /// <summary>What is drawn approximately ("blend mode 7 drawn as additive (2 materials)").</summary>
    public List<string> Approximated { get; } = new();
    /// <summary>Other remarks: a skin that could not be read, a bad array...</summary>
    public List<string> Notes { get; } = new();

    /// <summary>"M2 version 274 (MD21, 1 view of 4 read)".</summary>
    public string FormatText => $"M2 version {Version} (chunked MD21, {ViewsRead} of {SkinProfiles} view{(SkinProfiles == 1 ? "" : "s")} read)";

    /// <summary>
    /// What a chunk is (FORMAT KNOWLEDGE, our own words) and whether this milestone uses it. Unknown chunks are listed
    /// as "not known".
    /// </summary>
    public static (bool Used, string Meaning) Describe(string magic) => magic switch
    {
        "MD21" => (true, "the model (MD20 header and arrays)"),
        "SFID" => (true, "skin files by FileDataID (views, then LOD skins)"),
        "TXID" => (true, "texture files by FileDataID"),
        "AFID" => (false, "external animations (.anim files): later (M3)"),
        "SKID" => (false, "skeleton file (.skel): later (M3)"),
        "BFID" => (false, "bone files (.bone): not used for viewing"),
        "PFID" => (false, "physics file (.phys): not used for viewing"),
        "LDV1" => (false, "level-of-detail data: not used for viewing"),
        "TXAC" => (false, "extra texture data: not used for viewing"),
        "EXP2" => (false, "particle emitter extension: later (M3)"),
        "PABC" or "PADC" or "PSBC" or "PEDC" or "PGD1" => (false, "particle data: later (M3)"),
        "EDGF" => (false, "edge fade data: not used for viewing"),
        "DBOC" or "NERF" or "DETL" or "RPID" or "GPID" or "AFRA" or "WFV1" or "WFV2" or "WFV3" or "DPIV" => (false, "not used for viewing"),
        _ => (false, "not known: not used"),
    };
}
