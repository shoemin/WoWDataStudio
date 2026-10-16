"""Headless terrain edit for the in-game visual test (WoWDataTool\\tests\\ingame), modelled on blender_addon\\tests\\studio_terrain.py:

    blender -b --factory-startup --python ingame_terrain_edit.py -- ADDON_ROOT REPORT.json wowdata_terrain.json EDIT.json

Imports the terrain package like a user (File > Import > WoW 1.12 terrain), raises a smooth mound centred on a point given in PACKAGE
coordinates (Blender X east / Y north, the centre tile's north-west corner at the origin), on every tile object it touches, then runs the
add-on's "Sync shared edges" and exports ("Export terrain for WoW Data Studio") into the package's blender_export folder.
EDIT.json: {"center": [x, y], "radius": 18.0, "height": 22.0,
            "skip_edge": {"tile": "Tile 40_33", "edge": "W"}}   leave that tile's edge row un-raised (a sculpt that missed the neighbour's
                                                                 edge, the usual cause of a seam), so "Sync shared edges" has to close it
"""
import json
import math
import sys
import traceback

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
addon_root, report_path, manifest, edit_path = argv[0], argv[1], argv[2], argv[3]
sys.path.insert(0, addon_root)
import wowdata_blender  # noqa: E402
from wowdata_blender import terrain  # noqa: E402

with open(edit_path, 'r', encoding='utf-8') as f:
    edit = json.load(f)
out = {'blender': bpy.app.version_string, 'ok': False}
try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        wowdata_blender.register()
    except ValueError:
        pass
    out['import'] = terrain.import_wow_terrain(bpy.context, manifest)
    out['seams_at_import'] = terrain.check_seams(bpy.context)
    bpy.context.view_layer.update()        # matrix_world of the freshly placed tile objects is only valid after a depsgraph update
    out['tile_locations'] = {o.get('wow_tile_id'): [round(c, 2) for c in o.matrix_world.translation] for o in bpy.data.objects if o.get('wow_tile_id')}
    cx, cy = edit['center']
    radius, height = edit['radius'], edit['height']
    skip = edit.get('skip_edge') or {}
    raised = {}
    nearest = None
    for o in [o for o in bpy.data.objects if o.get('wow_tile_id')]:
        me = o.data
        vid = me.attributes['wow_vid'].data
        mw, inv = o.matrix_world, o.matrix_world.inverted()
        n = 0
        for v in me.vertices:
            p = mw @ v.co
            d = math.hypot(p.x - cx, p.y - cy)
            if nearest is None or d < nearest[0]:
                nearest = (d, o.get('wow_tile_id'), round(p.z, 2))
            if d >= radius:
                continue
            gid = vid[v.index].value - 1
            if o.get('wow_tile_id') == skip.get('tile') and 0 <= gid < 129 * 129:
                r, c = divmod(gid, 129)
                if {'W': c == 0, 'E': c == 128, 'N': r == 0, 'S': r == 128}[skip['edge']]:
                    continue
            k = math.cos(d / radius * math.pi / 2)
            v.co = inv @ (p + Vector((0, 0, height * k * k)))
            n += 1
        me.update()
        if n:
            raised[o.get('wow_tile_id')] = n
    out['raised_vertices'] = raised
    out['ground_at_center'] = nearest
    out['seams_after_edit'] = terrain.check_seams(bpy.context)
    out['sync'] = terrain.sync_edges(bpy.context)
    out['seams_after_sync'] = terrain.check_seams(bpy.context)
    out['export'] = terrain.export_terrain(bpy.context, terrain.default_export_path(bpy.context))
    out['ok'] = True
except Exception:  # noqa: BLE001
    out['error'] = traceback.format_exc()
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1, default=str)
