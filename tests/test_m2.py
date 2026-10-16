"""M2 reader / writer / validator on the real client models.

The round trip (parse -> serialize -> compare bytes) proves the record layouts and the file layout rule
(header-order, depth-first, 16-byte aligned blocks) for every model; the edit tests prove serialize() really
rebuilds offsets from the structure instead of copying bytes.
Set WOWDATA_QUICK=1 to check every 10th model instead of all 9,691.
"""
import os
import unittest

import numpy as np

from common import client, needs_client

from wowdata.m2 import M2, M2Error
from wowdata.m2.anim import animation_names, evaluate, sequence_keys
from wowdata.m2.validate import validate

NAMED = {
    'boar': 'Creature\\Boar\\Boar.m2',
    'orc male': 'Character\\Orc\\Male\\OrcMale.m2',
    'tree': 'World\\Kalimdor\\Durotar\\PassiveDoodads\\Trees\\DurotarPalm01.m2',
    'item': 'Item\\ObjectComponents\\Weapon\\Axe_2H_Battle_A_01.m2',
    'spell (particles)': 'Spells\\FireBall_Missile_High.m2',
    'spell (ribbons)': 'Spells\\AbolishMagic_Base.m2',
    'v257': 'Creature\\Cupid\\Cupid.m2',
    'camera': 'Cameras\\FlyByDwarf.m2',
}


def _load(path):
    data = client().read(path)
    assert data is not None, path
    return data, M2.parse(data)


