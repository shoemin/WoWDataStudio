"""What to show for a model target: the .m2, the textures for its replaceable slots and the visible geosets.

Targets (same syntax as `extract`): an .m2/.mdx path, display:N, creature:N, item:N / itemdisplay:N,
go:N / godisplay:N, race:<name or id>[:male|female].

Replaceable texture slots (M2Texture.type, filled by the client at run time):
  * creature skins 11/12/13 = CreatureDisplayInfo.TextureVariation[0..2] (<model folder>\\<name>.blp);
  * 1 = character body: humanoid NPCs use their baked texture (CreatureDisplayInfoExtra.bake); player
    models get a default skin composited from CharSections (see character_textures);
  * 6 = hair (CharSections section 3, texture 0), 8 = skin extra / fur (section 0, texture 1);
  * 2 = object skin: ItemDisplayInfo.ModelTexture for item models (capes have no default).

Character geosets follow CCharacterComponent_ApplyGeosets @00477520: hide everything, show geoset 0 and one
geoset per group: the CharCreate defaults (group*100+1, ears 702), the hair geoset from CharHairGeosets for the
hair style and the facial-hair geosets from CharacterFacialHairStyles. Worn items are not applied (no equipment
geosets).
"""
import os

import numpy as np

from . import blp, dbc
from .targets import parse as parse_target, TargetError, _index, _server_ids
from .vfs import resolve_name

BS = '\\'

# CharComponent_InitRegionRects @00475c50: x, y, width, height of the 10 regions of the 256x256 body texture
REGIONS = {
    'ArmUpper': (0, 0, 128, 64), 'ArmLower': (0, 64, 128, 64), 'Hand': (0, 128, 128, 32),
    'TorsoUpper': (128, 0, 128, 64), 'TorsoLower': (128, 64, 128, 32), 'LegUpper': (128, 96, 128, 64),
    'LegLower': (128, 160, 128, 64), 'Foot': (128, 224, 128, 32), 'FaceUpper': (0, 160, 128, 32),
    'FaceLower': (0, 192, 128, 64),
}


class Texture:
    """A resolved texture: RGBA pixels plus where they came from."""

    def __init__(self, rgba, source, path=None):
        self.rgba = rgba
        self.source = source      # human-readable description
        self.path = path          # archive path when it is one file


class ModelContext:
    def __init__(self, model_path, kind, label):
        self.model_path = model_path
        self.kind = kind                  # creature / character / npc / item / gameobject / path
        self.label = label
        self.slots = {}                   # texture type -> Texture
        self.geosets = None               # None = all visible, else a set of visible geoset ids
        self.notes = []
        self.info = {}

    def describe(self):
        return {'model': self.model_path, 'kind': self.kind, 'label': self.label, 'info': self.info,
                'slots': {str(k): {'source': t.source, 'path': t.path} for k, t in self.slots.items()},
                'visible_geosets': sorted(self.geosets) if self.geosets is not None else 'all',
                'notes': self.notes}


def load_blp(client, path):
    data = client.read(path)
    if data is None:
        return None
    try:
        return blp.BLP(data).decode(0)
    except Exception:
        return None


def _paste(dst, src, region):
    """Alpha-composite src (resized to the region if needed) over dst at the region rectangle."""
    from PIL import Image
    x, y, w, h = region
    scale = dst.shape[0] / 256.0
    x, y, w, h = int(x * scale), int(y * scale), int(w * scale), int(h * scale)
    if src.shape[0] != h or src.shape[1] != w:
        src = np.array(Image.fromarray(src, 'RGBA').resize((w, h), Image.BILINEAR))
    a = src[..., 3:4].astype(np.float32) / 255.0
    area = dst[y:y + h, x:x + w, :3].astype(np.float32)
    dst[y:y + h, x:x + w, :3] = (src[..., :3] * a + area * (1 - a) + 0.5).astype(np.uint8)


