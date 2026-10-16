"""DBC (WDBC) tables: a generic record reader plus typed 1.12 (build 5875) layouts.

File layout: 'WDBC', record count, field count, record size, string block size, records, string block.
Every 1.12 field is 4 bytes. Strings are offsets into the string block (UTF-8). Localized strings
are 8 offsets (enUS first) plus a flags mask = 9 fields.

The layouts below were checked against the files (field counts are asserted on load) and against the
record structs of our decompile (`names/structs.csv`: CreatureDisplayInfoRec, ItemDisplayInfoRec, ...).
Fields we do not need yet are kept as generic `f_<index>` integers.
"""
import struct

LOCALES = 8


class Field:
    def __init__(self, name, kind, count=1):
        self.name = name
        self.kind = kind      # 'i' int32, 'u' uint32, 'f' float, 's' string, 'loc' localized string
        self.count = count    # array length

    @property
    def width(self):
        return (LOCALES + 1 if self.kind == 'loc' else 1) * self.count


def F(name, kind='u', count=1):
    return Field(name, kind, count)


SCHEMAS = {
    'CreatureDisplayInfo': [F('id'), F('model_id'), F('sound_id'), F('extra_id'), F('scale', 'f'), F('opacity'),
                            F('skin', 's', 3), F('size_class', 'i'), F('blood_id'), F('npc_sound_id')],
    'CreatureModelData': [F('id'), F('flags'), F('model_path', 's'), F('size_class'), F('model_scale', 'f'), F('blood_id'),
                          F('footprint_texture_id'), F('footprint_length', 'f'), F('footprint_width', 'f'),
                          F('footprint_scale', 'f'), F('foley_material_id'), F('footstep_shake_size'),
                          F('death_thud_shake_size'), F('sound_data_id'), F('collision_width', 'f'), F('collision_height', 'f')],
    # equipment order (wowdev, 1.12): helm, shoulder, shirt, chest, belt, legs, boots, wrist, gloves, tabard
    'CreatureDisplayInfoExtra': [F('id'), F('race'), F('sex'), F('skin'), F('face'), F('hair_style'), F('hair_color'),
                                 F('facial_hair'), F('equipment', 'u', 10), F('bake', 's')],
    'GameObjectDisplayInfo': [F('id'), F('model_path', 's'), F('sound', 'u', 10)],
    # region textures: arm upper, arm lower, hand, torso upper, torso lower, leg upper, leg lower, foot
    'ItemDisplayInfo': [F('id'), F('model', 's', 2), F('model_texture', 's', 2), F('icon', 's'), F('geoset_group', 'u', 3),
                        F('flags'), F('spell_visual_id'), F('group_sound_index'), F('helmet_geoset_vis', 'u', 2),
                        F('texture', 's', 8), F('item_visual')],
    # sections: 0 skin, 1 face, 2 facial hair, 3 hair, 4 underwear
    'CharSections': [F('id'), F('race'), F('sex'), F('section'), F('variation'), F('color'), F('texture', 's', 3), F('flags')],
    'ChrRaces': [F('id'), F('flags'), F('faction_id'), F('exploration_sound_id'), F('male_display_id'), F('female_display_id'),
                 F('client_prefix', 's'), F('mount_scale', 'f'), F('base_language'), F('creature_type'),
                 F('login_effect_spell_id'), F('combat_stun_spell_id'), F('res_sickness_spell_id'), F('splash_sound_id'),
                 F('starting_taxi_nodes'), F('client_file_string', 's'), F('cinematic_sequence_id'), F('name', 'loc'),
                 F('facial_hair_customization', 's', 2), F('hair_customization', 's')],
    'CharHairGeosets': [F('id'), F('race'), F('sex'), F('variation'), F('geoset'), F('show_scalp')],
    'AreaTable': [F('id'), F('map_id'), F('zone_id'), F('explore_bit'), F('flags'), F('sound_pref'), F('sound_pref_water'),
                  F('ambience_id'), F('zone_music'), F('intro_sound'), F('exploration_level', 'i'), F('name', 'loc'),
                  F('faction_group_mask'), F('liquid_type'), F('min_elevation', 'f'), F('ambient_multiplier', 'f'), F('light_id')],
    'Map': [F('id'), F('directory', 's'), F('instance_type'), F('pvp'), F('name', 'loc')],   # remaining fields generic
}


class DBCError(Exception):
    pass


class Record(dict):
    """A row: dict access plus attribute access (`rec.model_path`)."""

    def __getattr__(self, key):
        try:
            return self[key]
        except KeyError:
            raise AttributeError(key)


