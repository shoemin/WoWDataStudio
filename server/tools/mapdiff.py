# SPDX-License-Identifier: MIT
"""Explains differences between two sets of server .map files (format MAPS / z1.4) section by section.

    python mapdiff.py <dirA> <dirB> [file ...]

Prints, over all differing files: which sections differ (header / area / height / liquid / holes),
and for the height section the max absolute height difference after decoding (float, int16, int8 storage).
"""
import os
import struct
import sys
from collections import Counter

HDR = struct.Struct('<4s4s8I')


def sections(b):
    h = HDR.unpack_from(b, 0)
    out = {'header': b[:HDR.size]}
    names = ['area', 'height', 'liquid', 'holes']
    for i, n in enumerate(names):
        off, size = h[2 + 2 * i], h[3 + 2 * i]
        out[n] = b[off:off + size]
    return h, out


def decode_heights(sec):
    """returns list of 145 V9 + 128 V8 heights (or the flat height)"""
    fourcc, flags, gh, gmax = struct.unpack_from('<4sIff', sec, 0)
    p = 16
    if flags & 1:
        return ('flat', [gh])
    if flags & 2:
        n = 129 * 129 + 128 * 128
        vals = struct.unpack_from('<%dH' % n, sec, p)
        step = (gmax - gh) / 65535.0
        return ('int16', [gh + v * step for v in vals])
    if flags & 4:
        n = 129 * 129 + 128 * 128
        vals = struct.unpack_from('<%dB' % n, sec, p)
        step = (gmax - gh) / 255.0
        return ('int8', [gh + v * step for v in vals])
    n = 129 * 129 + 128 * 128
    return ('float', list(struct.unpack_from('<%df' % n, sec, p)))


def main():
    a, b = sys.argv[1], sys.argv[2]
    files = sys.argv[3:] or sorted(f for f in os.listdir(a) if f.endswith('.map'))
    stats = Counter()
    examples = {}
    maxdiff_overall = 0.0
    storage = Counter()
    hdrflags = Counter()
    for f in files:
        pa, pb = os.path.join(a, f), os.path.join(b, f)
        if not os.path.exists(pb):
            stats['missing_in_B'] += 1
            continue
        ba, bb = open(pa, 'rb').read(), open(pb, 'rb').read()
        if ba == bb:
            stats['identical'] += 1
            continue
        stats['differ'] += 1
        ha, sa = sections(ba)
        hb, sb = sections(bb)
        key = tuple(n for n in ('header', 'area', 'height', 'liquid', 'holes') if sa[n] != sb[n])
        stats[key] += 1
        examples.setdefault(key, f)
        if sa['height'] != sb['height'] and sa['height'] and sb['height']:
            ka, va = decode_heights(sa['height'])
            kb, vb = decode_heights(sb['height'])
            storage[(ka, kb)] += 1
            fa = struct.unpack_from('<I', sa['height'], 4)[0]
            fb = struct.unpack_from('<I', sb['height'], 4)[0]
            hdrflags[(fa, fb)] += 1
            if len(va) == len(vb):
                m = max(abs(x - y) for x, y in zip(va, vb))
                maxdiff_overall = max(maxdiff_overall, m)
    for k, v in stats.most_common():
        print('%-45s %6d   e.g. %s' % (k, v, examples.get(k, '')))
    print('height storage (A,B):', dict(storage))
    print('height flags (A,B):', dict(hdrflags))
    print('max decoded height difference: %.6f' % maxdiff_overall)


if __name__ == '__main__':
    main()
