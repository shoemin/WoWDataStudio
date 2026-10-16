"""BLP2 decode / encode on real textures of every format the 1.12 client ships."""
import io
import random
import unittest

import numpy as np
from PIL import Image

from common import client, needs_client

from wowdata import blp, dxt

# one real file per (encoding, alphaDepth, alphaEncoding, hasMips) combination found in the client
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


def psnr(a, b):
    mse = float(((a.astype(np.float64) - b.astype(np.float64)) ** 2).mean())
    return 99.0 if mse == 0 else 10 * np.log10(255.0 ** 2 / mse)


@needs_client
class DecodeTests(unittest.TestCase):
    def test_samples_parse_and_validate(self):
        for label, p in SAMPLES.items():
            b = blp.BLP(client().read(p))
            self.assertEqual(blp.validate(b.data), [], label)
            img = b.decode(0)
            self.assertEqual(img.shape, (b.height, b.width, 4), label)
            for lv in range(b.level_count):
                b.decode(lv)

    def test_matches_pillow(self):
        """Pillow's BLP reader is an independent implementation.
        DXT: Pillow expands 565 with plain shifts (white = 248), so we compare with our decoder in that mode;
        colour and alpha must then match exactly.
        Palette: Pillow ignores the alpha plane (it takes alpha from the palette entry), so only the colours
        are compared with Pillow; the alpha plane is checked against the client's own rule below."""
        for label, p in SAMPLES.items():
            data = client().read(p)
            b = blp.BLP(data)
            try:
                pil = np.array(Image.open(io.BytesIO(data)).convert('RGBA'))
            except Exception as e:           # Pillow does not read every variant
                print('  (Pillow cannot read %s: %s)' % (label, e))
                continue
            if b.encoding == blp.ENC_DXT:
                ours = dxt.decode(b.mip_data(0), b.width, b.height, b.dxt_kind, b.alpha_depth > 0, shift_only=True)
                if b.alpha_depth == 0:
                    ours = ours.copy()
                    ours[..., 3] = pil[..., 3]
                # where alpha is 0 the colour is irrelevant (Pillow keeps it, DXT1 index 3 is black)
                vis = pil[..., 3] > 0
                self.assertTrue(np.array_equal(ours[vis], pil[vis]), label)
                self.assertTrue(np.array_equal(ours[..., 3], pil[..., 3]), label)
            else:
                ours = b.decode(0)
                self.assertTrue(np.array_equal(ours[..., :3], pil[..., :3]), label)

    def test_palette_alpha_plane_like_the_client(self):
        """BlpImage_PalToArgb8888 @005a87d0: 1-bit alpha LSB first via {0x00, 0xff}; 4-bit low nibble first
        via {0x00, 0x11, ..., 0xff}; 8-bit one byte per pixel. Reference written as a plain per-pixel loop."""
        for label in ('palette 1-bit alpha', 'palette 8-bit alpha', 'palette, 512x512, alphaEncoding 4'):
            b = blp.BLP(client().read(SAMPLES[label]))
            raw = b.mip_data(0)
            n = b.width * b.height
            plane = raw[n:]
            ref = []
            for i in range(n):
                if b.alpha_depth == 1:
                    ref.append(0xFF if (plane[i >> 3] >> (i & 7)) & 1 else 0)
                elif b.alpha_depth == 4:
                    ref.append(((plane[i >> 1] >> (4 * (i & 1))) & 0xF) * 0x11)
                else:
                    ref.append(plane[i])
            self.assertEqual(b.decode(0)[..., 3].reshape(-1).tolist(), ref, label)


