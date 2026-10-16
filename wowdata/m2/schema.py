"""Record layouts of the M2 format as the 1.12 client (build 5875) reads it: version 0x100 (256), and 0x101 (257).

Every layout here is checked against our decompile of WoW.exe:
  * M2Shared_FixupHeader @0071cdf0: the header fields, the size of every array element and which record
    fields are themselves arrays (M2Fixup_Views/Colors/Textures/ShortTrackArray_64/_6c/TextureTransforms/
    Attachments/Events/Lights/Cameras/RibbonEmitters/ParticleEmitters @0071e270..0071f210);
  * names/structs.csv: M2Header, M2Sequence, M2Bone, M2Track, M2View, M2Submesh, M2TexUnit, M2Texture,
    M2Attachment, M2Event, M2Color;
  * M2Model_FindTrackKeys @00713d50 / M2Model_EvalQuatTrack @00713ea0 / M2Model_EvalFloatTrack @0071af20 for
    the track semantics (inclusive per-sequence key ranges, 16-byte float quaternions x,y,z,w, linear
    interpolation for any interpolation type != 0);
  * M2Scene_SetBatchBlendState @0070c190 and the code after it for the render flags and blend modes.
Field *names* the decompile does not give come from the public format notes (wowdev.wiki); they are marked
"(wowdev)" below. The byte layout of every record is proven by the round-trip test (all 9,691 .m2 files of the
client re-serialise byte-identically from these layouts).

A record is described as a list of fields:
  ('name', dtype)            a plain value (numpy dtype string; '(3,)<f4' etc. for small vectors)
  Arr('name', elem)          an M2Array {uint32 count, uint32 offset}; elem is a dtype, a Rec or CHAR
  Trk('name', value_dtype)   an M2Track (0x1c bytes): uint16 interpolation, int16 global sequence,
                             M2Array ranges ({first, last} key index per sequence, inclusive),
                             M2Array timestamps (uint32 ms on the model's global timeline), M2Array values
  Trk('name', None)          the 0x14-byte value-less track of M2Event (interpolation, global sequence,
                             ranges, timestamps)
"""
import numpy as np

CHAR = 'char'          # M2Array<char>: kept as bytes (with the terminating NUL when the file has one)


class Arr:
    def __init__(self, name, elem):
        self.name = name
        self.elem = elem


class Trk:
    def __init__(self, name, value):
        self.name = name
        self.value = value


class Rec:
    """A fixed-size record: `dtype` is a numpy structured dtype covering every byte; arrays inside the record
    appear in it as '<name>#n' (count) and '<name>#o' (offset) fields and are listed in `arrays` in file order."""

    def __init__(self, name, size, fields):
        self.name = name
        self.size = size
        self.fields = fields
        names, formats, offsets = [], [], []
        self.arrays = []          # [(array path inside the record, element type)]
        self.tracks = []          # [(track name, value dtype or None)]
        self.plain = []           # plain field names
        pos = 0

        def add(n, f):
            nonlocal pos
            dt = np.dtype(f)
            names.append(n)
            formats.append(dt)
            offsets.append(pos)
            pos += dt.itemsize

        for f in fields:
            if isinstance(f, Arr):
                add(f.name + '#n', '<u4')
                add(f.name + '#o', '<u4')
                self.arrays.append((f.name, f.elem))
            elif isinstance(f, Trk):
                add(f.name + '.interpolation', '<u2')
                add(f.name + '.global_sequence', '<i2')
                for sub, elem in (('ranges', '(2,)<u4'), ('timestamps', '<u4')) + ((('values', f.value),) if f.value is not None else ()):
                    add('%s.%s#n' % (f.name, sub), '<u4')
                    add('%s.%s#o' % (f.name, sub), '<u4')
                    self.arrays.append(('%s.%s' % (f.name, sub), elem))
                self.tracks.append((f.name, f.value))
            else:
                n, fmt = f
                add(n, fmt)
                self.plain.append(n)
        if pos != size:
            raise AssertionError('%s: fields cover %#x bytes, the record is %#x' % (name, pos, size))
        self.dtype = np.dtype({'names': names, 'formats': formats, 'offsets': offsets, 'itemsize': size})

    def __repr__(self):
        return '<Rec %s %#x>' % (self.name, self.size)


V3 = '(3,)<f4'
QUAT = '(4,)<f4'          # x, y, z, w (D3DX order, M2Model_EvalQuatTrack); full floats in 1.12, not compressed
SPLINE_V3 = '(9,)<f4'     # M2SplineKey<C3Vector>: value, in tangent, out tangent (M2Fixup_Array36 = 0x24 bytes)
SPLINE_F = '(3,)<f4'      # M2SplineKey<float>: value, in, out (0x0c bytes)

