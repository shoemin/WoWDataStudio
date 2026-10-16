# SPDX-License-Identifier: MIT
"""Prints the MmapTileHeader + Detour dtMeshHeader of .mmtile files side by side.

    python mmtileinfo.py FILE [FILE ...]
"""
import struct
import sys

TILE = struct.Struct('<4sIIII')              # mmapMagic, dtVersion, mmapVersion, size, usesLiquids
MESH = struct.Struct('<4si3iI9i3f3f3ff')     # dtMeshHeader (Detour version 7)
MESH_NAMES = ['magic', 'version', 'x', 'y', 'layer', 'userId', 'polyCount', 'vertCount', 'maxLinkCount',
              'detailMeshCount', 'detailVertCount', 'detailTriCount', 'bvNodeCount', 'offMeshConCount',
              'offMeshBase', 'walkableHeight', 'walkableRadius', 'walkableClimb', 'bmin0', 'bmin1', 'bmin2',
              'bmax0', 'bmax1', 'bmax2', 'bvQuantFactor']


def info(path):
    b = open(path, 'rb').read()
    t = TILE.unpack_from(b, 0)
    m = MESH.unpack_from(b, TILE.size)
    d = {'file_size': len(b), 'mmapMagic': t[0], 'dtVersion': t[1], 'mmapVersion': t[2], 'size': t[3],
         'usesLiquids': t[4]}
    d.update(zip(MESH_NAMES, m))
    return d


def main():
    rows = [info(p) for p in sys.argv[1:]]
    for k in rows[0]:
        vals = [r[k] for r in rows]
        mark = '' if all(v == vals[0] for v in vals) else '   <-- differs'
        print('%-16s %s%s' % (k, '  '.join('%-14s' % (('%.4f' % v) if isinstance(v, float) else v) for v in vals), mark))


if __name__ == '__main__':
    main()