@needs_client
class RoundTripTests(unittest.TestCase):
    def test_unmodified_is_byte_identical(self):
        for label, p in SAMPLES.items():
            b = blp.BLP(client().read(p))
            out, rep = blp.encode_like(b, b.decode(0))
            self.assertTrue(rep['identical'], label)
            self.assertEqual(out, b.data, label)

    def test_random_sample_byte_identical(self):
        """300 random BLPs from all archives (fixed seed): decode -> encode_like gives the same bytes."""
        names = sorted(p for u, (p, a) in client().names().items() if u.endswith('.BLP'))
        rnd = random.Random(1125)
        checked = 0
        for p in rnd.sample(names, 300):
            data = client().read(p)
            if not data or data[:4] != b'BLP2':
                continue
            b = blp.BLP(data)
            if b.encoding not in (blp.ENC_DXT, blp.ENC_PALETTE):
                continue
            out, rep = blp.encode_like(b, b.decode(0))
            self.assertEqual(out, data, p)
            checked += 1
        self.assertGreater(checked, 250)

    def test_partial_edit_touches_only_its_blocks(self):
        b = blp.BLP(client().read(SAMPLES['dxt1 1-bit alpha']))
        img = b.decode(0).copy()
        img[100:118, 100:118, :3] = (255, 0, 0)
        img[100:118, 100:118, 3] = 255
        out, rep = blp.encode_like(b, img)
        self.assertFalse(rep['identical'])
        self.assertEqual(blp.validate(out), [])
        nb = blp.BLP(out)
        self.assertEqual((nb.mip_offsets, nb.mip_sizes, len(out)), (b.mip_offsets, b.mip_sizes, len(b.data)))
        # level 0: blocks (rows 25..29, cols 25..29) -> 5x5 = 25 blocks changed, everything else identical
        o = np.frombuffer(b.mip_data(0), np.uint8).reshape(64, 64, 8)
        n = np.frombuffer(nb.mip_data(0), np.uint8).reshape(64, 64, 8)
        changed = np.argwhere((o != n).any(-1))
        self.assertTrue(set(map(tuple, changed)) <= {(r, c) for r in range(25, 30) for c in range(25, 30)})
        self.assertEqual(rep['per_level'][0]['re_encoded'], 25)
        dec = nb.decode(0)
        self.assertTrue((dec[101:117, 101:117, 0] >= 240).all() and (dec[101:117, 101:117, 1:3] <= 15).all())
        untouched = np.ones(img.shape[:2], bool)
        untouched[100:120, 100:120] = False
        self.assertTrue(np.array_equal(dec[untouched], b.decode(0)[untouched]))

    def test_short_thin_levels(self):
        """628 Blizzard DXT3 files store max(1, (w/4)*(h/4)) blocks for thin mips (16x2 -> 1 block, not 4); they must
        decode at every level, validate, and keep that layout when edited."""
        b = blp.BLP(client().read('Interface\\Buttons\\UI-SliderBar-Border.blp'))
        self.assertEqual((b.width, b.height, b.mip_sizes[2]), (64, 8, 16))
        self.assertEqual(blp.validate(b.data), [])
        for lv in range(b.level_count):
            b.decode(lv)
        img = b.decode(0).copy()
        img[:, :8] = (255, 255, 255, 255)
        out, rep = blp.encode_like(b, img)
        nb = blp.BLP(out)
        self.assertEqual((nb.mip_offsets, nb.mip_sizes, len(out)), (b.mip_offsets, b.mip_sizes, len(b.data)))
        self.assertEqual(blp.validate(out), [])
        self.assertTrue((nb.decode(0)[:, :8] >= 250).all())

    def test_full_reencode_quality(self):
        for label in ('dxt1 no alpha', 'dxt1 1-bit alpha', 'dxt3'):
            b = blp.BLP(client().read(SAMPLES[label]))
            src = b.decode(0)
            data = blp.encode_new(src, blp.format_of(b), mips=bool(b.has_mips), template=b)
            nb = blp.BLP(data)
            self.assertEqual(blp.validate(data), [], label)
            self.assertEqual((nb.encoding, nb.alpha_depth, nb.alpha_encoding, nb.width, nb.height, len(data)),
                             (b.encoding, b.alpha_depth, b.alpha_encoding, b.width, b.height, len(b.data)), label)
            dec = nb.decode(0)
            vis = src[..., 3] > 0
            self.assertGreater(psnr(dec[..., :3][vis], src[..., :3][vis]), 40.0, label)
            self.assertTrue(np.array_equal(dec[..., 3] > 127, src[..., 3] > 127) or b.alpha_depth > 1, label)

    def test_palette_edit_keeps_palette(self):
        b = blp.BLP(client().read(SAMPLES['palette no alpha']))
        img = b.decode(0).copy()
        img[0:10, 0:10] = img[200, 200]          # an existing palette colour
        out, rep = blp.encode_like(b, img)
        self.assertEqual(rep['palette'], 'keep')
        self.assertEqual(out[blp.PALETTE_OFFSET:blp.PALETTE_OFFSET + 1024], b.palette_raw)
        self.assertTrue(np.array_equal(blp.BLP(out).decode(0), img))


class SyntheticTests(unittest.TestCase):
    def _image(self, w=64, h=32):
        y, x = np.mgrid[0:h, 0:w]
        img = np.zeros((h, w, 4), np.uint8)
        img[..., 0] = x * 255 // (w - 1)
        img[..., 1] = y * 255 // (h - 1)
        img[..., 2] = 128
        img[..., 3] = np.where(x < w // 4, 0, np.where(x < w // 2, 128, 255))
        return img

    def test_every_format(self):
        img = self._image()
        for fmt in blp.FORMATS:
            data = blp.encode_new(img, fmt)
            self.assertEqual(blp.validate(data), [], fmt)
            b = blp.BLP(data)
            self.assertEqual(b.level_count, 7, fmt)            # 64x32 .. 1x1
            dec = b.decode(0)
            vis = img[..., 3] >= 128
            self.assertGreater(psnr(dec[..., :3][vis], img[..., :3][vis]), 30.0, fmt)
            # an unmodified re-import of the new file is byte-identical too
            out, rep = blp.encode_like(b, dec)
            self.assertEqual(out, data, fmt)

    def test_small_mips(self):
        img = self._image(4, 4)
        for fmt in ('dxt1', 'dxt3', 'dxt5'):
            data = blp.encode_new(img, fmt)
            b = blp.BLP(data)
            self.assertEqual(b.level_count, 3)
            self.assertEqual(b.decode(2).shape, (1, 1, 4))

    def test_validate_rejects(self):
        img = self._image()
        good = blp.encode_new(img, 'dxt1')
        self.assertEqual(blp.validate(good), [])
        self.assertTrue(blp.validate(good[:-8]))                       # last mip cut off
        bad = bytearray(good)
        bad[8] = 3                                                     # raw encoding
        self.assertTrue(blp.validate(bytes(bad)))
        self.assertTrue(blp.validate(blp.encode_new(self._image(48, 32), 'dxt1')))   # not a power of two

    def test_dxt5_alpha_modes(self):
        a = np.array([[0, 255, 100, 120] * 4], np.uint8).reshape(1, 16)
        enc = dxt.encode_dxt5_alpha(a)
        dec = dxt.decode_dxt5_alpha(enc)
        self.assertEqual(int(dec[0, 0]), 0)
        self.assertEqual(int(dec[0, 1]), 255)
        self.assertLessEqual(int(np.abs(dec.astype(int) - a.astype(int)).max()), 4)


if __name__ == '__main__':
    unittest.main()
