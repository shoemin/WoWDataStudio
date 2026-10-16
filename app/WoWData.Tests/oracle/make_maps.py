"""Test oracle for the C# map formats, validator and diff (WoWData.Core\\Maps): the Python reference results
(wowdata/maps), written into <outdir> (app/.testcache/oracle/maps/; it holds data derived from Blizzard's files:
keep it out of source control).

Usage:
    python make_maps.py <outdir>

Files written (done.txt last):
    allclient.json   every map file of the client validated per map folder exactly like `wowdata map validate
                     --all-client` (validate_map_set): {files, findings: [[severity, code, file, where, message]],
                     sha256: {path: sha256}}
    variants\\*      real tiles, WDTs and WDLs with scripted edits and deliberate breakage (heights, placements, alpha,
                     layers, holes, liquids, emitters, WotLK leftovers, truncation...)
    variants.json    per variant: the base archive path, the file name, Python's findings for the variant alone and the
                     diff base -> variant (summary, content lines, layout lines, identical bytes)
"""
import hashlib
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # oracle -> WoWData.Tests -> app -> WoWDataTool
for p in (os.getcwd(), TOOL):
    if p not in sys.path:
        sys.path.insert(0, p)

from wowdata import config, vfs                 # noqa: E402
from wowdata.maps import formats as F           # noqa: E402
from wowdata.maps import validate as V          # noqa: E402
from wowdata.maps import diff as D              # noqa: E402

BS = chr(92)


def findings(report):
    return [[f.severity, f.code, f.file, f.where, f.message] for f in report.findings]


def u32(b, p):
    return struct.unpack_from('<I', b, p)[0]


def put32(b, p, v):
    struct.pack_into('<I', b, p, v & 0xFFFFFFFF)


def putf(b, p, v):
    struct.pack_into('<f', b, p, v)


def mcnk_base(adt, i):
    return adt.mcnk[i].offset


