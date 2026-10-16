"""Semantic, chunk-level diff of two 1.12 map files (ADT, WDT or WDL).

Compares what the client reads (see validate.py), not raw bytes: names are resolved through their index
tables, placements are compared by record, terrain per MCNK and per sub-chunk. Offsets and sizes that only
move because something else changed are reported as layout, not as content. MIT.
"""
import struct

from . import formats as F


class Diff:
    def __init__(self, a_name, b_name):
        self.a_name, self.b_name = a_name, b_name
        self.lines = []          # (section, message)
        self.layout = []         # offset/size-only differences
        self.identical_bytes = False

    def add(self, section, msg):
        self.lines.append((section, msg))

    @property
    def same_content(self):
        return not self.lines

    def summary(self):
        if self.identical_bytes:
            return 'byte-identical'
        if not self.lines:
            return 'same content (layout differs: %d item(s))' % len(self.layout) if self.layout else 'same content'
        return '%d content difference(s), %d layout difference(s)' % (len(self.lines), len(self.layout))

    def as_dict(self):
        return {'a': self.a_name, 'b': self.b_name, 'summary': self.summary(), 'identical_bytes': self.identical_bytes,
                'content': [{'section': s, 'message': m} for s, m in self.lines],
                'layout': [{'section': s, 'message': m} for s, m in self.layout]}


def _f(v):
    return ('%.4f' % v).rstrip('0').rstrip('.') if isinstance(v, float) else str(v)


def _cmp_seq(d, section, a, b, label, limit=6):
    if a == b:
        return
    if len(a) != len(b):
        d.add(section, '%s: %d -> %d entries' % (label, len(a), len(b)))
    shown = 0
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y:
            if shown < limit:
                d.add(section, '%s[%d]: %s -> %s' % (label, i, x, y))
            shown += 1
    if shown > limit:
        d.add(section, '%s: %d more changed entries' % (label, shown - limit))


def _floats_delta(a, b):
    n = 0
    mx = 0.0
    for x, y in zip(a, b):
        if x != y and not (x != x and y != y):
            n += 1
            mx = max(mx, abs(x - y))
    return n, mx


# --------------------------------------------------------------------------------------------- ADT
def _resolved_doodads(adt):
    out = []
    for r in adt.mddf:
        name = adt.mmdx.get(adt.mmid[r['nameId']], '?') if r['nameId'] < len(adt.mmid) else '?'
        out.append(dict(r, name=_norm_model(name)))
    return out


def _norm_model(name):
    # the client asks for .mdx/.mdl and loads .m2 (M2Cache_GetShared @00706a50), case-insensitively
    n = name.upper().replace('/', chr(92))
    if n.endswith('.MDX') or n.endswith('.MDL'):
        n = n[:-4] + '.M2'
    return n


def _resolved_wmos(adt):
    out = []
    for r in adt.modf:
        name = adt.mwmo.get(adt.mwid[r['nameId']], '?') if r['nameId'] < len(adt.mwid) else '?'
        out.append(dict(r, name=name.upper()))
    return out


def _placement_key(r):
    return {k: v for k, v in r.items() if k != 'nameId'}


def _mcnk_parts(b, adt, c):
    """The client-visible content of one MCNK, offsets resolved."""
    h = c.h
    fl = h['flags']
    parts = {}
    o = c.offset
    parts['header'] = {k: v for k, v in h.items() if not (k.startswith('ofs') or k.startswith('size'))}
    parts['header']['lowQualityMap'] = c.low_quality_map.hex()
    parts['header']['noEffectDoodad'] = c.no_effect_doodad
    p = o + h['ofsHeight'] + 8
    parts['MCVT'] = struct.unpack_from('<145f', b, p) if p + F.MCVT_SIZE <= len(b) else ()
    p = o + h['ofsNormal'] + 8
    parts['MCNR'] = b[p:p + F.MCNR_SIZE]
    layers = []
    alpha = []
    p = o + h['ofsLayer'] + 8
    mcal = o + h['ofsAlpha'] + 8
    for L in range(h['nLayers']):
        if p + 16 * (L + 1) > len(b):
            break
        tid, lf, oa, eff = struct.unpack_from('<4I', b, p + 16 * L)
        tex = adt.textures[tid].upper() if tid < len(adt.textures) else '#%d' % tid
        layers.append((tex, lf, eff))
        if lf & 0x100:
            alpha.append(b[mcal + oa: mcal + oa + F.ALPHA_LAYER_SIZE])
    parts['MCLY'] = layers
    parts['MCAL'] = alpha
    p = o + h['ofsShadow'] + 8
    parts['MCSH'] = b[p:p + F.MCSH_SIZE] if fl & 1 else b''
    n = h['nDoodadRefs'] + h['nMapObjRefs']
    p = o + h['ofsRefs'] + 8
    refs = struct.unpack_from('<%dI' % n, b, p) if p + 4 * n <= len(b) else ()
    dd = [adt.mddf[r]['uid'] if r < len(adt.mddf) else -1 for r in refs[:h['nDoodadRefs']]]
    ww = [adt.modf[r]['uid'] if r < len(adt.modf) else -1 for r in refs[h['nDoodadRefs']:]]
    parts['MCRF'] = (sorted(dd), sorted(ww))
    nliq = sum(1 for bit in F.MCNK_LIQUID_FLAGS if fl & bit)
    p = o + h['ofsLiquid'] + 8
    parts['MCLQ'] = [b[p + q * F.MCLQ_LAYER_SIZE: p + (q + 1) * F.MCLQ_LAYER_SIZE] for q in range(nliq)]
    p = o + h['ofsSndEmitters'] + 8
    parts['MCSE'] = b[p:p + F.MCSE_SIZE * h['nSndEmitters']]
    return parts


