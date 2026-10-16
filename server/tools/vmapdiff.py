# SPDX-License-Identifier: MIT
"""Explains differences between two sets of assembled vmaps (.vmtile, VMAP_7.0) spawn by spawn.

    python vmapdiff.py <dirA> <dirB> [file.vmtile ...]

A .vmtile is: magic[8], uint32 count, then per spawn: uint32 flags, uint16 adtId, uint32 ID, float pos[3],
float rot[3], float scale, (float bound[6] if flags & 4), uint32 nameLen, name, uint32 BIH node index.
"""
import os
import struct
import sys
from collections import Counter

MOD_HAS_BOUND = 4


def parse_tile(b):
    assert b[:8] == b'VMAP_7.0', b[:8]
    n = struct.unpack_from('<I', b, 8)[0]
    p = 12
    out = []
    for _ in range(n):
        flags, adt, sid = struct.unpack_from('<IHI', b, p)
        p += 10
        pos = struct.unpack_from('<3f', b, p); p += 12
        rot = struct.unpack_from('<3f', b, p); p += 12
        scale = struct.unpack_from('<f', b, p)[0]; p += 4
        bound = None
        if flags & MOD_HAS_BOUND:
            bound = struct.unpack_from('<6f', b, p); p += 24
        ln = struct.unpack_from('<I', b, p)[0]; p += 4
        name = b[p:p + ln].decode('latin-1'); p += ln
        node = struct.unpack_from('<I', b, p)[0]; p += 4
        out.append(dict(flags=flags, adt=adt, id=sid, pos=pos, rot=rot, scale=scale, bound=bound, name=name, node=node))
    assert p == len(b), (p, len(b))
    return out


def main():
    a, b = sys.argv[1], sys.argv[2]
    files = sys.argv[3:] or sorted(f for f in os.listdir(a) if f.endswith('.vmtile'))
    c = Counter()
    ex = {}
    maxpos = 0.0
    for f in files:
        pb = os.path.join(b, f)
        if not os.path.exists(pb):
            continue
        ba, bb = open(os.path.join(a, f), 'rb').read(), open(pb, 'rb').read()
        if ba == bb:
            c['identical'] += 1
            continue
        ta, tb = parse_tile(ba), parse_tile(bb)
        if len(ta) != len(tb):
            c['different spawn count'] += 1
            ex.setdefault('different spawn count', f)
            continue
        ka = {(s['id'], s['name']): s for s in ta}
        kb = {(s['id'], s['name']): s for s in tb}
        if set(ka) != set(kb):
            c['different spawn set'] += 1
            ex.setdefault('different spawn set', f)
            continue
        order = [(s['id'], s['name']) for s in ta] == [(s['id'], s['name']) for s in tb]
        fields = set()
        for k in ka:
            x, y = ka[k], kb[k]
            for fld in ('flags', 'adt', 'pos', 'rot', 'scale', 'bound', 'node'):
                if x[fld] != y[fld]:
                    fields.add(fld)
                    if fld == 'bound' and x['bound'] and y['bound']:
                        maxpos = max(maxpos, max(abs(p - q) for p, q in zip(x['bound'], y['bound'])))
        key = 'same spawns, order %s, fields differ: %s' % ('same' if order else 'DIFFERENT', ','.join(sorted(fields)) or '-')
        c[key] += 1
        ex.setdefault(key, f)
    for k, v in c.most_common():
        print('%6d  %s   e.g. %s' % (v, k, ex.get(k, '')))
    print('max bound difference: %g' % maxpos)


if __name__ == '__main__':
    main()
