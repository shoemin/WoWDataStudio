# SPDX-License-Identifier: MIT
"""Change report: staging extraction output vs the live server data (READ-ONLY on both sides).

    python compare_data.py --staging DIR --server DIR [--baseline DIR] [--kinds maps,vmaps,mmaps,dbc]
                           [--maps 0,1] [--json OUT.json] [--txt OUT.txt]

Layout compared (staging or baseline / server):
    maps   <dir>/maps/*.map      server/maps
    vmaps  <dir>/vmaps/*         server/vmaps    (.vmtree, .vmtile, models, temp_gameobject_models)
    mmaps  <dir>/mmaps/*         server/mmaps    (.mmap, .mmtile)   -- staging holds only rebuilt tiles
    dbc    <dir>/dbc/*.dbc       server/5875/dbc

Per kind: changed / new (vs server, by size then SHA-1), only_in_server, identical. Map-specific files (names
starting with a 3-digit map id) are filtered by --maps; shared files (models, dbc) are always compared.
mmaps is a partial set in staging, so only_in_server is not reported for it.

--baseline: an extraction of the STOCK client with the same tools and options (WoWDataTool/server/baseline).
Each changed/new file is then also labelled 'edit' (differs from the baseline: caused by our client data) or
'drift' (same as the baseline: the live file came from another extractor version).

touched_tiles (map:x,y in MoveMapGen --tile order = ADT x,y) are tiles whose NAVMESH INPUT changed: .map files that
differ, .vmtile files whose spawn geometry differs (spawn IDs and BIH node indices are ignored: they shift globally
whenever one model is added), and tiles that place a changed model. Compared against the baseline when given
(edits only), else against the server. mmap_rebuild_tiles = touched tiles + their 8 neighbours.

Tile naming (from the extractor sources): .map and .mmtile = MMMYYXX, .vmtile = MMM_XX_YY, ADT = Name_XX_YY.
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys
from collections import Counter

KIND_DIRS = {'maps': ('maps', 'maps'), 'vmaps': ('vmaps', 'vmaps'), 'mmaps': ('mmaps', 'mmaps'),
             'dbc': ('dbc', os.path.join('5875', 'dbc'))}
PARTIAL_KINDS = {'mmaps'}
RE_MAP = re.compile(r'^(\d{3})(\d{2})(\d{2})\.(map|mmtile)$', re.I)
RE_VMTILE = re.compile(r'^(\d{3})_(\d{2})_(\d{2})\.vmtile$', re.I)
RE_MAPID = re.compile(r'^(\d{3})(?:[._\d]|$)')
_hash_cache = {}


def sha1(path):
    if path not in _hash_cache:
        h = hashlib.sha1()
        with open(path, 'rb') as f:
            for b in iter(lambda: f.read(1 << 20), b''):
                h.update(b)
        _hash_cache[path] = h.hexdigest()
    return _hash_cache[path]


def same_file(a, b):
    return os.path.isfile(a) and os.path.isfile(b) and os.path.getsize(a) == os.path.getsize(b) and sha1(a) == sha1(b)


def map_id_of(name, kind):
    """map id for map-specific files, None for shared files (models, dbc)"""
    if kind == 'dbc':
        return None
    if name.lower().endswith(('.map', '.mmtile', '.mmap', '.vmtree', '.vmtile')):
        m = RE_MAPID.match(name)
        if m:
            return int(m.group(1))
    return None


def tile_of(name):
    """(map, x, y) in MoveMapGen/ADT order, or None"""
    m = RE_MAP.match(name)
    if m:
        return int(m.group(1)), int(m.group(3)), int(m.group(2))
    m = RE_VMTILE.match(name)
    if m:
        return int(m.group(1)), int(m.group(2)), int(m.group(3))
    return None


def vmtile_spawns(path):
    """spawn geometry of a .vmtile (VMAP_7.0) without spawn IDs / BIH node indices"""
    b = open(path, 'rb').read()
    if b[:8] != b'VMAP_7.0':
        return None
    n = struct.unpack_from('<I', b, 8)[0]
    p, out = 12, Counter()
    for _ in range(n):
        flags, adt = struct.unpack_from('<IH', b, p)
        p += 10                                   # flags, adtId, ID (ignored)
        geo = b[p:p + 28]                         # pos, rot, scale
        p += 28
        if flags & 4:                             # MOD_HAS_BOUND
            geo += b[p:p + 24]
            p += 24
        ln = struct.unpack_from('<I', b, p)[0]
        name = b[p + 4:p + 4 + ln]
        p += 4 + ln + 4                           # name + BIH node index (ignored)
        out[(flags, adt, geo, name)] += 1
    return out


def list_files(d):
    if not os.path.isdir(d):
        return None
    return {n for n in os.listdir(d) if os.path.isfile(os.path.join(d, n))}


def compare_kind(kind, staging, server, baseline, maps):
    sd = os.path.join(staging, KIND_DIRS[kind][0])
    ld = os.path.join(server, KIND_DIRS[kind][1])
    bd = os.path.join(baseline, KIND_DIRS[kind][0]) if baseline else None
    sfiles = list_files(sd)
    if sfiles is None:
        return None
    lfiles = list_files(ld) or set()

    def wanted(n):
        if maps is None:
            return True
        mid = map_id_of(n, kind)
        return mid is None or mid in maps

    res = {'staging_dir': sd, 'server_dir': ld, 'changed': [], 'new': [], 'only_in_server': [], 'identical': 0}
    if bd and os.path.isdir(bd):
        res['baseline_dir'] = bd
        res['edit'], res['drift'] = [], []
    for n in sorted(sfiles):
        if not wanted(n):
            continue
        sp, lp = os.path.join(sd, n), os.path.join(ld, n)
        if n not in lfiles:
            res['new'].append(n)
        elif not same_file(sp, lp):
            res['changed'].append(n)
        else:
            res['identical'] += 1
            continue
        if 'edit' in res:
            (res['drift'] if same_file(sp, os.path.join(bd, n)) else res['edit']).append(n)
    if kind not in PARTIAL_KINDS:
        for n in sorted(lfiles - sfiles):
            if wanted(n):
                res['only_in_server'].append(n)
    return res


def touched_tiles(staging, ref_root, ref_is_server, maps):
    """navmesh-input tiles that differ between staging and the reference (baseline or server)"""
    touched = set()
    sub = lambda kind: os.path.join(ref_root, KIND_DIRS[kind][1] if ref_is_server else KIND_DIRS[kind][0])
    ok = lambda mid: maps is None or mid in maps
    # terrain
    sd, rd = os.path.join(staging, 'maps'), sub('maps')
    s, r = list_files(sd), list_files(rd) or set()
    if s is not None:
        for n in (s | r):
            t = tile_of(n)
            if t and ok(t[0]) and not same_file(os.path.join(sd, n), os.path.join(rd, n)):
                touched.add(t)
    # vmaps: spawn geometry per tile + tiles that place a changed model
    sd, rd = os.path.join(staging, 'vmaps'), sub('vmaps')
    s, r = list_files(sd), list_files(rd) or set()
    if s is not None:
        changed_models = set()
        for n in s | r:
            low = n.lower()
            if low.endswith(('.vmtile', '.vmtree')) or low == 'temp_gameobject_models':
                continue
            if not same_file(os.path.join(sd, n), os.path.join(rd, n)):
                changed_models.add((n[:-4] if low.endswith('.vmo') else n).encode('latin-1').lower())
        for n in s | r:
            t = tile_of(n)
            if not t or not n.lower().endswith('.vmtile') or not ok(t[0]):
                continue
            a = vmtile_spawns(os.path.join(sd, n)) if n in s else Counter()
            b = vmtile_spawns(os.path.join(rd, n)) if n in r else Counter()
            if a != b:
                touched.add(t)
            elif changed_models and any(k[3].rstrip(b'\0').lower() in changed_models for k in a):
                touched.add(t)
    return touched


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--staging', required=True)
    ap.add_argument('--server', required=True)
    ap.add_argument('--baseline')
    ap.add_argument('--kinds', default='maps,vmaps,mmaps,dbc')
    ap.add_argument('--maps', default='')
    ap.add_argument('--json')
    ap.add_argument('--txt')
    a = ap.parse_args()
    maps = {int(x) for x in a.maps.replace(' ', '').split(',') if x != ''} or None
    baseline = a.baseline if a.baseline and os.path.isdir(a.baseline) else None
    report = {'staging': os.path.abspath(a.staging), 'server': os.path.abspath(a.server),
              'baseline': os.path.abspath(baseline) if baseline else None,
              'maps_filter': sorted(maps) if maps else 'all', 'kinds': {}}
    kinds = [k.strip() for k in a.kinds.split(',') if k.strip()]
    for kind in kinds:
        if kind not in KIND_DIRS:
            sys.exit('unknown kind: ' + kind)
        r = compare_kind(kind, a.staging, a.server, baseline, maps)
        if r is not None:
            report['kinds'][kind] = r
    touched = set()
    if 'maps' in kinds or 'vmaps' in kinds:
        touched = touched_tiles(a.staging, baseline or a.server, baseline is None, maps)
    rebuild = set()
    for (m, x, y) in touched:
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if 0 <= x + dx < 64 and 0 <= y + dy < 64:
                    rebuild.add((m, x + dx, y + dy))
    fmt = lambda st: ['%d:%d,%d' % t for t in sorted(st)]
    report['touched_tiles_reference'] = 'baseline' if baseline else 'server'
    report['touched_tiles'] = fmt(touched)
    report['mmap_rebuild_tiles'] = fmt(rebuild)

    lines = ['Change report: staging %s vs server %s' % (report['staging'], report['server']),
             'baseline: %s' % (report['baseline'] or '-'), 'map filter: %s' % report['maps_filter'], '']
    for kind, r in report['kinds'].items():
        extra = ''
        if 'edit' in r:
            extra = '   (of changed+new: %d edit = differ from the stock-client baseline, %d drift = same as baseline)' % (
                len(r['edit']), len(r['drift']))
        lines.append('[%s] changed %d, new %d, only in server %d, identical %d%s' % (
            kind, len(r['changed']), len(r['new']), len(r['only_in_server']), r['identical'], extra))
        edit = set(r.get('edit', []))
        for key in ('changed', 'new', 'only_in_server'):
            for n in r[key]:
                tag = ('edit' if n in edit else 'drift') if 'edit' in r and key != 'only_in_server' else ''
                lines.append('  %-15s %-5s %s' % (key, tag, n))
        lines.append('')
    lines.append('touched tiles vs %s (map:x,y): %s' % (report['touched_tiles_reference'],
                                                      ' '.join(report['touched_tiles']) or '-'))
    lines.append('mmap rebuild set incl. 8 neighbours: %d tiles' % len(rebuild))
    text = '\n'.join(lines) + '\n'
    if a.txt:
        with open(a.txt, 'w') as f:
            f.write(text)
    if a.json:
        with open(a.json, 'w') as f:
            json.dump(report, f, indent=1)
    for l in lines:                       # console: summary only (full lists in --txt / --json)
        if l.startswith('touched tiles') and len(report['touched_tiles']) > 12:
            l = 'touched tiles vs %s: %d, first: %s ...' % (report['touched_tiles_reference'], len(touched),
                                                            ' '.join(report['touched_tiles'][:12]))
        if not l.startswith('  '):
            print(l)


if __name__ == '__main__':
    main()
