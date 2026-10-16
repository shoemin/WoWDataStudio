"""Test oracle for the C# M2 reader / writer / validator / track evaluation: the Python reference results (wowdata/m2),
written into <outdir> (app/.testcache/oracle/m2/; it holds data derived from Blizzard's files: keep it out of source control).

Usage:
    python make_m2.py <outdir>

Files written (done.txt last):
    all.json      every loadable .m2 of the client: [[path, size, sha256, serialize-identical, errors, warnings, notes,
                  sorted 'level|where' keys of the error and warning issues]]
    edits.json    SHA-256 of edited models re-serialised by the Python writer
    sample.json   ~260 models (the named ones plus a seeded random sample): counts and key values of every part the C#
                  port reads, the issues, and evaluated animation samples
"""
import hashlib
import json
import os
import random
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # oracle -> WoWData.Tests -> app -> WoWDataTool
for p in (os.getcwd(), TOOL):
    if p not in sys.path:
        sys.path.insert(0, p)

from wowdata import config, vfs   # noqa: E402
from wowdata.m2 import M2          # noqa: E402
from wowdata.m2.anim import evaluate   # noqa: E402
from wowdata.m2.validate import validate   # noqa: E402

BS = chr(92)
NAMED = [
    'Creature' + BS + 'Boar' + BS + 'Boar.m2',
    'Character' + BS + 'Orc' + BS + 'Male' + BS + 'OrcMale.m2',
    'Character' + BS + 'Human' + BS + 'Male' + BS + 'HumanMale.m2',
    'World' + BS + 'Kalimdor' + BS + 'Durotar' + BS + 'PassiveDoodads' + BS + 'Trees' + BS + 'DurotarPalm01.m2',
    'Item' + BS + 'ObjectComponents' + BS + 'Weapon' + BS + 'Axe_2H_Battle_A_01.m2',
    'Spells' + BS + 'FireBall_Missile_High.m2',
    'Spells' + BS + 'AbolishMagic_Base.m2',
    'Creature' + BS + 'Cupid' + BS + 'Cupid.m2',
    'Cameras' + BS + 'FlyByDwarf.m2',
]


def f(x):
    return [float(v) for v in np.asarray(x, np.float64).reshape(-1)]


def track_info(t):
    return [int(t.interpolation), int(t.global_sequence), len(t.ranges), len(t)]


