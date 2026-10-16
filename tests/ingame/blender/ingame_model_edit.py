"""Headless Blender edit for the in-game visual test (WoWDataTool\\tests\\ingame), modelled on blender_addon\\tests\\studio_roundtrip.py:

    blender -b --factory-startup --python ingame_model_edit.py -- ADDON_ROOT REPORT.json IN.gltf OUT.gltf EDIT.json

Empty scene, register the add-on from ADDON_ROOT, import IN.gltf like a user would (File > Import > WoW 1.12 model), apply the scripted
edit, export with the add-on's "Export for WoW Data Studio" to OUT.gltf (skipped with "report_only").
EDIT.json keys (all optional):
  "report_only": true                       only list the vertex groups (bones) with their vertex counts, mean position and bone head
  "scale_all": {"factor": 4.0}              scale every mesh vertex about the model origin (static doodads)
  "scale_groups": {"groups": ["bone_012", ...], "factor": 3.0, "pivot_of": {"bone_013": "bone_012"}}
                                            scale the vertices whose strongest vertex group is one of these about THAT bone's head (pivot), so the part
                                            stays attached at its joint and animates rigidly with its bone
  "tint": [{"texture_id": "t00", "rect": [0, 0, 1, 1], "color": [1, 0, 1]}]   as studio_roundtrip.py (0.4 x original + 0.6 x colour)
"""
import json
import sys
import traceback

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
addon_root, report_path, src, dst, edit_path = argv[0], argv[1], argv[2], argv[3], argv[4]
sys.path.insert(0, addon_root)
import wowdata_blender  # noqa: E402

with open(edit_path, 'r', encoding='utf-8') as f:
    edit = json.load(f)
out = {'blender': bpy.app.version_string, 'ok': False}


def meshes():
    return [o for o in bpy.data.objects if o.type == 'MESH' and o.get('wow_submesh') is not None]


def armature():
    return next((o for o in bpy.data.objects if o.type == 'ARMATURE'), None)


def strongest_group(obj, v):
    best, w = None, -1.0
    for g in v.groups:
        if g.weight > w:
            best, w = obj.vertex_groups[g.group].name, g.weight
    return best, w


try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        wowdata_blender.register()
    except ValueError:
        pass
    out['import'] = wowdata_blender.import_wow_gltf(bpy.context, src)
    arm = armature()
    heads = {}
    if arm is not None:
        for b in arm.data.bones:
            heads[b.name] = arm.matrix_world @ b.head_local

    if edit.get('report_only'):
        stats = {}
        for o in meshes():
            mw = o.matrix_world
            for v in o.data.vertices:
                name, w = strongest_group(o, v)
                if name is None:
                    continue
                p = mw @ v.co
                s = stats.setdefault(name, {'n': 0, 'sum': [0.0, 0.0, 0.0], 'submesh': o.get('wow_submesh')})
                s['n'] += 1
                for i in range(3):
                    s['sum'][i] += p[i]
        out['groups'] = {k: {'vertices': s['n'], 'mean': [round(c / s['n'], 3) for c in s['sum']], 'submesh': s['submesh'],
                             'head': [round(c, 3) for c in heads[k]] if k in heads else None,
                             'parent': (arm.data.bones[k].parent.name if arm and k in arm.data.bones and arm.data.bones[k].parent else None)}
                         for k, s in sorted(stats.items())}
        out['ok'] = True
    else:
        sa = edit.get('scale_all')
        if sa:
            n = 0
            for o in meshes():
                mw, inv = o.matrix_world, o.matrix_world.inverted()
                root = bpy.data.objects.get(o.parent.name) if o.parent else None
                origin = (root.matrix_world.translation if root else Vector((0, 0, 0)))
                for v in o.data.vertices:
                    p = mw @ v.co
                    v.co = inv @ (origin + (p - origin) * sa['factor'])
                    n += 1
                o.data.update()
            out['scaled_vertices'] = n
        sg = edit.get('scale_groups')
        if sg:
            n = 0
            want = set(sg['groups'])
            pivot_of = sg.get('pivot_of', {})      # scale a child part together with its parent about the PARENT's joint (one rigid piece)
            for o in meshes():
                mw, inv = o.matrix_world, o.matrix_world.inverted()
                for v in o.data.vertices:
                    name, w = strongest_group(o, v)
                    if name in want and pivot_of.get(name, name) in heads:
                        h = heads[pivot_of.get(name, name)]
                        v.co = inv @ (h + (mw @ v.co - h) * sg['factor'])
                        n += 1
                o.data.update()
            out['scaled_vertices'] = n
        tinted = 0
        for t in edit.get('tint', []):
            for img in bpy.data.images:
                if img.get('wow_texture_id') != t['texture_id']:
                    continue
                w, h = img.size
                px = list(img.pixels)
                x0, y0, x1, y1 = t['rect']
                for y in range(int(y0 * h), int(y1 * h)):
                    row = h - 1 - y
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
