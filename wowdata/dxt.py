"""DXT1 / DXT3 / DXT5 (BC1-3) block decoder and encoder, vectorized with numpy.

Decoder: the standard S3TC rules. Interpolated colours use integer division on the 8-bit expanded
endpoints ((2*a + b) // 3, (a + b) // 2, (6*a0 + a1) // 7 ...), the same convention as Pillow's
BLP/DDS decoder, so the two can be cross-checked bit for bit.

Encoder: an independent implementation of the usual "principal axis + least-squares refinement"
approach (the method popularized by stb_dxt and squish's range fit): per 4x4 block
  1. endpoints from the extremes of the colours projected on the block's principal axis, inset by 1/16;
  2. nearest-palette index selection;
  3. two rounds of least-squares endpoint refinement, keeping a round only if the error drops.
Blocks with transparent pixels (DXT1 1-bit alpha) use the 3-colour mode with index 3 = transparent.
DXT5 alpha tries both the 8-value and the 6-value (+0/255) modes and keeps the better one.
"""
import numpy as np

# ---------------------------------------------------------------------- helpers


def _expand565(c, shift_only=False):
    """565 -> 888 by bit replication (what D3D/GPUs do). shift_only=True reproduces Pillow's
    decoder (plain shifts, white = 248), used only to cross-check against Pillow in the tests."""
    c = c.astype(np.int32)
    r = (c >> 11) & 31
    g = (c >> 5) & 63
    b = c & 31
    if shift_only:
        return np.stack([r << 3, g << 2, b << 3], axis=-1)
    return np.stack([(r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)], axis=-1)


def _pack565(rgb):
    """float/int (..., 3) in 0..255 -> uint16 565 (rounded)."""
    rgb = np.clip(np.rint(rgb), 0, 255).astype(np.int32)
    r = (rgb[..., 0] * 31 + 127) // 255
    g = (rgb[..., 1] * 63 + 127) // 255
    b = (rgb[..., 2] * 31 + 127) // 255
    return ((r << 11) | (g << 5) | b).astype(np.uint16)