def _nibble_diff(a, b):
    n = 0
    mx = 0
    for x, y in zip(a, b):
        if x != y:
            for s in (0, 4):
                dx = abs(((x >> s) & 0xF) - ((y >> s) & 0xF))
                if dx:
                    n += 1
                    mx = max(mx, dx)
    return n, mx


def diff_adt(a_bytes, b_bytes, a_name='a', b_name='b'):
    d = Diff(a_name, b_name)
    if a_bytes == b_bytes:
        d.identical_bytes = True
        return d
    A, B = F.Adt(a_bytes), F.Adt(b_bytes)
    ta = [m for _, m, _ in A.top if m != 'MCNK']
    tb = [m for _, m, _ in B.top if m != 'MCNK']
    if ta != tb or sum(m == 'MCNK' for _, m, _ in A.top) != sum(m == 'MCNK' for _, m, _ in B.top):
        d.add('chunks', 'top-level chunks %s -> %s' % (ta, tb))
    for k in F.MHDR_FIELDS:
        if A.mhdr.get(k) != B.mhdr.get(k):
            (d.layout if k.startswith('ofs') and k in dict(F.MHDR_CHUNKS) else d.lines).append(
                ('MHDR', '%s 0x%x -> 0x%x' % (k, A.mhdr.get(k, 0), B.mhdr.get(k, 0))))
    tex_a, tex_b = [t.upper() for t in A.textures], [t.upper() for t in B.textures]
    if sorted(tex_a) == sorted(tex_b) and tex_a != tex_b:
        d.layout.append(('MTEX', 'same %d textures in a different order (MCLY ids are resolved to names)' % len(tex_a)))
    else:
        _cmp_seq(d, 'MTEX', tex_a, tex_b, 'texture')
    da, db = _resolved_doodads(A), _resolved_doodads(B)
    wa, wb = _resolved_wmos(A), _resolved_wmos(B)
    if [x['nameId'] for x in da] != [x['nameId'] for x in db] or A.mmid != B.mmid or A.block('MMDX') != B.block('MMDX'):
        d.layout.append(('MMDX/MMID', 'name tables differ in layout'))
    if A.block('MWMO') != B.block('MWMO') or A.mwid != B.mwid:
        d.layout.append(('MWMO/MWID', 'name tables differ in layout'))
    _diff_placements(d, 'MDDF', da, db)
    _diff_placements(d, 'MODF', wa, wb)
    # MCNK
    for i in range(256):
        ca = A.mcnk[i] if i < len(A.mcnk) else None
        cb = B.mcnk[i] if i < len(B.mcnk) else None
        sec = 'MCNK %d (%d,%d)' % (i, i % 16, i // 16)
        if ca is None or cb is None or not ca.h or not cb.h:
            if (ca is None) != (cb is None):
                d.add(sec, 'present in only one file')
            continue
        if ca.offset != cb.offset or ca.size != cb.size:
            d.layout.append((sec, 'offset/size 0x%x/%d -> 0x%x/%d' % (ca.offset, ca.size, cb.offset, cb.size)))
        pa, pb = _mcnk_parts(a_bytes, A, ca), _mcnk_parts(b_bytes, B, cb)
        for k in pa['header']:
            if pa['header'][k] != pb['header'].get(k):
                d.add(sec, 'header %s: %s -> %s' % (k, _f(pa['header'][k]), _f(pb['header'].get(k))))
        n, mx = _floats_delta(pa['MCVT'], pb['MCVT'])
        if n:
            d.add(sec, 'MCVT: %d of 145 heights changed (max %.4f)' % (n, mx))
        if pa['MCNR'] != pb['MCNR']:
            d.add(sec, 'MCNR: %d of 435 normal bytes changed' % sum(1 for x, y in zip(pa['MCNR'], pb['MCNR']) if x != y))
        if pa['MCLY'] != pb['MCLY']:
            d.add(sec, 'MCLY: %s -> %s' % (pa['MCLY'], pb['MCLY']))
        for L, (x, y) in enumerate(zip(pa['MCAL'], pb['MCAL'])):
            if x != y:
                n, mx = _nibble_diff(x, y)
                d.add(sec, 'MCAL alpha layer %d: %d of 4096 texels changed (max %d of 15)' % (L + 1, n, mx))
        if len(pa['MCAL']) != len(pb['MCAL']):
            d.add(sec, 'MCAL: %d -> %d alpha layers' % (len(pa['MCAL']), len(pb['MCAL'])))
        if pa['MCSH'] != pb['MCSH']:
            n = sum(bin(x ^ y).count('1') for x, y in zip(pa['MCSH'], pb['MCSH']))
            d.add(sec, 'MCSH: %d shadow bits changed%s' % (n, '' if len(pa['MCSH']) == len(pb['MCSH']) else ' (present in only one)'))
        if pa['MCRF'] != pb['MCRF']:
            d.add(sec, 'MCRF: doodad uids %s -> %s, WMO uids %s -> %s' % (pa['MCRF'][0], pb['MCRF'][0], pa['MCRF'][1], pb['MCRF'][1]))
        if pa['MCLQ'] != pb['MCLQ']:
            if len(pa['MCLQ']) != len(pb['MCLQ']):
                d.add(sec, 'MCLQ: %d -> %d liquid layers' % (len(pa['MCLQ']), len(pb['MCLQ'])))
            for q, (x, y) in enumerate(zip(pa['MCLQ'], pb['MCLQ'])):
                if x != y:
                    d.add(sec, 'MCLQ layer %d: %s' % (q, _liquid_delta(x, y)))
        if pa['MCSE'] != pb['MCSE']:
            d.add(sec, 'MCSE: sound emitters differ (%d -> %d bytes)' % (len(pa['MCSE']), len(pb['MCSE'])))
    return d


def _liquid_delta(x, y):
    out = []
    mn = struct.unpack_from('<2f', x), struct.unpack_from('<2f', y)
    if mn[0] != mn[1]:
        out.append('min/max %s -> %s' % (mn[0], mn[1]))
    hx = [struct.unpack_from('<f', x, 8 + i * 8 + 4)[0] for i in range(81)]
    hy = [struct.unpack_from('<f', y, 8 + i * 8 + 4)[0] for i in range(81)]
    n, mx = _floats_delta(hx, hy)
    if n:
        out.append('%d heights (max %.4f)' % (n, mx))
    vx = [x[8 + i * 8: 8 + i * 8 + 4] for i in range(81)]
    vy = [y[8 + i * 8: 8 + i * 8 + 4] for i in range(81)]
    n = sum(1 for p, q in zip(vx, vy) if p != q)
    if n:
        out.append('%d vertex depth/flow records' % n)
    t0 = F.MCLQ_TILES_OFS
    n = sum(1 for p, q in zip(x[t0:t0 + 64], y[t0:t0 + 64]) if p != q)
    if n:
        out.append('%d tile flag bytes' % n)
    if x[t0 + 64:] != y[t0 + 64:]:
        out.append('flow data (n_flowvs/flowvs) differs')
    return ', '.join(out) or 'differs'


def _diff_placements(d, section, a, b):
    if [_placement_key(x) for x in a] == [_placement_key(x) for x in b]:
        return
    ka = {x['uid']: x for x in a}
    kb = {x['uid']: x for x in b}
    if len(ka) == len(a) and len(kb) == len(b):
        for uid in sorted(set(ka) - set(kb)):
            d.add(section, 'removed uid %d %s' % (uid, ka[uid]['name']))
        for uid in sorted(set(kb) - set(ka)):
            d.add(section, 'added uid %d %s' % (uid, kb[uid]['name']))
        changed = 0
        for uid in sorted(set(ka) & set(kb)):
            x, y = _placement_key(ka[uid]), _placement_key(kb[uid])
            if x != y:
                changed += 1
                if changed <= 8:
                    fields = [k for k in x if x[k] != y.get(k)]
                    d.add(section, 'uid %d %s: %s' % (uid, x['name'], ', '.join('%s %s -> %s' % (k, x[k], y.get(k)) for k in fields)))
        if changed > 8:
            d.add(section, '%d more changed placements' % (changed - 8))
        if not (set(ka) ^ set(kb)) and not changed:
            d.layout.append((section, 'same placements in a different order'))
    else:
        _cmp_seq(d, section, [_placement_key(x) for x in a], [_placement_key(x) for x in b], 'record')


# --------------------------------------------------------------------------------------------- WDT / WDL
def diff_wdt(a_bytes, b_bytes, a_name='a', b_name='b'):
    d = Diff(a_name, b_name)
    if a_bytes == b_bytes:
        d.identical_bytes = True
        return d
    A, B = F.Wdt(a_bytes), F.Wdt(b_bytes)
    if [m for _, m, _ in A.top] != [m for _, m, _ in B.top]:
        d.add('chunks', '%s -> %s' % ([m for _, m, _ in A.top], [m for _, m, _ in B.top]))
    if A.mphd != B.mphd:
        d.add('MPHD', 'flags 0x%x -> 0x%x%s' % (A.mphd_flags or 0, B.mphd_flags or 0,
                                               '' if A.mphd[4:] == B.mphd[4:] else ' (other MPHD bytes differ)'))
    ta, tb = set(A.tiles()), set(B.tiles())
    if ta != tb:
        d.add('MAIN', 'tiles removed %s, added %s' % (sorted(ta - tb)[:10], sorted(tb - ta)[:10]))
    if A.main != B.main and ta == tb:
        d.add('MAIN', 'tile flag/asyncId values differ')
    if (A.mphd_flags or 0) & 1 or (B.mphd_flags or 0) & 1:
        if getattr(A, 'global_wmo_name', None) != getattr(B, 'global_wmo_name', None):
            d.add('MWMO', '%s -> %s' % (getattr(A, 'global_wmo_name', None), getattr(B, 'global_wmo_name', None)))
        if getattr(A, 'global_modf', None) != getattr(B, 'global_modf', None):
            d.add('MODF', 'global WMO placement differs')
    return d


def diff_wdl(a_bytes, b_bytes, a_name='a', b_name='b'):
    d = Diff(a_name, b_name)
    if a_bytes == b_bytes:
        d.identical_bytes = True
        return d
    A, B = F.Wdl(a_bytes), F.Wdl(b_bytes)
    ca, cb = [m for _, m, _ in A.top], [m for _, m, _ in B.top]
    sa = [m for m in ca if m != 'MARE' and m != 'MAHO']
    sb = [m for m in cb if m != 'MARE' and m != 'MAHO']
    if sa != sb:
        d.add('chunks', '%s -> %s' % (sa, sb))
    if ('MAHO' in ca) != ('MAHO' in cb):
        d.add('chunks', 'MAHO %s' % ('removed' if 'MAHO' in ca else 'added'))
    if A.table_magic != B.table_magic:
        d.add('layout', 'chunk at the offset-table position: %s -> %s' % (A.table_magic, B.table_magic))
    ta, tb = set(A.tiles()), set(B.tiles())
    if ta != tb:
        d.add('MAOF', 'tiles removed %s, added %s' % (sorted(ta - tb)[:10], sorted(tb - ta)[:10]))
    changed = 0
    for i in range(min(len(A.offsets), len(B.offsets))):
        oa, ob = A.offsets[i], B.offsets[i]
        if not oa or not ob:
            continue
        if oa != ob:
            d.layout.append(('MAOF', 'tile %d,%d offset 0x%x -> 0x%x' % (i % 64, i // 64, oa, ob)))
        ha = struct.unpack_from('<545h', a_bytes, oa + 8) if oa + 8 + F.WDL_MARE_DATA <= len(a_bytes) else ()
        hb = struct.unpack_from('<545h', b_bytes, ob + 8) if ob + 8 + F.WDL_MARE_DATA <= len(b_bytes) else ()
        if ha != hb:
            changed += 1
            if changed <= 10:
                n = sum(1 for x, y in zip(ha, hb) if x != y)
                mx = max((abs(x - y) for x, y in zip(ha, hb)), default=0)
                d.add('MARE', 'tile %d,%d: %d of 545 heights changed (max %d)' % (i % 64, i // 64, n, mx))
    if changed > 10:
        d.add('MARE', '%d more tiles with changed heights' % (changed - 10))
    return d


def diff_bytes(a_bytes, b_bytes, name, a_name='a', b_name='b'):
    low = name.lower()
    if low.endswith('.adt'):
        return diff_adt(a_bytes, b_bytes, a_name, b_name)
    if low.endswith('.wdt'):
        return diff_wdt(a_bytes, b_bytes, a_name, b_name)
    if low.endswith('.wdl'):
        return diff_wdl(a_bytes, b_bytes, a_name, b_name)
    raise ValueError('not a map file: %s' % name)