def adt_variants(c):
    """(name, base path, bytes) of edited/broken copies of real tiles."""
    out = []
    base = 'World' + BS + 'Maps' + BS + 'Azeroth' + BS + 'Azeroth_32_48.adt'
    b0 = c.read(base)
    A = F.Adt(b0)

    def v(name, fn, src=base, data=None):
        b = bytearray(data if data is not None else c.read(src))
        a = F.Adt(bytes(b))
        fn(b, a)
        out.append((name, src, bytes(b)))

    # terrain edits a modder makes
    def heights(b, a):
        for i in range(37):
            ch = a.mcnk[i]
            p = ch.offset + ch.h['ofsHeight'] + 8
            for k in range(145):
                putf(b, p + 4 * k, struct.unpack_from('<f', b, p + 4 * k)[0] + 5.0 + (k % 7) * 0.25)
    v('heights_raised.adt', heights)

    def move_doodads(b, a):
        mddf = a.blocks['MDDF'][0] + 8
        for i in range(4):
            p = mddf + i * 36 + 8
            putf(b, p, struct.unpack_from('<f', b, p)[0] + 2.5)
    v('doodads_moved.adt', move_doodads)

    def doodad_uid(b, a):
        put32(b, a.blocks['MDDF'][0] + 8 + 4, 987654321)
    v('doodad_replaced_uid.adt', doodad_uid)

    def rotate_wmo(b, a):
        if a.modf:
            p = a.blocks['MODF'][0] + 8 + 20 + 4
            putf(b, p, struct.unpack_from('<f', b, p)[0] + 45.0)
    v('wmo_turned.adt', rotate_wmo)

    def alpha(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['nLayers'] >= 2:
                ly = ch.offset + ch.h['ofsLayer'] + 8
                oa = u32(b, ly + 16 + 8)
                p = ch.offset + ch.h['ofsAlpha'] + 8 + oa
                for k in range(0, 300):
                    b[p + k] = (b[p + k] + 0x13) & 0xFF
                return
    v('alpha_painted.adt', alpha)

    def layer_texture(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['nLayers'] >= 2 and len(a.textures) > 1:
                ly = ch.offset + ch.h['ofsLayer'] + 8
                tid = u32(b, ly + 16)
                put32(b, ly + 16, (tid + 1) % len(a.textures))
                return
    v('layer_texture_replaced.adt', layer_texture)

    def holes(b, a):
        ch = a.mcnk[9]
        put32(b, ch.offset + 8 + 0x3C, 0x0660)
    v('holes_cut.adt', holes)

    def holes_high(b, a):
        ch = a.mcnk[12]
        put32(b, ch.offset + 8 + 0x3C, 0x10000)
    v('holes_high_bits.adt', holes_high)

    def area(b, a):
        ch = a.mcnk[10]
        put32(b, ch.offset + 8 + 0x34, ch.h['areaId'] + 1)
    v('area_changed.adt', area)

    def shadow(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['flags'] & 1:
                p = ch.offset + ch.h['ofsShadow'] + 8
                for k in range(40):
                    b[p + k] ^= 0x5A
                return
    v('shadows_changed.adt', shadow)

    def normals(b, a):
        ch = a.mcnk[3]
        p = ch.offset + ch.h['ofsNormal'] + 8
        for k in range(30):
            b[p + k] = (b[p + k] + 3) & 0xFF
    v('normals_changed.adt', normals)

    # breakage the validator must catch
    def five_layers(b, a):
        ch = a.mcnk[20]
        put32(b, ch.offset + 8 + 0x0C, 5)
    v('five_layers.adt', five_layers)

    def big_alpha(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['nLayers'] >= 3:
                ly = ch.offset + ch.h['ofsLayer'] + 8
                put32(b, ly + 16 + 8, 4096)
                put32(b, ly + 32 + 8, 8192)
                put32(b, ch.offset + 8 + 0x28, 8 + 4096 * 2)
                return
    v('big_alpha.adt', big_alpha)

    def compressed_alpha(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['nLayers'] >= 2:
                ly = ch.offset + ch.h['ofsLayer'] + 8
                put32(b, ly + 16 + 4, u32(b, ly + 16 + 4) | 0x200 | 0x4000)
                return
    v('compressed_alpha.adt', compressed_alpha)

    def bad_texture_id(b, a):
        ch = a.mcnk[30]
        ly = ch.offset + ch.h['ofsLayer'] + 8
        put32(b, ly, 250)
    v('bad_texture_id.adt', bad_texture_id)

    def mmid_bad(b, a):
        put32(b, a.blocks['MMID'][0] + 8, 3)
    v('mmid_bad_offset.adt', mmid_bad)

    def mddf_bad(b, a):
        p = a.blocks['MDDF'][0] + 8
        put32(b, p, 99999)
        put32(b, p + 36 + 4, u32(b, p + 72 + 4))       # duplicate uid
        struct.pack_into('<H', b, p + 36 * 3 + 34, 4)   # flags
        struct.pack_into('<H', b, p + 36 * 4 + 32, 0)   # scale 0
    v('mddf_broken.adt', mddf_bad)

    def mhdr_wotlk(b, a):
        put32(b, 0x14, 1)
        put32(b, 0x14 + 0x24, 0x40)
    v('mhdr_wotlk.adt', mhdr_wotlk)

    def mcin_bad(b, a):
        p = a.blocks['MCIN'][0] + 8
        put32(b, p + 16 * 5, u32(b, p + 16 * 4))       # chunk 5 -> chunk 4
        put32(b, p + 16 * 6 + 8, 1)                     # flags
        put32(b, p + 16 * 7 + 4, 12)                    # size
    v('mcin_broken.adt', mcin_bad)

    def mcnk_index(b, a):
        ch = a.mcnk[17]
        put32(b, ch.offset + 8 + 4, 3)
        put32(b, ch.offset + 8, ch.h['flags'] | 0x10000 | 0x400)
        put32(b, ch.offset + 8 + 0x74, 4)
        put32(b, ch.offset + 8 + 0x78, 7)
    v('mcnk_header_odd.adt', mcnk_index)

    def subofs(b, a):
        ch = a.mcnk[40]
        put32(b, ch.offset + 8 + 0x14, 0)                         # MCVT offset 0
        put32(b, ch.offset + 8 + 0x18, ch.h['ofsNormal'] + 4)     # MCNR overlapping, wrong magic
        put32(b, ch.offset + 8 + 0x20, 0x7FFFFF)                   # MCRF outside
    v('subchunk_offsets.adt', subofs)

    def version(b, a):
        put32(b, 8, 19)
    v('version_19.adt', version)

    v('truncated.adt', lambda b, a: b.__delitem__(slice(len(b) * 3 // 5, None)))
    v('too_small.adt', lambda b, a: b.__delitem__(slice(64, None)))

    # a tile with water and one with sound emitters
    water = emit = None
    for n in sorted(c.glob('World' + BS + 'Maps' + BS + '*.adt')):
        if water and emit:
            break
        bb = c.read(n)
        aa = F.Adt(bb)
        for ch in aa.mcnk:
            if ch is None or not ch.h:
                continue
            if water is None and ch.h['flags'] & 0x3C and ch.h['sizeLiquid'] == 8 + 804:
                water = n
            if emit is None and ch.h['nSndEmitters'] > 0:
                emit = n

    def liquid(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['flags'] & 0x3C:
                p = ch.offset + ch.h['ofsLiquid'] + 8
                for k in range(10):
                    q = p + 8 + k * 8 + 4
                    putf(b, q, struct.unpack_from('<f', b, q)[0] + 1.5)
                t = p + F.MCLQ_TILES_OFS
                b[t] |= 0x10
                b[t + 1] |= 0x20
                b[t + 2] = (b[t + 2] & 0xF0) | 0x0F
                return
    v('water_changed.adt', liquid, src=water)

    def liquid_size(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if not (ch.h['flags'] & 0x3C):
                put32(b, ch.offset + 8 + 0x64, 100)
                return
    v('liquid_size.adt', liquid_size)

    def emitters(b, a):
        for i in range(256):
            ch = a.mcnk[i]
            if ch.h['nSndEmitters'] > 0:
                hp = ch.offset + ch.h['ofsSndEmitters']
                put32(b, hp + 4, 28 * ch.h['nSndEmitters'])
                p = hp + 8
                b[p + 10] ^= 0xFF
                return
    v('emitters_wotlk.adt', emitters, src=emit)
    return out


def wdt_variants(c):
    out = []
    base = 'World' + BS + 'Maps' + BS + 'Azeroth' + BS + 'Azeroth.wdt'
    gbase = None
    for n in sorted(c.glob('World' + BS + 'Maps' + BS + '*.wdt')):
        w = F.Wdt(c.read(n))
        if w.mphd_flags and w.mphd_flags & 1:
            gbase = n
            break

    def v(name, fn, src=base):
        b = bytearray(c.read(src))
        fn(b)
        out.append((name, src, bytes(b)))

    v('flags.wdt', lambda b: put32(b, 8 + 4 + 8, u32(b, 20) | 0x4 | 0x2 | 0x8 | 0x80))
    def main_flags(b):
        main = 12 + 8 + 32 + 8
        put32(b, main + (40 * 64 + 30) * 8, 3)
        put32(b, main + (41 * 64 + 30) * 8 + 4, 7)
    v('main_flags.wdt', main_flags)
    def tiles(b):
        main = 12 + 8 + 32 + 8
        for i in range(64 * 64):
            if u32(b, main + i * 8) & 1:
                put32(b, main + i * 8, 0)
                break
        put32(b, main + (63 * 64 + 63) * 8, 1)
    v('tiles_changed.wdt', tiles)
    v('truncated.wdt', lambda b: b.__delitem__(slice(5000, None)))
    v('version.wdt', lambda b: put32(b, 8, 17))
    v('no_tail.wdt', lambda b: b.__delitem__(slice(12 + 8 + 32 + 8 + 0x8000, None)))
    if gbase:
        def gname(b):
            p = 12 + 8 + 32 + 8 + 0x8000
            n = u32(b, p + 4)
            for k in range(n):
                b[p + 8 + k] = 0
        v('global_empty_name.wdt', gname, src=gbase)
        def gmodf(b):
            putf(b, len(b) - 64 + 8, 123.5)
        v('global_moved.wdt', gmodf, src=gbase)
    return out


def wdl_variants(c):
    out = []
    base = 'World' + BS + 'Maps' + BS + 'Azeroth' + BS + 'Azeroth.wdl'

    def v(name, fn, src=base):
        b = bytearray(c.read(src))
        r = fn(b)
        out.append((name, src, bytes(r if r is not None else b)))

    def heights(b):
        w = F.Wdl(bytes(b))
        done = 0
        for i, o in enumerate(w.offsets):
            if o and done < 13:
                for k in range(0, 545, 3):
                    q = o + 8 + 2 * k
                    struct.pack_into('<h', b, q, max(-32768, min(32767, struct.unpack_from('<h', b, q)[0] + 7)))
                done += 1
    v('heights.wdl', heights)

    def wotlk(b):
        mwmo = b'OMWM' + struct.pack('<I', 4) + b'ab\0\0'
        nb = bytes(b[:12]) + mwmo + bytes(b[12:])
        nb = bytearray(nb)
        # shift the offsets like a WotLK writer would
        for i in range(4096):
            o = u32(nb, 12 + len(mwmo) + 8 + i * 4)
            if o:
                put32(nb, 12 + len(mwmo) + 8 + i * 4, o + len(mwmo))
        return bytes(nb)
    v('wotlk_layout.wdl', wotlk)

    def bad_mare(b):
        w = F.Wdl(bytes(b))
        n = 0
        for i, o in enumerate(w.offsets):
            if o:
                if n < 7:
                    put32(b, 20 + i * 4, o + 2)
                elif n == 7:
                    put32(b, o + 4, 0x400)
                n += 1
    v('bad_mare.wdl', bad_mare)

    def maho(b):
        return bytes(b) + b'OHAM' + struct.pack('<I', 8) + bytes(8)
    v('maho.wdl', maho)

    def tiles(b):
        w = F.Wdl(bytes(b))
        for i, o in enumerate(w.offsets):
            if o:
                put32(b, 20 + i * 4, 0)
                break
    v('tile_removed.wdl', tiles)
    v('small.wdl', lambda b: bytes(b[:100]))
    return out


def main(out):
    os.makedirs(out, exist_ok=True)
    vdir = os.path.join(out, 'variants')
    os.makedirs(vdir, exist_ok=True)
    cfg = config.load()
    c = vfs.ClientData(cfg.data_dir, cache_dir=cfg.cache_dir)
    names = [n for n in c.glob('World' + BS + 'Maps' + BS + '*') if n.lower().endswith(('.adt', '.wdt', '.wdl'))]
    groups = {}
    for n in names:
        groups.setdefault(n.rsplit(BS, 1)[0].upper(), []).append(n)
    report = V.Report()
    sha = {}
    for g in sorted(groups):
        items = [(n, (lambda n=n: c.read(n))) for n in groups[g]]
        V.validate_map_set(items, report)
    for n in names:
        sha[n] = hashlib.sha256(c.read(n)).hexdigest()
    with open(os.path.join(out, 'allclient.json'), 'w', encoding='utf-8') as fh:
        json.dump({'files': report.files, 'findings': findings(report), 'sha256': sha}, fh)
    variants = []
    for name, src, data in adt_variants(c) + wdt_variants(c) + wdl_variants(c):
        with open(os.path.join(vdir, name), 'wb') as fh:
            fh.write(data)
        r = V.Report()
        V.validate_bytes(data, name, r)
        d = D.diff_bytes(c.read(src), data, name, src, name)
        variants.append({'name': name, 'base': src, 'sha256': hashlib.sha256(data).hexdigest(), 'findings': findings(r),
                         'diff': {'summary': d.summary(), 'identical': d.identical_bytes,
                                  'lines': [[s, m] for s, m in d.lines], 'layout': [[s, m] for s, m in d.layout]}})
    with open(os.path.join(out, 'variants.json'), 'w', encoding='utf-8') as fh:
        json.dump(variants, fh, indent=1)
    with open(os.path.join(out, 'done.txt'), 'w') as fh:
        fh.write('%d map files, %d findings, %d variants\n' % (len(names), len(report.findings), len(variants)))


if __name__ == '__main__':
    main(sys.argv[1])
