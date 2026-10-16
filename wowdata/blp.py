"""BLP2 textures: parse, decode to RGBA, encode back in the SAME format/flags/mip layout.

Header (1172 = 0x494 bytes, read by BlpImage_SetData @005a83b0 in our decompile):
  0x00 'BLP2'   0x04 uint32 type (1)
  0x08 uint8 encoding (1 = palette, 2 = DXT, 3 = raw BGRA; the 1.12 client handles 1 and 2)
  0x09 uint8 alphaDepth (0, 1, 4, 8)   0x0a uint8 alphaEncoding (DXT: 0 DXT1, 1 DXT3, 7 DXT5; palette: 8)
  0x0b uint8 hasMips   0x0c uint32 width   0x10 uint32 height
  0x14 uint32 mipOffsets[16]   0x54 uint32 mipSizes[16]   0x94 uint32 palette[256] (BGRA)
Palette mips: width*height index bytes, then the alpha plane (1 bit LSB-first / 4 bits low nibble
first / 8 bits per pixel). DXT mips: 4x4 blocks (a level smaller than 4x4 still uses whole blocks).

Encoding policy ("encode_like"): start from the original file's bytes and replace only what changed.
  * DXT: a 4x4 block whose pixels are unchanged keeps its original bytes; changed blocks are re-encoded.
    Lower mips are rebuilt from the edited level 0 (2x2 box filter) only where their footprint changed.
  * Palette: unchanged pixels keep their original index and alpha bits; changed pixels get the nearest
    colour of the original palette, or (when that is too far off) a new 256-colour palette is built.
So an unmodified PNG gives a byte-identical BLP, and an edit only touches the blocks it covers.
"""
import struct

import numpy as np

from . import dxt

HEADER_SIZE = 0x494
PALETTE_OFFSET = 0x94
ENC_PALETTE, ENC_DXT, ENC_RAW = 1, 2, 3
DXT_KIND = {0: 'dxt1', 1: 'dxt3', 7: 'dxt5'}
KIND_ALPHA_ENCODING = {'dxt1': 0, 'dxt3': 1, 'dxt5': 7}


class BLPError(Exception):
    pass


