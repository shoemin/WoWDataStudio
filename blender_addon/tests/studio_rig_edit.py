"""Headless skeleton / animation edits through Blender for WoW Data Studio's tests (and by hand):

    blender -b --factory-startup --python studio_rig_edit.py -- REPORT.json IN.gltf OUT.gltf [EDIT.json]

Empty scene, register the add-on from this folder, import IN.gltf like a user would, apply the scripted edits, export
with "Export for WoW Data Studio" to OUT.gltf (which also writes wowdata_rig.json). EDIT.json (every part optional):
  {"add_bone": {"name": "bone_tail_tip", "parent_wow_bone": 15, "offset": [0, 0, 0.3], "length": 0.2,
                "weight_submesh": 0, "weight_axis": 0, "weight_below": -1.0, "key_bone": -1},
   "new_action": {"from": "Stand", "name": "Stand [4]", "bone_wow": 21, "degrees": 35, "new_bone_degrees": 40},
   "retime": {"action": "Walk", "factor": 1.25},
   "move_attachment": {"id": 1, "offset": [0.0, 0.0, 0.15]},
   "add_marker": {"action": "Stand [4]", "name": "$CSD", "ms": 400}}
"""
import json
import math
import os
import sys
import traceback

import bpy
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import wowdata_blender  # noqa: E402
from wowdata_blender import rig as wrig  # noqa: E402

argv = sys.argv[sys.argv.index('--') + 1:]
report_path, src, dst = argv[0], argv[1], argv[2]
edit = {}
if len(argv) > 3 and argv[3]:
    with open(argv[3], 'r', encoding='utf-8') as f:
        edit = json.load(f)
out = {'blender': bpy.app.version_string, 'ok': False, 'done': []}


def bone_by_wow(arm, idx):
    return next((b for b in arm.data.bones if b.get('wow_bone') == idx), None)


try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        wowdata_blender.register()
    except ValueError:
        pass
    out['import'] = wowdata_blender.import_wow_gltf(bpy.context, src)
    root, man = wrig.wow_rig_root(bpy.context)
    arm = wrig.armature_of(root, bpy.context)
    fps = wrig.fps_of(bpy.context.scene)
    new_bone = None
    if 'add_bone' in edit:
        e = edit['add_bone']
        parent = bone_by_wow(arm, e['parent_wow_bone'])
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode='EDIT')
        eb = arm.data.edit_bones.new(e['name'])
        ph = arm.data.edit_bones[parent.name].head.copy()
        eb.head = ph + Vector(e['offset'])
        eb.tail = eb.head + Vector((0.0, 0.0, e.get('length', 0.2)))
        eb.parent = arm.data.edit_bones[parent.name]
        bpy.ops.object.mode_set(mode='OBJECT')
        new_bone = arm.data.bones[e['name']]
        if e.get('key_bone', -1) >= 0:
            new_bone['wow_key_bone_id'] = int(e['key_bone'])
        weighted = 0
        for o in bpy.data.objects:
            if o.type == 'MESH' and o.get('wow_submesh') == e['weight_submesh']:
                vg = o.vertex_groups.new(name=e['name'])
                ax, below = e.get('weight_axis', 0), e.get('weight_below', -1e9)
                ids = [v.index for v in o.data.vertices if v.co[ax] < below]
                for g in list(o.vertex_groups):
                    if g.name != e['name']:
                        g.remove(ids)
                vg.add(ids, 1.0, 'REPLACE')
                weighted += len(ids)
        out['done'].append('add_bone %s (%d vertices)' % (e['name'], weighted))
        out['weighted_vertices'] = weighted
    if 'new_action' in edit:
        e = edit['new_action']
        src_act = next(a for a in bpy.data.actions if wrig.strip_suffix(a.name) == e['from'])
        new = src_act.copy()
        new.name = e['name']
        new.use_fake_user = True
        if arm.animation_data is None:
            arm.animation_data_create()
        arm.animation_data.action = new
        f0, f1 = new.frame_range
        mid = (f0 + f1) / 2.0
        b = bone_by_wow(arm, e['bone_wow'])
        pb = arm.pose.bones[b.name]
        pb.rotation_mode = 'QUATERNION'
        for fr, deg in ((f0, 0.0), (mid, e.get('degrees', 30.0)), (f1, 0.0)):
            bpy.context.scene.frame_set(int(round(fr)))
            pb.rotation_quaternion = Euler((0.0, 0.0, math.radians(deg))).to_quaternion()
            pb.keyframe_insert('rotation_quaternion', frame=fr)
        if new_bone is not None:
            npb = arm.pose.bones[new_bone.name]
            npb.rotation_mode = 'QUATERNION'
            for fr, deg in ((f0, 0.0), (mid, e.get('new_bone_degrees', 40.0)), (f1, 0.0)):
                npb.rotation_quaternion = Euler((math.radians(deg), 0.0, 0.0)).to_quaternion()
                npb.keyframe_insert('rotation_quaternion', frame=fr)
        out['done'].append('new_action %s from %s' % (new.name, src_act.name))
    if 'retime' in edit:
        e = edit['retime']
        act = next(a for a in bpy.data.actions if wrig.strip_suffix(a.name) == e['action'])
        f = e['factor']
        for fc in act.fcurves:
            for k in fc.keyframe_points:
                k.co.x *= f
                k.handle_left.x *= f
                k.handle_right.x *= f
            fc.update()
        for pm in act.pose_markers:
            pm.frame = int(round(pm.frame * f))
        out['done'].append('retime %s x%s -> frames %s' % (act.name, f, tuple(act.frame_range)))
    if 'move_attachment' in edit:
        e = edit['move_attachment']
        o = next(x for x in bpy.data.objects if x.type == 'EMPTY' and x.get('wow_attachment_id') == e['id'])
        o.location = o.location + Vector(e['offset'])
        out['done'].append('move_attachment %s' % o.name)
    if 'add_marker' in edit:
        e = edit['add_marker']
        act = next(a for a in bpy.data.actions if wrig.strip_suffix(a.name) == e['action'])
        pm = act.pose_markers.new(e['name'])
        pm.frame = int(round(e['ms'] / 1000.0 * fps))
        out['done'].append('add_marker %s at frame %d' % (e['name'], pm.frame))
    out['export'] = wowdata_blender.export_for_studio(bpy.context, dst)
    out['ok'] = True
except Exception:  # noqa: BLE001
    out['error'] = traceback.format_exc()
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1, default=str)