def character_textures(cat, race, sex, skin=0, face=0, hair_style=0, hair_color=0, facial_hair=0):
    """{slot type: Texture} for a player model with the given CharSections choices (all 0 = the first option
    of each, what the client's character creation starts from). The body (type 1) is composited like
    CCharacterComponent_UpdateTextures: base skin, then face lower/upper, facial hair lower/upper and the scalp
    textures of the hair style into the face regions, then the underwear (pelvis -> LegUpper, torso ->
    TorsoUpper)."""
    client = cat.client
    rows = cat.char_sections(race, sex)

    def row(section, variation, color):
        for r in rows:
            if r.section == section and r.variation == variation and r.color == color:
                return r
        for r in rows:
            if r.section == section and r.variation == variation:
                return r
        return None

    out = {}
    used = []
    base_row = row(0, 0, skin)
    if base_row is None or not base_row.texture[0]:
        return out, ['no CharSections skin row for race %d sex %d' % (race, sex)]
    body = load_blp(client, base_row.texture[0])
    if body is None:
        return out, ['skin texture %s missing' % base_row.texture[0]]
    body = body.copy()
    used.append(base_row.texture[0])
    layers = []
    f = row(1, face, skin)
    if f:
        layers += [(f.texture[0], 'FaceLower'), (f.texture[1], 'FaceUpper')]
    fh = row(2, facial_hair, hair_color)
    if fh:
        layers += [(fh.texture[0], 'FaceLower'), (fh.texture[1], 'FaceUpper')]
    hr = row(3, hair_style, hair_color)
    if hr:
        layers += [(hr.texture[1], 'FaceLower'), (hr.texture[2], 'FaceUpper')]
    uw = row(4, 0, skin)
    if uw:
        layers += [(uw.texture[0], 'LegUpper'), (uw.texture[1], 'TorsoUpper')]
    for path, region in layers:
        if not path:
            continue
        img = load_blp(client, path)
        if img is not None:
            _paste(body, img, REGIONS[region])
            used.append(path)
    out[1] = Texture(body, 'composite of ' + ', '.join(os.path.basename(p) for p in used))
    hair = hr.texture[0] if hr and hr.texture[0] else None
    if not hair:   # bald style: take the first style of this colour so other hair geosets still look right
        for r in rows:
            if r.section == 3 and r.color == hair_color and r.texture[0]:
                hair = r.texture[0]
                break
    if hair:
        img = load_blp(client, hair)
        if img is not None:
            out[6] = Texture(img, hair, hair)
    if base_row.texture[1]:
        img = load_blp(client, base_row.texture[1])
        if img is not None:
            out[8] = Texture(img, base_row.texture[1], base_row.texture[1])
    notes = ['default character skin: CharSections skin %d, face %d, hair style %d colour %d, facial hair %d' %
             (skin, face, hair_style, hair_color, facial_hair)]
    return out, notes


# CharCreate defaults of CCharacterComponent's 16 geoset groups (0 = hair, 1-3 = facial hair, 7 = ears 702)
DEFAULT_GROUP_GEOSETS = [1, 101, 201, 301, 401, 501, 601, 702, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501]


def character_geosets(cat, race, sex, hair_style=0, facial_hair=0):
    """Visible geosets per CCharacterComponent_ApplyGeosets with no items worn: geoset 0, the 16 group defaults, the hair
    geoset of the style (CharHairGeosets; 1 when the style has none, CCharacterComponent_GetHairGeoset) and the
    facial-hair geosets (CharacterFacialHairStyles fields 6/7/8 + 100/300/200, CCharacterComponent_SetFacialHair)."""
    ids = list(DEFAULT_GROUP_GEOSETS)
    hair = None
    for r in cat.table('CharHairGeosets'):
        if r.race == race and r.sex == sex and r.variation == hair_style:
            hair = r.geoset
    ids[0] = hair if hair and hair > 0 else 1
    try:
        t = dbc.load(cat.client, 'CharacterFacialHairStyles')
        for i in range(t.count):
            if (t.raw(i, 0), t.raw(i, 1), t.raw(i, 2)) == (race, sex, facial_hair):
                ids[1] = t.raw(i, 6) + 100
                ids[3] = t.raw(i, 7) + 300
                ids[2] = t.raw(i, 8) + 200
                break
    except dbc.DBCError:
        pass
    return {0} | set(ids)


