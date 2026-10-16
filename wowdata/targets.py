"""Turn a command-line target into the list of client files it stands for.

Accepted forms (case-insensitive prefixes):
  Creature\\Boar\\BoarSkinIvory.blp   an archive path (.mdx/.mdl are read as .m2); wildcards * ? allowed
  3098 / creature:3098 / c:3098      a creature entry (server database: every display of the creature)
  display:503 / d:503                a CreatureDisplayInfo id
  item:12282 / i:12282               an item entry (server database) ; itemdisplay:22291 an ItemDisplayInfo id
  go:178561                          a game object entry (server database) ; godisplay:3813 a GameObjectDisplayInfo id
  race:2 / race:orc / race:orc:male  a playable race (model + its CharSections textures)
For an .m2 path the result also contains the textures named inside the model and, for creature
models, the skins of every display that uses the model (its "texture variations").
"""
import os

from . import serverdb
from .index import Index
from .vfs import resolve_name


class TargetError(Exception):
    pass


def parse(text):
    t = text.strip()
    low = t.lower()
    if t.isdigit():
        return 'creature', int(t)
    if ':' in t and not (len(t) > 1 and t[1] == ':' and t[0].isalpha() and len(t) > 2 and t[2] in '\\/'):
        kind, _, val = t.partition(':')
        kind = kind.lower()
        alias = {'c': 'creature', 'creature': 'creature', 'd': 'display', 'display': 'display', 'i': 'item', 'item': 'item',
                 'itemdisplay': 'itemdisplay', 'go': 'go', 'gameobject': 'go', 'godisplay': 'godisplay', 'race': 'race'}
        if kind in alias:
            if alias[kind] == 'race':
                return 'race', val
            try:
                return alias[kind], int(val)
            except ValueError:
                raise TargetError('%s: expected a number after %s:' % (text, kind))
    if '*' in t or '?' in t:
        return 'glob', t
    return 'path', t


def _index(cfg):
    try:
        return Index(cfg.index_path)
    except FileNotFoundError:
        return None


def _server_ids(cfg, sql):
    try:
        return serverdb.query(sql, cfg)
    except serverdb.ServerDBError as e:
        raise TargetError('needs the server database or a built index: %s' % e)


def resolve(text, cat, cfg):
    """[(role, path, archive)] for a target (duplicates removed, order kept)."""
    kind, val = parse(text)
    out = []
    if kind == 'path':
        p = cat.canonical(val)
        out.append(('file', p, cat.archive_of(p)))
        if p.lower().endswith('.m2'):
            out += cat._embedded_textures(p) if cat.archive_of(p) else []
            folder = p.rsplit('\\', 1)[0]
            for did, scale, skins in cat.model_variations(p):
                for s in skins:
                    sp = cat.canonical(folder + '\\' + s + '.blp')
                    out.append(('skin (display %d)' % did, sp, cat.archive_of(sp)))
    elif kind == 'glob':
        out += [('file', p, cat.archive_of(p)) for p in cat.client.glob(val)]
    elif kind == 'display':
        info = cat.creature_display(val)
        if info is None:
            raise TargetError('CreatureDisplayInfo %d does not exist' % val)
        out += info['files']
    elif kind == 'creature':
        ix = _index(cfg)
        rows = ix.q('SELECT display1, display2, display3, display4 FROM creature WHERE entry=?', val) if ix and ix.has_server() else None
        if rows is None:
            rows = _server_ids(cfg, 'SELECT display_id1, display_id2, display_id3, display_id4 FROM creature_template '
                                    'WHERE entry=%d ORDER BY patch DESC LIMIT 1' % val)
        if not rows:
            raise TargetError('creature %d not found' % val)
        for d in rows[0]:
            if d and int(d):
                info = cat.creature_display(int(d))
                if info:
                    out += info['files']
    elif kind in ('itemdisplay', 'item'):
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
        out += info['files']
    elif kind in ('go', 'godisplay'):
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
        if info is None:
            raise TargetError('GameObjectDisplayInfo %d does not exist' % did)
        out += info['files']
    elif kind == 'race':
        parts = val.split(':')
        sex = None
        if len(parts) > 1:
            sex = {'male': 0, 'm': 0, '0': 0, 'female': 1, 'f': 1, '1': 1}.get(parts[1].lower())
        race = None
        for r in cat.races():
            if parts[0].isdigit() and r.id == int(parts[0]) or parts[0].lower() in (r.name.lower(), r.client_file_string.lower()):
                race = r
        if race is None:
            raise TargetError('race %s not found' % val)
        for s in ([sex] if sex is not None else [0, 1]):
            disp = race.male_display_id if s == 0 else race.female_display_id
            info = cat.creature_display(disp)
            if info:
                out += info['files']
            for row in cat.char_sections(race.id, s):
                out += cat.section_files(row)
    seen = set()
    uniq = []
    for role, p, a in out:
        if p.upper() not in seen:
            seen.add(p.upper())
            uniq.append((role, p, a))
    return uniq


def out_path(out_dir, archive_path, ext=None):
    """<out_dir>\\<archive path> (optionally with a new extension), folders created."""
    rel = resolve_name(archive_path)
    if ext:
        rel = os.path.splitext(rel)[0] + ext
    p = os.path.join(out_dir, *rel.split('\\'))
    os.makedirs(os.path.dirname(p), exist_ok=True)
    return p
