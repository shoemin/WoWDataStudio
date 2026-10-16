"""Texture round trip: BLP (from the archives) -> PNG + sidecar JSON -> edited PNG -> BLP in the staging folder.

The sidecar `<name>.blp.json` written next to every exported PNG records where the texture lives in the
archives, which archive it came from, a SHA-256 of the original file and its exact BLP format
(encoding, alpha depth/encoding, mip levels, offsets and sizes). `import` uses it to encode the PNG
back into the same format and mip layout, re-encoding only what was edited (see blp.encode_like).
"""
import datetime
import hashlib
import json
import os

import numpy as np
from PIL import Image

from . import blp
from .targets import out_path
from .vfs import resolve_name

SIDECAR_SUFFIX = '.blp.json'


class TextureError(Exception):
    pass


def sidecar_for(png_path):
    base = png_path[:-4] if png_path.lower().endswith('.png') else png_path
    return base + SIDECAR_SUFFIX


def export_one(client, archive_path, out_dir, context=None):
    """Write <out_dir>\\<archive path>.png and its sidecar. Returns (png path, sidecar dict)."""
    name = resolve_name(archive_path)
    src = client.find(name)
    if src is None:
        raise TextureError('%s is not in the archives' % name)
    data = client.read(name)
    b = blp.BLP(data)
    rgba = b.decode(0)
    png = out_path(out_dir, name, '.png')
    if b.alpha_depth == 0:
        Image.fromarray(np.ascontiguousarray(rgba[..., :3]), 'RGB').save(png, optimize=False)
    else:
        Image.fromarray(np.ascontiguousarray(rgba), 'RGBA').save(png, optimize=False)
    side = {
        'tool': 'wowdata',
        'archive_path': name,
        'source': src.name,
        'source_kind': src.kind,
        'sha256': hashlib.sha256(data).hexdigest(),
        'size': len(data),
        'exported': datetime.datetime.now().isoformat(timespec='seconds'),
        'png': os.path.basename(png),
        'png_mode': 'RGB' if b.alpha_depth == 0 else 'RGBA',
        'blp': b.info(),
        'context': context or {},
        'note': 'Edit the PNG (keep its size for an exact round trip), then: wowdata texture import "%s"' % os.path.basename(png),
    }
    with open(sidecar_for(png), 'w', encoding='utf-8') as f:
        json.dump(side, f, indent=2)
    return png, side


def load_png(path):
    im = Image.open(path)
    im.load()
    if im.mode != 'RGBA':
        im = im.convert('RGBA')
    return np.array(im, dtype=np.uint8)


def import_one(client, png_path, staging_dir, target=None, fmt=None, palette_mode='auto', template=None, full=False):
    """Encode an edited PNG into the staging folder. Returns a result dict.

    target: archive path to write (default: the sidecar's archive_path).
    fmt: force a format ('dxt1', 'dxt1a', 'dxt3', 'dxt5', 'pal', 'pal1', 'pal4', 'pal8'); default = the original's.
    full: re-encode every block / pixel in the original's format instead of only the edited ones
          (for testing the encoder; normally the block-reuse path is better).
    template: archive path of the BLP whose format/layout to copy (default: the sidecar's archive_path).
    """
    side_path = sidecar_for(png_path)
    side = None
    if os.path.exists(side_path):
        with open(side_path, 'r', encoding='utf-8') as f:
            side = json.load(f)
    target = resolve_name(target or (side or {}).get('archive_path') or '')
    if not target:
        raise TextureError('%s has no sidecar (%s); pass --as <archive path>' % (png_path, os.path.basename(side_path)))
    if not target.lower().endswith('.blp'):
        raise TextureError('target %s must end with .blp' % target)
    tpl_path = resolve_name(template or (side or {}).get('archive_path') or target)
    orig_data = client.read(tpl_path)
    warnings = []
    if side and orig_data is not None and hashlib.sha256(orig_data).hexdigest() != side.get('sha256') and tpl_path == side.get('archive_path'):
        warnings.append('the archive copy of %s changed since the export (sha256 differs); encoding against the current one' % tpl_path)
    rgba = load_png(png_path)
    h, w = rgba.shape[:2]
    if not (blp.is_pow2(w) and blp.is_pow2(h)):
        raise TextureError('%s is %dx%d; the client needs power-of-two sizes (e.g. 256x256)' % (png_path, w, h))
    if orig_data is not None:
        orig = blp.BLP(orig_data)
        if full or (fmt and fmt != blp.format_of(orig)):
            f = fmt or blp.format_of(orig)
            data = blp.encode_new(rgba, f, mips=bool(orig.has_mips), template=orig if f == blp.format_of(orig) else None)
            nb = blp.BLP(data)
            rep = {'format': nb.format_name(), 'identical': data == orig_data, 'full_reencode': True,
                   'same_format': f == blp.format_of(orig), 'same_layout': (nb.mip_offsets, nb.mip_sizes, len(data)) ==
                   (orig.mip_offsets, orig.mip_sizes, len(orig_data))}
            if f == blp.format_of(orig):
                o0 = orig.decode(0).astype(np.int16)
                n0 = nb.decode(0).astype(np.int16)
                d = np.abs(o0 - n0)[..., :3]
                rep['vs_original_max_diff'] = int(d.max())
                rep['vs_original_mean_diff'] = round(float(d.mean()), 4)
                mse = float(((o0 - n0)[..., :3].astype(np.float64) ** 2).mean())
                rep['vs_original_psnr_db'] = round(10 * np.log10(255 ** 2 / mse), 2) if mse else None
        else:
            data, rep = blp.encode_like(orig, rgba, palette_mode=palette_mode)
    else:
        f = fmt or blp.suggest_format(rgba)
        data = blp.encode_new(rgba, f, mips=True)
        rep = {'format': blp.BLP(data).format_name(), 'identical': False, 'new_file': True}
        warnings.append('no original %s in the archives: wrote a new %s file' % (tpl_path, f))
    problems = blp.validate(data)
    if problems:
        raise TextureError('the encoded file would not load in the client: ' + '; '.join(problems))
    dest = out_path(staging_dir, target)
    with open(dest + '.tmp', 'wb') as f:
        f.write(data)
    os.replace(dest + '.tmp', dest)
    # what the client will display, compared with the PNG
    dec = blp.BLP(data).decode(0)
    a_used = blp.BLP(data).alpha_depth > 0
    diff = np.abs(dec.astype(np.int16) - rgba.astype(np.int16))
    if not a_used:
        diff[..., 3] = 0
    else:
        # colour of pixels that are transparent in both is invisible (and not stored by DXT1)
        hidden = (dec[..., 3] == 0) & (rgba[..., 3] == 0)
        diff[hidden] = 0
    rep.update({
        'png': png_path, 'target': target, 'template': tpl_path, 'staged': dest, 'bytes': len(data),
        'max_pixel_diff': int(diff.max()), 'mean_abs_diff': round(float(diff[..., :3].mean()), 4),
        'pixels_differing': int((diff.max(-1) > 0).sum()), 'warnings': warnings,
    })
    return rep


def tint(png_in, png_out, color=(255, 64, 64), strength=0.5):
    """A visibly tinted copy of a PNG (for in-game tests): RGB blended toward `color`, alpha kept."""
    rgba = load_png(png_in).astype(np.float64)
    c = np.array(color, np.float64)
    rgba[..., :3] = rgba[..., :3] * (1 - strength) + c * strength
    out = np.clip(np.rint(rgba), 0, 255).astype(np.uint8)
    Image.fromarray(out, 'RGBA').save(png_out)
    return png_out
