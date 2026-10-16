"""glTF exporter: file structure, textures, the sidecar, and the skinning maths.

The skinning test rebuilds every bone matrix two independent ways and compares them:
  * the client's way, straight from the M2 tracks: G = G_parent * T(pivot) T(t) R S T(-pivot)
    (values evaluated at the absolute time with the client's step / lerp rules);
  * the glTF way, from what the exporter wrote: J = J_parent * T(node t) R S, skin = J * inverseBindMatrix
    (values sampled from the exported animation accessors);
and checks skin == C G C^-1 with C the Z-up -> Y-up mapping.
"""
import json
import os
import shutil
import unittest

import numpy as np

from common import CFG, client, needs_client, tempdir

from wowdata import gltf, viewer
from wowdata.m2 import M2
from wowdata.m2.anim import evaluate
from wowdata.modelctx import resolve_model
from wowdata.resolve import Catalog

COMP = {5126: np.float32, 5121: np.uint8, 5123: np.uint16, 5125: np.uint32}
NCOMP = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


class GltfFile:
    def __init__(self, path):
        with open(path, 'r', encoding='utf-8') as f:
            self.g = json.load(f)
        with open(os.path.join(os.path.dirname(path), self.g['buffers'][0]['uri']), 'rb') as f:
            self.bin = f.read()
        self.dir = os.path.dirname(path)

    def acc(self, i):
        a = self.g['accessors'][i]
        bv = self.g['bufferViews'][a['bufferView']]
        n = NCOMP[a['type']]
        arr = np.frombuffer(self.bin, COMP[a['componentType']], a['count'] * n, bv['byteOffset'] + a.get('byteOffset', 0))
        return arr.reshape(a['count'], n) if n > 1 else arr


