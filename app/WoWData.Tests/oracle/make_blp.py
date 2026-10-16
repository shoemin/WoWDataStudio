"""Test oracle for the C# BLP / DXT / PNG port: writes the Python reference results into <outdir>.

Usage:
    python make_blp.py <outdir>                      generate everything (done.txt is written last)
    python make_blp.py --pil-decode <in.png> <out>   Pillow's RGBA of a PNG -> raw file, prints "W H"

<outdir> holds Blizzard game data (BLP files read from the client archives): keep it out of source control
(the C# tests use app/.testcache/oracle/blp/). Nothing is written anywhere else; the client is only read.
Layout (all raw images are RGBA, rows top to bottom, 4 bytes per pixel):
    manifest.json             what is where (see the keys written below)
    <stem>.blp                original BLP bytes
    <stem>.L<n>.rgba          Python BLP.decode(n)
    <stem>.L0.shift.rgba      dxt.decode(..., shift_only=True) of level 0 (DXT files)
    edit_*.in.rgba / .out.blp edited inputs and Python encode_like outputs
    new_*.in.rgba / .out.blp  encode_new inputs and outputs
    blocks_*.in / .out        raw block pixels (N x 16 x RGBA) and dxt.encode_blocks output
    bad_*.blp                 deliberately broken files for validate()
    png_*.png / .rgba         PNG test files and the expected RGBA
"""
import io
import json
import os
import random
import struct
import sys
import time
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # oracle -> WoWData.Tests -> app -> WoWDataTool
for p in (os.getcwd(), TOOL):
    if p not in sys.path:
        sys.path.insert(0, p)

import numpy as np                      # noqa: E402
from PIL import Image                   # noqa: E402

SAMPLES = {
    'dxt1 no alpha': 'Interface\\Icons\\INV_Axe_09.blp',
    'dxt1 1-bit alpha': 'Creature\\Boar\\BoarSkinIvory.blp',
    'dxt1 1-bit alpha, no mips': 'Interface\\WorldMap\\Alterac\\Dalaran4.blp',
    'dxt3': 'Creature\\MOUNTEDDEATHKNIGHT\\SMOKE02A.BLP',
    'dxt3, hasMips=2': 'Spells\\AuraRune11.blp',
    'palette no alpha': 'Character\\Orc\\Male\\OrcMaleSkin00_00.blp',
    'palette 1-bit alpha': 'Interface\\Cursor\\Attack.blp',
    'palette 8-bit alpha': 'Interface\\CURSOR\\PVP.blp',
    'palette 8-bit alpha, alphaEncoding 2': 'Particles\\dust1.blp',
    'palette, 512x512, alphaEncoding 4': 'textures\\TaxiMaps\\TaxiMap00.blp',
}
THIN_DXT3 = 'Interface\\Buttons\\UI-SliderBar-Border.blp'
RANDOM_SEED = 4242
RANDOM_COUNT = 200


def safe(path):
    s = ''.join(c if c.isalnum() or c in '-_.' else '_' for c in path.replace('\\', '__'))
    return s[:120]


def wraw(outdir, name, arr):
    with open(os.path.join(outdir, name), 'wb') as f:
        f.write(np.ascontiguousarray(arr, dtype=np.uint8).tobytes())
    return name


def wbytes(outdir, name, data):
    with open(os.path.join(outdir, name), 'wb') as f:
        f.write(data)
    return name


def jsonable(o):
    if isinstance(o, dict):
        return {k: jsonable(v) for k, v in o.items()}
    if isinstance(o, (list, tuple)):
        return [jsonable(v) for v in o]
    if isinstance(o, (np.integer,)):
        return int(o)
    if isinstance(o, (np.floating,)):
        return float(o)
    if isinstance(o, (np.bool_,)):
        return bool(o)
    return o


# ---------------------------------------------------------------------- BLP part


