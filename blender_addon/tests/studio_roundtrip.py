"""Headless round trip through Blender for WoW Data Studio's tests (and by hand):

    blender -b --factory-startup --python studio_roundtrip.py -- REPORT.json IN.gltf OUT.gltf [EDIT.json]

Empty scene, register the add-on from this folder, import IN.gltf like a user would (File > Import > WoW 1.12 model),
optionally apply a scripted edit, then export with the add-on's "Export for WoW Data Studio" to OUT.gltf.
EDIT.json: {"move": [{"submesh": 1, "axis": 2, "above": 1.5, "offset": [0, 0, 0.15]}],
            "tint": [{"texture_id": "t00", "rect": [0.1, 0.1, 0.5, 0.3], "color": [1.0, 0.1, 0.1]}]}
(rect = x0, y0, x1, y1 as fractions of the image, top-left origin like the PNG).
"""
import json
import os
import sys
import traceback

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import wowdata_blender  # noqa: E402

argv = sys.argv[sys.argv.index('--') + 1:]
report_path, src, dst = argv[0], argv[1], argv[2]
edit = {}
if len(argv) > 3 and argv[3]:
    with open(argv[3], 'r', encoding='utf-8') as f:
        edit = json.load(f)
out = {'blender': bpy.app.version_string, 'ok': False}
try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        wowdata_blender.register()
    except ValueError:
        pass
    out['import'] = wowdata_blender.import_wow_gltf(bpy.context, src)
    moved = 0
    for mv in edit.get('move', []):
        for o in bpy.data.objects:
            if o.type == 'MESH' and o.get('wow_submesh') == mv['submesh']:
                for v in o.data.vertices:
                    if v.co[mv.get('axis', 2)] > mv.get('above', -1e9):
                        v.co.x += mv['offset'][0]
                        v.co.y += mv['offset'][1]
                        v.co.z += mv['offset'][2]
                        moved += 1
                o.data.update()
    out['moved_vertices'] = moved
    tinted = 0
    for t in edit.get('tint', []):
        for img in bpy.data.images:
            if img.get('wow_texture_id') != t['texture_id']:
                continue
            w, h = img.size
            px = list(img.pixels)
            x0, y0, x1, y1 = t['rect']
            for y in range(int(y0 * h), int(y1 * h)):
                row = h - 1 - y                     # Blender stores rows bottom-up
                for x in range(int(x0 * w), int(x1 * w)):
                    i = (row * w + x) * 4
                    if px[i + 3] <= 0.0:
                        continue
                    for c in range(3):
                        px[i + c] = px[i + c] * 0.4 + t['color'][c] * 0.6
                    tinted += 1
            img.pixels = px
            img.update()
    out['tinted_pixels'] = tinted
    out['export'] = wowdata_blender.export_for_studio(bpy.context, dst)
    out['ok'] = True
except Exception:  # noqa: BLE001
    out['error'] = traceback.format_exc()
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1)