def quat_matrix(q):
    x, y, z, w = q / np.linalg.norm(q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def trs(t, q, s):
    m = np.eye(4)
    m[:3, :3] = quat_matrix(np.asarray(q, float)) * np.asarray(s, float)
    m[:3, 3] = t
    return m


def T(v):
    m = np.eye(4)
    m[:3, 3] = v
    return m


def m2_bone_matrices(m, seq, t_ms):
    """The client's bone matrices at time t_ms into sequence seq."""
    start = int(m.sequences['start'][seq])
    out = []
    for i, b in enumerate(m.bones):
        vals = {}
        for name, default in (('translation', np.zeros(3)), ('rotation', np.array([0, 0, 0, 1.0])), ('scale', np.ones(3))):
            tr = getattr(b, name)
            v = default
            if len(tr):
                r = tr.ranges
                a, z = (int(x) for x in r[seq]) if len(r) else (0, len(tr) - 1)
                if z >= a:
                    time = start + t_ms
                    if tr.global_sequence != -1:
                        time = t_ms % max(1, int(m.global_sequences[tr.global_sequence]))
                    v = evaluate(tr.timestamps[a:z + 1], tr.values[a:z + 1], time, tr.interpolation, name == 'rotation')
            vals[name] = v
        p = np.asarray(b.pivot, float)
        local = T(p) @ trs(vals['translation'], vals['rotation'], vals['scale']) @ T(-p)
        par = int(b.parent)
        out.append((out[par] if par >= 0 else np.eye(4)) @ local)
    return out


def sample(g, sampler, t):
    times = g.acc(sampler['input'])
    vals = g.acc(sampler['output']).astype(np.float64)
    return evaluate(times, vals, t, 0 if sampler['interpolation'] == 'STEP' else 1, vals.shape[1] == 4)


def gltf_skin_matrices(g, anim_index, t_s):
    nodes = g.g['nodes']
    skin = g.g['skins'][0]
    joints = skin['joints']
    ibm = g.acc(skin['inverseBindMatrices']).reshape(-1, 4, 4).transpose(0, 2, 1)
    local = {}
    for j in joints:
        n = nodes[j]
        local[j] = [np.array(n.get('translation', [0, 0, 0]), float), np.array(n.get('rotation', [0, 0, 0, 1]), float),
                    np.array(n.get('scale', [1, 1, 1]), float)]
    anim = g.g['animations'][anim_index]
    for ch in anim['channels']:
        k = {'translation': 0, 'rotation': 1, 'scale': 2}[ch['target']['path']]
        local[ch['target']['node']][k] = sample(g, anim['samplers'][ch['sampler']], t_s)
    parent = {}
    for i, n in enumerate(nodes):
        for c in n.get('children', []):
            parent[c] = i
    world = {}

    def w(j):
        if j not in world:
            mat = trs(*local[j])
            p = parent.get(j)
            world[j] = (w(p) if p in local else np.eye(4)) @ mat
        return world[j]
    return [w(j) @ ibm[k] for k, j in enumerate(joints)]


C4 = np.eye(4)
C4[:3, :3] = gltf.C


@needs_client
class TestGltfExport(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.out = tempdir()
        cls.cat = Catalog(client())
        cls.reports = {}
        for key, target in (('boar', 'display:503'), ('orc', 'race:orc'), ('tree', 'World\\Kalimdor\\Durotar\\PassiveDoodads\\Trees\\DurotarPalm01.m2'),
                            ('axe', 'itemdisplay:22291')):
            ctx = resolve_model(target, cls.cat, CFG)
            cls.reports[key] = gltf.export(client(), ctx, cls.out, glb=(key == 'boar'))

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.out, ignore_errors=True)

    def test_files_and_structure(self):
        for key, rep in self.reports.items():
            with self.subTest(key):
                g = GltfFile(rep['gltf'])
                self.assertEqual(g.g['asset']['version'], '2.0')
                self.assertEqual(len(g.bin), g.g['buffers'][0]['byteLength'])
                for bv in g.g['bufferViews']:
                    self.assertLessEqual(bv['byteOffset'] + bv['byteLength'], len(g.bin))
                for im in g.g['images']:
                    self.assertTrue(os.path.exists(os.path.join(g.dir, im['uri'])), im['uri'])
                for mesh in g.g['meshes']:
                    for prim in mesh['primitives']:
                        pos = g.acc(prim['attributes']['POSITION'])
                        idx = g.acc(prim['indices'])
                        self.assertLess(int(idx.max()), len(pos))
                        if 'WEIGHTS_0' in prim['attributes']:
                            w = g.acc(prim['attributes']['WEIGHTS_0']).astype(int)
                            self.assertTrue((w.sum(1) == 255).all())
                            j = g.acc(prim['attributes']['JOINTS_0'])
                            self.assertLess(int(j.max()), len(g.g['skins'][0]['joints']))
                for a in g.g.get('animations', []):
                    for s in a['samplers']:
                        t = g.acc(s['input'])
                        self.assertTrue((np.diff(t) > 0).all(), a['name'])
                self.assertTrue(os.path.exists(rep['sidecar']))
                self.assertEqual(rep['errors'], [])

    def test_boar(self):
        rep = self.reports['boar']
        g = GltfFile(rep['gltf'])
        self.assertEqual(len(g.g['skins'][0]['joints']), 57)
        names = [a['name'] for a in g.g['animations']]
        self.assertEqual(len(names), 13)
        for n in ('Stand', 'Walk', 'Run', 'Death', 'AttackUnarmed'):
            self.assertIn(n, names)
        self.assertEqual(len(set(names)), 13)
        self.assertEqual([t['resolved'] for t in rep['textures']], ['Creature\\Boar\\BoarSkinIvory.blp'])
        with open(rep['sidecar'], encoding='utf-8') as f:
            side = json.load(f)
        self.assertEqual(side['format'], 'wowdata-m2-sidecar')
        self.assertEqual(len(side['m2']['bones']), 57)
        self.assertIn('flags', side['m2']['bones'][0])
        self.assertEqual(len(side['m2']['sequences']), 13)
        self.assertIn('texture_units', side['m2']['views'][0])
        self.assertEqual(side['source']['archive_path'], 'Creature\\Boar\\Boar.m2')
        self.assertEqual(len(side['source']['sha256']), 64)
        self.assertEqual(rep['glb'][:4], b'glTF')

    def test_orc_character_defaults(self):
        rep = self.reports['orc']
        vis = sorted(m['geoset'] for m in rep['meshes'] if m['visible'])
        self.assertEqual(vis, [0, 401, 501, 702, 1301, 1501])
        body = [t for t in rep['textures'] if t['type'] == 1][0]
        self.assertTrue(body['resolved'].startswith('composite of OrcMaleSkin00_00.blp'))
        self.assertEqual(body['size'], [256, 256])
        self.assertEqual(rep['animations'], 134)

    def test_vertices_match_the_m2(self):
        rep = self.reports['boar']
        g = GltfFile(rep['gltf'])
        m = M2.parse(client().read('Creature\\Boar\\Boar.m2'))
        for mesh_rep, mesh in zip(rep['meshes'], g.g['meshes']):
            pos = g.acc(mesh['primitives'][0]['attributes']['POSITION'])
            want = m.vertices['position'][mesh_rep['model_vertices']] @ gltf.C.T
            self.assertTrue(np.allclose(pos, want, atol=1e-6))
            uv = g.acc(mesh['primitives'][0]['attributes']['TEXCOORD_0'])
            self.assertTrue(np.array_equal(uv, m.vertices['uv'][mesh_rep['model_vertices']]))

    def _check_skinning(self, key, path, seq_names, max_err=2e-3):
        rep = self.reports[key]
        g = GltfFile(rep['gltf'])
        m = M2.parse(client().read(path))
        anims = {a['name']: i for i, a in enumerate(g.g['animations'])}
        for name in seq_names:
            ai = anims[name]
            seq = g.g['animations'][ai]['extras']['wow_sequence']
            length = int(m.sequences['end'][seq]) - int(m.sequences['start'][seq])
            for frac in (0.0, 0.37, 0.81):
                t = length * frac
                ref = m2_bone_matrices(m, seq, t)
                got = gltf_skin_matrices(g, ai, t / 1000.0)
                for i, (a, b) in enumerate(zip(ref, got)):
                    err = np.abs(C4 @ a @ C4.T - b).max()
                    self.assertLess(err, max_err, '%s %s t=%.0f bone %d' % (key, name, t, i))

    def test_skinning_matches_the_client_formula(self):
        self._check_skinning('boar', 'Creature\\Boar\\Boar.m2', ['Stand', 'Walk', 'Run', 'Death', 'AttackUnarmed'])
        self._check_skinning('orc', 'Character\\Orc\\Male\\OrcMale.m2', ['Stand', 'Walk', 'Attack1H'])
        self._check_skinning('tree', 'World\\Kalimdor\\Durotar\\PassiveDoodads\\Trees\\DurotarPalm01.m2', ['Stand'])

    def test_viewer_page(self):
        rep = self.reports['boar']
        page = os.path.join(self.out, 'boar.html')
        viewer.write_html(page, gltf.glb_base64(rep['glb']), 'Boar', 'test')
        with open(page, encoding='utf-8') as f:
            text = f.read()
        self.assertIn('three@%s/build/three.module.js' % viewer.THREE_VERSION, text)
        self.assertIn(gltf.glb_base64(rep['glb'])[:100], text)
        self.assertNotIn('__GLB__', text)


if __name__ == '__main__':
    unittest.main()