def _race_of_model(cat, path):
    """(race id, sex) for Character\\<Race>\\<Male|Female>\\*.m2, else None."""
    parts = resolve_name(path).split(BS)
    if len(parts) < 4 or parts[0].lower() != 'character':
        return None
    sex = {'male': 0, 'female': 1}.get(parts[2].lower())
    for r in cat.races():
        if r.client_file_string.lower() == parts[1].lower() and sex is not None:
            return r.id, sex
    return None


def _skin_slots(cat, ctx, model, skins):
    folder = model.rsplit(BS, 1)[0] if BS in model else ''
    for i, s in enumerate(skins):
        if s:
            p = cat.canonical(folder + BS + s + '.blp')
            img = load_blp(cat.client, p)
            if img is not None:
                ctx.slots[11 + i] = Texture(img, p, p)
            else:
                ctx.notes.append('skin %s not found' % p)


def _creature_display(cat, display_id, label=None):
    cdi = cat.table('CreatureDisplayInfo').get(display_id)
    if cdi is None:
        raise TargetError('CreatureDisplayInfo %d does not exist' % display_id)
    cmd = cat.table('CreatureModelData').get(cdi.model_id)
    if not cmd or not cmd.model_path:
        raise TargetError('display %d has no model (CreatureModelData %d)' % (display_id, cdi.model_id))
    model = cat.canonical(cmd.model_path)
    ctx = ModelContext(model, 'creature', label or 'display %d' % display_id)
    ctx.info.update({'display_id': display_id, 'model_id': cdi.model_id, 'scale': cdi.scale,
                     'texture_variations': [s for s in cdi.skin]})
    _skin_slots(cat, ctx, model, cdi.skin)
    if cdi.extra_id:
        ex = cat.table('CreatureDisplayInfoExtra').get(cdi.extra_id)
        if ex:
            ctx.kind = 'npc'
            ctx.info['extra'] = dict(ex)
            if ex.bake:
                p = cat.canonical('Textures\\BakedNpcTextures\\' + ex.bake)
                img = load_blp(cat.client, p)
                if img is not None:
                    ctx.slots[1] = Texture(img, 'baked NPC texture ' + p, p)
            texs, _ = character_textures(cat, ex.race, ex.sex, ex.skin, ex.face, ex.hair_style, ex.hair_color, ex.facial_hair)
            for k, t in texs.items():
                ctx.slots.setdefault(k, t)
            ctx.geosets = character_geosets(cat, ex.race, ex.sex, ex.hair_style, ex.facial_hair)
            ctx.notes.append('humanoid NPC: baked body texture, hair from CharSections; equipment geosets are not applied')
    else:
        rs = _race_of_model(cat, model)
        if rs:
            _character(cat, ctx, rs[0], rs[1])
    return ctx


def _character(cat, ctx, race, sex, **choices):
    ctx.kind = 'character'
    texs, notes = character_textures(cat, race, sex, **choices)
    ctx.slots.update(texs)
    ctx.geosets = character_geosets(cat, race, sex, choices.get('hair_style', 0), choices.get('facial_hair', 0))
    ctx.notes += notes
    ctx.info.update({'race': race, 'sex': sex})


