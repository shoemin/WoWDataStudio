"""The "what uses what" search index (SQLite).

Built from the client's DBCs (through the archives), the file lists of every archive and, when the
server database is reachable, the creature / game object / item templates and spawn counts.
Rebuild with `wowdata index build` after the DBCs or the server database change.

Not indexed yet (see ROADMAP.md): world doodads and WMOs placed by map tiles (ADT MDDF/MODF scan) and
WMO doodad sets. Trees and most world props are found through their file path for now (`find tree`
lists the matching .m2 files).
"""
import json
import os
import sqlite3
import time

from . import serverdb
from .resolve import Catalog, SECTIONS

SCHEMA = """
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE files(path TEXT PRIMARY KEY COLLATE NOCASE, archive TEXT, ext TEXT);
CREATE TABLE creature_display(id INTEGER PRIMARY KEY, model_id INT, model TEXT COLLATE NOCASE, scale REAL,
    skin1 TEXT, skin2 TEXT, skin3 TEXT, extra_id INT, race INT, sex INT);
CREATE TABLE gameobject_display(id INTEGER PRIMARY KEY, model TEXT COLLATE NOCASE);
CREATE TABLE item_display(id INTEGER PRIMARY KEY, icon TEXT, model1 TEXT, model2 TEXT, model_texture1 TEXT, model_texture2 TEXT);
CREATE TABLE asset(kind TEXT, display_id INT, role TEXT, path TEXT COLLATE NOCASE, archive TEXT);
CREATE TABLE race(id INTEGER PRIMARY KEY, name TEXT, prefix TEXT, client_file TEXT, male_display INT, female_display INT);
CREATE TABLE char_section(id INTEGER PRIMARY KEY, race INT, sex INT, section INT, variation INT, color INT,
    texture1 TEXT, texture2 TEXT, texture3 TEXT, flags INT);
CREATE TABLE map(id INTEGER PRIMARY KEY, directory TEXT, name TEXT);
CREATE TABLE creature(entry INTEGER PRIMARY KEY, name TEXT, subname TEXT, level_min INT, level_max INT,
    display1 INT, display2 INT, display3 INT, display4 INT);
CREATE TABLE creature_spawn(entry INT, map INT, count INT);
CREATE TABLE gameobject(entry INTEGER PRIMARY KEY, name TEXT, type INT, display_id INT);
CREATE TABLE gameobject_spawn(entry INT, map INT, count INT);
CREATE TABLE item(entry INTEGER PRIMARY KEY, name TEXT, display_id INT, inventory_type INT, quality INT, class INT, subclass INT);
CREATE INDEX asset_path ON asset(path);
CREATE INDEX asset_display ON asset(kind, display_id);
CREATE INDEX creature_name ON creature(name);
CREATE INDEX item_display_idx ON item(display_id);
CREATE INDEX spawn_entry ON creature_spawn(entry);
"""


