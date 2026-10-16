# SPDX-License-Identifier: MIT
"""Classifies differences between two sets of .mmtile files (MMAP v6, Detour v7, 64-bit poly refs).

    python mmapdiff.py <dirA> <dirB> [file.mmtile ...]

Classes per file:
  identical        byte-identical
  equivalent       vertices, polygons, detail meshes/verts/tris and off-mesh connections identical; only the
                   link section (rebuilt by dtNavMesh::addTile when the server loads the tile), BV-tree bounds
                   and/or header bounds differ
  geometry-diff    same counts but vertex/polygon/detail data differ
  topology-diff    different polygon/vertex counts
"""
import os
import struct
import sys
from collections import Counter


def sections(b):
    a = b[20:]
    pc, vc, mlc, dmc, dvc, dtc, bvc, omc, omb = struct.unpack_from('<9i', a, 24)
    sizes = [('header', 100), ('verts', 12 * vc), ('polys', 32 * pc), ('links', 16 * mlc), ('dmeshes', 12 * dmc),
             ('dverts', 12 * dvc), ('dtris', 4 * dtc), ('bvtree', 16 * bvc), ('offmesh', 36 * omc)]
    out, p = {}, 0
    for n, s in sizes:
        out[n] = a[p:p + s]
        p += s
    out['counts'] = (pc, vc, dmc, dvc, dtc, omc)
    return out


def classify(x, y):
    if x == y:
        return 'identical'
    sx, sy = sections(x), sections(y)
    if sx['counts'] != sy['counts']:
        return 'topology-diff'
    if all(sx[k] == sy[k] for k in ('verts', 'polys', 'dmeshes', 'dverts', 'dtris', 'offmesh')):
        return 'equivalent'
    return 'geometry-diff'


def main():
    a, b = sys.argv[1], sys.argv[2]
    files = sys.argv[3:] or sorted(f for f in os.listdir(a) if f.endswith('.mmtile'))
    c, ex = Counter(), {}
    for f in files:
        pb = os.path.join(b, f)
        if not os.path.exists(pb):
            c['only in A'] += 1
            continue
        k = classify(open(os.path.join(a, f), 'rb').read(), open(pb, 'rb').read())
        c[k] += 1
        ex.setdefault(k, []).append(f)
    for k, v in c.most_common():
        print('%-14s %5d   e.g. %s' % (k, v, ' '.join(ex.get(k, [])[:6])))


if __name__ == '__main__':
    main()
