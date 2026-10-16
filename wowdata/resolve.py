"""Display IDs -> the files the client loads for them (models, skins, icons, item textures).

The rules follow the client code (names from our decompile):
  * creature display: CreatureDisplayInfo.model_id -> CreatureModelData.model_path (.mdx -> .m2);
    TextureVariation (skin 1-3) = <model folder>\\<skin>.blp, filling the model's replaceable texture
    slots 11/12/13; CreatureDisplayInfoExtra.bake = textures\\BakedNpcTextures\\<bake> (humanoid NPCs).
  * game object display: GameObjectDisplayInfo.model_path (.mdx -> .m2, or a .wmo).
  * item display: models and their textures under Item\\ObjectComponents\\<slot>\\ (helmets have one model
    per race/sex: <model>_<race prefix><M|F>.m2), body textures under Item\\TextureComponents\\<region>Texture\\
    with the _M / _F / _U suffix chosen by CCharacterComponent_LoadArmorRegionTexture @00476e20,
    icon = Interface\\Icons\\<icon>.blp.
  * character sections (CharSections) name their textures directly.
"""
import os

from . import dbc
from .vfs import resolve_name

BS = '\\'
REGIONS = ['ArmUpper', 'ArmLower', 'Hand', 'TorsoUpper', 'TorsoLower', 'LegUpper', 'LegLower', 'Foot']
SECTIONS = {0: 'skin', 1: 'face', 2: 'facial hair', 3: 'hair', 4: 'underwear'}


