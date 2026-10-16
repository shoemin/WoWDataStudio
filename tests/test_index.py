"""Search index and lookup chains (the examples of docs/MODEL_TOOL_RESEARCH.md section 2)."""
import os
import shutil
import unittest

from common import CFG, client, needs_client, tempdir

from wowdata import index, serverdb
from wowdata.resolve import Catalog
from wowdata.targets import resolve

HAVE_SERVER = serverdb.available(CFG)


@needs_client
class CatalogTests(unittest.TestCase):
    def test_creature_display_503(self):
        info = Catalog(client()).creature_display(503)
        self.assertEqual(info['model'], 'Creature\\Boar\\Boar.m2')
        roles = {r: (p, a) for r, p, a in info['files']}
        self.assertEqual(roles['model'], ('Creature\\Boar\\Boar.m2', 'model.MPQ'))
        self.assertEqual(roles['skin1'], ('Creature\\Boar\\BoarSkinIvory.blp', 'texture.MPQ'))

    def test_boar_variations(self):
        var = Catalog(client()).model_variations('Creature\\Boar\\Boar.mdx')
        self.assertEqual(len(var), 27)
        self.assertEqual(len({s for _, _, sk in var for s in sk}), 9)

    def test_item_and_gameobject(self):
        cat = Catalog(client())
        files = {p for _, p, _ in cat.item_display(22291)['files']}
        self.assertIn('Item\\ObjectComponents\\Weapon\\Axe_2H_Battle_A_01.m2', files)
        self.assertIn('Item\\ObjectComponents\\Weapon\\Axe_2H_Battle_A_01Green.blp', files)
        self.assertIn('Interface\\Icons\\INV_Axe_09.blp', files)
        files = {p for _, p, _ in cat.item_display(9995)['files']}
        self.assertIn('Item\\TextureComponents\\TorsoUpperTexture\\Leather_Horde_A_02Brown_Chest_TU_M.blp', files)
        self.assertIn('Item\\TextureComponents\\TorsoUpperTexture\\Leather_Horde_A_02Brown_Chest_TU_F.blp', files)
        go = cat.gameobject_display(3813)
        self.assertTrue(go['model'].upper().endswith('MARAUDONSPORETREE.M2'))

    def test_targets(self):
        cat = Catalog(client())
        paths = [p for _, p, _ in resolve('display:503', cat, CFG)]
        self.assertEqual(paths, ['Creature\\Boar\\Boar.m2', 'Creature\\Boar\\BoarSkinIvory.blp'])
        paths = [p for _, p, _ in resolve('Creature\\Boar\\Boar.mdx', cat, CFG)]
        self.assertEqual(paths[0], 'Creature\\Boar\\Boar.m2')
        self.assertEqual(len([p for p in paths if p.endswith('.blp')]), 9)     # the 9 skins
        orc = resolve('race:orc:male', cat, CFG)
        self.assertEqual(orc[0][1], 'Character\\Orc\\Male\\OrcMale.m2')
        self.assertIn('Character\\Orc\\Male\\OrcMaleSkin00_00.blp', [p for _, p, _ in orc])

    @unittest.skipUnless(HAVE_SERVER, 'server database not reachable')
    def test_creature_target(self):
        paths = [p for _, p, _ in resolve('creature:3098', Catalog(client()), CFG)]
        self.assertEqual(paths, ['Creature\\Boar\\Boar.m2', 'Creature\\Boar\\BoarSkinIvory.blp'])


@needs_client
class IndexTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempdir()
        cls.path = os.path.join(cls.dir, 'index.sqlite')
        cls.counts = index.build(client(), cls.path, CFG, use_server=HAVE_SERVER, log=lambda *a: None)
        cls.ix = index.Index(cls.path)

    @classmethod
    def tearDownClass(cls):
        cls.ix.db.close()
        shutil.rmtree(cls.dir, ignore_errors=True)

    def test_counts(self):
        self.assertEqual(self.counts['creature_display'], 10534)
        self.assertEqual(self.counts['gameobject_display'], 1638)
        self.assertEqual(self.counts['item_display'], 29604)
        self.assertEqual(self.counts['files'], 79733)        # 85,441 listed names minus 5,708 deleted

    @unittest.skipUnless(HAVE_SERVER, 'server database not reachable')
    def test_find_boar(self):
        res = self.ix.find('mottled boar', ['creature'])
        rows = {r['entry']: r for r in res['creature']['rows']}
        self.assertEqual(set(rows), {3098, 3099, 3100, 3225})
        b = rows[3098]
        self.assertEqual(b['spawns'][0]['map'], 1)
        d = b['displays'][0]
        self.assertEqual(d['id'], 503)
        self.assertEqual(d['model'], 'Creature\\Boar\\Boar.m2')
        files = {f['role']: (f['path'], f['archive']) for f in d['files']}
        self.assertEqual(files['skin1'], ('Creature\\Boar\\BoarSkinIvory.blp', 'texture.MPQ'))
        self.assertEqual(sum(len(v['displays']) for v in d['variations']), 27)

    @unittest.skipUnless(HAVE_SERVER, 'server database not reachable')
    def test_find_items_and_objects(self):
        res = self.ix.find('worn battleaxe', ['item'])
        self.assertIn(12282, [r['entry'] for r in res['item']['rows']])
        res = self.ix.find('spore tree', ['gameobject'])
        self.assertIn(178561, [r['entry'] for r in res['gameobject']['rows']])

    def test_find_race_and_paths(self):
        res = self.ix.find('orc male', ['race'])
        r = res['race']['rows'][0]
        self.assertEqual((r['race'], r['sex'], r['section_rows']), (2, 'male', 248))
        self.assertEqual(r['display']['model'], 'Character\\Orc\\Male\\OrcMale.m2')
        res = self.ix.find('durotarpalm', ['file'])
        self.assertEqual(len([f for f in res['file']['rows'] if f['path'].lower().endswith('.m2')]), 3)


if __name__ == '__main__':
    unittest.main()