class DBC:
    def __init__(self, data, name=None, schema=None):
        if data[:4] != b'WDBC':
            raise DBCError('%s: not a WDBC file' % name)
        self.name = name
        self.count, self.field_count, self.record_size, self.string_size = struct.unpack_from('<4I', data, 4)
        start = 20
        self.strings = data[start + self.count * self.record_size:start + self.count * self.record_size + self.string_size]
        self.field_size = 4
        if self.record_size != self.field_count * 4:
            # a few 1.12 tables pack byte fields (CharBaseInfo: race, class = 2 bytes per record)
            if self.field_count and self.record_size % self.field_count == 0 and self.record_size // self.field_count in (1, 2):
                self.field_size = self.record_size // self.field_count
                code = 'B' if self.field_size == 1 else 'H'
                self._ints = struct.unpack_from('<%d%s' % (self.count * self.field_count, code), data, start)
                self._floats = tuple(float(v) for v in self._ints)
            else:
                # mixed field sizes (CharStartOutfit: 4-byte id, 4 byte fields, then 4-byte arrays):
                # only whole records are available (record_bytes); a typed layout would need byte fields
                self.field_size = None
                self._ints = self._floats = ()
            self._data = data
            if schema is not None:
                raise DBCError('%s: typed layouts need 4-byte fields' % name)
            self.schema = None
            self._rows = None
            self._by_id = None
            return
        self._ints = struct.unpack_from('<%di' % (self.count * self.field_count), data, start)
        self._floats = struct.unpack_from('<%df' % (self.count * self.field_count), data, start)
        if schema is None and name in SCHEMAS:
            schema = SCHEMAS[name]
        self.schema = self._complete(schema) if schema is not None else None
        self._rows = None
        self._by_id = None

    def _complete(self, schema):
        used = sum(f.width for f in schema)
        if used > self.field_count:
            raise DBCError('%s: layout has %d fields, the file has %d' % (self.name, used, self.field_count))
        schema = list(schema)
        for i in range(used, self.field_count):
            schema.append(F('f_%d' % i, 'i'))
        return schema

    # ------------------------------------------------------------------ raw access
    def string(self, offset):
        if offset <= 0 or offset >= len(self.strings):
            return ''
        end = self.strings.find(b'\0', offset)
        raw = self.strings[offset:end if end >= 0 else None]
        try:
            return raw.decode('utf-8')
        except UnicodeDecodeError:
            return raw.decode('latin-1')

    def record_bytes(self, row):
        start = 20 + row * self.record_size
        data = getattr(self, '_data', None)
        if data is None:
            raise DBCError('record_bytes is only kept for tables with non-4-byte fields')
        return data[start:start + self.record_size]

    def raw(self, row, col):
        if self.field_size is None:
            raise DBCError('%s has mixed field sizes; use record_bytes()' % self.name)
        return self._ints[row * self.field_count + col]

    def raw_float(self, row, col):
        return self._floats[row * self.field_count + col]

    def raw_row(self, row):
        return self._ints[row * self.field_count:(row + 1) * self.field_count]

    # ------------------------------------------------------------------ typed access
    def _decode(self, row):
        rec = Record()
        col = 0
        base = row * self.field_count
        for f in self.schema:
            vals = []
            for _ in range(f.count):
                if f.kind == 'loc':
                    vals.append(self.string(self._ints[base + col]))   # enUS
                    col += LOCALES + 1
                    continue
                v = self._ints[base + col]
                if f.kind == 'u':
                    v &= 0xFFFFFFFF
                elif f.kind == 'f':
                    v = self._floats[base + col]
                elif f.kind == 's':
                    v = self.string(v)
                vals.append(v)
                col += 1
            rec[f.name] = vals[0] if f.count == 1 else vals
        return rec

    def rows(self):
        if self.schema is None:
            raise DBCError('%s: no layout; use raw_row()' % self.name)
        if self._rows is None:
            self._rows = [self._decode(i) for i in range(self.count)]
        return self._rows

    def by_id(self):
        if self._by_id is None:
            key = self.schema[0].name
            self._by_id = {r[key]: r for r in self.rows()}
        return self._by_id

    def get(self, rid):
        return self.by_id().get(rid)

    def __len__(self):
        return self.count

    def __iter__(self):
        return iter(self.rows())


def guess_row(dbc, row):
    """Untyped row with a best-effort guess per value (string / float / int), for unknown tables."""
    if dbc.field_size is None:
        return [dbc.record_bytes(row).hex()]
    if dbc.field_size != 4:
        return list(dbc.raw_row(row))
    out = []
    for c in range(dbc.field_count):
        v = dbc.raw(row, c)
        if 0 < v < len(dbc.strings) and dbc.strings[v - 1] == 0 and dbc.strings[v] != 0:
            out.append(dbc.string(v))
            continue
        fv = dbc.raw_float(row, c)
        if abs(v) > 0x01000000 and 1e-4 < abs(fv) < 1e7:
            out.append(round(fv, 6))
        else:
            out.append(v)
    return out


def load(client, name):
    """Load DBFilesClient\\<name>.dbc from a ClientData (the copy the client really uses)."""
    data = client.read('DBFilesClient\\%s.dbc' % name)
    if data is None:
        raise DBCError('DBFilesClient\\%s.dbc not found' % name)
    return DBC(data, name)


def load_file(path, name=None):
    import os
    with open(path, 'rb') as f:
        data = f.read()
    return DBC(data, name or os.path.splitext(os.path.basename(path))[0])