def _dxt1_palette(c0, c1, shift_only=False):
    """(N,) uint16 x2 -> palette (N,4,3) int32 and alpha (N,4)."""
    p0 = _expand565(c0, shift_only)
    p1 = _expand565(c1, shift_only)
    four = (c0 > c1)[:, None]
    p2 = np.where(four, (2 * p0 + p1) // 3, (p0 + p1) // 2)
    p3 = np.where(four, (p0 + 2 * p1) // 3, 0)
    pal = np.stack([p0, p1, p2, p3], axis=1)
    alpha = np.full((len(c0), 4), 255, np.int32)
    alpha[:, 3] = np.where(c0 > c1, 255, 0)
    return pal, alpha


def blocks_to_image(blocks_px, width, height):
    """(nby*nbx, 16, C) block pixels -> (height, width, C) image (cropped)."""
    nbx = max(1, (width + 3) // 4)
    nby = max(1, (height + 3) // 4)
    c = blocks_px.shape[-1]
    img = blocks_px.reshape(nby, nbx, 4, 4, c).transpose(0, 2, 1, 3, 4).reshape(nby * 4, nbx * 4, c)
    return img[:height, :width]


def image_to_blocks(img):
    """(h, w, C) -> (nby*nbx, 16, C), padding by edge replication to whole blocks."""
    h, w, c = img.shape
    nbx = max(1, (w + 3) // 4)
    nby = max(1, (h + 3) // 4)
    ph, pw = nby * 4 - h, nbx * 4 - w
    if ph or pw:
        img = np.pad(img, ((0, ph), (0, pw), (0, 0)), mode='edge')
    return img.reshape(nby, 4, nbx, 4, c).transpose(0, 2, 1, 3, 4).reshape(nby * nbx, 16, c)


def block_count(width, height):
    return max(1, (width + 3) // 4) * max(1, (height + 3) // 4)


def block_size(kind):
    return 8 if kind == 'dxt1' else 16


# ---------------------------------------------------------------------- decoding


def decode_color_blocks(raw8, shift_only=False):
    """(N, 8) uint8 colour blocks -> rgb (N,16,3) uint8, alpha (N,16) uint8 (0 for DXT1 index 3 in 3-colour mode)."""
    c0 = raw8[:, 0].astype(np.uint16) | (raw8[:, 1].astype(np.uint16) << 8)
    c1 = raw8[:, 2].astype(np.uint16) | (raw8[:, 3].astype(np.uint16) << 8)
    bits = (raw8[:, 4].astype(np.uint32) | (raw8[:, 5].astype(np.uint32) << 8) |
            (raw8[:, 6].astype(np.uint32) << 16) | (raw8[:, 7].astype(np.uint32) << 24))
    idx = (bits[:, None] >> (2 * np.arange(16, dtype=np.uint32))[None, :]) & 3
    pal, palpha = _dxt1_palette(c0, c1, shift_only)
    n = len(raw8)
    rgb = pal[np.arange(n)[:, None], idx]
    a = palpha[np.arange(n)[:, None], idx]
    return rgb.astype(np.uint8), a.astype(np.uint8)


def decode_dxt3_alpha(raw8):
    nib = np.empty((len(raw8), 16), np.uint8)
    nib[:, 0::2] = raw8 & 0x0F
    nib[:, 1::2] = raw8 >> 4
    return (nib * 17).astype(np.uint8)


def _alpha_values(a0, a1):
    a0 = a0.astype(np.int32)
    a1 = a1.astype(np.int32)
    eight = (a0 > a1)[:, None]
    k = np.arange(1, 7)[None, :]
    v8 = ((7 - k) * a0[:, None] + k * a1[:, None]) // 7
    k5 = np.arange(1, 5)[None, :]
    v6 = ((5 - k5) * a0[:, None] + k5 * a1[:, None]) // 5
    v6 = np.concatenate([v6, np.zeros((len(a0), 1), np.int32), np.full((len(a0), 1), 255, np.int32)], axis=1)
    rest = np.where(eight, v8, v6)
    return np.concatenate([a0[:, None], a1[:, None], rest], axis=1)


def decode_dxt5_alpha(raw8):
    a0 = raw8[:, 0]
    a1 = raw8[:, 1]
    bits = np.zeros(len(raw8), np.uint64)
    for i in range(6):
        bits |= raw8[:, 2 + i].astype(np.uint64) << np.uint64(8 * i)
    idx = ((bits[:, None] >> (3 * np.arange(16, dtype=np.uint64))[None, :]) & np.uint64(7)).astype(np.int64)
    vals = _alpha_values(a0, a1)
    return vals[np.arange(len(raw8))[:, None], idx].astype(np.uint8)


def decode(data, width, height, kind, use_alpha=True, shift_only=False):
    """DXT data of one mip level -> RGBA (height, width, 4) uint8.

    kind: 'dxt1', 'dxt3', 'dxt5'. For DXT1 `use_alpha=False` ignores the punch-through alpha (alphaDepth 0).
    """
    n = block_count(width, height)
    bs = block_size(kind)
    raw = np.frombuffer(bytes(data[:n * bs]), np.uint8).reshape(n, bs)
    if kind == 'dxt1':
        rgb, a = decode_color_blocks(raw, shift_only)
        if not use_alpha:
            a = np.full_like(a, 255)
    else:
        rgb, _ = decode_color_blocks(raw[:, 8:16].copy(), shift_only)
        a = decode_dxt3_alpha(raw[:, :8]) if kind == 'dxt3' else decode_dxt5_alpha(raw[:, :8])
    px = np.concatenate([rgb, a[..., None]], axis=-1)
    return blocks_to_image(px, width, height)


# ---------------------------------------------------------------------- encoding: colour


def _nearest(px, pal, allowed):
    """px (N,16,3) float, pal (N,4,3), allowed (N,4) bool -> idx (N,16), err (N,)."""
    d = ((px[:, :, None, :] - pal[:, None, :, :].astype(np.float64)) ** 2).sum(-1)   # (N,16,4)
    d = np.where(allowed[:, None, :], d, np.inf)
    idx = d.argmin(-1)
    err = np.take_along_axis(d, idx[..., None], -1)[..., 0]
    return idx, err


def _principal_axis(px, w):
    """Weighted principal axis of each block's colours. px (N,16,3), w (N,16) -> (N,3) unit vectors."""
    ws = w.sum(1, keepdims=True)
    ws = np.where(ws == 0, 1, ws)
    mean = (px * w[..., None]).sum(1) / ws
    d = (px - mean[:, None, :]) * np.sqrt(w)[..., None]
    cov = np.einsum('nki,nkj->nij', d, d)
    # start from the extent of the block; power iteration
    big = np.where(w[..., None] > 0, px, -np.inf).max(1)
    small = np.where(w[..., None] > 0, px, np.inf).min(1)
    v = np.nan_to_num(big - small, nan=1.0, posinf=1.0, neginf=1.0)
    v = np.where(np.abs(v).sum(1, keepdims=True) < 1e-6, np.array([[1.0, 1.0, 1.0]]), v)
    for _ in range(8):
        v = np.einsum('nij,nj->ni', cov, v)
        norm = np.linalg.norm(v, axis=1, keepdims=True)
        bad = norm[:, 0] < 1e-9
        v = np.where(bad[:, None], np.array([[1.0, 1.0, 1.0]]) / np.sqrt(3), v / np.where(norm == 0, 1, norm))
    return mean, v


def _ls_endpoints(px, w, idx, three):
    """Least-squares endpoints for fixed indices. Returns (A, B) float (N,3), ok mask."""
    # palette weights of endpoint A (c0) and B (c1) per index
    wa4 = np.array([1.0, 0.0, 2.0 / 3.0, 1.0 / 3.0])
    wa3 = np.array([1.0, 0.0, 0.5, 0.0])
    alpha = np.where(three[:, None], wa3[idx], wa4[idx])
    beta = np.where(three[:, None], np.where(idx == 3, 0.0, 1.0 - wa3[idx]), 1.0 - wa4[idx])
    use = w * np.where(three[:, None] & (idx == 3), 0.0, 1.0)
    aa = (use * alpha * alpha).sum(1)
    bb = (use * beta * beta).sum(1)
    ab = (use * alpha * beta).sum(1)
    ax = ((use * alpha)[..., None] * px).sum(1)
    bx = ((use * beta)[..., None] * px).sum(1)
    det = aa * bb - ab * ab
    ok = np.abs(det) > 1e-9
    det = np.where(ok, det, 1.0)
    A = (ax * bb[:, None] - bx * ab[:, None]) / det[:, None]
    B = (bx * aa[:, None] - ax * ab[:, None]) / det[:, None]
    return np.clip(A, 0, 255), np.clip(B, 0, 255), ok


def _eval(px, w, c0, c1, three, transparent):
    """Indices and error for endpoints c0/c1 in the requested mode (before ordering fix-up)."""
    p0 = _expand565(c0)
    p1 = _expand565(c1)
    t = three[:, None]
    pal = np.stack([p0, p1,
                    np.where(t, (p0 + p1) // 2, (2 * p0 + p1) // 3),
                    np.where(t, 0, (p0 + 2 * p1) // 3)], axis=1)
    allowed = np.ones((len(c0), 4), bool)
    allowed[:, 3] = ~three
    idx, err = _nearest(px, pal, allowed)
    idx = np.where(transparent, 3, idx)
    err = np.where(transparent | (w == 0), 0.0, err)
    return idx, err.sum(1)


def encode_color(rgb, transparent=None):
    """rgb (N,16,3) uint8, transparent (N,16) bool or None -> (N, 8) uint8 DXT1 colour blocks.

    Blocks with any transparent pixel are written in 3-colour mode (c0 <= c1) with index 3 for those
    pixels; all other blocks in 4-colour mode (c0 > c1).
    """
    n = len(rgb)
    px = rgb.astype(np.float64)
    if transparent is None:
        transparent = np.zeros((n, 16), bool)
    three = transparent.any(1)
    w = (~transparent).astype(np.float64)
    mean, axis = _principal_axis(px, w)
    t = ((px - mean[:, None, :]) * axis[:, None, :]).sum(-1)
    tmax = np.where(w > 0, t, -np.inf).max(1)
    tmin = np.where(w > 0, t, np.inf).min(1)
    tmax = np.where(np.isfinite(tmax), tmax, 0)
    tmin = np.where(np.isfinite(tmin), tmin, 0)
    inset = (tmax - tmin) / 16.0
    ea = np.clip(mean + (tmax - inset)[:, None] * axis, 0, 255)
    eb = np.clip(mean + (tmin + inset)[:, None] * axis, 0, 255)
    c0 = _pack565(ea)
    c1 = _pack565(eb)
    idx, err = _eval(px, w, c0, c1, three, transparent)
    for _ in range(2):
        A, B, ok = _ls_endpoints(px, w, idx, three)
        n0 = np.where(ok, _pack565(A), c0)
        n1 = np.where(ok, _pack565(B), c1)
        nidx, nerr = _eval(px, w, n0, n1, three, transparent)
        better = nerr < err
        c0 = np.where(better, n0, c0)
        c1 = np.where(better, n1, c1)
        idx = np.where(better[:, None], nidx, idx)
        err = np.where(better, nerr, err)
    # mode fix-up: 4-colour needs c0 > c1, 3-colour needs c0 <= c1
    swap4 = ~three & (c0 < c1)
    swap3 = three & (c0 > c1)
    swap = swap4 | swap3
    c0s = np.where(swap, c1, c0)
    c1s = np.where(swap, c0, c1)
    remap4 = np.array([1, 0, 3, 2])
    remap3 = np.array([1, 0, 2, 3])
    idx = np.where(swap4[:, None], remap4[idx], idx)
    idx = np.where(swap3[:, None], remap3[idx], idx)
    c0, c1 = c0s, c1s
    # equal endpoints in an opaque block: 3-colour mode by definition; index 0 everywhere is exact
    eq = ~three & (c0 == c1)
    idx = np.where(eq[:, None], 0, idx)
    # fully transparent blocks
    allt = transparent.all(1)
    c0 = np.where(allt, 0, c0)
    c1 = np.where(allt, 0, c1)
    idx = np.where(allt[:, None], 3, idx)
    bits = (idx.astype(np.uint32) << (2 * np.arange(16, dtype=np.uint32))[None, :]).sum(1).astype(np.uint32)
    out = np.empty((n, 8), np.uint8)
    out[:, 0] = c0 & 0xFF
    out[:, 1] = c0 >> 8
    out[:, 2] = c1 & 0xFF
    out[:, 3] = c1 >> 8
    for i in range(4):
        out[:, 4 + i] = (bits >> (8 * i)) & 0xFF
    return out


# ---------------------------------------------------------------------- encoding: alpha


def encode_dxt3_alpha(a):
    q = np.clip((a.astype(np.int32) + 8) // 17, 0, 15).astype(np.uint8)
    return (q[:, 0::2] | (q[:, 1::2] << 4)).astype(np.uint8)


def _alpha_try(a, a0, a1):
    vals = _alpha_values(a0, a1)
    d = np.abs(a[:, :, None].astype(np.int32) - vals[:, None, :])
    idx = d.argmin(-1)
    err = (np.take_along_axis(d, idx[..., None], -1)[..., 0] ** 2).sum(1)
    return idx, err


def encode_dxt5_alpha(a):
    a = a.astype(np.int32)
    n = len(a)
    hi = a.max(1)
    lo = a.min(1)
    # 8-value mode: a0 > a1
    a0 = hi.copy()
    a1 = lo.copy()
    same = a0 == a1
    idx8, err8 = _alpha_try(a, a0, a1)
    idx8 = np.where(same[:, None], 0, idx8)
    err8 = np.where(same, 0, err8)
    # 6-value mode (a0 <= a1) with explicit 0 and 255, endpoints from the values in between
    mid = np.where((a > 0) & (a < 255), a, -1)
    mhi = mid.max(1)
    mlo = np.where(mid >= 0, mid, 256).min(1)
    has_mid = mhi >= 0
    b0 = np.where(has_mid, mlo, 0)
    b1 = np.where(has_mid, mhi, 0)
    idx6, err6 = _alpha_try(a, b0, b1)
    use6 = (err6 < err8) & ~same
    e0 = np.where(use6, b0, a0)
    e1 = np.where(use6, b1, a1)
    idx = np.where(use6[:, None], idx6, idx8)
    bits = np.zeros(n, np.uint64)
    for i in range(16):
        bits |= idx[:, i].astype(np.uint64) << np.uint64(3 * i)
    out = np.empty((n, 8), np.uint8)
    out[:, 0] = e0
    out[:, 1] = e1
    for i in range(6):
        out[:, 2 + i] = ((bits >> np.uint64(8 * i)) & np.uint64(0xFF)).astype(np.uint8)
    return out


def encode(rgba, kind, use_alpha=True):
    """RGBA (h, w, 4) uint8 -> DXT bytes of one level."""
    blocks = image_to_blocks(np.ascontiguousarray(rgba))
    rgb = blocks[..., :3]
    a = blocks[..., 3]
    if kind == 'dxt1':
        transparent = (a < 128) if use_alpha else None
        return encode_color(rgb, transparent).tobytes()
    color = encode_color(rgb, None)
    alpha = encode_dxt3_alpha(a) if kind == 'dxt3' else encode_dxt5_alpha(a)
    return np.concatenate([alpha, color], axis=1).tobytes()


def encode_blocks(blocks_rgba, kind, use_alpha=True):
    """Like encode() but on (N,16,4) block pixels; returns (N, block size) uint8."""
    rgb = blocks_rgba[..., :3]
    a = blocks_rgba[..., 3]
    if kind == 'dxt1':
        return encode_color(rgb, (a < 128) if use_alpha else None)
    color = encode_color(rgb, None)
    alpha = encode_dxt3_alpha(a) if kind == 'dxt3' else encode_dxt5_alpha(a)
    return np.concatenate([alpha, color], axis=1)