class Catalog:
    """DBC tables plus a case-insensitive file-name index of the client's archives."""

    def __init__(self, client):
        self.client = client
        self._tables = {}
        self._names = None
        self._by_base = None
        self._embedded = {}
        self._helmets = None

    def table(self, name):
        if name not in self._tables:
            self._tables[name] = dbc.load(self.client, name)
        return self._tables[name]

    def names(self):
        if self._names is None:
            self._names = self.client.names()
        return self._names

    def archive_of(self, path):
        hit = self.names().get(resolve_name(path).upper())
        return hit[1] if hit else None

    def canonical(self, path):
        """Path with the case of the listfile (or as given when unknown)."""
        p = resolve_name(path)
        hit = self.names().get(p.upper())
        return hit[0] if hit else p

    def by_basename(self, base, prefix='ITEM\\'):
        if self._by_base is None:
            idx = {}
            for u, (p, a) in self.names().items():
                if u.startswith(prefix):
                    idx.setdefault(u.rsplit(BS, 1)[-1], []).append(p)
            self._by_base = idx
        return self._by_base.get(base.upper(), [])

    def helmet_models(self, stem):
        """Per race/sex helmet models <stem>_<race prefix><M|F>.m2 under Item\\ObjectComponents\\Head."""
        if self._helmets is None:
            idx = {}
            head = 'ITEM\\OBJECTCOMPONENTS\\HEAD\\'
            for u, (p, a) in self.names().items():
                rest = u[len(head):]
                if u.startswith(head) and u.endswith('.M2') and '_' in rest:
                    idx.setdefault(rest.rsplit('_', 1)[0], []).append(p)
            self._helmets = idx
        return sorted(self._helmets.get(stem.upper(), []))

    # ------------------------------------------------------------------ creatures
    def creature_display(self, display_id):
        """{'id', 'model', 'scale', 'files': [(role, path, archive)], 'extra': {...} or None} or None."""
        cdi = self.table('CreatureDisplayInfo').get(display_id)
        if cdi is None:
            return None
        cmd = self.table('CreatureModelData').get(cdi.model_id)
        files = []
        model = None
        if cmd and cmd.model_path:
            model = self.canonical(cmd.model_path)
            files.append(('model', model, self.archive_of(model)))
            folder = model.rsplit(BS, 1)[0] if BS in model else ''
            for i, skin in enumerate(cdi.skin):
                if skin:
                    p = self.canonical(folder + BS + skin + '.blp')
                    files.append(('skin%d' % (i + 1), p, self.archive_of(p)))
            if self.archive_of(model):
                files += self._embedded_textures(model)
        extra = None
        if cdi.extra_id:
            ex = self.table('CreatureDisplayInfoExtra').get(cdi.extra_id)
            if ex:
                extra = {'id': ex.id, 'race': ex.race, 'sex': ex.sex, 'equipment': ex.equipment}
                if ex.bake:
                    p = self.canonical('Textures\\BakedNpcTextures\\' + ex.bake)
                    files.append(('baked skin', p, self.archive_of(p)))
        return {'id': display_id, 'model_id': cdi.model_id, 'model': model, 'scale': cdi.scale,
                'skins': [s for s in cdi.skin if s], 'files': files, 'extra': extra}

    def _embedded_textures(self, model):
        key = model.upper()
        if key in self._embedded:
            return self._embedded[key]
        from .m2 import M2Header, M2Error
        out = []
        try:
            hdr = M2Header(self.client.read(model))
            for typ, flags, name in hdr.textures():
                if typ == 0 and name:
                    p = self.canonical(name)
                    out.append(('model texture', p, self.archive_of(p)))
        except (M2Error, TypeError):
            pass
        self._embedded[key] = out
        return out

    def model_variations(self, model_path):
        """All creature displays using a model: [(display id, scale, skins)]."""
        target = resolve_name(model_path).upper()
        models = {r.id for r in self.table('CreatureModelData') if r.model_path and resolve_name(r.model_path).upper() == target}
        return [(r.id, r.scale, [s for s in r.skin if s]) for r in self.table('CreatureDisplayInfo') if r.model_id in models]

    # ------------------------------------------------------------------ game objects
    def gameobject_display(self, display_id):
        r = self.table('GameObjectDisplayInfo').get(display_id)
        if r is None:
            return None
        files = []
        model = None
        if r.model_path:
            model = self.canonical(r.model_path)
            files.append(('model', model, self.archive_of(model)))
            if model.lower().endswith('.m2') and self.archive_of(model):
                files += self._embedded_textures(model)
        return {'id': display_id, 'model': model, 'files': files}

    # ------------------------------------------------------------------ items
    def item_display(self, display_id, races=None):
        r = self.table('ItemDisplayInfo').get(display_id)
        if r is None:
            return None
        files = []
        model_dirs = []
        for i, m in enumerate(r.model):
            if not m:
                continue
            stem = os.path.splitext(m)[0]
            hits = self.by_basename(stem + '.m2')
            if hits:
                for h in hits:
                    files.append(('model%d' % (i + 1), h, self.archive_of(h)))
                    model_dirs.append(h.rsplit(BS, 1)[0].upper())
            else:
                # helmets: one model per race and sex (<stem>_<prefix><M|F>.m2)
                for p in self.helmet_models(stem):
                    files.append(('model%d (race/sex)' % (i + 1), p, self.archive_of(p)))
                    model_dirs.append(p.rsplit(BS, 1)[0].upper())
        for i, t in enumerate(r.model_texture):
            if not t:
                continue
            hits = self.by_basename(t + '.blp')
            same = [h for h in hits if h.rsplit(BS, 1)[0].upper() in model_dirs]
            for h in (same or hits):
                files.append(('model texture%d' % (i + 1), h, self.archive_of(h)))
        for region, tex in zip(REGIONS, r.texture):
            if not tex:
                continue
            for suf in ('_M', '_F', '_U'):
                p = 'Item\\TextureComponents\\%sTexture\\%s%s.blp' % (region, tex, suf)
                if self.archive_of(p):
                    p = self.canonical(p)
                    files.append(('%s texture%s' % (region, suf), p, self.archive_of(p)))
        if r.icon:
            p = self.canonical('Interface\\Icons\\%s.blp' % r.icon)
            files.append(('icon', p, self.archive_of(p)))
        return {'id': display_id, 'icon': r.icon, 'files': files}

    # ------------------------------------------------------------------ races / character sections
    def races(self):
        return list(self.table('ChrRaces'))

    def char_sections(self, race, sex=None):
        out = []
        for r in self.table('CharSections'):
            if r.race == race and (sex is None or r.sex == sex):
                out.append(r)
        return out

    def section_files(self, row):
        files = []
        for t in row.texture:
            if t:
                p = self.canonical(t)
                files.append(('%s v%d c%d' % (SECTIONS.get(row.section, row.section), row.variation, row.color), p, self.archive_of(p)))
        return files