@needs_client
class TestNamedModels(unittest.TestCase):
    def test_round_trip_and_validation(self):
        for label, path in NAMED.items():
            with self.subTest(label):
                data, m = _load(path)
                self.assertEqual(m.serialize(), data, '%s does not re-serialise byte-identically' % path)
                covered, stray = m.coverage()
                self.assertEqual(stray, 0, 'non-zero bytes outside every known block')
                errs = [i for i in validate(m) if i.level == 'error']
                self.assertEqual(errs, [])

    def test_boar_contents(self):
        _, m = _load(NAMED['boar'])
        self.assertEqual(m.version, 0x100)
        self.assertEqual((len(m.sequences), len(m.bones), len(m.vertices)), (13, 57, 401))
        self.assertEqual([int(v.bone_limit) for v in m.views], [256, 75, 53, 21])
        self.assertEqual(len(m.textures), 1)
        self.assertEqual(int(m.textures['type'][0]), 11)        # creature skin 1
        self.assertEqual((len(m.attachments), len(m.events), len(m.cameras)), (9, 18, 2))
        self.assertEqual(len(m.collision_triangles) // 3, 12)
        # bone influences per vertex as in the research: 1 = 190, 2 = 170, 3 = 35, 4 = 6
        n = (m.vertices['weights'] > 0).sum(1)
        self.assertEqual([int((n == k).sum()) for k in (1, 2, 3, 4)], [190, 170, 35, 6])
        self.assertTrue((m.vertices['weights'].astype(int).sum(1) == 255).all())
        # every rotation is a unit quaternion (16-byte floats in 1.12, not compressed)
        for b in m.bones:
            q = b.rotation.values
            if len(q):
                self.assertTrue(np.allclose(np.linalg.norm(q, axis=1), 1, atol=1e-3))
        names = animation_names(client())
        self.assertEqual(names[int(m.sequences['anim_id'][0])], 'Run')
        self.assertIn('Stand', [names[int(a)] for a in m.sequences['anim_id']])

    def test_orc_contents(self):
        _, m = _load(NAMED['orc male'])
        self.assertEqual((len(m.bones), len(m.vertices), len(m.sequences), len(m.attachments)), (128, 2728, 134, 34))
        self.assertEqual(sorted(int(t) for t in m.textures['type']), [1, 2, 6])
        geosets = sorted({int(g) for g in m.views[0].submeshes['geoset_id']})
        self.assertEqual(len(geosets), 41)
        self.assertIn(1501, geosets)

    def test_particles_and_ribbons_parse(self):
        _, p = _load(NAMED['spell (particles)'])
        self.assertGreater(len(p.particles), 0)
        for e in p.particles:
            self.assertIn(int(e.emitter_type), (1, 2, 3))
            self.assertEqual(len(e.emission_rate.values), len(e.emission_rate.timestamps))
        _, r = _load(NAMED['spell (ribbons)'])
        self.assertGreater(len(r.ribbons), 0)
        self.assertGreater(len(r.ribbons[0].textures), 0)

    def test_version_257(self):
        data, m = _load(NAMED['v257'])
        self.assertEqual(m.version, 0x101)
        self.assertEqual(len(m.texture_combiner_combos), 5)
        self.assertEqual(m.serialize(), data)

    def test_raw_blocks_kept(self):
        data, m = _load(NAMED['boar'])
        b = m.block_bytes('bones[15].rotation.values')
        q = m.bones[15].rotation.values
        self.assertEqual(b, q.astype('<f4').tobytes())
        self.assertEqual(m.bones.offset, int.from_bytes(data[0x38:0x3c], 'little'))


@needs_client
class TestEdits(unittest.TestCase):
    def test_edit_rebuilds_offsets(self):
        data, m = _load(NAMED['boar'])
        # lengthen one track and the vertex list: every later block moves
        t = m.bones[0].translation
        t.set(timestamps=np.append(t.timestamps, 61000).astype(np.uint32),
              values=np.vstack([t.values, [[0, 0, 0.5]]]).astype(np.float32))
        m.vertices = np.concatenate([m.vertices, m.vertices[:3]])
        m.name = b'BoarEdit\0'
        out = m.serialize()
        self.assertNotEqual(out, data)
        m2 = M2.parse(out)
        self.assertEqual(len(m2.vertices), 404)
        self.assertEqual(m2.model_name, 'BoarEdit')
        self.assertEqual(int(m2.bones[0].translation.timestamps[-1]), 61000)
        self.assertTrue(np.allclose(m2.bones[0].translation.values[-1], [0, 0, 0.5]))
        for path, ofs, n in m2.blocks:
            if path != 'header':
                self.assertEqual(ofs % 16, 0, path)
        self.assertEqual(m2.serialize(), out)
        self.assertEqual([i for i in validate(m2) if i.level == 'error'], [])

    def test_validator_catches_bad_indices(self):
        _, m = _load(NAMED['boar'])
        m.views[0].triangles[0] = 60000
        m.bones.flat['parent'][3] = 999
        m.views[0].texture_units.flat['render_flags'][0] = 50
        msgs = [str(i) for i in validate(m) if i.level == 'error']
        self.assertTrue(any('views[0].triangles' in s for s in msgs))
        self.assertTrue(any('bones[3]' in s and 'parent' in s for s in msgs))
        self.assertTrue(any('render flags 50' in s for s in msgs))

    def test_truncated_file_is_rejected(self):
        data, _ = _load(NAMED['boar'])
        with self.assertRaises(M2Error):
            M2.parse(data[:len(data) // 2])
        with self.assertRaises(M2Error):
            M2.parse(b'MD20' + (0x200).to_bytes(4, 'little') + data[8:])


@needs_client
class TestAnimation(unittest.TestCase):
    def test_keys_outside_the_window_are_interpolated(self):
        _, m = _load(NAMED['boar'])
        t = m.bones[0].translation          # sequence 3 uses keys 4..5 at 10000 and 38033 ms
        s = m.sequences
        a, b = (int(x) for x in t.ranges[3])
        self.assertEqual((int(t.timestamps[a]), int(t.timestamps[b])), (10000, 38033))
        times, vals = sequence_keys(t, 3, int(s['start'][3]), int(s['end'][3]), m.global_sequences)
        self.assertEqual(times[0], 0)
        self.assertEqual(times[-1], int(s['end'][3]) - int(s['start'][3]))
        ref = evaluate(t.timestamps[a:b + 1], t.values[a:b + 1], int(s['start'][3]), 1)
        self.assertTrue(np.allclose(vals[0], ref))

    def test_global_sequence_tracks_unroll(self):
        _, m = _load(NAMED['orc male'])      # 2 global sequences, 20 bone tracks on them
        found = False
        for bone in m.bones:
            for tr in bone.tracks():
                if tr.global_sequence != -1 and len(tr):
                    k = sequence_keys(tr, 0, int(m.sequences['start'][0]), int(m.sequences['end'][0]), m.global_sequences,
                                      quaternion=tr.name == 'rotation')
                    self.assertIsNotNone(k)
                    self.assertEqual(k[0][0], 0)
                    found = True
        self.assertTrue(found, 'OrcMale has global-sequence bone tracks')


@needs_client
class TestAllModels(unittest.TestCase):
    def test_every_model_parses_round_trips_and_validates(self):
        c = client()
        names = sorted(v[0] for k, v in c.names().items() if k.endswith('.M2'))
        if os.environ.get('WOWDATA_QUICK'):
            names = names[::10]
        self.assertGreater(len(names), 900)
        failures, different, errors = [], [], []
        for p in names:
            data = c.read(p)
            try:
                m = M2.parse(data)
            except M2Error as e:
                failures.append('%s: %s' % (p, e))
                continue
            if m.serialize() != data:
                different.append(p)
            errors += ['%s %r' % (p, i) for i in validate(m) if i.level == 'error']
        self.assertEqual(failures, [])
        self.assertEqual(different, [])
        self.assertEqual(errors, [])


if __name__ == '__main__':
    unittest.main()