SEQUENCE = Rec('M2Sequence', 0x44, [
    ('anim_id', '<u2'),            # AnimationData id
    ('sub_id', '<u2'),             # variation index
    ('start', '<u4'),              # start / end timestamps (ms) on the global timeline
    ('end', '<u4'),
    ('move_speed', '<f4'),
    ('flags', '<u4'),              # bit0 clear = time is clamped at the end (M2Model_Update)
    ('frequency', '<i2'),          # weight for random variant selection
    ('pad_16', '<u2'),
    ('replay', '(2,)<u4'),         # (wowdev) replay min / max
    ('blend_time', '<u4'),         # (wowdev) ms
    ('bounds_min', V3),            # per-sequence bounds (M2Scene_PickRay)
    ('bounds_max', V3),
    ('bounds_radius', '<f4'),
    ('next_variant', '<i2'),       # next sequence of the same animation (-1 = end)
    ('next_alias', '<u2'),         # (wowdev)
])

PLAYABLE = Rec('M2PlayableAnim', 4, [('fallback_id', '<i2'), ('flags', '<u2')])

BONE = Rec('M2Bone', 0x6c, [
    ('key_bone_id', '<i4'),        # -1 = none; header key_bone_lookup maps key bone ids back
    ('flags', '<u4'),              # (wowdev) 0x8 spherical billboard, 0x200 transformed, ...
    ('parent', '<i2'),             # -1 = root
    ('submesh_id', '<u2'),
    Trk('translation', V3),
    Trk('rotation', QUAT),
    Trk('scale', V3),
    ('pivot', V3),
])

VERTEX_DTYPE = np.dtype([('position', V3), ('weights', '(4,)u1'), ('bones', '(4,)u1'), ('normal', V3),
                         ('uv', '(2,)<f4'), ('uv2', '(2,)<f4')])     # 0x30 bytes (M2Fixup_Vertices)

SUBMESH = Rec('M2Submesh', 0x20, [
    ('geoset_id', '<u2'),          # mesh part id (decompile reads it as a uint32; the high half is 'level' (wowdev), always 0 in 1.12 files)
    ('level', '<u2'),
    ('vertex_start', '<u2'),       # first entry of view.indices
    ('vertex_count', '<u2'),
    ('index_start', '<u2'),        # first entry of view.triangles
    ('index_count', '<u2'),
    ('bone_count', '<u2'),         # bone palette size (compared with the view bone limit)
    ('bone_start', '<u2'),         # first entry of header bone_lookup
    ('bone_influences', '<u2'),    # max bones per vertex
    ('center_bone', '<u2'),
    ('center', V3),
])

TEXUNIT = Rec('M2TexUnit', 0x18, [
    ('flags', 'u1'),               # (wowdev) 0x10 = static (not animated) texture
    ('priority', 'i1'),            # (wowdev) priority plane
    ('shader_id', '<u2'),          # (wowdev)
    ('submesh', '<u2'),            # index into view.submeshes
    ('geoset_index', '<u2'),       # (wowdev)
    ('color', '<i2'),              # index into colors, -1 = none
    ('render_flags', '<u2'),       # index into render_flags
    ('material_layer', '<u2'),     # (wowdev)
    ('texture_count', '<u2'),      # layers (the shaders support 2)
    ('texture_lookup', '<u2'),     # first index into texture_lookup
    ('tex_unit_lookup', '<u2'),    # index into tex_unit_lookup
    ('transparency_lookup', '<u2'),
    ('texture_transform_lookup', '<u2'),
])

VIEW = Rec('M2View', 0x2c, [
    Arr('indices', '<u2'),         # view vertex -> model vertex
    Arr('triangles', '<u2'),       # 3 per triangle, into indices
    Arr('properties', '(4,)u1'),   # per view vertex: bone palette indices (into bone_lookup from submesh.bone_start)
    Arr('submeshes', SUBMESH),
    Arr('texture_units', TEXUNIT),
    ('bone_limit', '<u4'),         # 256 / 75 / 53 / 21: M2Shared_SelectViewAndBuild picks the largest <= the device limit
])

COLOR = Rec('M2Color', 0x38, [Trk('color', V3), Trk('alpha', '<i2')])                     # alpha: fixed16 (0x7fff = 1)
TEXTURE = Rec('M2Texture', 0x10, [('type', '<u4'), ('flags', '<u4'), Arr('filename', CHAR)])  # flags (wowdev): 1 wrap U, 2 wrap V
TRANSPARENCY = Rec('M2TextureWeight', 0x1c, [Trk('weight', '<i2')])                            # header +0x64
FLIPBOOK = Rec('M2Track6c', 0x1c, [Trk('value', '<u2')])          # header +0x6c: one-track records, 2-byte values; unused in 1.12 files
TEXTURE_TRANSFORM = Rec('M2TextureTransform', 0x54, [Trk('translation', V3), Trk('rotation', QUAT), Trk('scale', V3)])
RENDER_FLAGS = Rec('M2Material', 4, [
    ('flags', '<u2'),      # 0x1 unlit, 0x2 unfogged, 0x4 two-sided (no culling), 0x8 no depth test, 0x10 no depth write
    ('blend', '<u2'),      # 0 opaque, 1 alpha key (ref = alpha*224/255), 2 alpha, 3 no-alpha add, 4 add, 5 mod, 6 mod2x
])

