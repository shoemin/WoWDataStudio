using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWData.Core.ModelIO;

/// <summary>How "Export for Blender" writes the looks of a model.</summary>
public enum ModelExportMode
{
    /// <summary>One glTF with the look shown in the app (default).</summary>
    CurrentLook,
    /// <summary>One glTF with every skin as a KHR_materials_variants variant (Blender's glTF Variants panel).</summary>
    AllVariants,
    /// <summary>One glTF per skin, all sharing the same mesh and skeleton data.</summary>
    OnePerVariant,
}

/// <summary>
/// wowdata_manifest.json: what an export package contains and where every part came from, so the import can match
/// what comes back (by the ids also stored in the glTF extras / Blender custom properties) and tell what changed.
/// </summary>
public sealed class ModelPackageManifest
{
    public const string FileName = "wowdata_manifest.json";
    public const string FormatName = "wowdata-model-package";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("format_version")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("tool")] public string Tool { get; set; } = "WoW Data Studio";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("model")] public PackageModel Model { get; set; } = new();
    [JsonPropertyName("mode")] public string Mode { get; set; } = "current";
    [JsonPropertyName("gltf_files")] public List<PackageGltfFile> GltfFiles { get; set; } = new();
    [JsonPropertyName("coordinates")] public string Coordinates { get; set; } =
        "glTF (x, y, z) = M2 (x, z, -y); Blender's glTF importer converts back, so Blender coordinates equal the M2's (Z up, faces +X, 1 unit = 1 yard)";
    [JsonPropertyName("submeshes")] public List<PackageSubmesh> Submeshes { get; set; } = new();
    [JsonPropertyName("bones")] public List<PackageBone> Bones { get; set; } = new();
    [JsonPropertyName("materials")] public List<PackageMaterial> Materials { get; set; } = new();
    [JsonPropertyName("textures")] public List<PackageTexture> Textures { get; set; } = new();
    [JsonPropertyName("variants")] public List<PackageVariant> Variants { get; set; } = new();
    [JsonPropertyName("users")] public PackageUsers Users { get; set; } = new();
    /// <summary>0.10.0: the skeleton, animations, attachment points and events for editing (null: a mesh / texture export).</summary>
    [JsonPropertyName("rig")] public PackageRig? Rig { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ModelPackageManifest? FromJson(string text)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ModelPackageManifest>(text);
            return m != null && m.Format == FormatName ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public PackageTexture? Texture(string id) => Textures.FirstOrDefault(t => t.Id == id);
}

public sealed class PackageModel
{
    [JsonPropertyName("archive_path")] public string ArchivePath { get; set; } = "";
    [JsonPropertyName("archive")] public string? Archive { get; set; }
    /// <summary>"game" (the archives) or "workspace" (an earlier edit of yours).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "game";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("game_sha256")] public string? GameSha256 { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("m2_version")] public int M2Version { get; set; }
    [JsonPropertyName("vertices")] public int Vertices { get; set; }
    [JsonPropertyName("triangles")] public int Triangles { get; set; }
    [JsonPropertyName("bones")] public int Bones { get; set; }
    [JsonPropertyName("sequences")] public int Sequences { get; set; }
    [JsonPropertyName("bone_limits")] public List<int> BoneLimits { get; set; } = new();
    /// <summary>SHA-256 of the parts of the model, so the import can say which part a file changed.</summary>
    [JsonPropertyName("checksums")] public Dictionary<string, string> Checksums { get; set; } = new();
    [JsonPropertyName("is_character")] public bool IsCharacter { get; set; }
}

public sealed class PackageGltfFile
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    /// <summary>-1: every variant (or the only look); otherwise the variant this file shows.</summary>
    [JsonPropertyName("variant")] public int Variant { get; set; } = -1;
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

public sealed class PackageSubmesh
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("geoset")] public int Geoset { get; set; }
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("material")] public string Material { get; set; } = "";
    [JsonPropertyName("vertices")] public int Vertices { get; set; }
    [JsonPropertyName("triangles")] public int Triangles { get; set; }
    [JsonPropertyName("texture_units")] public List<int> TextureUnits { get; set; } = new();
    [JsonPropertyName("default_visible")] public bool DefaultVisible { get; set; } = true;
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>Model vertex of each glTF vertex of this mesh (in order).</summary>
    [JsonPropertyName("model_vertices")] public List<int> ModelVertices { get; set; } = new();
}

public sealed class PackageBone
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("key_bone")] public int KeyBone { get; set; } = -1;
    [JsonPropertyName("parent")] public int Parent { get; set; } = -1;
}

public sealed class PackageMaterial
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("submesh")] public int Submesh { get; set; }
    /// <summary>-1: the material of the look in the file (or the only one); otherwise the variant it belongs to.</summary>
    [JsonPropertyName("variant")] public int Variant { get; set; } = -1;
    [JsonPropertyName("texture")] public string? Texture { get; set; }
    [JsonPropertyName("blend")] public int Blend { get; set; }
    [JsonPropertyName("render_flags")] public int RenderFlags { get; set; }
}

public sealed class PackageTextureOriginal
{
    [JsonPropertyName("archive")] public string? Archive { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "game";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("levels")] public int Levels { get; set; }
    [JsonPropertyName("alpha_depth")] public int AlphaDepth { get; set; }
}

public sealed class PackageTexture
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Relative path of the PNG inside the package ("textures/BoarSkinIvory.png").</summary>
    [JsonPropertyName("png")] public string Png { get; set; } = "";
    /// <summary>The game texture this PNG becomes (null for composites and placeholders).</summary>
    [JsonPropertyName("game_path")] public string? GamePath { get; set; }
    /// <summary>"file" (named in the model), "creature skin", "item skin", "hair", "skin extra", "composite", "placeholder".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "file";
    [JsonPropertyName("m2_textures")] public List<int> M2Textures { get; set; } = new();
    [JsonPropertyName("slot")] public int Slot { get; set; }
    [JsonPropertyName("variants")] public List<int> Variants { get; set; } = new();
    [JsonPropertyName("editable")] public bool Editable { get; set; } = true;
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("original")] public PackageTextureOriginal? Original { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    /// <summary>SHA-256 of the RGBA pixels written to the PNG (alpha 255 when the PNG has no alpha channel).</summary>
    [JsonPropertyName("pixel_sha256")] public string PixelSha256 { get; set; } = "";
    [JsonPropertyName("png_sha256")] public string PngSha256 { get; set; } = "";
    [JsonPropertyName("parts")] public List<string>? Parts { get; set; }
}

public sealed class PackageVariant
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"creature display", "item display", "character look", "model".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("ids")] public List<long> Ids { get; set; } = new();
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    /// <summary>M2 texture type (slot) -> texture id.</summary>
    [JsonPropertyName("slots")] public Dictionary<string, string> Slots { get; set; } = new();
}

public sealed class PackageUsers
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("warning")] public string? Warning { get; set; }
}
