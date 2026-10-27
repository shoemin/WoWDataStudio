using WoWData.Core.Imaging;

namespace WoWData.Core.M2;

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md (2.2): what the 3D viewer, its pop-out windows and the compare
/// window ask about a model's looks: textures, the looks of a creature or item display, a model's default look,
/// character looks, animation names. <see cref="ModelResolver"/> (your 1.12 game) implements it unchanged;
/// <see cref="Modern.ModernModelResolver"/> answers for another version opened read only.
/// </summary>
public interface IModelLooks
{
    /// <summary>A texture by name (or #FileDataID for another version), decoded and cached; null when it cannot be read.</summary>
    Imaging.RgbaImage? LoadBlp(string path);
    ModelLook CreatureDisplay(int displayId);
    ModelLook ItemDisplay(int displayId, string? model = null);
    ModelLook DefaultFor(string model);
    ModelLook Character(int race, int sex, CharacterLook look);
    CharacterOptions Options(int race, int sex);
    (int Race, int Sex)? RaceOfModel(string path);
    IReadOnlyDictionary<int, string> AnimationNames { get; }
    string AnimationName(int id);
    AnimationCatalog Animations { get; }
    ImageCache TextureCache { get; }
    void ClearTextureCache();
    /// <summary>The name the app shows for a file path (1.12: the archive's spelling).</summary>
    string Canonical(string path);
}

/// <summary>M2 (design 2.2): reads a model file into the typed <see cref="M2Model"/> the viewer draws.</summary>
public interface IModelSource
{
    /// <summary>The model; throws <see cref="IOException"/> when the file is not there or cannot be read,
    /// <see cref="M2Exception"/> when it is not a model this version's reader understands.</summary>
    M2Model Load(string path);
}

/// <summary>Your 1.12 game's models: the file as the app has it (your workspace version, else the game's), read by
/// <see cref="M2Model.Load"/>.</summary>
public sealed class ModelSource112 : IModelSource
{
    private readonly Func<string, byte[]?> _read;

    public ModelSource112(Func<string, byte[]?> read) => _read = read;

    public M2Model Load(string path) => M2Model.Load(_read(path) ?? throw new IOException($"{path} is not in the game archives"));
}