ATTACHMENT = Rec('M2Attachment', 0x30, [
    ('id', '<u4'), ('bone', '<u2'), ('unknown_06', '<u2'), ('position', V3), Trk('visible', 'u1')])

EVENT = Rec('M2Event', 0x2c, [
    ('id', 'S4'),          # four characters, e.g. b'$DSL'
    ('data', '<u4'), ('bone', '<u2'), ('unknown_0a', '<u2'), ('position', V3), Trk('enabled', None)])

LIGHT = Rec('M2Light', 0xd4, [
    ('type', '<u2'), ('bone', '<i2'), ('position', V3),
    Trk('ambient_color', V3), Trk('ambient_intensity', '<f4'), Trk('diffuse_color', V3), Trk('diffuse_intensity', '<f4'),
    Trk('attenuation_start', '<f4'), Trk('attenuation_end', '<f4'), Trk('visibility', 'u1')])

CAMERA = Rec('M2Camera', 0x7c, [
    ('type', '<u4'), ('fov', '<f4'), ('far_clip', '<f4'), ('near_clip', '<f4'),
    Trk('positions', SPLINE_V3), ('position_base', V3), Trk('target_positions', SPLINE_V3), ('target_position_base', V3),
    Trk('roll', SPLINE_F)])

RIBBON = Rec('M2Ribbon', 0xdc, [
    ('id', '<u4'), ('bone', '<u4'), ('position', V3), Arr('textures', '<u2'), Arr('materials', '<u2'),
    Trk('color', V3), Trk('alpha', '<i2'), Trk('height_above', '<f4'), Trk('height_below', '<f4'),
    ('edges_per_second', '<f4'), ('edge_lifetime', '<f4'), ('gravity', '<f4'), ('texture_rows', '<u2'), ('texture_cols', '<u2'),
    Trk('texture_slot', '<u2'), Trk('visibility', 'u1')])

PARTICLE = Rec('M2Particle', 0x1f8, [
    ('id', '<u4'), ('flags', '<u4'), ('position', V3), ('bone', '<u2'), ('texture', '<u2'),
    Arr('geometry_model', CHAR), Arr('recursion_model', CHAR),
    ('blend', '<u2'), ('emitter_type', '<u2'),          # emitter type at +0x2a: 1 plane, 2 sphere, 3 spline
    ('particle_type', 'u1'), ('head_or_tail', 'u1'), ('texture_tile_rotation', '<i2'),
    ('texture_rows', '<u2'), ('texture_cols', '<u2'),
    Trk('emission_speed', '<f4'), Trk('speed_variation', '<f4'), Trk('vertical_range', '<f4'), Trk('horizontal_range', '<f4'),
    Trk('gravity', '<f4'), Trk('lifespan', '<f4'), Trk('emission_rate', '<f4'), Trk('emission_area_length', '<f4'),
    Trk('emission_area_width', '<f4'), Trk('z_source', '<f4'),
    # +0x14c..+0x1d4 (wowdev names; the decompile only sizes this block)
    ('mid_point', '<f4'), ('colors', '(3,)<u4'), ('scales', '(3,)<f4'), ('lifespan_uv_anim', '(3,)<u2'),
    ('decay_uv_anim', '(3,)<u2'), ('tail_uv_anim', '(2,)<i2'), ('tail_decay_uv_anim', '(2,)<i2'),
    ('params_17c', '(22,)<f4'),     # tail length, twinkle, burst, drag, spin, tumble, wind, follow... (not split: unverified)
    Arr('spline_points', V3), Trk('enabled', 'u1')])

