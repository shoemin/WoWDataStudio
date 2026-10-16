"""Headless Blender test of the wowdata add-on (run by tests/test_blender.py, or by hand):

    blender-launcher.exe -b --factory-startup --python blender_addon\\tests\\blender_import_test.py -- REPORT.json A.gltf [B.gltf ...]

For every glTF: empty scene, register the add-on from this folder, import, then record what is in the scene
(objects, vertex counts, bones, actions, materials, custom properties) into REPORT.json.
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
report_path, files = argv[0], argv[1:]
wowdata_blender.register()
out = {'blender': bpy.app.version_string, 'models': []}
for fp in files:
    entry = {'gltf': fp}
    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        try:
            wowdata_blender.register()
        except ValueError:
            pass     # already registered
        rep = wowdata_blender.import_wow_gltf(bpy.context, fp)
        entry['import'] = rep
        objs = []
        for o in bpy.data.objects:
            d = {'name': o.name, 'type': o.type, 'hidden': o.hide_get() if o.name in bpy.context.view_layer.objects else None,
                 'parent': o.parent.name if o.parent else None}
            if o.type == 'MESH':
                d['vertices'] = len(o.data.vertices)
                d['polygons'] = len(o.data.polygons)
                d['materials'] = [m.name for m in o.data.materials if m]
                d['vertex_groups'] = len(o.vertex_groups)
                d['geoset'] = o.get('wow_geoset_id')
                d['armature_modifier'] = any(m.type == 'ARMATURE' for m in o.modifiers)
            if o.type == 'ARMATURE':
                d['bones'] = len(o.data.bones)
                d['bone_props'] = sum(1 for b in o.data.bones if 'wow_bone' in b)
                d['has_sidecar_prop'] = 'wow_sidecar' in o
                ad = o.animation_data
                d['active_action'] = ad.action.name if ad and ad.action else None
                d['nla_tracks'] = len(ad.nla_tracks) if ad else 0
            objs.append(d)
        entry['objects'] = objs
        entry['actions'] = [{'name': a.name, 'frames': [round(a.frame_range[0], 2), round(a.frame_range[1], 2)],
                             'wow_sequence': a.get('wow_sequence'), 'wow_anim_id': a.get('wow_anim_id')}
                            for a in bpy.data.actions]
        entry['materials'] = [{'name': m.name, 'wow_blend': m.get('wow_blend'), 'fixed': bool(m.get('wow_fixed')),
                               'render_method': getattr(m, 'surface_render_method', None),
                               'backface_culling': m.use_backface_culling} for m in bpy.data.materials]
        entry['images'] = [{'name': i.name, 'size': list(i.size)} for i in bpy.data.images]
        entry['ok'] = True
    except Exception:  # noqa: BLE001
        entry['ok'] = False
        entry['error'] = traceback.format_exc()
    out['models'].append(entry)
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1)
print('wowdata blender test: wrote', report_path)