def _int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def build(client, path, cfg=None, use_server=True, log=print):
    """(Re)build the index at `path`. Returns a dict of row counts."""
    t0 = time.time()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    if os.path.exists(tmp):
        os.remove(tmp)
    db = sqlite3.connect(tmp)
    db.executescript(SCHEMA)
    cat = Catalog(client)
    counts = {}

    names = cat.names()
    db.executemany('INSERT INTO files VALUES (?,?,?)',
                   [(p, a, os.path.splitext(p)[1].lower()) for p, a in names.values()])
    counts['files'] = len(names)
    log('  files: %d (from the (listfile) of %d archives)' % (len(names), len(client.archives)))

    rows, assets = [], []
    for r in cat.table('CreatureDisplayInfo'):
        info = cat.creature_display(r.id)
        ex = info['extra'] or {}
        rows.append((r.id, r.model_id, info['model'], r.scale, r.skin[0], r.skin[1], r.skin[2], r.extra_id or None,
                     ex.get('race'), ex.get('sex')))
        assets += [('creature', r.id, role, p, a) for role, p, a in info['files']]
    db.executemany('INSERT INTO creature_display VALUES (?,?,?,?,?,?,?,?,?,?)', rows)
    counts['creature_display'] = len(rows)
    log('  creature displays: %d' % len(rows))

    rows = []
    for r in cat.table('GameObjectDisplayInfo'):
        info = cat.gameobject_display(r.id)
        rows.append((r.id, info['model']))
        assets += [('gameobject', r.id, role, p, a) for role, p, a in info['files']]
    db.executemany('INSERT INTO gameobject_display VALUES (?,?)', rows)
    counts['gameobject_display'] = len(rows)
    log('  game object displays: %d' % len(rows))

    rows = []
    for r in cat.table('ItemDisplayInfo'):
        info = cat.item_display(r.id)
        rows.append((r.id, r.icon, r.model[0], r.model[1], r.model_texture[0], r.model_texture[1]))
        assets += [('item', r.id, role, p, a) for role, p, a in info['files']]
    db.executemany('INSERT INTO item_display VALUES (?,?,?,?,?,?)', rows)
    counts['item_display'] = len(rows)
    log('  item displays: %d' % len(rows))

    db.executemany('INSERT INTO asset VALUES (?,?,?,?,?)', assets)
    counts['asset'] = len(assets)

    db.executemany('INSERT INTO race VALUES (?,?,?,?,?,?)',
                   [(r.id, r.name, r.client_prefix, r.client_file_string, r.male_display_id, r.female_display_id) for r in cat.races()])
    db.executemany('INSERT INTO char_section VALUES (?,?,?,?,?,?,?,?,?,?)',
                   [(r.id, r.race, r.sex, r.section, r.variation, r.color, r.texture[0], r.texture[1], r.texture[2], r.flags)
                    for r in cat.table('CharSections')])
    db.executemany('INSERT INTO map VALUES (?,?,?)', [(r.id, r.directory, r.name) for r in cat.table('Map')])
    log('  races: %d, character sections: %d' % (len(cat.races()), len(cat.table('CharSections'))))

    server = False
    if use_server:
        try:
            cr = serverdb.creatures(cfg)
            db.executemany('INSERT INTO creature VALUES (?,?,?,?,?,?,?,?,?)',
                           [(_int(r[0]), r[1], r[2], _int(r[3]), _int(r[4]), _int(r[5]), _int(r[6]), _int(r[7]), _int(r[8])) for r in cr])
            db.executemany('INSERT INTO creature_spawn VALUES (?,?,?)', [tuple(_int(v) for v in r) for r in serverdb.creature_spawns(cfg)])
            go = serverdb.gameobjects(cfg)
            db.executemany('INSERT INTO gameobject VALUES (?,?,?,?)', [(_int(r[0]), r[1], _int(r[2]), _int(r[3])) for r in go])
            db.executemany('INSERT INTO gameobject_spawn VALUES (?,?,?)', [tuple(_int(v) for v in r) for r in serverdb.gameobject_spawns(cfg)])
            it = serverdb.items(cfg)
            db.executemany('INSERT INTO item VALUES (?,?,?,?,?,?,?)', [(_int(r[0]), r[1], *[_int(v) for v in r[2:7]]) for r in it])
            counts.update(creature=len(cr), gameobject=len(go), item=len(it))
            server = True
            log('  server templates: %d creatures, %d game objects, %d items' % (len(cr), len(go), len(it)))
        except serverdb.ServerDBError as e:
            log('  server database not used: %s' % e)
    meta = {'built': time.strftime('%Y-%m-%d %H:%M:%S'), 'server': str(server),
            'archives': json.dumps([n for n, _, _ in client.archives]), 'counts': json.dumps(counts)}
    db.executemany('INSERT INTO meta VALUES (?,?)', list(meta.items()))
    db.commit()
    db.close()
    os.replace(tmp, path)
    counts['seconds'] = round(time.time() - t0, 1)
    return counts