def blp_entry(outdir, blp, dxt, stem, path, data, label=None):
    b = blp.BLP(data)
    wbytes(outdir, stem + '.blp', data)
    levels = []
    for lv in range(b.level_count):
        img = b.decode(lv)
        wraw(outdir, '%s.L%d.rgba' % (stem, lv), img)
        levels.append([int(img.shape[1]), int(img.shape[0])])
    shift = None
    if b.encoding == blp.ENC_DXT and b.dxt_kind:
        raw = b.mip_data(0)
        need = dxt.block_count(b.width, b.height) * dxt.block_size(b.dxt_kind)
        if len(raw) >= need:
            sh = dxt.decode(raw, b.width, b.height, b.dxt_kind, b.alpha_depth > 0, shift_only=True)
            shift = wraw(outdir, stem + '.L0.shift.rgba', sh)
    try:
        fmt_of = blp.format_of(b)
    except Exception:
        fmt_of = None
    return {
        'stem': stem, 'path': path, 'label': label, 'levels': levels, 'info': jsonable(b.info()),
        'format_of': fmt_of, 'validate': blp.validate(data), 'shift': shift, 'dxt_kind': b.dxt_kind,
        'width': b.width, 'height': b.height,
    }


def edits_for(img, is_palette, alpha_used):
    """Deterministic edits of a decoded level 0: list of (name, image, palette_mode)."""
    h, w = img.shape[:2]
    out = []
    e = img.copy()
    x0, y0 = w * 3 // 8 + 1, h * 3 // 8 + 1
    e[y0:y0 + max(1, h // 8), x0:x0 + max(1, w // 8)] = (255, 0, 0, 255)
    out.append(('rect', e, 'auto'))
    e = img.copy().astype(np.float64)
    ys, xs = slice(h // 4, 3 * h // 4), slice(w // 8, w // 2 + 3)
    e[ys, xs, :3] = e[ys, xs, :3] * 0.45 + np.array([40.0, 255.0, 40.0]) * 0.55
    out.append(('tint_region', np.clip(np.rint(e), 0, 255).astype(np.uint8), 'auto'))
    if alpha_used:
        e = img.copy()
        e[1:1 + max(1, h // 6), 2:2 + max(1, w // 5), 3] = 0
        e[h // 2:h // 2 + max(1, h // 7), w // 2:w // 2 + max(1, w // 9), 3] = 100
        e[h // 2 + 2:h // 2 + 5, 1:7, 3] = 255
        out.append(('alpha', e, 'auto'))
    e = img.copy().astype(np.float64)
    e[..., :3] = e[..., :3] * 0.45 + np.array([40.0, 255.0, 40.0]) * 0.55
    full = np.clip(np.rint(e), 0, 255).astype(np.uint8)
    out.append(('tint_full', full, 'auto'))
    if is_palette:
        # auto mode usually picks a new (Pillow median cut) palette for these edits, which the C# port does not
        # reproduce byte for byte: the forced 'keep' variants give byte-exact palette coverage
        out.append(('rect_keep', out[0][1], 'keep'))
        out.append(('tint_region_keep', out[1][1], 'keep'))
        out.append(('tint_full_keep', full, 'keep'))
        out.append(('rect_new', out[0][1], 'new'))
    return out


def make_blp_part(outdir, man):
    from wowdata import blp, config, dxt, vfs
    cfg = config.load()
    if not os.path.isdir(cfg.data_dir):
        raise SystemExit('the WoW client Data folder was not found: %s (configure wowdata.json)' % cfg.data_dir)
    c = vfs.ClientData(cfg.data_dir, cache_dir=cfg.cache_dir)

    t = time.time()
    samples = []
    for i, (label, p) in enumerate(SAMPLES.items()):
        samples.append(blp_entry(outdir, blp, dxt, 's%02d_%s' % (i, safe(p)), p, c.read(p), label))
    samples.append(blp_entry(outdir, blp, dxt, 's%02d_%s' % (len(samples), safe(THIN_DXT3)), THIN_DXT3, c.read(THIN_DXT3), 'dxt3 thin short mips'))
    man['samples'] = samples

    names = sorted(p for u, (p, a) in c.names().items() if u.endswith('.BLP'))
    rnd = random.Random(RANDOM_SEED)
    rand = []
    for p in rnd.sample(names, RANDOM_COUNT + 80):
        if len(rand) >= RANDOM_COUNT:
            break
        data = c.read(p)
        if not data or data[:4] != b'BLP2' or len(data) < blp.HEADER_SIZE:
            continue
        b = blp.BLP(data)
        if b.encoding not in (blp.ENC_DXT, blp.ENC_PALETTE):
            continue
        rand.append(blp_entry(outdir, blp, dxt, 'r%03d_%s' % (len(rand), safe(p)), p, data))
    man['random'] = rand
    print('decoded %d samples + %d random in %.1fs' % (len(samples), len(rand), time.time() - t), flush=True)

    # ---- edited round trips (encode_like)
    t = time.time()
    edits = []
    targets = samples + rand[:30]
    for ent in targets:
        b = blp.BLP(c.read(ent['path']))
        img = b.decode(0)
        is_pal = b.encoding == blp.ENC_PALETTE
        lst = edits_for(img, is_pal, b.alpha_depth > 0)
        if ent['path'] == THIN_DXT3:
            e = img.copy()
            e[:, :8] = (255, 255, 255, 255)
            lst.append(('thin_white', e, 'auto'))
        if ent in rand[:30]:
            lst = [x for x in lst if x[0] in ('rect', 'tint_full', 'rect_keep')]
        for name, e, mode in lst:
            out, rep = blp.encode_like(b, e, palette_mode=mode)
            base = 'edit_%s_%s' % (ent['stem'][:4], name)
            edits.append({'stem': ent['stem'], 'name': name, 'palette_mode': mode,
                          'in': wraw(outdir, base + '.in.rgba', e), 'out': wbytes(outdir, base + '.out.blp', out),
                          'width': int(e.shape[1]), 'height': int(e.shape[0]),
                          'report': jsonable(dict(rep)), 'validate': blp.validate(out)})
    # resized path (encode_new with template): level 1 of the boar skin, and of the orc palette skin
    for label in ('dxt1 1-bit alpha', 'dxt3', 'palette no alpha'):
        ent = samples[list(SAMPLES).index(label)]
        b = blp.BLP(c.read(ent['path']))
        e = b.decode(1)
        out, rep = blp.encode_like(b, e)
        base = 'edit_%s_resized' % ent['stem'][:4]
        edits.append({'stem': ent['stem'], 'name': 'resized', 'palette_mode': 'auto',
                      'in': wraw(outdir, base + '.in.rgba', e), 'out': wbytes(outdir, base + '.out.blp', out),
                      'width': int(e.shape[1]), 'height': int(e.shape[0]),
                      'report': jsonable(dict(rep)), 'validate': blp.validate(out)})
    man['edits'] = edits
    print('%d edits in %.1fs' % (len(edits), time.time() - t), flush=True)

    # ---- encode_new for every format
    t = time.time()
    news = []
    y, x = np.mgrid[0:32, 0:64]
    synth = np.zeros((32, 64, 4), np.uint8)
    synth[..., 0] = x * 255 // 63
    synth[..., 1] = y * 255 // 31
    synth[..., 2] = 128
    synth[..., 3] = np.where(x < 16, 0, np.where(x < 32, 128, 255))
    sources = [
        ('orc', blp.BLP(c.read(SAMPLES['palette no alpha'])).decode(0)),
        ('smoke', blp.BLP(c.read(SAMPLES['dxt3'])).decode(0)),
        ('boar', blp.BLP(c.read(SAMPLES['dxt1 1-bit alpha'])).decode(0)),    # natural, thousands of colours
        ('axe', blp.BLP(c.read(SAMPLES['dxt1 no alpha'])).decode(0)),
        ('synth', synth),
    ]
    for sname, src in sources:
        inname = wraw(outdir, 'new_%s.in.rgba' % sname, src)
        for fmt in blp.FORMATS:
            for mips in (True, False) if sname == 'synth' else (True,):
                out = blp.encode_new(src, fmt, mips=mips)
                news.append({'src': inname, 'name': sname, 'width': int(src.shape[1]), 'height': int(src.shape[0]),
                             'fmt': fmt, 'mips': mips, 'template': None,
                             'out': wbytes(outdir, 'new_%s_%s_%d.out.blp' % (sname, fmt, mips), out),
                             'validate': blp.validate(out)})
    # with a template (keeps type / alphaEncoding / DXT palette block)
    for label in ('dxt1 1-bit alpha', 'dxt3, hasMips=2', 'palette 8-bit alpha, alphaEncoding 2'):
        ent = samples[list(SAMPLES).index(label)]
        b = blp.BLP(c.read(ent['path']))
        src = b.decode(0)
        inname = wraw(outdir, 'new_tpl_%s.in.rgba' % ent['stem'][:3], src)
        fmt = blp.format_of(b)
        out = blp.encode_new(src, fmt, mips=bool(b.has_mips), template=b)
        news.append({'src': inname, 'name': 'tpl_' + ent['stem'][:3], 'width': int(src.shape[1]), 'height': int(src.shape[0]),
                     'fmt': fmt, 'mips': bool(b.has_mips), 'template': ent['stem'],
                     'out': wbytes(outdir, 'new_tpl_%s.out.blp' % ent['stem'][:3], out), 'validate': blp.validate(out)})
    man['encode_new'] = news
    print('%d encode_new in %.1fs' % (len(news), time.time() - t), flush=True)

    # ---- raw block encoder stress test
    t = time.time()
    rng = np.random.default_rng(77)
    parts = []
    n = 3000
    parts.append(rng.integers(0, 256, (n, 16, 4)))                                        # noise
    base = rng.integers(0, 256, (n, 1, 4))
    parts.append(np.clip(base + rng.integers(-10, 11, (n, 16, 4)), 0, 255))                # smooth
    g = np.linspace(0, 1, 16)[None, :, None]
    a0, a1 = rng.integers(0, 256, (n, 1, 4)), rng.integers(0, 256, (n, 1, 4))
    parts.append(np.rint(a0 + (a1 - a0) * g))                                              # gradients
    two = np.where(rng.random((n, 16, 1)) < 0.5, a0, a1)
    parts.append(two)                                                                      # two colours
    parts.append(np.repeat(rng.integers(0, 256, (200, 1, 4)), 16, axis=1))                 # constant
    sp = np.repeat(rng.integers(0, 256, (200, 1, 4)), 16, axis=1)
    sp[:, rng.integers(0, 16, 200)] = rng.integers(0, 256, (200, 4))
    parts.append(sp)
    # real decoded blocks of the random textures (level 0)
    real = []
    for ent in rand[:60]:
        b = blp.BLP(c.read(ent['path']))
        real.append(dxt.image_to_blocks(b.decode(0)))
    real = np.concatenate(real)
    pick = np.random.default_rng(78).choice(len(real), min(len(real), 12000), replace=False)
    parts.append(real[np.sort(pick)])
    allb = np.concatenate([p.astype(np.uint8) for p in parts])
    # alpha variants: punch-through masks for DXT1a
    a = allb[..., 3].copy()
    third = len(allb) // 3
    a[:third] = np.where(a[:third] < 128, 0, 255)
    a[third:2 * third] = np.where(rng.random((2 * third - third, 16)) < 0.08, 0, 255)
    allb[..., 3] = a
    allb[:50, :, 3] = 0                                                                   # fully transparent
    blocks_in = wraw(outdir, 'blocks.in', allb)
    blocks = []
    for kind, ua in (('dxt1', True), ('dxt1', False), ('dxt3', True), ('dxt5', True)):
        enc = dxt.encode_blocks(allb, kind, ua)
        blocks.append({'kind': kind, 'use_alpha': ua, 'in': blocks_in, 'count': int(len(allb)),
                       'out': wbytes(outdir, 'blocks_%s_%d.out' % (kind, ua), enc.tobytes())})
    man['blocks'] = blocks
    print('%d blocks x %d modes in %.1fs' % (len(allb), len(blocks), time.time() - t), flush=True)

    # ---- validate() on broken files
    good = blp.encode_new(synth, 'dxt1')
    bad = []

    def add_bad(name, data):
        bad.append({'file': wbytes(outdir, 'bad_%s.blp' % name, data), 'problems': blp.validate(data)})
    add_bad('good', good)
    add_bad('magic', b'BLP1' + good[4:])
    add_bad('short_header', good[:100])
    add_bad('cut_last_mip', good[:-8])
    x = bytearray(good); x[8] = 3; add_bad('raw_encoding', bytes(x))
    x = bytearray(good); x[4] = 2; add_bad('type2', bytes(x))
    x = bytearray(good); x[10] = 5; add_bad('alpha_encoding5', bytes(x))
    x = bytearray(good); x[9] = 4; add_bad('dxt1_depth4', bytes(x))
    x = bytearray(good); struct.pack_into('<I', x, 0x14 + 4 * 6, 0); add_bad('missing_mip', bytes(x))
    x = bytearray(good); struct.pack_into('<I', x, 0x54 + 4 * 0, 100); add_bad('small_mip', bytes(x))
    add_bad('non_pow2', blp.encode_new(synth[:, :48].copy(), 'dxt1'))
    big = np.zeros((8, 1024, 4), np.uint8); big[..., 3] = 255
    add_bad('too_wide', blp.encode_new(big, 'dxt1'))
    x = bytearray(blp.encode_new(synth, 'pal4')); struct.pack_into('<I', x, 0x54 + 4 * 2, 10); add_bad('pal_small_mip', bytes(x))
    man['bad'] = bad


# ---------------------------------------------------------------------- PNG part


def chunk(t, d):
    return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)


ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]


def pack_row(samples, depth):
    """1-D array of samples -> packed bytes (big endian for 16 bit)."""
    if depth == 8:
        return samples.astype(np.uint8).tobytes()
    if depth == 16:
        return samples.astype('>u2').tobytes()
    per = 8 // depth
    s = list(samples.astype(int))
    s += [0] * ((-len(s)) % per)
    out = bytearray()
    for i in range(0, len(s), per):
        v = 0
        for j in range(per):
            v = (v << depth) | s[i + j]
        out.append(v)
    return bytes(out)


def filter_row(raw, prev, bpp, f):
    out = bytearray(len(raw))
    for i in range(len(raw)):
        a = raw[i - bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i - bpp] if i >= bpp else 0
        if f == 0:
            p = 0
        elif f == 1:
            p = a
        elif f == 2:
            p = b
        elif f == 3:
            p = (a + b) >> 1
        else:
            pp = a + b - c
            pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
            p = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
        out[i] = (raw[i] - p) & 0xFF
    return bytes(out)


def write_png(samples, ctype, depth, interlace=False, plte=None, trns=None, seed=0):
    """samples: (h, w, channels) ints in the sample range. Rows use rotating filter types."""
    h, w, ch = samples.shape
    bpp = max(1, ch * depth // 8)
    rnd = random.Random(seed)
    passes = ADAM7 if interlace else [(0, 0, 1, 1)]
    data = bytearray()
    for x0, y0, dx, dy in passes:
        sub = samples[y0::dy, x0::dx]
        if sub.shape[0] == 0 or sub.shape[1] == 0:
            continue
        prev = bytes(len(pack_row(sub[0].reshape(-1), depth)))
        for row in sub:
            raw = pack_row(row.reshape(-1), depth)
            f = rnd.randrange(5)
            data.append(f)
            data += filter_row(raw, prev, bpp, f)
            prev = raw
    out = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, depth, ctype, 0, 0, 1 if interlace else 0))
    if plte is not None:
        out += chunk(b'PLTE', bytes(plte))
    if trns is not None:
        out += chunk(b'tRNS', bytes(trns))
    comp = zlib.compress(bytes(data), 9)
    half = len(comp) // 2                                  # two IDAT chunks
    out += chunk(b'tEXt', b'Comment\x00oracle') + chunk(b'IDAT', comp[:half]) + chunk(b'IDAT', comp[half:]) + chunk(b'IEND', b'')
    return out


def spec_rgba(samples, ctype, depth, plte=None, trns=None):
    """The documented C# rule (PNG spec; 16 bit -> high byte; tRNS compared with raw samples)."""
    h, w, ch = samples.shape
    s = samples.astype(np.int64)
    maxv = (1 << depth) - 1

    def to8(v):
        if depth == 16:
            return v >> 8
        if depth == 8:
            return v
        return v * 255 // maxv
    out = np.zeros((h, w, 4), np.int64)
    out[..., 3] = 255
    if ctype == 0:
        out[..., 0] = out[..., 1] = out[..., 2] = to8(s[..., 0])
        if trns is not None:
            key = (trns[0] << 8) | trns[1]
            out[..., 3] = np.where(s[..., 0] == key, 0, 255)
    elif ctype == 2:
        out[..., :3] = to8(s)
        if trns is not None:
            key = [(trns[0] << 8) | trns[1], (trns[2] << 8) | trns[3], (trns[4] << 8) | trns[5]]
            out[..., 3] = np.where((s[..., 0] == key[0]) & (s[..., 1] == key[1]) & (s[..., 2] == key[2]), 0, 255)
    elif ctype == 4:
        out[..., 0] = out[..., 1] = out[..., 2] = to8(s[..., 0])
        out[..., 3] = to8(s[..., 1])
    elif ctype == 6:
        out[...] = to8(s)
    return out.astype(np.uint8)


def make_png_part(outdir, man):
    pngs = []
    rng = np.random.default_rng(99)

    def add(name, data, rule='pillow', expected=None, desc=''):
        fn = wbytes(outdir, 'png_%s.png' % name, data)
        im = Image.open(io.BytesIO(data))
        pil = np.array(im.convert('RGBA'))
        exp = pil if expected is None else expected
        pngs.append({'file': fn, 'expected': wraw(outdir, 'png_%s.rgba' % name, exp), 'width': int(exp.shape[1]),
                     'height': int(exp.shape[0]), 'rule': rule, 'desc': desc,
                     'pillow_agrees': bool(np.array_equal(pil, exp))})

    # Pillow-written files
    h, w = 37, 53
    rgba = rng.integers(0, 256, (h, w, 4)).astype(np.uint8)
    rgba[5:9, 5:9, 3] = 0
    for mode in ('1', 'L', 'LA', 'P', 'RGB', 'RGBA'):
        im = Image.fromarray(rgba, 'RGBA').convert(mode) if mode != 'P' else Image.fromarray(rgba[..., :3], 'RGB').quantize(77)
        bio = io.BytesIO()
        im.save(bio, 'PNG')
        add('pil_' + mode, bio.getvalue(), desc='Pillow writes mode ' + mode)
    im = Image.fromarray(rgba[..., :3], 'RGB').quantize(16)
    bio = io.BytesIO()
    im.save(bio, 'PNG', transparency=bytes([0, 128, 255] + [255] * 13))
    add('pil_P_trns', bio.getvalue(), desc='Pillow palette with tRNS (16 colours -> 4-bit)')
    im = Image.fromarray(rgba[..., 0], 'L')
    bio = io.BytesIO()
    im.save(bio, 'PNG', transparency=int(rgba[0, 0, 0]))
    add('pil_L_trns', bio.getvalue(), desc='Pillow gray with tRNS')
    im = Image.fromarray(rgba[..., :3], 'RGB')
    bio = io.BytesIO()
    im.save(bio, 'PNG', transparency=tuple(int(v) for v in rgba[3, 3, :3]))
    add('pil_RGB_trns', bio.getvalue(), desc='Pillow RGB with tRNS')
    im = Image.fromarray((rng.integers(0, 65536, (h, w))).astype(np.uint16))
    bio = io.BytesIO()
    im.save(bio, 'PNG')
    data = bio.getvalue()
    s16 = np.array(Image.open(io.BytesIO(data))).astype(np.int64)[..., None]
    add('pil_I16', data, 'spec', spec_rgba(s16, 0, 16), 'Pillow 16-bit gray (Pillow clips on convert; C# takes the high byte)')

    # hand-written files: every colour type / depth, plain and interlaced, odd sizes, all filter types
    sizes = [(13, 7), (1, 1), (33, 17), (9, 10)]
    case = 0
    for ctype, depths in ((0, (1, 2, 4, 8, 16)), (2, (8, 16)), (3, (1, 2, 4, 8)), (4, (8, 16)), (6, (8, 16))):
        for depth in depths:
            for interlace in (False, True):
                for (w, h) in sizes[:3] if interlace else sizes:
                    case += 1
                    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
                    maxv = (1 << depth) - 1
                    plte = trns = None
                    if ctype == 3:
                        npal = min(maxv + 1, 200)
                        samples = rng.integers(0, npal, (h, w, 1))
                        plte = rng.integers(0, 256, npal * 3).astype(np.uint8)
                        trns = rng.integers(0, 256, max(1, npal // 2)).astype(np.uint8) if case % 2 else None
                    else:
                        samples = rng.integers(0, maxv + 1, (h, w, ch))
                    data = write_png(samples, ctype, depth, interlace, plte, trns, seed=case)
                    name = 'c%d_d%d_%s_%dx%d' % (ctype, depth, 'i' if interlace else 'p', w, h)
                    quirk = depth == 16 and ctype == 0
                    add(name, data, 'spec' if quirk else 'pillow', spec_rgba(samples, ctype, depth) if quirk else None,
                        'colour type %d, %d bit, %s' % (ctype, depth, 'Adam7' if interlace else 'plain'))
                    # tRNS colour keys for gray / RGB
                    if ctype in (0, 2) and (w, h) == (13, 7):
                        key = samples[0, 0]
                        trns = b''.join(struct.pack('>H', int(v)) for v in key)
                        data = write_png(samples, ctype, depth, interlace, None, trns, seed=case)
                        quirk = depth == 16 or (ctype == 0 and depth < 8)
                        add(name + '_trns', data, 'spec' if quirk else 'pillow',
                            spec_rgba(samples, ctype, depth, trns=trns) if quirk else None, 'with a tRNS colour key')
    # broken files
    good = write_png(rng.integers(0, 256, (4, 4, 3)), 2, 8)
    broken = {'not_png': b'GIF89a' + bytes(40), 'truncated': good[:len(good) - 30], 'empty': b'',
              'bad_crc': good[:20] + bytes([good[20] ^ 0xFF]) + good[21:]}
    for k, v in broken.items():
        wbytes(outdir, 'png_broken_%s.png' % k, v)
    man['png'] = pngs
    man['png_broken'] = ['png_broken_%s.png' % k for k in broken]


def main():
    if len(sys.argv) == 4 and sys.argv[1] == '--pil-decode':
        im = Image.open(sys.argv[2])
        a = np.array(im.convert('RGBA'))
        with open(sys.argv[3], 'wb') as f:
            f.write(a.tobytes())
        print(a.shape[1], a.shape[0], im.mode)
        return
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    outdir = sys.argv[1]
    os.makedirs(outdir, exist_ok=True)
    done = os.path.join(outdir, 'done.txt')
    if os.path.exists(done):
        os.remove(done)
    man = {'generated': time.strftime('%Y-%m-%d %H:%M:%S'), 'numpy': np.__version__}
    t = time.time()
    make_png_part(outdir, man)
    make_blp_part(outdir, man)
    with open(os.path.join(outdir, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(jsonable(man), f, indent=1)
    with open(done, 'w') as f:
        f.write('ok %.1fs\n' % (time.time() - t))
    print('oracle written to %s in %.1fs' % (outdir, time.time() - t))


if __name__ == '__main__':
    main()
