"""The texture round trip end to end (Mottled Boar), and the delivery commands against a FAKE client folder.

Nothing here writes into the real client: deliveries use a temporary `client_dir`, and the real
Client\\Data listing (names, sizes, times) is compared before and after.
"""
import hashlib
import json
import os
import shutil
import unittest

import numpy as np

from common import CFG, client, data_dir_snapshot, needs_client, tempdir

from wowdata import blp, config, deploy, texture
from wowdata.mpq import MPQArchive

BOAR = 'Creature\\Boar\\BoarSkinIvory.blp'


def fake_cfg(root):
    cfg = config.Config(CFG)
    cfg['client_dir'] = os.path.join(root, 'FakeClient')
    os.makedirs(os.path.join(cfg['client_dir'], 'Data'))
    for k in ('staging_dir', 'build_dir', 'backup_dir', 'state_dir', 'export_dir'):
        cfg[k] = os.path.join(root, k)
    return cfg


@needs_client
class TextureWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.before = data_dir_snapshot()

    @classmethod
    def tearDownClass(cls):
        assert data_dir_snapshot() == cls.before, 'the real Client\\Data folder changed during the tests'

    def setUp(self):
        self.root = tempdir()
        self.cfg = fake_cfg(self.root)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_mottled_boar_round_trip(self):
        c = client()
        png, side = texture.export_one(c, BOAR, self.cfg.export_dir, {'target': 'creature:3098'})
        self.assertTrue(os.path.exists(png))
        with open(texture.sidecar_for(png)) as f:
            meta = json.load(f)
        self.assertEqual(meta['archive_path'], BOAR)
        self.assertEqual(meta['source'], 'texture.MPQ')
        self.assertEqual(meta['blp']['format'], 'DXT1 + 1-bit alpha')
        self.assertEqual((meta['blp']['width'], meta['blp']['height'], meta['blp']['levels']), (256, 256, 9))
        # 1) unmodified PNG -> byte-identical BLP
        rep = texture.import_one(c, png, self.cfg.staging_dir)
        self.assertTrue(rep['identical'])
        with open(rep['staged'], 'rb') as f:
            staged = f.read()
        self.assertEqual(hashlib.sha256(staged).hexdigest(), meta['sha256'])
        # 2) full re-encode: same format and layout, only DXT approximation differences
        rep = texture.import_one(c, png, os.path.join(self.root, 'full'), full=True)
        self.assertTrue(rep['same_format'] and rep['same_layout'])
        self.assertGreater(rep['vs_original_psnr_db'], 45)
        # 3) a tinted copy -> staging only
        tinted = texture.tint(png, os.path.join(self.root, 'tinted.png'), (40, 255, 40), 0.55)
        rep = texture.import_one(c, tinted, self.cfg.staging_dir, target=BOAR)
        self.assertFalse(rep['identical'])
        with open(rep['staged'], 'rb') as f:
            data = f.read()
        self.assertEqual(blp.validate(data), [])
        b = blp.BLP(data)
        o = blp.BLP(c.read(BOAR))
        self.assertEqual((b.encoding, b.alpha_depth, b.alpha_encoding, b.mip_offsets, b.mip_sizes),
                         (o.encoding, o.alpha_depth, o.alpha_encoding, o.mip_offsets, o.mip_sizes))
        vis = o.decode(0)[..., 3] > 0
        self.assertGreater(b.decode(0)[..., 1][vis].mean(), o.decode(0)[..., 1][vis].mean() + 40)   # visibly greener
        self.assertTrue(np.array_equal(b.decode(0)[..., 3], o.decode(0)[..., 3]))                  # same cut-outs

    def test_import_without_sidecar_needs_target(self):
        c = client()
        png, _ = texture.export_one(c, BOAR, self.cfg.export_dir)
        lone = os.path.join(self.root, 'lone.png')
        shutil.copy(png, lone)
        with self.assertRaises(texture.TextureError):
            texture.import_one(c, lone, self.cfg.staging_dir)
        rep = texture.import_one(c, lone, self.cfg.staging_dir, target='Creature\\Boar\\BoarSkinGreenTest.blp', template=BOAR)
        self.assertTrue(os.path.exists(rep['staged']))


@needs_client
class DeliveryTests(unittest.TestCase):
    def setUp(self):
        self.root = tempdir()
        self.cfg = fake_cfg(self.root)
        self.data = self.cfg.data_dir
        src = client().read(BOAR)
        p = os.path.join(self.cfg.staging_dir, 'Creature', 'Boar', 'BoarSkinIvory.blp')
        os.makedirs(os.path.dirname(p))
        with open(p, 'wb') as f:
            f.write(src)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_loose_install_and_remove(self):
        acts = deploy.loose_install(self.cfg)
        dest = os.path.join(self.data, 'Creature', 'Boar', 'BoarSkinIvory.blp')
        self.assertEqual([a[3] for a in acts], ['add'])
        self.assertTrue(os.path.exists(dest))
        self.assertEqual(deploy.loose_status(self.cfg)[0][1], 'ok')
        self.assertEqual(deploy.loose_install(self.cfg)[0][3], 'update')        # ours: may be replaced
        deploy.loose_remove(self.cfg)
        self.assertFalse(os.path.exists(dest))
        self.assertFalse(os.path.exists(os.path.join(self.data, 'Creature')))   # emptied folders pruned
        self.assertTrue(os.path.isdir(self.data))

    def test_loose_never_overwrites_foreign_files(self):
        dest = os.path.join(self.data, 'Creature', 'Boar', 'BoarSkinIvory.blp')
        os.makedirs(os.path.dirname(dest))
        with open(dest, 'wb') as f:
            f.write(b'somebody else')
        with self.assertRaises(deploy.DeployError):
            deploy.loose_install(self.cfg)
        deploy.loose_install(self.cfg, force=True)                                # backed up, then replaced
        deploy.loose_remove(self.cfg)
        with open(dest, 'rb') as f:
            self.assertEqual(f.read(), b'somebody else')                          # restored

    def test_pack_and_install(self):
        s = deploy.pack(self.cfg)
        self.assertTrue(s['verified'])
        self.assertNotIn('installed', s)
        self.assertFalse(os.listdir(self.data))                                   # nothing installed without --install
        s = deploy.pack(self.cfg, install=True)
        dest = os.path.join(self.data, 'patch-3.MPQ')
        self.assertEqual(s['installed'], dest)
        with MPQArchive(dest) as a:
            self.assertEqual(a.read(BOAR), client().read(BOAR))
        s = deploy.pack(self.cfg, install=True)                                   # again: the old one is backed up
        self.assertTrue(os.path.exists(s['backup']))
        self.assertTrue(s['backup'].startswith(self.cfg.backup_dir))
        for bad in ('patch-2.MPQ', 'patch.MPQ', 'model.MPQ', 'patch-10.MPQ'):
            with self.assertRaises(deploy.DeployError):
                deploy.pack(self.cfg, install=True, name=bad)

    def test_staging_checks(self):
        p = os.path.join(self.cfg.staging_dir, 'Creature', 'Boar', 'Broken.blp')
        with open(p, 'wb') as f:
            f.write(b'BLP2' + bytes(2000))
        with self.assertRaises(deploy.DeployError):
            deploy.pack(self.cfg)


if __name__ == '__main__':
    unittest.main()