def dump(m, data):
    d = {'version': m.version, 'name': m.model_name, 'global_flags': int(m.global_flags), 'size': len(data),
         'counts': {k: int(v) for k, v in m.counts().items()},
         'bounds': f(m.bounds_min) + f(m.bounds_max) + [float(m.bounds_radius)],
         'global_sequences': [int(x) for x in m.global_sequences]}
    s = m.sequences
    d['sequences'] = [[int(s['anim_id'][i]), int(s['sub_id'][i]), int(s['start'][i]), int(s['end'][i]), int(s['flags'][i]),
                       int(s['next_variant'][i]), float(s['move_speed'][i])] for i in range(len(s))]
    d['bones'] = []
    for i in range(len(m.bones)):
        b = m.bones[i]
        d['bones'].append([int(b.key_bone_id), int(b.flags), int(b.parent), int(b.submesh_id)] + f(b.pivot) +
                          track_info(b.translation) + track_info(b.rotation) + track_info(b.scale))
    v = m.vertices
    if len(v):
        d['vertex_sums'] = f(v['position'].astype(np.float64).sum(0)) + f(v['normal'].astype(np.float64).sum(0)) + \
            f(v['uv'].astype(np.float64).sum(0)) + [int(v['weights'].astype(np.int64).sum()), int(v['bones'].astype(np.int64).sum())]
        d['vertex_first'] = f(v['position'][0]) + [int(x) for x in v['weights'][0]] + [int(x) for x in v['bones'][0]] + f(v['normal'][0]) + f(v['uv'][0]) + f(v['uv2'][0])
        d['vertex_last'] = f(v['position'][-1]) + f(v['uv'][-1])
    d['views'] = []
    for vi in range(len(m.views)):
        w = m.views[vi]
        sm, tu = w.submeshes, w.texture_units
        d['views'].append({
            'counts': [len(w.indices), len(w.triangles), len(w.properties), len(sm), len(tu), int(w.bone_limit)],
            'index_sum': int(np.asarray(w.indices, np.int64).sum()), 'triangle_sum': int(np.asarray(w.triangles, np.int64).sum()),
            'submeshes': [[int(sm.flat[n][k]) for n in ('geoset_id', 'vertex_start', 'vertex_count', 'index_start', 'index_count', 'bone_count',
                                                     'bone_start', 'bone_influences', 'center_bone')] for k in range(len(sm))],
            'texunits': [[int(tu.flat[n][k]) for n in ('flags', 'priority', 'shader_id', 'submesh', 'geoset_index', 'color', 'render_flags',
                                                    'material_layer', 'texture_count', 'texture_lookup', 'tex_unit_lookup',
                                                    'transparency_lookup', 'texture_transform_lookup')] for k in range(len(tu))],
        })
    d['textures'] = [[int(m.textures['type'][i]), int(m.textures['flags'][i]), m.texture_name(i)] for i in range(len(m.textures))]
    d['render_flags'] = [[int(r['flags']), int(r['blend'])] for r in m.render_flags.flat]
    for k in ('animation_lookup', 'key_bone_lookup', 'replaceable_lookup', 'bone_lookup', 'texture_lookup', 'tex_unit_lookup',
              'transparency_lookup', 'texture_transform_lookup'):
        d[k] = [int(x) for x in getattr(m, k)]
    d['colors'] = [track_info(m.colors[i].color) + track_info(m.colors[i].alpha) for i in range(len(m.colors))]
    d['transparency'] = [track_info(m.transparency[i].weight) for i in range(len(m.transparency))]
    a = m.attachments
    d['attachments'] = [[int(a['id'][i]), int(a['bone'][i])] + f(a['position'][i]) for i in range(len(a))]
    d['serialize_sha256'] = hashlib.sha256(m.serialize()).hexdigest()
    d['issues'] = sorted('%s|%s' % (i.level, i.where) for i in validate(m))
    # animation samples: the Python evaluator on the keys of a range, at times strictly inside the range's key span
    samples = []
    rnd = random.Random(len(data))
    seqs = m.sequences
    animated = [i for i in range(len(m.bones)) if len(m.bones[i].rotation) or len(m.bones[i].translation)]
    for bi in animated[:4]:
        b = m.bones[bi]
        for path, tr in (('translation', b.translation), ('rotation', b.rotation), ('scale', b.scale)):
            if not len(tr) or tr.global_sequence != -1:
                continue
            ranges = tr.ranges
            for si in range(min(len(seqs), 3)):
                if len(ranges) and si >= len(ranges):
                    continue
                lo, hi = (int(ranges[si][0]), int(ranges[si][1])) if len(ranges) else (0, len(tr) - 1)
                if hi <= lo or hi >= len(tr):
                    continue
                ts = tr.timestamps[lo:hi + 1].astype(np.float64)
                vs = tr.values[lo:hi + 1]
                if ts[-1] <= ts[0]:
                    continue
                for _ in range(3):
                    t = int(rnd.uniform(ts[0], ts[-1]))
                    val = evaluate(ts, vs, t, tr.interpolation, quaternion=False)
                    samples.append([bi, path, si, int(seqs['start'][si]), t, f(val)])
    d['anim_samples'] = samples
    return d


def main(out):
    os.makedirs(out, exist_ok=True)
    cfg = config.load()
    c = vfs.ClientData(cfg.data_dir, cache_dir=cfg.cache_dir)
    names = sorted(n for n in c.names() if n.lower().endswith('.m2'))
    rows = []
    for n in names:
        data = c.read(n)
        m = M2.parse(data)
        issues = validate(m)
        lv = [i.level for i in issues]
        keys = sorted('%s|%s' % (i.level, i.where) for i in issues if i.level != 'note')
        rows.append([n, len(data), hashlib.sha256(data).hexdigest(), m.serialize() == data, lv.count('error'), lv.count('warning'),
                     lv.count('note'), keys])
    with open(os.path.join(out, 'all.json'), 'w', encoding='utf-8') as fh:
        json.dump(rows, fh)
    rnd = random.Random(20261010)
    upper = {n.upper(): n for n in names}
    pick = [upper[p.upper()] for p in NAMED if p.upper() in upper]
    rest = [n for n in names if n not in pick]
    pick += rnd.sample(rest, 250)
    sample = {}
    for n in pick:
        data = c.read(n)
        sample[n] = dump(M2.parse(data), data)
    with open(os.path.join(out, 'sample.json'), 'w', encoding='utf-8') as fh:
        json.dump(sample, fh)
    # edits: the writer must lay an edited model out exactly like the Python writer
    boar = M2.parse(c.read(NAMED[0]))
    boar.name = b'BoarEditedByTheTests' + bytes(1)
    v = boar.vertices
    v['position'][0] = (1.5, -2.25, 3.0)
    edits = {'rename_and_move_vertex': hashlib.sha256(boar.serialize()).hexdigest()}
    with open(os.path.join(out, 'edits.json'), 'w', encoding='utf-8') as fh:
        json.dump(edits, fh)
    with open(os.path.join(out, 'done.txt'), 'w') as fh:
        fh.write('%d models, %d sampled\n' % (len(rows), len(sample)))


if __name__ == '__main__':
    main(sys.argv[1])