_HEADER_FIELDS = [
    ('magic', 'S4'), ('version', '<u4'), Arr('name', CHAR), ('global_flags', '<u4'),
    Arr('global_sequences', '<u4'), Arr('sequences', SEQUENCE), Arr('animation_lookup', '<i2'), Arr('playable_lookup', PLAYABLE),
    Arr('bones', BONE), Arr('key_bone_lookup', '<i2'), Arr('vertices', VERTEX_DTYPE), Arr('views', VIEW),
    Arr('colors', COLOR), Arr('textures', TEXTURE), Arr('transparency', TRANSPARENCY), Arr('track_6c', FLIPBOOK),
    Arr('texture_transforms', TEXTURE_TRANSFORM), Arr('replaceable_lookup', '<i2'), Arr('render_flags', RENDER_FLAGS),
    Arr('bone_lookup', '<u2'), Arr('texture_lookup', '<u2'), Arr('tex_unit_lookup', '<i2'), Arr('transparency_lookup', '<u2'),
    Arr('texture_transform_lookup', '<i2'),
    ('bounds_min', V3), ('bounds_max', V3), ('bounds_radius', '<f4'),
    ('collision_min', V3), ('collision_max', V3), ('collision_radius', '<f4'),
    Arr('collision_triangles', '<u2'), Arr('collision_vertices', V3), Arr('collision_normals', V3),
    Arr('attachments', ATTACHMENT), Arr('attachment_lookup', '<i2'), Arr('events', EVENT), Arr('lights', LIGHT),
    Arr('cameras', CAMERA), Arr('camera_lookup', '<i2'), Arr('ribbons', RIBBON), Arr('particles', PARTICLE),
]
HEADER_256 = Rec('M2Header', 0x144, _HEADER_FIELDS)
# version 0x101: two more uint16 arrays, fixed up only for that version (texCombineLookup2 at +0x148 is read by
# M2Scene_SetBatchTextureStages instead of g_M2BlendTextureStageTable; the second one's use is unknown)
HEADER_257 = Rec('M2Header257', 0x154, _HEADER_FIELDS + [Arr('texture_combiner_combos', '<u2'), Arr('unknown_14c', '<u2')])

TEXTURE_TYPES = {0: 'file', 1: 'body (character skin)', 2: 'object skin (item, cape)', 6: 'hair', 8: 'fur / skin extra',
                 11: 'creature skin 1', 12: 'creature skin 2', 13: 'creature skin 3'}
BLEND_MODES = {0: 'opaque', 1: 'alpha key', 2: 'alpha', 3: 'no-alpha add', 4: 'add', 5: 'mod', 6: 'mod2x'}
RENDER_FLAG_BITS = {0x1: 'unlit', 0x2: 'unfogged', 0x4: 'two-sided', 0x8: 'no depth test', 0x10: 'no depth write'}
INTERPOLATION = {0: 'none (step)', 1: 'linear', 2: 'hermite', 3: 'bezier'}

# key bones (wowdev; the client resolves them through key_bone_lookup)
KEY_BONES = ['ArmL', 'ArmR', 'ShoulderL', 'ShoulderR', 'SpineLow', 'Waist', 'Head', 'Jaw', 'IndexFingerR', 'MiddleFingerR',
             'PinkyFingerR', 'RingFingerR', 'ThumbR', 'IndexFingerL', 'MiddleFingerL', 'PinkyFingerL', 'RingFingerL',
             'ThumbL', '$BTH', '$CSR', '$CSL', '_Breath', '_Name', '_NameMount', '$CHD', '$CCH', 'Root']

ATTACHMENT_NAMES = {
    0: 'Shield', 1: 'HandRight', 2: 'HandLeft', 3: 'ElbowRight', 4: 'ElbowLeft', 5: 'ShoulderRight', 6: 'ShoulderLeft',
    7: 'KneeRight', 8: 'KneeLeft', 9: 'HipRight', 10: 'HipLeft', 11: 'Helm', 12: 'Back', 13: 'ShoulderFlapRight',
    14: 'ShoulderFlapLeft', 15: 'ChestBloodFront', 16: 'ChestBloodBack', 17: 'Breath', 18: 'PlayerName', 19: 'Base',
    20: 'Head', 21: 'SpellLeftHand', 22: 'SpellRightHand', 23: 'Special1', 24: 'Special2', 25: 'Special3',
    26: 'SheathMainHand', 27: 'SheathOffHand', 28: 'SheathShield', 29: 'PlayerNameMounted', 30: 'LargeWeaponLeft',
    31: 'LargeWeaponRight', 32: 'HipWeaponLeft', 33: 'HipWeaponRight', 34: 'Chest', 35: 'HandArrow', 36: 'Bullet',
    37: 'SpellHandOmni', 38: 'SpellHandDirected', 39: 'VehicleSeat1', 40: 'VehicleSeat2', 41: 'VehicleSeat3',
    42: 'VehicleSeat4', 43: 'VehicleSeat5', 44: 'VehicleSeat6', 45: 'VehicleSeat7', 46: 'VehicleSeat8',
    47: 'LeftFoot', 48: 'RightFoot', 49: 'ShieldNoGlove', 50: 'SpineLow', 51: 'AlteredShoulderR', 52: 'AlteredShoulderL',
}   # (wowdev; ids above 36 do not occur in 1.12 models)
