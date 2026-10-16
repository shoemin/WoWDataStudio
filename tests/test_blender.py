"""The Blender add-on, headless: export the boar, the orc male and a palm tree, import them in Blender 4.5 with the
add-on (blender_addon/tests/blender_import_test.py) and check what ends up in the scene.

Skipped when Blender is not installed. Blender is the Microsoft Store build, started through its execution alias
%LOCALAPPDATA%\\Microsoft\\WindowsApps\\blender-launcher.exe (or set WOWDATA_BLENDER to another blender.exe).
"""
import json
import os
import shutil
import subprocess
import unittest

from common import CFG, ROOT, client, needs_client, tempdir

from wowdata import gltf
from wowdata.modelctx import resolve_model
from wowdata.resolve import Catalog

BLENDER = os.environ.get('WOWDATA_BLENDER') or os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Microsoft', 'WindowsApps', 'blender-launcher.exe')
SCRIPT = os.path.join(ROOT, 'blender_addon', 'tests', 'blender_import_test.py')
MODELS = {'boar': 'display:503', 'orc': 'race:orc', 'tree': 'World\\Kalimdor\\Durotar\\PassiveDoodads\\Trees\\DurotarPalm01.m2'}


def run_blender(report, files, timeout=600):
    cmd = [BLENDER, '-b', '--factory-startup', '--python', SCRIPT, '--', report] + files
    subprocess.run(cmd, capture_output=True, timeout=timeout)
    with open(report, 'r', encoding='utf-8') as f:
        return json.load(f)


@needs_client
@unittest.skipUnless(os.path.exists(BLENDER), 'Blender not found (%s)' % BLENDER)
class TestBlenderAddon(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.out = tempdir()
        cat = Catalog(client())
        cls.exports = {k: gltf.export(client(), resolve_model(t, cat, CFG), cls.out) for k, t in MODELS.items()}
        report = os.path.join(cls.out, 'blender_report.json')
        cls.report = run_blender(report, [cls.exports[k]['gltf'] for k in MODELS])
        cls.models = dict(zip(MODELS, cls.report['models']))

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.out, ignore_errors=True)

    def scene(self, key):
        m = self.models[key]
        self.assertTrue(m['ok'], m.get('error'))
        arm = [o for o in m['objects'] if o['type'] == 'ARMATURE']
        meshes = [o for o in m['objects'] if o['type'] == 'MESH']
        return m, arm, meshes

    def test_zip_installs_and_enables(self):
        import sys
        sys.path.insert(0, os.path.join(ROOT, 'blender_addon'))
        import build_zip
        scripts = os.path.join(self.out, 'blender_user_scripts')
        os.makedirs(scripts)
        z = build_zip.build(os.path.join(self.out, 'wowdata_blender.zip'))
        res = os.path.join(self.out, 'install.json')
        env = dict(os.environ, BLENDER_USER_SCRIPTS=scripts)     # keeps the user's own Blender setup untouched
        subprocess.run([BLENDER, '-b', '--factory-startup', '--python',
                        os.path.join(ROOT, 'blender_addon', 'tests', 'blender_install_test.py'), '--', res, z],
                       capture_output=True, timeout=300, env=env)
        with open(res, encoding='utf-8') as f:
            r = json.load(f)
        self.assertEqual((r['install'], r['enable']), (['FINISHED'], ['FINISHED']))
        self.assertTrue(r['module_file'].startswith(scripts))
        self.assertEqual(r['operators'], ['import_gltf', 'import_target'])

    def test_versions(self):
        self.assertTrue(self.report['blender'].startswith('4.5'), self.report['blender'])

    def check_model(self, key, bones, actions, visible_meshes):
        m, arm, meshes = self.scene(key)
        rep = self.exports[key]
        self.assertEqual(len(arm), 1)
        self.assertEqual(arm[0]['bones'], bones)
        self.assertEqual(arm[0]['bone_props'], bones)          # sidecar bone data stored on every bone
        self.assertTrue(arm[0]['has_sidecar_prop'])
        self.assertEqual(len(m['actions']), actions)
        self.assertTrue(all(a['wow_sequence'] is not None for a in m['actions']))
        geo = [o for o in meshes if o['geoset'] is not None]
        self.assertEqual(len(geo), len(rep['meshes']))
        self.assertEqual(sum(o['vertices'] for o in geo), sum(x['vertices'] for x in rep['meshes']))
        self.assertTrue(all(o['armature_modifier'] for o in geo))
        self.assertEqual(sum(1 for o in geo if not o['hidden']), visible_meshes)
        self.assertTrue(all(x['fixed'] for x in m['materials'] if x['wow_blend'] is not None))

    def test_boar(self):
        self.check_model('boar', 57, 13, 2)

    def test_orc_male(self):
        self.check_model('orc', 128, 134, 6)

    def test_tree(self):
        self.check_model('tree', 13, 1, 4)
        m, arm, meshes = self.scene('tree')
        col = [o for o in meshes if o['name'].startswith('COL_')]
        self.assertEqual(len(col), 1)
        self.assertTrue(col[0]['hidden'])


if __name__ == '__main__':
    unittest.main()