class Index:
    def __init__(self, path):
        if not os.path.exists(path):
            raise FileNotFoundError(path)
        self.db = sqlite3.connect(path)
        self.db.row_factory = sqlite3.Row

    def meta(self):
        return {r['key']: r['value'] for r in self.db.execute('SELECT * FROM meta')}

    def q(self, sql, *args):
        return self.db.execute(sql, args).fetchall()

    def has_server(self):
        return self.meta().get('server') == 'True'

    # ------------------------------------------------------------------ chains
    def assets(self, kind, display_id):
        return [dict(r) for r in self.q('SELECT role, path, archive FROM asset WHERE kind=? AND display_id=?', kind, display_id)]

    def creature_display(self, display_id):
        r = self.q('SELECT * FROM creature_display WHERE id=?', display_id)
        if not r:
            return None
        d = dict(r[0])
        d['files'] = self.assets('creature', display_id)
        d['variations'] = self.model_variations(d['model']) if d['model'] else []
        return d

    def model_variations(self, model):
        rows = self.q('SELECT id, scale, skin1, skin2, skin3 FROM creature_display WHERE model=? ORDER BY id', model)
        skins = {}
        for r in rows:
            key = tuple(s for s in (r['skin1'], r['skin2'], r['skin3']) if s)
            skins.setdefault(key, []).append(r['id'])
        return [{'skins': list(k), 'displays': v} for k, v in sorted(skins.items())]

    def spawns(self, table, entry):
        rows = self.q('SELECT s.map, s.count, m.name FROM %s s LEFT JOIN map m ON m.id = s.map WHERE s.entry=? ORDER BY s.count DESC' % table, entry)
        return [{'map': r['map'], 'map_name': r['name'], 'count': r['count']} for r in rows]

    # ------------------------------------------------------------------ search
    def find(self, text, kinds=None, limit=20):
        words = [w for w in text.lower().split() if w]
        kinds = set(kinds or ['creature', 'gameobject', 'item', 'race', 'model', 'file'])
        res = {}

        def like(col):
            return ' AND '.join(["lower(%s) LIKE ?" % col] * len(words)), ['%' + w + '%' for w in words]

        if 'creature' in kinds and self.has_server():
            cond, args = like('name')
            rows = self.q('SELECT * FROM creature WHERE %s ORDER BY level_min, entry' % cond, *args)
            out = []
            for r in rows[:limit]:
                d = dict(r)
                ids = []
                for k in ('display1', 'display2', 'display3', 'display4'):
                    if d[k] and d[k] not in ids:
                        ids.append(d[k])
                d['displays'] = [self.creature_display(i) or {'id': i, 'missing': True} for i in ids]
                d['spawns'] = self.spawns('creature_spawn', d['entry'])
                out.append(d)
            res['creature'] = {'total': len(rows), 'rows': out}

        if 'gameobject' in kinds and self.has_server():
            cond, args = like('name')
            rows = self.q('SELECT * FROM gameobject WHERE %s ORDER BY entry' % cond, *args)
            out = []
            for r in rows[:limit]:
                d = dict(r)
                d['files'] = self.assets('gameobject', d['display_id'])
                d['spawns'] = self.spawns('gameobject_spawn', d['entry'])
                out.append(d)
            res['gameobject'] = {'total': len(rows), 'rows': out}

        if 'item' in kinds and self.has_server():
            cond, args = like('name')
            rows = self.q('SELECT * FROM item WHERE %s ORDER BY entry' % cond, *args)
            out = []
            for r in rows[:limit]:
                d = dict(r)
                d['files'] = self.assets('item', d['display_id'])
                out.append(d)
            res['item'] = {'total': len(rows), 'rows': out}

        if 'race' in kinds:
            sex = None
            rw = []
            for w in words:
                if w in ('male', 'm', 'man'):
                    sex = 0
                elif w in ('female', 'f', 'woman'):
                    sex = 1
                else:
                    rw.append(w)
            out = []
            if rw:
                for r in self.q('SELECT * FROM race ORDER BY id'):
                    nm = (r['name'] or '').lower()
                    if all(w in nm or w == (r['client_file'] or '').lower() for w in rw):
                        for s in ([sex] if sex is not None else [0, 1]):
                            disp = r['male_display'] if s == 0 else r['female_display']
                            secs = self.q('SELECT * FROM char_section WHERE race=? AND sex=? ORDER BY section, variation, color', r['id'], s)
                            by_sec = {}
                            for c in secs:
                                by_sec.setdefault(SECTIONS.get(c['section'], c['section']), []).append(
                                    [t for t in (c['texture1'], c['texture2'], c['texture3']) if t])
                            out.append({'race': r['id'], 'name': r['name'], 'sex': 'male' if s == 0 else 'female',
                                        'display': self.creature_display(disp), 'sections': by_sec, 'section_rows': len(secs)})
            res['race'] = {'total': len(out), 'rows': out}

        if 'model' in kinds:
            cond, args = like('path')
            rows = self.q("SELECT path, archive, COUNT(DISTINCT kind || display_id) AS users, group_concat(DISTINCT kind) AS kinds "
                          "FROM asset WHERE role LIKE 'model%%' AND %s GROUP BY path ORDER BY users DESC, path" % cond, *args)
            out = []
            for r in rows[:limit]:
                d = dict(r)
                if 'creature' in (d['kinds'] or ''):
                    d['variations'] = self.model_variations(d['path'])
                out.append(d)
            res['model'] = {'total': len(rows), 'rows': out}

        if 'file' in kinds:
            cond, args = like('path')
            rows = self.q('SELECT path, archive FROM files WHERE %s ORDER BY path' % cond, *args)
            res['file'] = {'total': len(rows), 'rows': [dict(r) for r in rows[:limit * 5]]}
        return res
