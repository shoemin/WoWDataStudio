"""Typed DBC layouts on the client's own tables (via the archives)."""
import unittest

from common import CFG, client, needs_client

from wowdata import dbc
from wowdata.dbc import SCHEMAS


@needs_client
class DBCTests(unittest.TestCase):
    def test_layouts_match_field_counts(self):
        c = client()
        for name, schema in SCHEMAS.items():
            t = dbc.load(c, name)
            width = sum(f.width for f in schema)
            self.assertLessEqual(width, t.field_count, name)
            if name != 'Map':          # Map keeps its tail generic on purpose
                self.assertEqual(width, t.field_count, name)
            self.assertGreater(len(t.rows()), 0)

    def test_boar_chain(self):
        c = client()
        cdi = dbc.load(c, 'CreatureDisplayInfo').get(503)
        self.assertEqual(cdi.model_id, 66)
        self.assertAlmostEqual(cdi.scale, 0.6, places=5)
        self.assertEqual(cdi.skin, ['BoarSkinIvory', '', ''])
        cmd = dbc.load(c, 'CreatureModelData').get(66)
        self.assertEqual(cmd.model_path, 'Creature\\Boar\\Boar.mdx')
        self.assertAlmostEqual(cmd.collision_width, 0.6944, places=4)
        self.assertAlmostEqual(cmd.collision_height, 2.083, places=3)

    def test_other_tables(self):
        c = client()
        go = dbc.load(c, 'GameObjectDisplayInfo').get(3813)
        self.assertTrue(go.model_path.upper().endswith('MARAUDONSPORETREE.MDX'))
        it = dbc.load(c, 'ItemDisplayInfo').get(22291)
        self.assertEqual(it.model[0], 'Axe_2H_Battle_A_01.mdx')
        self.assertEqual(it.model_texture[0], 'Axe_2H_Battle_A_01Green')
        self.assertEqual(it.icon, 'INV_Axe_09')
        it = dbc.load(c, 'ItemDisplayInfo').get(9995)
        self.assertEqual(it.texture[3], 'Leather_Horde_A_02Brown_Chest_TU')     # torso upper
        orc = dbc.load(c, 'ChrRaces').get(2)
        self.assertEqual((orc.name, orc.client_prefix, orc.male_display_id, orc.female_display_id), ('Orc', 'Or', 51, 52))
        secs = [r for r in dbc.load(c, 'CharSections') if r.race == 2 and r.sex == 0]
        self.assertEqual(len(secs), 248)
        skin = [r for r in secs if r.section == 0 and r.variation == 0 and r.color == 0][0]
        self.assertEqual(skin.texture[0], 'Character\\Orc\\Male\\OrcMaleSkin00_00.blp')
        ex = dbc.load(c, 'CreatureDisplayInfoExtra').get(364)
        self.assertEqual((ex.race, ex.sex, ex.skin, ex.face), (2, 0, 7, 7))
        self.assertEqual(ex.bake, 'aad9b05a73c9209ee0450f49b2148d3b.blp')
        self.assertEqual(dbc.load(c, 'AreaTable').get(14).name, 'Durotar')
        self.assertEqual(dbc.load(c, 'Map').get(1).directory, 'Kalimdor')

    def test_generic_reader(self):
        c = client()
        t = dbc.DBC(c.read('DBFilesClient\\Spell.dbc'), 'Spell')   # no layout: raw access
        self.assertIsNone(t.schema)
        self.assertGreater(t.count, 1000)
        self.assertEqual(len(t.raw_row(0)), t.field_count)
        self.assertEqual(len(dbc.guess_row(t, 0)), t.field_count)


if __name__ == '__main__':
    unittest.main()