def resolve_model(text, cat, cfg, choices=None):
    """ModelContext for a command-line target."""
    choices = choices or {}
    kind, val = parse_target(text)
    if kind == 'glob':
        raise TargetError('a model target cannot be a wildcard')
    if kind == 'path':
        p = cat.canonical(val)
        if not p.lower().endswith('.m2'):
            raise TargetError('%s is not a model (.m2/.mdx)' % val)
        if not cat.archive_of(p):
            raise TargetError('%s is not in the archives' % p)
        ctx = ModelContext(p, 'path', os.path.basename(p))
        rs = _race_of_model(cat, p)
        if rs:
            _character(cat, ctx, rs[0], rs[1], **choices)
        else:
            var = cat.model_variations(p)
            if var:
                did, scale, skins = var[0]
                ctx.kind = 'creature'
                _skin_slots(cat, ctx, p, skins + [''] * (3 - len(skins)))
                ctx.info['display_id'] = did
                ctx.notes.append('skins of display %d (the first of %d displays using this model; pick another with display:<id>)' % (did, len(var)))
        return ctx
    if kind == 'display':
        return _creature_display(cat, val)
    if kind == 'creature':
        ix = _index(cfg)
        rows = ix.q('SELECT display1, display2, display3, display4 FROM creature WHERE entry=?', val) if ix and ix.has_server() else None
        if rows is None:
            rows = _server_ids(cfg, 'SELECT display_id1, display_id2, display_id3, display_id4 FROM creature_template '
                                    'WHERE entry=%d ORDER BY patch DESC LIMIT 1' % val)
        if not rows:
            raise TargetError('creature %d not found' % val)
        ids = [int(d) for d in rows[0] if d and int(d)]
        if not ids:
            raise TargetError('creature %d has no display id' % val)
        ctx = _creature_display(cat, ids[0], 'creature %d' % val)
        ctx.info['creature'] = val
        if len(ids) > 1:
            ctx.notes.append('creature %d has displays %s; showing %d' % (val, ids, ids[0]))
        return ctx
    if kind in ('item', 'itemdisplay'):
        did = val
        if kind == 'item':
            ix = _index(cfg)
            rows = ix.q('SELECT display_id FROM item WHERE entry=?', val) if ix and ix.has_server() else None
            if rows is None:
                rows = _server_ids(cfg, 'SELECT display_id FROM item_template WHERE entry=%d ORDER BY patch DESC LIMIT 1' % val)
            if not rows:
                raise TargetError('item %d not found' % val)
            did = int(rows[0][0])
        info = cat.item_display(did)
        if info is None:
            raise TargetError('ItemDisplayInfo %d does not exist' % did)
        models = [p for role, p, a in info['files'] if role.startswith('model') and 'texture' not in role and a]
        if not models:
            raise TargetError('item display %d has no model (a body-texture-only item?)' % did)
        ctx = ModelContext(models[0], 'item', '%s %d' % (kind, val))
        ctx.info.update({'item_display_id': did})
        tex = [p for role, p, a in info['files'] if role == 'model texture1' and a]
        if tex:
            img = load_blp(cat.client, tex[0])
            if img is not None:
                ctx.slots[2] = Texture(img, tex[0], tex[0])
        if len(models) > 1:
            ctx.notes.append('item has %d models (%s); showing the first' % (len(models), ', '.join(os.path.basename(m) for m in models[:6])))
        return ctx
    if kind in ('go', 'godisplay'):
        did = val
        if kind == 'go':
            ix = _index(cfg)
            rows = ix.q('SELECT display_id FROM gameobject WHERE entry=?', val) if ix and ix.has_server() else None
            if rows is None:
                rows = _server_ids(cfg, 'SELECT displayId FROM gameobject_template WHERE entry=%d ORDER BY patch DESC LIMIT 1' % val)
            if not rows:
                raise TargetError('game object %d not found' % val)
            did = int(rows[0][0])
        info = cat.gameobject_display(did)
        if info is None or not info['model']:
            raise TargetError('GameObjectDisplayInfo %d has no model' % did)
        if not info['model'].lower().endswith('.m2'):
            raise TargetError('%s is a WMO (not supported by the model exporter yet)' % info['model'])
        ctx = ModelContext(info['model'], 'gameobject', '%s %d' % (kind, val))
        ctx.info['gameobject_display_id'] = did
        return ctx
    if kind == 'race':
        parts = val.split(':')
        sex = {'male': 0, 'm': 0, '0': 0, 'female': 1, 'f': 1, '1': 1}.get(parts[1].lower(), 0) if len(parts) > 1 else 0
        race = None
        for r in cat.races():
            if parts[0].isdigit() and r.id == int(parts[0]) or parts[0].lower() in (r.name.lower(), r.client_file_string.lower()):
                race = r
        if race is None:
            raise TargetError('race %s not found' % val)
        disp = race.male_display_id if sex == 0 else race.female_display_id
        cdi = cat.table('CreatureDisplayInfo').get(disp)
        cmd = cat.table('CreatureModelData').get(cdi.model_id) if cdi else None
        if not cmd:
            raise TargetError('race %s: display %d has no model' % (val, disp))
        ctx = ModelContext(cat.canonical(cmd.model_path), 'character', '%s %s' % (race.name, 'male' if sex == 0 else 'female'))
        ctx.info['display_id'] = disp
        _character(cat, ctx, race.id, sex, **choices)
        return ctx
    raise TargetError('unsupported model target %s' % text)
