"""Headless terrain round trip through Blender for WoW Data Studio's tests (and by hand):

    blender -b --factory-startup --python studio_terrain.py -- REPORT.json wowdata_terrain.json [EDIT.json]

Empty scene, register the add-on from this folder, import the terrain package like a user would (File > Import > WoW
1.12 terrain), optionally apply a scripted edit, then export with "Export terrain for WoW Data Studio" into the
package's blender_export folder. EDIT.json:
  {"raise_edge": {"tile": "Tile 32_48", "edge": "E", "rows": [40, 72], "height": 6.0, "width": 4},
   "sync": true,                                    (run "Sync shared edges" after the edit)
   "move_xy": {"tile": "Tile 32_48", "count": 25, "offset": [1.5, -0.7]},
   "add_vertex": {"tile": "Tile 32_48"}}
raise_edge raises the vertices within `width` grid steps of the edge, `height` yards at the edge falling to 0 inwards,
for the rows (or columns) in the range (outer grid units).
"""
import json
import os
import sys
import traceback

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import wowdata_blender  # noqa: E402
from wowdata_blender import terrain  # noqa: E402

argv = sys.argv[sys.argv.index('--') + 1:]
report_path, manifest = argv[0], argv[1]
edit = {}
if len(argv) > 2 and argv[2]:
    with open(argv[2], 'r', encoding='utf-8') as f:
        edit = json.load(f)
out = {'blender': bpy.app.version_string, 'ok': False}


def tile_object(name):
    return next(o for o in bpy.data.objects if o.get('wow_tile_id') == name)


try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        wowdata_blender.register()
    except ValueError:
        pass
    out['import'] = terrain.import_wow_terrain(bpy.context, manifest)
    out['labels'] = sorted(o.name for o in bpy.data.objects if 'wow_terrain_label' in o)
    out['collections'] = sorted(c.name for c in bpy.data.collections)
    out['materials'] = {m.name: len(m.node_tree.nodes) for m in bpy.data.materials if m.get('wow_terrain_material')}
    out['seams_at_import'] = terrain.check_seams(bpy.context)

    unit = 1600.0 / 3.0 / 128.0
    re = edit.get('raise_edge')
    if re:
        o = tile_object(re['tile'])
        me = o.data
        vid = me.attributes['wow_vid'].data
        lo, hi = re['rows']
        width, height = re.get('width', 4), re['height']
        moved = 0
        for v in me.vertices:
            gid = vid[v.index].value - 1
            if gid < 129 * 129:
                r, c = divmod(gid, 129)
                fr, fc = float(r), float(c)
            else:
                r, c = divmod(gid - 129 * 129, 128)
                fr, fc = r + 0.5, c + 0.5
            along, depth = {'E': (fr, 128 - fc), 'W': (fr, fc), 'N': (fc, fr), 'S': (fc, 128 - fr)}[re['edge']]
            if lo <= along <= hi and depth <= width:
                v.co.z += height * (1.0 - depth / (width + 1.0))
                moved += 1
        me.update()
        out['raised_vertices'] = moved
        out['seams_after_edit'] = terrain.check_seams(bpy.context)
    if edit.get('sync'):
        out['sync'] = terrain.sync_edges(bpy.context)
        out['seams_after_sync'] = terrain.check_seams(bpy.context)
    mv = edit.get('move_xy')
    if mv:
        o = tile_object(mv['tile'])
        n = 0
        for v in o.data.vertices[1000:1000 + mv['count']]:
            v.co.x += mv['offset'][0]
            v.co.y += mv['offset'][1]
            n += 1
        o.data.update()
        out['moved_xy'] = n
    if edit.get('add_vertex'):
        import bmesh
        o = tile_object(edit['add_vertex']['tile'])
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.verts.new((10.0, -10.0, 50.0))
        bm.to_mesh(o.data)
        bm.free()
        out['added_vertex'] = True
    out['export'] = terrain.export_terrain(bpy.context, terrain.default_export_path(bpy.context))
    out['ok'] = True
except Exception:  # noqa: BLE001
    out['error'] = traceback.format_exc()
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1)