def _levels_for(width, height):
    n = 1
    w, h = width, height
    while w > 1 or h > 1:
        w, h = max(1, w // 2), max(1, h // 2)
        n += 1
    return n


def level_dims(width, height, level):
    return max(1, width >> level), max(1, height >> level)


class BLP:
    def __init__(self, data):
        data = bytes(data)
        if len(data) < HEADER_SIZE or data[:4] != b'BLP2':
            raise BLPError('not a BLP2 file')
        self.data = data
        (self.type, self.encoding, self.alpha_depth, self.alpha_encoding, self.has_mips,
         self.width, self.height) = struct.unpack_from('<IBBBBII', data, 4)
        self.mip_offsets = list(struct.unpack_from('<16I', data, 0x14))
        self.mip_sizes = list(struct.unpack_from('<16I', data, 0x54))
        self.palette_raw = data[PALETTE_OFFSET:PALETTE_OFFSET + 1024]

    # ------------------------------------------------------------------ description
    @property
    def dxt_kind(self):
        return DXT_KIND.get(self.alpha_encoding) if self.encoding == ENC_DXT else None

    def format_name(self):
        if self.encoding == ENC_DXT:
            k = self.dxt_kind or '?'
            if k == 'dxt1':
                return 'DXT1' + (' + 1-bit alpha' if self.alpha_depth else ' (no alpha)')
            return k.upper() + ' (%d-bit alpha)' % self.alpha_depth
        if self.encoding == ENC_PALETTE:
            return 'palette 256' + (' + %d-bit alpha' % self.alpha_depth if self.alpha_depth else ' (no alpha)')
        if self.encoding == ENC_RAW:
            return 'raw BGRA (not supported by the 1.12 client)'
        return 'unknown encoding %d' % self.encoding

    @property
    def level_count(self):
        """Number of mip levels stored in the file."""
        if not self.has_mips:
            return 1
        n = 0
        for i in range(min(16, _levels_for(self.width, self.height))):
            if self.mip_offsets[i] and self.mip_sizes[i]:
                n += 1
            else:
                break
        return max(1, n)

    def info(self):
        return {
            'type': self.type, 'encoding': self.encoding, 'alpha_depth': self.alpha_depth,
            'alpha_encoding': self.alpha_encoding, 'has_mips': self.has_mips,
            'width': self.width, 'height': self.height, 'levels': self.level_count,
            'mip_offsets': self.mip_offsets[:self.level_count], 'mip_sizes': self.mip_sizes[:self.level_count],
            'format': self.format_name(), 'file_size': len(self.data),
        }

    def mip_data(self, level):
        o, s = self.mip_offsets[level], self.mip_sizes[level]
        return self.data[o:o + s]

    def palette_bgra(self):
        return np.frombuffer(self.palette_raw, np.uint8).reshape(256, 4)

    # ------------------------------------------------------------------ decoding
    def decode(self, level=0):
        """RGBA uint8 array (height, width, 4) of one mip level."""
        w, h = level_dims(self.width, self.height, level)
        raw = self.mip_data(level)
        if self.encoding == ENC_DXT:
            kind = self.dxt_kind
            if kind is None:
                raise BLPError('unknown DXT alpha encoding %d' % self.alpha_encoding)
            need = dxt.block_count(w, h) * dxt.block_size(kind)
            if len(raw) < need:
                # thin levels stored short by Blizzard's converter: like the client (which hands the
                # pointer to the GPU), read on into the bytes that follow; zeros past the end of the file
                o = self.mip_offsets[level]
                raw = self.data[o:o + need]
                raw = raw + bytes(need - len(raw))
            return dxt.decode(raw, w, h, kind, use_alpha=self.alpha_depth > 0)
        if self.encoding == ENC_PALETTE:
            idx, alpha = self._palette_planes(raw, w, h)
            pal = self.palette_bgra()
            rgb = pal[idx][..., [2, 1, 0]]
            return np.concatenate([rgb, alpha[..., None]], axis=-1).reshape(h, w, 4)
        if self.encoding == ENC_RAW:
            px = np.frombuffer(raw[:w * h * 4], np.uint8).reshape(h, w, 4)
            out = px[..., [2, 1, 0, 3]].copy()
            if not self.alpha_depth:
                out[..., 3] = 255
            return out
        raise BLPError('unknown encoding %d' % self.encoding)

    def _palette_planes(self, raw, w, h):
        n = w * h
        idx = np.frombuffer(raw[:n], np.uint8).reshape(h, w)
        ab = np.frombuffer(raw[n:], np.uint8)
        d = self.alpha_depth
        if d == 0:
            alpha = np.full((h, w), 255, np.uint8)
        elif d == 1:
            bits = np.unpackbits(ab[:(n + 7) // 8], bitorder='little')[:n]
            alpha = (bits * 255).astype(np.uint8).reshape(h, w)
        elif d == 4:
            nib = np.empty(((n + 1) // 2) * 2, np.uint8)
            src = ab[:(n + 1) // 2]
            nib[0::2] = src & 0x0F
            nib[1::2] = src >> 4
            alpha = (nib[:n] * 17).astype(np.uint8).reshape(h, w)
        elif d == 8:
            alpha = ab[:n].copy().reshape(h, w)
        else:
            raise BLPError('unsupported palette alpha depth %d' % d)
        return idx, alpha


# ---------------------------------------------------------------------- helpers for encoding


def downsample(img):
    """2x2 box filter of an RGBA uint8 image (a dimension already at 1 is kept)."""
    h, w, c = img.shape
    f = img.astype(np.float64)
    if h > 1:
        f = (f[0::2] + f[1::2]) / 2.0
    if w > 1:
        f = (f[:, 0::2] + f[:, 1::2]) / 2.0
    return np.clip(np.rint(f), 0, 255).astype(np.uint8)


def downsample_mask(m):
    h, w = m.shape
    if h > 1:
        m = m[0::2] | m[1::2]
    if w > 1:
        m = m[:, 0::2] | m[:, 1::2]
    return m


def build_chain(level0, count, binary_alpha=False):
    chain = [level0]
    for _ in range(1, count):
        nxt = downsample(chain[-1])
        if binary_alpha:
            nxt[..., 3] = np.where(nxt[..., 3] >= 128, 255, 0)
        chain.append(nxt)
    return chain


def _changed(a, b, compare_alpha):
    if compare_alpha:
        return (a != b).any(-1)
    return (a[..., :3] != b[..., :3]).any(-1)


def _pack_alpha(alpha, depth):
    a = alpha.reshape(-1)
    if depth == 0:
        return b''
    if depth == 1:
        return np.packbits((a >= 128).astype(np.uint8), bitorder='little').tobytes()
    if depth == 4:
        q = np.clip((a.astype(np.int32) + 8) // 17, 0, 15).astype(np.uint8)
        if len(q) % 2:
            q = np.append(q, 0)
        return (q[0::2] | (q[1::2] << 4)).astype(np.uint8).tobytes()
    if depth == 8:
        return a.astype(np.uint8).tobytes()
    raise BLPError('unsupported alpha depth %d' % depth)


def _quantize_alpha(alpha, depth):
    """The alpha values the given depth can store (what the client will see)."""
    if depth == 0:
        return np.full_like(alpha, 255)
    if depth == 1:
        return np.where(alpha >= 128, 255, 0).astype(np.uint8)
    if depth == 4:
        return (np.clip((alpha.astype(np.int32) + 8) // 17, 0, 15) * 17).astype(np.uint8)
    return alpha


def _nearest_palette(rgb, pal_rgb, chunk=65536):
    """rgb (N,3) uint8, pal_rgb (256,3) -> index (N,) uint8 and squared error (N,)."""
    p = pal_rgb.astype(np.int32)
    out = np.empty(len(rgb), np.uint8)
    err = np.empty(len(rgb), np.int64)
    for s in range(0, len(rgb), chunk):
        c = rgb[s:s + chunk].astype(np.int32)
        d = ((c[:, None, :] - p[None, :, :]) ** 2).sum(-1)
        i = d.argmin(1)
        out[s:s + chunk] = i
        err[s:s + chunk] = d[np.arange(len(c)), i]
    return out, err


def _make_palette(rgb_img, palette_alpha_byte):
    """New 256-colour palette for an RGB image (Pillow median cut, no dithering) -> (256,4) BGRA."""
    from PIL import Image
    im = Image.fromarray(np.ascontiguousarray(rgb_img), 'RGB')
    q = im.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    pal = np.array(q.getpalette()[:768], np.uint8).reshape(-1, 3)
    if len(pal) < 256:
        pal = np.concatenate([pal, np.zeros((256 - len(pal), 3), np.uint8)])
    bgra = np.empty((256, 4), np.uint8)
    bgra[:, 0] = pal[:, 2]
    bgra[:, 1] = pal[:, 1]
    bgra[:, 2] = pal[:, 0]
    bgra[:, 3] = palette_alpha_byte
    return bgra


# ---------------------------------------------------------------------- encode like an original


class EncodeReport(dict):
    pass


def encode_like(orig, rgba, palette_mode='auto', max_palette_rms=6.0):
    """Encode RGBA (h,w,4) uint8 into the original BLP's format. Returns (bytes, EncodeReport).

    Same size: the original file is copied and only changed blocks / pixels are rewritten.
    Different size: a new file with the original's encoding, alpha depth/encoding and mip policy.
    palette_mode: 'auto' | 'keep' | 'new' (palette encodings only).
    """
    rgba = np.ascontiguousarray(rgba, dtype=np.uint8)
    h, w = rgba.shape[:2]
    rep = EncodeReport(format=orig.format_name(), width=w, height=h)
    if (w, h) != (orig.width, orig.height):
        rep['resized'] = True
        fmt = format_of(orig)
        data = encode_new(rgba, fmt, mips=bool(orig.has_mips), template=orig)
        rep['levels'] = BLP(data).level_count
        rep['identical'] = False
        return data, rep
    if orig.encoding == ENC_DXT:
        return _encode_like_dxt(orig, rgba, rep)
    if orig.encoding == ENC_PALETTE:
        return _encode_like_palette(orig, rgba, rep, palette_mode, max_palette_rms)
    raise BLPError('encoding %d cannot be written (the 1.12 client only reads palette and DXT)' % orig.encoding)


def _encode_like_dxt(orig, rgba, rep):
    kind = orig.dxt_kind
    use_alpha = orig.alpha_depth > 0
    compare_alpha = use_alpha
    out = bytearray(orig.data)
    levels = orig.level_count
    bs = dxt.block_size(kind)
    new0 = rgba.copy()
    if not use_alpha:
        new0[..., 3] = 255
    orig0 = orig.decode(0)
    mask = _changed(orig0, new0, compare_alpha)
    if kind == 'dxt1' and use_alpha:
        # colour under fully transparent pixels is not stored by DXT1 (index 3 = transparent black):
        # an editor changing it is not an edit
        mask &= ~((orig0[..., 3] < 128) & (new0[..., 3] < 128))
    chain = build_chain(new0, levels, binary_alpha=(kind == 'dxt1'))
    rep['changed_pixels'] = int(mask.sum())
    rep['levels'] = levels
    per_level = []
    for lv in range(levels):
        lw, lh = level_dims(orig.width, orig.height, lv)
        if lv > 0:
            mask = downsample_mask(mask)
        bmask = dxt.image_to_blocks(mask[..., None].astype(np.uint8))[..., 0].any(1)
        # Blizzard's converter stored only max(1, (w/4)*(h/4)) blocks (16x2 -> 1 block instead of 4):
        # 628 shipped DXT3 files do that. Keep their layout: only the blocks the file has are written.
        stored = min(len(bmask), orig.mip_sizes[lv] // bs)
        bmask = bmask[:stored]
        nchg = int(bmask.sum())
        per_level.append({'level': lv, 'size': '%dx%d' % (lw, lh), 'blocks': int(stored), 're_encoded': nchg})
        if nchg == 0:
            continue
        blocks = dxt.image_to_blocks(chain[lv])[:stored][bmask]
        enc = dxt.encode_blocks(blocks, kind, use_alpha)
        off = orig.mip_offsets[lv]
        lvl = np.frombuffer(bytes(out[off:off + stored * bs]), np.uint8).reshape(-1, bs).copy()
        lvl[bmask] = enc
        out[off:off + stored * bs] = lvl.tobytes()
    rep['per_level'] = per_level
    data = bytes(out)
    rep['identical'] = data == orig.data
    return data, rep


def _encode_like_palette(orig, rgba, rep, palette_mode, max_rms):
    depth = orig.alpha_depth
    levels = orig.level_count
    new0 = rgba.copy()
    new0[..., 3] = _quantize_alpha(new0[..., 3], depth)
    orig0 = orig.decode(0)
    mask0 = _changed(orig0, new0, depth > 0)
    chain = build_chain(new0, levels, binary_alpha=(depth == 1))
    pal = orig.palette_bgra()
    pal_rgb = pal[:, [2, 1, 0]]
    rep['changed_pixels'] = int(mask0.sum())
    rep['levels'] = levels
    # decide the palette
    mode = palette_mode
    if mode == 'auto':
        mode = 'keep'
        if mask0.any():
            _, err = _nearest_palette(new0[mask0][:, :3], pal_rgb)
            rms = float(np.sqrt(err.mean() / 3.0))
            rep['palette_rms_keep'] = round(rms, 2)
            if rms > max_rms:
                mode = 'new'
    rep['palette'] = mode
    out = bytearray(orig.data)
    if mode == 'new':
        alpha_byte = int(np.bincount(pal[:, 3]).argmax())
        pal = _make_palette(new0[..., :3], alpha_byte)
        pal_rgb = pal[:, [2, 1, 0]]
        out[PALETTE_OFFSET:PALETTE_OFFSET + 1024] = pal.tobytes()
    mask = mask0
    per_level = []
    for lv in range(levels):
        lw, lh = level_dims(orig.width, orig.height, lv)
        if lv > 0:
            mask = downsample_mask(mask)
        img = chain[lv]
        oidx, oalpha = orig._palette_planes(orig.mip_data(lv), lw, lh)
        if mode == 'new':
            idx, _ = _nearest_palette(img[..., :3].reshape(-1, 3), pal_rgb)
            idx = idx.reshape(lh, lw)
            alpha = img[..., 3]
            nchg = lw * lh
        else:
            idx = oidx.copy()
            alpha = oalpha.copy()
            if mask.any():
                ni, _ = _nearest_palette(img[mask][:, :3], pal_rgb)
                idx[mask] = ni
                alpha[mask] = img[mask][:, 3]
            nchg = int(mask.sum())
        per_level.append({'level': lv, 'size': '%dx%d' % (lw, lh), 'pixels': lw * lh, 're_encoded': nchg})
        payload = idx.astype(np.uint8).tobytes() + _pack_alpha(alpha, depth)
        off, size = orig.mip_offsets[lv], orig.mip_sizes[lv]
        if len(payload) != size:
            raise BLPError('level %d: payload %d bytes, original mip %d bytes' % (lv, len(payload), size))
        out[off:off + size] = payload
    rep['per_level'] = per_level
    data = bytes(out)
    rep['identical'] = data == orig.data
    return data, rep


# ---------------------------------------------------------------------- new files


FORMATS = {
    # name: (encoding, alphaDepth, alphaEncoding)
    'dxt1': (ENC_DXT, 0, 0), 'dxt1a': (ENC_DXT, 1, 0), 'dxt3': (ENC_DXT, 8, 1), 'dxt5': (ENC_DXT, 8, 7),
    'pal': (ENC_PALETTE, 0, 8), 'pal1': (ENC_PALETTE, 1, 8), 'pal4': (ENC_PALETTE, 4, 8), 'pal8': (ENC_PALETTE, 8, 8),
}


def format_of(b):
    for k, (e, d, a) in FORMATS.items():
        if e == b.encoding and (e == ENC_PALETTE or a == b.alpha_encoding) and d == b.alpha_depth:
            return k
    if b.encoding == ENC_DXT:
        return {0: 'dxt1a' if b.alpha_depth else 'dxt1', 1: 'dxt3', 7: 'dxt5'}[b.alpha_encoding]
    if b.encoding == ENC_PALETTE:
        return {0: 'pal', 1: 'pal1', 4: 'pal4', 8: 'pal8'}[b.alpha_depth]
    raise BLPError('no writable format for encoding %d' % b.encoding)


def suggest_format(rgba):
    a = rgba[..., 3]
    if (a == 255).all():
        return 'dxt1'
    if np.isin(a, (0, 255)).all():
        return 'dxt1a'
    return 'dxt5'


def encode_new(rgba, fmt, mips=True, template=None):
    """A complete new BLP2 file in `fmt` (see FORMATS). With `template`, its header bytes that we do not
    compute (type, alphaEncoding of palette files, DXT palette block) are copied."""
    if fmt not in FORMATS:
        raise BLPError('unknown format %r (choose from %s)' % (fmt, ', '.join(FORMATS)))
    enc, depth, aenc = FORMATS[fmt]
    if template is not None and template.encoding == enc and template.alpha_depth == depth:
        aenc = template.alpha_encoding
    rgba = np.ascontiguousarray(rgba, dtype=np.uint8)
    h, w = rgba.shape[:2]
    levels = _levels_for(w, h) if mips else 1
    levels = min(levels, 16)
    if enc == ENC_DXT:
        kind = DXT_KIND[aenc]
        chain = build_chain(rgba, levels, binary_alpha=(kind == 'dxt1'))
        payloads = [dxt.encode(img, kind, use_alpha=depth > 0) for img in chain]
        palette = template.palette_raw if template is not None and template.encoding == ENC_DXT else bytes(1024)
    else:
        q = rgba.copy()
        q[..., 3] = _quantize_alpha(q[..., 3], depth)
        alpha_byte = 0
        if template is not None and template.encoding == ENC_PALETTE:
            alpha_byte = int(np.bincount(template.palette_bgra()[:, 3]).argmax())
        pal = _make_palette(q[..., :3], alpha_byte)
        chain = build_chain(q, levels, binary_alpha=(depth == 1))
        payloads = []
        for img in chain:
            idx, _ = _nearest_palette(img[..., :3].reshape(-1, 3), pal[:, [2, 1, 0]])
            payloads.append(idx.tobytes() + _pack_alpha(img[..., 3], depth))
        palette = pal.tobytes()
    offsets, sizes = [0] * 16, [0] * 16
    pos = HEADER_SIZE
    for i, p in enumerate(payloads):
        offsets[i] = pos
        sizes[i] = len(p)
        pos += len(p)
    btype = template.type if template is not None else 1
    head = struct.pack('<4sIBBBBII', b'BLP2', btype, enc, depth, aenc, 1 if mips else 0, w, h)
    head += struct.pack('<16I', *offsets) + struct.pack('<16I', *sizes) + palette
    return head + b''.join(payloads)


# ---------------------------------------------------------------------- client compatibility


def is_pow2(n):
    return n > 0 and (n & (n - 1)) == 0


def validate(data):
    """Problems that would stop the 1.12 client from using the file (empty list = OK).

    Rules from our decompile: BlpImage_SetData requires 'BLP2' and type 1; Texture_ChooseBlpGxFormat
    maps alphaEncoding 0/1/7 to DXT1/3/5 and 8 to palette formats by alphaDepth; the client computes
    the mip count from the dimensions (MipBits_GetLevelCount), so a file with hasMips must store the
    full chain down to 1x1; BlpImage_IsValidDimension allows powers of two 1..512.
    """
    problems = []
    try:
        b = BLP(data)
    except BLPError as e:
        return [str(e)]
    if b.type != 1:
        problems.append('type is %d, the client requires 1' % b.type)
    if b.encoding not in (ENC_PALETTE, ENC_DXT):
        problems.append('encoding %d is not handled by the 1.12 client (only 1 = palette, 2 = DXT)' % b.encoding)
    if b.encoding == ENC_DXT and b.alpha_encoding not in DXT_KIND:
        problems.append('DXT with alphaEncoding %d (expected 0, 1 or 7)' % b.alpha_encoding)
    if b.encoding == ENC_DXT and b.alpha_encoding == 0 and b.alpha_depth not in (0, 1):
        problems.append('DXT1 with alphaDepth %d (expected 0 or 1)' % b.alpha_depth)
    if b.encoding == ENC_PALETTE and b.alpha_depth not in (0, 1, 4, 8):
        problems.append('palette alphaDepth %d (expected 0, 1, 4, 8)' % b.alpha_depth)
    if not (is_pow2(b.width) and is_pow2(b.height)):
        problems.append('size %dx%d is not a power of two' % (b.width, b.height))
    if b.width > 512 or b.height > 512:
        problems.append('size %dx%d is above 512 (BlpImage_IsValidDimension limit; risky)' % (b.width, b.height))
    need = _levels_for(b.width, b.height) if b.has_mips else 1
    for lv in range(need):
        o, s = b.mip_offsets[lv], b.mip_sizes[lv]
        lw, lh = level_dims(b.width, b.height, lv)
        if b.encoding == ENC_DXT:
            # Blizzard's converter stored max(1, (w/4)*(h/4)) blocks (integer division), so a level thinner than
            # 4 pixels has a single block (16x2 -> 1 block, not 4); 628 shipped files do that: accept it
            exp = max(1, (lw // 4) * (lh // 4)) * dxt.block_size(b.dxt_kind or 'dxt1')
        else:
            exp = lw * lh + len(_pack_alpha(np.zeros(lw * lh, np.uint8), b.alpha_depth))
        if not o or o + s > len(data):
            problems.append('level %d (%dx%d) is missing or outside the file' % (lv, lw, lh))
            continue
        if s < exp:
            problems.append('level %d (%dx%d) has %d bytes, needs %d' % (lv, lw, lh, s, exp))
    return problems
